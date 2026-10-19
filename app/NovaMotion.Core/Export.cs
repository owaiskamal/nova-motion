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

/// <summary>How an export is set up: engine settings, output file, and the mpv command line.</summary>
public static class ExportPlan
{
    public static readonly string[] Targets = { "60", "120", "x2" };

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
                                                     string logPath, ExportOptions o)
    {
        var vpy = rt.EngineScript;
        var args = new List<string>
        {
            "--no-config",                     // no player scripts or settings: the export is set up here
            $"--input-ipc-server={pipePath}",
            $"--log-file={logPath}",
            "--hwdec=auto-copy",
            "--sid=no", "--osd-level=0",
            $"--vf=@novamotion:vapoursynth=file=%{vpy.Length}%{vpy}:buffered-frames=24:concurrent-frames=auto",
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
        File.WriteAllText(cfg, JsonSerializer.Serialize(engine, Json.Options));

        var mpv = new MpvPlayer(_runtime, _store);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mpv.Exited += () => exited.TrySetResult();
        try
        {
            await mpv.StartProcessAsync(ExportPlan.MpvArguments(_runtime, input, output, mpv.PipePath, log, options),
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
            TryDelete(log);
            return null;
        }
        TryDelete(output);
        return Reason(log);
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
