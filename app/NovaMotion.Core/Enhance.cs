// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Globalization;

namespace NovaMotion.Core;

/// <summary>
/// Enhance's AI clean-up and upscaling for exports: the choices on the page, and whether this PC
/// has what they need. The work itself is engine/enhance.py, run by the export's engine.
/// Every model allows commercial use (THIRD_PARTY_NOTICES.md).
/// </summary>
public static class EnhanceCatalog
{
    public static readonly IReadOnlyList<Choice> Presets = new[]
    {
        new Choice("off", "Smooth only", "Frame interpolation only, at the video's own size."),
        new Choice("film", "Film & live action", "Real-ESRGAN: cleaner, sharper real-world footage, skin, hair and fabric."),
        new Choice("anime", "Anime & art", "Real-CUGAN: crisp lines and flat colour for anime, cartoons and illustration."),
    };

    public static readonly IReadOnlyList<Choice> Sizes = new[]
    {
        new Choice("0", "Original"), new Choice("1080", "1080p"), new Choice("1440", "1440p"),
        new Choice("2160", "4K", "The usual choice: sites like YouTube give 4K uploads a much higher bitrate, so they stay crisp."),
        new Choice("4320", "8K", "Four times the time and file size of 4K."),
    };

    public static readonly IReadOnlyList<Choice> Cleanups = new[]
    {
        new Choice("off", "Keep grain", "Keeps film grain and fine texture."),
        new Choice("light", "Light", "Takes out noise, keeps texture. The usual choice."),
        new Choice("strong", "Strong", "For noisy, low-light or old footage. Smoother, less texture."),
    };

    public static readonly IReadOnlyList<Choice> Strengths = new[]
    {
        new Choice("natural", "Natural", "Mostly the original, gently sharpened by the AI."),
        new Choice("balanced", "Balanced", "Half AI, half original: sharper without the painted look. The usual choice."),
        new Choice("full", "Full AI", "The AI's picture alone: the most detail, and on real footage it can look painted."),
    };

    public static readonly IReadOnlyList<Choice> Details = new[]
    {
        new Choice("0", "Off"), new Choice("0.3", "Crisp"), new Choice("0.7", "Extra crisp"),
    };

    public static readonly IReadOnlyList<Choice> Grains = new[]
    {
        new Choice("0", "None"), new Choice("0.3", "Fine"), new Choice("0.7", "Film"),
    };

    public static string SizeName(int height) => height switch
    {
        <= 0 => "same size",
        4320 => "8K",
        2160 => "4K",
        _ => $"{height}p",
    };

    /// <summary>"Film & live action · 4K · light clean-up", for the export list.</summary>
    public static string Describe(EnhanceSettings e)
    {
        if (!e.IsOn) return "";
        var preset = Presets.First(p => p.Value == e.Preset).Label;
        var clean = e.Clean switch { "off" => "grain kept", "strong" => "strong clean-up", _ => "light clean-up" };
        var strength = e.Strength switch { "natural" => "natural", "full" => "full AI", _ => "balanced" };
        return $"{preset} · {SizeName(e.Height)} · {clean} · {strength}" + (e.FixCompression ? " · compression fixed" : "");
    }

    /// <summary>The models Enhance needs, relative to vs-plugins\models.</summary>
    public static readonly string[] RequiredModels =
    {
        @"enhance\realesr-general-x4v3.onnx", @"enhance\realesr-general-mid-x4v3.onnx", @"enhance\realesr-general-wdn-x4v3.onnx",
        @"cugan\up2x-latest-conservative.onnx", @"RealESRGANv2\realesr-animevideov3.onnx",
    };

    /// <summary>The models, vs-mlrt and a way to run them on the graphics card (TensorRT or Vulkan).</summary>
    public static bool IsReady(RuntimeLayout? rt)
    {
        if (rt is null) return false;
        var plugins = Path.Combine(rt.MpvDir, "vs-plugins");
        bool Has(string pattern) => Directory.Exists(plugins) && Directory.GetFiles(plugins, pattern).Length > 0;
        return File.Exists(Path.Combine(rt.MpvDir, "vsmlrt.py")) && (Has("vstrt*.dll") || Has("vsncnn*.dll"))
               && File.Exists(Path.Combine(rt.Root, "enhance.py"))
               && RequiredModels.All(m => File.Exists(Path.Combine(plugins, "models", m.Replace('\\', Path.DirectorySeparatorChar))));
    }

    public static double Number(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
