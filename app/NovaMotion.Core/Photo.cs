// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace NovaMotion.Core;

/// <summary>One "Enhance a photo" job. Restore: Remove noise and blur, how much of NAFNet's result is kept (0: off).
/// Generative: SeedVR2 makes new detail instead (engine/generate.py); the other choices then don't apply.</summary>
public sealed record PhotoOptions(string Preset = "film", int Scale = 2, string Clean = "light", bool MaxDetail = false,
                                  bool FixCompression = false, double Detail = 0.2, double Restore = 0, bool Generative = false)
{
    /// <summary>"standard", "max" or "generative": the Model choice.</summary>
    public string Model => Generative ? "generative" : MaxDetail ? "max" : "standard";
}

/// <summary>
/// Enhance's photo mode: one picture through the same models as videos (engine/photo.py), plus the heavy
/// "Max detail" models a whole video can't afford. The result is a PNG (or JPEG for JPEG photos) in
/// Pictures\NOVA Studio.
/// </summary>
public static class Photo
{
    public static readonly IReadOnlyList<Choice> Presets = new[]
    {
        new Choice("film", "Photo & film", "Photos, live action and anything real."),
        new Choice("anime", "Anime & art", "Drawings, anime, art and graphics: clean lines and flat colour."),
    };

    public static readonly IReadOnlyList<Choice> Scales = new[]
    {
        new Choice("1", "Same size", "Cleaned and sharpened at its own size."),
        new Choice("2", "2×", "Twice the width and height."),
        new Choice("4", "4×", "Four times the width and height (up to 8K on the long side)."),
    };

    public static readonly IReadOnlyList<Choice> Models = new[]
    {
        new Choice("standard", "Standard", "The video models: quick, natural."),
        new Choice("max", "Max detail", "Real-ESRGAN x4plus: adds much more texture and crisper edges. About 10 times slower, still seconds to a minute a photo."),
        new Choice("generative", "Generative", "SeedVR2, generative AI: looks at the whole picture and makes new detail that fits it (skin, hair, fabric, leaves). Needs an NVIDIA card with 8 GB or more."),
    };

    /// <summary>setup-generate.cmd finished: the runner, the model and the Python that runs them.</summary>
    public static bool HasGenerative(RuntimeLayout? rt) =>
        rt is not null && File.Exists(rt.GenerateReady) && File.Exists(rt.GenerateScript) && File.Exists(rt.GeneratePython);

    /// <summary>The graphics card's memory setup-generate.cmd found, in GB (0: unknown).</summary>
    public static double GenerateVramGb(RuntimeLayout? rt)
    {
        if (rt is null || !File.Exists(rt.GenerateReady)) return 0;
        foreach (var line in File.ReadAllLines(rt.GenerateReady))
            if (line.StartsWith("vram_mb ", StringComparison.Ordinal)
                && double.TryParse(line[8..], NumberStyles.Float, CultureInfo.InvariantCulture, out var mb)) return Math.Round(mb / 1024, 1);
        return 0;
    }

    /// <summary>The script, Pillow (setup-enhance.cmd adds it) and the Enhance models are there.</summary>
    public static bool IsReady(RuntimeLayout? rt) =>
        rt is not null && File.Exists(rt.PhotoScript) && Directory.Exists(rt.StudioPackages)
        && Directory.GetFiles(rt.StudioPackages, "pillow-*.installed").Length > 0 && EnhanceCatalog.IsReady(rt);

    /// <summary>The Max detail models are installed (enhance-models-v2).</summary>
    public static bool HasMaxDetail(RuntimeLayout? rt) =>
        rt is not null && File.Exists(Path.Combine(rt.MpvDir, "vs-plugins", "models", "enhance", "RealESRGAN_x4plus.onnx"));

    /// <summary>Remove noise and blur's model is installed (enhance-models-v3).</summary>
    public static bool HasRestore(RuntimeLayout? rt) =>
        rt is not null && File.Exists(Path.Combine(rt.MpvDir, "vs-plugins", "models", "restore", "nafnet_reds64.onnx"));

    /// <summary>The usual amount of Remove noise and blur: three quarters of NAFNet's picture, so skin keeps some of its texture.</summary>
    public const double RestoreAmount = 0.75;

    /// <summary>"Beach (NOVA Enhance 2x).png" in the folder, never over an existing file; JPEG photos stay JPEG.</summary>
    public static string OutputPath(string image, PhotoOptions o, string folder)
    {
        var name = Path.GetFileNameWithoutExtension(image);
        var ext = Path.GetExtension(image).ToLowerInvariant() is ".jpg" or ".jpeg" ? ".jpg" : ".png";
        var label = o.Scale <= 1 ? "NOVA Enhance" : $"NOVA Enhance {o.Scale}x";
        if (o.Generative) label += " generative";
        else if (o.MaxDetail) label += " max";
        var path = Path.Combine(folder, $"{name} ({label}){ext}");
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name} ({label}) ({i}){ext}");
        return path;
    }

    public static string SettingsJson(PhotoOptions o) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["preset"] = o.Preset, ["scale"] = o.Scale, ["clean"] = o.Clean, ["strength"] = "full",
        ["model"] = o.MaxDetail ? "max" : "standard", ["fix_compression"] = o.FixCompression,
        ["detail"] = o.Detail, ["grain"] = 0, ["restore"] = o.Restore,
    });

    public static IReadOnlyList<string> Arguments(RuntimeLayout rt, string image, string output, PhotoOptions o, int gpu = 0, string? gpuClass = null,
                                                  string? compare = null, (double X, double Y)? spot = null)
    {
        var args = new List<string>
        {
            "-I", rt.PhotoScript, "--image", image, "--out", output, "--settings", SettingsJson(o),
            "--packages", rt.StudioPackages, "--gpu", gpu.ToString(CultureInfo.InvariantCulture), "--gpu-class", gpuClass ?? "",
        };
        if (compare is not null) args.AddRange(new[] { "--compare", compare, "--spot", Spot(spot) });
        return args;
    }

    /// <summary>Generative detail: engine/generate.py on setup-generate.cmd's Python, SeedVR2's runner and model.</summary>
    public static IReadOnlyList<string> GenerateArguments(RuntimeLayout rt, string image, string output, PhotoOptions o, int gpu = 0,
                                                          string? compare = null, (double X, double Y)? spot = null)
    {
        var args = new List<string>
        {
            "-I", rt.GenerateScript, "--image", image, "--out", output, "--settings", SettingsJson(o),
            "--runner", rt.GenerateRunner, "--models", rt.GenerateModels, "--gpu", gpu.ToString(CultureInfo.InvariantCulture),
            "--vram", GenerateVramGb(rt).ToString(CultureInfo.InvariantCulture),
        };
        if (compare is not null) args.AddRange(new[] { "--compare", compare, "--spot", Spot(spot) });
        return args;
    }

    /// <summary>Only the before/after for a result already made, centred elsewhere: under a second.</summary>
    public static IReadOnlyList<string> CompareArguments(RuntimeLayout rt, string image, string result, string compare, (double X, double Y) spot) =>
        new[] { "-I", rt.PhotoScript, "--image", image, "--out", result, "--compare", compare, "--spot", Spot(spot), "--compare-only",
                "--packages", rt.StudioPackages };

    public static string Spot((double X, double Y)? spot) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Clamp(spot?.X ?? 0.5, 0, 1):0.###},{Math.Clamp(spot?.Y ?? 0.5, 0, 1):0.###}");

    /// <summary>A new file for each before/after: the picture on screen is cached by its path.</summary>
    public static string ComparePath(RuntimeLayout rt) =>
        Path.Combine(rt.Root, "previews", $"photo-compare-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.png");

    /// <summary>"3/12" → 0.25.</summary>
    public static double? ParseProgress(string text)
    {
        var parts = text.Trim().Split('/');
        return parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b) && b > 0 ? Math.Clamp(a / (double)b, 0, 1) : null;
    }

    /// <summary>What the page says during each stage.</summary>
    public static string StageText(string stage) => stage switch
    {
        "picture" => "Reading the picture…",
        "clean" => "Fixing compression…",
        "restore" => "Removing noise and blur…",
        "generate" => "Generating detail…",
        "enhance" => "Enhancing…",
        "save" => "Saving…",
        _ => "Working…",
    };
}

public interface IPhotoRunner
{
    /// <summary>Makes the picture and, with compare, its before/after at 100% around spot; reports stage
    /// text. Throws with a readable message on failure.</summary>
    Task RunAsync(string image, string output, PhotoOptions options, IProgress<string> stage, CancellationToken ct,
                  string? compare = null, (double X, double Y)? spot = null, IProgress<double>? done = null);

    /// <summary>Only the before/after of a result, around another spot.</summary>
    Task CompareAsync(string image, string result, string compare, (double X, double Y) spot, CancellationToken ct);
}

/// <summary>Runs engine/photo.py on the runtime's Python and follows its output.</summary>
public sealed class PhotoRunner(RuntimeLayout runtime, Func<(int Gpu, string? GpuClass)>? gpu = null) : IPhotoRunner
{
    public Task RunAsync(string image, string output, PhotoOptions options, IProgress<string> stage, CancellationToken ct,
                         string? compare = null, (double X, double Y)? spot = null, IProgress<double>? done = null)
    {
        var (index, gpuClass) = gpu?.Invoke() ?? (0, null);
        if (compare is not null) Directory.CreateDirectory(Path.GetDirectoryName(compare)!);
        if (options.Generative)
            return RunScriptAsync(Photo.GenerateArguments(runtime, image, output, options, index, compare, spot), output, "photo.log", stage, ct, done,
                                  runtime.GeneratePython);
        return RunScriptAsync(Photo.Arguments(runtime, image, output, options, index, gpuClass, compare, spot), output, "photo.log", stage, ct, done);
    }

    public Task CompareAsync(string image, string result, string compare, (double X, double Y) spot, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(compare)!);
        return RunScriptAsync(Photo.CompareArguments(runtime, image, result, compare, spot), compare, "photo-compare.log", new Progress<string>(), ct);
    }

    private async Task RunScriptAsync(IReadOnlyList<string> args, string output, string logName, IProgress<string> stage, CancellationToken ct,
                                      IProgress<double>? done = null, string? python = null)
    {
        var psi = new ProcessStartInfo(python ?? runtime.PythonExe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = runtime.MpvDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var log = Path.Combine(runtime.Root, logName);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Python didn't start");
        var stderr = p.StandardError.ReadToEndAsync(ct);
        string? error = null;
        var lines = new List<string>();
        await using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }))
        {
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                lines.Add(line);
                if (line.StartsWith("stage ", StringComparison.Ordinal)) stage.Report(Photo.StageText(line[6..].Trim()));
                else if (line.StartsWith("note ", StringComparison.Ordinal)) stage.Report(line[5..].Trim());
                else if (line.StartsWith("progress ", StringComparison.Ordinal) && Photo.ParseProgress(line[9..]) is { } f) done?.Report(f);
                else if (line.StartsWith("error ", StringComparison.Ordinal)) error = line[6..].Trim();
            }
            await p.WaitForExitAsync(ct);
        }
        var details = await stderr;
        try { await File.WriteAllTextAsync(log, $"{string.Join(' ', psi.ArgumentList)}\n\n{string.Join('\n', lines)}\n\n{details}", CancellationToken.None); }
        catch (IOException) { }
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException(error is { } e ? $"The photo couldn't be enhanced: {e}" : "The photo couldn't be enhanced; details are in runtime\\photo.log.");
    }
}
