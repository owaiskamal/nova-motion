// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using NovaMotion.Core;
using NovaMotion.Core.ViewModels;
using Forms = System.Windows.Forms;

namespace NovaMotion.App;

public partial class MainWindow : Window, IUiHost
{
    private readonly SettingsStore _store = new();
    private readonly RuntimeLayout? _runtime = RuntimeLayout.Find();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly MainViewModel _vm;
    private PlayerOverlay? _overlay;
    private Forms.NotifyIcon? _tray;
    private bool _quitting;
    private Rect _restoreBounds;
    private WindowState _restoreState;

    public MainViewModel ViewModel => _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(this, _store, _runtime, CreatePlayer);
        DataContext = _vm;
        _vm.PropertyChanged += OnVmChanged;

        _tick.Tick += (_, _) => _vm.Tick();
        _tick.Start();
        Loaded += async (_, _) => await OnLoadedAsync();
        Activated += (_, _) => _vm.CheckClipboard();
        LocationChanged += (_, _) => PlaceOverlay();
        SizeChanged += (_, _) => PlaceOverlay();
        StateChanged += (_, _) => OnStateChanged();
        Video.SizeChanged += (_, _) => PlaceOverlay();
        PreviewKeyDown += (_, e) => OnKey(e);
        DragEnter += OnDragEnter;
        DragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
        DragLeave += (_, _) => DropHint.Visibility = Visibility.Collapsed;
        Drop += (_, e) => OnDrop(e);
        SetupTray();
    }

    private IPlayerBackend CreatePlayer()
    {
        if (_runtime is null) throw new InvalidOperationException("the player files are missing. Run build.cmd once, then restart NOVA Motion");
        return new MpvPlayer(_runtime, _store) { WindowHandle = Video.VideoHandle.ToInt64() };
    }

    private async Task OnLoadedAsync()
    {
        _overlay = new PlayerOverlay(_vm) { Owner = this };
        _overlay.Dropped += OnDrop;
        _overlay.KeyPressed += OnKey;
        UpdatePlayerSurface();
        Home.FocusLink();
        _vm.SetGpu(await GpuInfo.DetectAsync());
        if (UiTest.Requested) _ = UiTest.RunAsync(this, _vm);
    }

    // ------------------------------------------------------------------ IUiHost

    public void Post(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    public IReadOnlyList<string>? PickVideoFiles()
    {
        var exts = string.Join(";", MediaSource.VideoExtensions.Select(e => "*" + e));
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = $"Videos|{exts}|All files|*.*", Title = "Open videos" };
        return dlg.ShowDialog(this) == true ? dlg.FileNames : null;
    }

    public string? PickSubtitleFile()
    {
        var exts = string.Join(";", MediaSource.SubtitleExtensions.Select(e => "*" + e));
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = $"Subtitles|{exts}|All files|*.*", Title = "Add a subtitle file" };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    public string? ClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (Exception) { return null; }   // clipboard busy in another app
    }

    public void OpenExternal(string pathOrUrl)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pathOrUrl) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    public void RunInConsole(string script, string arguments)
    {
        if (!File.Exists(script)) { _vm.ShowToast("Couldn't find " + Path.GetFileName(script)); return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"\"{script}\" {arguments}\"")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(script)!,
            });
        }
        catch (Exception ex) { _vm.ShowToast("Couldn't start the script: " + ex.Message); }
    }

    // ------------------------------------------------------------------ view state

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.HasMedia):
            case nameof(MainViewModel.IsSettingsOpen):
                UpdatePlayerSurface();
                break;
            case nameof(MainViewModel.IsFullscreen):
                SetFullscreen(_vm.IsFullscreen);
                break;
            case nameof(MainViewModel.EngineState):
                EngineDot.Fill = (Brush)FindResource(_vm.EngineState switch
                {
                    EngineState.Active => "Nova",
                    EngineState.Failed => "Danger",
                    EngineState.Preparing or EngineState.PreparingLong or EngineState.Opening => "Muted",
                    _ => "Faint",
                });
                break;
            case nameof(MainViewModel.ToastVisible):
                HomeToast.Visibility = _vm.ToastVisible && (!_vm.HasMedia || _vm.IsSettingsOpen) ? Visibility.Visible : Visibility.Collapsed;
                break;
        }
    }

    /// <summary>The video window and its overlay show only while a video plays and no page covers it.</summary>
    private void UpdatePlayerSurface()
    {
        var show = _vm.HasMedia && !_vm.IsSettingsOpen;
        Video.Visibility = show ? Visibility.Visible : Visibility.Hidden;
        if (_overlay is null) return;
        if (show && IsVisible && WindowState != WindowState.Minimized)
        {
            PlaceOverlay();
            if (!_overlay.IsVisible) _overlay.Show();
            _overlay.ShowControls();
        }
        else if (_overlay.IsVisible) _overlay.Hide();
        if (!_vm.HasMedia) Home.FocusLink();
    }

    /// <summary>Keeps the overlay window exactly over the video area.</summary>
    private void PlaceOverlay()
    {
        if (_overlay is null || !Video.IsVisible || PresentationSource.FromVisual(Video) is not { CompositionTarget: { } ct }) return;
        var topLeft = ct.TransformFromDevice.Transform(Video.PointToScreen(new Point(0, 0)));
        var bottomRight = ct.TransformFromDevice.Transform(Video.PointToScreen(new Point(Video.ActualWidth, Video.ActualHeight)));
        _overlay.Left = topLeft.X;
        _overlay.Top = topLeft.Y;
        _overlay.Width = Math.Max(0, bottomRight.X - topLeft.X);
        _overlay.Height = Math.Max(0, bottomRight.Y - topLeft.Y);
    }

    private void OnStateChanged()
    {
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        // A maximised custom-chrome window overhangs the screen by its resize border
        Frame.Padding = WindowState == WindowState.Maximized && !_vm.IsFullscreen ? new Thickness(7) : new Thickness(0);
        UpdatePlayerSurface();
    }

    private void SetFullscreen(bool on)
    {
        if (on)
        {
            _restoreState = WindowState;
            _restoreBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            var screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).Bounds;
            var src = PresentationSource.FromVisual(this)?.CompositionTarget;
            var tl = src?.TransformFromDevice.Transform(new Point(screen.Left, screen.Top)) ?? new Point(screen.Left, screen.Top);
            var br = src?.TransformFromDevice.Transform(new Point(screen.Right, screen.Bottom)) ?? new Point(screen.Right, screen.Bottom);
            TitleRow.Height = new GridLength(0);
            WindowChrome.SetWindowChrome(this, MakeChrome(caption: 0, resize: 0));
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            Frame.Padding = new Thickness(0);
            Left = tl.X; Top = tl.Y; Width = br.X - tl.X; Height = br.Y - tl.Y;
            Topmost = true;
        }
        else
        {
            Topmost = false;
            TitleRow.Height = new GridLength(44);
            WindowChrome.SetWindowChrome(this, MakeChrome(caption: 44, resize: 6));
            ResizeMode = ResizeMode.CanResize;
            if (!_restoreBounds.IsEmpty)
            {
                Left = _restoreBounds.Left; Top = _restoreBounds.Top;
                Width = _restoreBounds.Width; Height = _restoreBounds.Height;
            }
            WindowState = _restoreState;
            OnStateChanged();
        }
        Dispatcher.BeginInvoke(PlaceOverlay, DispatcherPriority.Loaded);
    }

    private static WindowChrome MakeChrome(double caption, double resize) => new()
    {
        CaptionHeight = caption,
        ResizeBorderThickness = new Thickness(resize),
        GlassFrameThickness = new Thickness(0),
        CornerRadius = new CornerRadius(0),
        UseAeroCaptionButtons = false,
    };

    // ------------------------------------------------------------------ input

    private void OnKey(KeyEventArgs e)
    {
        if (!_vm.HasMedia || _vm.IsSettingsOpen) return;
        if (Keyboard.FocusedElement is TextBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var (action, arg) = KeyMap.Resolve(key.ToString(), mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Shift), mods.HasFlag(ModifierKeys.Alt));
        if (action == PlayerAction.None) return;
        e.Handled = true;
        _overlay?.ShowControls();
        _ = _vm.HandleAsync(action, arg);
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (_vm.HasMedia) return;     // over the video, the overlay window takes drops
        DropHint.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnDrop(DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        var enqueue = e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey);
        List<string> targets = new();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files)
            {
                if (Directory.Exists(f))
                    targets.AddRange(Directory.GetFiles(f).Where(MediaSource.IsVideoFile).OrderBy(x => x, NaturalComparer.Instance));
                else if (MediaSource.IsSubtitleFile(f) && _vm.HasMedia)
                {
                    _vm.AddSubtitle(f);
                    return;
                }
                else targets.Add(f);
            }
        }
        else if (MediaSource.AsLink(e.Data.GetData(DataFormats.UnicodeText) as string ?? e.Data.GetData(DataFormats.Text) as string) is { } link)
            targets.Add(link);
        if (targets.Count == 0) return;
        if (enqueue && _vm.HasMedia) _vm.Enqueue(targets);
        else _ = _vm.OpenAsync(targets);
        Activate();
    }

    /// <summary>A command-line argument or one forwarded from a second instance.</summary>
    public void HandleArgument(string arg)
    {
        ShowFromTray();
        if (arg == "--show") return;
        if (NovaMotionLink.Parse(arg) is { } link) _ = _vm.OpenAsync(new[] { link.Target }, link.Start);
        else if (File.Exists(arg)) _ = _vm.OpenAsync(new[] { arg });
    }

    public void OpenSettings(string section)
    {
        SettingsPage.ShowSection(section);
        _vm.OpenSettingsCommand.Execute(null);
    }

    private void EngineChip_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.HasMedia && !_vm.IsSettingsOpen) _vm.ToggleDrawerCommand.Execute("motion");
        else OpenSettings("smoothing");
    }

    // ------------------------------------------------------------------ title bar

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ tray & lifetime

    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open NOVA Motion", null, (_, _) => ShowFromTray());
        menu.Items.Add("Smoothing on/off", null, (_, _) => Dispatcher.Invoke(() => _vm.SmoothingEnabled = !_vm.SmoothingEnabled));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(Quit));
        _tray = new Forms.NotifyIcon
        {
            Text = "NOVA Motion",
            Icon = (Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null) ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    public void ShowFromTray() => Dispatcher.Invoke(() =>
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        UpdatePlayerSurface();
    });

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting && _vm.Settings.CloseToTray)
        {
            e.Cancel = true;
            _ = _vm.StopAsync();
            _vm.IsFullscreen = false;
            Hide();
            _overlay?.Hide();
            _tray?.ShowBalloonTip(2000, "NOVA Motion", "Still running in the tray, ready for links from your browser.", Forms.ToolTipIcon.None);
            return;
        }
        if (!_quitting) { e.Cancel = true; Quit(); return; }
        base.OnClosing(e);
    }

    public async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _tick.Stop();
        _overlay?.Close();
        await _vm.DisposeAsync();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        Close();
        Application.Current.Shutdown();
    }
}
