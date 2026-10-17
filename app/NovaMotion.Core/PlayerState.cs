// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Globalization;
using System.Text.Json.Nodes;

namespace NovaMotion.Core;

/// <summary>An audio or subtitle track of the current file, from mpv's "track-list".</summary>
public sealed record TrackInfo(int Id, string Type, string? Title, string? Lang, string? Codec,
                               bool Selected, bool External, int Channels, bool Default)
{
    /// <summary>"English", "Director's commentary (English)", "Track 2"...</summary>
    public string Label
    {
        get
        {
            var lang = LanguageName(Lang);
            if (!string.IsNullOrWhiteSpace(Title))
                return lang is null || Title.Contains(lang, StringComparison.OrdinalIgnoreCase) ? Title! : $"{Title} ({lang})";
            return lang ?? $"Track {Id}";
        }
    }

    /// <summary>"AAC · 5.1", "SRT · external"...</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Codec)) parts.Add(CodecName(Codec!));
            if (Type == "audio" && Channels > 0)
                parts.Add(Channels switch { 1 => "Mono", 2 => "Stereo", 6 => "5.1", 8 => "7.1", _ => $"{Channels} ch" });
            if (External) parts.Add("external");
            return string.Join(" · ", parts);
        }
    }

    public static IReadOnlyList<TrackInfo> Parse(JsonNode? trackList)
    {
        var list = new List<TrackInfo>();
        if (trackList is not JsonArray arr) return list;
        foreach (var t in arr)
        {
            if (t is null) continue;
            list.Add(new TrackInfo(
                Id: Int(t["id"]),
                Type: Str(t["type"]) ?? "",
                Title: Str(t["title"]),
                Lang: Str(t["lang"]),
                Codec: Str(t["codec"]),
                Selected: Bool(t["selected"]),
                External: Bool(t["external"]),
                Channels: Int(t["demux-channel-count"]),
                Default: Bool(t["default"])));
        }
        return list;
    }

    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["eng"] = "English", ["ja"] = "Japanese", ["jpn"] = "Japanese",
        ["de"] = "German", ["ger"] = "German", ["deu"] = "German", ["fr"] = "French", ["fre"] = "French", ["fra"] = "French",
        ["es"] = "Spanish", ["spa"] = "Spanish", ["it"] = "Italian", ["ita"] = "Italian", ["pt"] = "Portuguese", ["por"] = "Portuguese",
        ["ru"] = "Russian", ["rus"] = "Russian", ["zh"] = "Chinese", ["chi"] = "Chinese", ["zho"] = "Chinese",
        ["ko"] = "Korean", ["kor"] = "Korean", ["ar"] = "Arabic", ["ara"] = "Arabic", ["hi"] = "Hindi", ["hin"] = "Hindi",
        ["ur"] = "Urdu", ["urd"] = "Urdu", ["tr"] = "Turkish", ["tur"] = "Turkish", ["nl"] = "Dutch", ["dut"] = "Dutch", ["nld"] = "Dutch",
        ["pl"] = "Polish", ["pol"] = "Polish", ["sv"] = "Swedish", ["swe"] = "Swedish", ["und"] = null!,
    };

    public static string? LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        if (Languages.TryGetValue(code, out var name)) return name;
        try
        {
            var c = CultureInfo.GetCultureInfo(code);
            return c.EnglishName.StartsWith("Unknown", StringComparison.Ordinal) ? code : c.EnglishName;
        }
        catch (CultureNotFoundException) { return code; }
    }

    private static string CodecName(string codec) => codec.ToLowerInvariant() switch
    {
        "subrip" or "srt" => "SRT",
        "ass" or "ssa" => "ASS",
        "hdmv_pgs_subtitle" or "pgssub" => "PGS",
        "dvd_subtitle" => "VobSub",
        "webvtt" => "WebVTT",
        "eac3" => "E-AC-3",
        "truehd" => "TrueHD",
        "dts" => "DTS",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        "flac" => "FLAC",
        _ => codec.ToUpperInvariant(),
    };

    internal static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    internal static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) && b;
    internal static int Int(JsonNode? n) =>
        n is JsonValue v ? (v.TryGetValue(out int i) ? i : v.TryGetValue(out double d) ? (int)d : 0) : 0;
    internal static double Dbl(JsonNode? n) =>
        n is JsonValue v ? (v.TryGetValue(out double d) ? d : v.TryGetValue(out int i) ? i : 0) : 0;
}

/// <summary>
/// Live picture of the player, kept up to date from mpv "property-change" events
/// (no polling). One instance per app; Apply() is called for every change.
/// </summary>
public sealed class PlayerState
{
    /// <summary>Properties MpvPlayer asks mpv to report.</summary>
    public static readonly IReadOnlyList<string> Observed = new[]
    {
        "time-pos", "duration", "pause", "volume", "mute", "speed", "media-title", "path",
        "container-fps", "estimated-vf-fps", "display-fps", "vf", "track-list", "chapter-list",
        "demuxer-cache-time", "paused-for-cache", "seeking", "idle-active", "eof-reached",
        "video-params/w", "video-params/h", "hwdec-current", "core-idle",
    };

    public double Position { get; private set; }
    public double Duration { get; private set; }
    public bool Paused { get; private set; }
    public double Volume { get; private set; } = 100;
    public bool Muted { get; private set; }
    public double Speed { get; private set; } = 1;
    public string Title { get; private set; } = "";
    public string Path { get; private set; } = "";
    public double SourceFps { get; private set; }
    public double OutputFps { get; private set; }
    public double DisplayFps { get; private set; }
    /// <summary>The novamotion filter is in the chain and mpv has not disabled it.</summary>
    public bool EngineInChain { get; private set; }
    public double CachedUntil { get; private set; }
    public bool Buffering { get; private set; }
    public bool Seeking { get; private set; }
    public bool Idle { get; private set; } = true;
    public bool Ended { get; private set; }
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }
    public string HwDec { get; private set; } = "";
    public IReadOnlyList<TrackInfo> Tracks { get; private set; } = Array.Empty<TrackInfo>();
    public IReadOnlyList<(double Time, string Title)> Chapters { get; private set; } = Array.Empty<(double, string)>();

    public IEnumerable<TrackInfo> AudioTracks => Tracks.Where(t => t.Type == "audio");
    public IEnumerable<TrackInfo> SubtitleTracks => Tracks.Where(t => t.Type == "sub");
    public bool HasVideo => VideoWidth > 0;

    /// <summary>Applies one property change. Returns false for properties it doesn't track.</summary>
    public bool Apply(string name, JsonNode? data)
    {
        switch (name)
        {
            case "time-pos": Position = TrackInfo.Dbl(data); break;
            case "duration": Duration = TrackInfo.Dbl(data); break;
            case "pause": Paused = TrackInfo.Bool(data); break;
            case "volume": Volume = TrackInfo.Dbl(data); break;
            case "mute": Muted = TrackInfo.Bool(data); break;
            case "speed": Speed = data is null ? 1 : TrackInfo.Dbl(data); break;
            case "media-title": Title = TrackInfo.Str(data) ?? ""; break;
            case "path": Path = TrackInfo.Str(data) ?? ""; break;
            case "container-fps": SourceFps = TrackInfo.Dbl(data); break;
            case "estimated-vf-fps": OutputFps = TrackInfo.Dbl(data); break;
            case "display-fps": DisplayFps = TrackInfo.Dbl(data); break;
            case "vf":
                // mpv leaves a failed filter in the list with "enabled": false
                EngineInChain = data is JsonArray arr && arr.Any(f =>
                    TrackInfo.Str(f?["label"]) == "novamotion" && (f?["enabled"] is not JsonValue en || !en.TryGetValue(out bool e) || e));
                break;
            case "track-list": Tracks = TrackInfo.Parse(data); break;
            case "chapter-list":
                Chapters = data is JsonArray ch
                    ? ch.Where(c => c is not null).Select(c => (TrackInfo.Dbl(c!["time"]), TrackInfo.Str(c!["title"]) ?? "")).ToList()
                    : Array.Empty<(double, string)>();
                break;
            case "demuxer-cache-time": CachedUntil = TrackInfo.Dbl(data); break;
            case "paused-for-cache": Buffering = TrackInfo.Bool(data); break;
            case "seeking": Seeking = TrackInfo.Bool(data); break;
            case "idle-active":
                Idle = TrackInfo.Bool(data);
                if (Idle) ResetMedia();
                break;
            case "eof-reached": Ended = TrackInfo.Bool(data); break;
            case "video-params/w": VideoWidth = TrackInfo.Int(data); break;
            case "video-params/h": VideoHeight = TrackInfo.Int(data); break;
            case "hwdec-current": HwDec = TrackInfo.Str(data) ?? ""; break;
            case "core-idle": break;
            default: return false;
        }
        return true;
    }

    private void ResetMedia()
    {
        Position = Duration = SourceFps = OutputFps = CachedUntil = 0;
        Title = Path = "";
        Ended = Buffering = false;
        VideoWidth = VideoHeight = 0;
        Tracks = Array.Empty<TrackInfo>();
        Chapters = Array.Empty<(double, string)>();
    }
}

public static class TimeFormat
{
    /// <summary>4:05, 1:02:03. Negative/NaN show as 0:00.</summary>
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>"Today", "Yesterday", "3 days ago", "12 Oct".</summary>
    public static string Ago(DateTime whenUtc, DateTime nowUtc)
    {
        var a = whenUtc.ToLocalTime().Date; var b = nowUtc.ToLocalTime().Date;
        var days = (b - a).Days;
        return days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            < 7 => $"{days} days ago",
            _ => whenUtc.ToLocalTime().ToString(whenUtc.Year == nowUtc.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture),
        };
    }
}

/// <summary>Helpers for what the user opens: local files or web links.</summary>
public static class MediaSource
{
    public static bool IsOnline(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>"youtube.com", or the folder a local file is in.</summary>
    public static string Origin(string target)
    {
        if (IsOnline(target))
        {
            var host = new Uri(target).Host;
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host.StartsWith("m.") ? host[2..] : host;
        }
        try
        {
            var dir = System.IO.Path.GetDirectoryName(target.Replace('\\', System.IO.Path.DirectorySeparatorChar));
            return string.IsNullOrEmpty(dir) ? "" : System.IO.Path.GetFileName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : dir;
        }
        catch (ArgumentException) { return ""; }
    }

    /// <summary>A readable name before mpv reports the real title.</summary>
    public static string GuessTitle(string target)
    {
        if (IsOnline(target)) return Origin(target);
        var name = System.IO.Path.GetFileNameWithoutExtension(target.Replace('\\', '/').Split('/').Last());
        return string.IsNullOrWhiteSpace(name) ? target : name;
    }

    public static readonly string[] VideoExtensions =
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".webm", ".m2ts", ".mts", ".ts", ".wmv", ".flv", ".mpg", ".mpeg", ".3gp", ".ogv",
    };

    public static readonly string[] SubtitleExtensions = { ".srt", ".ass", ".ssa", ".vtt", ".sub", ".sup" };

    public static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsSubtitleFile(string path) =>
        SubtitleExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Text that looks like something we can play: a web link (pasted or dropped).</summary>
    public static string? AsLink(string? text)
    {
        var t = text?.Trim().Trim('"');
        if (string.IsNullOrEmpty(t) || t.Contains('\n') || t.Length > 2000) return null;
        return IsOnline(t) ? t : null;
    }
}
