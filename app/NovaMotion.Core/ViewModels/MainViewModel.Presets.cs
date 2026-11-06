// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Collections.ObjectModel;

namespace NovaMotion.Core.ViewModels;

/// <summary>A preset card on the Enhance page.</summary>
public sealed class PresetItem : Observable
{
    private bool _isSelected;
    public PresetItem(string id, string name, string description, bool mine) { Id = id; Name = name; Description = description; IsMine = mine; }
    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public bool IsMine { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>A video chosen on the Enhance page, not started yet: its size and length (read in a second)
/// give the time it will take with the current choices.</summary>
public sealed class StagedVideo : Observable
{
    private SourceInfo? _source;
    private string _eta = "";
    public StagedVideo(string path) { Path = path; Name = System.IO.Path.GetFileName(path); }
    public string Path { get; }
    public string Name { get; }
    public SourceInfo? Source { get => _source; set { if (Set(ref _source, value)) Raise(nameof(Meta)); } }
    /// <summary>"1080p · 24 fps · 22:41"</summary>
    public string Meta => _source is { Height: > 0 } s
        ? $"{s.Height}p · {Math.Round(s.Fps ?? 0, 2):0.##} fps" + (s.Seconds is > 0 and var d ? $" · {Clock(d)}" : "")
        : "Reading…";
    public string Eta { get => _eta; set => Set(ref _eta, value); }

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}

/// <summary>
/// Enhance's presets: choosing a card sets every option; changing an option shows the preset as edited,
/// with "Save as a new preset" and "Back to it as it came". Videos are chosen first and start with
/// Enhance, so the page can say how long they'll take before anything runs.
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<PresetItem> VideoPresetItems { get; } = new();
    public ObservableCollection<PresetItem> PhotoPresetItems { get; } = new();
    /// <summary>The person's own presets, in the sidebar.</summary>
    public ObservableCollection<PresetItem> MyPresetItems { get; } = new();
    public bool HasMyPresets => MyPresetItems.Count > 0;

    public ObservableCollection<StagedVideo> StagedVideos { get; } = new();
    public bool HasStagedVideos => StagedVideos.Count > 0;

    /// <summary>Reads a video's size, frame rate and length; tests replace it.</summary>
    public Func<string, CancellationToken, Task<SourceInfo>>? ProbeVideo { get; set; }

    private bool _isCustomizeOpen, _photoMode;
    private string _newPresetName = "";
    private Command? _selectVideoPreset, _selectPhotoPreset, _openCustomize, _closeCustomize, _savePreset, _resetPreset, _deletePreset,
                     _useMyPreset, _showVideos, _showPhotos, _chooseVideos, _enhanceStaged, _removeStaged;

    /// <summary>Enhance shows photos instead of videos.</summary>
    public bool PhotoMode { get => _photoMode; set { if (Set(ref _photoMode, value)) Raise(nameof(VideoMode), nameof(PresetName), nameof(PresetEdited), nameof(EnhanceButtonText), nameof(EtaText), nameof(CanDeletePreset)); } }
    public bool VideoMode => !PhotoMode;
    public Command ShowVideosCommand => _showVideos ??= new Command(() => PhotoMode = false);
    public Command ShowPhotosCommand => _showPhotos ??= new Command(() => PhotoMode = true);

    public bool IsCustomizeOpen { get => _isCustomizeOpen; set => Set(ref _isCustomizeOpen, value); }
    public Command OpenCustomizeCommand => _openCustomize ??= new Command(() => { NewPresetName = ""; IsCustomizeOpen = true; });
    public Command CloseCustomizeCommand => _closeCustomize ??= new Command(() => IsCustomizeOpen = false);
    public string NewPresetName { get => _newPresetName; set => Set(ref _newPresetName, value); }

    public Command UseVideoPresetCommand => _selectVideoPreset ??= new Command(p => { if (p is PresetItem i) ApplyVideoPreset(i.Id); });
    public Command UsePhotoPresetCommand => _selectPhotoPreset ??= new Command(p => { if (p is PresetItem i) ApplyPhotoPreset(i.Id); });
    public Command SavePresetCommand => _savePreset ??= new Command(SavePreset);
    public Command ResetPresetCommand => _resetPreset ??= new Command(() => { if (PhotoMode) ApplyPhotoPreset(_settings.PhotoPreset); else ApplyVideoPreset(_settings.VideoPreset); });
    public Command DeletePresetCommand => _deletePreset ??= new Command(DeletePreset, () => CanDeletePreset);
    /// <summary>From the sidebar: Enhance, with that preset.</summary>
    public Command UseMyPresetCommand => _useMyPreset ??= new Command(p =>
    {
        if (p is not PresetItem i) return;
        SelectModule(StudioModule.Enhance);
        if (Presets.FindPhoto(_settings, i.Id) is not null) { PhotoMode = true; ApplyPhotoPreset(i.Id); }
        else { PhotoMode = false; ApplyVideoPreset(i.Id); }
    });

    public Command ChooseVideosCommand => _chooseVideos ??= new Command(() => { if (_host.PickVideoFiles() is { Count: > 0 } f) StageVideos(f); });
    public Command EnhanceStagedCommand => _enhanceStaged ??= new Command(EnhanceStaged, () => HasStagedVideos);
    public Command RemoveStagedCommand => _removeStaged ??= new Command(p =>
    {
        if (p is not StagedVideo v) return;
        StagedVideos.Remove(v);
        RaiseStaged();
    });

    // ------------------------------------------------------------------ the current choices

    /// <summary>What the page is set to now, as a preset would set it.</summary>
    public VideoRecipe CurrentRecipe() => new()
    {
        Enhance = Boost with { },
        Target = ExportTargets.FirstOrDefault(t => t.IsSelected)?.Value ?? "60",
        Quality = _exportQuality,
        HardwareEncoder = _exportGpuEncoder,
    };

    private void ApplyRecipe(VideoRecipe r)
    {
        _settings.Enhance = r.Enhance with { Tuning = EnhanceSettings.CurrentTuning };
        foreach (var t in ExportTargets) t.IsSelected = t.Value == r.Target;
        if (!ExportTargets.Any(t => t.IsSelected) && ExportTargets.FirstOrDefault() is { } first) first.IsSelected = true;
        ExportBest = r.Quality == ExportQuality.Best;
        ExportGpuEncoder = r.HardwareEncoder;
        SyncEnhance();
    }

    public void ApplyVideoPreset(string id)
    {
        if (Presets.FindVideo(_settings, id) is not { } p) return;
        _settings.VideoPreset = p.Id;
        ApplyRecipe(p.Recipe);
        _store.Save(_settings);
        SyncPresets();
    }

    public void ApplyPhotoPreset(string id)
    {
        if (Presets.FindPhoto(_settings, id) is not { } p) return;
        _settings.PhotoPreset = p.Id;
        _photoOptions = p.Options;
        _store.Save(_settings);
        SyncPhoto();
        SyncPresets();
    }

    private VideoPreset? CurrentVideoPreset => Presets.FindVideo(_settings, _settings.VideoPreset);
    private PhotoPreset? CurrentPhotoPreset => Presets.FindPhoto(_settings, _settings.PhotoPreset);

    /// <summary>The chosen preset's name, for the sheet's title.</summary>
    public string PresetName => (PhotoMode ? CurrentPhotoPreset?.Name : CurrentVideoPreset?.Name) ?? "Custom";
    /// <summary>An option differs from the preset's.</summary>
    public bool PresetEdited => PhotoMode
        ? CurrentPhotoPreset is not { } pp || Presets.IsEdited(pp, _photoOptions)
        : CurrentVideoPreset is not { } vp || Presets.IsEdited(vp, CurrentRecipe());
    public bool CanDeletePreset => PhotoMode ? CurrentPhotoPreset is { BuiltIn: false } : CurrentVideoPreset is { BuiltIn: false };

    private void SavePreset()
    {
        var name = NewPresetName.Trim();
        if (name.Length == 0) name = PresetName + (PresetEdited ? " (mine)" : " copy");
        if (PhotoMode)
        {
            var p = Presets.SavePhoto(_settings, name, PhotoSummary, _photoOptions);
            _settings.PhotoPreset = p.Id;
        }
        else
        {
            var p = Presets.SaveVideo(_settings, name, VideoSummaryLine, CurrentRecipe());
            _settings.VideoPreset = p.Id;
        }
        _store.Save(_settings);
        NewPresetName = "";
        BuildPresetItems();
        ShowToast($"Saved as “{(PhotoMode ? CurrentPhotoPreset?.Name : CurrentVideoPreset?.Name)}”");
    }

    private void DeletePreset()
    {
        var id = PhotoMode ? _settings.PhotoPreset : _settings.VideoPreset;
        if (!Presets.Remove(_settings, id)) return;
        if (PhotoMode) ApplyPhotoPreset(Presets.Photo[0].Id); else ApplyVideoPreset(Presets.Video[0].Id);
        _store.Save(_settings);
        BuildPresetItems();
        IsCustomizeOpen = false;
    }

    private void InitPresets()
    {
        // Before presets, the page's own choices were the settings: they start as a built-in preset
        // when they match one, else as they are (shown as edited)
        if (Presets.FindVideo(_settings, _settings.VideoPreset) is null)
        {
            // First time with presets: the preset the old choices match, else the first one
            var now = CurrentRecipe();
            var match = Presets.Video.FirstOrDefault(p => !Presets.IsEdited(p, now));
            if (match is not null) _settings.VideoPreset = match.Id;
            else { _settings.VideoPreset = Presets.Video[0].Id; ApplyRecipe(Presets.Video[0].Recipe); }
            _store.Save(_settings);
        }
        if (Presets.FindPhoto(_settings, _settings.PhotoPreset) is { } pp) _photoOptions = pp.Options;
        else { _settings.PhotoPreset = Presets.Photo[0].Id; _photoOptions = Presets.Photo[0].Options; }
        BuildPresetItems();
        SyncPhoto();
    }

    private void BuildPresetItems()
    {
        VideoPresetItems.Clear();
        foreach (var p in Presets.AllVideo(_settings)) VideoPresetItems.Add(new PresetItem(p.Id, p.Name, p.Description, !p.BuiltIn));
        PhotoPresetItems.Clear();
        foreach (var p in Presets.AllPhoto(_settings)) PhotoPresetItems.Add(new PresetItem(p.Id, p.Name, p.Description, !p.BuiltIn));
        MyPresetItems.Clear();
        foreach (var p in _settings.MyVideoPresets) MyPresetItems.Add(new PresetItem(p.Id, p.Name, p.Description, true));
        foreach (var p in _settings.MyPhotoPresets) MyPresetItems.Add(new PresetItem(p.Id, p.Name, p.Description, true));
        Raise(nameof(HasMyPresets));
        SyncPresets();
    }

    /// <summary>Selection, edited state, summary rows and estimates, after any change.</summary>
    private void SyncPresets()
    {
        foreach (var i in VideoPresetItems) i.IsSelected = i.Id == _settings.VideoPreset && !PresetEditedVideo;
        foreach (var i in PhotoPresetItems) i.IsSelected = i.Id == _settings.PhotoPreset && !PresetEditedPhoto;
        UpdateEstimates();
        Raise(nameof(PresetName), nameof(PresetEdited), nameof(CanDeletePreset), nameof(VideoSummaryLine), nameof(SizeRow), nameof(FrameRateRow),
              nameof(CleanupRow), nameof(StrengthRow), nameof(FixCompressionRow), nameof(PhotoSummary), nameof(PhotoKindRow), nameof(PhotoSizeRow),
              nameof(PhotoModelRow), nameof(PhotoCleanupRow), nameof(PhotoFixRow), nameof(PhotoRestoreRow), nameof(EnhanceButtonText));
        _deletePreset?.Refresh();
    }

    private bool PresetEditedVideo => CurrentVideoPreset is not { } vp || Presets.IsEdited(vp, CurrentRecipe());
    private bool PresetEditedPhoto => CurrentPhotoPreset is not { } pp || Presets.IsEdited(pp, _photoOptions);

    // ------------------------------------------------------------------ summary rows (tap to customize)

    public string SizeRow => Boost.IsOn ? (Boost.Height <= 0 ? "Same size" : EnhanceCatalog.SizeName(Boost.Height)) : "Same size";
    public string FrameRateRow => EngineCatalog.TargetName(ExportTargets.FirstOrDefault(t => t.IsSelected)?.Value ?? "60");
    public string CleanupRow => Boost.IsOn ? EnhanceCatalog.Cleanups.FirstOrDefault(c => c.Value == Boost.Clean)?.Label ?? "" : "None";
    public string StrengthRow => Boost.IsOn ? EnhanceCatalog.Strengths.FirstOrDefault(c => c.Value == (Boost.Strength ?? "balanced"))?.Label ?? "" : "—";
    public string FixCompressionRow => Boost.IsOn && Boost.FixCompression ? "On" : "Off";
    public string VideoSummaryLine => Boost.IsOn ? $"{EnhanceCatalog.Describe(Boost)} · {FrameRateRow}" : $"Smooth only · {FrameRateRow}";

    public string PhotoKindRow => Photo.Presets.FirstOrDefault(c => c.Value == _photoOptions.Preset)?.Label ?? "";
    public string PhotoSizeRow => Photo.Scales.FirstOrDefault(c => c.Value == _photoOptions.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture))?.Label ?? "";
    public string PhotoModelRow => _photoOptions.Generative ? "Generative" : _photoOptions.MaxDetail ? "Max detail" : "Standard";
    public string PhotoCleanupRow => _photoOptions.MaxDetail || _photoOptions.Generative ? "Built in" : EnhanceCatalog.Cleanups.FirstOrDefault(c => c.Value == _photoOptions.Clean)?.Label ?? "";
    public string PhotoFixRow => _photoOptions.FixCompression ? "On" : "Off";
    public string PhotoRestoreRow => _photoOptions.Generative ? "Built in" : _photoOptions.Restore > 0 ? "On" : "Off";
    public string PhotoSummary => _photoOptions.Generative
        ? $"{PhotoKindRow} · {PhotoSizeRow} · Generative"
        : _photoOptions.Restore > 0 && _photoOptions.Scale <= 1
        ? $"{PhotoKindRow} · {PhotoSizeRow} · Noise and blur removed"
        : $"{PhotoKindRow} · {PhotoSizeRow} · {PhotoModelRow}" + (_photoOptions.Restore > 0 ? " · Noise and blur removed" : "");

    // ------------------------------------------------------------------ videos waiting to start, and estimates

    /// <summary>Videos chosen or dropped on the page: read, estimated, started with Enhance.</summary>
    public void StageVideos(IEnumerable<string> files)
    {
        foreach (var f in files.Where(f => !MediaSource.IsOnline(f)))
        {
            if (StagedVideos.Any(v => string.Equals(v.Path, f, StringComparison.OrdinalIgnoreCase))) continue;
            var v = new StagedVideo(f);
            StagedVideos.Add(v);
            _ = ReadStagedAsync(v);
            _previewVideo ??= f;
        }
        Raise(nameof(PreviewVideoName), nameof(HasPreviewVideo));
        RaiseStaged();
    }

    private async Task ReadStagedAsync(StagedVideo v)
    {
        var probe = ProbeVideo ?? (Runtime is { } rt ? (p, ct) => ExportRunner.ProbeAsync(rt.MpvExe, p, ct) : null);
        if (probe is null) return;
        SourceInfo s;
        try { s = await probe(v.Path, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { return; }
        _host.Post(() => { v.Source = s; UpdateEstimates(); });
    }

    private void RaiseStaged()
    {
        Raise(nameof(HasStagedVideos), nameof(EnhanceButtonText));
        _enhanceStaged?.Refresh();
        UpdateEstimates();
    }

    private void EnhanceStaged()
    {
        if (!HasStagedVideos) return;
        var work = StagedVideos.ToDictionary(v => v.Path, v => v.Source is { } s ? WorkFor(s) : null, StringComparer.OrdinalIgnoreCase);
        var already = Exports.ToHashSet();
        // Recorded before the queue starts: the first one begins straight away
        _queuedWork = work;
        QueueExports(StagedVideos.Select(v => v.Path).ToList());
        _queuedWork = null;
        foreach (var e in Exports.Where(e => !already.Contains(e)))
            e.EstimatedWork ??= work.GetValueOrDefault(e.Input);
        StagedVideos.Clear();
        RaiseStaged();
    }

    private Dictionary<string, Dictionary<string, double>?>? _queuedWork;

    private Dictionary<string, double>? WorkFor(SourceInfo s) =>
        s is { Width: > 0, Height: > 0, Fps: > 0, Seconds: > 0 }
            ? Estimate.VideoWork(s.Width.Value, s.Height.Value, s.Fps.Value, PartSeconds(s.Seconds.Value), CurrentRecipe(), Eng.GpuClass, _settings.EstimateCalibration)
            : null;

    /// <summary>Only part of the video when From and Seconds are set.</summary>
    private double PartSeconds(double whole)
    {
        var from = ExportOptions.ParseTime(ExportStartText) ?? 0;
        var length = ExportOptions.ParseTime(ExportLengthText);
        return Math.Max(1, Math.Min(whole - from, length ?? double.MaxValue));
    }

    private string _etaText = "";
    /// <summary>"About 1 h 10 min on this PC", for what's chosen, before anything runs.</summary>
    public string EtaText { get => PhotoMode ? PhotoEtaText : _etaText; private set { _etaText = value; Raise(); } }

    public string EnhanceButtonText => PhotoMode ? "Enhance photo"
        : StagedVideos.Count switch { 0 => "Choose videos to enhance", 1 => "Enhance video", var n => $"Enhance {n} videos" };

    private void UpdateEstimates()
    {
        var total = TimeSpan.Zero;
        var known = 0;
        foreach (var v in StagedVideos)
        {
            if (v.Source is { } s && WorkFor(s) is { } w)
            {
                var t = TimeSpan.FromSeconds(w.Values.Sum());
                v.Eta = Estimate.Words(t);
                total += t;
                known++;
            }
            else v.Eta = "";
        }
        var where = Eng.GpuClass is "entry" ? " on this PC (GTX-class card)" : " on this PC";
        EtaText = known == 0 ? "" : $"{char.ToUpperInvariant(Estimate.Words(total)[0])}{Estimate.Words(total)[1..]}{where}"
                                    + (known < StagedVideos.Count ? ", for the videos read so far" : "");
        Raise(nameof(PhotoEtaText));
    }

    /// <summary>Learns this PC's speed from a finished export (EnhanceStaged recorded its estimate).</summary>
    private void LearnFromExport(ExportItem item)
    {
        if (item.EstimatedWork is not { } work || item.StartedAt is not { } started || item.Options.Preview || item.Options.Still) return;
        Estimate.Calibrate(_settings.EstimateCalibration, work, _clock() - started);
        _store.Save(_settings);
    }

    private (int W, int H)? _photoSize;
    public string PhotoEtaText => _photo is null || _photoSize is not { } sz ? ""
        : $"{char.ToUpperInvariant(Estimate.Words(Estimate.Photo(sz.W, sz.H, _photoOptions, Eng.GpuClass, _settings.EstimateCalibration))[0])}"
          + $"{Estimate.Words(Estimate.Photo(sz.W, sz.H, _photoOptions, Eng.GpuClass, _settings.EstimateCalibration))[1..]} on this PC";
}
