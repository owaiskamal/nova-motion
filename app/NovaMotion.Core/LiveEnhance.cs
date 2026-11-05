// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
namespace NovaMotion.Core;

/// <summary>
/// Live enhance in Motion: GPU shaders that mpv runs on every frame it shows, after smoothing, on any
/// graphics card. Crisp is NOVA Motion's adaptive sharpening (engine/mpv/shaders/nova-crisp*.glsl);
/// Anime is Anime4K's Mode A (Fast) (MIT): restores line art, and enlarges when the screen is bigger
/// than the video. The shaders ship in mpv's config folder, so "~~/shaders" finds them.
/// </summary>
public static class LiveEnhance
{
    public static readonly IReadOnlyList<Choice> Options = new[]
    {
        new Choice("off", "Off", "The picture as it is."),
        new Choice("crisp", "Crisp", "Adaptive sharpening for film and video: clearly crisper edges and texture, without halos. Very light on the GPU."),
        new Choice("crisp-strong", "Extra crisp", "Much stronger, for soft, old or upscaled sources."),
        new Choice("anime", "Anime", "Anime4K: restores line art and enlarges anime to your screen. GTX 16 and laptop graphics get its lighter version, so smoothing keeps up."),
    };

    private static readonly string[] Anime4K =
    {
        "Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_M.glsl", "Anime4K_Upscale_CNN_x2_M.glsl",
        "Anime4K_AutoDownscalePre_x2.glsl", "Anime4K_AutoDownscalePre_x4.glsl", "Anime4K_Upscale_CNN_x2_S.glsl",
    };

    /// <summary>Anime4K's lightest chain: the small restore and upscale networks. The full one with
    /// RIFE dropped 100+ frames every 5 seconds on a GTX 1650.</summary>
    private static readonly string[] Anime4KLight =
    {
        "Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_S.glsl", "Anime4K_Upscale_CNN_x2_S.glsl",
    };

    /// <summary>The shader files for a mode, as mpv paths ("~~/shaders/…"), in order. GTX 16 cards
    /// and integrated graphics ("entry", "integrated") get the light Anime4K chain.</summary>
    public static IReadOnlyList<string> Shaders(string? mode, string? gpuClass = null) => (mode switch
    {
        "crisp" => new[] { "nova-crisp.glsl" },
        "crisp-strong" => new[] { "nova-crisp-strong.glsl" },
        "anime" => gpuClass is "entry" or "integrated" ? Anime4KLight : Anime4K,
        _ => Array.Empty<string>(),
    }).Select(f => "~~/shaders/" + f).ToList();

    /// <summary>mpv's list separator for path lists: ";" on Windows, ":" elsewhere.</summary>
    public static string Joined(string? mode, string? gpuClass = null) =>
        string.Join(OperatingSystem.IsWindows() ? ";" : ":", Shaders(mode, gpuClass));

    public static string Name(string? mode) => Options.FirstOrDefault(o => o.Value == mode)?.Label ?? "Off";
}
