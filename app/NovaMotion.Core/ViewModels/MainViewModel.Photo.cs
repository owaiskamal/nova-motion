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
        SelectPhotoModelCommand = new Command(p => { if (p is OptionItem o) { _photoOptions = _photoOptions with { MaxDetail = o.Value == "max", Generative = o.Value == "generative" }; SyncPhoto(); } });
        SelectPhotoCleanupCommand = new Command(p => { if (p is OptionItem o) { _photoOptions = _photoOptions with { Clean = o.Value }; SyncPhoto(); } });
        ClearPhotoPartCommand = new Command(() => PhotoPart = null, () => HasPhotoPart);
        SyncPhoto();
    }

    private void SyncPhoto()
    {
        foreach (var o in PhotoPresets) o.IsSelected = o.Value == _photoOptions.Preset;
        foreach (var o in PhotoScales) o.IsSelected = o.Value == _photoOptions.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var o in PhotoModels) o.IsSelected = o.Value == _photoOptions.Model;
        foreach (var o in PhotoCleanups) o.IsSelected = o.Value == _photoOptions.Clean;
        Raise(nameof(PhotoOptions), nameof(PhotoModelNote), nameof(PhotoCleanupEnabled), nameof(PhotoRestore), nameof(PhotoRestoreEnabled), nameof(CanEnhancePhoto),
              nameof(PhotoGenerativeChosen), nameof(PhotoGenerativeAmount), nameof(PhotoGenerativeAmountText),
              nameof(PhotoModelMissing), nameof(PhotoModelMissingPart));
        SyncPresets();
    }

    public PhotoOptions PhotoOptions => _photoOptions;

    public bool PhotoFixCompression
    {
        get => _photoOptions.FixCompression;
        set { if (_photoOptions.FixCompression != value) { _photoOptions = _photoOptions with { FixCompression = value }; Raise(nameof(PhotoFixCompression)); SyncPresets(); } }
    }

    /// <summary>Remove noise and blur (NAFNet), at its usual amount.</summary>
    public bool PhotoRestore
    {
        get => _photoOptions.Restore > 0;
        set
        {
            if (PhotoRestore == value) return;
            _photoOptions = _photoOptions with { Restore = value ? Photo.RestoreAmount : 0 };
            Raise(nameof(PhotoRestore), nameof(PhotoModelNote), nameof(PhotoModelMissing), nameof(PhotoModelMissingPart));
            SyncPresets();
        }
    }

    /// <summary>Max detail uses its own fixed clean-up, so the choice only applies to Standard.</summary>
    public bool PhotoCleanupEnabled => !_photoOptions.MaxDetail && !_photoOptions.Generative;
    /// <summary>Generative restores the picture itself.</summary>
    public bool PhotoRestoreEnabled => !_photoOptions.Generative;
    /// <summary>Generative is chosen and can run here.</summary>
    private bool GenerativeUsable => Photo.HasGenerative(Runtime) && Photo.GenerateVramGb(Runtime) is 0 or >= 3.5;

    /// <summary>Generative's Strength: how much of the generated picture is kept, 0.5..1.</summary>
    public double PhotoGenerativeAmount
    {
        get => _photoOptions.Amount;
        set
        {
            var v = Math.Round(Math.Clamp(value, 0.5, 1), 2);
            if (Math.Abs(_photoOptions.Amount - v) < 0.001) return;
            _photoOptions = _photoOptions with { Amount = v };
            Raise(nameof(PhotoGenerativeAmount), nameof(PhotoGenerativeAmountText));
            SyncPresets();
        }
    }
    public string PhotoGenerativeAmountText => $"{_photoOptions.Amount * 100:0}%";
    public bool PhotoGenerativeChosen => _photoOptions.Generative;

    private PhotoPart? _photoPart;
    /// <summary>Generative only for this part of the photo (dragged on the picture), or the whole photo.</summary>
    public PhotoPart? PhotoPart
    {
        get => _photoPart;
        set
        {
            _photoPart = value;
            Raise(nameof(PhotoPart), nameof(HasPhotoPart), nameof(PhotoPartText), nameof(EtaText), nameof(PhotoEtaText));
            ClearPhotoPartCommand?.Refresh();
        }
    }
    public bool HasPhotoPart => _photoPart is not null;
    public string PhotoPartText => _photoPart is { } p && _photoSize is { } sz
        ? $"Only the chosen part: {(int)((p.X1 - p.X0) * sz.W)} × {(int)((p.Y1 - p.Y0) * sz.H)} px"
        : "Drag on the picture to generate only a part (much quicker)";
    public Command ClearPhotoPartCommand { get; private set; } = null!;

    /// <summary>The chosen photo model needs a part that isn't installed: the page offers to get it.</summary>
    public bool PhotoModelMissing => (_photoOptions.Generative && !Photo.HasGenerative(Runtime))
                                     || (_photoOptions.MaxDetail && !Photo.HasMaxDetail(Runtime))
                                     || (_photoOptions.Restore > 0 && !Photo.HasRestore(Runtime));
    public string PhotoModelMissingPart => _photoOptions.Generative && !Photo.HasGenerative(Runtime) ? AiParts.Generative : AiParts.Enhance;

    public string PhotoModelNote =>
        _photoOptions.Generative && !Photo.HasGenerative(Runtime)
            ? "Generative isn't installed yet: get it on the AI Models page (about 5 GB, once; an NVIDIA card with 4 GB or more)."
        : _photoOptions.Generative && !GenerativeUsable
            ? $"Generative needs an NVIDIA card with 4 GB or more; this one has {Photo.GenerateVramGb(Runtime):0.#} GB. Max detail works on it."
        : _photoOptions.Generative && Photo.GenerateVramGb(Runtime) is > 0 and < 7.5
            ? "Generative: SeedVR2 makes new detail that fits the picture. On this card about 1.5 minutes a megapixel (a 12 MP photo about 20 minutes): drag on the picture to do just a face or a subject."
        : _photoOptions.Generative
            ? "Generative: SeedVR2 looks at the whole picture and makes new detail that fits it. It cleans the picture itself, so the other choices don't apply."
        : _photoOptions.Restore > 0 && !Photo.HasRestore(Runtime)
            ? "Remove noise and blur needs an update of the Enhance models: get it on the AI Models page. Until then the photo is enhanced without it."
        : _photoOptions.MaxDetail && !Photo.HasMaxDetail(Runtime)
            ? "Max detail needs an update of the Enhance models: get it on the AI Models page. Until then the standard ones are used."
        : _photoOptions.Restore > 0 && _photoOptions.Scale <= 1
            ? "At the same size the photo is cleaned and sharpened by Remove noise and blur; the model choice applies when enlarging."
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
        PhotoPart = null;
        PhotoStatus = "";
        RaisePhoto();
    }

    public bool IsEnhancingPhoto { get => _isEnhancingPhoto; private set { if (Set(ref _isEnhancingPhoto, value)) RaisePhoto(); } }

    private DateTime _photoStarted;
    private double _photoDone, _photoShown;
    private TimeSpan _photoEstimate;

    /// <summary>0..1: the strips done (the photo is enhanced in strips); before the first, a little by the
    /// estimate while the models load. Never goes back.</summary>
    public double PhotoProgress => _photoShown;
    /// <summary>"40% · about 1 min left"</summary>
    public string PhotoProgressText { get; private set; } = "";

    /// <summary>On each strip, and every half second from Tick.</summary>
    private void UpdatePhotoProgress()
    {
        if (!IsEnhancingPhoto) return;
        var spent = _clock() - _photoStarted;
        var byTime = _photoEstimate.TotalSeconds > 0 ? Math.Min(0.08, spent.TotalSeconds / _photoEstimate.TotalSeconds * 0.08) : 0;
        _photoShown = Math.Max(_photoShown, Math.Max(_photoDone * 0.98, byTime));
        TimeSpan left;
        if (_photoDone > 0.05)
            left = TimeSpan.FromSeconds(spent.TotalSeconds * (1 - _photoDone) / _photoDone);
        else
            left = _photoEstimate - spent;
        PhotoProgressText = $"{Math.Floor(_photoShown * 100):0}%" + (left.TotalSeconds > 5 ? $" · {Estimate.Words(left)} left" : _photoDone >= 1 ? " · saving" : " · nearly done");
        Raise(nameof(PhotoProgress), nameof(PhotoProgressText));
    }
    public string PhotoStatus { get => _photoStatus; private set => Set(ref _photoStatus, value); }
    public bool HasPhotoResult => _photoResult is not null && !IsEnhancingPhoto;
    public string PhotoResultName => _photoResult is { } r ? Path.GetFileName(r) : "";
    public bool CanEnhancePhoto => PhotoReady && HasPhoto && !IsEnhancingPhoto && PhotoEnhancer is not null
                                   && (!_photoOptions.Generative || GenerativeUsable);

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
              nameof(PhotoReady), nameof(PhotoNeedsSetup), nameof(PhotoShown), nameof(PhotoShowBefore), nameof(PhotoShownLabel), nameof(PhotoModelNote), nameof(PhotoModelMissing), nameof(PhotoModelMissingPart),
              nameof(PhotoCompare), nameof(HasPhotoCompare));
        EnhancePhotoCommand?.Refresh(); CancelPhotoCommand?.Refresh(); ChoosePhotoCommand?.Refresh();
        OpenPhotoResultCommand?.Refresh(); ShowPhotoResultCommand?.Refresh();
    }

    /// <summary>Checks again whether setup-enhance.cmd has run: when the Enhance page opens.</summary>
    public void RefreshPhoto() => RaisePhoto();

    private async Task EnhancePhotoAsync()
    {
        if (!CanEnhancePhoto || _photo is not { } image || PhotoEnhancer is not { } runner) return;
        var options = _photoOptions.Generative ? _photoOptions with { Part = _photoPart } : _photoOptions;
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
        _photoStarted = _clock();
        _photoDone = 0;
        _photoShown = 0;
        _photoEstimate = _photoSize is { } sz ? Estimate.Photo(sz.W, sz.H, options, Eng.GpuClass, _settings.EstimateCalibration) : TimeSpan.FromMinutes(1);
        UpdatePhotoProgress();
        var stage = new Progress<string>(s => _host.Post(() => PhotoStatus = s));
        var done = new Progress<double>(f => _host.Post(() => { _photoDone = f; UpdatePhotoProgress(); }));
        try
        {
            await runner.RunAsync(image, output, options, stage, _photoCancel.Token, compare, _photoSpot, done);
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
