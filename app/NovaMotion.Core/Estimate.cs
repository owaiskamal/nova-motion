// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
namespace NovaMotion.Core;

/// <summary>
/// How long an export or a photo will take on this PC, before it starts: a 22-minute anime episode at
/// the heaviest settings ran 40 hours on a GTX 1650, and nobody should find that out from a progress bar.
///
/// The work is counted the way the engine does it (enhance.py): which model, at what size, in how many
/// 640 x 384 tiles, for how many frames (after smoothing, since enlarging comes after RIFE), plus Fix
/// compression on the original frames. Each model family has a first-guess time per tile by graphics
/// class; every finished export then corrects that family's guess for this PC (Calibrate).
/// </summary>
public static class Estimate
{
    public static readonly (int W, int H) Tile = (640, 384);
    public const int Overlap = 16;
    public static readonly (int W, int H) DpirTile = (384, 256);
    public const int FixCompressionMaxHeight = 1440;

    /// <summary>Model families, by how heavy one tile is.</summary>
    public const string Compact = "compact", Cugan = "cugan", Rrdb = "rrdb", Rrdb6 = "rrdb6", Dpir = "dpir", Rife = "rife", Encode = "encode";

    /// <summary>Seconds a tile (RIFE and encoding: a 1080p frame), first guesses by graphics class: from GTX
    /// 1650 runs (a 1500 x 2000 photo through x4plus in 30 s; a 22-minute episode with Real-CUGAN and Fix
    /// compression estimated at 40 h), and about 5x faster on RTX cards. Calibrate replaces them.</summary>
    private static readonly Dictionary<string, Dictionary<string, double>> FirstGuess = new()
    {
        ["entry"] = new() { [Compact] = 0.07, [Cugan] = 0.30, [Rrdb6] = 0.45, [Rrdb] = 1.5, [Dpir] = 0.07, [Rife] = 0.025, [Encode] = 0.006 },
        ["integrated"] = new() { [Compact] = 0.25, [Cugan] = 1.2, [Rrdb6] = 1.8, [Rrdb] = 6.0, [Dpir] = 0.5, [Rife] = 0.08, [Encode] = 0.01 },
        [""] = new() { [Compact] = 0.014, [Cugan] = 0.06, [Rrdb6] = 0.09, [Rrdb] = 0.3, [Dpir] = 0.025, [Rife] = 0.006, [Encode] = 0.004 },
    };

    public static double SecondsPerTile(string family, string? gpuClass, IReadOnlyDictionary<string, double>? calibration = null)
    {
        var table = FirstGuess.TryGetValue(gpuClass ?? "", out var t) ? t : FirstGuess[""];
        var k = calibration is not null && calibration.TryGetValue(family, out var c) && c > 0 ? c : 1.0;
        return table[family] * k;
    }

    /// <summary>The engine's output_size: the shape at the chosen height, never smaller, even numbers.</summary>
    public static (int W, int H) OutputSize(int w, int h, int height) =>
        height <= 0 || height <= h ? (w, h) : ((int)Math.Round(w * (double)height / h / 2) * 2, height / 2 * 2);

    /// <summary>The engine's ai_input_size: the video's own size, never more than half the finished one.</summary>
    public static (int W, int H) AiInputSize(int w, int h, int outW, int outH)
    {
        if (h * 2 <= outH * 1.01) return (w, h);
        var k = outH / 2.0 / h;
        return (Math.Max(2, (int)Math.Round(w * k / 2) * 2), Math.Max(2, (int)Math.Round(h * k / 2) * 2));
    }

    /// <summary>Tiles a picture is cut into (smaller ones are padded to one tile).</summary>
    public static int Tiles(int w, int h, (int W, int H) tile, int overlap = Overlap)
    {
        static int Along(int size, int t, int ov) => size <= t ? 1 : (int)Math.Ceiling((size - 2.0 * ov) / (t - 2 * ov));
        return Along(w, tile.W, overlap) * Along(h, tile.H, overlap);
    }

    /// <summary>The family of the model the engine picks (enhance.model_plan).</summary>
    public static string ModelFamily(string preset, int inH, int outH, bool maxDetail)
    {
        if (maxDetail) return preset == "anime" ? Rrdb6 : Rrdb;
        if (preset == "film") return Compact;
        return outH / (double)inH > 2.5 ? Compact : Cugan;
    }

    /// <summary>The frames an export makes from a source: the target frame rate, never more than asked.</summary>
    public static double OutputFps(double sourceFps, string target) => target switch
    {
        "x2" => sourceFps * 2,
        "source" or "off" or "" => sourceFps,
        _ when double.TryParse(target, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) && t > sourceFps => t,
        _ => sourceFps,
    };

    /// <summary>The work by family, in seconds at first-guess speed, for a video export.</summary>
    public static Dictionary<string, double> VideoWork(int w, int h, double fps, double seconds, VideoRecipe r, string? gpuClass,
                                                       IReadOnlyDictionary<string, double>? calibration = null)
    {
        var work = new Dictionary<string, double>();
        void Add(string family, double tiles) => work[family] = work.GetValueOrDefault(family) + tiles * SecondsPerTile(family, gpuClass, calibration);
        var outFps = OutputFps(fps, r.Target);
        var srcFrames = Math.Max(1, fps * seconds);
        var outFrames = Math.Max(1, outFps * seconds);
        var hd = w * (double)h / (1920 * 1080);
        if (outFps > fps * 1.01) Add(Rife, outFrames * Math.Max(0.25, hd));
        var (ow, oh) = (w, h);
        if (r.Enhance.IsOn)
        {
            if (r.Enhance.FixCompression && h <= FixCompressionMaxHeight) Add(Dpir, srcFrames * Tiles(w, h, DpirTile));
            (ow, oh) = OutputSize(w, h, r.Enhance.Height);
            var (iw, ih) = AiInputSize(w, h, ow, oh);
            Add(ModelFamily(r.Enhance.Preset, ih, oh, false), outFrames * Tiles(iw, ih, Tile));
        }
        Add(Encode, outFrames * Math.Max(0.25, ow * (double)oh / (1920 * 1080)));
        return work;
    }

    public static TimeSpan Video(int w, int h, double fps, double seconds, VideoRecipe r, string? gpuClass,
                                 IReadOnlyDictionary<string, double>? calibration = null) =>
        TimeSpan.FromSeconds(VideoWork(w, h, fps, seconds, r, gpuClass, calibration).Values.Sum());

    /// <summary>A photo: the whole picture through the model (up to 12 MP), then saved.</summary>
    public static TimeSpan Photo(int w, int h, PhotoOptions o, string? gpuClass, IReadOnlyDictionary<string, double>? calibration = null)
    {
        var scale = Math.Max(1.0, Math.Min(o.Scale, 8192.0 / Math.Max(w, h)));
        var (iw, ih) = Math.Max(w, h) * 4 <= 16384 ? (w, h) : AiInputSize(w, h, (int)(w * scale), (int)(h * scale));
        var s = Tiles(iw, ih, Tile) * SecondsPerTile(ModelFamily(o.Preset, ih, (int)(h * scale), o.MaxDetail), gpuClass, calibration);
        if (o.FixCompression) s += Tiles(w, h, DpirTile) * SecondsPerTile(Dpir, gpuClass, calibration);
        return TimeSpan.FromSeconds(s + 3);                 // reading, starting the models, saving
    }

    /// <summary>
    /// After a finished export: how long it really took against the estimate corrects the families it
    /// used, in proportion to their share of the work, gently (a third of the way each time) so one odd
    /// run doesn't swing the next estimate.
    /// </summary>
    public static void Calibrate(Dictionary<string, double> calibration, IReadOnlyDictionary<string, double> estimatedWork, TimeSpan actual)
    {
        var total = estimatedWork.Values.Sum();
        if (total < 5 || actual.TotalSeconds < 5) return;
        var ratio = Math.Clamp(actual.TotalSeconds / total, 0.1, 10);
        foreach (var (family, seconds) in estimatedWork)
        {
            var share = seconds / total;
            if (share < 0.1) continue;
            var old = calibration.GetValueOrDefault(family, 1.0);
            var target = old * Math.Pow(ratio, share);      // the bigger its share, the more it explains the miss
            calibration[family] = Math.Round(old + (target - old) / 3, 4);
        }
    }

    /// <summary>"about 40 min", "about 1 h 10 min", "about 2 days"; under a minute: "under a minute".</summary>
    public static string Words(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return "under a minute";
        if (t.TotalMinutes < 60) return $"about {Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";
        if (t.TotalHours < 48)
        {
            var h = (int)t.TotalHours;
            var m = (int)Math.Round((t.TotalMinutes - h * 60) / 5) * 5;
            if (m == 60) { h++; m = 0; }
            return m == 0 ? $"about {h} h" : $"about {h} h {m} min";
        }
        return $"about {Math.Round(t.TotalDays, MidpointRounding.AwayFromZero)} days";
    }
}
