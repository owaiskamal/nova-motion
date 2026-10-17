// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.IO.Pipes;
using System.Text;

namespace NovaMotion.Core;

/// <summary>
/// novamotion:// links let the browser extension (or any app) hand a video to NOVA Motion:
///   novamotion://play?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3D...&t=93
/// </summary>
public static class NovaMotionLink
{
    public const string Scheme = "novamotion";

    public static string Create(string videoUrl, double? startSeconds = null)
    {
        var link = $"{Scheme}://play?url={Uri.EscapeDataString(videoUrl)}";
        if (startSeconds is > 0) link += $"&t={(int)startSeconds.Value}";
        return link;
    }

    /// <summary>Returns the target URL/path and optional start time, or null if the link is not ours / unsafe.</summary>
    public static (string Target, int? Start)? Parse(string link)
    {
        if (!link.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase)) return null;
        var q = link.IndexOf('?');
        if (q < 0) return null;

        string? url = null; int? start = null;
        foreach (var part in link[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var key = part[..eq];
            var value = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            if (key == "url") url = value;
            else if (key == "t" && int.TryParse(value, out var t) && t >= 0) start = t;
        }

        // Only web URLs come in through links. A link must never be able to make mpv open
        // local files, devices or mpv-specific protocols (e.g. "memory://", "lavf://").
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        return (uri.AbsoluteUri, start);
    }

    /// <summary>Registers novamotion:// for the current user (HKCU, no admin needed).</summary>
    public static void RegisterProtocol(string exePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
        root.SetValue("", "URL:NOVA Motion");
        root.SetValue("URL Protocol", "");
        using var icon = root.CreateSubKey("DefaultIcon");
        icon.SetValue("", $"\"{exePath}\",0");
        using var cmd = root.CreateSubKey(@"shell\open\command");
        cmd.SetValue("", $"\"{exePath}\" \"%1\"");
    }
}

/// <summary>
/// Keeps one NovaMotion app running: a second launch (e.g. from a novamotion:// link)
/// forwards its argument to the first instance and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly string _pipeName;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    public bool IsFirst { get; }
    public event Action<string>? ArgumentReceived;

    public SingleInstance(string id = "NovaMotion.App")
    {
        _pipeName = id + "." + Environment.UserName;
        _mutex = new Mutex(true, @"Local\" + _pipeName, out var created);
        IsFirst = created;
        if (IsFirst) _ = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var arg = await reader.ReadToEndAsync(_cts.Token);
                if (!string.IsNullOrWhiteSpace(arg)) ArgumentReceived?.Invoke(arg.Trim());
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { /* client vanished; keep listening */ }
        }
    }

    public bool Forward(string argument)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(argument);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return false; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } }
        _mutex.Dispose();
    }
}
