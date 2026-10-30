// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
// The app's behaviour (queue, resume, tracks, engine status, keyboard) against a fake mpv.
using System.Text.Json.Nodes;
using NovaMotion.Core;
using NovaMotion.Core.ViewModels;

internal sealed class FakeBackend : IPlayerBackend
{
    public readonly List<object?[]> Commands = new();
    public bool Running;
    public int Starts;
    public bool IsRunning => Running;
    public event Action<string, JsonNode?>? EventReceived;
    public event Action<string, JsonNode?>? PropertyChanged;
    public event Action? Exited;

    public Task StartAsync(string? target, AppSettings settings, CancellationToken ct = default)
    { Running = true; Starts++; return Task.CompletedTask; }

    /// <summary>Stuck in the engine: answers nothing, like a frozen mpv. A restart clears it.</summary>
    public bool Frozen;

    public Task<JsonNode?> CommandAsync(params object?[] command)
    {
        Commands.Add(command);
        if (Frozen) return Task.FromException<JsonNode?>(new TimeoutException("mpv did not answer"));
        return Task.FromResult<JsonNode?>(JsonValue.Create(true));
    }

    public EngineSettings? Applied;
    public Task ApplyAsync(EngineSettings engine) { Applied = engine; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Running = false; Frozen = false; return ValueTask.CompletedTask; }

    public void Prop(string name, JsonNode? value) => PropertyChanged?.Invoke(name, value);
    public void Event(string name, string json = "{}") => EventReceived?.Invoke(name, JsonNode.Parse(json));
    public void Crash() { Running = false; Exited?.Invoke(); }

    public object?[]? Last(string cmd) => Commands.LastOrDefault(c => c[0] as string == cmd);
    public object?[]? LastSet(string prop) => Commands.LastOrDefault(c => c[0] as string == "set_property" && c[1] as string == prop);
}

internal sealed class FakeHost : IUiHost
{
    public string? Clipboard;
    public List<string> Opened = new();
    public IReadOnlyList<string>? Files;
    public void Post(Action action) => action();
    public IReadOnlyList<string>? PickVideoFiles() => Files;
    public string? PickSubtitleFile() => "/tmp/subs.srt";
    public string? ClipboardText() => Clipboard;
    public void OpenExternal(string pathOrUrl) => Opened.Add(pathOrUrl);
    public List<string> Ran = new();
    public void RunInConsole(string script, string arguments) => Ran.Add($"{Path.GetFileName(script)} {arguments}");
}

internal static class ViewModelTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nm-vm-" + Guid.NewGuid().ToString("N")[..6]);
        var store = new SettingsStore(dir);
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        var fake = new FakeBackend();
        var host = new FakeHost();
        var rtDir = Path.Combine(dir, "runtime");
        Directory.CreateDirectory(Path.Combine(rtDir, "models", "rife-v4.6_ensembleFalse"));
        File.WriteAllText(Path.Combine(rtDir, "novamotion.vpy"), "");
        File.WriteAllText(Path.Combine(rtDir, "models", "rife-v4.6_ensembleFalse", "flownet.param"), "");
        // On Windows the MVTools engine counts as installed only when its plugin is there
        Directory.CreateDirectory(Path.Combine(rtDir, "mpv", "vs-plugins"));
        File.WriteAllText(Path.Combine(rtDir, "mpv", "vs-plugins", "libmvtools.dll"), "");
        var vm = new MainViewModel(host, store, RuntimeLayout.Find(dir), () => fake, () => now);
        check(vm.Engines.Single(e => e.Value == "rife_trt").Available == false && vm.Engines.Single(e => e.Value == "rife_trt").Badge == "Needs setup",
              "vm: TensorRT shown as needing setup when not installed");
        vm.SelectEngineCommand.Execute(vm.Engines.Single(e => e.Value == "rife_trt"));
        check(vm.Settings.Active.Engine.Engine == "rife", "vm: an engine that isn't installed can't be chosen");
        vm.SetGpu(new GpuInfo(new[] { "NVIDIA GeForce RTX 4070" }));

        check(!vm.HasMedia && vm.EngineState == EngineState.Idle, "vm: starts on the home screen");
        check(vm.CadenceTicks.Count == 5 && vm.CadenceTicks.Count(t => !t.Generated) == 1, "vm: idle cadence shows 24 → 60 (5 ticks, 1 real)");

        // ---- open a link
        vm.TargetText = "  https://www.youtube.com/watch?v=abc  ";
        check(vm.PlayCommand.CanExecute(null), "vm: Play enabled once text is typed");
        vm.PlayCommand.Execute(null);
        await Task.Delay(20);
        check(fake.Starts == 1 && fake.Last("loadfile")?[1] as string == "https://www.youtube.com/watch?v=abc", "vm: link trimmed and loaded");
        check(vm.HasMedia && vm.IsOpening && vm.BusyText == "Fetching video…", "vm: shows opening state for links");
        check(vm.Title == "youtube.com" && vm.Origin == "youtube.com", "vm: placeholder title before mpv reports one");
        check(fake.LastSet("start")?[2] as string == "none", "vm: new video starts at the beginning");

        fake.Prop("idle-active", false);
        fake.Event("file-loaded");
        fake.Prop("media-title", "Big Buck Bunny");
        fake.Prop("duration", 600);
        fake.Prop("time-pos", 42.5);
        fake.Prop("container-fps", 25);
        fake.Prop("display-fps", 60);
        fake.Prop("video-params/w", 1920);
        fake.Prop("video-params/h", 1080);
        check(!vm.IsOpening && vm.Title == "Big Buck Bunny", "vm: title from mpv");
        check(vm.PositionText == "0:42" && vm.DurationText == "10:00" && Math.Abs(vm.SeekValue - 42.5) < 1e-9, "vm: time readouts and seek bar follow playback");
        check(vm.EngineState == EngineState.Preparing, "vm: engine preparing right after load");

        fake.Prop("vf", JsonNode.Parse("[{\"label\":\"novamotion\",\"name\":\"vapoursynth\",\"enabled\":true}]"));
        fake.Prop("estimated-vf-fps", 59.9);
        check(vm.EngineState == EngineState.Active && vm.BadgeText == "25 → 60 fps", $"vm: active engine badge ({vm.BadgeText})");
        vm.Demo = true;
        check(vm.ShowDemoLabels && vm.DemoLeftLabel == "Original · 25 fps" && vm.DemoRightLabel == "NOVA Motion · 60 fps" && vm.DemoSideBySide,
              $"vm: before/after labels ({vm.DemoLeftLabel} | {vm.DemoRightLabel})");
        vm.DemoSideBySide = false;
        check(fake.Applied?.DemoMode == "split", "vm: before/after style reaches the engine");
        vm.DemoSideBySide = true;
        vm.Demo = false;
        check(!vm.ShowDemoLabels && fake.Applied?.Demo == false, "vm: labels go with before/after off");
        check(vm.CadenceTicks.Count == 12 && vm.CadenceTicks.Count(t => t.Generated) == 11, "vm: 25 → 60 cadence is 12 frames, 11 generated");
        check(vm.GeneratedShareText.StartsWith("92"), $"vm: generated share text ({vm.GeneratedShareText})");

        fake.Prop("vf", JsonNode.Parse("[{\"label\":\"novamotion\",\"name\":\"vapoursynth\",\"enabled\":false}]"));
        fake.Prop("estimated-vf-fps", 25);
        now = now.AddSeconds(10);
        vm.Tick();
        check(vm.EngineState == EngineState.Failed && vm.EngineProblem, "vm: disabled filter shows as a failed engine");

        // ---- keeping playback smooth: frames dropping steps down to a lighter TensorRT model
        vm.Settings.Active.Engine.Engine = "rife_trt";
        vm.Settings.Active.Engine.TrtModel = "auto";
        fake.Prop("vf", JsonNode.Parse("[{\"label\":\"novamotion\",\"enabled\":true}]"));
        fake.Prop("estimated-vf-fps", 60);
        vm.Tick();
        // Drops in the first seconds after the engine starts are the player catching up: ignored
        for (var i = 0; i < 8; i++) { now = now.AddSeconds(0.5); fake.Prop("frame-drop-count", 40 * (i + 1)); vm.Tick(); }
        check(vm.Settings.Active.Engine.QualityStep == 0, "vm: drops right after the engine starts don't step down");
        for (var i = 0; i < 6; i++) { now = now.AddSeconds(0.5); vm.Tick(); }
        var dropsAt = 320;
        for (var i = 0; i < 30 && vm.Settings.Active.Engine.QualityStep == 0; i++) { now = now.AddSeconds(0.5); dropsAt += 5; fake.Prop("frame-drop-count", dropsAt); vm.Tick(); }
        check(vm.Settings.Active.Engine.QualityStep == 1 && fake.Applied?.QualityStep == 1 && vm.ToastText.Contains("lighter model"),
              $"vm: steady dropped frames switch to a lighter model (step {vm.Settings.Active.Engine.QualityStep}, rate {vm.DropRate:0.#})");
        for (var i = 0; i < 6; i++) { now = now.AddSeconds(0.5); dropsAt += 5; fake.Prop("frame-drop-count", dropsAt); vm.Tick(); }
        check(vm.Settings.Active.Engine.QualityStep == 1, "vm: waits for the lighter model to settle before stepping again");
        for (var i = 0; i < 40; i++) { now = now.AddSeconds(0.5); fake.Prop("frame-drop-count", dropsAt); vm.Tick(); }
        check(vm.Settings.Active.Engine.QualityStep == 1 && vm.DropText == "None", $"vm: no drops, no more steps ({vm.DropText}, step {vm.Settings.Active.Engine.QualityStep})");
        // Drops that are all "mistimed" (display sync, not a slow GPU): switch sync, keep the model
        var mist = 0;
        for (var i = 0; i < 16; i++) { now = now.AddSeconds(0.5); mist += 15; dropsAt += 15; fake.Prop("frame-drop-count", dropsAt); fake.Prop("mistimed-frame-count", mist); vm.Tick(); }
        check(vm.Settings.Active.Engine.QualityStep == 1 && vm.Settings.VideoSync == "audio"
              && fake.LastSet("video-sync")?[2] as string == "audio" && vm.ToastText.Contains("refresh timing"),
              $"vm: mistimed drops switch video sync to timestamps instead of a lighter model (step {vm.Settings.Active.Engine.QualityStep}, sync '{vm.Settings.VideoSync}')");
        // No lighter model to go to (MVTools here): late frames lower the frame rate instead
        vm.Settings.Active.Engine.Engine = "mvtools";
        for (var i = 0; i < 30; i++) { now = now.AddSeconds(0.5); dropsAt += 10; fake.Prop("frame-drop-count", dropsAt); vm.Tick(); }
        check(vm.Settings.Active.Engine.RateStep >= 1 && fake.Applied?.RateStep >= 1 && vm.ToastText.Contains("lowered the frame rate"),
              $"vm: steady late frames with no lighter model lower the frame rate (rate step {vm.Settings.Active.Engine.RateStep})");

        // ---- seek bar scrubbing
        vm.IsScrubbing = true;
        vm.SeekValue = 300;
        fake.Prop("time-pos", 43);
        check(Math.Abs(vm.SeekValue - 300) < 1e-9 && vm.PositionText == "5:00", "vm: playback doesn't fight a drag in progress");
        vm.IsScrubbing = false;
        await Task.Delay(10);
        var seek = fake.Last("seek");
        check(seek is not null && Convert.ToDouble(seek[1]) == 300 && seek[2] as string == "absolute+exact", "vm: releasing the seek bar seeks there");
        check(vm.TimeAt(0.25) == "2:30", "vm: hover preview time");

        // ---- volume
        fake.Prop("volume", 80);
        check(vm.Volume == 80 && fake.LastSet("volume") is null, "vm: volume from mpv does not echo back");
        vm.Volume = 40;
        check(fake.LastSet("volume")?[2] is double v && v == 40 && vm.VolumeGlyph == Glyphs.Volume2, "vm: volume slider sets mpv volume");
        fake.Prop("mute", true);
        check(vm.VolumeGlyph == Glyphs.Mute && vm.VolumeText == "Muted", "vm: mute glyph");

        // ---- tracks
        fake.Prop("track-list", JsonNode.Parse("""
            [{"id":1,"type":"video","selected":true},
             {"id":1,"type":"audio","lang":"jpn","codec":"aac","demux-channel-count":2,"selected":true},
             {"id":2,"type":"audio","lang":"eng","title":"Dub","codec":"eac3","demux-channel-count":6},
             {"id":1,"type":"sub","lang":"eng","codec":"subrip","selected":true},
             {"id":2,"type":"sub","lang":"ja","codec":"ass","external":true}]
            """));
        check(vm.AudioTracks.Count == 2 && vm.AudioTracks[0].Label == "Japanese" && vm.AudioTracks[1].Label == "Dub (English)", "vm: audio track labels");
        check(vm.AudioTracks[1].Detail == "E-AC-3 · 5.1", $"vm: audio track detail ({vm.AudioTracks[1].Detail})");
        check(vm.SubtitleTracks.Count == 3 && vm.SubtitleTracks[0].Label == "Off" && vm.SubtitleSummary == "English", "vm: subtitle list with Off");
        check(vm.SubtitleTracks[2].Detail == "ASS · external", "vm: external subtitle detail");
        vm.SelectSubtitleCommand.Execute(vm.SubtitleTracks[0]);
        check(fake.LastSet("sid")?[2] as string == "no", "vm: choosing Off turns subtitles off");
        vm.SelectAudioCommand.Execute(vm.AudioTracks[1]);
        check(fake.LastSet("aid")?[2] is int aid && aid == 2, "vm: choosing an audio track");

        // ---- keyboard
        await vm.HandleAsync(PlayerAction.SeekForward);
        check(fake.Last("seek")?[1] is double s5 && s5 == 5 && fake.Last("seek")?[2] as string == "relative", "vm: → seeks 5 s");
        await vm.HandleAsync(PlayerAction.SeekPercent, 50);
        check(fake.Last("seek")?[2] as string == "absolute-percent", "vm: 5 jumps to 50 %");
        await vm.HandleAsync(PlayerAction.ToggleFullscreen);
        check(vm.IsFullscreen && vm.FullscreenGlyph == Glyphs.ExitFullScreen, "vm: F toggles full screen");
        vm.ToggleDrawerCommand.Execute("motion");
        await vm.HandleAsync(PlayerAction.ExitFullscreen);
        check(vm.IsFullscreen && !vm.IsDrawerOpen, "vm: Esc closes an open panel first");
        await vm.HandleAsync(PlayerAction.ExitFullscreen);
        check(!vm.IsFullscreen, "vm: then Esc leaves full screen");
        await vm.HandleAsync(PlayerAction.ToggleSmoothing);
        check(!vm.SmoothingEnabled && fake.Applied?.Enabled == false && vm.ToastText == "Smoothing off", "vm: Ctrl+S turns smoothing off and tells mpv");
        check(vm.EngineState == EngineState.Off, "vm: engine state Off");
        await vm.HandleAsync(PlayerAction.ToggleSmoothing);
        check(KeyMap.Resolve("Space", false, false, false).Action == PlayerAction.TogglePause
              && KeyMap.Resolve("Right", false, true, false).Action == PlayerAction.SeekForwardLong
              && KeyMap.Resolve("D7", false, false, false) == (PlayerAction.SeekPercent, 70)
              && KeyMap.Resolve("S", true, false, false).Action == PlayerAction.ToggleSmoothing, "keymap: common shortcuts");

        // ---- motion settings
        var mv = vm.Engines.First(e => e.Value == "mvtools");
        vm.SelectEngineCommand.Execute(mv);
        check(fake.Applied?.Engine == "mvtools" && mv.IsSelected && vm.IsMvTools, "vm: choosing an engine applies it");
        var p60 = vm.Targets.First(t => t.Value == "60");
        vm.SelectTargetCommand.Execute(p60);
        check(fake.Applied?.TargetText == "60" && vm.TargetName == "60 fps", "vm: choosing a target applies it");
        var anime = vm.Profiles.First(p => p.Value == "Anime");
        vm.SelectProfileCommand.Execute(anime);
        check(vm.ActiveProfileName == "Anime" && vm.Profiles.Single(p => p.IsSelected).Value == "Anime", "vm: switching profile");
        var json = JsonNode.Parse(File.ReadAllText(store.EngineConfigPath))!;
        check(json["target"]!.ToString() == "x2", "vm: profile switch rewrites engine.json");

        // ---- history and resume
        fake.Prop("time-pos", 120);
        now = now.AddSeconds(15);
        vm.Tick();
        var h = new MediaHistory(dir);
        check(h.Items.Count == 1 && h.Items[0].Title == "Big Buck Bunny" && Math.Abs(h.Items[0].Position - 120) < 1e-9, "vm: position saved while playing");
        await vm.StopAsync();
        fake.Prop("idle-active", true);
        check(!vm.HasMedia && vm.Recent.Count == 1 && vm.Recent[0].Status == "8:00 left", $"vm: stop returns home with recent item ({vm.Recent.FirstOrDefault()?.Status})");
        check(vm.ContinueItem?.Title == "Big Buck Bunny", "vm: continue watching");
        vm.PlayRecentCommand.Execute(vm.Recent[0]);
        await Task.Delay(10);
        check(fake.LastSet("start")?[2] as string == "117", $"vm: resumes 3 s before where you stopped ({fake.LastSet("start")?[2]})");
        fake.Prop("idle-active", false);
        fake.Event("file-loaded");
        check(vm.ToastText == "Resumed at 1:57" && vm.HasToastAction, "vm: resume toast with Start over");
        vm.ToastActionCommand.Execute(null);
        await Task.Delay(10);
        check(fake.Last("seek")?[1] is double z && z == 0 && !vm.ToastVisible, "vm: Start over seeks to 0");

        // ---- queue and end of file
        var vids = Path.Combine(dir, "show");
        Directory.CreateDirectory(vids);
        foreach (var n in new[] { "Ep 10.mkv", "Ep 2.mkv", "Ep 1.mkv", "notes.txt" }) File.WriteAllText(Path.Combine(vids, n), "");
        await vm.OpenAsync(new[] { Path.Combine(vids, "Ep 2.mkv") });
        check(vm.QueueItems.Select(q => Path.GetFileName(q.Target)).SequenceEqual(new[] { "Ep 2.mkv", "Ep 10.mkv" }), "vm: a file brings the rest of its folder, in natural order");
        check(vm.HasNext && vm.NextTitle == "Ep 10" && vm.QueueSummary == "1 of 2", "vm: next-up info");
        fake.Prop("idle-active", false);
        fake.Event("file-loaded");
        fake.Prop("duration", 1400);
        fake.Prop("time-pos", 1400);
        fake.Prop("eof-reached", true);
        await Task.Delay(10);
        check((fake.Last("loadfile")?[1] as string)?.EndsWith("Ep 10.mkv") == true && vm.QueueItems[1].IsCurrent, "vm: end of file plays the next one");
        check(new MediaHistory(dir).Find(Path.Combine(vids, "Ep 2.mkv"))?.Finished == true, "vm: finished video marked watched");
        fake.Prop("eof-reached", false);

        // ---- errors and crashes
        fake.Event("end-file", "{\"reason\":\"error\",\"file_error\":\"loading failed\"}");
        check(vm.ToastText.StartsWith("This file couldn't be opened"), "vm: friendly error for a broken file");
        fake.Crash();
        check(!vm.HasMedia && vm.ToastText.Contains("stopped unexpectedly") && vm.HasToastAction, "vm: player crash returns home with a way to the logs");
        await vm.OpenAsync(new[] { "https://vimeo.com/1" });
        check(fake.Starts == 2, "vm: player restarts after a crash");
        check(vm.Settings.Active.Engine.QualityStep == 0, "vm: every new video starts on the best model again");
        fake.Prop("idle-active", false);
        fake.Event("file-loaded");
        fake.Prop("time-pos", 61);
        vm.SelectQualityNowCommand.Execute(vm.OnlineHeights.First(o => o.Value == "720"));
        await Task.Delay(10);
        check((fake.LastSet("ytdl-format")?[2] as string)?.StartsWith("bestvideo[height<=?720]") == true
              && fake.LastSet("start")?[2] as string == "61" && vm.Settings.OnlineMaxHeight == 720,
              "vm: changing quality reloads the link at the same moment");

        // ---- clipboard
        await vm.StopAsync();
        fake.Prop("idle-active", true);
        host.Clipboard = "https://www.twitch.tv/videos/1";
        vm.CheckClipboard();
        check(vm.HasClipboardLink && vm.ClipboardOrigin == "twitch.tv", "vm: offers a link from the clipboard");
        vm.DismissClipboardCommand.Execute(null);
        vm.CheckClipboard();
        check(!vm.HasClipboardLink, "vm: a dismissed clipboard link stays dismissed");
        host.Clipboard = "not a link";
        vm.CheckClipboard();
        check(!vm.HasClipboardLink, "vm: ignores clipboard text that isn't a link");

        // ---- toasts expire
        vm.ShowToast("hello");
        now = now.AddSeconds(5);
        vm.Tick();
        check(!vm.ToastVisible, "vm: toasts disappear");

        await vm.DisposeAsync();
        check(!fake.Running, "vm: closing stops the player");

        // ---- pure helpers
        check(Cadence.Pattern(24, 48).SequenceEqual(new[] { false, true }), "cadence: 2× alternates");
        check(Cadence.Pattern(23.976, 59.94).Count == 5, "cadence: NTSC 24 → 60 is 2:5");
        check(Cadence.Pattern(30, 30).Count == 0, "cadence: no pattern without generated frames");
        check(TimeFormat.Format(3723) == "1:02:03" && TimeFormat.Format(65) == "1:05" && TimeFormat.Format(double.NaN) == "0:00", "time format");
        check(TimeFormat.Ago(now.AddDays(-1), now) == "Yesterday" && TimeFormat.Ago(now.AddDays(-3), now) == "3 days ago", "time ago");
        check(EngineCatalog.ModelName("rife-v4.22_lite_ensembleFalse") == "RIFE 4.22 Lite", "model names");
        check(TensorRtEngines.PaddedSize(1920, 1080) == "1920x1088" && TensorRtEngines.Describe("1920x1088") == "1080p", "TensorRT padded sizes");
        check(MediaSource.Origin("https://m.youtube.com/watch?v=1") == "youtube.com" && MediaSource.AsLink(" \"https://a.b/c\" ") == "https://a.b/c", "media source helpers");
        check(new NaturalComparer().Compare("Ep 9", "Ep 10") < 0 && new NaturalComparer().Compare("a02", "a1") > 0, "natural sort");

        var bench = BenchmarkLog.Parse("""
            ==> rife_trt
                Output 960 frames in 11.8 seconds (81.3 fps)
                => 81.3 fps  (real-time with headroom)
            ==> rife
                HUNG: no result after 300 s (killed)
            ==> mvtools
                => 94.0 fps  (real-time with headroom)
            """);
        check(bench.Count == 3 && bench[0].Fps == 81.3 && bench[1].Fps is null && bench[2].Fps == 94, "benchmark log parsed");
        var laptop = BenchmarkLog.Parse("""
            ==> rife_trt
                => 20.4 fps  (TOO SLOW for 60 fps)
            ==> rife_trt@0.5
                => 71.2 fps  (real-time with headroom)
            ==> rife@0
                => 5.4 fps  (TOO SLOW for 60 fps)
            ==> rife@1
                => 22.8 fps  (TOO SLOW for 60 fps)
            ==> rife@2
                (no Vulkan device 2)
            ==> mvtools
                => 77.4 fps  (real-time with headroom)
            """);
        check(laptop.Count == 5 && BenchmarkLog.FastestVulkanDevice(laptop) == 1 && BenchmarkLog.FastestVulkanDevice(bench) is null,
              "benchmark: the faster of a laptop's two graphics chips is found for Vulkan");
        check(EngineCatalog.EngineName("rife_trt@0.5") == "RIFE · TensorRT, AI at 50% size" && EngineCatalog.EngineName("rife@1") == "RIFE · Vulkan, graphics chip 1"
              && EngineCatalog.EngineName("mvtools") == "MVTools" && EngineCatalog.EngineName("rife_half@1") == "RIFE · Vulkan, AI at 50% size, graphics chip 1", $"benchmark: row names ({EngineCatalog.EngineName("rife_trt@0.5")})");

        check(YtDlp.ParseUpdate("Current version: stable@2026.09.01\nUpdating to stable@2026.10.10 ...\nUpdated yt-dlp to stable@2026.10.10") == (YtDlp.Outcome.Updated, "2026.10.10"),
              "yt-dlp: update output parsed");
        check(YtDlp.ParseUpdate("Latest version: stable@2026.10.10\nyt-dlp is up to date (stable@2026.10.10)") == (YtDlp.Outcome.UpToDate, "2026.10.10"),
              "yt-dlp: up-to-date output parsed");
        check(YtDlp.ParseUpdate("ERROR: Unable to download").Result == YtDlp.Outcome.Failed, "yt-dlp: failure detected");
        var ys = new AppSettings { YtDlpCheckedUtc = now.AddHours(-5) };
        check(!YtDlp.Due(ys, now) && YtDlp.Due(new AppSettings(), now) && !YtDlp.Due(new AppSettings { AutoUpdateYtDlp = false }, now),
              "yt-dlp: checked at most once a day, and only when enabled");
        check(MpvPlayer.YtdlFormat(720).StartsWith("bestvideo[height<=?720][vcodec!*=av01]"), "online format string");

        var q = new PlayQueue();
        q.Set(new[] { "a", "b", "c", "d" });
        q.Jump(2); q.Move(3, 0);
        check(q.Current == "c" && q.Index == 3 && string.Join("", q.Items) == "dabc", "queue: move keeps the current item");
        q.RemoveAt(0);
        check(q.Current == "c" && q.Index == 2, "queue: removing before current shifts index");

        Directory.Delete(dir, true);
    }
}
