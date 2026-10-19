// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaMotion.Core;

public enum ExportQuality
{
    /// <summary>The best model installed, whatever it costs: export doesn't have to keep up with playback.</summary>
    Best,
    /// <summary>The model live playback would use: about twice as fast to render.</summary>
    Faster,
}

public sealed record ExportOptions
{
    /// <summary>"60", "120", "x2"…</summary>
    public string Target { get; init; } = "60";
    public ExportQuality Quality { get; init; } = ExportQuality.Best;
    /// <summary>Folder for the new file; null puts it next to the original.</summary>
    public string? OutputFolder { get; init; }
    /// <summary>Use NVIDIA's hardware encoder (fast); otherwise x264 on the CPU.</summary>
    public bool HardwareEncoder { get; init; } = true;
}

public sealed record SourceInfo(int? Frames, double? Fps);

/// <summary>A frame rate as a fraction, with the engine's rules for which rate a video gets.</summary>
public sealed record FrameRate(long Num, long Den)
{
    public double Value => (double)Num / Den;

    private static readonly FrameRate[] Standard =
        { new(24000, 1001), new(24, 1), new(25, 1), new(30000, 1001), new(30, 1), new(48, 1), new(50, 1), new(60000, 1001), new(60, 1) };

    /// <summary>as_fraction in novamotion.vpy: 23.976 -> 24000/1001, 25.0 -> 25.</summary>
    public static FrameRate Of(double fps)
    {
        var whole = Math.Round(fps);
        if (Math.Abs(fps - whole) < 0.005) return new((long)whole, 1);
        var ntsc = Math.Round(fps * 1.001);
        if (Math.Abs(fps - ntsc * 1000 / 1001) < 0.005) return new((long)ntsc * 1000, 1001);
        return new((long)Math.Round(fps * 1001), 1001);
    }

    /// <summary>snap_rate in novamotion.vpy: a rate within 2 % of a standard one is that rate.</summary>
    public static FrameRate Snap(double fps)
    {
        var f = Of(fps);
        var nearest = Standard.MinBy(r => Math.Abs(r.Value - f.Value))!;
        return Math.Abs(nearest.Value - f.Value) / nearest.Value < 0.02 ? nearest : f;
    }

    /// <summary>The engine's output rate for an export (no display involved), as in build() and target_fps.</summary>
    public static FrameRate Output(EngineSettings e, double containerFps)
    {
        var src = Snap(containerFps);
        if (!e.Enabled || src.Value > e.MaxSourceFps) return src;
        var t = e.TargetText;
        FrameRate want = t.StartsWith('x') ? new(src.Num * (int.TryParse(t[1..], out var k) && k > 0 ? k : 2), src.Den)
                       : double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Of(v)
                       : new(60, 1);
        return want.Value < src.Value * 1.1 ? src : want;
    }
}

/// <summary>How an export is set up: engine settings, output file, and the mpv command line.</summary>
public static class ExportPlan
{
    public static readonly string[] Targets = { "60", "120", "x2" };
    /// <summary>Source frames repeated after the last one; covers the engine's look-ahead and a miscounted length.</summary>
    public const int TailPad = 8;

    /// <summary>"Match (NOVA Motion 60 fps).mp4" next to the original, never overwriting a file.</summary>
    public static string OutputPath(string input, ExportOptions o, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var dir = o.OutputFolder ?? Path.GetDirectoryName(input) ?? ".";
        var name = Path.GetFileNameWithoutExtension(input);
        var label = o.Target.StartsWith('x') ? $"{o.Target}" : $"{o.Target} fps";
        var path = Path.Combine(dir, $"{name} (NOVA Motion {label}).mp4");
        for (var i = 2; exists(path); i++) path = Path.Combine(dir, $"{name} (NOVA Motion {label}) ({i}).mp4");
        return path;
    }

    /// <summary>The engine settings for an export: the best engine and model this PC has.</summary>
    public static EngineSettings Engine(EngineSettings playing, ExportOptions o, EngineAvailability a, IReadOnlyList<string> vulkanModels)
    {
        var e = playing.Clone();
        e.Enabled = true;
        e.Demo = false;
        e.RtxVsr = false;
        e.QualityStep = 0;
        e.SceneDetect = true;
        e.Dedup = true;
        e.TargetText = o.Target;
        e.MvQuality = "quality";
        e.Engine = a.TensorRt ? "rife_trt" : vulkanModels.Count > 0 ? "rife" : "mvtools";
        if (o.Quality == ExportQuality.Best)
        {
            // The newest full model; lite models trade detail for speed, which an export doesn't need
            e.TrtModel = "rife-v4.26";
            e.RifeModel = vulkanModels
                .Where(m => !m.Contains("lite", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(ModelVersion).FirstOrDefault() ?? vulkanModels.FirstOrDefault() ?? e.RifeModel;
        }
        else e.TrtModel = "auto";
        return e;
    }

    private static double ModelVersion(string model)
    {
        var m = System.Text.RegularExpressions.Regex.Match(model, @"v4[._](\d+)");
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>mpv in encoding mode: decode, run the engine, encode. No window, no audio device.</summary>
    public static IReadOnlyList<string> MpvArguments(RuntimeLayout rt, string input, string output, string pipePath,
                                                     string logPath, ExportOptions o, FrameRate? rate = null)
    {
        var vpy = rt.EngineScript;
        // mpv times the last few engine frames by the input frames they came after; numbering every
        // frame at the engine's output rate gives the file exact, even timestamps to the end
        var stamp = rate is { } r ? $",lavfi=[setpts=N*{r.Den}/({r.Num}*TB)]" : "";
        var args = new List<string>
        {
            "--no-config",                     // no player scripts or settings: the export is set up here
            // Started without a console, mpv.exe picks "pseudo-gui" mode, which idles after the last file
            // instead of exiting; an export must end when the file does
            "--player-operation-mode=cplayer", "--idle=no", "--force-window=no", "--keep-open=no",
            $"--input-ipc-server={pipePath}",
            $"--log-file={logPath}",
            "--msg-level=all=v",
            "--hwdec=auto-copy",
            "--sid=no", "--osd-level=0",
            // tpad repeats the last frame a few times: mpv's filter stops at the first request past the end
            // of the input and drops the frames in flight, and the engine looks a few frames ahead. With the
            // engine trimmed to the real length, the repeats are only ever looked at, never output.
            $"--vf=lavfi=[tpad=stop_mode=clone:stop={TailPad}],@novamotion:vapoursynth=file=%{vpy.Length}%{vpy}:buffered-frames=24:concurrent-frames=auto{stamp}",
            $"--o={output}",
            "--of=mp4",
            "--oac=aac", "--oacopts=b=192k",
        };
        if (o.HardwareEncoder)
        {
            // NVENC HEVC at constant quality: visually lossless at this level, a fraction of the CPU
            args.Add("--ovc=hevc_nvenc");
            args.Add("--ovcopts=preset=p6,rc=vbr,cq=19,b=0,tag=hvc1");
        }
        else
        {
            args.Add("--ovc=libx264");
            args.Add("--ovcopts=preset=slow,crf=17");
        }
        args.Add("--");
        args.Add(input);
        return args;
    }

    /// <summary>"12:04 left" style text from progress so far.</summary>
    public static string Remaining(double fraction, TimeSpan elapsed)
    {
        if (fraction <= 0.01 || elapsed.TotalSeconds < 3) return "Estimating time…";
        var left = TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - fraction) / fraction);
        return left.TotalHours >= 1 ? $"About {(int)left.TotalHours} h {left.Minutes} min left"
             : left.TotalMinutes >= 1 ? $"About {(int)Math.Ceiling(left.TotalMinutes)} min left"
             : "Less than a minute left";
    }
}

/// <summary>What the app needs from a running export. ExportRunner runs mpv; tests use a fake.</summary>
public interface IExportRunner
{
    /// <summary>Runs the export; reports progress 0..1; returns null on success or the reason it failed.</summary>
    Task<string?> RunAsync(string input, string output, EngineSettings engine, ExportOptions options,
                           IProgress<double> progress, CancellationToken ct);
}

/// <summary>Runs one export through mpv's encoding mode with the NOVA Motion engine as a filter.</summary>
public sealed class ExportRunner : IExportRunner
{
    private readonly RuntimeLayout _runtime;
    private readonly SettingsStore _store;

    public ExportRunner(RuntimeLayout runtime, SettingsStore store) { _runtime = runtime; _store = store; }

    public async Task<string?> RunAsync(string input, string output, EngineSettings engine, ExportOptions options,
                                        IProgress<double> progress, CancellationToken ct)
    {
        var dir = Path.Combine(_store.Directory, "exports");
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid().ToString("N")[..8];
        var cfg = Path.Combine(dir, $"engine-{id}.json");
        var log = Path.Combine(dir, $"export-{id}.log");
        var source = await ProbeAsync(_runtime.MpvExe, input, ct);
        var probed = engine.Clone();
        probed.SourceFrames = source.Frames;
        File.WriteAllText(cfg, JsonSerializer.Serialize(probed, Json.Options));
        var rate = source.Fps is { } fps ? FrameRate.Output(probed, fps) : null;

        var mpv = new MpvPlayer(_runtime, _store);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mpv.Exited += () => exited.TrySetResult();
        try
        {
            await mpv.StartProcessAsync(ExportPlan.MpvArguments(_runtime, input, output, mpv.PipePath, log, options, rate),
                new Dictionary<string, string> { ["NOVAMOTION_ENGINE_CONFIG"] = cfg }, ct);
            double duration = 0;
            while (!exited.Task.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (duration <= 0 && await mpv.CommandAsync("get_property", "duration") is JsonValue d && d.TryGetValue(out double dv)) duration = dv;
                    if (duration > 0 && await mpv.CommandAsync("get_property", "time-pos") is JsonValue t && t.TryGetValue(out double tv))
                        progress.Report(Math.Clamp(tv / duration, 0, 1));
                }
                catch (Exception ex) when (ex is MpvException or TimeoutException or InvalidOperationException or IOException) { }
                await Task.WhenAny(exited.Task, Task.Delay(500, ct));
            }
        }
        catch (OperationCanceledException)
        {
            mpv.Kill();
            TryDelete(output);
            return "Cancelled";
        }
        finally
        {
            TryDelete(cfg);
        }

        if (mpv.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 1024)
        {
            progress.Report(1);
            if (Environment.GetEnvironmentVariable("NOVAMOTION_UITEST") is null) TryDelete(log);
            return null;
        }
        TryDelete(output);
        return Reason(log);
    }

    /// <summary>The source's frame count and frame rate from its container. Takes about a second.</summary>
    public static async Task<SourceInfo> ProbeAsync(string mpvExe, string input, CancellationToken ct)
    {
        if (!File.Exists(mpvExe)) return new SourceInfo(null, null);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(mpvExe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { "--no-config", "--player-operation-mode=cplayer", "--idle=no", "--vo=null", "--ao=null",
                                      "--frames=1", "--term-playing-msg=NMPROBE ${=estimated-frame-count} ${=container-fps}", "--", input })
                psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            var output = await p.StandardOutput.ReadToEndAsync(timeout.Token) + await stderr;
            await p.WaitForExitAsync(timeout.Token);
            return ParseProbe(output);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            ct.ThrowIfCancellationRequested();
            return new SourceInfo(null, null);
        }
    }

    public static SourceInfo ParseProbe(string output)
    {
        var m = System.Text.RegularExpressions.Regex.Match(output, @"NMPROBE (\S*) (\S*)");
        if (!m.Success) return new SourceInfo(null, null);
        int? frames = int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
        double? fps = double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0 ? f : null;
        return new SourceInfo(frames, fps);
    }

    /// <summary>The most useful line of mpv's log for a failed export.</summary>
    internal static string Reason(string log)
    {
        try
        {
            var lines = File.Exists(log) ? File.ReadAllLines(log) : Array.Empty<string>();
            var err = lines.LastOrDefault(l => l.Contains("[e]", StringComparison.Ordinal) || l.Contains("[f]", StringComparison.Ordinal));
            if (err?.Contains("encoder", StringComparison.OrdinalIgnoreCase) == true && err.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
                return "NVIDIA's video encoder isn't available. Turn off \"Use the GPU encoder\" and export again.";
            return err is null ? "The export stopped. Details are in the exports folder." : err[(err.IndexOf(']', err.IndexOf(']') + 1) + 1)..].Trim();
        }
        catch (IOException) { return "The export stopped."; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
