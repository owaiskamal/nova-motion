// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NovaMotion.Core.ViewModels;

namespace NovaMotion.App.Controls;

/// <summary>
/// Seek bar drawn by hand: buffered range, progress, chapter gaps, hover growth and a thumb.
/// Value/IsScrubbing bind two-way; the view model seeks when IsScrubbing turns false.
/// </summary>
public sealed class SeekBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CacheFractionProperty = DependencyProperty.Register(nameof(CacheFraction), typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ChaptersProperty = DependencyProperty.Register(nameof(Chapters), typeof(IEnumerable), typeof(SeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsScrubbingProperty = DependencyProperty.Register(nameof(IsScrubbing), typeof(bool), typeof(SeekBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(nameof(ProgressBrush), typeof(Brush), typeof(SeekBar),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double CacheFraction { get => (double)GetValue(CacheFractionProperty); set => SetValue(CacheFractionProperty, value); }
    public IEnumerable? Chapters { get => (IEnumerable?)GetValue(ChaptersProperty); set => SetValue(ChaptersProperty, value); }
    public bool IsScrubbing { get => (bool)GetValue(IsScrubbingProperty); set => SetValue(IsScrubbingProperty, value); }
    public Brush ProgressBrush { get => (Brush)GetValue(ProgressBrushProperty); set => SetValue(ProgressBrushProperty, value); }

    /// <summary>Mouse position along the bar (0..1), or null when the mouse leaves.</summary>
    public event Action<double?, double>? HoverChanged;

    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush CacheBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush HoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)));
    private double? _hover;

    public SeekBar()
    {
        Cursor = Cursors.Hand;
        Height = 22;
        Focusable = false;
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private double FractionAt(double x) => ActualWidth <= 0 ? 0 : Math.Clamp(x / ActualWidth, 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // whole area is clickable
        if (w <= 0) return;

        var active = IsMouseOver || IsScrubbing;
        var barH = active ? 6.0 : 4.0;
        var y = (h - barH) / 2;
        var r = barH / 2;
        var played = Maximum > 0 ? Math.Clamp(Value / Maximum, 0, 1) : 0;

        // Chapter gaps split the bar into segments, like the reference players
        var gaps = (Chapters?.OfType<double>() ?? Enumerable.Empty<double>()).Where(f => f > 0.002 && f < 0.998).OrderBy(f => f).ToList();
        var edges = new List<double> { 0 };
        edges.AddRange(gaps);
        edges.Add(1);
        for (var i = 0; i < edges.Count - 1; i++)
        {
            var x0 = edges[i] * w + (i == 0 ? 0 : 1.5);
            var x1 = edges[i + 1] * w - (i == edges.Count - 2 ? 0 : 1.5);
            if (x1 <= x0) continue;
            Segment(dc, TrackBrush, x0, x1, y, barH, r);
            Segment(dc, CacheBrush, x0, Math.Min(x1, CacheFraction * w), y, barH, r);
            if (_hover is { } hv && active && !IsScrubbing) Segment(dc, HoverBrush, x0, Math.Min(x1, hv * w), y, barH, r);
            Segment(dc, ProgressBrush, x0, Math.Min(x1, played * w), y, barH, r);
        }

        if (active && Maximum > 0)
            dc.DrawEllipse(ProgressBrush, null, new Point(played * w, h / 2), 7, 7);
    }

    private static void Segment(DrawingContext dc, Brush b, double x0, double x1, double y, double h, double r)
    {
        if (x1 - x0 < 0.5) return;
        dc.DrawRoundedRectangle(b, null, new Rect(x0, y, x1 - x0, h), r, r);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Maximum <= 0) return;
        CaptureMouse();
        IsScrubbing = true;
        Value = FractionAt(e.GetPosition(this).X) * Maximum;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var f = FractionAt(e.GetPosition(this).X);
        _hover = f;
        if (IsScrubbing && IsMouseCaptured) Value = f * Maximum;
        HoverChanged?.Invoke(f, e.GetPosition(this).X);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsScrubbing) return;
        Value = FractionAt(e.GetPosition(this).X) * Maximum;
        ReleaseMouseCapture();
        IsScrubbing = false;
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (IsScrubbing) IsScrubbing = false;
    }

    protected override void OnMouseEnter(MouseEventArgs e) => InvalidateVisual();

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        HoverChanged?.Invoke(null, 0);
        InvalidateVisual();
    }
}

/// <summary>
/// The frame-cadence strip: one bar per output frame in a repeat of the pattern.
/// Real source frames are drawn in RealBrush, frames NOVA Motion generates in GeneratedBrush.
/// </summary>
public sealed class CadenceStrip : FrameworkElement
{
    public static readonly DependencyProperty TicksProperty = DependencyProperty.Register(nameof(Ticks), typeof(IEnumerable), typeof(CadenceStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty BarWidthProperty = DependencyProperty.Register(nameof(BarWidth), typeof(double), typeof(CadenceStrip),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(CadenceStrip),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty RealBrushProperty = DependencyProperty.Register(nameof(RealBrush), typeof(Brush), typeof(CadenceStrip),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GeneratedBrushProperty = DependencyProperty.Register(nameof(GeneratedBrush), typeof(Brush), typeof(CadenceStrip),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RepeatProperty = DependencyProperty.Register(nameof(Repeat), typeof(int), typeof(CadenceStrip),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public IEnumerable? Ticks { get => (IEnumerable?)GetValue(TicksProperty); set => SetValue(TicksProperty, value); }
    public double BarWidth { get => (double)GetValue(BarWidthProperty); set => SetValue(BarWidthProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public Brush RealBrush { get => (Brush)GetValue(RealBrushProperty); set => SetValue(RealBrushProperty, value); }
    public Brush GeneratedBrush { get => (Brush)GetValue(GeneratedBrushProperty); set => SetValue(GeneratedBrushProperty, value); }
    /// <summary>How many repeats of the pattern to draw (the home page shows several).</summary>
    public int Repeat { get => (int)GetValue(RepeatProperty); set => SetValue(RepeatProperty, value); }

    private List<bool> Bars()
    {
        var one = (Ticks?.OfType<CadenceTick>() ?? Enumerable.Empty<CadenceTick>()).Select(t => t.Generated).ToList();
        var all = new List<bool>();
        for (var i = 0; i < Math.Max(1, Repeat); i++) all.AddRange(one);
        return all;
    }

    protected override Size MeasureOverride(Size available)
    {
        var n = Bars().Count;
        var h = double.IsInfinity(available.Height) ? 14 : available.Height;
        return new Size(n == 0 ? 0 : n * BarWidth + (n - 1) * Gap, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bars = Bars();
        var h = ActualHeight;
        for (var i = 0; i < bars.Count; i++)
        {
            var x = i * (BarWidth + Gap);
            // Real frames stand a little taller: they are the anchors the AI works between
            var bh = bars[i] ? h * 0.72 : h;
            dc.DrawRoundedRectangle(bars[i] ? GeneratedBrush : RealBrush, null,
                new Rect(x, (h - bh) / 2, BarWidth, bh), BarWidth / 2, BarWidth / 2);
        }
    }
}

/// <summary>A black Win32 child window for mpv to draw into (mpv --wid).</summary>
public sealed class VideoHost : HwndHost
{
    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
    private const string ClassName = "NovaMotionVideo";
    private static DefProc? _proc;   // kept alive for the lifetime of the class
    private static bool _registered;

    public IntPtr VideoHandle { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureClass();
        VideoHandle = CreateWindowEx(0, ClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        return new HandleRef(this, VideoHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

    private static void EnsureClass()
    {
        if (_registered) return;
        _proc = DefWindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            hbrBackground = GetStockObject(4 /* BLACK_BRUSH */),
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);
        _registered = true;
    }

    private delegate IntPtr DefProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public int style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int obj);
}

/// <summary>true → Visible. Parameter "invert" flips it; "hidden" uses Hidden instead of Collapsed.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public bool UseHidden { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var p = parameter as string ?? "";
        var on = value switch { bool b => b, null => false, string s => s.Length > 0, int i => i != 0, ICollection c => c.Count > 0, _ => true };
        if (Invert || p.Contains("invert")) on = !on;
        return on ? Visibility.Visible : UseHidden || p.Contains("hidden") ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InvertBool : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Enum value equals the parameter → true (for drawer and page switches).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var eq = string.Equals(value?.ToString(), parameter as string, StringComparison.OrdinalIgnoreCase);
        return targetType == typeof(Visibility) ? (eq ? Visibility.Visible : Visibility.Collapsed) : eq;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>0..1 → a width of up to the parameter (pixels).</summary>
public sealed class FractionToWidth : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var max = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 100;
        return value is double d ? Math.Clamp(d, 0, 1) * max : 0.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
