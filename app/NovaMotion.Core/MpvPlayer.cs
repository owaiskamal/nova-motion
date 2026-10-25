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

/// <summary>What the app needs from a player process. MpvPlayer implements it; tests use a fake.</summary>
public interface IPlayerBackend : IAsyncDisposable
{
    bool IsRunning { get; }
    event Action<string, JsonNode?>? EventReceived;
    event Action<string, JsonNode?>? PropertyChanged;
    event Action? Exited;
    Task StartAsync(string? target, AppSettings settings, CancellationToken ct = default);
    Task<JsonNode?> CommandAsync(params object?[] command);
    Task ApplyAsync(EngineSettings engine);
}

/// <summary>
/// One mpv window driven over its JSON IPC pipe.
/// Protocol: newline-delimited JSON, {"command":[...], "request_id":n} -> {"request_id":n,"error":"success","data":...}
/// </summary>
public sealed class MpvPlayer : IPlayerBackend
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
    private int _nextObserveId;

    public event Action<string, JsonNode?>? EventReceived;  // mpv events: "file-loaded", "end-file", ...
    /// <summary>Values of observed properties (see <see cref="PlayerState.Observed"/>) as they change.</summary>
    public event Action<string, JsonNode?>? PropertyChanged;
    public event Action? Exited;

    public bool IsRunning => _process is { HasExited: false };
    public int? ExitCode => _process is { HasExited: true } p ? p.ExitCode : null;

    /// <summary>The IPC pipe path mpv is told to listen on.</summary>
    public string PipePath => OperatingSystem.IsWindows() ? $@"\\.\pipe\{_pipeName}" : Path.Combine(Path.GetTempPath(), _pipeName);

    /// <summary>
    /// Window to draw the video into (Win32 HWND). When set, mpv runs embedded with no controls
    /// of its own and stays open between files; the app draws the controls.
    /// </summary>
    public long? WindowHandle { get; set; }

    public MpvPlayer(RuntimeLayout runtime, SettingsStore store)
    {
        _runtime = runtime;
        _store = store;
    }

    /// <summary>
    /// yt-dlp format for online video: best video up to this height, avoiding AV1 (slow to decode
    /// on many GPUs, and every decoded frame competes with the interpolation for the GPU).
    /// </summary>
    public static string YtdlFormat(int maxHeight) =>
        $"bestvideo[height<=?{maxHeight}][vcodec!*=av01]+bestaudio/bestvideo[height<=?{maxHeight}]+bestaudio/best";

    /// <summary>Command-line for mpv. Kept separate so it can be unit tested.</summary>
    public IReadOnlyList<string> BuildArguments(string? target, AppSettings settings)
    {
        var pipePath = PipePath;
        var args = new List<string>
        {
            $"--input-ipc-server={pipePath}",
            $"--script-opts=novamotion-vpy={_runtime.EngineScript},novamotion-config={_store.EngineConfigPath},ytdl_hook-ytdl_path={_runtime.YtDlpExe}",
            $"--ytdl-format={YtdlFormat(settings.OnlineMaxHeight)}",
            $"--log-file={Path.Combine(_store.Directory, "mpv.log")}",
        };
        if (WindowHandle is { } wid)
        {
            args.AddRange(new[]
            {
                $"--wid={wid}",
                "--force-window=yes",
                "--idle=yes",            // stay running between files: the next one opens instantly
                "--keep-open=yes",       // pause on the last frame instead of closing
                "--osc=no", "--osd-bar=no", "--osd-level=0",   // the app draws controls and messages
                "--input-cursor=no", "--input-vo-keyboard=no", // mouse and keys go to the app
                "--cursor-autohide=no",
                $"--volume={Math.Clamp(settings.Volume, 0, 130)}",
            });
        }
        else
        {
            args.Add("--force-window=immediate");
            args.Add("--title=${media-title} - NOVA Motion");
        }
        if (settings.VideoSync is "audio") args.Add("--video-sync=audio");
        var userConf = Path.Combine(_store.Directory, "mpv-user.conf");
        if (File.Exists(userConf)) args.Add($"--include={userConf}");
        if (target is not null)
        {
            args.Add("--");
            args.Add(target);
        }
        return args;
    }

    public async Task StartAsync(string? target, AppSettings settings, CancellationToken ct = default)
    {
        if (IsRunning) { if (target is not null) await LoadAsync(target); return; }

        _store.WriteEngineConfig(settings.Active.Engine);
        await StartProcessAsync(BuildArguments(target, settings), new Dictionary<string, string>
        {
            ["NOVAMOTION_ENGINE_CONFIG"] = _store.EngineConfigPath,
            ["NOVAMOTION_ENGINE_STATE"] = _store.EngineStatePath,
        }, ct);
        await ObserveAsync(PlayerState.Observed);
    }

    /// <summary>Starts mpv with exactly these arguments and connects to its IPC pipe (see PipePath).</summary>
    public async Task StartProcessAsync(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
                                        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(_runtime.MpvExe) { UseShellExecute = false, WorkingDirectory = _runtime.MpvDir, CreateNoWindow = true };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in environment) psi.Environment[k] = v;

        _process = Process.Start(psi) ?? throw new InvalidOperationException("mpv did not start");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => { Exited?.Invoke(); _ = DisconnectAsync(); };
        await ConnectAsync(TimeSpan.FromSeconds(10), ct);
    }

    /// <summary>Ends mpv without the polite "quit" (used to cancel an export).</summary>
    public void Kill()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    /// <summary>Asks mpv to report these properties whenever they change (PropertyChanged).</summary>
    public async Task ObserveAsync(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var id = Interlocked.Increment(ref _nextObserveId);
            try { await CommandAsync("observe_property", id, name); }
            catch (MpvException) { /* property unknown to this mpv version */ }
        }
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
                    if (ev == "property-change" && msg["name"]?.GetValue<string>() is { } prop)
                        PropertyChanged?.Invoke(prop, msg["data"]);
                    else
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

    public Task SetPropertyAsync(string name, object? value) => CommandAsync("set_property", name, value);

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
