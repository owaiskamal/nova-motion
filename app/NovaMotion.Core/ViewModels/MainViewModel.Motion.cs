// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace NovaMotion.Core.ViewModels;

/// <summary>Smoothing settings, the engine status readout, and the system page.</summary>
public sealed partial class MainViewModel
{
    public EngineAvailability Availability { get; private set; }

    public ObservableCollection<OptionItem> Profiles { get; } = new();
    public ObservableCollection<OptionItem> Engines { get; } = new();
    public ObservableCollection<OptionItem> Targets { get; } = new();
    public ObservableCollection<OptionItem> MvQualities { get; } = new();
    public ObservableCollection<OptionItem> OnlineHeights { get; } = new();
    public ObservableCollection<OptionItem> Models { get; } = new();

    public Command SelectProfileCommand { get; private set; } = null!;
    public Command SelectEngineCommand { get; private set; } = null!;
    public Command SelectTargetCommand { get; private set; } = null!;
    public Command SelectMvQualityCommand { get; private set; } = null!;
    public Command SelectOnlineHeightCommand { get; private set; } = null!;
    public Command SelectModelCommand { get; private set; } = null!;
    public Command ResetProfilesCommand { get; private set; } = null!;

    private EngineSettings Eng => _settings.Active.Engine;

    private void InitMotion()
    {
        SelectProfileCommand = new Command(p => { if (p is OptionItem o) { _settings.ActiveProfile = o.Value; SyncMotion(); _ = SaveAndApplyAsync(); } });
        SelectEngineCommand = new Command(p => { if (p is OptionItem { Available: true } o) { Eng.Engine = o.Value; SyncMotion(); _ = SaveAndApplyAsync(); } });
        SelectTargetCommand = new Command(p => { if (p is OptionItem o) { Eng.TargetText = o.Value; SyncMotion(); _ = SaveAndApplyAsync(); } });
        SelectMvQualityCommand = new Command(p => { if (p is OptionItem o) { Eng.MvQuality = o.Value; SyncMotion(); _ = SaveAndApplyAsync(); } });
        SelectModelCommand = new Command(p => { if (p is OptionItem o) { Eng.RifeModel = o.Value; SyncMotion(); _ = SaveAndApplyAsync(); } });
        SelectOnlineHeightCommand = new Command(p =>
        {
            if (p is OptionItem o && int.TryParse(o.Value, out var h))
            {
                _settings.OnlineMaxHeight = h; SyncMotion(); _store.Save(_settings);
                if (IsOnline) ShowToast($"Online quality up to {o.Label} from the next video");
            }
        });
        ResetProfilesCommand = new Command(() =>
        {
            var fresh = AppSettings.CreateDefaults(_gpu);
            _settings.Profiles = fresh.Profiles;
            _settings.ActiveProfile = fresh.ActiveProfile;
            SyncMotion();
            _ = SaveAndApplyAsync();
            ShowToast("Profiles reset to the defaults for this PC");
        });
        PrepareEnginesCommand = new Command(PrepareEngines);
        UpdateYtDlpCommand = new Command(() => _ = UpdateYtDlpAsync(force: true));
        SelectQualityNowCommand = new Command(p => { if (p is OptionItem o) _ = SelectQualityNowAsync(o); });
        foreach (var c in EngineCatalog.Targets) Targets.Add(new OptionItem(c));
        foreach (var c in EngineCatalog.MvQualities) MvQualities.Add(new OptionItem(c));
        foreach (var c in EngineCatalog.OnlineHeights) OnlineHeights.Add(new OptionItem(c));
        RebuildChoices();
    }

    private void RebuildChoices()
    {
        Engines.Clear();
        foreach (var c in EngineCatalog.Engines(Availability)) Engines.Add(new OptionItem(c));
        Models.Clear();
        foreach (var m in Runtime?.RifeModels() ?? Array.Empty<string>())
            Models.Add(new OptionItem(new Choice(m, EngineCatalog.ModelName(m), m.Contains("lite", StringComparison.OrdinalIgnoreCase) ? "Lighter, a little softer" : "")));
        SyncMotion();
    }

    /// <summary>Puts the active profile's values into the option lists and toggles.</summary>
    private void SyncMotion()
    {
        Profiles.Clear();
        foreach (var p in _settings.Profiles)
            Profiles.Add(new OptionItem(new Choice(p.Name, p.Name, ProfileSummary(p.Engine))) { IsSelected = p.Name == _settings.Active.Name });
        foreach (var o in Engines) o.IsSelected = o.Value == Eng.Engine;
        foreach (var o in Targets) o.IsSelected = o.Value == Eng.TargetText;
        foreach (var o in MvQualities) o.IsSelected = o.Value == Eng.MvQuality;
        foreach (var o in OnlineHeights) o.IsSelected = o.Value == _settings.OnlineMaxHeight.ToString(CultureInfo.InvariantCulture);
        foreach (var o in Models) o.IsSelected = o.Value == Eng.RifeModel;
        if (Models.Count > 0 && !Models.Any(m => m.IsSelected)) Models[0].IsSelected = true;
        Raise(nameof(SmoothingEnabled), nameof(SceneDetect), nameof(Demo), nameof(DemoSideBySide), nameof(ShowDemoLabels), nameof(RtxVsr), nameof(IsRife), nameof(IsMvTools),
              nameof(ActiveProfileName), nameof(EngineName), nameof(TargetName), nameof(HasModels),
              nameof(CloseToTray), nameof(ResumePlayback), nameof(AutoQueueFolder), nameof(AutoUpdateYtDlp), nameof(StartMinimizedToTray));
        UpdateEngineState();
    }

    private static string ProfileSummary(EngineSettings e) =>
        $"{EngineCatalog.EngineName(e.Engine)} · {EngineCatalog.TargetName(e.TargetText)}";

    public string ActiveProfileName => _settings.Active.Name;
    public string EngineName => EngineCatalog.EngineName(Eng.Engine);
    public string TargetName => EngineCatalog.TargetName(Eng.TargetText);
    public bool IsRife => Eng.Engine is "rife" or "rife_trt";
    public bool IsMvTools => Eng.Engine == "mvtools";
    public bool HasModels => Models.Count > 1 && Eng.Engine == "rife";

    public bool SmoothingEnabled { get => Eng.Enabled; set { if (Eng.Enabled == value) return; Eng.Enabled = value; Raise(); _ = SaveAndApplyAsync(); } }
    public bool SceneDetect { get => Eng.SceneDetect; set { if (Eng.SceneDetect == value) return; Eng.SceneDetect = value; Raise(); _ = SaveAndApplyAsync(); } }
    public bool Demo { get => Eng.Demo; set { if (Eng.Demo == value) return; Eng.Demo = value; Raise(); Raise(nameof(ShowDemoLabels)); _ = SaveAndApplyAsync(); } }
    public bool DemoSideBySide
    {
        get => Eng.DemoMode != "split";
        set { var m = value ? "side" : "split"; if (Eng.DemoMode == m) return; Eng.DemoMode = m; Raise(); Raise(nameof(DemoLeftLabel)); _ = SaveAndApplyAsync(); }
    }

    /// <summary>Labels over the before/after picture, shown while it is on.</summary>
    public bool ShowDemoLabels => Eng.Demo && HasMedia && EngineState == EngineState.Active;
    public string DemoLeftLabel => _state.SourceFps > 0 ? $"Original · {FormatFps(_state.SourceFps)} fps" : "Original";
    public string DemoRightLabel => EngineActive ? $"NOVA Motion · {OutputFpsText} fps" : "NOVA Motion";

    public bool RtxVsr { get => Eng.RtxVsr; set { if (Eng.RtxVsr == value) return; Eng.RtxVsr = value; Raise(); _ = SaveAndApplyAsync(); } }

    public bool CloseToTray { get => _settings.CloseToTray; set { _settings.CloseToTray = value; Raise(); _store.Save(_settings); } }
    public bool ResumePlayback { get => _settings.ResumePlayback; set { _settings.ResumePlayback = value; Raise(); _store.Save(_settings); } }
    public bool AutoQueueFolder { get => _settings.AutoQueueFolder; set { _settings.AutoQueueFolder = value; Raise(); _store.Save(_settings); } }
    public bool AutoUpdateYtDlp { get => _settings.AutoUpdateYtDlp; set { _settings.AutoUpdateYtDlp = value; Raise(); _store.Save(_settings); } }
    public bool StartMinimizedToTray { get => _settings.StartMinimizedToTray; set { _settings.StartMinimizedToTray = value; Raise(); _store.Save(_settings); } }

    private void CycleEngine()
    {
        var avail = Engines.Where(e => e.Available).ToList();
        if (avail.Count == 0) return;
        var i = avail.FindIndex(e => e.IsSelected);
        SelectEngineCommand.Execute(avail[(i + 1) % avail.Count]);
        ShowToast("Engine: " + EngineName);
    }

    private void CycleTarget()
    {
        var i = Targets.ToList().FindIndex(t => t.IsSelected);
        SelectTargetCommand.Execute(Targets[(i + 1) % Targets.Count]);
        ShowToast("Frame rate: " + TargetName);
    }

    private async Task SaveAndApplyAsync()
    {
        _store.Save(_settings);
        if (_backend is { IsRunning: true })
        {
            try { await _backend.ApplyAsync(Eng); }
            catch (Exception ex) when (ex is MpvException or TimeoutException or InvalidOperationException or IOException)
            {
                ShowToast("Could not update the player: " + ex.Message);
            }
        }
        _engineChangedAt = _clock();
        UpdateEngineState();
    }

    // ------------------------------------------------------------------ engine status

    private DateTime? _engineChangedAt;
    private string? _engineSignature;
    private EngineState _engineState = EngineState.Idle;
    public EngineState EngineState { get => _engineState; private set => Set(ref _engineState, value); }

    public bool EngineActive => EngineState == EngineState.Active;
    public bool EngineWorking => EngineState is EngineState.Preparing or EngineState.PreparingLong or EngineState.Opening;
    public bool EngineProblem => EngineState == EngineState.Failed;

    /// <summary>Big readout: "60", or "25" when nothing is generated.</summary>
    public string OutputFpsText => _state.OutputFps > 0 ? Math.Round(_state.OutputFps).ToString(CultureInfo.InvariantCulture) : "–";
    public string SourceFpsText => _state.SourceFps > 0 ? FormatFps(_state.SourceFps) : "–";
    public string DisplayText => _state.DisplayFps > 0 ? $"{_state.DisplayFps:0.##} Hz" : "–";
    public string VideoSizeText => _state.VideoWidth > 0 ? $"{_state.VideoWidth} × {_state.VideoHeight}" : "–";
    public string DecoderText => string.IsNullOrEmpty(_state.HwDec) || _state.HwDec == "no" ? "Software" : $"Hardware ({_state.HwDec})";
    public string GeneratedShareText => EngineActive ? $"{Cadence.GeneratedShare(_state.SourceFps, _state.OutputFps):P0}" : "–";

    /// <summary>Short badge on the controls: "25 → 60 fps".</summary>
    public string BadgeText => EngineState switch
    {
        EngineState.Active => $"{FormatFps(_state.SourceFps)} → {OutputFpsText} fps",
        EngineState.Preparing or EngineState.PreparingLong or EngineState.Opening => "Preparing",
        EngineState.Off => "Smoothing off",
        EngineState.HighFrameRate => $"{FormatFps(_state.SourceFps)} fps",
        EngineState.Failed => "Not smoothing",
        _ => "",
    };

    /// <summary>One line under the readout explaining what's happening.</summary>
    public string EngineStatusText => EngineState switch
    {
        EngineState.Active => $"{EngineName} is generating {GeneratedShareText} of the frames you see.",
        EngineState.Opening => "Opening the video…",
        EngineState.Preparing => $"{EngineName} is starting.",
        EngineState.PreparingLong when Eng.Engine == "rife_trt" =>
            "TensorRT is building an engine for this video size. This happens once per size and takes a few minutes.",
        EngineState.PreparingLong => $"{EngineName} is taking longer than usual to start.",
        EngineState.Off => "Smoothing is off. The video plays at its own frame rate.",
        EngineState.HighFrameRate => $"This video is already {FormatFps(_state.SourceFps)} fps, so no frames are added.",
        EngineState.NoVideo => "Nothing to smooth: this file has no video.",
        EngineState.Failed => $"{EngineName} didn't start, so the video plays as is. Try another engine or see mpv.log.",
        _ => "Smoothing starts when a video plays.",
    };

    /// <summary>Frame-cadence strip for the current video (or a 24 → 60 example when idle).</summary>
    public IReadOnlyList<CadenceTick> CadenceTicks
    {
        get
        {
            var (src, dst) = EngineActive ? (_state.SourceFps, _state.OutputFps) : (24.0, 60.0);
            return Cadence.Pattern(src, dst).Select(g => new CadenceTick(g)).ToList();
        }
    }

    /// <summary>Recent output frame rate as path data (120 × 28 box) for a sparkline.</summary>
    public string FpsSparkline
    {
        get
        {
            if (_fpsSamples.Count < 2) return "M0,27 L120,27";
            var top = Math.Max(_fpsSamples.Max(), Math.Max(_state.DisplayFps, 1)) * 1.05;
            var bottom = Math.Min(_fpsSamples.Min(), _state.SourceFps > 0 ? _state.SourceFps : _fpsSamples.Min()) * 0.9;
            var range = Math.Max(1, top - bottom);
            var sb = new StringBuilder();
            for (var i = 0; i < _fpsSamples.Count; i++)
            {
                var x = 120.0 * i / (_fpsSamples.Count - 1);
                var y = 27 - 26 * (_fpsSamples[i] - bottom) / range;
                sb.Append(i == 0 ? 'M' : 'L').Append(x.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
                  .Append(y.ToString("0.#", CultureInfo.InvariantCulture)).Append(' ');
            }
            return sb.ToString().TrimEnd();
        }
    }

    private static string FormatFps(double fps) =>
        Math.Abs(fps - Math.Round(fps)) < 0.01 ? Math.Round(fps).ToString(CultureInfo.InvariantCulture) : fps.ToString("0.###", CultureInfo.InvariantCulture);

    private void UpdateEngineState()
    {
        var now = _clock();
        EngineState s;
        if (!HasMedia) s = EngineState.Idle;
        else if (IsOpening || _loadedAt is null) s = EngineState.Opening;
        else if (!Eng.Enabled) s = EngineState.Off;
        else if (_state.VideoWidth == 0 && now - _loadedAt > TimeSpan.FromSeconds(3)) s = EngineState.NoVideo;
        else if (_state.SourceFps > Eng.MaxSourceFps) s = EngineState.HighFrameRate;
        else if (_state.EngineInChain && _state.OutputFps > _state.SourceFps * 1.1) s = EngineState.Active;
        else if (_state.EngineInChain || now - _loadedAt < TimeSpan.FromSeconds(4))
        {
            var since = _engineChangedAt is { } c && c > _loadedAt ? c : _loadedAt.Value;
            s = now - since > TimeSpan.FromSeconds(25) ? EngineState.PreparingLong : EngineState.Preparing;
        }
        else s = EngineState.Failed;

        EngineState = s;
        // Called for every frame-rate report: only notify the view when something it shows changed
        var signature = $"{s}|{OutputFpsText}|{SourceFpsText}|{DisplayText}|{VideoSizeText}|{DecoderText}|{Eng.Engine}";
        if (signature == _engineSignature) return;
        _engineSignature = signature;
        Raise(nameof(EngineActive), nameof(EngineWorking), nameof(EngineProblem), nameof(BadgeText), nameof(EngineStatusText),
              nameof(OutputFpsText), nameof(SourceFpsText), nameof(DisplayText), nameof(VideoSizeText), nameof(DecoderText),
              nameof(GeneratedShareText), nameof(CadenceTicks), nameof(EngineSizeHint),
              nameof(ShowDemoLabels), nameof(DemoLeftLabel), nameof(DemoRightLabel));
    }

    /// <summary>Warns before a long TensorRT build: no engine built yet for this video size.</summary>
    public string? EngineSizeHint =>
        Eng.Engine == "rife_trt" && Availability.TensorRt && _state.VideoWidth > 0
        && !TensorRtEngines.IsReady(Availability, _state.VideoWidth, _state.VideoHeight) && EngineState is not EngineState.Active
            ? $"First {TensorRtEngines.Describe(TensorRtEngines.PaddedSize(_state.VideoWidth, _state.VideoHeight))} video with TensorRT: building its engine once."
            : null;

    // ------------------------------------------------------------------ system page

    private GpuInfo? _gpu;
    public string GpuName { get; private set; } = "Detecting…";
    public string Version { get; } = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.2.0";
    public ObservableCollection<BenchmarkItem> Benchmarks { get; } = new();
    public bool HasBenchmarks => Benchmarks.Count > 0;
    public ObservableCollection<string> Problems { get; } = new();
    public bool HasProblems => Problems.Count > 0;
    public string TensorRtText => Availability.TensorRt
        ? Availability.TensorRtSizes.Count > 0
            ? "Installed · engines ready for " + string.Join(", ", Availability.TensorRtSizes.Select(TensorRtEngines.Describe))
            : "Installed · engines build on first play"
        : "Not installed · run setup-tensorrt.cmd (NVIDIA RTX)";
    public IReadOnlyList<ShortcutItem> Shortcuts { get; } = KeyMap.All.Select(s => new ShortcutItem(s.Keys, s.Description)).ToList();

    // ------------------------------------------------------------------ online

    private string _ytDlpStatus = "";
    public string YtDlpStatus { get => _ytDlpStatus; private set => Set(ref _ytDlpStatus, value); }
    public Command UpdateYtDlpCommand { get; private set; } = null!;
    public Command SelectQualityNowCommand { get; private set; } = null!;

    /// <summary>Checks for a new yt-dlp once a day (or now, when asked). Skipped while a link plays.</summary>
    public async Task UpdateYtDlpAsync(bool force = false)
    {
        if (Runtime is null || (!force && !YtDlp.Due(_settings, _clock()))) return;
        if (HasMedia && IsOnline) return;
        YtDlpStatus = "Checking for a new yt-dlp…";
        var (result, version) = await YtDlp.UpdateAsync(Runtime.YtDlpExe);
        _settings.YtDlpCheckedUtc = _clock();
        _store.Save(_settings);
        YtDlpStatus = result switch
        {
            YtDlp.Outcome.Updated => $"Updated to {version} today",
            YtDlp.Outcome.UpToDate => $"Version {version}, checked today",
            _ => "Couldn't check for a new version. NOVA Motion will try again tomorrow.",
        };
    }

    /// <summary>Changes the online quality and reloads the current link at the same moment.</summary>
    private async Task SelectQualityNowAsync(OptionItem o)
    {
        if (!int.TryParse(o.Value, out var h)) return;
        _settings.OnlineMaxHeight = h;
        SyncMotion();
        _store.Save(_settings);
        await Send("set_property", "ytdl-format", MpvPlayer.YtdlFormat(h));
        if (IsOnline && _current is not null && !_state.Idle)
        {
            var at = _state.Position;
            await PlayCurrentAsync(at > 1 ? at : null);
            ShowToast($"Switching to {o.Label}…");
        }
    }

    /// <summary>Common video sizes and whether a TensorRT engine is ready for each.</summary>
    public ObservableCollection<EngineSizeItem> EngineSizes { get; } = new();
    public bool HasMissingEngineSizes => EngineSizes.Any(s => !s.Ready);
    public Command PrepareEnginesCommand { get; private set; } = null!;

    private void RebuildEngineSizes()
    {
        EngineSizes.Clear();
        foreach (var (label, w, h) in TensorRtEngines.CommonSizes)
            EngineSizes.Add(new EngineSizeItem(label, TensorRtEngines.IsReady(Availability, w, h)));
        Raise(nameof(HasMissingEngineSizes));
    }

    private void PrepareEngines()
    {
        if (Runtime is null || !Availability.TensorRt) return;
        var script = Path.Combine(Path.GetDirectoryName(Runtime.Root) ?? Runtime.Root, "prepare-engines.cmd");
        var missing = EngineSizes.Where(s => !s.Ready).Select(s => s.Label).ToList();
        if (missing.Count == 0) return;
        _host.RunInConsole(script, string.Join(" ", missing));
        ShowToast("Preparing engines in a separate window. This takes a few minutes per size.");
    }

    /// <summary>Called once the GPU is known. First run picks profiles that suit it.</summary>
    public void SetGpu(GpuInfo gpu)
    {
        _gpu = gpu;
        GpuName = gpu.Names.Count > 0 ? gpu.Primary : "Unknown GPU";
        Raise(nameof(GpuName));
        if (!_settings.FirstRunDone)
        {
            var volume = _settings.Volume;
            _settings = AppSettings.CreateDefaults(gpu);
            _settings.Volume = volume;
            _settings.FirstRunDone = true;
            _store.Save(_settings);
            SyncMotion();
        }
        _ = UpdateYtDlpAsync();
    }

    public void RefreshSystem()
    {
        Availability = EngineAvailability.Of(Runtime);
        RebuildChoices();
        Benchmarks.Clear();
        var results = BenchmarkLog.Read(Runtime);
        var best = results.Where(r => r.Fps is not null).Select(r => r.Fps!.Value).DefaultIfEmpty(60).Max();
        foreach (var r in results)
            Benchmarks.Add(new BenchmarkItem(EngineCatalog.EngineName(r.Engine), r.Fps is { } f ? $"{f:0} fps" : "failed",
                                             r.Fps is { } v ? Math.Clamp(v / Math.Max(best, 60), 0.02, 1) : 0.02, r.Fps >= 60));
        Problems.Clear();
        if (Runtime is null) Problems.Add("The player files are missing. Run build.cmd once to download them, then restart NOVA Motion.");
        else foreach (var p in Runtime.Problems()) Problems.Add(p);
        RebuildEngineSizes();
        Raise(nameof(Availability), nameof(HasBenchmarks), nameof(HasProblems), nameof(TensorRtText), nameof(CanPlay), nameof(HasTensorRt));
    }

    public bool CanPlay => Runtime is not null && File.Exists(Runtime.MpvExe);
    public bool HasTensorRt => Availability.TensorRt;
}
