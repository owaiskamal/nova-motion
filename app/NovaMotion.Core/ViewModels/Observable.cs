// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace NovaMotion.Core.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void Raise(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

/// <summary>ICommand over a delegate. Parameter is passed through as object.</summary>
public sealed class Command : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;
    public event EventHandler? CanExecuteChanged;

    public Command(Action<object?> run, Func<object?, bool>? can = null) { _run = run; _can = can; }
    public Command(Action run, Func<bool>? can = null) : this(_ => run(), can is null ? null : _ => can()) { }

    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) _run(parameter); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A selectable option in a list (segmented control, menu, chip).</summary>
public sealed class OptionItem : Observable
{
    private bool _isSelected;
    public OptionItem(Choice choice) => Choice = choice;
    public Choice Choice { get; }
    public string Value => Choice.Value;
    public string Label => Choice.Label;
    public string Description => Choice.Description;
    public bool Available => Choice.Available;
    public string? Badge => Choice.Badge;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}
