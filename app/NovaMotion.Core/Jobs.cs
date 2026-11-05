// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NovaMotion.Core;

/// <summary>One piece of AI preparation in progress: TensorRT building an engine for this graphics card.</summary>
public sealed record JobInfo(string Title, string Detail, int Step, int Steps, DateTime Started, double ExpectedSeconds)
{
    /// <summary>0..1 over all steps; the current step by time against how long it should take,
    /// held below its end until it really finishes.</summary>
    public double Fraction(DateTime now)
    {
        // Past 90% of the expected time it keeps creeping towards the end instead of stopping, so a
        // build that overruns its estimate still looks alive
        var r = ExpectedSeconds > 0 ? (now - Started).TotalSeconds / ExpectedSeconds : 0.5;
        var part = r <= 0.9 ? r : 0.9 + 0.095 * (1 - Math.Exp(-(r - 0.9) / 1.5));
        return Math.Clamp((Step - 1 + Math.Max(part, 0)) / Math.Max(Steps, 1), 0, 1);
    }

    /// <summary>"Model 3 of 9 · about 12 min left" or "… taking longer than usual".</summary>
    public string Status(DateTime now)
    {
        var spent = (now - Started).TotalSeconds;
        var left = Math.Max(ExpectedSeconds - spent, 0) + Math.Max(Steps - Step, 0) * ExpectedSeconds;
        var step = Steps > 1 ? $"{Step} of {Steps} · " : "";
        var time = spent > ExpectedSeconds * 1.2 ? $"taking longer than usual on this card ({Math.Round(spent / 60)} min so far), still working"
                 : left >= 90 ? $"about {Math.Round(left / 60)} min left"
                 : left >= 20 ? "about a minute left" : "nearly done";
        return $"{step}{Detail} · {time}";
    }
}

/// <summary>Reads runtime\jobs: the progress files the engine's builders write (engine/jobs.py).</summary>
public static class Jobs
{
    public static string Folder(RuntimeLayout rt) => Path.Combine(rt.Root, "jobs");

    /// <summary>Jobs in progress, oldest first. A file whose process has gone is stale and removed.</summary>
    public static IReadOnlyList<JobInfo> Read(RuntimeLayout? rt, Func<int, bool>? alive = null)
    {
        if (rt is null || !Directory.Exists(Folder(rt))) return Array.Empty<JobInfo>();
        alive ??= IsAlive;
        var list = new List<JobInfo>();
        foreach (var file in Directory.GetFiles(Folder(rt), "*.json"))
        {
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(file));
                if (j is null) continue;
                if (TrackInfo.Int(j["pid"]) is var pid && pid > 0 && !alive(pid))
                {
                    try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    continue;
                }
                var started = DateTimeOffset.FromUnixTimeMilliseconds((long)(TrackInfo.Dbl(j["started"]) * 1000)).UtcDateTime;
                list.Add(new JobInfo(TrackInfo.Str(j["title"]) ?? "Preparing AI", TrackInfo.Str(j["detail"]) ?? "",
                                     Math.Max(1, TrackInfo.Int(j["step"])), Math.Max(1, TrackInfo.Int(j["steps"])),
                                     started, TrackInfo.Dbl(j["expected_s"])));
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException) { }
        }
        return list.OrderBy(j => j.Started).ToList();
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
