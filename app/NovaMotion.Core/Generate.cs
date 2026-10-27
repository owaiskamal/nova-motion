// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Diagnostics;
using System.Globalization;

namespace NovaMotion.Core;

/// <summary>How well a model runs on this PC.</summary>
public enum ModelFit { Good, Slow, TooHeavy, Unsupported }

/// <summary>An open image or video model NOVA Studio plans to offer, with what it needs.</summary>
/// <param name="GoodGb">Graphics memory for full-quality weights at a comfortable speed.</param>
/// <param name="MinGb">Graphics memory for quantised weights with part of the model in system memory.</param>
public sealed record GenModel(string Id, string Name, string Kind, string License, double GoodGb, double MinGb, string Note);

/// <summary>A model's verdict for this PC, as shown on the Generate page.</summary>
/// <param name="Reason">Why the model can't run, for the summary; rows show the model's description instead.</param>
public sealed record ModelVerdict(GenModel Model, ModelFit Fit, string Text, string Reason = "")
{
    public string Name => Model.Name;
    public string Kind => Model.Kind;
    public string License => Model.License;
    public string FitLabel => Fit switch
    {
        ModelFit.Good => "Runs well",
        ModelFit.Slow => "Runs, slower",
        ModelFit.TooHeavy => "Too heavy",
        _ => "Not supported",
    };
    public bool IsGood => Fit == ModelFit.Good;
    public bool IsSlow => Fit == ModelFit.Slow;
    public bool IsOut => Fit is ModelFit.TooHeavy or ModelFit.Unsupported;
}

/// <summary>
/// The open models Generate starts with, and what a PC needs to run them. Only models whose
/// licence allows commercial use are listed; users download them from the publisher.
/// </summary>
public static class GenerateCatalog
{
    /// <summary>System memory recommended for video: quantised runs keep part of the model in it.</summary>
    public const double VideoRamGb = 32;

    public static readonly IReadOnlyList<GenModel> Models = new[]
    {
        new GenModel("sdxl", "SDXL", "Images", "CreativeML Open RAIL++-M", 8, 6,
                     "Fast 1024×1024 images and the largest choice of styles."),
        new GenModel("flux-schnell", "Flux.1 Schnell", "Images", "Apache 2.0", 16, 8,
                     "Sharp images and readable text in 4 steps."),
        new GenModel("wan22-5b", "Wan 2.2 TI2V-5B", "Video", "Apache 2.0", 24, 12,
                     "Text or image to 5 s video at 720p."),
        new GenModel("wan22-14b", "Wan 2.2 I2V-14B", "Video", "Apache 2.0", 32, 12,
                     "The best open image-to-video model; 480p on 12 GB cards."),
    };

    public static ModelVerdict Assess(GenModel m, GpuInfo? gpu, double? vramGb)
    {
        if (gpu is null || gpu.Names.Count == 0)
            return new(m, ModelFit.Unsupported, m.Note, "Graphics card not detected yet.");
        if (!gpu.IsNvidia)
            return new(m, ModelFit.Unsupported, m.Note, "Generate needs an NVIDIA graphics card for now. AMD and Intel support depends on the models' tools.");
        if (vramGb is not { } gb)
            return new(m, ModelFit.Unsupported, m.Note, "Couldn't read the graphics card's memory.");
        var size = $"{gb:0.#} GB";
        if (gb >= m.GoodGb - 0.5)
            return new(m, ModelFit.Good, $"{m.Note} Your {size} fits the full model.");
        if (gb >= m.MinGb - 0.5)
            return new(m, ModelFit.Slow, $"{m.Note} On {size} it uses compressed weights and system memory: a little less detail, a few times slower.");
        return new(m, ModelFit.TooHeavy, $"Needs at least {m.MinGb:0} GB of graphics memory; this card has {size}.");
    }

    public static IReadOnlyList<ModelVerdict> AssessAll(GpuInfo? gpu, double? vramGb) =>
        Models.Select(m => Assess(m, gpu, vramGb)).ToList();

    /// <summary>One plain sentence on what this PC can make.</summary>
    public static string Summary(IReadOnlyList<ModelVerdict> verdicts, double? ramGb)
    {
        if (verdicts.Count == 0 || verdicts.All(v => v.Fit == ModelFit.Unsupported))
            return verdicts.FirstOrDefault()?.Reason ?? "";
        bool Can(string kind) => verdicts.Any(v => v.Kind == kind && v.Fit is ModelFit.Good or ModelFit.Slow);
        var text = (Can("Images"), Can("Video")) switch
        {
            (true, true) => "This PC can make both images and video.",
            (true, false) => "This PC can make images. Video needs a card with 12 GB or more.",
            _ => "This graphics card is too small for local image and video models. Motion and Enhance work fully.",
        };
        if (Can("Video") && ramGb is { } ram && ram < VideoRamGb - 1)
            text += $" For video, {VideoRamGb:0} GB of system memory is recommended; this PC has {ram:0} GB.";
        return text;
    }
}

/// <summary>Graphics and system memory, which decide what Generate can run.</summary>
public static class SystemMemory
{
    /// <summary>
    /// Dedicated memory of the largest graphics card, in GB. Windows' usual adapter query caps at
    /// 4 GB, so this reads the 64-bit size the driver records in the registry instead.
    /// </summary>
    public static async Task<double?> GraphicsGbAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return null;
        const string script =
            "Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}\\0*' " +
            "-ErrorAction SilentlyContinue | ForEach-Object { $_.'HardwareInformation.qwMemorySize' }";
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return ParseLargestGb(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The largest of one byte count per line, in GB (1024³).</summary>
    public static double? ParseLargestGb(string output)
    {
        double? best = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (ulong.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes > 0)
                best = Math.Max(best ?? 0, Math.Round(bytes / 1073741824.0, 1));
        return best;
    }

    /// <summary>Installed system memory, in GB.</summary>
    public static double? SystemGb()
    {
        var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return bytes > 0 ? Math.Round(bytes / 1073741824.0) : null;
    }
}
