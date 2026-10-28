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
    /// <summary>TensorRT: the size the AI works at (Auto, Full, 75 %, Half).</summary>
    public ObservableCollection<OptionItem> AiSizes { get; } = new();
    public Command SelectAiSizeCommand { get; private set; } = null!;

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
        SelectModelCommand = new Command(p =>
        {
            if (p is not OptionItem o) return;
            if (Eng.Engine == "rife_trt") { Eng.TrtModel = o.Value; Eng.QualityStep = 0; } else Eng.RifeModel = o.Value;
            SyncMotion(); _ = SaveAndApplyAsync();
        });
        SelectAiSizeCommand = new Command(p =>
        {
            if (p is not OptionItem o) return;
            Eng.AiScale = o.Value; Eng.QualityStep = 0;
            SyncMotion(); _ = SaveAndApplyAsync();
        });
        foreach (var (value, label, tip) in new[]
        {
            ("auto", "Auto", "The biggest size this PC keeps up with (from tuning)"),
            ("1", "Full", "The AI sees every pixel. Sharpest; the most work for the GPU."),
            ("0.75", "75%", "A little softer in-between frames, about half the work"),
            ("0.5", "Half", "For GTX-class cards: a quarter of the work. Real frames stay full size."),
        }) AiSizes.Add(new OptionItem(new Choice(value, label, tip)));
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
        TuneCommand = new Command(Tune);
        DismissTunePromptCommand = new Command(DismissTunePrompt);
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
        RebuildModels();
        foreach (var o in AiSizes) o.IsSelected = o.Value == (Eng.AiScale is "" ? "auto" : Eng.AiScale);
        Raise(nameof(SmoothingEnabled), nameof(SceneDetect), nameof(Dedup), nameof(Demo), nameof(DemoSideBySide), nameof(ShowDemoLabels), nameof(RtxVsr), nameof(IsRife), nameof(IsMvTools),
              nameof(ActiveProfileName), nameof(EngineName), nameof(TargetName), nameof(HasModels), nameof(IsTensorRt),
              nameof(CloseToTray), nameof(ResumePlayback), nameof(AutoQueueFolder), nameof(AutoUpdateYtDlp), nameof(StartMinimizedToTray));
        UpdateEngineState();
    }

    /// <summary>The model list for the current engine: Vulkan RIFE's folders, or TensorRT's models with Auto first.</summary>
    private void RebuildModels()
    {
        Models.Clear();
        static string Tip(string m) => m.Contains("lite", StringComparison.OrdinalIgnoreCase) ? "Lighter, a little softer" : "";
        if (Eng.Engine == "rife_trt")
        {
            Models.Add(new OptionItem(new Choice("auto", "Auto", "The best model this PC keeps up with, picked per video (from tuning)")));
            foreach (var m in Runtime?.TrtModels() ?? Array.Empty<string>())
                Models.Add(new OptionItem(new Choice(m, EngineCatalog.ModelName(m), Tip(m))));
            foreach (var o in Models) o.IsSelected = o.Value == Eng.TrtModel;
        }
        else
        {
            foreach (var m in Runtime?.RifeModels() ?? Array.Empty<string>())
                Models.Add(new OptionItem(new Choice(m, EngineCatalog.ModelName(m), Tip(m))));
            foreach (var o in Models) o.IsSelected = o.Value == Eng.RifeModel;
        }
        if (Models.Count > 0 && !Models.Any(m => m.IsSelected)) Models[0].IsSelected = true;
    }

    public bool IsTensorRt => Eng.Engine == "rife_trt";

    private static string ProfileSummary(EngineSettings e) =>
        $"{EngineCatalog.EngineName(e.Engine)} · {EngineCatalog.TargetName(e.TargetText)}";

    public string ActiveProfileName => _settings.Active.Name;
    public string EngineName => EngineCatalog.EngineName(Eng.Engine);
    public string TargetName => EngineCatalog.TargetName(Eng.TargetText);
    public bool IsRife => Eng.Engine is "rife" or "rife_trt";
    public bool IsMvTools => Eng.Engine == "mvtools";
    public bool HasModels => Models.Count > 1 && IsRife;

    public bool SmoothingEnabled { get => Eng.Enabled; set { if (Eng.Enabled == value) return; Eng.Enabled = value; Raise(); _ = SaveAndApplyAsync(); } }
    public bool Dedup { get => Eng.Dedup; set { if (Eng.Dedup == value) return; Eng.Dedup = value; Raise(); _ = SaveAndApplyAsync(); } }
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

    // ------------------------------------------------------------------ keeping playback smooth

    private readonly List<(DateTime At, long Drops, long Mistimed)> _dropSamples = new();
    private DateTime? _lastStepAt;
    private DateTime _engineStateReadAt;

    /// <summary>Most lighter steps the player takes on its own (one per model below the best fit).</summary>
    public const int MaxQualityStep = 3;
    /// <summary>Most frame-rate steps down the player takes (each one whole multiple of the video's rate).</summary>
    public const int MaxRateStep = 4;
    /// <summary>From the engine's state: whether a lighter model or AI size is left to step to.</summary>
    private bool _engineLighter = true;
    /// <summary>Dropped frames per second, averaged over this window, that count as "not keeping up".</summary>
    public const double DropRateLimit = 2.5;
    public static readonly TimeSpan DropWindow = TimeSpan.FromSeconds(6);

    private double _dropRate;
    /// <summary>Frames the player dropped per second over the last few seconds.</summary>
    public double DropRate { get => _dropRate; private set { if (Set(ref _dropRate, value)) Raise(nameof(DropText)); } }
    public string DropText => DropRate < 0.2 ? "None" : $"{DropRate:0.#} a second";

    /// <summary>
    /// When frames keep dropping, the GPU isn't keeping up with the chosen model: switch to the
    /// next lighter one (TensorRT, automatic model only). Like a game lowering its resolution
    /// to hold the frame rate. Resets with the next video.
    /// </summary>
    private void WatchDrops(DateTime now)
    {
        if (!EngineActive || _state.Paused || _state.Seeking || _state.Buffering)
        {
            _dropSamples.Clear();
            DropRate = 0;
            return;
        }
        _dropSamples.Add((now, _state.FrameDrops, _state.MistimedFrames));
        while (_dropSamples.Count > 0 && now - _dropSamples[0].At > DropWindow) _dropSamples.RemoveAt(0);
        var span = (now - _dropSamples[0].At).TotalSeconds;
        if (span < DropWindow.TotalSeconds * 0.8) { DropRate = 0; return; }
        var all = Math.Max(0, (_dropSamples[^1].Drops - _dropSamples[0].Drops) / span);
        var mistimed = Math.Max(0, (_dropSamples[^1].Mistimed - _dropSamples[0].Mistimed) / span);
        DropRate = all;

        // Drops that are "mistimed" aren't the GPU falling behind: locking to the screen's refresh
        // failed (common on laptops whose screen is on the built-in graphics). Show frames by their
        // timestamps instead, and don't blame the model for it.
        if (mistimed > DropRateLimit && mistimed >= all * 0.6 && _settings.VideoSync != "audio")
        {
            _settings.VideoSync = "audio";
            _store.Save(_settings);
            _dropSamples.Clear();
            _ = Send("set_property", "video-sync", "audio");
            ShowToast("This screen's refresh timing was off, so NOVA Motion now times frames by the video instead. Playback should be steady now.");
            return;
        }
        var late = Math.Max(0, all - mistimed);

        // First a lighter model or AI size, while one exists; then a lower frame rate (a whole
        // multiple of the video's), down to twice the video's rate. Works on any screen and engine.
        var canStep = Eng.Engine == "rife_trt" && Eng.TrtModel == "auto" && Eng.QualityStep < MaxQualityStep && _engineLighter;
        var canLower = !canStep && Eng.RateStep < MaxRateStep;
        var settled = _lastStepAt is null || now - _lastStepAt > TimeSpan.FromSeconds(10);
        if (late > DropRateLimit && settled && (canStep || canLower))
        {
            _lastStepAt = now;
            _dropSamples.Clear();
            if (canStep)
            {
                Eng.QualityStep++;
                ShowToast("Frames were dropping, so NOVA Motion switched to a lighter model to stay smooth.");
            }
            else
            {
                Eng.RateStep++;
                ShowToast("Frames were dropping, so NOVA Motion lowered the frame rate to keep playback steady.");
            }
            _ = SaveAndApplyAsync();
        }
    }

    private string _modelText = "";
    /// <summary>The model the engine is using right now, e.g. "RIFE 4.26 (picked for this PC)".</summary>
    public string ModelText { get => _modelText; private set { if (Set(ref _modelText, value)) Raise(nameof(HasModelText)); } }
    public bool HasModelText => ModelText.Length > 0;
    public string ModelWhy { get; private set; } = "";

    private void ReadEngineState(DateTime now)
    {
        if (!EngineActive) { ModelText = ""; return; }
        if (now - _engineStateReadAt < TimeSpan.FromSeconds(2)) return;
        _engineStateReadAt = now;
        try
        {
            if (!File.Exists(_store.EngineStatePath)) return;
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_store.EngineStatePath));
            var model = TrackInfo.Str(json?["model"]);
            if (model is null || TrackInfo.Str(json?["engine"]) != Eng.Engine) { ModelText = ""; return; }
            if (model == "mvtools")
            {
                ModelText = "MVTools for now: the AI can't keep up on this PC" + (HasTuning ? "" : " until it's tuned");
                ModelWhy = TrackInfo.Str(json?["why"]) ?? ""; Raise(nameof(ModelWhy));
                return;
            }
            _engineLighter = json?["lighter"] is not System.Text.Json.Nodes.JsonValue lv || !lv.TryGetValue(out bool l) || l;
            var name = EngineCatalog.ModelName("rife-" + model.Replace('_', '.').Replace(".lite", "_lite"));
            // On cards that can't run the AI at full size: e.g. "· AI at 960×540"
            if (TrackInfo.Dbl(json?["scale"]) is > 0 and < 1 && TrackInfo.Dbl(json?["ai_width"]) is > 0 and var aw && TrackInfo.Dbl(json?["ai_height"]) is > 0 and var ah)
                name += $" · AI at {aw:0}×{ah:0}";
            ModelText = Eng.TrtModel == "auto"
                ? name + (Eng.QualityStep > 0 ? " (lighter, to stay smooth)" : " (picked for this PC)")
                : name;
            ModelWhy = TrackInfo.Str(json?["why"]) ?? "";
            Raise(nameof(ModelWhy));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ tuning

    public Command TuneCommand { get; private set; } = null!;
    public ObservableCollection<BenchmarkItem> TuningResults { get; } = new();
    public bool HasTuning => TuningResults.Count > 0;
    public string TuningText { get; private set; } = "";

    private void RefreshTuning()
    {
        TuningResults.Clear();
        TuningText = "Not tuned yet. Tuning measures each model on this PC (about 10 minutes), then NOVA Motion picks the best one that plays smoothly.";
        try
        {
            var path = Runtime is null ? null : Path.Combine(Runtime.Root, "tuning.json");
            if (path is not null && File.Exists(path)
                && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is { } t
                && t["sizes"] is System.Text.Json.Nodes.JsonObject all
                && all.FirstOrDefault() is { Key: var sizeKey, Value: System.Text.Json.Nodes.JsonObject sizes })
            {
                // Tuned at 1080p (RTX) or at the half size GTX-class cards play at (960x576)
                var half = sizeKey.StartsWith("960x", StringComparison.Ordinal);
                var max = sizes.Select(kv => TrackInfo.Dbl(kv.Value)).DefaultIfEmpty(1).Max();
                foreach (var (model, value) in sizes)
                {
                    var fps = TrackInfo.Dbl(value);
                    // 55 AI frames a second for 25 -> 60, with the engine's headroom
                    TuningResults.Add(new BenchmarkItem(EngineCatalog.ModelName("rife-" + model.Replace('_', '.').Replace(".lite", "_lite")),
                        $"{fps:0} fps", Math.Clamp(fps / Math.Max(max, 1), 0.02, 1), fps * 11 / 12 >= 55 * 1.4));
                }
                TuningText = $"Tuned on {TrackInfo.Str(t["measured"])?.Replace('T', ' ') ?? "this PC"}. Frames each model makes per second "
                           + (half ? "with the AI at half size, as this card plays 1080p." : "at 1080p.");
            }
            else if (path is not null && File.Exists(path)
                && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is { } tv
                && tv["vulkan"]?["options"] is System.Text.Json.Nodes.JsonObject vopts)
            {
                // Without TensorRT: Vulkan RIFE per model and AI size, and MVTools
                var rows = vopts.Select(kv => (Name: kv.Key, Fps: TrackInfo.Dbl(kv.Value))).ToList();
                var mv = TrackInfo.Dbl(tv["mvtools"]);
                var max = rows.Select(r => r.Fps).Append(mv).DefaultIfEmpty(1).Max();
                foreach (var (name, fps) in rows)
                {
                    var at = name.LastIndexOf('@');
                    var size = at > 0 && double.TryParse(name[(at + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) ? sc : 1;
                    var label = $"{EngineCatalog.ModelName(at > 0 ? name[..at] : name)}, AI at {(size * 100).ToString("0", CultureInfo.InvariantCulture)}%";
                    TuningResults.Add(new BenchmarkItem(label, $"{fps:0} fps", Math.Clamp(fps / Math.Max(max, 1), 0.02, 1), fps * 11 / 12 >= 55 * 1.4));
                }
                if (mv > 0) TuningResults.Add(new BenchmarkItem("MVTools (processor)", $"{mv:0} fps", Math.Clamp(mv / Math.Max(max, 1), 0.02, 1), mv >= 60));
                TuningText = $"Tuned on {TrackInfo.Str(tv["measured"])?.Replace('T', ' ') ?? "this PC"} (no TensorRT here). Frames a second at 1080p, 25 to 60 fps.";
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        Raise(nameof(HasTuning), nameof(TuningText), nameof(ShowTunePrompt), nameof(ShowGpuAdvice));
    }

    /// <summary>The Home card that offers tuning: TensorRT is installed, nothing measured yet.</summary>
    public bool ShowTunePrompt => CanTune && !HasTuning && !_settings.TunePromptDismissed;
    /// <summary>Tuning works with TensorRT, or with Vulkan RIFE and MVTools on any other PC.</summary>
    public bool CanTune => Runtime is not null && (Availability.TensorRt || Availability.RifeModels > 0);
    public Command DismissTunePromptCommand { get; private set; } = null!;
    /// <summary>The advice line under the card name: entry and integrated graphics, when the tune card isn't saying it.</summary>
    public bool ShowGpuAdvice => (_gpu?.Tier is GpuTier.Entry or GpuTier.Integrated) && !ShowTunePrompt;

    /// <summary>A line about what this card can do, for Home and Settings.</summary>
    public string GpuAdvice => _gpu?.Tier switch
    {
        GpuTier.Entry => Availability.TensorRt
            ? "Your graphics card is entry-level, so NOVA Motion starts with the light AI model at 60 fps. Tuning finds out what it can really do."
            : "Your graphics card is entry-level, so NOVA Motion uses the light AI model at 60 fps. If playback stutters, choose Light in the player.",
        GpuTier.Integrated => "Built-in graphics: NOVA Motion plays MVTools until tuning shows the AI keeps up, then runs the AI on a half-size copy. Tuning takes about 10 minutes.",
        _ => "Tuning measures each AI model on this PC (about 10 minutes), then NOVA Motion uses the best one that keeps up.",
    };

    private void DismissTunePrompt()
    {
        _settings.TunePromptDismissed = true;
        _store.Save(_settings);
        Raise(nameof(ShowTunePrompt), nameof(ShowGpuAdvice));
    }

    private void Tune()
    {
        if (!CanTune || Runtime is null) return;
        if (!_settings.TunePromptDismissed) DismissTunePrompt();
        var script = Path.Combine(Path.GetDirectoryName(Runtime.Root) ?? Runtime.Root, "tune.cmd");
        _host.RunInConsole(script, "");
        ShowToast("Tuning in a separate window. Close other GPU-heavy apps meanwhile.");
    }

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
        }
        else if (_settings.DefaultsVersion < AppSettings.CurrentDefaults)
        {
            // Settings made before entry cards had defaults of their own asked them for too much
            if (_settings.DefaultsVersion < 2 && gpu.Tier == GpuTier.Entry) _settings.ApplyGpuDefaults(gpu);
            // Built-in graphics got MVTools only; now Vulkan RIFE at half size once tuned (version 4)
            else if (_settings.DefaultsVersion < 4 && gpu.Tier == GpuTier.Integrated) _settings.ApplyGpuDefaults(gpu);
            // Hybrid laptops: frames by their timestamps (version 3)
            if (gpu.IsHybrid) _settings.VideoSync = "audio";
            _settings.DefaultsVersion = AppSettings.CurrentDefaults;
        }
        _settings.MarkGpu(gpu);
        _store.Save(_settings);
        SyncMotion();
        RebuildChoices();
        Raise(nameof(ShowTunePrompt), nameof(GpuAdvice), nameof(ShowGpuAdvice));
        RebuildGenerate();
        _ = UpdateYtDlpAsync();
    }

    public void RefreshSystem()
    {
        Availability = EngineAvailability.Of(Runtime);
        RebuildChoices();
        Benchmarks.Clear();
        var results = BenchmarkLog.Read(Runtime);
        // Laptops have two graphics chips; Vulkan RIFE goes on the one the benchmark found fastest
        if (BenchmarkLog.FastestVulkanDevice(results) is { } device && _settings.Profiles.Any(p => p.Engine.RifeGpu != device))
        {
            foreach (var p in _settings.Profiles) p.Engine.RifeGpu = device;
            _store.Save(_settings);
        }
        var best = results.Where(r => r.Fps is not null).Select(r => r.Fps!.Value).DefaultIfEmpty(60).Max();
        foreach (var r in results)
            Benchmarks.Add(new BenchmarkItem(EngineCatalog.EngineName(r.Engine), r.Fps is { } f ? $"{f:0} fps" : "failed",
                                             r.Fps is { } v ? Math.Clamp(v / Math.Max(best, 60), 0.02, 1) : 0.02, r.Fps >= 60));
        Problems.Clear();
        if (Runtime is null) Problems.Add("The player files are missing. Run build.cmd once to download them, then restart NOVA Studio.");
        else foreach (var p in Runtime.Problems()) Problems.Add(p);
        RebuildEngineSizes();
        RefreshTuning();
        Raise(nameof(Availability), nameof(HasBenchmarks), nameof(HasProblems), nameof(TensorRtText), nameof(CanPlay), nameof(HasTensorRt), nameof(ShowTunePrompt), nameof(GpuAdvice), nameof(ShowGpuAdvice), nameof(CanTune));
    }

    public bool CanPlay => Runtime is not null && File.Exists(Runtime.MpvExe);
    public bool HasTensorRt => Availability.TensorRt;
}
