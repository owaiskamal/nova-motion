// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Collections.ObjectModel;

namespace NovaMotion.Core.ViewModels;

internal sealed class InlineProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}

public enum ExportStatus { Waiting, Running, Done, Failed, Cancelled }

/// <summary>One video in the export list.</summary>
public sealed class ExportItem : Observable
{
    private ExportStatus _status;
    private double _progress;
    private string _detail = "Waiting";

    public ExportItem(string input, string output, ExportOptions options)
    {
        Input = input; Output = output; Options = options;
        Title = MediaSource.GuessTitle(input);
        Summary = $"{EngineCatalog.TargetName(options.Target)} · {(options.Quality == ExportQuality.Best ? "Best quality" : "Faster")}"
                + (options.Comparison ? " · before/after" : "")
                + (options.StartSeconds is > 0 || options.LengthSeconds is > 0
                    ? $" · from {TimeFormat.Format(options.StartSeconds ?? 0)}" + (options.LengthSeconds is { } l ? $" for {l:0.#} s" : "")
                    : "");
    }

    public string Input { get; }
    public string Output { get; }
    public ExportOptions Options { get; }
    public string Title { get; }
    public string Summary { get; }
    public string OutputName => Path.GetFileName(Output);
    internal CancellationTokenSource? Cancel { get; set; }
    internal DateTime? StartedAt { get; set; }

    public ExportStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) Raise(nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(CanCancel), nameof(CanRemove)); }
    }
    public double Progress { get => _progress; set { if (Set(ref _progress, value)) Raise(nameof(PercentText)); } }
    public string PercentText => $"{Progress:P0}";
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public bool IsRunning => Status == ExportStatus.Running;
    public bool IsDone => Status == ExportStatus.Done;
    public bool IsFailed => Status is ExportStatus.Failed or ExportStatus.Cancelled;
    public bool CanCancel => Status is ExportStatus.Waiting or ExportStatus.Running;
    public bool CanRemove => !CanCancel;
}

/// <summary>Enhance &amp; Export: render smooth copies of files with the best model, no time limit per frame.</summary>
public sealed partial class MainViewModel
{
    private readonly IExportRunner? _exporter;
    private bool _exportLoopRunning;

    public ObservableCollection<ExportItem> Exports { get; } = new();
    public ObservableCollection<OptionItem> ExportTargets { get; } = new();
    public bool HasExports => Exports.Count > 0;
    public bool CanExport => _exporter is not null;

    private bool _isExportOpen;
    public bool IsExportOpen { get => _isExportOpen; set { if (Set(ref _isExportOpen, value)) RaiseModule(); } }

    private ExportQuality _exportQuality = ExportQuality.Best;
    public bool ExportBest { get => _exportQuality == ExportQuality.Best; set { _exportQuality = value ? ExportQuality.Best : ExportQuality.Faster; Raise(); Raise(nameof(ExportFaster)); } }
    public bool ExportFaster { get => !ExportBest; set => ExportBest = !value; }

    private bool _exportGpuEncoder = true;
    public bool ExportGpuEncoder { get => _exportGpuEncoder; set => Set(ref _exportGpuEncoder, value); }

    private bool _exportComparison;
    /// <summary>Export a before/after video: the original and NOVA Motion side by side, labelled.</summary>
    public bool ExportComparison { get => _exportComparison; set => Set(ref _exportComparison, value); }
    private string _exportStartText = "";
    /// <summary>Only part of each video, from this time ("3:15"); empty = from the start.</summary>
    public string ExportStartText { get => _exportStartText; set { if (Set(ref _exportStartText, value)) Raise(nameof(ExportRangeProblem)); } }
    private string _exportLengthText = "";
    /// <summary>…for this many seconds ("20"); empty = to the end.</summary>
    public string ExportLengthText { get => _exportLengthText; set { if (Set(ref _exportLengthText, value)) Raise(nameof(ExportRangeProblem)); } }
    public string ExportRangeProblem =>
        (ExportStartText.Length > 0 && ExportOptions.ParseTime(ExportStartText) is null) || (ExportLengthText.Length > 0 && ExportOptions.ParseTime(ExportLengthText) is not > 0)
            ? "Write times as 3:15 or 195, and the length in seconds." : "";

    /// <summary>What the export will use, in a sentence.</summary>
    public string ExportEngineText => Availability.TensorRt
        ? "RIFE on TensorRT, with the newest model installed. Fast on RTX cards."
        : Availability.RifeModels > 0
            ? "RIFE on Vulkan. Works on any modern GPU; slower than playback, but every frame gets full quality."
            : "MVTools. Install RIFE (build.cmd) for AI quality.";

    public Command OpenExportCommand { get; private set; } = null!;
    public Command CloseExportCommand { get; private set; } = null!;
    public Command AddExportFilesCommand { get; private set; } = null!;
    public Command ExportCurrentCommand { get; private set; } = null!;
    public Command SelectExportTargetCommand { get; private set; } = null!;
    public Command CancelExportCommand { get; private set; } = null!;
    public Command RemoveExportCommand { get; private set; } = null!;
    public Command ShowExportCommand { get; private set; } = null!;
    public Command ClearFinishedExportsCommand { get; private set; } = null!;

    private void InitExport()
    {
        foreach (var t in ExportPlan.Targets)
            ExportTargets.Add(new OptionItem(new Choice(t, EngineCatalog.TargetName(t))) { IsSelected = t == "60" });
        OpenExportCommand = new Command(() => SelectModule(StudioModule.Enhance));
        CloseExportCommand = new Command(() => SelectModule(StudioModule.Motion));
        AddExportFilesCommand = new Command(() => { if (_host.PickVideoFiles() is { Count: > 0 } f) QueueExports(f); });
        ExportCurrentCommand = new Command(() =>
        {
            if (_current is null || MediaSource.IsOnline(_current)) { ShowToast("Export works on video files on this PC."); return; }
            QueueExports(new[] { _current });
            ShowToast("Added to Enhance", "Open", () => SelectModule(StudioModule.Enhance));
        });
        SelectExportTargetCommand = new Command(p =>
        {
            if (p is not OptionItem o) return;
            foreach (var t in ExportTargets) t.IsSelected = t == o;
        });
        CancelExportCommand = new Command(p =>
        {
            if (p is not ExportItem e) return;
            if (e.Status == ExportStatus.Waiting) { e.Status = ExportStatus.Cancelled; e.Detail = "Cancelled"; }
            else e.Cancel?.Cancel();
        });
        RemoveExportCommand = new Command(p => { if (p is ExportItem { CanRemove: true } e) { Exports.Remove(e); Raise(nameof(HasExports)); } });
        ShowExportCommand = new Command(p => { if (p is ExportItem { IsDone: true } e) _host.OpenExternal(Path.GetDirectoryName(e.Output)!); });
        ClearFinishedExportsCommand = new Command(() =>
        {
            foreach (var e in Exports.Where(x => x.CanRemove).ToList()) Exports.Remove(e);
            Raise(nameof(HasExports));
        });
    }

    /// <summary>Adds files to the export list with the current options and starts working through it.</summary>
    public void QueueExports(IEnumerable<string> files)
    {
        if (_exporter is null) { ShowToast("The player files are missing. Run build.cmd once."); return; }
        var options = new ExportOptions
        {
            Target = ExportTargets.FirstOrDefault(t => t.IsSelected)?.Value ?? "60",
            Quality = _exportQuality,
            HardwareEncoder = _exportGpuEncoder && _gpu?.IsNvidia != false,
            Comparison = _exportComparison,
            StartSeconds = ExportOptions.ParseTime(ExportStartText),
            LengthSeconds = ExportOptions.ParseTime(ExportLengthText),
        };
        var taken = Exports.Select(e => e.Output).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files.Where(f => !MediaSource.IsOnline(f)))
        {
            var output = ExportPlan.OutputPath(f, options, p => File.Exists(p) || taken.Contains(p));
            taken.Add(output);
            Exports.Add(new ExportItem(f, output, options));
        }
        Raise(nameof(HasExports));
        _ = RunExportsAsync();
    }

    private async Task RunExportsAsync()
    {
        if (_exportLoopRunning || _exporter is null) return;
        _exportLoopRunning = true;
        try
        {
            while (Exports.FirstOrDefault(e => e.Status == ExportStatus.Waiting) is { } item)
            {
                item.Status = ExportStatus.Running;
                item.Detail = "Starting…";
                item.StartedAt = _clock();
                item.Cancel = new CancellationTokenSource();
                var engine = ExportPlan.Engine(Eng, item.Options, Availability, Runtime?.RifeModels() ?? Array.Empty<string>());
                // Reported straight to the UI thread (Progress<T> would queue reports, and a late
                // one could overwrite the final status)
                var progress = new InlineProgress(p => _host.Post(() =>
                {
                    if (item.Status != ExportStatus.Running) return;
                    item.Progress = p;
                    if (item.StartedAt is { } s) item.Detail = ExportPlan.Remaining(p, _clock() - s);
                }));
                string? error;
                try { error = await _exporter.RunAsync(item.Input, item.Output, engine, item.Options, progress, item.Cancel.Token); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
                {
                    error = ex.Message;
                }
                _host.Post(() =>
                {
                    if (error is null)
                    {
                        item.Status = ExportStatus.Done;
                        item.Progress = 1;
                        item.Detail = "Saved as " + item.OutputName;
                        if (!IsExportOpen) ShowToast("Export finished: " + item.OutputName, "Show", () => _host.OpenExternal(Path.GetDirectoryName(item.Output)!));
                    }
                    else
                    {
                        item.Status = error == "Cancelled" ? ExportStatus.Cancelled : ExportStatus.Failed;
                        item.Detail = error;
                    }
                });
            }
        }
        finally { _exportLoopRunning = false; }
    }
}
