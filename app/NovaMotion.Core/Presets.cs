// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Text.Json.Serialization;

namespace NovaMotion.Core;

/// <summary>Everything a preset sets for a video export: the AI look, the frame rate and how it's saved.</summary>
public sealed record VideoRecipe
{
    [JsonPropertyName("enhance")] public EnhanceSettings Enhance { get; init; } = new() { Preset = "off" };
    /// <summary>"60", "120", "x2", or "source" (no smoothing).</summary>
    [JsonPropertyName("target")] public string Target { get; init; } = "60";
    [JsonPropertyName("quality")] public ExportQuality Quality { get; init; } = ExportQuality.Best;
    [JsonPropertyName("gpu_encoder")] public bool HardwareEncoder { get; init; } = true;
    /// <summary>Anime: drawings kept as drawn, only camera moves smoothed (no morphing faces).</summary>
    [JsonPropertyName("drawn")] public bool Drawn { get; init; }

    /// <summary>Same choices, ignoring bookkeeping (Enhance's tuning marker).</summary>
    public bool SameAs(VideoRecipe other) =>
        Target == other.Target && Quality == other.Quality && HardwareEncoder == other.HardwareEncoder && Drawn == other.Drawn
        && SameLook(Enhance, other.Enhance);

    internal static bool SameLook(EnhanceSettings a, EnhanceSettings b) =>
        a.Preset == b.Preset && a.Height == b.Height && a.Clean == b.Clean && a.FixCompression == b.FixCompression
        && a.Strength == b.Strength && Math.Abs(a.Detail - b.Detail) < 0.005 && Math.Abs(a.Grain - b.Grain) < 0.005;
}

/// <summary>A named set of choices: built in, or saved by the person ("Save as a new preset").</summary>
public sealed record VideoPreset
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("recipe")] public VideoRecipe Recipe { get; init; } = new();
    [JsonIgnore] public bool BuiltIn { get; init; }
}

/// <summary>The same for photos.</summary>
public sealed record PhotoPreset
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("options")] public PhotoOptions Options { get; init; } = new();
    [JsonIgnore] public bool BuiltIn { get; init; }
}

/// <summary>
/// The presets Enhance starts with, and the person's own. Choosing one sets every option; changing an
/// option afterwards shows the preset as edited, with "Save as a new preset" and "Back to it as it came".
/// </summary>
public static class Presets
{
    private static EnhanceSettings Look(string kind, int height, string clean, string strength, bool fix = false, double detail = 0, double grain = 0) =>
        new() { Preset = kind, Height = height, Clean = clean, Strength = strength, FixCompression = fix, Detail = detail, Grain = grain,
                Tuning = EnhanceSettings.CurrentTuning };

    public static readonly IReadOnlyList<VideoPreset> Video = new[]
    {
        new VideoPreset { Id = "anime", Name = "Anime episode", Description = "Clean lines, 60 fps, same size", BuiltIn = true,
                          Recipe = new() { Enhance = Look("anime", 0, "light", "balanced", grain: 0.3), Drawn = true } },
        new VideoPreset { Id = "film", Name = "Film & live action", Description = "Natural look, keeps the grain", BuiltIn = true,
                          Recipe = new() { Enhance = Look("film", 0, "off", "natural") } },
        new VideoPreset { Id = "old", Name = "Old or low-res", Description = "Enlarge to 1440p, strong clean-up", BuiltIn = true,
                          Recipe = new() { Enhance = Look("film", 1440, "strong", "full") } },
        new VideoPreset { Id = "download", Name = "Blocky download", Description = "Fix compression first; slow", BuiltIn = true,
                          Recipe = new() { Enhance = Look("film", 0, "light", "balanced", fix: true) } },
        new VideoPreset { Id = "remaster", Name = "4K remaster", Description = "For big screens; slow on GTX cards", BuiltIn = true,
                          Recipe = new() { Enhance = Look("film", 2160, "light", "balanced") } },
        new VideoPreset { Id = "smooth", Name = "Just smooth", Description = "No AI clean-up, fastest", BuiltIn = true,
                          Recipe = new() { Enhance = Look("off", 0, "light", "balanced") } },
    };

    public static readonly IReadOnlyList<PhotoPreset> Photo = new[]
    {
        new PhotoPreset { Id = "phone", Name = "Phone photo", Description = "Cleaner and crisper, same size", BuiltIn = true,
                          Options = new PhotoOptions("film", 1, "light", MaxDetail: true, Restore: NovaMotion.Core.Photo.RestoreAmount) },
        new PhotoPreset { Id = "print", Name = "Enlarge to print", Description = "2×, Max detail", BuiltIn = true,
                          Options = new PhotoOptions("film", 2, "light", MaxDetail: true, Restore: NovaMotion.Core.Photo.RestoreAmount) },
        new PhotoPreset { Id = "small", Name = "Small or old picture", Description = "4×, Max detail", BuiltIn = true,
                          Options = new PhotoOptions("film", 4, "strong", MaxDetail: true, Restore: NovaMotion.Core.Photo.RestoreAmount) },
        new PhotoPreset { Id = "chat", Name = "Saved from a chat", Description = "Fix JPEG blocks, 2×", BuiltIn = true,
                          Options = new PhotoOptions("film", 2, "light", MaxDetail: false, FixCompression: true, Restore: NovaMotion.Core.Photo.RestoreAmount) },
        new PhotoPreset { Id = "art", Name = "Anime & art", Description = "Clean lines, 4×", BuiltIn = true,
                          Options = new PhotoOptions("anime", 4, "light", MaxDetail: true) },
    };

    public static IEnumerable<VideoPreset> AllVideo(AppSettings s) => Video.Concat(s.MyVideoPresets);
    public static IEnumerable<PhotoPreset> AllPhoto(AppSettings s) => Photo.Concat(s.MyPhotoPresets);

    public static VideoPreset? FindVideo(AppSettings s, string? id) => AllVideo(s).FirstOrDefault(p => p.Id == id);
    public static PhotoPreset? FindPhoto(AppSettings s, string? id) => AllPhoto(s).FirstOrDefault(p => p.Id == id);

    /// <summary>The choices differ from the preset's: shown as "Edited".</summary>
    public static bool IsEdited(VideoPreset p, VideoRecipe current) => !p.Recipe.SameAs(current);
    public static bool IsEdited(PhotoPreset p, PhotoOptions current) => p.Options != current;

    /// <summary>Saves the current choices as the person's own preset; a name already used gets a number.</summary>
    public static VideoPreset SaveVideo(AppSettings s, string name, string description, VideoRecipe recipe)
    {
        var p = new VideoPreset { Id = NewId("my-video", s.MyVideoPresets.Select(x => x.Id)), Name = UniqueName(name, AllVideo(s).Select(x => x.Name)),
                                  Description = description, Recipe = recipe };
        s.MyVideoPresets.Add(p);
        return p;
    }

    public static PhotoPreset SavePhoto(AppSettings s, string name, string description, PhotoOptions options)
    {
        var p = new PhotoPreset { Id = NewId("my-photo", s.MyPhotoPresets.Select(x => x.Id)), Name = UniqueName(name, AllPhoto(s).Select(x => x.Name)),
                                  Description = description, Options = options };
        s.MyPhotoPresets.Add(p);
        return p;
    }

    /// <summary>Only the person's own presets can be removed or renamed; built-in ones stay.</summary>
    public static bool Remove(AppSettings s, string id) =>
        s.MyVideoPresets.RemoveAll(p => p.Id == id) + s.MyPhotoPresets.RemoveAll(p => p.Id == id) > 0;

    public static bool Rename(AppSettings s, string id, string name)
    {
        var name2 = name.Trim();
        if (name2.Length == 0) return false;
        var v = s.MyVideoPresets.FindIndex(p => p.Id == id);
        if (v >= 0) { s.MyVideoPresets[v] = s.MyVideoPresets[v] with { Name = UniqueName(name2, AllVideo(s).Where(p => p.Id != id).Select(p => p.Name)) }; return true; }
        var ph = s.MyPhotoPresets.FindIndex(p => p.Id == id);
        if (ph >= 0) { s.MyPhotoPresets[ph] = s.MyPhotoPresets[ph] with { Name = UniqueName(name2, AllPhoto(s).Where(p => p.Id != id).Select(p => p.Name)) }; return true; }
        return false;
    }

    private static string NewId(string prefix, IEnumerable<string> taken)
    {
        var set = taken.ToHashSet();
        for (var i = 1; ; i++) if (!set.Contains($"{prefix}-{i}")) return $"{prefix}-{i}";
    }

    private static string UniqueName(string name, IEnumerable<string> taken)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "My preset" : name.Trim();
        var set = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(baseName)) return baseName;
        for (var i = 2; ; i++) if (!set.Contains($"{baseName} {i}")) return $"{baseName} {i}";
    }
}
