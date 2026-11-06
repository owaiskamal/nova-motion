// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
namespace NovaMotion.Core.ViewModels;

/// <summary>
/// Activity: everything working in the background (an AI model being prepared, exports, a photo), in
/// one place. The sidebar shows a summary with a progress ring; clicking it opens the panel.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _isActivityOpen;
    private Command? _toggleActivity, _closeActivity;

    public bool IsActivityOpen { get => _isActivityOpen; set => Set(ref _isActivityOpen, value); }

    private Command? _toggleSidebar;
    /// <summary>The sidebar as icons only (a mini sidebar), remembered.</summary>
    public bool IsSidebarCollapsed
    {
        get => _settings.SidebarCollapsed;
        set { if (_settings.SidebarCollapsed == value) return; _settings.SidebarCollapsed = value; _store.Save(_settings); Raise(); Raise(nameof(IsSidebarExpanded)); }
    }
    public bool IsSidebarExpanded => !IsSidebarCollapsed;
    public Command ToggleSidebarCommand => _toggleSidebar ??= new Command(() => IsSidebarCollapsed = !IsSidebarCollapsed);
    public Command ToggleActivityCommand => _toggleActivity ??= new Command(() => IsActivityOpen = !IsActivityOpen);
    public Command CloseActivityCommand => _closeActivity ??= new Command(() => IsActivityOpen = false);

    private ExportItem? RunningExport => Exports.FirstOrDefault(e => e.IsRunning);
    private int WaitingExports => Exports.Count(e => e.Status == ExportStatus.Waiting);

    /// <summary>How many things are working or waiting their turn.</summary>
    public int ActivityCount => (HasJob ? 1 : 0) + (RunningExport is null ? 0 : 1) + WaitingExports + (IsEnhancingPhoto ? 1 : 0);
    public bool HasActivity => ActivityCount > 0;

    /// <summary>"Preparing a model", "Exporting", "3 working".</summary>
    public string ActivityTitle => ActivityCount switch
    {
        0 => "Nothing running",
        1 when HasJob => "Preparing a model",
        1 when RunningExport is not null => "Exporting",
        1 when IsEnhancingPhoto => "Enhancing a photo",
        1 => "1 waiting",
        var n => $"{n} working",
    };

    /// <summary>The line under it: what the busiest thing is doing.</summary>
    public string ActivityDetail =>
        RunningExport is { } e ? $"{e.Title} · {e.Detail}"
        : HasJob ? JobStatus
        : IsEnhancingPhoto ? PhotoStatus
        : WaitingExports > 0 ? "Waiting their turn" : "";

    /// <summary>0..1 for the sidebar's ring: the running export, else the preparation.</summary>
    public double ActivityProgress => RunningExport?.Progress ?? (HasJob ? JobProgress : 0);

    /// <summary>Called from Tick.</summary>
    private void RaiseActivity() =>
        Raise(nameof(ActivityCount), nameof(HasActivity), nameof(ActivityTitle), nameof(ActivityDetail), nameof(ActivityProgress));
}
