// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Text.Json;

namespace NovaMotion.Core;

public sealed class HistoryEntry
{
    public string Target { get; set; } = "";
    public string Title { get; set; } = "";
    public double Position { get; set; }
    public double Duration { get; set; }
    public DateTime LastPlayedUtc { get; set; }

    public bool Finished => Duration > 0 && Position >= Duration - FinishedMargin(Duration);
    public double Progress => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;

    /// <summary>The last stretch counts as watched: credits, or the last 3% of short clips.</summary>
    internal static double FinishedMargin(double duration) => Math.Min(60, Math.Max(5, duration * 0.03));
}

/// <summary>Recently played files and links, with where you stopped (history.json).</summary>
public sealed class MediaHistory
{
    public const int MaxEntries = 60;
    private readonly string _path;
    private readonly List<HistoryEntry> _items;

    public MediaHistory(string directory)
    {
        _path = Path.Combine(directory, "history.json");
        _items = Load(_path);
    }

    public IReadOnlyList<HistoryEntry> Items => _items;

    /// <summary>Where to resume, or null to start from the beginning.</summary>
    public double? ResumePosition(string target)
    {
        var e = Find(target);
        if (e is null || e.Finished || e.Position < 30) return null;
        return Math.Max(0, e.Position - 3);   // a few seconds back, so you see where you were
    }

    public HistoryEntry? Find(string target) =>
        _items.FirstOrDefault(i => string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds or updates an entry and moves it to the top.</summary>
    public HistoryEntry Record(string target, string? title, double position, double duration, DateTime nowUtc)
    {
        var e = Find(target);
        if (e is null) { e = new HistoryEntry { Target = target }; }
        else _items.Remove(e);
        if (!string.IsNullOrWhiteSpace(title)) e.Title = title!;
        if (string.IsNullOrWhiteSpace(e.Title)) e.Title = MediaSource.GuessTitle(target);
        if (duration > 0) e.Duration = duration;
        if (position >= 0) e.Position = position;
        e.LastPlayedUtc = nowUtc;
        _items.Insert(0, e);
        if (_items.Count > MaxEntries) _items.RemoveRange(MaxEntries, _items.Count - MaxEntries);
        return e;
    }

    public bool Remove(string target)
    {
        var e = Find(target);
        return e is not null && _items.Remove(e);
    }

    public void Clear() => _items.Clear();

    public void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Json.Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException) { /* history is a convenience; never fail playback over it */ }
        catch (UnauthorizedAccessException) { }
    }

    private static List<HistoryEntry> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(path), Json.Options)?
                    .Where(e => !string.IsNullOrWhiteSpace(e.Target)).Take(MaxEntries).ToList() ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }
}

/// <summary>What plays next. The app owns the queue; mpv only ever holds the current item.</summary>
public sealed class PlayQueue
{
    private readonly List<string> _items = new();
    public IReadOnlyList<string> Items => _items;
    public int Index { get; private set; } = -1;
    public string? Current => Index >= 0 && Index < _items.Count ? _items[Index] : null;
    public bool HasNext => Index + 1 < _items.Count;
    public bool HasPrevious => Index > 0;
    public event Action? Changed;

    /// <summary>Replaces the queue and makes the first item current.</summary>
    public void Set(IEnumerable<string> targets)
    {
        _items.Clear();
        _items.AddRange(targets.Where(t => !string.IsNullOrWhiteSpace(t)));
        Index = _items.Count > 0 ? 0 : -1;
        Changed?.Invoke();
    }

    public void Add(IEnumerable<string> targets)
    {
        _items.AddRange(targets.Where(t => !string.IsNullOrWhiteSpace(t)));
        if (Index < 0 && _items.Count > 0) Index = 0;
        Changed?.Invoke();
    }

    /// <summary>Puts items right after the current one.</summary>
    public void PlayNext(IEnumerable<string> targets)
    {
        var list = targets.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        _items.InsertRange(Math.Max(0, Index + 1), list);
        if (Index < 0 && _items.Count > 0) Index = 0;
        Changed?.Invoke();
    }

    public string? MoveNext() { if (!HasNext) return null; Index++; Changed?.Invoke(); return Current; }
    public string? MovePrevious() { if (!HasPrevious) return null; Index--; Changed?.Invoke(); return Current; }

    public string? Jump(int index)
    {
        if (index < 0 || index >= _items.Count) return null;
        Index = index; Changed?.Invoke(); return Current;
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _items.RemoveAt(index);
        if (index < Index) Index--;
        else if (index == Index && Index >= _items.Count) Index = _items.Count - 1;
        Changed?.Invoke();
    }

    public void Move(int from, int to)
    {
        if (from < 0 || from >= _items.Count || to < 0 || to >= _items.Count || from == to) return;
        var item = _items[from];
        _items.RemoveAt(from);
        _items.Insert(to, item);
        if (Index == from) Index = to;
        else if (from < Index && to >= Index) Index--;
        else if (from > Index && to <= Index) Index++;
        Changed?.Invoke();
    }

    public void Clear() { _items.Clear(); Index = -1; Changed?.Invoke(); }

    /// <summary>The other videos in the same folder, in name order, starting at <paramref name="file"/>.</summary>
    public static IReadOnlyList<string> FolderSequence(string file)
    {
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return new[] { file };
            var files = Directory.GetFiles(dir).Where(MediaSource.IsVideoFile)
                .OrderBy(f => f, NaturalComparer.Instance).ToList();
            var i = files.FindIndex(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? new[] { file } : files.Skip(i).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new[] { file }; }
    }
}

/// <summary>"Episode 2" before "Episode 10".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (a is null || b is null) return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0'); var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
