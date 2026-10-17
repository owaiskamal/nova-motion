// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaMotion.Core;

public sealed record PlaybackStatus(string Title, double SourceFps, double OutputFps, double DisplayFps,
                                    bool EngineActive, bool Paused, double Position, double Duration);

/// <summary>
/// One mpv window driven over its JSON IPC pipe.
/// Protocol: newline-delimited JSON, {"command":[...], "request_id":n} -> {"request_id":n,"error":"success","data":...}
/// </summary>
public sealed class MpvPlayer : IAsyncDisposable
{
    private readonly RuntimeLayout _runtime;
    private readonly SettingsStore _store;
    private readonly string _pipeName = "novamotion-" + Guid.NewGuid().ToString("N")[..12];
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readerCts;
    private int _nextId;

    public event Action<string, JsonNode?>? EventReceived;  // mpv events: "file-loaded", "end-file", ...
    public event Action? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public MpvPlayer(RuntimeLayout runtime, SettingsStore store)
    {
        _runtime = runtime;
        _store = store;
    }

    /// <summary>Command-line for mpv. Kept separate so it can be unit tested.</summary>
    public IReadOnlyList<string> BuildArguments(string target, AppSettings settings)
    {
        var pipePath = OperatingSystem.IsWindows() ? $@"\\.\pipe\{_pipeName}" : Path.Combine(Path.GetTempPath(), _pipeName);
        var h = settings.OnlineMaxHeight;
        var args = new List<string>
        {
            $"--input-ipc-server={pipePath}",
            $"--script-opts=novamotion-vpy={_runtime.EngineScript},novamotion-config={_store.EngineConfigPath},ytdl_hook-ytdl_path={_runtime.YtDlpExe}",
            $"--ytdl-format=bestvideo[height<=?{h}][vcodec!*=av01]+bestaudio/bestvideo[height<=?{h}]+bestaudio/best",
            "--force-window=immediate",
            $"--log-file={Path.Combine(_store.Directory, "mpv.log")}",
            "--title=${media-title} - NOVA Motion",
        };
        var userConf = Path.Combine(_store.Directory, "mpv-user.conf");
        if (File.Exists(userConf)) args.Add($"--include={userConf}");
        args.Add("--");
        args.Add(target);
        return args;
    }

    public async Task StartAsync(string target, AppSettings settings, CancellationToken ct = default)
    {
        if (IsRunning) { await LoadAsync(target); return; }

        _store.WriteEngineConfig(settings.Active.Engine);
        var psi = new ProcessStartInfo(_runtime.MpvExe) { UseShellExecute = false, WorkingDirectory = _runtime.MpvDir };
        foreach (var a in BuildArguments(target, settings)) psi.ArgumentList.Add(a);
        psi.Environment["NOVAMOTION_ENGINE_CONFIG"] = _store.EngineConfigPath;

        _process = Process.Start(psi) ?? throw new InvalidOperationException("mpv did not start");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => { Exited?.Invoke(); _ = DisconnectAsync(); };

        await ConnectAsync(TimeSpan.FromSeconds(10), ct);
    }

    /// <summary>Connects to an existing IPC pipe (used by StartAsync and by tests with a fake mpv).</summary>
    public async Task ConnectAsync(TimeSpan timeout, CancellationToken ct = default, string? pipeName = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var pipe = new NamedPipeClientStream(".", pipeName ?? _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(500, ct);
                _pipe = pipe;
                break;
            }
            catch (TimeoutException)
            {
                await pipe.DisposeAsync();
                if (DateTime.UtcNow > deadline) throw new TimeoutException("mpv IPC did not come up");
                if (_process is { HasExited: true }) throw new InvalidOperationException("mpv exited during startup");
            }
        }

        _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _readerCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_pipe, _readerCts.Token));
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        try
        {
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); } catch (JsonException) { continue; }
                if (msg is null) continue;

                if (msg["request_id"] is JsonValue idv && idv.TryGetValue(out int id) && _pending.TryRemove(id, out var tcs))
                {
                    var err = msg["error"]?.GetValue<string>();
                    if (err is null or "success") tcs.TrySetResult(msg["data"]);
                    else tcs.TrySetException(new MpvException(err));
                }
                else if (msg["event"]?.GetValue<string>() is { } ev)
                {
                    EventReceived?.Invoke(ev, msg);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            foreach (var kv in _pending) kv.Value.TrySetCanceled();
            _pending.Clear();
        }
    }

    public async Task<JsonNode?> CommandAsync(params object?[] command)
    {
        if (_writer is null) throw new InvalidOperationException("Not connected to mpv");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?> { ["command"] = command, ["request_id"] = id });

        await _writeLock.WaitAsync();
        try { await _writer.WriteLineAsync(payload); }
        finally { _writeLock.Release(); }

        var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        if (done != tcs.Task) { _pending.TryRemove(id, out _); throw new TimeoutException($"mpv did not answer {command[0]}"); }
        return await tcs.Task;
    }

    public Task LoadAsync(string target) => CommandAsync("loadfile", target, "replace");

    /// <summary>Writes the new engine settings and tells the Lua script to rebuild the filter chain.</summary>
    public async Task ApplyAsync(EngineSettings engine)
    {
        _store.WriteEngineConfig(engine);
        if (IsRunning && _writer is not null) await CommandAsync("script-message", "novamotion-reload");
    }

    public async Task<PlaybackStatus?> GetStatusAsync()
    {
        if (!IsRunning || _writer is null) return null;
        async Task<JsonNode?> Prop(string name)
        {
            try { return await CommandAsync("get_property", name); }
            catch (MpvException) { return null; }   // "property unavailable" while loading
        }
        double D(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double d) ? d : 0;

        var vf = await Prop("vf");
        // mpv leaves a failed filter in the list with "enabled": false
        var engineActive = vf is JsonArray arr && arr.Any(f => f?["label"]?.GetValue<string>() == "novamotion"
                                                                && f?["enabled"]?.GetValue<bool>() != false);
        return new PlaybackStatus(
            Title: (await Prop("media-title"))?.GetValue<string>() ?? "",
            SourceFps: D(await Prop("container-fps")),
            OutputFps: D(await Prop("estimated-vf-fps")),
            DisplayFps: D(await Prop("display-fps")),
            EngineActive: engineActive,
            Paused: (await Prop("pause")) is JsonValue p && p.TryGetValue(out bool b) && b,
            Position: D(await Prop("time-pos")),
            Duration: D(await Prop("duration")));
    }

    private async Task DisconnectAsync()
    {
        _readerCts?.Cancel();
        if (_writer is not null) { try { await _writer.DisposeAsync(); } catch (IOException) { } }
        if (_pipe is not null) await _pipe.DisposeAsync();
        _writer = null; _pipe = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning) { try { await CommandAsync("quit"); } catch (Exception) { _process?.Kill(); } }
        await DisconnectAsync();
        _process?.Dispose();
    }
}

public sealed class MpvException(string error) : Exception($"mpv: {error}");
