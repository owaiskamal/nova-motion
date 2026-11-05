// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Collections.ObjectModel;

namespace NovaMotion.Core.ViewModels;

/// <summary>Enhance a photo: one picture through Enhance's models, with a before/after to compare.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Enhances the pictures; set by the app from the runtime, or by tests.</summary>
    public IPhotoRunner? PhotoEnhancer { get; set; }

    /// <summary>Where enhanced photos go: Pictures\NOVA Studio.</summary>
    public string PhotoFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) is { Length: > 0 } p ? p : Path.GetTempPath(), "NOVA Studio");

    public ObservableCollection<OptionItem> PhotoPresets { get; } = new(Photo.Presets.Select(c => new OptionItem(c)));
    public ObservableCollection<OptionItem> PhotoScales { get; } = new(Photo.Scales.Select(c => new OptionItem(c)));
    public ObservableCollection<OptionItem> PhotoModels { get; } = new(Photo.Models.Select(c => new OptionItem(c)));
    public ObservableCollection<OptionItem> PhotoCleanups { get; } = new(EnhanceCatalog.Cleanups.Select(c => new OptionItem(c)));

    public Command ChoosePhotoCommand { get; private set; } = null!;
    public Command EnhancePhotoCommand { get; private set; } = null!;
    public Command CancelPhotoCommand { get; private set; } = null!;
    public Command OpenPhotoResultCommand { get; private set; } = null!;
    public Command ShowPhotoResultCommand { get; private set; } = null!;
    public Command SelectPhotoPresetCommand { get; private set; } = null!;
    public Command SelectPhotoScaleCommand { get; private set; } = null!;
    public Command SelectPhotoModelCommand { get; private set; } = null!;
    public Command SelectPhotoCleanupCommand { get; private set; } = null!;
    private Command? _togglePhotoBefore;
    /// <summary>The whole picture shows the original instead of the result, or back.</summary>
    public Command TogglePhotoBeforeCommand => _togglePhotoBefore ??= new Command(() => PhotoShowBefore = !PhotoShowBefore);

    private PhotoOptions _photoOptions = new();
    private string? _photo, _photoResult, _photoCompare;
    private (double X, double Y) _photoSpot = (0.5, 0.5);
    private bool _isEnhancingPhoto, _photoShowBefore;
    private string _photoStatus = "";
    private CancellationTokenSource? _photoCancel;

    private void InitPhoto()
    {
        if (Runtime is not null) PhotoEnhancer ??= new PhotoRunner(Runtime, () => (Eng.RifeGpu, Eng.GpuClass));
        ChoosePhotoCommand = new Command(() => { if (_host.PickImageFile() is { } f) SetPhoto(f); }, () => !IsEnhancingPhoto);
        EnhancePhotoCommand = new Command(() => _ = EnhancePhotoAsync(), () => CanEnhancePhoto);
        CancelPhotoCommand = new Command(() => _photoCancel?.Cancel(), () => IsEnhancingPhoto);
        OpenPhotoResultCommand = new Command(() => { if (_photoResult is { } r && File.Exists(r)) _host.OpenExternal(r); }, () => HasPhotoResult);
        ShowPhotoResultCommand = new Command(() => { if (_photoResult is { } r) _host.OpenExternal(Path.GetDirectoryName(r)!); }, () => HasPhotoResult);
        SelectPhotoPresetCommand = new Command(p => { if (p is OptionItem o) { _photoOptions = _photoOptions with { Preset = o.Value }; SyncPhoto(); } });
        SelectPhotoScaleCommand = new Command(p => { if (p is OptionItem o && int.TryParse(o.Value, out var s)) { _photoOptions = _photoOptions with { Scale = s }; SyncPhoto(); } });
        SelectPhotoModelCommand = new Command(p => { if (p is OptionItem o) { _photoOptions = _photoOptions with { MaxDetail = o.Value == "max" }; SyncPhoto(); } });
        SelectPhotoCleanupCommand = new Command(p => { if (p is OptionItem o) { _photoOptions = _photoOptions with { Clean = o.Value }; SyncPhoto(); } });
        SyncPhoto();
    }

    private void SyncPhoto()
    {
        foreach (var o in PhotoPresets) o.IsSelected = o.Value == _photoOptions.Preset;
        foreach (var o in PhotoScales) o.IsSelected = o.Value == _photoOptions.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var o in PhotoModels) o.IsSelected = o.Value == (_photoOptions.MaxDetail ? "max" : "standard");
        foreach (var o in PhotoCleanups) o.IsSelected = o.Value == _photoOptions.Clean;
        Raise(nameof(PhotoOptions), nameof(PhotoModelNote), nameof(PhotoCleanupEnabled));
        SyncPresets();
    }

    public PhotoOptions PhotoOptions => _photoOptions;

    public bool PhotoFixCompression
    {
        get => _photoOptions.FixCompression;
        set { if (_photoOptions.FixCompression != value) { _photoOptions = _photoOptions with { FixCompression = value }; Raise(nameof(PhotoFixCompression)); SyncPresets(); } }
    }

    /// <summary>Max detail uses its own fixed clean-up, so the choice only applies to Standard.</summary>
    public bool PhotoCleanupEnabled => !_photoOptions.MaxDetail;

    public string PhotoModelNote => _photoOptions.MaxDetail && !Photo.HasMaxDetail(Runtime)
        ? "Max detail needs its models: run setup-enhance.cmd again once they're published. Until then the standard ones are used."
        : Photo.Models.First(c => c.Value == (_photoOptions.MaxDetail ? "max" : "standard")).Description;

    public bool PhotoReady => Photo.IsReady(Runtime);
    public bool PhotoNeedsSetup => !PhotoReady;

    public string? PhotoPath => _photo;
    public string PhotoName => _photo is { } p ? Path.GetFileName(p) : "No picture chosen";
    public bool HasPhoto => _photo is not null;

    /// <summary>A picture chosen or dropped on the card.</summary>
    public void SetPhoto(string path)
    {
        if (IsEnhancingPhoto) return;
        if (!Animate.IsImage(path) || !File.Exists(path)) { ShowToast("Choose a picture: PNG, JPEG, WebP, BMP or TIFF."); return; }
        _photo = path;
        _photoSize = ImageSize.Read(path);
        _photoResult = null;
        _photoCompare = null;
        _photoSpot = (0.5, 0.5);
        _photoShowBefore = false;
        PhotoStatus = "";
        RaisePhoto();
    }

    public bool IsEnhancingPhoto { get => _isEnhancingPhoto; private set { if (Set(ref _isEnhancingPhoto, value)) RaisePhoto(); } }
    public string PhotoStatus { get => _photoStatus; private set => Set(ref _photoStatus, value); }
    public bool HasPhotoResult => _photoResult is not null && !IsEnhancingPhoto;
    public string PhotoResultName => _photoResult is { } r ? Path.GetFileName(r) : "";
    public bool CanEnhancePhoto => PhotoReady && HasPhoto && !IsEnhancingPhoto && PhotoEnhancer is not null;

    /// <summary>The whole picture: the result once there is one (click it to choose where to compare), else the original.</summary>
    public string? PhotoShown => _photoResult is { } r && !_photoShowBefore ? r : _photo;
    public bool PhotoShowBefore
    {
        get => _photoShowBefore;
        set { if (_photoShowBefore != value) { _photoShowBefore = value; Raise(nameof(PhotoShowBefore), nameof(PhotoShown), nameof(PhotoShownLabel)); } }
    }
    public string PhotoShownLabel => _photoResult is null ? "Original" : _photoShowBefore ? "Whole picture · original" : "Whole picture · enhanced";

    /// <summary>Before | after of one part at 100%: shrunk to fit, a whole picture hides what enhancing changed.</summary>
    public string? PhotoCompare => _photoCompare;
    public bool HasPhotoCompare => _photoCompare is not null && !IsEnhancingPhoto;
    public (double X, double Y) PhotoSpot => _photoSpot;

    /// <summary>Compare another part: x and y are fractions of the picture's width and height.</summary>
    public async Task SetPhotoSpotAsync(double x, double y)
    {
        if (_photoResult is not { } result || _photo is not { } image || PhotoEnhancer is not { } runner || IsEnhancingPhoto) return;
        _photoSpot = (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        if (Runtime is null) return;
        var compare = Photo.ComparePath(Runtime);
        try
        {
            await runner.CompareAsync(image, result, compare, _photoSpot, CancellationToken.None);
            var old = _photoCompare;
            _photoCompare = compare;
            Raise(nameof(PhotoCompare), nameof(HasPhotoCompare));
            try { if (old is not null && File.Exists(old)) File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            PhotoStatus = ex.Message;
        }
    }

    private void RaisePhoto()
    {
        Raise(nameof(PhotoPath), nameof(PhotoName), nameof(HasPhoto), nameof(HasPhotoResult), nameof(PhotoResultName), nameof(CanEnhancePhoto),
              nameof(PhotoReady), nameof(PhotoNeedsSetup), nameof(PhotoShown), nameof(PhotoShowBefore), nameof(PhotoShownLabel), nameof(PhotoModelNote),
              nameof(PhotoCompare), nameof(HasPhotoCompare));
        EnhancePhotoCommand?.Refresh(); CancelPhotoCommand?.Refresh(); ChoosePhotoCommand?.Refresh();
        OpenPhotoResultCommand?.Refresh(); ShowPhotoResultCommand?.Refresh();
    }

    /// <summary>Checks again whether setup-enhance.cmd has run: when the Enhance page opens.</summary>
    public void RefreshPhoto() => RaisePhoto();

    private async Task EnhancePhotoAsync()
    {
        if (!CanEnhancePhoto || _photo is not { } image || PhotoEnhancer is not { } runner) return;
        var options = _photoOptions;
        string output;
        try
        {
            Directory.CreateDirectory(PhotoFolder);
            output = Photo.OutputPath(image, options, PhotoFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PhotoStatus = "Couldn't create the folder for the picture: " + ex.Message;
            return;
        }
        _photoCancel = new CancellationTokenSource();
        _photoResult = null;
        _photoCompare = null;
        var compare = Runtime is not null ? Photo.ComparePath(Runtime) : null;
        _photoShowBefore = false;
        PhotoStatus = "Starting…";
        IsEnhancingPhoto = true;
        var started = DateTime.UtcNow;
        var stage = new Progress<string>(s => _host.Post(() => PhotoStatus = s));
        try
        {
            await runner.RunAsync(image, output, options, stage, _photoCancel.Token, compare, _photoSpot);
            _photoResult = output;
            _photoCompare = compare is not null && File.Exists(compare) ? compare : null;
            PhotoStatus = $"Saved in {PhotoFolder} ({(DateTime.UtcNow - started).TotalSeconds:0} s)";
        }
        catch (OperationCanceledException)
        {
            PhotoStatus = "Cancelled";
            try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            PhotoStatus = ex.Message;
        }
        finally
        {
            _photoCancel.Dispose();
            _photoCancel = null;
            IsEnhancingPhoto = false;
            RaisePhoto();
        }
    }
}
