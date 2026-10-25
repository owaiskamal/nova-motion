// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaMotion.Core;

/// <summary>A choice shown in the UI: value stored in settings, label, one-line explanation.</summary>
public sealed record Choice(string Value, string Label, string Description = "", bool Available = true, string? Badge = null);

/// <summary>Names and options of the interpolation engines and frame-rate targets.</summary>
public static class EngineCatalog
{
    public static string EngineName(string engine)
    {
        var parts = engine.Split('@', 2);
        var name = parts[0] switch
        {
            "rife_trt" => "RIFE · TensorRT",
            "rife" => "RIFE · Vulkan",
            "mvtools" => "MVTools",
            _ => parts[0],
        };
        if (parts.Length < 2) return name;
        // Benchmark rows: "rife_trt@0.5" is the AI on a half-size copy, "rife@1" Vulkan device 1
        return parts[0] == "rife_trt" ? $"{name}, AI at {((double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) ? sc : 1) * 100).ToString("0", CultureInfo.InvariantCulture)}% size"
             : parts[0] == "rife" ? $"{name}, graphics chip {parts[1]}" : name;
    }

    public static IReadOnlyList<Choice> Engines(EngineAvailability a) => new[]
    {
        new Choice("rife_trt", "RIFE · TensorRT", "AI frames on NVIDIA RTX tensor cores. Fastest and sharpest.",
                   a.TensorRt, a.TensorRt ? "Fastest" : "Needs setup"),
        new Choice("rife", "RIFE · Vulkan", "The same AI model on any modern GPU (NVIDIA, AMD, Intel).", a.RifeModels > 0),
        new Choice("mvtools", "MVTools", "Classic motion vectors on the CPU. Light, works everywhere.", a.MvTools),
    };

    public static readonly IReadOnlyList<Choice> Targets = new[]
    {
        new Choice("display", "Match display", "Your screen's refresh rate (e.g. 60, 120, 144 Hz)."),
        new Choice("display/2", "Half display", "Half the refresh rate on 100 Hz+ screens. Lighter on the GPU."),
        new Choice("60", "60 fps"),
        new Choice("120", "120 fps"),
        new Choice("x2", "2× source", "Doubles the frames: 24 → 48, 30 → 60."),
        new Choice("x3", "3× source"),
    };

    public static string TargetName(string target) =>
        Targets.FirstOrDefault(t => t.Value == target)?.Label
        ?? (double.TryParse(target, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? $"{n:0.#} fps" : target);

    public static readonly IReadOnlyList<Choice> MvQualities = new[]
    {
        new Choice("fast", "Fast"), new Choice("balanced", "Balanced"), new Choice("quality", "Quality"),
    };

    public static readonly IReadOnlyList<Choice> OnlineHeights = new[]
    {
        new Choice("720", "720p"), new Choice("1080", "1080p"), new Choice("1440", "1440p"), new Choice("2160", "4K"),
    };

    /// <summary>"rife-v4.22_lite_ensembleFalse" → "RIFE 4.22 Lite".</summary>
    public static string ModelName(string model)
    {
        var m = Regex.Match(model, @"v(\d+)\.(\d+)(?:[_-](lite|heavy))?", RegexOptions.IgnoreCase);
        if (!m.Success) return model;
        var name = $"RIFE {m.Groups[1].Value}.{m.Groups[2].Value}";
        if (m.Groups[3].Success) name += " " + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(m.Groups[3].Value.ToLowerInvariant());
        if (model.Contains("ensembleTrue", StringComparison.OrdinalIgnoreCase)) name += " (ensemble)";
        return name;
    }
}

/// <summary>Which engines this machine can run, from what setup installed.</summary>
public sealed record EngineAvailability(bool TensorRt, int RifeModels, bool MvTools, bool YtDlp,
                                        IReadOnlyList<string> TensorRtSizes)
{
    public static EngineAvailability Of(RuntimeLayout? rt)
    {
        if (rt is null) return new(false, 0, false, false, Array.Empty<string>());
        var plugins = Path.Combine(rt.MpvDir, "vs-plugins");
        bool Has(string pattern) => Directory.Exists(plugins) && Directory.GetFiles(plugins, pattern).Length > 0;
        return new(
            TensorRt: Has("vstrt*.dll") && File.Exists(Path.Combine(rt.MpvDir, "vsmlrt.py")),
            RifeModels: rt.RifeModels().Count,
            MvTools: Has("*mvtools*.dll") || !OperatingSystem.IsWindows(),
            YtDlp: File.Exists(rt.YtDlpExe),
            TensorRtSizes: TensorRtEngines.ReadSizes(rt.TrtEngineDir));
    }
}

/// <summary>
/// TensorRT engines are compiled per GPU and per frame size; the first play at a new size
/// pauses for that build. The engine records each size it has built in a small manifest.
/// </summary>
public static class TensorRtEngines
{
    public const string ManifestName = "novamotion-engines.json";

    /// <summary>Frame sizes with a built engine, e.g. "1920x1088" (padded to multiples of 64).</summary>
    public static IReadOnlyList<string> ReadSizes(string dir)
    {
        try
        {
            var path = Path.Combine(dir, ManifestName);
            if (!File.Exists(path)) return Array.Empty<string>();
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject o
                ? o.Select(kv => kv.Key).OrderBy(k => k, NaturalComparer.Instance).ToList()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The padded size the engine uses for a video (RIFE needs multiples of 64).</summary>
    public static string PaddedSize(int width, int height) =>
        $"{width + ((64 - width % 64) % 64)}x{height + ((64 - height % 64) % 64)}";

    public static bool IsReady(EngineAvailability a, int width, int height) =>
        a.TensorRtSizes.Contains(PaddedSize(width, height));

    /// <summary>Common sizes offered for preparing ahead, as (label, width, height).</summary>
    public static readonly IReadOnlyList<(string Label, int Width, int Height)> CommonSizes = new[]
    {
        ("720p", 1280, 720), ("1080p", 1920, 1080), ("1440p", 2560, 1440), ("4K", 3840, 2160),
    };

    /// <summary>"1920x1088" → "1080p" where it matches a common size.</summary>
    public static string Describe(string size)
    {
        foreach (var (label, w, h) in CommonSizes)
            if (PaddedSize(w, h) == size) return label;
        return size.Replace('x', '×');
    }
}

/// <summary>Results of the last benchmark.cmd run, parsed from runtime\benchmark.log.</summary>
public static class BenchmarkLog
{
    public sealed record Result(string Engine, double? Fps);

    public static IReadOnlyList<Result> Parse(string text)
    {
        // Each engine section starts with "==> rife_trt" and ends with "=> 81.3 fps"
        var results = new List<Result>();
        string? engine = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var start = Regex.Match(line, @"^==>\s+(\S+)");
            if (start.Success) { engine = start.Groups[1].Value; continue; }
            if (engine is null) continue;
            var fps = Regex.Match(line, @"^=>\s+([\d.,]+)\s+fps");
            if (fps.Success && double.TryParse(fps.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                results.Add(new Result(engine, v)); engine = null;
            }
            else if (line.StartsWith("HUNG", StringComparison.Ordinal) || line.Contains("failed", StringComparison.OrdinalIgnoreCase) && line.StartsWith("=>"))
            {
                results.Add(new Result(engine, null)); engine = null;
            }
        }
        return results;
    }

    /// <summary>The Vulkan device RIFE ran fastest on, when the benchmark tried more than one.</summary>
    public static int? FastestVulkanDevice(IReadOnlyList<Result> results)
    {
        var vulkan = results.Where(r => r.Fps is not null && r.Engine.StartsWith("rife@", StringComparison.Ordinal)).ToList();
        if (vulkan.Count < 2) return null;
        var best = vulkan.MaxBy(r => r.Fps!.Value)!;
        return int.TryParse(best.Engine[5..], out var id) ? id : null;
    }

    public static IReadOnlyList<Result> Read(RuntimeLayout? rt)
    {
        if (rt is null) return Array.Empty<Result>();
        try
        {
            var path = Path.Combine(rt.Root, "benchmark.log");
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : Array.Empty<Result>();
        }
        catch (IOException) { return Array.Empty<Result>(); }
    }
}
