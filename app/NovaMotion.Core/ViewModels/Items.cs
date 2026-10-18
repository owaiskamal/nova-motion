// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
namespace NovaMotion.Core.ViewModels;

public sealed class RecentItem : Observable
{
    public RecentItem(HistoryEntry e, DateTime nowUtc)
    {
        Target = e.Target;
        Title = e.Title;
        Origin = MediaSource.Origin(e.Target);
        IsOnline = MediaSource.IsOnline(e.Target);
        Progress = e.Finished ? 1 : e.Progress;
        Finished = e.Finished;
        When = TimeFormat.Ago(e.LastPlayedUtc, nowUtc);
        Status = e.Finished ? "Watched"
               : e.Duration > 0 && e.Position >= 30 ? $"{TimeFormat.Format(e.Duration - e.Position)} left"
               : e.Duration > 0 ? TimeFormat.Format(e.Duration) : "";
    }

    public string Target { get; }
    public string Title { get; }
    public string Origin { get; }
    public bool IsOnline { get; }
    public double Progress { get; }
    public bool Finished { get; }
    public bool HasProgress => Progress > 0 && !Finished;
    public string When { get; }
    public string Status { get; }
    /// <summary>First letter shown on the tile when there is no picture.</summary>
    public string Initial => Title.FirstOrDefault(char.IsLetterOrDigit) is var c and not '\0' ? char.ToUpperInvariant(c).ToString() : "#";
}

public sealed class TrackItem : Observable
{
    private bool _isSelected;
    public TrackItem(int id, string label, string detail, bool selected) { Id = id; Label = label; Detail = detail; _isSelected = selected; }
    /// <summary>mpv track id; 0 means "off".</summary>
    public int Id { get; }
    public string Label { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class QueueItem : Observable
{
    private bool _isCurrent;
    public QueueItem(int index, string target, string title, bool current)
    {
        Index = index; Target = target; Title = title; _isCurrent = current;
        Origin = MediaSource.Origin(target);
    }
    public int Index { get; }
    public int Number => Index + 1;
    public string Target { get; }
    public string Title { get; }
    public string Origin { get; }
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
}

/// <summary>One bar of the frame-cadence strip: a real source frame, or one NOVA Motion made.</summary>
public sealed record CadenceTick(bool Generated);

public sealed record ShortcutItem(string Keys, string Description);

public sealed record StatItem(string Label, string Value);

public sealed record EngineSizeItem(string Label, bool Ready)
{
    public string Status => Ready ? "Ready" : "Builds on first play";
}

public sealed record BenchmarkItem(string Engine, string Result, double Fraction, bool RealTime);
