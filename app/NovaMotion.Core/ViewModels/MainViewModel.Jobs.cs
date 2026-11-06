// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Diagnostics;

namespace NovaMotion.Core.ViewModels;

/// <summary>AI preparation in progress (TensorRT building engines), shown on every page with a
/// progress bar, and Enhance's Prepare all models, run inside the app.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Where jobs come from; tests replace it.</summary>
    public Func<IReadOnlyList<JobInfo>>? ReadJobs { get; set; }
    /// <summary>Starts a process without a window; tests replace it.</summary>
    public Func<ProcessStartInfo, bool> StartProcess { get; set; } = psi =>
    {
        try { using var p = Process.Start(psi); return p is not null; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    };

    private DateTime _jobsReadAt;
    private JobInfo? _job;
    private int _jobCount;

    /// <summary>A job has run for a couple of seconds: a model that's already prepared loads faster than that and never shows.</summary>
    public bool HasJob => _job is not null && (_clock() - _job.Started).TotalSeconds >= 2;
    public string JobTitle => _job is null ? "" : _job.Title + (_jobCount > 1 ? $" (+{_jobCount - 1} more)" : "");
    public string JobStatus => _job?.Status(_clock()) ?? "";
    public double JobProgress => _job?.Fraction(_clock()) ?? 0;

    private Command? _prepareModels;
    public Command PrepareModelsCommand => _prepareModels ??= new Command(PrepareModels, () => !HasJob);

    /// <summary>Called from Tick: the jobs folder once a second.</summary>
    private void PollJobs()
    {
        var now = _clock();
        RaiseActivity();
        if ((now - _jobsReadAt).TotalSeconds < 1) return;
        _jobsReadAt = now;
        var jobs = (ReadJobs ?? (() => Jobs.Read(Runtime)))();
        var had = HasJob;
        _job = jobs.FirstOrDefault();
        _jobCount = jobs.Count;
        Raise(nameof(HasJob), nameof(JobTitle), nameof(JobStatus), nameof(JobProgress));
        if (had && !HasJob)
        {
            ShowToast("AI preparation finished");
            RefreshEnhance();
        }
        if (had != HasJob) _prepareModels?.Refresh();
    }

    /// <summary>Prepares every Enhance model for this graphics card, in the background, with progress
    /// on every page (engine/enhance_build.py).</summary>
    private void PrepareModels() => OpenModelsCommand.Execute(AiParts.Enhance);
}
