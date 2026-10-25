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
    /// <summary>TensorRT model: "auto" picks the best one this PC sustains (see tune.cmd), or e.g. "rife-v4.26".</summary>
    [JsonPropertyName("trt_model")]      public string TrtModel { get; set; } = "auto";
    /// <summary>"entry" on GTX-class cards: the engine starts from its lightest model until tuned.</summary>
    /// <summary>Exports only: how many frames the source has, so the engine knows where it ends.</summary>
    [JsonPropertyName("source_frames"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceFrames { get; set; }
    /// <summary>TensorRT: run the AI on a smaller copy ("1", "0.75", "0.5") or "auto" from tuning.</summary>
    [JsonPropertyName("ai_scale")]       public string AiScale { get; set; } = "auto";
    [JsonPropertyName("gpu_class")]      public string GpuClass { get; set; } = "";
    /// <summary>Set by the player while frames drop: use this many models lighter. Reset for every video.</summary>
    [JsonPropertyName("quality_step")]   public int QualityStep { get; set; }
    [JsonPropertyName("rife_threads")]   public int RifeThreads { get; set; } = 4;
    [JsonPropertyName("scene_detect")]   public bool SceneDetect { get; set; } = true;
    /// <summary>Blend across frames the video repeats (common in sports and TV uploads).</summary>
    [JsonPropertyName("dedup")]          public bool Dedup { get; set; } = true;
    [JsonPropertyName("mv_quality")]     public string MvQuality { get; set; } = "balanced"; // fast | balanced | quality
    [JsonPropertyName("demo")]           public bool Demo { get; set; }
    /// <summary>"side": the middle of the picture twice, original then smoothed. "split": left and right halves.</summary>
    [JsonPropertyName("demo_mode")]      public string DemoMode { get; set; } = "side";
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
    /// <summary>Which version of the GPU-based defaults these settings were made with.</summary>
    public int DefaultsVersion { get; set; }
    public const int CurrentDefaults = 3;
    /// <summary>"" (display-resample, from mpv.conf: frames locked to the screen's refresh) or "audio"
    /// (frames shown by their timestamps), for screens whose refresh timing can't be trusted.</summary>
    public string VideoSync { get; set; } = "";
    /// <summary>The "tune for this PC" card on Home was closed.</summary>
    public bool TunePromptDismissed { get; set; }

    public Profile Active =>
        Profiles.FirstOrDefault(p => p.Name == ActiveProfile) ?? Profiles.FirstOrDefault() ?? new Profile();

    public static AppSettings CreateDefaults(GpuInfo? gpu = null)
    {
        var s = new AppSettings { DefaultsVersion = CurrentDefaults };
        s.ApplyGpuDefaults(gpu);
        return s;
    }

    /// <summary>Profiles that suit the graphics card. Other settings are left alone.</summary>
    public void ApplyGpuDefaults(GpuInfo? gpu)
    {
        var tier = gpu?.Tier ?? GpuTier.Unknown;
        // TensorRT on cards that have it; it falls back to Vulkan RIFE if TensorRT isn't installed
        var rife = gpu?.SupportsTensorRt == true ? "rife_trt" : "rife";
        const string lite = "rife-v4.22_lite_ensembleFalse";
        Profiles.Clear();
        if (tier == GpuTier.Entry)
        {
            // A GTX 1650 makes about 40 light-model frames a second at 1080p: enough for 60 fps
            // from film, not for 144 Hz. So 60 fps and the light model, and MVTools as the safe choice.
            Profiles.Add(new Profile { Name = "Balanced",        Engine = new EngineSettings { Engine = rife, TargetText = "60", RifeModel = lite, RifeThreads = 2 } });
            Profiles.Add(new Profile { Name = "Max quality",     Engine = new EngineSettings { Engine = rife, TargetText = "60", RifeThreads = 3 } });
            Profiles.Add(new Profile { Name = "Light (any GPU)", Engine = new EngineSettings { Engine = "mvtools", MvQuality = "balanced", TargetText = "60" } });
            Profiles.Add(new Profile { Name = "Anime",           Engine = new EngineSettings { Engine = rife, TargetText = "x2", SceneDetect = true, RifeModel = lite, RifeThreads = 2 } });
        }
        else
        {
            Profiles.Add(new Profile { Name = "Balanced",        Engine = new EngineSettings { Engine = rife, TargetText = "display" } });
            Profiles.Add(new Profile { Name = "Max quality",     Engine = new EngineSettings { Engine = rife, TargetText = "display", RifeThreads = 6 } });
            Profiles.Add(new Profile { Name = "Light (any GPU)", Engine = new EngineSettings { Engine = "mvtools", MvQuality = "fast", TargetText = "60" } });
            Profiles.Add(new Profile { Name = "Anime",           Engine = new EngineSettings { Engine = rife, TargetText = "x2", SceneDetect = true, RifeModel = lite } });
        }
        ActiveProfile = tier == GpuTier.Integrated ? "Light (any GPU)" : "Balanced";
        if (gpu?.IsHybrid == true) VideoSync = "audio";
        MarkGpu(gpu);
        DefaultsVersion = CurrentDefaults;
    }

    /// <summary>Tells every profile's engine what class of card it runs on.</summary>
    public void MarkGpu(GpuInfo? gpu)
    {
        var cls = gpu?.Tier switch { GpuTier.Entry => "entry", GpuTier.Integrated => "integrated", _ => "" };
        foreach (var p in Profiles) p.Engine.GpuClass = cls;
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
