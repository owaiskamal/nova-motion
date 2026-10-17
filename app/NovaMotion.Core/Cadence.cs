// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
namespace NovaMotion.Core;

/// <summary>
/// The rhythm of real and generated frames. Going from 25 to 60 fps, every 5 source frames
/// become 12 output frames; output frame n is a real frame only when n·5 is a multiple of 12.
/// </summary>
public static class Cadence
{
    /// <summary>Smallest p/q (p ≤ maxTicks) within 1.5 % of output/source.</summary>
    public static (int Out, int In)? Ratio(double sourceFps, double outputFps, int maxTicks = 16)
    {
        if (sourceFps <= 0 || outputFps <= sourceFps * 1.05) return null;
        var r = outputFps / sourceFps;
        for (var q = 1; q <= maxTicks; q++)
        {
            var p = (int)Math.Round(r * q);
            if (p > maxTicks) break;
            if (Math.Abs((double)p / q - r) / r < 0.015) return (p, q);
        }
        return null;
    }

    /// <summary>One repeat of the pattern: false = real frame, true = generated.</summary>
    public static IReadOnlyList<bool> Pattern(double sourceFps, double outputFps, int maxTicks = 16)
    {
        if (Ratio(sourceFps, outputFps, maxTicks) is not var (p, q))
            return Array.Empty<bool>();
        var ticks = new bool[p];
        for (var n = 0; n < p; n++) ticks[n] = n * q % p != 0;
        return ticks;
    }

    /// <summary>
    /// Share of displayed frames that are generated: 0.5 for 24 → 48, 11/12 for 25 → 60
    /// (only frames that fall exactly on a source frame are copied).
    /// </summary>
    public static double GeneratedShare(double sourceFps, double outputFps)
    {
        var p = Pattern(sourceFps, outputFps);
        return p.Count == 0 ? 0 : (double)p.Count(g => g) / p.Count;
    }
}
