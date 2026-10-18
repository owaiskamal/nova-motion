// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NovaMotion.Core;

/// <summary>Keeps yt-dlp current. Sites change often, and an old yt-dlp is the usual reason a link stops playing.</summary>
public static class YtDlp
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    public static bool Due(AppSettings s, DateTime nowUtc) =>
        s.AutoUpdateYtDlp && (s.YtDlpCheckedUtc is null || nowUtc - s.YtDlpCheckedUtc.Value >= CheckEvery);

    public enum Outcome { UpToDate, Updated, Failed }

    /// <summary>Reads yt-dlp's answer to -U. "Updated yt-dlp to stable@2026.10.10" → (Updated, "2026.10.10").</summary>
    public static (Outcome Result, string? Version) ParseUpdate(string output)
    {
        var updated = Regex.Match(output, @"Updated yt-dlp to\s+(?:\S+@)?(\S+)", RegexOptions.IgnoreCase);
        if (updated.Success) return (Outcome.Updated, updated.Groups[1].Value.TrimEnd('.'));
        var current = Regex.Match(output, @"yt-dlp is up to date \((?:\S+@)?([^)]+)\)", RegexOptions.IgnoreCase);
        if (current.Success) return (Outcome.UpToDate, current.Groups[1].Value);
        return (Outcome.Failed, null);
    }

    /// <summary>Runs "yt-dlp -U" in the background (no window). Never throws.</summary>
    public static async Task<(Outcome Result, string? Version)> UpdateAsync(string exe, CancellationToken ct = default)
    {
        if (!File.Exists(exe)) return (Outcome.Failed, null);
        try
        {
            var psi = new ProcessStartInfo(exe, "-U --no-colors")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync(ct);
            var error = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await p.WaitForExitAsync(timeout.Token);
            return ParseUpdate(await output + "\n" + await error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or OperationCanceledException or IOException)
        {
            return (Outcome.Failed, null);
        }
    }
}
