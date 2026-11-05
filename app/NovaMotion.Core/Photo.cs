// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace NovaMotion.Core;

/// <summary>One "Enhance a photo" job.</summary>
public sealed record PhotoOptions(string Preset = "film", int Scale = 2, string Clean = "light", bool MaxDetail = false,
                                  bool FixCompression = false, double Detail = 0.2);

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
    };

    /// <summary>The script, Pillow (setup-enhance.cmd adds it) and the Enhance models are there.</summary>
    public static bool IsReady(RuntimeLayout? rt) =>
        rt is not null && File.Exists(rt.PhotoScript) && Directory.Exists(rt.StudioPackages)
        && Directory.GetFiles(rt.StudioPackages, "pillow-*.installed").Length > 0 && EnhanceCatalog.IsReady(rt);

    /// <summary>The Max detail models are installed (enhance-models-v2).</summary>
    public static bool HasMaxDetail(RuntimeLayout? rt) =>
        rt is not null && File.Exists(Path.Combine(rt.MpvDir, "vs-plugins", "models", "enhance", "RealESRGAN_x4plus.onnx"));

    /// <summary>"Beach (NOVA Enhance 2x).png" in the folder, never over an existing file; JPEG photos stay JPEG.</summary>
    public static string OutputPath(string image, PhotoOptions o, string folder)
    {
        var name = Path.GetFileNameWithoutExtension(image);
        var ext = Path.GetExtension(image).ToLowerInvariant() is ".jpg" or ".jpeg" ? ".jpg" : ".png";
        var label = o.Scale <= 1 ? "NOVA Enhance" : $"NOVA Enhance {o.Scale}x";
        if (o.MaxDetail) label += " max";
        var path = Path.Combine(folder, $"{name} ({label}){ext}");
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name} ({label}) ({i}){ext}");
        return path;
    }

    public static string SettingsJson(PhotoOptions o) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["preset"] = o.Preset, ["scale"] = o.Scale, ["clean"] = o.Clean, ["strength"] = "full",
        ["model"] = o.MaxDetail ? "max" : "standard", ["fix_compression"] = o.FixCompression,
        ["detail"] = o.Detail, ["grain"] = 0,
    });

    public static IReadOnlyList<string> Arguments(RuntimeLayout rt, string image, string output, PhotoOptions o, int gpu = 0, string? gpuClass = null) =>
        new[]
        {
            "-I", rt.PhotoScript, "--image", image, "--out", output, "--settings", SettingsJson(o),
            "--packages", rt.StudioPackages, "--gpu", gpu.ToString(CultureInfo.InvariantCulture), "--gpu-class", gpuClass ?? "",
        };

    /// <summary>What the page says during each stage.</summary>
    public static string StageText(string stage) => stage switch
    {
        "picture" => "Reading the picture…",
        "clean" => "Fixing compression…",
        "enhance" => "Enhancing…",
        "save" => "Saving…",
        _ => "Working…",
    };
}

public interface IPhotoRunner
{
    /// <summary>Makes the picture; reports stage text. Throws with a readable message on failure.</summary>
    Task RunAsync(string image, string output, PhotoOptions options, IProgress<string> stage, CancellationToken ct);
}

/// <summary>Runs engine/photo.py on the runtime's Python and follows its output.</summary>
public sealed class PhotoRunner(RuntimeLayout runtime, Func<(int Gpu, string? GpuClass)>? gpu = null) : IPhotoRunner
{
    public async Task RunAsync(string image, string output, PhotoOptions options, IProgress<string> stage, CancellationToken ct)
    {
        var (index, gpuClass) = gpu?.Invoke() ?? (0, null);
        var psi = new ProcessStartInfo(runtime.PythonExe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = runtime.MpvDir,
        };
        foreach (var a in Photo.Arguments(runtime, image, output, options, index, gpuClass)) psi.ArgumentList.Add(a);
        var log = Path.Combine(runtime.Root, "photo.log");
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
