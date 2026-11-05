// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NovaMotion.Core;
using NovaMotion.Core.ViewModels;

namespace NovaMotion.App;

/// <summary>Player controls over the video: auto-hiding bars, click/double-click/wheel, drawers.</summary>
public partial class PlayerOverlay : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(2.6) };
    private readonly DispatcherTimer _click = new() { Interval = TimeSpan.FromMilliseconds(230) };
    private Point _lastMouse = new(double.NaN, double.NaN);
    private bool _controlsShown = true;

    /// <summary>Raised for files or links dropped on the video.</summary>
    public event Action<DragEventArgs>? Dropped;
    public event Action<KeyEventArgs>? KeyPressed;

    public PlayerOverlay(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        _idle.Tick += (_, _) => { _idle.Stop(); if (CanHide()) HideControls(); };
        _click.Tick += (_, _) => { _click.Stop(); _ = _vm.TogglePauseAsync(); };

        Root.MouseMove += OnMouseMove;
        Root.MouseLeftButtonDown += OnMouseDown;
        Root.MouseWheel += OnWheel;
        Root.MouseLeave += (_, _) => { if (CanHide()) _idle.Start(); };
        Seek.HoverChanged += OnSeekHover;
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        Drop += (_, e) => Dropped?.Invoke(e);
        DragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
        vm.PropertyChanged += OnVmChanged;
        IsVisibleChanged += (_, _) => { if (IsVisible) ShowControls(); };
    }

    // ------------------------------------------------------------------ show / hide

    private bool CanHide() =>
        _vm.HasMedia && !_vm.Paused && !_vm.IsDrawerOpen && !_vm.IsScrubbing && !_vm.IsBusy
        && !BottomBar.IsMouseOver && !TopBar.IsMouseOver;

    public void ShowControls()
    {
        Root.Cursor = null;
        if (!_controlsShown)
        {
            _controlsShown = true;
            TopBar.Visibility = BottomBar.Visibility = Visibility.Visible;
            Fade(TopBar, 1, 140); Fade(BottomBar, 1, 140);
        }
        _idle.Stop();
        _idle.Start();
    }

    private void HideControls()
    {
        if (!_controlsShown) return;
        _controlsShown = false;
        HoverBubble.Visibility = Visibility.Collapsed;
        Fade(TopBar, 0, 320, () => { if (!_controlsShown) TopBar.Visibility = Visibility.Collapsed; });
        Fade(BottomBar, 0, 320, () => { if (!_controlsShown) BottomBar.Visibility = Visibility.Collapsed; });
        // No cursor over a playing video; it comes back as soon as the mouse moves
        Root.Cursor = Cursors.None;
    }

    private static void Fade(UIElement el, double to, int ms, Action? done = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new QuadraticEase() };
        if (done is not null) a.Completed += (_, _) => done();
        el.BeginAnimation(OpacityProperty, a);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Paused):
            case nameof(MainViewModel.IsDrawerOpen):
            case nameof(MainViewModel.IsBusy):
                ShowControls();
                if (e.PropertyName == nameof(MainViewModel.IsDrawerOpen) && _vm.IsDrawerOpen) SlideInDrawer();
                break;
        }
    }

    private void SlideInDrawer()
    {
        DrawerShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ------------------------------------------------------------------ mouse

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // WPF also raises MouseMove when layout changes under a still mouse; only real moves count
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _lastMouse.X) < 1 && Math.Abs(p.Y - _lastMouse.Y) < 1) return;
        _lastMouse = p;
        ShowControls();
    }

    private bool IsOnVideo(MouseEventArgs e) => ReferenceEquals(e.OriginalSource, Root) || e.OriginalSource is Border b && (b == TopBar || b == BottomBar);

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsOnVideo(e)) return;
        if (_vm.IsDrawerOpen) { _vm.ActiveDrawer = NovaMotion.Core.ViewModels.Drawer.None; e.Handled = true; return; }
        if (e.ClickCount == 2)
        {
            _click.Stop();             // the first click of a double-click must not pause
            // Double-click the sides to jump 10 s, as phone players do; the middle goes full screen
            var x = e.GetPosition(Root).X / Math.Max(1, Root.ActualWidth);
            if (x < 0.3) { _ = _vm.SeekAsync(-10, true); _vm.ShowToast("−10 s"); }
            else if (x > 0.7) { _ = _vm.SeekAsync(10, true); _vm.ShowToast("+10 s"); }
            else _vm.IsFullscreen = !_vm.IsFullscreen;
        }
        else _click.Start();
        e.Handled = true;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (DrawerPanel.IsMouseOver) return;      // scrolling the panel
        _vm.Volume = Math.Clamp(Math.Round(_vm.Volume) + (e.Delta > 0 ? 5 : -5), 0, _vm.MaxVolume);
        _vm.ShowToast($"Volume {_vm.Volume:0}%");
        ShowControls();
        e.Handled = true;
    }

    private double _hoverFraction = -1;

    private async void OnSeekHover(double? fraction, double x)
    {
        if (fraction is not { } f || _vm.Duration <= 0)
        {
            HoverBubble.Visibility = Visibility.Collapsed;
            HoverPicture.Visibility = Visibility.Collapsed;
            _hoverFraction = -1;
            return;
        }
        var chapter = _vm.ChapterAt(f);
        HoverText.Text = chapter is null ? _vm.TimeAt(f) : $"{_vm.TimeAt(f)}  ·  {chapter}";
        HoverBubble.Visibility = Visibility.Visible;
        HoverBubble.UpdateLayout();
        var w = HoverBubble.ActualWidth;
        Canvas.SetLeft(HoverBubble, Math.Clamp(x - w / 2, 0, Math.Max(0, Seek.ActualWidth - w)));
        // A picture of that moment, for files on this PC (made in the background, kept for the next hover)
        _hoverFraction = f;
        var file = await _vm.ThumbnailAtAsync(f);
        if (file is null || Math.Abs(_hoverFraction - f) > 0.0001 || HoverBubble.Visibility != Visibility.Visible) return;
        try
        {
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;   // no lock on the file
            img.UriSource = new Uri(file);
            img.EndInit();
            HoverImage.Source = img;
            HoverPicture.Visibility = Visibility.Visible;
            HoverBubble.UpdateLayout();
            w = HoverBubble.ActualWidth;
            Canvas.SetLeft(HoverBubble, Math.Clamp(x - w / 2, 0, Math.Max(0, Seek.ActualWidth - w)));
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UriFormatException) { }
    }

    // ------------------------------------------------------------------ keys

    private bool _fastHeld;

    /// <summary>Holding → plays at 2× until it's let go (a tap still jumps 5 s).</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Right && e.IsRepeat && Keyboard.Modifiers == ModifierKeys.None && _vm.HasMedia)
        {
            if (!_fastHeld) { _fastHeld = true; _vm.SetSpeedCommand.Execute(2.0); _vm.ShowToast("2× while held"); }
            e.Handled = true;
            return;
        }
        KeyPressed?.Invoke(e);
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Right || !_fastHeld) return;
        _fastHeld = false;
        _vm.SetSpeedCommand.Execute(1.0);
        e.Handled = true;
    }
}
