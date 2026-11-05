// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Collections.ObjectModel;

namespace NovaMotion.Core.ViewModels;

/// <summary>Enhance's AI clean-up and upscaling choices, the preview, and its setup.</summary>
public sealed partial class MainViewModel
{
    /// <summary>A preview is a few seconds: long enough to see motion and detail, quick to make.</summary>
    public const double PreviewSeconds = 4;
    public string PreviewFolder { get; set; } = Path.Combine(Path.GetTempPath(), "NOVA Studio previews");

    public ObservableCollection<OptionItem> EnhancePresets { get; } = new(EnhanceCatalog.Presets.Select(c => new OptionItem(c)));
    public ObservableCollection<OptionItem> EnhanceSizes { get; } = new(EnhanceCatalog.Sizes.Select(c => new OptionItem(c)));
    public ObservableCollection<OptionItem> EnhanceCleanups { get; } = new(EnhanceCatalog.Cleanups.Select(c => new OptionItem(c)));
    public ObservableCollection<OptionItem> EnhanceStrengths { get; } = new(EnhanceCatalog.Strengths.Select(c => new OptionItem(c)));
    public ObservableCollection<OptionItem> EnhanceDetails { get; } = new(EnhanceCatalog.Details.Select(c => new OptionItem(c)));
    public ObservableCollection<OptionItem> EnhanceGrains { get; } = new(EnhanceCatalog.Grains.Select(c => new OptionItem(c)));

    public Command SelectEnhancePresetCommand { get; private set; } = null!;
    public Command SelectEnhanceSizeCommand { get; private set; } = null!;
    public Command SelectEnhanceCleanupCommand { get; private set; } = null!;
    public Command SelectEnhanceDetailCommand { get; private set; } = null!;
    public Command SelectEnhanceStrengthCommand { get; private set; } = null!;
    public Command SelectEnhanceGrainCommand { get; private set; } = null!;
    public Command PreviewEnhanceCommand { get; private set; } = null!;
    public Command SetupEnhanceCommand { get; private set; } = null!;
    public Command PreviewFrameCommand { get; private set; } = null!;
    public Command ChangePreviewVideoCommand { get; private set; } = null!;
    public Command OpenFramePreviewCommand { get; private set; } = null!;

    private string? _previewVideo, _framePreview;

    private EnhanceSettings Boost => _settings.Enhance ??= new EnhanceSettings();

    private void InitEnhance()
    {
        if (Boost.Tuning < EnhanceSettings.CurrentTuning)
        {
            // Sharpening on top of the AI drew dark outlines on real footage: off, once, for everyone
            Boost.Detail = 0;
            Boost.Tuning = EnhanceSettings.CurrentTuning;
            _store.Save(_settings);
        }
        SelectEnhanceStrengthCommand = new Command(p => ChangeEnhance(p, (e, v) => e.Strength = v));
        SelectEnhancePresetCommand = new Command(p => ChangeEnhance(p, (e, v) => e.Preset = v));
        SelectEnhanceSizeCommand = new Command(p => ChangeEnhance(p, (e, v) => e.Height = (int)EnhanceCatalog.Number(v)));
        SelectEnhanceCleanupCommand = new Command(p => ChangeEnhance(p, (e, v) => e.Clean = v));
        SelectEnhanceDetailCommand = new Command(p => ChangeEnhance(p, (e, v) => e.Detail = EnhanceCatalog.Number(v)));
        SelectEnhanceGrainCommand = new Command(p => ChangeEnhance(p, (e, v) => e.Grain = EnhanceCatalog.Number(v)));
        PreviewEnhanceCommand = new Command(() =>
        {
            if (_host.PickVideoFiles() is not { Count: > 0 } f) return;
            QueueExports(f.Take(1), preview: true);
            ShowToast($"Making a {PreviewSeconds:0}-second before/after" + (ExportStartText.Length > 0 ? $" from {ExportStartText}" : "")
                      + ". It plays when it's ready.");
        });
        PreviewFrameCommand = new Command(() =>
        {
            if (_previewVideo is null || !File.Exists(_previewVideo))
            {
                if (_host.PickVideoFiles() is not { Count: > 0 } f) return;
                _previewVideo = f[0];
                Raise(nameof(PreviewVideoName));
            }
            QueueExports(new[] { _previewVideo }, preview: false, still: true);
            FramePreviewStatus = "Making a before/after frame" + (ExportStartText.Length > 0 ? $" at {ExportStartText}" : "") + "…";
        });
        ChangePreviewVideoCommand = new Command(() =>
        {
            if (_host.PickVideoFiles() is not { Count: > 0 } f) return;
            _previewVideo = f[0];
            Raise(nameof(PreviewVideoName));
            PreviewFrameCommand.Execute(null);
        });
        OpenFramePreviewCommand = new Command(() => { if (_framePreview is { } p && File.Exists(p)) _host.OpenExternal(p); },
                                              () => _framePreview is not null);
        SetupEnhanceCommand = new Command(() =>
        {
            if (Runtime is null) return;
            _host.RunInConsole(Path.Combine(Path.GetDirectoryName(Runtime.Root) ?? Runtime.Root, "setup-enhance.cmd"), "");
            ShowToast("Adding the Enhance models in a separate window. Come back here when it says it's ready.");
        });
        SyncEnhance();
    }

    private void ChangeEnhance(object? p, Action<EnhanceSettings, string> apply)
    {
        if (p is not OptionItem o) return;
        apply(Boost, o.Value);
        _store.Save(_settings);
        SyncEnhance();
    }

    private void SyncEnhance()
    {
        var e = Boost;
        static bool Same(string v, double x) => Math.Abs(EnhanceCatalog.Number(v) - x) < 0.01;
        foreach (var o in EnhancePresets) o.IsSelected = o.Value == e.Preset;
        foreach (var o in EnhanceSizes) o.IsSelected = Same(o.Value, e.Height);
        foreach (var o in EnhanceCleanups) o.IsSelected = o.Value == e.Clean;
        foreach (var o in EnhanceDetails) o.IsSelected = Same(o.Value, e.Detail);
        foreach (var o in EnhanceStrengths) o.IsSelected = o.Value == (e.Strength ?? "balanced");
        foreach (var o in EnhanceGrains) o.IsSelected = Same(o.Value, e.Grain);
        Raise(nameof(EnhanceOn), nameof(EnhanceReady), nameof(EnhanceNeedsSetup), nameof(ShowEnhanceOptions),
              nameof(EnhancePresetNote), nameof(EnhanceFixCompression), nameof(EnhanceSummary));
    }

    /// <summary>A preset other than "Smooth only" is chosen.</summary>
    public bool EnhanceOn => Boost.IsOn;
    /// <summary>The models and vs-mlrt are installed (setup-enhance.cmd).</summary>
    public bool EnhanceReady => EnhanceCatalog.IsReady(Runtime);
    public bool EnhanceNeedsSetup => EnhanceOn && !EnhanceReady;
    public bool ShowEnhanceOptions => EnhanceOn && EnhanceReady;
    public string EnhancePresetNote => EnhanceCatalog.Presets.FirstOrDefault(c => c.Value == Boost.Preset)?.Description ?? "";
    public string EnhanceSummary => EnhanceOn ? EnhanceCatalog.Describe(Boost) : "";

    public bool EnhanceFixCompression
    {
        get => Boost.FixCompression;
        set { if (Boost.FixCompression == value) return; Boost.FixCompression = value; _store.Save(_settings); Raise(); Raise(nameof(EnhanceSummary)); }
    }

    /// <summary>Checks again whether setup-enhance.cmd has run: when the Enhance page opens.</summary>
    public void RefreshEnhance() => SyncEnhance();

    /// <summary>The before/after frame on the page: original on the left, enhanced on the right, at full size.</summary>
    public string? FramePreview => _framePreview;
    public bool HasFramePreview => _framePreview is not null;
    public string PreviewVideoName => _previewVideo is { } v ? Path.GetFileName(v) : "";
    private string _framePreviewStatus = "";
    public string FramePreviewStatus { get => _framePreviewStatus; private set => Set(ref _framePreviewStatus, value); }

    private void ShowFramePreview(string file)
    {
        _framePreview = file;
        FramePreviewStatus = $"{Path.GetFileName(_previewVideo ?? file)}: original on the left, enhanced on the right, at full size. Click to open it larger.";
        Raise(nameof(FramePreview), nameof(HasFramePreview));
        OpenFramePreviewCommand.Refresh();
        ShowToast("Frame preview ready");
    }

    private void PlayPreview(string file)
    {
        SelectModule(StudioModule.Motion);
        _ = OpenAsync(new[] { file });
    }
}
