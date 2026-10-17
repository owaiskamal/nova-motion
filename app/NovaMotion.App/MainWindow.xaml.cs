// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NovaMotion.Core;
using Forms = System.Windows.Forms;

namespace NovaMotion.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly RuntimeLayout? _runtime = RuntimeLayout.Find();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AppSettings _settings;
    private MpvPlayer? _player;
    private Forms.NotifyIcon? _tray;
    private bool _loadingUi = true;   // ignore control events fired while XAML loads; LoadUi() clears it
    private bool _quitting;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _store.Load();

        Drop += OnDrop;
        Loaded += async (_, _) => await OnLoadedAsync();
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        SetupTray();
        ShowProblems();
        LoadUi();
    }

    // ------------------------------------------------------------------ startup

    private async Task OnLoadedAsync()
    {
        var gpu = await GpuInfo.DetectAsync();
        GpuText.Text = gpu.Names.Count > 0 ? gpu.Primary : "GPU unknown";
        if (!_settings.FirstRunDone)
        {
            // Pick sensible profiles for this machine the first time
            _settings = AppSettings.CreateDefaults(gpu);
            _settings.FirstRunDone = true;
            _store.Save(_settings);
            LoadUi();
        }
        VsrBox.IsEnabled = gpu.IsNvidiaRtx || gpu.Names.Count == 0;
        if (!VsrBox.IsEnabled) VsrBox.ToolTip = "Needs an NVIDIA RTX GPU";
    }

    private void ShowProblems()
    {
        if (_runtime is null)
        {
            ProblemsText.Text = "Runtime not found. Run setup\\Get-Runtime.ps1 once, then restart NOVA Motion.";
            PlayButton.IsEnabled = false;
            return;
        }
        ProblemsText.Text = string.Join(Environment.NewLine, _runtime.Problems());
    }

    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open NOVA Motion", null, (_, _) => ShowFromTray());
        menu.Items.Add("Smoothing on/off", null, (_, _) => Dispatcher.Invoke(() =>
        {
            EnabledBox.IsChecked = EnabledBox.IsChecked != true;
            Setting_Changed(EnabledBox, new RoutedEventArgs());
        }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(Quit));

        _tray = new Forms.NotifyIcon
        {
            Text = "NOVA Motion",
            Icon = (Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null)
                   ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray() => Dispatcher.Invoke(() =>
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    });

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting)
        {
            e.Cancel = true;     // keep running in the tray
            Hide();
            _tray?.ShowBalloonTip(2000, "NOVA Motion", "Still running in the tray.", Forms.ToolTipIcon.None);
            return;
        }
        base.OnClosing(e);
    }

    private async void Quit()
    {
        _quitting = true;
        _statusTimer.Stop();
        if (_player is not null) await _player.DisposeAsync();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        Close();
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ open & play

    /// <summary>Handles a command-line argument or one forwarded from a second instance.</summary>
    public void HandleArgument(string arg)
    {
        ShowFromTray();
        if (arg == "--show") return;
        if (NovaMotionLink.Parse(arg) is { } link)
        {
            TargetBox.Text = link.Target;
            _ = PlayAsync(link.Target, link.Start);
        }
        else if (File.Exists(arg))
        {
            TargetBox.Text = arg;
            _ = PlayAsync(arg, null);
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Video|*.mkv;*.mp4;*.avi;*.mov;*.webm;*.m2ts;*.ts;*.wmv;*.flv|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true) TargetBox.Text = dlg.FileName;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            TargetBox.Text = files[0];
            _ = PlayAsync(files[0], null);
        }
        else if (e.Data.GetData(DataFormats.Text) is string text && Uri.IsWellFormedUriString(text.Trim(), UriKind.Absolute))
        {
            TargetBox.Text = text.Trim();
            _ = PlayAsync(text.Trim(), null);
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        var target = TargetBox.Text.Trim().Trim('"');
        if (target.Length == 0) return;
        _ = PlayAsync(target, null);
    }

    private async Task PlayAsync(string target, int? startSeconds)
    {
        if (_runtime is null) return;
        try
        {
            PlayButton.IsEnabled = false;
            FpsText.Text = "Starting…";
            if (_player is null || !_player.IsRunning)
            {
                if (_player is not null) await _player.DisposeAsync();
                _player = new MpvPlayer(_runtime, _store);
                _player.Exited += () => Dispatcher.Invoke(() =>
                {
                    _statusTimer.Stop();
                    TitleText.Text = "Nothing playing";
                    FpsText.Text = "Idle";
                    EngineDot.Fill = (Brush)FindResource("Muted");
                });
            }
            await _player.StartAsync(target, _settings);
            if (startSeconds is > 0)
            {
                await Task.Delay(1500);   // let the file open before seeking
                try { await _player.CommandAsync("seek", startSeconds.Value, "absolute"); } catch (Exception) { }
            }
            _statusTimer.Start();
        }
        catch (Exception ex)
        {
            FpsText.Text = "Could not start player: " + ex.Message;
        }
        finally
        {
            PlayButton.IsEnabled = true;
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_player is null) return;
        PlaybackStatus? st;
        try { st = await _player.GetStatusAsync(); }
        catch (Exception) { return; }
        if (st is null) return;

        TitleText.Text = string.IsNullOrEmpty(st.Title) ? "Loading…" : st.Title;
        if (st.EngineActive && st.OutputFps > st.SourceFps * 1.1)
        {
            FpsText.Text = $"{st.SourceFps:0.###} fps → {st.OutputFps:0.#} fps   ·   display {st.DisplayFps:0.#} Hz";
            EngineDot.Fill = (Brush)FindResource("Good");
        }
        else
        {
            var why = !_settings.Active.Engine.Enabled ? "smoothing off"
                    : st.SourceFps > _settings.Active.Engine.MaxSourceFps ? "source is already high frame rate"
                    : st.EngineActive ? "warming up"
                    : "engine failed - switch engine or check mpv.log";
            FpsText.Text = $"{st.SourceFps:0.###} fps   ·   {why}";
            var failed = !st.EngineActive && _settings.Active.Engine.Enabled && st.SourceFps <= _settings.Active.Engine.MaxSourceFps;
            EngineDot.Fill = failed ? Brushes.IndianRed : (Brush)FindResource(st.EngineActive ? "Warn" : "Muted");
        }
    }

    // ------------------------------------------------------------------ settings UI

    private void LoadUi()
    {
        _loadingUi = true;
        try
        {
            ProfileBox.ItemsSource = _settings.Profiles.Select(p => p.Name).ToList();
            ProfileBox.SelectedItem = _settings.Active.Name;

            var models = _runtime?.RifeModels() ?? Array.Empty<string>();
            ModelBox.ItemsSource = models;

            var e = _settings.Active.Engine;
            Select(EngineBox, e.Engine);
            Select(TargetFpsBox, e.TargetText);
            Select(MvQualityBox, e.MvQuality);
            Select(OnlineHeightBox, _settings.OnlineMaxHeight.ToString());
            ModelBox.SelectedItem = models.Contains(e.RifeModel) ? e.RifeModel : models.FirstOrDefault();
            EnabledBox.IsChecked = e.Enabled;
            SceneBox.IsChecked = e.SceneDetect;
            DemoBox.IsChecked = e.Demo;
            VsrBox.IsChecked = e.RtxVsr;
        }
        finally { _loadingUi = false; }
    }

    private static void Select(ComboBox box, string tag)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag)
                           ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || ProfileBox.SelectedItem is not string name) return;
        _settings.ActiveProfile = name;
        LoadUi();
        _ = SaveAndApplyAsync();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        var eng = _settings.Active.Engine;
        eng.Engine = TagOf(EngineBox) ?? eng.Engine;
        eng.TargetText = TagOf(TargetFpsBox) ?? eng.TargetText;
        eng.MvQuality = TagOf(MvQualityBox) ?? eng.MvQuality;
        if (ModelBox.SelectedItem is string model) eng.RifeModel = model;
        eng.Enabled = EnabledBox.IsChecked == true;
        eng.SceneDetect = SceneBox.IsChecked == true;
        eng.Demo = DemoBox.IsChecked == true;
        eng.RtxVsr = VsrBox.IsChecked == true;
        if (int.TryParse(TagOf(OnlineHeightBox), out var h)) _settings.OnlineMaxHeight = h;
        _ = SaveAndApplyAsync();
    }

    private async Task SaveAndApplyAsync()
    {
        _store.Save(_settings);
        if (_player is { IsRunning: true })
        {
            try { await _player.ApplyAsync(_settings.Active.Engine); }
            catch (Exception ex) { FpsText.Text = "Could not update player: " + ex.Message; }
        }
    }
}
