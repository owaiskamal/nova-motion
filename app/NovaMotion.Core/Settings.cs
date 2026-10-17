// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaMotion.Core;

/// <summary>Settings the VapourSynth engine reads (engine.json). Field names match novamotion.vpy.</summary>
public sealed class EngineSettings
{
    [JsonPropertyName("enabled")]        public bool Enabled { get; set; } = true;
    [JsonPropertyName("engine")]         public string Engine { get; set; } = "rife";        // rife_trt | rife | mvtools
    [JsonPropertyName("target")]         public JsonElement Target { get; set; } = JsonSerializer.SerializeToElement("display");
    [JsonPropertyName("max_source_fps")] public double MaxSourceFps { get; set; } = 31.0;
    [JsonPropertyName("rife_model")]     public string RifeModel { get; set; } = "rife-v4.6_ensembleFalse";
    [JsonPropertyName("rife_gpu")]       public int RifeGpu { get; set; }
    [JsonPropertyName("rife_threads")]   public int RifeThreads { get; set; } = 4;
    [JsonPropertyName("scene_detect")]   public bool SceneDetect { get; set; } = true;
    [JsonPropertyName("mv_quality")]     public string MvQuality { get; set; } = "balanced"; // fast | balanced | quality
    [JsonPropertyName("demo")]           public bool Demo { get; set; }
    [JsonPropertyName("rtx_vsr")]        public bool RtxVsr { get; set; }

    /// <summary>"display", "x2", "x3" or a number such as "60" / "120".</summary>
    [JsonIgnore]
    public string TargetText
    {
        get => Target.ValueKind == JsonValueKind.Number ? Target.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture) : Target.GetString() ?? "display";
        set => Target = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? JsonSerializer.SerializeToElement(n)
            : JsonSerializer.SerializeToElement(string.IsNullOrWhiteSpace(value) ? "display" : value.Trim().ToLowerInvariant());
    }

    public EngineSettings Clone() =>
        JsonSerializer.Deserialize<EngineSettings>(JsonSerializer.Serialize(this, Json.Options), Json.Options)!;
}

public sealed class Profile
{
    public string Name { get; set; } = "Default";
    public EngineSettings Engine { get; set; } = new();
}

public sealed class AppSettings
{
    public List<Profile> Profiles { get; set; } = new();
    public string ActiveProfile { get; set; } = "Balanced";
    /// <summary>Maximum height requested from YouTube & co (yt-dlp format).</summary>
    public int OnlineMaxHeight { get; set; } = 1080;
    public bool StartMinimizedToTray { get; set; }
    /// <summary>Closing the window keeps NOVA Motion in the tray (for browser links).</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Continue files and links from where you stopped.</summary>
    public bool ResumePlayback { get; set; } = true;
    /// <summary>Play the rest of the folder after a local file.</summary>
    public bool AutoQueueFolder { get; set; } = true;
    /// <summary>Keep yt-dlp current; sites change often and old versions stop working.</summary>
    public bool AutoUpdateYtDlp { get; set; } = true;
    public DateTime? YtDlpCheckedUtc { get; set; }
    public double Volume { get; set; } = 100;
    public bool FirstRunDone { get; set; }

    public Profile Active =>
        Profiles.FirstOrDefault(p => p.Name == ActiveProfile) ?? Profiles.FirstOrDefault() ?? new Profile();

    public static AppSettings CreateDefaults(GpuInfo? gpu = null)
    {
        var weak = gpu?.IsIntegratedOnly == true;
        // RTX cards get the TensorRT engine; it falls back to Vulkan RIFE if TensorRT isn't installed
        var rife = gpu?.IsNvidiaRtx == true ? "rife_trt" : "rife";
        var s = new AppSettings
        {
            ActiveProfile = weak ? "Light (any GPU)" : "Balanced",
            Profiles =
            {
                new Profile { Name = "Balanced",        Engine = new EngineSettings { Engine = rife, TargetText = "display" } },
                new Profile { Name = "Max quality",     Engine = new EngineSettings { Engine = rife, TargetText = "display", RifeThreads = 6, RtxVsr = gpu?.IsNvidiaRtx == true } },
                new Profile { Name = "Light (any GPU)", Engine = new EngineSettings { Engine = "mvtools", MvQuality = "fast", TargetText = "60" } },
                new Profile { Name = "Anime",           Engine = new EngineSettings { Engine = rife, TargetText = "x2", SceneDetect = true, RifeModel = "rife-v4.22_lite_ensembleFalse" } },
            },
        };
        return s;
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
