// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Collections.ObjectModel;
using System.Globalization;

namespace NovaMotion.Core.ViewModels;

/// <summary>Generate's "Animate a picture": a still picture becomes a short 3D camera move.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Makes the videos; set by the app from the runtime, or by tests.</summary>
    public IAnimateRunner? Animator { get; set; }

    /// <summary>Where finished clips go: Videos\NOVA Studio.</summary>
    public string AnimateFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) is { Length: > 0 } v ? v : Path.GetTempPath(), "NOVA Studio");

    public ObservableCollection<OptionItem> AnimateMoves { get; } = new(Animate.Moves.Select(c => new OptionItem(c)));
    public ObservableCollection<OptionItem> AnimateLengths { get; } = new(Animate.Lengths.Select(c => new OptionItem(c)));
    public ObservableCollection<OptionItem> AnimateStrengths { get; } = new(Animate.Strengths.Select(c => new OptionItem(c)));

    public Command ChooseAnimateImageCommand { get; private set; } = null!;
    public Command StartAnimateCommand { get; private set; } = null!;
    public Command CancelAnimateCommand { get; private set; } = null!;
    public Command PlayAnimateResultCommand { get; private set; } = null!;
    public Command ShowAnimateResultCommand { get; private set; } = null!;
    public Command SetupAnimateCommand { get; private set; } = null!;
    public Command SelectAnimateMoveCommand { get; private set; } = null!;
    public Command SelectAnimateLengthCommand { get; private set; } = null!;
    public Command SelectAnimateStrengthCommand { get; private set; } = null!;

    private CameraMove _animateMove = CameraMove.Push;
    private double _animateSeconds = 6, _animateStrength = 1;
    private bool _animate60;
    private string? _animateImage, _animateResult;
    private bool _isAnimating;
    private double _animateProgress;
    private string _animateStatus = "";
    private CancellationTokenSource? _animateCancel;

    private void InitAnimate()
    {
        if (Runtime is not null) Animator ??= new AnimateRunner(Runtime);
        ChooseAnimateImageCommand = new Command(() => { if (_host.PickImageFile() is { } f) SetAnimateImage(f); }, () => !IsAnimating);
        StartAnimateCommand = new Command(() => _ = StartAnimateAsync(), () => CanAnimate);
        CancelAnimateCommand = new Command(() => _animateCancel?.Cancel(), () => IsAnimating);
        PlayAnimateResultCommand = new Command(() =>
        {
            if (_animateResult is not { } r || !File.Exists(r)) return;
            SelectModule(StudioModule.Motion);
            _ = OpenAsync(new[] { r });
        }, () => HasAnimateResult);
        ShowAnimateResultCommand = new Command(() => { if (_animateResult is { } r) _host.OpenExternal(Path.GetDirectoryName(r)!); }, () => HasAnimateResult);
        SetupAnimateCommand = new Command(() => OpenModelsCommand.Execute(AiParts.Animate));
        SelectAnimateMoveCommand = new Command(p => { if (p is OptionItem o) { _animateMove = Animate.ParseMove(o.Value); SyncAnimate(); } });
        SelectAnimateLengthCommand = new Command(p => { if (p is OptionItem o && TryNumber(o.Value, out var s)) { _animateSeconds = s; SyncAnimate(); } });
        SelectAnimateStrengthCommand = new Command(p => { if (p is OptionItem o && TryNumber(o.Value, out var s)) { _animateStrength = s; SyncAnimate(); } });
        SyncAnimate();
    }

    private static bool TryNumber(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private void SyncAnimate()
    {
        foreach (var o in AnimateMoves) o.IsSelected = o.Value == Animate.MoveName(_animateMove);
        foreach (var o in AnimateLengths) o.IsSelected = TryNumber(o.Value, out var v) && Math.Abs(v - _animateSeconds) < 0.01;
        foreach (var o in AnimateStrengths) o.IsSelected = TryNumber(o.Value, out var v) && Math.Abs(v - _animateStrength) < 0.01;
        Raise(nameof(AnimateMoveNote));
    }

    /// <summary>The depth model and packages are installed (setup-studio.cmd).</summary>
    public bool AnimateReady => Animate.IsReady(Runtime);
    public bool AnimateNeedsSetup => !AnimateReady;
    public string AnimateMoveNote => Animate.Moves.First(c => c.Value == Animate.MoveName(_animateMove)).Description;

    public bool Animate60 { get => _animate60; set => Set(ref _animate60, value); }
    public AnimateOptions AnimateOptions => new(_animateMove, _animateSeconds, _animateStrength, Animate60 ? 60 : 30);

    public string? AnimateImage => _animateImage;
    public string AnimateImageName => _animateImage is { } p ? Path.GetFileName(p) : "No picture chosen";
    public bool HasAnimateImage => _animateImage is not null;

    /// <summary>A picture chosen or dropped on the card.</summary>
    public void SetAnimateImage(string path)
    {
        if (IsAnimating) return;
        if (!Animate.IsImage(path) || !File.Exists(path)) { ShowToast("Choose a picture: PNG, JPEG, WebP, BMP or TIFF."); return; }
        _animateImage = path;
        _animateResult = null;
        AnimateStatus = "";
        RaiseAnimate();
    }

    public bool IsAnimating { get => _isAnimating; private set { if (Set(ref _isAnimating, value)) RaiseAnimate(); } }
    public double AnimateProgress { get => _animateProgress; private set => Set(ref _animateProgress, value); }
    public string AnimateStatus { get => _animateStatus; private set => Set(ref _animateStatus, value); }
    public bool HasAnimateResult => _animateResult is not null && !IsAnimating;
    public string AnimateResultName => _animateResult is { } r ? Path.GetFileName(r) : "";
    public bool CanAnimate => AnimateReady && HasAnimateImage && !IsAnimating && Animator is not null;

    private void RaiseAnimate()
    {
        Raise(nameof(AnimateImage), nameof(AnimateImageName), nameof(HasAnimateImage), nameof(HasAnimateResult), nameof(AnimateResultName),
              nameof(CanAnimate), nameof(AnimateReady), nameof(AnimateNeedsSetup));
        StartAnimateCommand?.Refresh(); CancelAnimateCommand?.Refresh(); ChooseAnimateImageCommand?.Refresh();
        PlayAnimateResultCommand?.Refresh(); ShowAnimateResultCommand?.Refresh();
    }

    /// <summary>Checks again whether setup-studio.cmd has run: when the Generate page opens.</summary>
    public void RefreshAnimate() => RaiseAnimate();

    private async Task StartAnimateAsync()
    {
        if (!CanAnimate || _animateImage is not { } image || Animator is not { } runner) return;
        var options = AnimateOptions;
        string output;
        try
        {
            Directory.CreateDirectory(AnimateFolder);
            output = Animate.OutputPath(image, options.Move, AnimateFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnimateStatus = "Couldn't create the folder for the video: " + ex.Message;
            return;
        }
        _animateCancel = new CancellationTokenSource();
        _animateResult = null;
        AnimateProgress = 0;
        AnimateStatus = "Starting…";
        IsAnimating = true;
        var progress = new Progress<(string Stage, double Progress)>(p => _host.Post(() =>
        {
            AnimateProgress = p.Progress;
            AnimateStatus = $"{p.Stage} {p.Progress:P0}";
        }));
        try
        {
            await runner.RunAsync(image, output, options, progress, _animateCancel.Token);
            _animateResult = output;
            AnimateProgress = 1;
            AnimateStatus = $"Saved in {AnimateFolder}";
        }
        catch (OperationCanceledException)
        {
            AnimateStatus = "Cancelled";
            try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            AnimateStatus = ex.Message;
        }
        finally
        {
            _animateCancel.Dispose();
            _animateCancel = null;
            IsAnimating = false;
            RaiseAnimate();
        }
    }
}
