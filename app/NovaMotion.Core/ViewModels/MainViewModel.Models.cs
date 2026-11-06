// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Collections.ObjectModel;
using NovaMotion.Core;

namespace NovaMotion.Core.ViewModels;

/// <summary>An IProgress that hands each report on at once, in the order they come.</summary>
internal sealed class Reports<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>One card on the AI Models page.</summary>
public sealed class AiPartItem : Observable
{
    public AiPartItem(AiPart part) => Part = part;

    public AiPart Part { get; }
    public string Id => Part.Id;
    public string Name => Part.Name;
    public string Feature => Part.Feature;
    public string Description => Part.Description;
    public string Size => Part.Size;

    private PartState _state;
    public PartState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
                Raise(nameof(StatusText), nameof(ButtonText), nameof(HasButton), nameof(IsBusy), nameof(IsReady), nameof(IsBlocked),
                      nameof(HasProgress), nameof(IsFailed), nameof(CanCancel));
        }
    }

    private double? _progress;
    /// <summary>0..1 while installing or preparing; null when the step has no measure.</summary>
    public double? Progress
    {
        get => _progress;
        set { if (Set(ref _progress, value)) Raise(nameof(ProgressValue), nameof(IsIndeterminate), nameof(PercentText)); }
    }
    public double ProgressValue => _progress ?? 0;
    public bool IsIndeterminate => IsBusy && _progress is null;
    public string PercentText => _progress is { } p ? $"{Math.Floor(p * 100):0}%" : "";

    private string _detail = "";
    /// <summary>What's happening now ("Enhance models: 120 MB of 550 MB, 8.2 MB/s"), or the reason it can't be used here.</summary>
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    private string _timeLeft = "";
    public string TimeLeft { get => _timeLeft; set => Set(ref _timeLeft, value); }

    /// <summary>A line kept after a run ends ("Stopped. Downloading again picks up where it left off.").</summary>
    public string Note { get; set; } = "";

    private string _error = "";
    public string Error { get => _error; set => Set(ref _error, value); }

    public string StatusText => _state switch
    {
        PartState.NotInstalled => "Not installed",
        PartState.NotForThisPc => "Not for this PC",
        PartState.Waiting => "Waiting",
        PartState.Installing => "Downloading",
        PartState.NeedsPreparing => "Needs preparing for this graphics card",
        PartState.Preparing => "Preparing for this graphics card",
        PartState.Ready => "Ready",
        PartState.UpdateAvailable => "Update available",
        PartState.Failed => "Didn't finish",
        _ => "",
    };

    public string ButtonText => _state switch
    {
        PartState.NotInstalled => "Download",
        PartState.NeedsPreparing => "Prepare",
        PartState.UpdateAvailable => "Update",
        PartState.Failed => "Try again",
        _ => "",
    };

    public bool HasButton => ButtonText.Length > 0;
    public bool IsBusy => _state is PartState.Installing or PartState.Preparing or PartState.Waiting;
    public bool HasProgress => _state is PartState.Installing or PartState.Preparing;
    public bool CanCancel => IsBusy;
    public bool IsReady => _state == PartState.Ready;
    public bool IsBlocked => _state == PartState.NotForThisPc;
    public bool IsFailed => _state == PartState.Failed;
}

/// <summary>The AI Models page: every part NOVA Studio can install, its state, and installing or preparing
/// it inside the app with a real progress bar (no console windows).</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<AiPartItem> AiPartItems { get; } = new();

    /// <summary>Runs the setup scripts and model preparation; a test puts in a stand-in.</summary>
    public IPartRunner? PartRunner { get; set; }

    public Command OpenModelsCommand { get; private set; } = null!;
    public Command CloseModelsCommand { get; private set; } = null!;
    public Command PartActionCommand { get; private set; } = null!;
    public Command CancelPartCommand { get; private set; } = null!;
    public Command ShowPartLogCommand { get; private set; } = null!;

    private readonly Queue<(AiPartItem Item, bool Prepare)> _partQueue = new();
    private CancellationTokenSource? _partCancel;
    private AiPartItem? _partRunning;
    private DateTime _partStarted;
    private double _partStartedAt;

    private bool _isModelsOpen;
    public bool IsModelsOpen
    {
        get => _isModelsOpen;
        set
        {
            if (!Set(ref _isModelsOpen, value)) return;
            if (value) RefreshParts();
            RaiseModule();
        }
    }

    private void InitModels()
    {
        foreach (var p in AiParts.All) AiPartItems.Add(new AiPartItem(p));
        PartRunner ??= Runtime is not null ? new PartRunner(Runtime) : null;
        OpenModelsCommand = new Command(p =>
        {
            IsSettingsOpen = false;
            IsModelsOpen = true;
            if (p is string id) InstallPart(id);
        });
        CloseModelsCommand = new Command(() => IsModelsOpen = false);
        PartActionCommand = new Command(p => { if (p is AiPartItem i) Act(i); });
        CancelPartCommand = new Command(p => { if (p is AiPartItem i) CancelPart(i); });
        ShowPartLogCommand = new Command(() => { if (Runtime is not null) _host.OpenExternal(Path.Combine(Runtime.Root, "logs")); });
        RefreshParts();
    }

    // The parts each page offers in place, right where the feature is
    public AiPartItem? EnhancePart => AiPartItems.FirstOrDefault(i => i.Id == AiParts.Enhance);
    public AiPartItem? AnimatePart => AiPartItems.FirstOrDefault(i => i.Id == AiParts.Animate);
    public AiPartItem? TensorRtPart => AiPartItems.FirstOrDefault(i => i.Id == AiParts.TensorRt);
    public AiPartItem? PhotoMissingPart => AiPartItems.FirstOrDefault(i => i.Id == PhotoModelMissingPart);
    /// <summary>TensorRT can be added on this PC (an NVIDIA card) and isn't yet.</summary>
    public bool TensorRtOffered => TensorRtPart is { } t && t.State is not (PartState.Ready or PartState.NotForThisPc);

    /// <summary>The photo models whose parts aren't installed show a lock.</summary>
    private void LockPhotoModels()
    {
        foreach (var o in PhotoModels)
            o.Locked = o.Value switch { "max" => !Photo.HasMaxDetail(Runtime), "generative" => !Photo.HasGenerative(Runtime), _ => false };
    }

    private PcGraphics Graphics => new(_gpu?.IsNvidia == true, _graphicsGb, _gpu?.Primary ?? "");

    /// <summary>Reads every part's state from disk again (not the ones running or waiting).</summary>
    public void RefreshParts()
    {
        foreach (var i in AiPartItems)
        {
            if (i.IsBusy) continue;
            if (i.State == PartState.Failed && i.Error.Length > 0) continue;
            i.State = AiParts.StateOf(i.Part, Runtime, Graphics);
            i.Detail = i.State switch
            {
                PartState.NotForThisPc => i.Part.WhyNot?.Invoke(Graphics) ?? "",
                PartState.UpdateAvailable => i.Part.UpdateNote,
                PartState.NeedsPreparing => $"{AiParts.UnpreparedModels(Runtime).Count} models, once: about 3 to 6 minutes each on a GTX 16 card, seconds on an RTX card. Without it, the first export with each model waits for it.",
                _ => i.Note,
            };
            i.Progress = null;
            i.TimeLeft = "";
        }
        RaiseParts();
    }

    /// <summary>How many parts this PC can use that aren't ready yet: the badge on the sidebar.</summary>
    public int PartsNeedingAction => AiPartItems.Count(i => i.State is PartState.NotInstalled or PartState.NeedsPreparing or PartState.UpdateAvailable or PartState.Failed);
    public bool HasPartsNeedingAction => PartsNeedingAction > 0;
    public string PartsSummary => AiPartItems.Any(i => i.IsBusy)
        ? "Downloads run one at a time and keep going while you use NOVA Studio."
        : PartsNeedingAction == 0 ? "Everything this PC can use is installed and ready."
        : $"{PartsNeedingAction} to install or prepare. Each feature unlocks as soon as its part is ready.";

    /// <summary>Installs a part (and what it needs first) from anywhere in the app, e.g. a feature's "Download" button.</summary>
    public void InstallPart(string id)
    {
        if (AiPartItems.FirstOrDefault(i => i.Id == id) is not { } item) return;
        if (item.State is PartState.Ready || item.IsBusy) return;
        Act(item);
    }

    private void Act(AiPartItem item)
    {
        if (item.IsBusy || item.State is PartState.NotForThisPc or PartState.Ready) return;
        if (PartRunner is null) { ShowToast("The player files are missing, so nothing can be installed. Run build.cmd once."); return; }
        item.Error = "";
        item.Note = "";
        if (item.State == PartState.NeedsPreparing) { Enqueue(item, prepare: true); return; }
        // What it needs first (Generative needs the Enhance models)
        if (item.Part.Requires is { } need && AiPartItems.FirstOrDefault(i => i.Id == need) is { } first
            && first.State is PartState.NotInstalled or PartState.Failed)
            Enqueue(first, prepare: false);
        Enqueue(item, prepare: false);
    }

    private void Enqueue(AiPartItem item, bool prepare)
    {
        if (item.IsBusy) return;
        item.State = PartState.Waiting;
        item.Detail = prepare ? "Waiting to prepare" : "Waiting to download";
        _partQueue.Enqueue((item, prepare));
        RaiseParts();
        if (_partRunning is null) _ = RunPartsAsync();
    }

    private void CancelPart(AiPartItem item)
    {
        if (item == _partRunning) { _partCancel?.Cancel(); return; }
        var rest = _partQueue.Where(q => q.Item != item).ToList();
        _partQueue.Clear();
        foreach (var q in rest) _partQueue.Enqueue(q);
        item.State = AiParts.StateOf(item.Part, Runtime, Graphics);
        item.Detail = "";
        RaiseParts();
    }

    private async Task RunPartsAsync()
    {
        while (_partQueue.Count > 0)
        {
            var (item, prepare) = _partQueue.Dequeue();
            if (item.State != PartState.Waiting) continue;
            _partRunning = item;
            _partCancel = new CancellationTokenSource();
            _partStarted = _clock();
            _partStartedAt = 0;
            item.State = prepare ? PartState.Preparing : PartState.Installing;
            item.Progress = prepare ? 0 : null;
            item.Detail = prepare ? "Starting…" : "Connecting…";
            item.TimeLeft = "";
            RaiseParts();
            // Straight to the window's thread, in order (Progress<T> may reorder reports on the thread pool)
            var progress = new Reports<PartProgress>(p => _host.Post(() => OnPartProgress(item, p)));
            try
            {
                if (prepare) await PartRunner!.PrepareAsync(progress, _partCancel.Token);
                else await PartRunner!.InstallAsync(item.Part, progress, _partCancel.Token);
                item.Error = "";
                item.State = AiParts.StateOf(item.Part, Runtime, Graphics);
                if (item.State == PartState.NotInstalled)
                    throw new InvalidOperationException("It finished, but its files aren't all there. Try again; details are in runtime\\logs.");
                OnPartInstalled(item, prepare);
            }
            catch (OperationCanceledException)
            {
                item.State = AiParts.StateOf(item.Part, Runtime, Graphics);
                item.Note = prepare ? "Stopped. Models already prepared stay prepared." : "Stopped. Downloading again picks up where it left off.";
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                item.State = PartState.Failed;
                item.Error = ex.Message;
                item.Detail = "";
            }
            finally
            {
                item.Progress = null;
                item.TimeLeft = "";
                _partCancel?.Dispose();
                _partCancel = null;
                _partRunning = null;
            }
            RefreshParts();
            RaiseParts();
        }
    }

    private void OnPartProgress(AiPartItem item, PartProgress p)
    {
        if (!item.IsBusy) return;
        item.Detail = p.Text;
        item.Progress = p.Overall is { } o ? Math.Clamp(o, item.Progress ?? 0, 1) : item.Progress is > 0 ? item.Progress : null;
        // Time left from the bar's speed so far, once it has moved enough to say
        if (p.Overall is { } f && f > 0.03)
        {
            if (_partStartedAt <= 0) { _partStartedAt = f; _partStarted = _clock(); return; }
            var spent = (_clock() - _partStarted).TotalSeconds;
            var done = f - _partStartedAt;
            if (spent > 5 && done > 0.01)
            {
                var left = TimeSpan.FromSeconds(spent * (1 - f) / done);
                item.TimeLeft = left.TotalSeconds < 60 ? "less than a minute left" : $"{Estimate.Words(left)} left";
            }
        }
    }

    private void OnPartInstalled(AiPartItem item, bool prepared)
    {
        // Everything that depends on installed parts looks again
        RefreshSystem();
        RefreshEnhance();
        RefreshPhoto();
        RefreshAnimate();
        if (!prepared && item.Part.Preparable && AiParts.NeedsPreparing(Runtime))
        {
            // Preparing for the card follows a download by itself, so first use never waits for it
            Enqueue(item, prepare: true);
            return;
        }
        if (!prepared && item.Id == AiParts.TensorRt && AiPartItems.FirstOrDefault(i => i.Id == AiParts.Enhance) is { } enhance
            && AiParts.StateOf(enhance.Part, Runtime, Graphics) == PartState.NeedsPreparing)
            Enqueue(enhance, prepare: true);
        ShowToast(prepared ? $"{item.Name}: prepared for this graphics card." : $"{item.Name} is ready.");
    }

    private void RaiseParts()
    {
        LockPhotoModels();
        Raise(nameof(PartsNeedingAction), nameof(HasPartsNeedingAction), nameof(PartsSummary), nameof(TensorRtOffered), nameof(PhotoMissingPart));
    }
}
