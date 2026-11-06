// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NovaMotion.Core;
using NovaMotion.Core.ViewModels;

namespace NovaMotion.App.Views;

public partial class ExportView : UserControl
{
    private Point? _dragFrom;
    private bool _dragging;
    private MainViewModel? _vm;

    public ExportView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as MainViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
            ShowPart();
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.PhotoPart) or nameof(MainViewModel.PhotoGenerativeChosen) or nameof(MainViewModel.PhotoShown))
            ShowPart();
    }

    private void PhotoOverview_SizeChanged(object sender, SizeChangedEventArgs e) => ShowPart();

    /// <summary>The chosen part, drawn over the picture (Generative only).</summary>
    private void ShowPart(PhotoPart? part = null)
    {
        part ??= _vm?.PhotoGenerativeChosen == true ? _vm.PhotoPart : null;
        var w = PhotoOverview.ActualWidth;
        var h = PhotoOverview.ActualHeight;
        if (part is null || w <= 0 || h <= 0) { PartBox.Visibility = Visibility.Collapsed; return; }
        Canvas.SetLeft(PartBox, part.X0 * w);
        Canvas.SetTop(PartBox, part.Y0 * h);
        PartBox.Width = Math.Max(2, (part.X1 - part.X0) * w);
        PartBox.Height = Math.Max(2, (part.Y1 - part.Y0) * h);
        PartBox.Visibility = Visibility.Visible;
    }

    private void PhotoOverview_Down(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.PhotoGenerativeChosen != true || _vm.IsEnhancingPhoto) return;
        _dragFrom = e.GetPosition(PhotoOverview);
        _dragging = false;
        PhotoOverview.CaptureMouse();
    }

    private void PhotoOverview_Move(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(PhotoOverview);
        if (!_dragging && (Math.Abs(p.X - from.X) > 6 || Math.Abs(p.Y - from.Y) > 6)) _dragging = true;
        if (_dragging) ShowPart(Part(from, p));
    }

    private PhotoPart? Part(Point a, Point b)
    {
        var (w, h) = (PhotoOverview.ActualWidth, PhotoOverview.ActualHeight);
        return w <= 0 || h <= 0 ? null : PhotoPart.From(a.X / w, a.Y / h, b.X / w, b.Y / h);
    }

    /// <summary>A drag chooses the part Generative works on; a click compares that part at 100%.</summary>
    private async void PhotoOverview_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not Image img || img.ActualWidth <= 0 || img.ActualHeight <= 0) return;
        var p = e.GetPosition(img);
        if (_dragFrom is { } from)
        {
            img.ReleaseMouseCapture();
            _dragFrom = null;
            if (_dragging)
            {
                _dragging = false;
                vm.PhotoPart = Part(from, p);
                ShowPart();
                return;
            }
        }
        await vm.SetPhotoSpotAsync(p.X / img.ActualWidth, p.Y / img.ActualHeight);
    }
}
