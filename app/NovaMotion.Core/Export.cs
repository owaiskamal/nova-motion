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
    /// <summary>A before/after video: the original and NOVA Motion side by side, labelled.</summary>
    public bool Comparison { get; init; }
    /// <summary>How a before/after shows the picture: "split" (left original, right NOVA), "whole" (both
    /// whole frames side by side) or "zoom" (the middle at 100%, to see detail).</summary>
    public string Compare { get; init; } = "split";
    /// <summary>Anime: drawings kept as drawn, only camera moves smoothed.</summary>
    public bool Drawn { get; init; }
    /// <summary>Only part of the video: from this second…</summary>
    public double? StartSeconds { get; init; }
    /// <summary>…for this many seconds (null: to the end).</summary>
    public double? LengthSeconds { get; init; }
    /// <summary>AI clean-up and upscaling; null or "off": smoothing only.</summary>
    public EnhanceSettings? Enhance { get; init; }
    /// <summary>A short before/after made to look at in the app, not to keep.</summary>
    public bool Preview { get; init; }
    /// <summary>One before/after frame as a PNG, at StartSeconds: Enhance's look in seconds, no smoothing.</summary>
    public bool Still { get; init; }

    public bool Enhancing => Enhance is { IsOn: true };

    /// <summary>"3:15" or "195" (seconds) to seconds; null when empty or unreadable.</summary>
    public static double? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        double total = 0;
        foreach (var part in text.Trim().Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return null;
            total = total * 60 + v;
        }
        return total;
    }
}

public sealed record SourceInfo(int? Frames, double? Fps, int? Width = null, int? Height = null, double? Seconds = null);

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

    /// <summary>snap_rate in novamotion.vpy: a rate within 2.5 % of a standard one is that rate.</summary>
    public static FrameRate Snap(double fps)
    {
        var f = Of(fps);
        var nearest = Standard.MinBy(r => Math.Abs(r.Value - f.Value))!;
        return Math.Abs(nearest.Value - f.Value) / nearest.Value < 0.025 ? nearest : f;
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

    /// <summary>How many frames the engine makes from <paramref name="sourceFrames"/> at <paramref name="src"/>.</summary>
    public static long Frames(long sourceFrames, FrameRate src, FrameRate dst) =>
        sourceFrames * dst.Num * src.Den / (dst.Den * src.Num);
}

/// <summary>How an export is set up: engine settings, output file, and the mpv command line.</summary>
public static class ExportPlan
{
    public static readonly string[] Targets = { "60", "120", "x2" };
    /// <summary>Source frames repeated after the last one; covers the engine's look-ahead and a miscounted length.</summary>
    public const int TailPad = 8;
    /// <summary>The widest before/after video: what NVIDIA's H.264 encoder accepts.</summary>
    public const int MaxCompareWidth = 4096;
    /// <summary>An export is written under this name and renamed when it's complete, so a file with the
    /// final name is always a whole video (a crash or power cut left a broken one before).</summary>
    public static string PartPath(string output) => output + ".part";

    /// <summary>"Match (NOVA Motion 60 fps).mp4" next to the original, never overwriting a file.</summary>
    public static string OutputPath(string input, ExportOptions o, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var dir = o.OutputFolder ?? Path.GetDirectoryName(input) ?? ".";
        var name = Path.GetFileNameWithoutExtension(input);
        if (o.Still)
        {
            var still = Path.Combine(dir, $"{name} (NOVA Enhance frame {(o.StartSeconds ?? 0).ToString("0.#", CultureInfo.InvariantCulture)} s).png");
            for (var i = 2; exists(still); i++) still = Path.Combine(dir, $"{name} (NOVA Enhance frame) ({i}).png");
            return still;
        }
        var label = (o.Comparison ? "before-after " : "") + (o.Enhancing ? EnhanceCatalog.SizeName(o.Enhance!.Height) + " " : "")
                  + (o.Target.StartsWith('x') ? $"{o.Target}" : $"{o.Target} fps");
        var product = o.Enhancing ? "NOVA Enhance" : "NOVA Motion";
        var path = Path.Combine(dir, $"{name} ({product} {label}).mp4");
        for (var i = 2; exists(path); i++) path = Path.Combine(dir, $"{name} ({product} {label}) ({i}).mp4");
        return path;
    }

    /// <summary>The engine settings for an export: the best engine and model this PC has.</summary>
    public static EngineSettings Engine(EngineSettings playing, ExportOptions o, EngineAvailability a, IReadOnlyList<string> vulkanModels)
    {
        var e = playing.Clone();
        e.Enabled = !o.Still;               // a frame shows the picture: nothing to interpolate
        e.Demo = o.Comparison || o.Still;
        // The engine's names: for Motion-only clips "side" is the middle of the picture twice
        e.DemoMode = o.Compare switch { "whole" => "whole", "zoom" => o.Enhancing ? "zoom" : "side", _ => "split" };
        e.DemoLabels = o.Comparison || o.Still;
        e.RtxVsr = false;
        e.QualityStep = 0;
        e.SizeStep = 0;
        e.SceneDetect = true;
        e.Dedup = true;
        e.Drawn = o.Drawn;
        e.TargetText = o.Target;
        e.Enhance = o.Enhancing ? o.Enhance! with { } : null;
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

    /// <summary>
    /// From a start time, mpv decodes from the keyframe before it and the engine would see those earlier
    /// frames too: its first frame wouldn't be the start, and an engine trimmed to the part's frame count
    /// would run out before mpv reached the start ("Invalid frame number ... requested"). Dropping them
    /// before the engine makes its frame 0 the start frame. A few ms early, so the start frame itself is
    /// kept whatever the file's timestamp rounding; the frame before it is a whole frame earlier.
    /// </summary>
    public static string SeekTrim(ExportOptions o) =>
        o.StartSeconds is > 0 and var at
            ? $"trim=start={Math.Max(0, at - 0.004).ToString("0.######", CultureInfo.InvariantCulture)},"
            : "";

    /// <summary>A start time on a frame, so the engine's first frame and mpv's agree.</summary>
    public static double OnFrame(double seconds, double fps) =>
        fps > 0 ? Math.Round(Math.Round(seconds * fps) / fps, 6) : seconds;

    /// <summary>mpv in encoding mode: decode, run the engine, encode. No window, no audio device.</summary>
    public static IReadOnlyList<string> MpvArguments(RuntimeLayout rt, string input, string output, string pipePath,
                                                     string logPath, ExportOptions o, FrameRate? rate = null, long? frames = null)
    {
        var vpy = rt.EngineScript;
        // mpv times the last few engine frames by the input frames they came after; numbering every
        // frame at the engine's output rate gives the file exact, even timestamps to the end
        var stamp = rate is { } r && !o.Still ? $",lavfi=[setpts=N*{r.Den}/({r.Num}*TB)]" : "";
        // Before/after videos are for sharing: 720p, so a short clip stays small enough to upload.
        // Enhance's before/after is already a full-size crop: shrinking it would hide the detail it shows.
        if (o.Comparison && !o.Enhancing) stamp += ",lavfi=[scale=-2:'min(720,ih)']";
        // NVIDIA's H.264 encoder stops at 4096 px wide: an 8K split before/after (7680) failed to start
        else if (o.Comparison && !o.Still) stamp += $",lavfi=[scale='min({MaxCompareWidth},iw)':-2]";
        // Before/after clips are plain 8-bit 4:2:0 H.264, which every browser and phone plays; a 10-bit
        // iPhone clip would otherwise come out as 10-bit H.264, which most don't
        if (o.Comparison && !o.Still) stamp += ",format=fmt=yuv420p";
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
            $"--vf=lavfi=[{SeekTrim(o)}tpad=stop_mode=clone:stop={TailPad}],@novamotion:vapoursynth=file=%{vpy.Length}%{vpy}:buffered-frames=24:concurrent-frames=auto{stamp}",
            $"--o={output}",
        };
        if (o.Still)
        {
            // One PNG: the image muxer rewrites the same file for the copy of the last frame mpv asks for
            args.AddRange(new[] { "--of=image2", "--ofopts=update=1", "--ovc=png", "--no-audio" });
            if (o.StartSeconds is > 0 and var at) args.Add($"--start={at.ToString(CultureInfo.InvariantCulture)}");
            args.Add($"--frames={(frames is > 0 ? frames + 1 : 2)}");
            args.Add("--");
            args.Add(input);
            return args;
        }
        args.AddRange(new[] { "--of=mp4", "--oac=aac", "--oacopts=b=192k" });
        // The engine pads its end with spare frames; stop at the real ones. mpv holds the last
        // frame it counts back from the encoder, so one more (a copy of the last frame) is asked for.
        if (frames is > 0) args.Add($"--frames={frames + 1}");
        if (o.StartSeconds is > 0 and var start) args.Add($"--start={start.ToString(CultureInfo.InvariantCulture)}");
        if (o.LengthSeconds is > 0 and var length) args.Add($"--length={length.ToString(CultureInfo.InvariantCulture)}");
        if (o.Comparison)
        {
            // H.264 plays in every browser (GitHub, social sites); HEVC doesn't in most
            args.Add(o.HardwareEncoder ? "--ovc=h264_nvenc" : "--ovc=libx264");
            args.Add(o.HardwareEncoder ? "--ovcopts=preset=p6,rc=vbr,cq=24,b=0" : "--ovcopts=preset=slow,crf=22,profile=high");
        }
        else if (o.HardwareEncoder)
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

    /// <summary>What an export that hasn't made a frame yet is waiting for, after a while: TensorRT
    /// preparing a model for this graphics card (once per model; minutes on GTX-class cards).</summary>
    public static string? Waiting(double fraction, TimeSpan elapsed, ExportOptions o) =>
        fraction > 0.001 || elapsed < TimeSpan.FromSeconds(40) ? null
        : o.Enhancing ? "Preparing the AI models for this graphics card. This happens once per model and can take several minutes."
        : "Preparing the AI engine for this video size (once).";

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
        if (Path.GetDirectoryName(output) is { Length: > 0 } outDir) Directory.CreateDirectory(outDir);
        var id = Guid.NewGuid().ToString("N")[..8];
        var cfg = Path.Combine(dir, $"engine-{id}.json");
        var log = Path.Combine(dir, $"export-{id}.log");
        var source = await ProbeAsync(_runtime.MpvExe, input, ct);
        var probed = engine.Clone();
        probed.SourceFrames = source.Frames;
        // A part of the video: its own frame count, from the length (or what's left after the start)
        if (source.Fps is { } sf && (options.LengthSeconds is > 0 || options.StartSeconds is > 0))
        {
            var whole = source.Frames ?? int.MaxValue;
            var from = (int)Math.Round((options.StartSeconds ?? 0) * sf);
            var count = options.LengthSeconds is { } len ? (int)Math.Round(len * sf) : whole - from;
            probed.SourceFrames = Math.Max(1, Math.Min(count, whole - from));
        }
        if (options.Still)
        {
            probed.SourceFrames = 1;
            // No time given: a third of the way in, past titles and fades
            if (options.StartSeconds is null && source.Frames is > 0 and var count && source.Fps is > 0 and var f)
                options = options with { StartSeconds = Math.Round(count / f / 3, 2) };
        }
        if (options.StartSeconds is > 0 and var st && source.Fps is > 0 and var sfps)
            options = options with { StartSeconds = ExportPlan.OnFrame(st, sfps) };
        File.WriteAllText(cfg, JsonSerializer.Serialize(probed, Json.Options));
        var rate = source.Fps is { } fps ? FrameRate.Output(probed, fps) : null;
        long? frames = options.Still ? 1
                     : rate is not null && source.Frames is { } n ? FrameRate.Frames(n, FrameRate.Snap(source.Fps!.Value), rate) : null;

        var final = output;
        output = ExportPlan.PartPath(final);
        TryDelete(output);
        await using var mpv = new MpvPlayer(_runtime, _store);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mpv.Exited += () => exited.TrySetResult();
        try
        {
            await mpv.StartProcessAsync(ExportPlan.MpvArguments(_runtime, input, output, mpv.PipePath, log, options, rate, frames),
                new Dictionary<string, string> { ["NOVAMOTION_ENGINE_CONFIG"] = cfg }, ct);
            double duration = 0;
            while (!exited.Task.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (duration <= 0 && await mpv.CommandAsync("get_property", "duration") is JsonValue d && d.TryGetValue(out double dv)) duration = dv;
                    var from = options.StartSeconds ?? 0;
                    var span = options.LengthSeconds ?? (duration - from);
                    if (duration > 0 && span > 0 && await mpv.CommandAsync("get_property", "time-pos") is JsonValue t && t.TryGetValue(out double tv))
                        progress.Report(Math.Clamp((tv - from) / span, 0, 1));
                    else progress.Report(0);     // still starting: the app can say what it's waiting for
                }
                catch (Exception ex) when (ex is MpvException or TimeoutException or InvalidOperationException or IOException
                                               || (ex is OperationCanceledException && !ct.IsCancellationRequested)) { }
                await Task.WhenAny(exited.Task, Task.Delay(500, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await StopAsync(mpv, exited.Task);
            TryDelete(output);
            return "Cancelled";
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception
                                       or OperationCanceledException)
        {
            // mpv didn't start or couldn't be reached: never left running on its own, writing a file nobody asked for
            await StopAsync(mpv, exited.Task);
            TryDelete(output);
            return ex is System.ComponentModel.Win32Exception
                ? "The player (mpv.exe) couldn't be started. If antivirus removed it, run build.cmd or reinstall."
                : "The export couldn't start: " + ex.Message;
        }
        finally
        {
            TryDelete(cfg);
        }

        if ((mpv.ExitCode == 0 || options.Still) && File.Exists(output) && new FileInfo(output).Length > 1024)
        {
            // mpv drops a filter that fails and goes on: the file would be the plain video, labelled enhanced
            if (EngineFailure(log) is { } why)
            {
                TryDelete(output);
                return why;
            }
            try { File.Move(output, final, overwrite: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(output);
                return $"The video was made but couldn't be saved as {Path.GetFileName(final)}: {ex.Message}";
            }
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
                                      "--frames=1", "--term-playing-msg=NMPROBE ${=estimated-frame-count} ${=container-fps} ${=width} ${=height} ${=duration}", "--", input })
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
        var m = System.Text.RegularExpressions.Regex.Match(output, @"NMPROBE (\S*) (\S*)(?: (\S*) (\S*) (\S*))?");
        if (!m.Success) return new SourceInfo(null, null);
        int? frames = int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
        double? fps = double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0 ? f : null;
        int? w = int.TryParse(m.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ww) && ww > 0 ? ww : null;
        int? h = int.TryParse(m.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hh) && hh > 0 ? hh : null;
        double? d = double.TryParse(m.Groups[5].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd) && dd > 0 ? dd : null;
        // Containers without a frame count: the length and rate give it
        frames ??= d is { } len && fps is { } r ? (int)Math.Round(len * r) : null;
        return new SourceInfo(frames, fps, w, h, d);
    }

    /// <summary>The most useful line of mpv's log for a failed export.</summary>
    /// <summary>Why the engine script failed, if it did: mpv keeps going without the filter, so the export
    /// would otherwise "succeed" with neither smoothing nor enhancement.</summary>
    public static string? EngineFailure(string log)
    {
        try
        {
            if (!File.Exists(log)) return null;
            var lines = File.ReadAllLines(log);
            var failed = lines.Any(l => l.Contains("Disabling filter", StringComparison.Ordinal)
                                        || (l.Contains("[vapoursynth]", StringComparison.Ordinal) && (l.Contains("[e]", StringComparison.Ordinal) || l.Contains("[f]", StringComparison.Ordinal))));
            if (!failed) return null;
            // The Python error's last line: "vapoursynth.Error: Enhance model missing: …" or "SomeError: …"
            var why = lines.Where(l => l.Contains("[vapoursynth]", StringComparison.Ordinal))
                           .Select(l => l[(l.IndexOf("[vapoursynth]", StringComparison.Ordinal) + "[vapoursynth]".Length)..].Trim())
                           .LastOrDefault(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^[\w.]*(Error|Exception)\b.*:"));
            if (why is not null) why = why[(why.IndexOf(':') + 1)..].Trim();
            if (why?.Contains("trtexec", StringComparison.OrdinalIgnoreCase) == true)
                return "The AI engine didn't run, so nothing was enhanced: TensorRT couldn't prepare a model for this graphics card"
                       + " (often too little graphics memory). Turn off Fix compression, or choose Natural or a smaller size, and try again.";
            return "The AI engine didn't run, so nothing was enhanced" + (string.IsNullOrEmpty(why) ? "." : ": " + why)
                   + " Details are in the exports folder; collect-logs.cmd gathers them.";
        }
        catch (IOException) { return null; }
    }

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

    /// <summary>Ends mpv and waits (briefly) until it has let go of the file it was writing.</summary>
    private static async Task StopAsync(MpvPlayer mpv, Task exited)
    {
        mpv.Kill();
        if (mpv.IsRunning) await Task.WhenAny(exited, Task.Delay(3000));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
