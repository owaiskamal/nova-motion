// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NovaMotion.Core.ViewModels;

/// <summary>Limits shared with the player process.</summary>
public static class MainViewModelLimits
{
    /// <summary>Volume boost: up to 150 % for quiet files.</summary>
    public const double MaxVolume = 150;
}

/// <summary>
/// What modern players have: chapters and Skip intro, ±10 s, subtitle and audio timing, subtitle size,
/// night mode, picture adjustments, aspect, zoom and rotation, A-B loop, a sleep timer, an Up next
/// countdown, picture in picture, and a shortcuts sheet.
/// </summary>
public sealed partial class MainViewModel
{
    public double MaxVolume => MainViewModelLimits.MaxVolume;

    private double _subDelay, _audioDelay, _subScale = 1;
    private bool _nightMode, _isPip, _showShortcuts, _upNextCancelled;
    private int _brightness, _contrast, _saturation, _gamma, _rotate, _abStage;
    private DateTime? _sleepAt;
    private bool _sleepAtEnd;
    private Command? _back10, _forward10, _nextChapter, _prevChapter, _skipIntro, _subEarlier, _subLater, _audioEarlier, _audioLater,
                     _resetPicture, _rotateCmd, _abLoop, _togglePip, _toggleShortcuts, _toggleDemo, _upNextNow, _upNextCancel,
                     _selectAspect, _selectZoom, _selectSleep;

    public Command Back10Command => _back10 ??= new Command(() => _ = SeekAsync(-10, true));
    public Command Forward10Command => _forward10 ??= new Command(() => _ = SeekAsync(10, true));
    public Command NextChapterCommand => _nextChapter ??= new Command(() => _ = Send("add", "chapter", 1));
    public Command PreviousChapterCommand => _prevChapter ??= new Command(() => _ = Send("add", "chapter", -1));
    public Command ToggleDemoCommand => _toggleDemo ??= new Command(() => Demo = !Demo);

    // ------------------------------------------------------------------ chapters: Skip intro

    private static readonly Regex IntroChapter = new(@"^\s*(intro|opening|op|recap|previously)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The chapter playing now, by name.</summary>
    public string CurrentChapter => _state.Chapters.LastOrDefault(c => c.Time <= _state.Position + 0.05).Title ?? "";

    /// <summary>The chapter now is an intro, opening or recap, with something after it.</summary>
    public bool ShowSkipIntro => HasMedia && SkipTarget() is not null;
    public string SkipIntroText => IntroChapter.Match(CurrentChapter).Groups[1].Value.ToLowerInvariant() switch
    {
        "recap" or "previously" => "Skip recap",
        _ => "Skip intro",
    };
    public Command SkipIntroCommand => _skipIntro ??= new Command(() => { if (SkipTarget() is { } t) _ = SeekAsync(t, relative: false); });

    private double? SkipTarget()
    {
        if (!IntroChapter.IsMatch(CurrentChapter)) return null;
        var next = _state.Chapters.FirstOrDefault(c => c.Time > _state.Position + 1);
        return next.Title is null && next.Time == 0 ? null : next.Time;
    }

    // ------------------------------------------------------------------ subtitles and audio

    public double SubDelay { get => _subDelay; set { if (Set(ref _subDelay, Math.Round(value, 2))) { _ = Send("set_property", "sub-delay", _subDelay); Raise(nameof(SubDelayText)); } } }
    public double AudioDelay { get => _audioDelay; set { if (Set(ref _audioDelay, Math.Round(value, 2))) { _ = Send("set_property", "audio-delay", _audioDelay); Raise(nameof(AudioDelayText)); } } }
    public string SubDelayText => Seconds(_subDelay);
    public string AudioDelayText => Seconds(_audioDelay);
    /// <summary>Subtitles a tenth of a second earlier or later; audio by 50 ms.</summary>
    public Command SubEarlierCommand => _subEarlier ??= new Command(() => SubDelay -= 0.1);
    public Command SubLaterCommand => _subLater ??= new Command(() => SubDelay += 0.1);
    public Command AudioEarlierCommand => _audioEarlier ??= new Command(() => AudioDelay -= 0.05);
    public Command AudioLaterCommand => _audioLater ??= new Command(() => AudioDelay += 0.05);

    /// <summary>Subtitle size, 0.5 to 2 times.</summary>
    public double SubScale { get => _subScale; set { value = Math.Clamp(value, 0.5, 2); if (Set(ref _subScale, value)) _ = Send("set_property", "sub-scale", value); } }

    /// <summary>Night mode: loud scenes quieter, voices clearer (dynamic range evened out).</summary>
    public bool NightMode
    {
        get => _nightMode;
        set
        {
            if (!Set(ref _nightMode, value)) return;
            _ = value ? Send("af", "add", "@night:lavfi=[dynaudnorm=f=250:g=15:p=0.9:m=8]") : Send("af", "remove", "@night");
            ShowToast(value ? "Night mode: quieter peaks, clearer voices" : "Night mode off");
        }
    }

    private static string Seconds(double s) => Math.Abs(s) < 0.005 ? "0 s" : (s > 0 ? "+" : "−") + Math.Abs(s).ToString("0.0#", CultureInfo.InvariantCulture) + " s";

    // ------------------------------------------------------------------ picture

    public int Brightness { get => _brightness; set => SetPicture(ref _brightness, value, "brightness"); }
    public int Contrast { get => _contrast; set => SetPicture(ref _contrast, value, "contrast"); }
    public int Saturation { get => _saturation; set => SetPicture(ref _saturation, value, "saturation"); }
    public int Gamma { get => _gamma; set => SetPicture(ref _gamma, value, "gamma"); }
    public bool PictureChanged => _brightness != 0 || _contrast != 0 || _saturation != 0 || _gamma != 0 || _rotate != 0
                                  || AspectOptions.Any(a => a.IsSelected && a.Value != "auto") || ZoomOptions.Any(z => z.IsSelected && z.Value != "fit");

    private void SetPicture(ref int field, int value, string property)
    {
        value = Math.Clamp(value, -100, 100);
        if (field == value) return;
        field = value;
        _ = Send("set_property", property, value);
        Raise(property switch { "brightness" => nameof(Brightness), "contrast" => nameof(Contrast), "saturation" => nameof(Saturation), _ => nameof(Gamma) });
        Raise(nameof(PictureChanged));
    }

    public ObservableCollection<OptionItem> AspectOptions { get; } = new(new[]
    {
        new Choice("auto", "Auto"), new Choice("16:9", "16:9"), new Choice("4:3", "4:3"), new Choice("21:9", "21:9"), new Choice("2.39:1", "2.39"),
    }.Select(c => new OptionItem(c) { IsSelected = c.Value == "auto" }));
    public ObservableCollection<OptionItem> ZoomOptions { get; } = new(new[]
    {
        new Choice("fit", "Fit", "The whole picture, with bars"), new Choice("fill", "Fill", "No bars: the edges are cut"),
        new Choice("stretch", "Stretch", "No bars: the picture is stretched"),
    }.Select(c => new OptionItem(c) { IsSelected = c.Value == "fit" }));

    public Command SelectAspectCommand => _selectAspect ??= new Command(p =>
    {
        if (p is not OptionItem o) return;
        foreach (var a in AspectOptions) a.IsSelected = a == o;
        _ = Send("set_property", "video-aspect-override", o.Value == "auto" ? "-1" : o.Value);
        Raise(nameof(PictureChanged));
    });
    public Command SelectZoomCommand => _selectZoom ??= new Command(p =>
    {
        if (p is not OptionItem o) return;
        foreach (var z in ZoomOptions) z.IsSelected = z == o;
        _ = Send("set_property", "panscan", o.Value == "fill" ? 1.0 : 0.0);
        _ = Send("set_property", "keepaspect", o.Value != "stretch");
        Raise(nameof(PictureChanged));
    });
    /// <summary>A quarter turn clockwise, for phone videos filmed sideways.</summary>
    public Command RotateCommand => _rotateCmd ??= new Command(() =>
    {
        _rotate = (_rotate + 90) % 360;
        _ = Send("set_property", "video-rotate", _rotate);
        ShowToast(_rotate == 0 ? "Rotation off" : $"Rotated {_rotate}°");
        Raise(nameof(PictureChanged));
    });
    public Command ResetPictureCommand => _resetPicture ??= new Command(() =>
    {
        Brightness = Contrast = Saturation = Gamma = 0;
        _rotate = 0;
        _ = Send("set_property", "video-rotate", 0);
        SelectAspectCommand.Execute(AspectOptions[0]);
        SelectZoomCommand.Execute(ZoomOptions[0]);
        Raise(nameof(PictureChanged));
    });

    // ------------------------------------------------------------------ A-B loop

    /// <summary>First press marks A, second marks B and loops between them, third clears.</summary>
    public Command ABLoopCommand => _abLoop ??= new Command(() =>
    {
        _ = Send("ab-loop");
        _abStage = (_abStage + 1) % 3;
        Raise(nameof(ABLoopText), nameof(ABLoopOn));
        ShowToast(_abStage switch
        {
            1 => $"Loop start: {TimeFormat.Format(_state.Position)}. Press again at the end",
            2 => $"Looping to {TimeFormat.Format(_state.Position)}. Press again to stop",
            _ => "Loop off",
        });
    });
    public string ABLoopText => _abStage switch { 1 => "Set loop end", 2 => "Stop the loop", _ => "Loop a part (A–B)" };
    public bool ABLoopOn => _abStage == 2;

    // ------------------------------------------------------------------ sleep timer

    public ObservableCollection<OptionItem> SleepOptions { get; } = new(new[]
    {
        new Choice("0", "Off"), new Choice("15", "15 min"), new Choice("30", "30 min"), new Choice("60", "1 h"), new Choice("end", "End of video"),
    }.Select(c => new OptionItem(c) { IsSelected = c.Value == "0" }));

    public Command SelectSleepCommand => _selectSleep ??= new Command(p =>
    {
        if (p is not OptionItem o) return;
        foreach (var s in SleepOptions) s.IsSelected = s == o;
        _sleepAtEnd = o.Value == "end";
        _sleepAt = int.TryParse(o.Value, out var m) && m > 0 ? _clock().AddMinutes(m) : null;
        ShowToast(o.Value switch { "0" => "Sleep timer off", "end" => "Pausing at the end of this video", _ => $"Pausing in {o.Label}" });
        Raise(nameof(SleepText));
    });
    public string SleepText => _sleepAtEnd ? "At the end of this video" : _sleepAt is { } at ? $"Pauses at {at.ToLocalTime():HH:mm}" : "";

    // ------------------------------------------------------------------ Up next

    /// <summary>The last seconds of a video with another after it: a countdown card.</summary>
    public bool ShowUpNext => HasMedia && _queue.HasNext && !_upNextCancelled && !_sleepAtEnd && HasDuration
                              && _state.Duration - _state.Position is > 0 and <= UpNextSeconds && !_state.Paused;
    public const double UpNextSeconds = 12;
    public string UpNextText => $"Plays in {Math.Max(1, Math.Ceiling(_state.Duration - _state.Position)):0} s";
    public Command UpNextNowCommand => _upNextNow ??= new Command(() => _ = PlayNextAsync());
    public Command UpNextCancelCommand => _upNextCancel ??= new Command(() => { _upNextCancelled = true; Raise(nameof(ShowUpNext)); });

    // ------------------------------------------------------------------ picture in picture, shortcuts

    /// <summary>A small always-on-top window in a corner; the app shapes the window.</summary>
    public bool IsPip { get => _isPip; set { if (Set(ref _isPip, value) && value) IsFullscreen = false; } }
    public Command TogglePipCommand => _togglePip ??= new Command(() => IsPip = !IsPip);

    public bool ShowShortcuts { get => _showShortcuts; set => Set(ref _showShortcuts, value); }
    public Command ToggleShortcutsCommand => _toggleShortcuts ??= new Command(() => ShowShortcuts = !ShowShortcuts);
    public IReadOnlyList<KeyMap.Shortcut> ShortcutList => KeyMap.All;

    // ------------------------------------------------------------------ seek-bar previews

    private Thumbnails? _thumbs;
    private CancellationTokenSource? _thumbAsk;

    /// <summary>A picture of the video at this fraction of it, for the seek bar's hover bubble; null for
    /// streams, or when a newer request replaced this one.</summary>
    public async Task<string?> ThumbnailAtAsync(double fraction)
    {
        if (_current is not { } media || Runtime is null || !HasDuration) return null;
        _thumbs ??= new Thumbnails(Runtime.MpvExe);
        _thumbAsk?.Cancel();
        var ask = _thumbAsk = new CancellationTokenSource();
        try { await Task.Delay(120, ask.Token); }
        catch (OperationCanceledException) { return null; }
        return await _thumbs.GetAsync(media, Math.Clamp(fraction, 0, 1) * _state.Duration, _state.Duration, ask.Token);
    }

    // ------------------------------------------------------------------ every half second

    private void PlayerTick(DateTime now)
    {
        if (!HasMedia) { if (IsPip) IsPip = false; return; }
        Raise(nameof(ShowSkipIntro), nameof(SkipIntroText), nameof(CurrentChapter), nameof(ShowUpNext), nameof(UpNextText));
        if (_sleepAt is { } at && now >= at)
        {
            _sleepAt = null;
            foreach (var s in SleepOptions) s.IsSelected = s.Value == "0";
            _ = Send("set_property", "pause", true);
            ShowToast("Sleep timer: paused");
            Raise(nameof(SleepText));
        }
        if (_sleepAtEnd && HasDuration && _state.Duration - _state.Position < 0.8 && !_state.Paused)
        {
            _sleepAtEnd = false;
            _upNextCancelled = true;
            foreach (var s in SleepOptions) s.IsSelected = s.Value == "0";
            _ = Send("set_property", "pause", true);
            Raise(nameof(SleepText));
        }
    }

    /// <summary>A new file starts with the person's timing and picture reset; night mode and size stay.</summary>
    private void ResetPerFile()
    {
        _subDelay = _audioDelay = 0;
        _abStage = 0;
        _upNextCancelled = false;
        Raise(nameof(SubDelayText), nameof(AudioDelayText), nameof(ABLoopText), nameof(ABLoopOn), nameof(SubDelay), nameof(AudioDelay));
        if (_nightMode) _ = Send("af", "add", "@night:lavfi=[dynaudnorm=f=250:g=15:p=0.9:m=8]");
        if (Math.Abs(_subScale - 1) > 0.01) _ = Send("set_property", "sub-scale", _subScale);
    }
}
