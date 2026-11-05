// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Diagnostics;
using System.Globalization;

namespace NovaMotion.Core;

/// <summary>How the camera moves through the picture.</summary>
public enum CameraMove { Push, Pull, Drift, Rise, Sway, Orbit }

/// <summary>One "Animate a picture" job.</summary>
public sealed record AnimateOptions(CameraMove Move = CameraMove.Push, double Seconds = 6, double Strength = 1, int Fps = 30);

/// <summary>A line the parallax script printed: a stage, progress, the finished file or an error.</summary>
public sealed record AnimateLine(string Kind, string Text, double Progress);

/// <summary>
/// Generate's "Animate a picture": a still picture becomes a short 3D camera move. The depth comes
/// from Depth Anything V2 Small on the graphics card through DirectML, so it runs on any DirectX 12
/// card, a GTX 1650 or Intel Xe included. The work is done by engine/parallax.py on the runtime's Python.
/// </summary>
public static class Animate
{
    public static readonly IReadOnlyList<Choice> Moves = new[]
    {
        new Choice("push", "Push in", "The camera moves slowly towards the picture."),
        new Choice("pull", "Pull out", "The camera eases back, showing more of the scene."),
        new Choice("drift", "Drift", "The camera slides sideways; near things pass faster than far ones."),
        new Choice("rise", "Rise", "The camera lifts gently."),
        new Choice("sway", "Sway", "Side to side and back: loops without a seam."),
        new Choice("orbit", "Orbit", "A small circle around the subject: loops without a seam."),
    };

    public static readonly IReadOnlyList<Choice> Strengths = new[]
    {
        new Choice("0.6", "Subtle"), new Choice("1", "Medium"), new Choice("1.6", "Strong"),
    };

    public static readonly IReadOnlyList<Choice> Lengths = new[]
    {
        new Choice("4", "4 s"), new Choice("6", "6 s"), new Choice("8", "8 s"), new Choice("12", "12 s"),
    };

    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff" };

    public static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static string MoveName(CameraMove m) => m.ToString().ToLowerInvariant();

    public static CameraMove ParseMove(string? value) =>
        Enum.TryParse<CameraMove>(value, ignoreCase: true, out var m) ? m : CameraMove.Push;

    /// <summary>Everything the runtime needs is there: the script, the depth model and the packages.</summary>
    public static bool IsReady(RuntimeLayout? rt) =>
        rt is not null && File.Exists(rt.ParallaxScript) && File.Exists(rt.DepthModel) && Directory.Exists(rt.StudioPackages)
        && Directory.GetFiles(rt.StudioPackages, "onnxruntime-directml-*.installed").Length > 0;

    /// <summary>"Lighthouse - push in.mp4" next to the picture's name in the folder, never over an existing file.</summary>
    public static string OutputPath(string image, CameraMove move, string folder)
    {
        var name = Path.GetFileNameWithoutExtension(image);
        var label = Moves.First(c => c.Value == MoveName(move)).Label.ToLowerInvariant();
        var path = Path.Combine(folder, $"{name} - {label}.mp4");
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name} - {label} ({i}).mp4");
        return path;
    }

    public static IReadOnlyList<string> Arguments(RuntimeLayout rt, string image, string output, AnimateOptions o)
    {
        var inv = CultureInfo.InvariantCulture;
        return new[]
        {
            "-I", rt.ParallaxScript,
            "--image", image, "--out", output, "--move", MoveName(o.Move),
            "--seconds", o.Seconds.ToString("0.##", inv), "--strength", o.Strength.ToString("0.##", inv),
            "--fps", o.Fps.ToString(inv),
            "--model", rt.DepthModel, "--mpv", rt.MpvExe, "--packages", rt.StudioPackages,
        };
    }

    public static AnimateLine? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var space = line.IndexOf(' ');
        var kind = space < 0 ? line : line[..space];
        var rest = space < 0 ? "" : line[(space + 1)..].Trim();
        return kind switch
        {
            "progress" when double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => new(kind, "", Math.Clamp(p, 0, 1)),
            "stage" or "done" or "error" => new(kind, rest, 0),
            _ => null,
        };
    }

    /// <summary>What the page says during each stage.</summary>
    public static string StageText(string stage) => stage switch
    {
        "picture" => "Reading the picture…",
        "depth" => "Working out depth…",
        "frames" => "Drawing the camera move…",
        _ => "Working…",
    };
}

public interface IAnimateRunner
{
    /// <summary>Makes the video; reports (stage text, 0..1). Throws with a readable message on failure.</summary>
    Task RunAsync(string image, string output, AnimateOptions options, IProgress<(string Stage, double Progress)> progress, CancellationToken ct);
}

/// <summary>Runs engine/parallax.py on the runtime's Python and follows its output.</summary>
public sealed class AnimateRunner(RuntimeLayout runtime) : IAnimateRunner
{
    public async Task RunAsync(string image, string output, AnimateOptions options, IProgress<(string Stage, double Progress)> progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(runtime.PythonExe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = runtime.MpvDir,
        };
        foreach (var a in Animate.Arguments(runtime, image, output, options)) psi.ArgumentList.Add(a);
        var log = Path.Combine(runtime.Root, "animate.log");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Python didn't start");
        var stderr = p.StandardError.ReadToEndAsync(ct);
        string? error = null, stage = "Working…";
        double share = 0;
        await using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }))
        {
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (Animate.Parse(line) is not { } l) continue;
                if (l.Kind == "stage") { stage = Animate.StageText(l.Text); share = l.Text == "frames" ? 0.1 : share; }
                else if (l.Kind == "progress") share = 0.1 + 0.9 * l.Progress;
                else if (l.Kind == "error") error = l.Text;
                progress.Report((stage, share));
            }
            await p.WaitForExitAsync(ct);
        }
        var details = await stderr;
        try { await File.WriteAllTextAsync(log, $"{string.Join(' ', psi.ArgumentList)}\n\n{details}", CancellationToken.None); }
        catch (IOException) { }
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException(error ?? "The picture couldn't be animated; details are in runtime\\animate.log.");
    }
}
