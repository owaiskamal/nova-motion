// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace NovaMotion.Core.ViewModels;

/// <summary>What the view model needs from the window it lives in.</summary>
public interface IUiHost
{
    /// <summary>Runs on the UI thread (player events arrive on a background thread).</summary>
    void Post(Action action);
    IReadOnlyList<string>? PickVideoFiles();
    string? PickSubtitleFile();
    string? ClipboardText();
    void OpenExternal(string pathOrUrl);
}

public enum EngineState { Idle, Off, Opening, Preparing, PreparingLong, Active, Failed, HighFrameRate, NoVideo }

public enum Drawer { None, Motion, Tracks, Queue }

/// <summary>Segoe Fluent Icons glyphs used by state-dependent buttons.</summary>
public static class Glyphs
{
    public const string Play = "", Pause = "";
    public const string Mute = "", Volume0 = "", Volume1 = "", Volume2 = "", Volume3 = "";
    public const string FullScreen = "", ExitFullScreen = "";
}

/// <summary>
/// The whole app's state and behaviour, independent of WPF so it can be tested with a fake player.
/// </summary>
public sealed partial class MainViewModel : Observable, IAsyncDisposable
{
    private readonly IUiHost _host;
    private readonly SettingsStore _store;
    private readonly Func<IPlayerBackend> _createBackend;
    private readonly Func<DateTime> _clock;
    private readonly MediaHistory _history;
    private readonly PlayQueue _queue = new();
    private readonly PlayerState _state = new();
    private readonly List<double> _fpsSamples = new();
    private AppSettings _settings;
    private IPlayerBackend? _backend;
    private string? _current;
    private DateTime? _loadedAt, _lastHistorySave, _toastUntil, _seekAt;
    private double? _pendingResume;
    private bool _syncingVolume, _handledEof;

    public MainViewModel(IUiHost host, SettingsStore store, RuntimeLayout? runtime,
                         Func<IPlayerBackend> createBackend, Func<DateTime>? clock = null)
    {
        _host = host;
        _store = store;
        Runtime = runtime;
        _createBackend = createBackend;
        _clock = clock ?? (() => DateTime.UtcNow);
        _settings = store.Load();
        _history = new MediaHistory(store.Directory);
        _volume = _settings.Volume;
        Availability = EngineAvailability.Of(runtime);
        _queue.Changed += RebuildQueue;

        PlayCommand = new Command(() => _ = OpenAsync(new[] { TargetText.Trim().Trim('"') }), () => TargetText.Trim().Length > 0);
        OpenFilesCommand = new Command(() => { if (_host.PickVideoFiles() is { Count: > 0 } f) _ = OpenAsync(f); });
        AddFilesCommand = new Command(() => { if (_host.PickVideoFiles() is { Count: > 0 } f) Enqueue(f); });
        PlayClipboardCommand = new Command(() => { if (ClipboardLink is { } l) { ClipboardLink = null; _ = OpenAsync(new[] { l }); } });
        DismissClipboardCommand = new Command(() => { _dismissedClipboard = ClipboardLink; ClipboardLink = null; });
        PlayRecentCommand = new Command(p => { if (p is RecentItem r) _ = OpenAsync(new[] { r.Target }); });
        RestartRecentCommand = new Command(p => { if (p is RecentItem r) _ = OpenAsync(new[] { r.Target }, start: 0); });
        RemoveRecentCommand = new Command(p => { if (p is RecentItem r) { _history.Remove(r.Target); _history.Save(); RefreshRecent(); } });
        ClearRecentCommand = new Command(() => { _history.Clear(); _history.Save(); RefreshRecent(); });

        TogglePauseCommand = new Command(() => _ = TogglePauseAsync());
        SeekByCommand = new Command(p => { if (TryNumber(p, out var s)) _ = SeekAsync(s, relative: true); });
        StopCommand = new Command(() => _ = StopAsync());
        NextCommand = new Command(() => _ = PlayNextAsync(), () => _queue.HasNext);
        PreviousCommand = new Command(() => _ = PlayPreviousAsync());
        ToggleMuteCommand = new Command(() => _ = Send("cycle", "mute"));
        ToggleFullscreenCommand = new Command(() => IsFullscreen = !IsFullscreen);
        SetSpeedCommand = new Command(p => { if (TryNumber(p, out var s)) _ = Send("set_property", "speed", s); });
        SelectAudioCommand = new Command(p => { if (p is TrackItem t) _ = Send("set_property", "aid", t.Id == 0 ? "no" : t.Id); });
        SelectSubtitleCommand = new Command(p => { if (p is TrackItem t) _ = Send("set_property", "sid", t.Id == 0 ? "no" : t.Id); });
        AddSubtitleCommand = new Command(() => { if (_host.PickSubtitleFile() is { } f) _ = Send("sub-add", f, "select"); });
        PlayQueueItemCommand = new Command(p => { if (p is QueueItem q && _queue.Jump(q.Index) is not null) _ = PlayCurrentAsync(null); });
        RemoveQueueItemCommand = new Command(p => { if (p is QueueItem q && !q.IsCurrent) _queue.RemoveAt(q.Index); });
        ToggleDrawerCommand = new Command(p => ToggleDrawer(p as string));
        CloseDrawerCommand = new Command(() => ActiveDrawer = Drawer.None);
        OpenSettingsCommand = new Command(() => { RefreshSystem(); IsSettingsOpen = true; });
        CloseSettingsCommand = new Command(() => IsSettingsOpen = false);
        ScreenshotCommand = new Command(() => _ = ScreenshotAsync());
        ToastActionCommand = new Command(() => { var a = _toastAction; HideToast(); a?.Invoke(); });
        OpenLogFolderCommand = new Command(() => _host.OpenExternal(_store.Directory));
        OpenProjectPageCommand = new Command(() => _host.OpenExternal(ProjectUrl));

        InitMotion();
        RefreshRecent();
        RefreshSystem();
    }

    public const string ProjectUrl = "https://github.com/owaiskamal/nova-motion";
    public RuntimeLayout? Runtime { get; }
    public AppSettings Settings => _settings;
    public PlayerState State => _state;
    public PlayQueue Queue => _queue;
    public MediaHistory History => _history;

    // ------------------------------------------------------------------ commands

    public Command PlayCommand { get; }
    public Command OpenFilesCommand { get; }
    public Command AddFilesCommand { get; }
    public Command PlayClipboardCommand { get; }
    public Command DismissClipboardCommand { get; }
    public Command PlayRecentCommand { get; }
    public Command RestartRecentCommand { get; }
    public Command RemoveRecentCommand { get; }
    public Command ClearRecentCommand { get; }
    public Command TogglePauseCommand { get; }
    public Command SeekByCommand { get; }
    public Command StopCommand { get; }
    public Command NextCommand { get; }
    public Command PreviousCommand { get; }
    public Command ToggleMuteCommand { get; }
    public Command ToggleFullscreenCommand { get; }
    public Command SetSpeedCommand { get; }
    public Command SelectAudioCommand { get; }
    public Command SelectSubtitleCommand { get; }
    public Command AddSubtitleCommand { get; }
    public Command PlayQueueItemCommand { get; }
    public Command RemoveQueueItemCommand { get; }
    public Command ToggleDrawerCommand { get; }
    public Command CloseDrawerCommand { get; }
    public Command OpenSettingsCommand { get; }
    public Command CloseSettingsCommand { get; }
    public Command ScreenshotCommand { get; }
    public Command ToastActionCommand { get; }
    public Command OpenLogFolderCommand { get; }
    public Command OpenProjectPageCommand { get; }

    // ------------------------------------------------------------------ home

    private string _targetText = "";
    public string TargetText
    {
        get => _targetText;
        set { if (Set(ref _targetText, value ?? "")) PlayCommand.Refresh(); }
    }

    public ObservableCollection<RecentItem> Recent { get; } = new();
    public bool HasRecent => Recent.Count > 0;
    public RecentItem? ContinueItem => Recent.FirstOrDefault(r => r.HasProgress);

    private string? _clipboardLink, _dismissedClipboard;
    /// <summary>A link on the clipboard, offered as "Play link from clipboard".</summary>
    public string? ClipboardLink
    {
        get => _clipboardLink;
        private set { if (Set(ref _clipboardLink, value)) Raise(nameof(HasClipboardLink), nameof(ClipboardOrigin)); }
    }
    public bool HasClipboardLink => ClipboardLink is not null;
    public string ClipboardOrigin => ClipboardLink is null ? "" : MediaSource.Origin(ClipboardLink);

    /// <summary>Call when the window is activated.</summary>
    public void CheckClipboard()
    {
        var link = MediaSource.AsLink(_host.ClipboardText());
        if (link is null || link == _dismissedClipboard || link == _current || HasMedia) { ClipboardLink = null; return; }
        ClipboardLink = link;
    }

    private void RefreshRecent()
    {
        var now = _clock();
        Recent.Clear();
        foreach (var e in _history.Items.Take(24)) Recent.Add(new RecentItem(e, now));
        Raise(nameof(HasRecent), nameof(ContinueItem));
    }

    // ------------------------------------------------------------------ opening

    /// <summary>Plays files or links, replacing the queue. A single local file brings its folder along.</summary>
    public async Task OpenAsync(IReadOnlyList<string> targets, double? start = null)
    {
        var list = targets.Select(t => t.Trim().Trim('"')).Where(t => t.Length > 0).ToList();
        if (list.Count == 0) return;
        if (list.Count == 1 && !MediaSource.IsOnline(list[0]) && _settings.AutoQueueFolder && File.Exists(list[0]))
            list = PlayQueue.FolderSequence(list[0]).ToList();
        IsSettingsOpen = false;
        TargetText = "";
        _queue.Set(list);
        await PlayCurrentAsync(start);
    }

    /// <summary>Adds to the end of the queue; starts playing if nothing is.</summary>
    public void Enqueue(IReadOnlyList<string> targets)
    {
        var wasEmpty = _queue.Current is null || !HasMedia;
        _queue.Add(targets);
        if (wasEmpty) _ = PlayCurrentAsync(null);
        else ShowToast(targets.Count == 1 ? "Added to queue" : $"{targets.Count} videos added to queue");
    }

    private async Task PlayCurrentAsync(double? start)
    {
        var target = _queue.Current;
        if (target is null) return;
        SaveHistory();
        _current = target;
        _loadedAt = null;
        _handledEof = false;
        _fpsSamples.Clear();
        IsOpening = true;
        OpeningTitle = MediaSource.GuessTitle(target);
        Raise(nameof(Title), nameof(Origin), nameof(HasMedia));
        ClipboardLink = null;

        var resume = start ?? (_settings.ResumePlayback ? _history.ResumePosition(target) : null);
        _pendingResume = resume is > 0 ? resume : null;
        try
        {
            await EnsureBackendAsync();
            await Send("set_property", "start", _pendingResume is { } r ? r.ToString("0.###", CultureInfo.InvariantCulture) : "none");
            await Send("set_property", "pause", false);
            await Send("loadfile", target, "replace");
            _history.Record(target, null, _pendingResume ?? 0, 0, _clock());
            _lastHistorySave = _clock();
        }
        catch (Exception ex)
        {
            IsOpening = false;
            ShowToast("Could not start the player: " + ex.Message);
        }
        UpdateEngineState();
    }

    private async Task EnsureBackendAsync()
    {
        if (_backend is { IsRunning: true }) return;
        if (_backend is not null) await _backend.DisposeAsync();
        var b = _createBackend();
        b.PropertyChanged += (n, d) => _host.Post(() => OnProperty(n, d));
        b.EventReceived += (e, m) => _host.Post(() => OnEvent(e, m));
        b.Exited += () => _host.Post(OnExited);
        _backend = b;
        _store.Save(_settings);
        await b.StartAsync(null, _settings);
    }

    private async Task<JsonNode?> Send(params object?[] command)
    {
        if (_backend is not { IsRunning: true }) return null;
        try { return await _backend.CommandAsync(command); }
        catch (MpvException) { return null; }
        catch (TimeoutException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public async Task PlayNextAsync()
    {
        if (_queue.MoveNext() is null) return;
        await PlayCurrentAsync(null);
    }

    public async Task PlayPreviousAsync()
    {
        // Like every player: first press restarts the current video
        if (Position > 5 || !_queue.HasPrevious) { await SeekAsync(0, relative: false); return; }
        _queue.MovePrevious();
        await PlayCurrentAsync(null);
    }

    public async Task StopAsync()
    {
        SaveHistory();
        IsFullscreen = false;
        ActiveDrawer = Drawer.None;
        await Send("stop");
        _current = null;
        IsOpening = false;
        _queue.Clear();
        RefreshRecent();
        Raise(nameof(HasMedia));
    }

    // ------------------------------------------------------------------ playback

    public bool HasMedia => IsOpening || !_state.Idle;

    private bool _isOpening;
    public bool IsOpening
    {
        get => _isOpening;
        private set { if (Set(ref _isOpening, value)) Raise(nameof(HasMedia), nameof(IsBusy), nameof(BusyText)); }
    }

    private string _openingTitle = "";
    private string OpeningTitle { get => _openingTitle; set => _openingTitle = value; }

    public string Title => !string.IsNullOrWhiteSpace(_state.Title) && !IsOpening ? _state.Title : _openingTitle;
    public string Origin => _current is null ? "" : MediaSource.Origin(_current);
    public bool IsOnline => _current is not null && MediaSource.IsOnline(_current);

    public bool IsBusy => IsOpening || _state.Buffering || (_state.Seeking && _seekAt is { } t && _clock() - t > TimeSpan.FromMilliseconds(400));
    public string BusyText => IsOpening ? (IsOnline ? "Fetching video…" : "Opening…") : _state.Buffering ? "Buffering…" : "";

    public bool Paused => _state.Paused;
    public string PlayPauseGlyph => _state.Paused || _state.Ended ? Glyphs.Play : Glyphs.Pause;
    public string PlayPauseTip => _state.Paused ? "Play (Space)" : "Pause (Space)";

    public double Position => _state.Position;
    public double Duration => _state.Duration;
    public bool HasDuration => _state.Duration > 0;
    public string PositionText => TimeFormat.Format(IsScrubbing ? SeekValue : _state.Position);
    public string DurationText => HasDuration ? TimeFormat.Format(_state.Duration) : "Live";
    public double CachedFraction => HasDuration ? Math.Clamp(_state.CachedUntil / _state.Duration, 0, 1) : 0;
    public IReadOnlyList<double> ChapterFractions =>
        HasDuration ? _state.Chapters.Where(c => c.Time > 0).Select(c => c.Time / _state.Duration).ToList() : Array.Empty<double>();

    private double _seekValue;
    /// <summary>Seek bar value in seconds. Follows playback, except while the user is dragging.</summary>
    public double SeekValue
    {
        get => _seekValue;
        set { if (Set(ref _seekValue, value) && IsScrubbing) Raise(nameof(PositionText)); }
    }

    private bool _isScrubbing;
    public bool IsScrubbing
    {
        get => _isScrubbing;
        set
        {
            if (!Set(ref _isScrubbing, value)) return;
            if (!value) _ = SeekAsync(SeekValue, relative: false);   // released: jump there
        }
    }

    /// <summary>Text for the seek bar hover preview at a fraction (0..1) of the video.</summary>
    public string TimeAt(double fraction) => TimeFormat.Format(Math.Clamp(fraction, 0, 1) * _state.Duration);

    public string? ChapterAt(double fraction)
    {
        var t = Math.Clamp(fraction, 0, 1) * _state.Duration;
        var c = _state.Chapters.LastOrDefault(ch => ch.Time <= t);
        return string.IsNullOrWhiteSpace(c.Title) ? null : c.Title;
    }

    public async Task SeekAsync(double seconds, bool relative)
    {
        _seekAt = _clock();
        if (!relative && HasDuration) seconds = Math.Clamp(seconds, 0, Math.Max(0, _state.Duration - 0.5));
        await Send("seek", seconds, relative ? "relative" : "absolute+exact");
    }

    public async Task SeekPercentAsync(int percent) => await Send("seek", Math.Clamp(percent, 0, 100), "absolute-percent");

    public async Task TogglePauseAsync()
    {
        if (_state.Ended) { await SeekAsync(0, relative: false); await Send("set_property", "pause", false); return; }
        await Send("cycle", "pause");
    }

    private double _volume;
    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(value, 0, 130);
            if (!Set(ref _volume, value)) return;
            Raise(nameof(VolumeGlyph), nameof(VolumeText));
            _settings.Volume = value;
            if (!_syncingVolume)
            {
                _ = Send("set_property", "volume", value);
                if (_state.Muted && value > 0) _ = Send("set_property", "mute", false);
            }
        }
    }
    public bool Muted => _state.Muted;
    public string VolumeText => Muted ? "Muted" : $"{Volume:0}%";
    public string VolumeGlyph => Muted || Volume <= 0 ? Glyphs.Mute
        : Volume < 34 ? Glyphs.Volume1 : Volume < 67 ? Glyphs.Volume2 : Glyphs.Volume3;

    public double Speed => _state.Speed;
    public string SpeedText => Math.Abs(_state.Speed - 1) < 0.001 ? "1×" : $"{_state.Speed:0.##}×";
    public IReadOnlyList<double> SpeedOptions { get; } = new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };

    private bool _isFullscreen;
    public bool IsFullscreen
    {
        get => _isFullscreen;
        set { if (Set(ref _isFullscreen, value)) Raise(nameof(FullscreenGlyph)); }
    }
    public string FullscreenGlyph => IsFullscreen ? Glyphs.ExitFullScreen : Glyphs.FullScreen;

    private bool _isSettingsOpen;
    public bool IsSettingsOpen { get => _isSettingsOpen; set => Set(ref _isSettingsOpen, value); }

    private Drawer _activeDrawer;
    public Drawer ActiveDrawer
    {
        get => _activeDrawer;
        set
        {
            if (!Set(ref _activeDrawer, value)) return;
            Raise(nameof(IsMotionOpen), nameof(IsTracksOpen), nameof(IsQueueOpen), nameof(IsDrawerOpen));
        }
    }
    public bool IsDrawerOpen => ActiveDrawer != Drawer.None;
    public bool IsMotionOpen => ActiveDrawer == Drawer.Motion;
    public bool IsTracksOpen => ActiveDrawer == Drawer.Tracks;
    public bool IsQueueOpen => ActiveDrawer == Drawer.Queue;

    private void ToggleDrawer(string? which)
    {
        var d = which?.ToLowerInvariant() switch
        {
            "motion" => Drawer.Motion, "tracks" => Drawer.Tracks, "queue" => Drawer.Queue, _ => Drawer.None,
        };
        ActiveDrawer = ActiveDrawer == d ? Drawer.None : d;
    }

    // ------------------------------------------------------------------ tracks & queue

    public ObservableCollection<TrackItem> AudioTracks { get; } = new();
    public ObservableCollection<TrackItem> SubtitleTracks { get; } = new();
    public bool HasAudioChoice => AudioTracks.Count > 1;
    public bool HasSubtitles => SubtitleTracks.Count > 1;
    public string SubtitleSummary => SubtitleTracks.FirstOrDefault(t => t.IsSelected && t.Id != 0)?.Label ?? "Off";

    private void RebuildTracks()
    {
        AudioTracks.Clear();
        foreach (var t in _state.AudioTracks) AudioTracks.Add(new TrackItem(t.Id, t.Label, t.Detail, t.Selected));
        SubtitleTracks.Clear();
        SubtitleTracks.Add(new TrackItem(0, "Off", "", !_state.SubtitleTracks.Any(t => t.Selected)));
        foreach (var t in _state.SubtitleTracks) SubtitleTracks.Add(new TrackItem(t.Id, t.Label, t.Detail, t.Selected));
        Raise(nameof(HasAudioChoice), nameof(HasSubtitles), nameof(SubtitleSummary));
    }

    public ObservableCollection<QueueItem> QueueItems { get; } = new();
    public bool HasQueue => QueueItems.Count > 1;
    public bool HasNext => _queue.HasNext;
    public string QueueSummary => _queue.Items.Count <= 1 ? "" : $"{_queue.Index + 1} of {_queue.Items.Count}";
    public string? NextTitle => _queue.HasNext ? TitleFor(_queue.Items[_queue.Index + 1]) : null;

    private string TitleFor(string target) => _history.Find(target)?.Title is { Length: > 0 } t ? t : MediaSource.GuessTitle(target);

    private void RebuildQueue()
    {
        QueueItems.Clear();
        for (var i = 0; i < _queue.Items.Count; i++)
            QueueItems.Add(new QueueItem(i, _queue.Items[i], TitleFor(_queue.Items[i]), i == _queue.Index));
        NextCommand.Refresh();
        Raise(nameof(HasQueue), nameof(HasNext), nameof(QueueSummary), nameof(NextTitle));
    }

    // ------------------------------------------------------------------ player events

    private static readonly string[] PlaybackProps =
    {
        nameof(Title), nameof(Paused), nameof(PlayPauseGlyph), nameof(PlayPauseTip), nameof(Position), nameof(Duration),
        nameof(HasDuration), nameof(PositionText), nameof(DurationText), nameof(CachedFraction), nameof(IsBusy), nameof(BusyText),
    };

    internal void OnProperty(string name, JsonNode? data)
    {
        if (!_state.Apply(name, data)) return;
        switch (name)
        {
            case "time-pos":
                if (!IsScrubbing) { _seekValue = _state.Position; Raise(nameof(SeekValue)); }
                Raise(nameof(Position), nameof(PositionText));
                return;
            case "volume":
                _syncingVolume = true;
                try { Volume = _state.Volume; } finally { _syncingVolume = false; }
                return;
            case "mute": Raise(nameof(Muted), nameof(VolumeGlyph), nameof(VolumeText)); return;
            case "speed": Raise(nameof(Speed), nameof(SpeedText)); return;
            case "track-list": RebuildTracks(); return;
            case "chapter-list": Raise(nameof(ChapterFractions)); return;
            case "demuxer-cache-time": Raise(nameof(CachedFraction)); return;
            case "eof-reached":
                Raise(nameof(PlayPauseGlyph));
                if (_state.Ended && !_handledEof) OnEnded();
                return;
            case "idle-active":
                if (_state.Idle && !IsOpening) { _current = null; RebuildTracks(); }
                Raise(nameof(HasMedia));
                Raise(PlaybackProps);
                UpdateEngineState();
                return;
        }
        if (name is "container-fps" or "estimated-vf-fps" or "display-fps" or "vf" or "video-params/w" or "video-params/h" or "hwdec-current")
            UpdateEngineState();
        else
            Raise(PlaybackProps);
    }

    internal void OnEvent(string ev, JsonNode? msg)
    {
        switch (ev)
        {
            case "file-loaded":
                IsOpening = false;
                _loadedAt = _clock();
                _ = Send("set_property", "start", "none");
                if (_pendingResume is { } r && r > 0)
                    ShowToast($"Resumed at {TimeFormat.Format(r)}", "Start over", () => _ = SeekAsync(0, relative: false));
                _pendingResume = null;
                Raise(PlaybackProps);
                Raise(nameof(Origin), nameof(IsOnline));
                break;
            case "end-file":
                if (TrackInfo.Str(msg?["reason"]) == "error")
                {
                    IsOpening = false;
                    var why = TrackInfo.Str(msg?["file_error"]);
                    ShowToast(FriendlyError(why, _current));
                    if (_queue.HasNext) _ = PlayNextAsync();
                }
                break;
            case "shutdown":
                break;
        }
    }

    private static string FriendlyError(string? mpvError, string? target)
    {
        var online = target is not null && MediaSource.IsOnline(target);
        return mpvError switch
        {
            "loading failed" when online => "This link couldn't be opened. The site may need a newer yt-dlp, or the video is private.",
            "loading failed" => "This file couldn't be opened. It may be missing or damaged.",
            "unrecognized file format" => "This isn't a video format NOVA Motion can play.",
            "no audio or video data played" => "The file has no playable audio or video.",
            null or "" => "The video couldn't be played.",
            _ => $"The video couldn't be played ({mpvError}).",
        };
    }

    private void OnEnded()
    {
        _handledEof = true;
        if (_current is not null)
        {
            _history.Record(_current, _state.Title, _state.Duration, _state.Duration, _clock());
            _history.Save();
        }
        if (_queue.HasNext) _ = PlayNextAsync();
        else IsFullscreen = false;
    }

    private void OnExited()
    {
        var unexpected = HasMedia;
        SaveHistory();
        _backend = null;
        _state.Apply("idle-active", true);
        _current = null;
        IsOpening = false;
        IsFullscreen = false;
        Raise(nameof(HasMedia));
        Raise(PlaybackProps);
        UpdateEngineState();
        RefreshRecent();
        if (unexpected) ShowToast("The player stopped unexpectedly. Details are in mpv.log.", "Open logs", () => _host.OpenExternal(_store.Directory));
    }

    private void SaveHistory()
    {
        if (_current is null || _state.Idle || IsOpening) return;
        _history.Record(_current, _state.Title, _state.Position, _state.Duration, _clock());
        _history.Save();
        _lastHistorySave = _clock();
    }

    // ------------------------------------------------------------------ keyboard

    public async Task HandleAsync(PlayerAction action, int arg = 0)
    {
        switch (action)
        {
            case PlayerAction.TogglePause: await TogglePauseAsync(); break;
            case PlayerAction.SeekBack: await SeekAsync(-5, true); break;
            case PlayerAction.SeekForward: await SeekAsync(5, true); break;
            case PlayerAction.SeekBackLong: await SeekAsync(-30, true); break;
            case PlayerAction.SeekForwardLong: await SeekAsync(30, true); break;
            case PlayerAction.SeekPercent: await SeekPercentAsync(arg); break;
            case PlayerAction.VolumeUp: Volume = Math.Min(130, Math.Round(Volume) + 5); ShowToast($"Volume {Volume:0}%"); break;
            case PlayerAction.VolumeDown: Volume = Math.Max(0, Math.Round(Volume) - 5); ShowToast($"Volume {Volume:0}%"); break;
            case PlayerAction.ToggleMute: await Send("cycle", "mute"); break;
            case PlayerAction.ToggleFullscreen: IsFullscreen = !IsFullscreen; break;
            case PlayerAction.ExitFullscreen:
                if (ActiveDrawer != Drawer.None) ActiveDrawer = Drawer.None;
                else IsFullscreen = false;
                break;
            case PlayerAction.Next: await PlayNextAsync(); break;
            case PlayerAction.Previous: await PlayPreviousAsync(); break;
            case PlayerAction.FrameStep: await Send("frame-step"); break;
            case PlayerAction.FrameBackStep: await Send("frame-back-step"); break;
            case PlayerAction.SpeedUp: await Send("multiply", "speed", 1.1); break;
            case PlayerAction.SpeedDown: await Send("multiply", "speed", 1 / 1.1); break;
            case PlayerAction.SpeedReset: await Send("set_property", "speed", 1.0); break;
            case PlayerAction.ToggleSmoothing: SmoothingEnabled = !SmoothingEnabled; ShowToast(SmoothingEnabled ? "Smoothing on" : "Smoothing off"); break;
            case PlayerAction.CycleEngine: CycleEngine(); break;
            case PlayerAction.CycleTarget: CycleTarget(); break;
            case PlayerAction.ToggleDemo: Demo = !Demo; ShowToast(Demo ? "Split screen: original on the left" : "Split screen off"); break;
            case PlayerAction.ToggleVsr: RtxVsr = !RtxVsr; ShowToast(RtxVsr ? "RTX Video Super Resolution on" : "RTX Video Super Resolution off"); break;
            case PlayerAction.ToggleMotionPanel: ToggleDrawer("motion"); break;
            case PlayerAction.CycleSubtitles: await Send("cycle", "sub"); ShowToast("Subtitles: " + SubtitleSummary); break;
            case PlayerAction.CycleAudio: await Send("cycle", "audio"); break;
            case PlayerAction.Screenshot: await ScreenshotAsync(); break;
            case PlayerAction.Stop: await StopAsync(); break;
        }
    }

    private async Task ScreenshotAsync()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NOVA Motion");
        try { Directory.CreateDirectory(dir); } catch (IOException) { return; }
        var file = Path.Combine(dir, $"{SafeName(Title)} {TimeFormat.Format(Position).Replace(':', '.')}.png");
        if (await Send("screenshot-to-file", file, "video") is not null || File.Exists(file))
            ShowToast("Screenshot saved to Pictures › NOVA Motion", "Show", () => _host.OpenExternal(dir));
    }

    private static string SafeName(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Length > 60 ? s[..60] : s) sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? ' ' : c);
        return sb.ToString().Trim() is { Length: > 0 } n ? n : "NOVA Motion";
    }

    // ------------------------------------------------------------------ toast & tick

    private string _toastText = "";
    private string? _toastActionText;
    private Action? _toastAction;
    public string ToastText { get => _toastText; private set => Set(ref _toastText, value); }
    public string? ToastActionText { get => _toastActionText; private set { if (Set(ref _toastActionText, value)) Raise(nameof(HasToastAction)); } }
    public bool HasToastAction => ToastActionText is not null;
    private bool _toastVisible;
    public bool ToastVisible { get => _toastVisible; private set => Set(ref _toastVisible, value); }

    public void ShowToast(string text, string? action = null, Action? onAction = null)
    {
        ToastText = text;
        ToastActionText = action;
        _toastAction = onAction;
        ToastVisible = true;
        _toastUntil = _clock() + TimeSpan.FromSeconds(action is null ? 2.6 : 6);
    }

    private void HideToast() { ToastVisible = false; _toastAction = null; ToastActionText = null; }

    /// <summary>Called by the view twice a second.</summary>
    public void Tick()
    {
        var now = _clock();
        if (ToastVisible && _toastUntil is { } until && now >= until) HideToast();
        if (_state.Seeking || IsBusy) Raise(nameof(IsBusy), nameof(BusyText));

        if (HasMedia && !IsOpening && !_state.Paused && _state.OutputFps > 0)
        {
            _fpsSamples.Add(_state.OutputFps);
            if (_fpsSamples.Count > 90) _fpsSamples.RemoveAt(0);
            Raise(nameof(FpsSparkline));
        }
        if (_lastHistorySave is { } last && now - last > TimeSpan.FromSeconds(10) && !_state.Paused) SaveHistory();
        UpdateEngineState();
    }

    private static bool TryNumber(object? p, out double v)
    {
        v = 0;
        return p switch
        {
            double d => (v = d) == d,
            int i => (v = i) == i,
            string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v),
            _ => false,
        };
    }

    public async ValueTask DisposeAsync()
    {
        SaveHistory();
        _store.Save(_settings);
        if (_backend is not null) await _backend.DisposeAsync();
        _backend = null;
    }
}
