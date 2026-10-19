// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Diagnostics;

namespace NovaMotion.Core;

/// <summary>Locates the runtime folder produced by setup\Get-Runtime.ps1.</summary>
public sealed class RuntimeLayout
{
    public string Root { get; }
    public string MpvDir => Path.Combine(Root, "mpv");
    public string MpvExe => Path.Combine(MpvDir, OperatingSystem.IsWindows() ? "mpv.exe" : "mpv");
    public string YtDlpExe => Path.Combine(MpvDir, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
    public string EngineScript => Path.Combine(Root, "novamotion.vpy");
    public string ModelsDir => Path.Combine(Root, "models");
    public string TrtEngineDir => Path.Combine(Root, "trt-engines");

    private RuntimeLayout(string root) => Root = root;

    /// <summary>Looks for a "runtime" folder next to the app or up to four parent folders up.</summary>
    public static RuntimeLayout? Find(string? startDir = null)
    {
        var dir = new DirectoryInfo(startDir ?? AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "runtime");
            if (File.Exists(Path.Combine(candidate, "novamotion.vpy"))) return new RuntimeLayout(candidate);
        }
        return null;
    }

    public IReadOnlyList<string> RifeModels() =>
        System.IO.Directory.Exists(ModelsDir)
            ? System.IO.Directory.GetDirectories(ModelsDir)
                .Where(d => File.Exists(Path.Combine(d, "flownet.param")))
                .Select(Path.GetFileName).OfType<string>().OrderByDescending(n => n).ToList()
            : Array.Empty<string>();

    public IEnumerable<string> Problems()
    {
        if (!File.Exists(MpvExe)) yield return $"mpv not found at {MpvExe}";
        if (!File.Exists(EngineScript)) yield return $"engine script missing: {EngineScript}";
        if (!File.Exists(Path.Combine(MpvDir, "VSScript.dll")) && OperatingSystem.IsWindows())
            yield return "VapourSynth is not installed in the runtime";
        if (!File.Exists(YtDlpExe)) yield return "yt-dlp missing: online links will not play";
        if (RifeModels().Count == 0) yield return "no RIFE models found: only MVTools will work";
    }
}

public enum GpuTier { Unknown, Integrated, Entry, Performance }

public sealed record GpuInfo(IReadOnlyList<string> Names)
{
    public string Primary => Names.FirstOrDefault(IsDiscrete) ?? Names.FirstOrDefault() ?? "Unknown GPU";
    public bool IsNvidia => Names.Any(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
    public bool IsNvidiaRtx => Names.Any(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                                             && n.Contains("RTX", StringComparison.OrdinalIgnoreCase));
    public bool IsIntegratedOnly => Names.Count > 0 && !Names.Any(IsDiscrete);

    /// <summary>TensorRT needs an NVIDIA Turing card or newer: every RTX, and the GTX 16 series.</summary>
    public bool SupportsTensorRt => Names.Any(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
        && (n.Contains("RTX", StringComparison.OrdinalIgnoreCase) || GtxSixteen.IsMatch(n)));

    private static readonly System.Text.RegularExpressions.Regex GtxSixteen =
        new(@"GTX\s*16\d\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Cards that run RIFE, but not every model at 1080p: GTX, MX, the smallest RTX laptop chips,
    // entry Radeon and Arc. They get the light models and a 60 fps target until tuned.
    private static readonly System.Text.RegularExpressions.Regex EntryCard = new(
        @"GTX|\bMX\s*\d|RTX\s*(2050|3050)|Radeon\s*RX\s*(5[0-6]\d|6[45]\d\d|7600)|Arc\s*A3",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public GpuTier Tier =>
        Names.Count == 0 ? GpuTier.Unknown
        : IsIntegratedOnly ? GpuTier.Integrated
        : EntryCard.IsMatch(Primary) ? GpuTier.Entry
        : GpuTier.Performance;

    private static bool IsDiscrete(string n) =>
        n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
        || n.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase)
        || n.Contains("Arc", StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks Windows for the installed display adapters (no extra packages needed).</summary>
    public static async Task<GpuInfo> DetectAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return new GpuInfo(Array.Empty<string>());
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_VideoController).Name\"")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var names = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(n => !n.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                         && !n.Contains("Remote", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return new GpuInfo(names);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new GpuInfo(Array.Empty<string>());
        }
    }
}
