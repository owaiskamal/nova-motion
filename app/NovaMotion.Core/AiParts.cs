// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Diagnostics;
using System.Globalization;

namespace NovaMotion.Core;

/// <summary>Where an installable part stands on this PC.</summary>
public enum PartState { NotInstalled, NotForThisPc, Waiting, Installing, NeedsPreparing, Preparing, Ready, UpdateAvailable, Failed }

/// <summary>What the AI Models page knows about this PC's graphics.</summary>
public sealed record PcGraphics(bool Nvidia, double? MemoryGb, string Name = "");

/// <summary>
/// One part NOVA Studio can install from its AI Models page: what it unlocks, its size, which cards it
/// runs on, how to tell it's installed, and the setup script that installs it (run hidden, its
/// progress read from the @@ lines setup\Progress.ps1 prints).
/// </summary>
public sealed record AiPart(
    string Id, string Name, string Feature, string Description, string Size,
    string Script, IReadOnlyList<string> Arguments,
    Func<RuntimeLayout?, bool> IsInstalled,
    Func<RuntimeLayout?, bool>? NeedsUpdate = null,
    Func<PcGraphics, string?>? WhyNot = null,
    string? Requires = null,
    string UpdateNote = "",
    Func<PcGraphics, string>? SizeFor = null,
    Func<PcGraphics, double>? NeedGbFor = null,
    IReadOnlyList<string>? PartialDirs = null)
{
    /// <summary>The download size for this PC's card (Generative's model depends on its memory).</summary>
    public string SizeOn(PcGraphics g) => SizeFor?.Invoke(g) ?? Size;

    /// <summary>Free disk space it needs while installing, in GB (downloads and unpacked files at once).</summary>
    public double NeedGb(PcGraphics g) => NeedGbFor?.Invoke(g) ?? 1;

    /// <summary>Has a one-time "prepare for this graphics card" step (TensorRT engines).</summary>
    public bool Preparable => Id == AiParts.Enhance;
}

public static class AiParts
{
    public const string Player = "player", TensorRt = "tensorrt", Enhance = "enhance", Animate = "animate", Generative = "generative";

    public static readonly IReadOnlyList<AiPart> All = new[]
    {
        new AiPart(Player, "Player engine", "Motion",
            "mpv, VapourSynth, RIFE on Vulkan and MVTools: watching and smoothing on any graphics card.",
            "about 150 MB", "Get-Runtime.ps1", Array.Empty<string>(),
            rt => rt is not null && File.Exists(Path.Combine(rt.MpvDir, "vapoursynth.dll")),
            NeedGbFor: _ => 0.6, PartialDirs: new[] { ".cache" }),
        new AiPart(TensorRt, "TensorRT for NVIDIA", "Motion · Enhance",
            "NVIDIA's engine: RIFE smoothing and Enhance run several times faster on GTX 16 and RTX cards.",
            "about 3 GB", "Get-TensorRT.ps1", new[] { "-NoTune" },
            rt => EngineAvailability.Of(rt).TensorRt,
            WhyNot: g => g.Nvidia ? null : "Needs an NVIDIA graphics card (GTX 16 or RTX). The other engines work without it.",
            Requires: Player,
            NeedGbFor: _ => 8, PartialDirs: new[] { Path.Combine(".cache", "trt") }),
        new AiPart(Enhance, "Enhance models", "Enhance",
            "Clean-up and upscaling for videos and photos, Remove noise and blur, and Max detail for photos.",
            "about 500 MB", "Get-EnhanceModels.ps1", new[] { "-NoPrepare" },
            rt => Photo.IsReady(rt),
            NeedsUpdate: rt => !Photo.HasRestore(rt) || !Photo.HasMaxDetail(rt),
            Requires: Player,
            UpdateNote: "Adds Remove noise and blur and Max detail for photos",
            NeedGbFor: _ => 1.2, PartialDirs: new[] { Path.Combine("mpv", "vs-plugins", "models"), Path.Combine(".cache", "enhance") }),
        new AiPart(Animate, "Animate a picture", "Generate",
            "A depth model that turns a still picture into a short 3D camera move. Runs on any graphics card.",
            "about 150 MB", "Get-StudioParts.ps1", Array.Empty<string>(),
            rt => Core.Animate.IsReady(rt),
            Requires: Player,
            NeedGbFor: _ => 0.6, PartialDirs: new[] { "studio", ".cache" }),
        new AiPart(Generative, "Generative detail", "Enhance · Photos",
            "SeedVR2 makes new detail that fits the picture: eyelashes, hair, fur, skin texture. Its own Python with PyTorch for CUDA.",
            "about 5 GB (7 GB on cards with 8 GB or more)", "Get-Generate.ps1", Array.Empty<string>(),
            rt => Photo.HasGenerative(rt),
            WhyNot: g => !g.Nvidia ? "Needs an NVIDIA graphics card with 4 GB or more."
                       : g.MemoryGb is { } m && m < 3.5 ? $"Needs an NVIDIA card with 4 GB or more; this one has {m:0.#} GB." : null,
            Requires: Enhance,
            // Cards under 8 GB get the 4-bit model (2 GB) instead of the 8-bit one (3.4 GB); PyTorch for CUDA is about 3 GB of it
            SizeFor: g => g.MemoryGb is { } m && m < 7.5 ? "about 5.5 GB (the 4-bit model, for this card)" : "about 7 GB",
            NeedGbFor: g => g.MemoryGb is { } m && m < 7.5 ? 10 : 12,
            PartialDirs: new[] { Path.Combine("generate", "models"), Path.Combine(".cache", "generate") }),
    };

    /// <summary>What a stopped download of the part left behind (resumed when it starts again), in bytes.</summary>
    public static long PartialBytes(AiPart part, RuntimeLayout? rt)
    {
        if (rt is null || part.PartialDirs is null) return 0;
        long total = 0;
        foreach (var d in part.PartialDirs)
        {
            var dir = Path.Combine(rt.Root, d);
            if (!Directory.Exists(dir)) continue;
            // .cache itself holds the player's and Animate's downloads; its folders belong to the other parts
            var depth = d == ".cache" ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
            try { total += new DirectoryInfo(dir).GetFiles("*.part", depth).Sum(f => f.Length); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    /// <summary>Why it can't install for lack of disk space, or null when there's room.</summary>
    public static string? DiskProblem(AiPart part, RuntimeLayout? rt, PcGraphics g, Func<string, long?>? freeBytes = null)
    {
        if (rt is null) return null;
        freeBytes ??= FreeBytes;
        if (freeBytes(rt.Root) is not { } free) return null;
        var need = part.NeedGb(g) * (1L << 30) - PartialBytes(part, rt);
        if (free >= need) return null;
        var drive = Path.GetPathRoot(Path.GetFullPath(rt.Root))?.TrimEnd('\\', '/') is { Length: > 0 } r ? r : "the drive";
        return $"{part.Name} needs about {part.NeedGb(g):0.#} GB free on {drive} while it installs; there's {free / (double)(1L << 30):0.#} GB. "
               + "Free some space (or empty the Recycle Bin) and try again.";
    }

    private static long? FreeBytes(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    public static AiPart Get(string id) => All.First(p => p.Id == id);

    /// <summary>The setup scripts' folder, next to runtime\.</summary>
    public static string SetupDir(RuntimeLayout rt) => Path.Combine(Path.GetDirectoryName(rt.Root) ?? rt.Root, "setup");

    /// <summary>The installed state of a part, before anything is running.</summary>
    public static PartState StateOf(AiPart part, RuntimeLayout? rt, PcGraphics graphics)
    {
        if (part.IsInstalled(rt))
        {
            if (part.NeedsUpdate?.Invoke(rt) == true) return PartState.UpdateAvailable;
            if (part.Preparable && NeedsPreparing(rt)) return PartState.NeedsPreparing;
            return PartState.Ready;
        }
        return part.WhyNot?.Invoke(graphics) is null ? PartState.NotInstalled : PartState.NotForThisPc;
    }

    // ------------------------------------------------------------------ preparing Enhance's models

    // RealESRGANv2 (AnimeVideo v3) isn't here: anime uses Real-CUGAN at every size since 0.3.1, so a copy
    // left by an older version is neither prepared nor counted as needing it
    private static readonly string[] ModelFolders = { "enhance", "cugan", "dpir", "restore" };

    /// <summary>Enhance's installed model files (the ones enhance_build.py prepares).</summary>
    public static IReadOnlyList<string> EnhanceModelFiles(RuntimeLayout? rt)
    {
        if (rt is null) return Array.Empty<string>();
        var models = Path.Combine(rt.MpvDir, "vs-plugins", "models");
        return ModelFolders.Select(f => Path.Combine(models, f)).Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.onnx")).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>With TensorRT, a model is prepared once for the card: listed by enhance_build.py in
    /// runtime\enhance-prepared.txt, or its engine already next to it. Without TensorRT nothing needs it.</summary>
    public static bool NeedsPreparing(RuntimeLayout? rt) => UnpreparedModels(rt).Count > 0;

    public static IReadOnlyList<string> UnpreparedModels(RuntimeLayout? rt)
    {
        if (rt is null || !EngineAvailability.Of(rt).TensorRt) return Array.Empty<string>();
        var list = Path.Combine(rt.Root, "enhance-prepared.txt");
        var listed = File.Exists(list)
            ? new HashSet<string>(File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return EnhanceModelFiles(rt).Where(f =>
        {
            var name = Path.GetFileName(f);
            return !listed.Contains(name) && Directory.GetFiles(Path.GetDirectoryName(f)!, name + "*.engine").Length == 0;
        }).ToList();
    }
}

/// <summary>A progress report from a running setup script or model preparation.</summary>
/// <param name="Overall">0..1 of the whole job, or null while there's no measure.</param>
public sealed record PartProgress(double? Overall, string Text);

/// <summary>Reads the @@ lines setup\Progress.ps1 (and enhance_build.py) print.</summary>
public sealed class PartProgressReader
{
    private double _from = -1, _to = -1;
    private string _step = "";

    public string? Error { get; private set; }

    /// <summary>The report for one output line, or null when the line isn't one.</summary>
    public PartProgress? Read(string line)
    {
        if (!line.StartsWith("@@", StringComparison.Ordinal)) return null;
        var (verb, rest) = Split(line[2..]);
        switch (verb)
        {
            case "step":
            {
                var (a, r1) = Split(rest);
                var (b, text) = Split(r1);
                _from = Number(a) ?? -1;
                _to = Number(b) ?? -1;
                _step = text;
                return new PartProgress(_from >= 0 ? _from : null, text);
            }
            case "progress":
            {
                var (f, text) = Split(rest);
                var frac = Number(f) ?? -1;
                if (_from < 0) return new PartProgress(null, text.Length > 0 ? text : _step);
                var overall = frac < 0 ? _from : _from + (_to - _from) * Math.Clamp(frac, 0, 1);
                return new PartProgress(overall, text.Length > 0 ? text : _step);
            }
            case "error":
                Error = rest.Trim();
                return null;
            default:
                return null;
        }
    }

    private static (string, string) Split(string s)
    {
        s = s.TrimStart();
        var i = s.IndexOf(' ');
        return i < 0 ? (s, "") : (s[..i], s[(i + 1)..].Trim());
    }

    private static double? Number(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>Runs a part's setup script, or the preparing of Enhance's models, hidden.</summary>
public interface IPartRunner
{
    /// <summary>Installs the part. Throws with a plain message when it fails.</summary>
    Task InstallAsync(AiPart part, IProgress<PartProgress> progress, CancellationToken ct);

    /// <summary>Prepares Enhance's models for this graphics card (TensorRT engines).</summary>
    Task PrepareAsync(IProgress<PartProgress> progress, CancellationToken ct);
}

public sealed class PartRunner(RuntimeLayout runtime) : IPartRunner
{
    public Task InstallAsync(AiPart part, IProgress<PartProgress> progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe");
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AiParts.SetupDir(runtime), part.Script) })
            psi.ArgumentList.Add(a);
        foreach (var a in part.Arguments) psi.ArgumentList.Add(a);
        return RunAsync(psi, progress, ct, $"setup-{part.Id}.log", part.Name);
    }

    public Task PrepareAsync(IProgress<PartProgress> progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(runtime.PythonExe) { WorkingDirectory = runtime.MpvDir };
        psi.ArgumentList.Add(Path.Combine(runtime.Root, "enhance_build.py"));
        psi.ArgumentList.Add("all");
        return RunAsync(psi, progress, ct, "prepare-models-app.log", "Preparing the models");
    }

    private async Task RunAsync(ProcessStartInfo psi, IProgress<PartProgress> progress, CancellationToken ct, string logName, string what)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        var reader = new PartProgressReader();
        var tail = new Queue<string>();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{what} couldn't start");
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }))
        {
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (reader.Read(line) is { } report) progress.Report(report);
                else if (!line.StartsWith("@@", StringComparison.Ordinal) && line.Trim().Length > 0)
                {
                    tail.Enqueue(line.TrimEnd());
                    if (tail.Count > 200) tail.Dequeue();
                }
            }
            await p.WaitForExitAsync(ct);
        }
        var errors = await stderr;
        try
        {
            Directory.CreateDirectory(Path.Combine(runtime.Root, "logs"));
            await File.WriteAllTextAsync(Path.Combine(runtime.Root, "logs", logName),
                string.Join('\n', tail) + "\n\n" + errors, CancellationToken.None);
        }
        catch (IOException) { }
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(reader.Error is { Length: > 0 } e ? e : $"{what} stopped (details in runtime\\logs\\{logName})");
    }
}
