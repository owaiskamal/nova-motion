// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace NovaMotion.Core;

/// <summary>
/// Seek-bar previews: a small picture of the video at the time under the mouse. A separate mpv run
/// grabs one frame (a fraction of a second from a local file); pictures are kept per time slot, so
/// moving back over the bar shows them at once. Local files only: a stream would be fetched again.
/// </summary>
public sealed class Thumbnails
{
    /// <summary>One picture per slot: 1/200 of the video, at least every 2 s.</summary>
    public const int Slots = 200;
    public const int Width = 320;

    private readonly string _mpv;
    private readonly string _dir;
    private readonly ConcurrentDictionary<(string, int), string> _done = new();
    private readonly SemaphoreSlim _one = new(1, 1);

    public Thumbnails(string mpvExe, string? folder = null)
    {
        _mpv = mpvExe;
        _dir = folder ?? Path.Combine(Path.GetTempPath(), "NOVA Studio thumbnails");
    }

    public static int SlotOf(double seconds, double duration) =>
        duration <= 0 ? 0 : (int)Math.Clamp(Math.Floor(seconds / SlotLength(duration)), 0, Slots);

    public static double SlotLength(double duration) => Math.Max(2, duration / Slots);

    /// <summary>The picture's file, or null for streams, failures and requests overtaken by newer ones.</summary>
    public async Task<string?> GetAsync(string media, double seconds, double duration, CancellationToken ct)
    {
        if (MediaSource.IsOnline(media) || !File.Exists(media) || !File.Exists(_mpv)) return null;
        var slot = SlotOf(seconds, duration);
        if (_done.TryGetValue((media, slot), out var hit) && File.Exists(hit)) return hit;
        await _one.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_done.TryGetValue((media, slot), out hit) && File.Exists(hit)) return hit;
            Directory.CreateDirectory(_dir);
            var file = Path.Combine(_dir, $"{Hash(media)}-{slot}.jpg");
            if (!File.Exists(file) && !await GrabAsync(media, (slot + 0.5) * SlotLength(duration), file, ct)) return null;
            _done[(media, slot)] = file;
            return file;
        }
        catch (OperationCanceledException) { return null; }
        finally { _one.Release(); }
    }

    private async Task<bool> GrabAsync(string media, double at, string file, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_mpv) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[]
                 {
                     "--no-config", "--player-operation-mode=cplayer", "--idle=no", "--no-audio", "--no-sub", "--hr-seek=no",
                     "--start=" + at.ToString("0.###", CultureInfo.InvariantCulture), "--frames=1",
                     $"--vf=scale={Width}:-2", "--o=" + file, "--of=image2", "--ovc=mjpeg", "--ovcopts=qscale=4", "--", media,
                 })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var drain = Task.WhenAll(p.StandardOutput.ReadToEndAsync(timeout.Token), p.StandardError.ReadToEndAsync(timeout.Token));
            try { await p.WaitForExitAsync(timeout.Token); await drain; }
            catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
            return File.Exists(file) && new FileInfo(file).Length > 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return false; }
    }

    private static string Hash(string s)
    {
        var h = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h)[..12].ToLowerInvariant();
    }
}
