// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
// Enhance's AI clean-up and upscaling: the export plan, setup detection, the page and the preview.
using System.Text.Json;
using NovaMotion.Core;
using NovaMotion.Core.ViewModels;

internal static class EnhanceTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nm-enhance-" + Guid.NewGuid().ToString("N")[..6]);
        var rtDir = Path.Combine(dir, "runtime");
        Directory.CreateDirectory(rtDir);
        File.WriteAllText(Path.Combine(rtDir, "novamotion.vpy"), "");
        var rt = RuntimeLayout.Find(dir)!;

        // ---- export plan
        var film = new EnhanceSettings { Preset = "film", Height = 2160, Clean = "strong" };
        var o = new ExportOptions { Target = "60", Enhance = film };
        var e = ExportPlan.Engine(new EngineSettings(), o, new EngineAvailability(true, 1, true, true, Array.Empty<string>()), Array.Empty<string>());
        var json = JsonSerializer.Serialize(e);
        check(e.Enhance == film && !ReferenceEquals(e.Enhance, film), "enhance: the engine gets a copy of the choices");
        check(json.Contains("\"enhance\"") && json.Contains("\"preset\":\"film\"") && json.Contains("\"height\":2160") && json.Contains("\"clean\":\"strong\"") && json.Contains("\"strength\":\"balanced\""),
              "enhance: written for the engine as it reads it");
        var plain = ExportPlan.Engine(new EngineSettings(), new ExportOptions(), new EngineAvailability(true, 1, true, true, Array.Empty<string>()), Array.Empty<string>());
        check(plain.Enhance is null && !JsonSerializer.Serialize(plain).Contains("\"enhance\""), "enhance: off leaves the engine config as before");
        check(ExportPlan.Engine(new EngineSettings(), new ExportOptions { Enhance = new EnhanceSettings() }, new EngineAvailability(true, 1, true, true, Array.Empty<string>()), Array.Empty<string>()).Enhance is null,
              "enhance: preset off counts as off");

        check(ExportPlan.OutputPath(@"C:\v\Match.mkv", o, _ => false).EndsWith("Match (NOVA Enhance 4K 60 fps).mp4"), "enhance: file named for what it is");
        string Args(ExportOptions x) => string.Join(" ", ExportPlan.MpvArguments(rt, "in.mp4", "out.mp4", "pipe", "log", x));
        check(Args(new ExportOptions { Comparison = true }).Contains("min(720,ih)"), "before/after without enhance: 720p to share");
        check(!Args(new ExportOptions { Comparison = true, Enhance = film }).Contains("min(720,ih)"), "before/after with enhance: full-size crop, not shrunk");
        check(ExportPlan.Waiting(0, TimeSpan.FromMinutes(2), o)?.Contains("Preparing the AI models") == true
              && ExportPlan.Waiting(0, TimeSpan.FromSeconds(10), o) is null && ExportPlan.Waiting(0.2, TimeSpan.FromMinutes(5), o) is null,
              "export: a long wait before the first frame says the models are being prepared");
        check(EnhanceCatalog.Describe(film) == "Film & live action · 4K · strong clean-up · balanced", $"enhance: described ({EnhanceCatalog.Describe(film)})");

        // ---- setup detection
        check(!EnhanceCatalog.IsReady(rt), "enhance: not ready before setup-enhance.cmd");
        var plugins = Path.Combine(rt.MpvDir, "vs-plugins");
        foreach (var m in EnhanceCatalog.RequiredModels)
        {
            var p = Path.Combine(plugins, "models", m.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "");
        }
        File.WriteAllText(Path.Combine(rt.MpvDir, "vsmlrt.py"), "");
        File.WriteAllText(Path.Combine(rt.Root, "enhance.py"), "");
        check(!EnhanceCatalog.IsReady(rt), "enhance: models alone aren't enough without a way to run them");
        File.WriteAllText(Path.Combine(plugins, "vsncnn.dll"), "");
        check(EnhanceCatalog.IsReady(rt), "enhance: ready with the models, vsmlrt.py and Vulkan (vsncnn)");

        // ---- the page
        var host = new FakeHost();
        var exporter = new FakeExporter();
        var backend = new FakeBackend();
        var vm = new MainViewModel(host, new SettingsStore(Path.Combine(dir, "settings")), rt, () => backend, () => DateTime.UtcNow, exporter);
        check(!vm.EnhanceOn && !vm.ShowEnhanceOptions && vm.EnhancePresets.Single(p => p.IsSelected).Value == "off", "enhance: starts as smooth only");
        check(vm.EnhanceStrengths.Single(p => p.IsSelected).Value == "balanced" && vm.EnhanceDetails.Single(p => p.IsSelected).Label == "Off",
              "enhance: balanced AI strength and no extra sharpening by default");
        vm.SelectEnhancePresetCommand.Execute(vm.EnhancePresets.Single(p => p.Value == "anime"));
        check(vm.EnhanceOn && vm.ShowEnhanceOptions && !vm.EnhanceNeedsSetup && vm.EnhancePresetNote.Contains("Real-CUGAN"), "enhance: anime chosen, options shown");
        check(vm.EnhanceSizes.Single(p => p.IsSelected).Label == "4K", "enhance: 4K by default");
        vm.SelectEnhanceSizeCommand.Execute(vm.EnhanceSizes.Single(p => p.Label == "1440p"));
        vm.SelectEnhanceCleanupCommand.Execute(vm.EnhanceCleanups.Single(p => p.Value == "off"));
        vm.SelectEnhanceGrainCommand.Execute(vm.EnhanceGrains.Single(p => p.Label == "Film"));
        vm.EnhanceFixCompression = true;
        vm.SelectEnhanceStrengthCommand.Execute(vm.EnhanceStrengths.Single(p => p.Value == "full"));
        check(vm.Settings.Enhance == new EnhanceSettings { Preset = "anime", Height = 1440, Clean = "off", Detail = 0, Grain = 0.7, FixCompression = true,
                                                           Strength = "full", Tuning = EnhanceSettings.CurrentTuning },
              $"enhance: choices kept in settings ({vm.Settings.Enhance})");
        var again = new MainViewModel(host, new SettingsStore(Path.Combine(dir, "settings")), rt, () => backend, () => DateTime.UtcNow, exporter);
        check(again.EnhancePresets.Single(p => p.IsSelected).Value == "anime" && again.EnhanceSizes.Single(p => p.IsSelected).Label == "1440p",
              "enhance: the page opens as it was left");

        var video = Path.Combine(dir, "Episode 01.mkv");
        File.WriteAllText(video, "");
        vm.QueueExports(new[] { video });
        await Task.Delay(50);
        var run = exporter.Runs.Last();
        check(run.Options.Enhance is { Preset: "anime", Height: 1440 } && run.Engine.Enhance is { Preset: "anime" }, "enhance: exports use the choices");
        check(vm.Exports.Last().Summary.StartsWith("Anime & art · 1440p · grain kept · full AI · compression fixed"), $"enhance: shown in the list ({vm.Exports.Last().Summary})");

        // ---- preview: 4 s before/after, in a temporary folder, played when ready
        vm.PreviewFolder = Path.Combine(dir, "previews");
        host.Files = new[] { video };
        vm.ExportStartText = "1:30";
        vm.PreviewEnhanceCommand.Execute(null);
        await Task.Delay(80);
        run = exporter.Runs.Last();
        check(run.Options.Preview && run.Options.Comparison && run.Options.LengthSeconds == 4 && run.Options.StartSeconds == 90,
              "preview: a 4-second before/after from the chosen time");
        check(run.Out.StartsWith(vm.PreviewFolder), $"preview: made in a temporary folder ({run.Out})");
        check(backend.Last("loadfile")?[1] as string == run.Out && vm.IsMotionModule, "preview: plays in Motion when ready");

        // ---- live enhance in Motion: switched on the running player, remembered
        vm.SetLiveEnhance("crisp");
        await Task.Delay(50);
        check(backend.Commands.Any(c => c[0] as string == "change-list" && c[1] as string == "glsl-shaders" && (c[3] as string)!.EndsWith("nova-crisp.glsl")),
              "live enhance: sent to the running player");
        check(vm.Settings.LiveEnhance == "crisp" && vm.LiveEnhanceOptions.Single(o => o.IsSelected).Value == "crisp" && vm.LiveEnhanceNote.Contains("sharpening"),
              "live enhance: chosen and described");
        vm.SetLiveEnhance("nonsense");
        check(vm.Settings.LiveEnhance == "off", "live enhance: unknown modes are off");

        // ---- an engine script that failed: mpv went on without it, so the export must not pass as enhanced
        var failLog = Path.Combine(dir, "export-fail.log");
        File.WriteAllLines(failLog, new[]
        {
            "[   2.1][e][vapoursynth] Script evaluation failed:",
            "[   2.1][e][vapoursynth] Traceback (most recent call last):",
            "[   2.1][e][vapoursynth] vapoursynth.Error: Enhance model missing: realesr-general-mid-x4v3.onnx (run setup-enhance.cmd)",
            "[   2.2][w][vf] Disabling filter novamotion because it has failed.",
        });
        var fail = ExportRunner.EngineFailure(failLog);
        check(fail?.Contains("Enhance model missing: realesr-general-mid-x4v3.onnx") == true && fail.StartsWith("The AI engine didn't run"),
              $"export: a failed engine script is reported, not passed off as enhanced ({fail})");
        var trtLog = Path.Combine(dir, "export-trt.log");
        File.WriteAllLines(trtLog, new[]
        {
            "[ 405.0][f][vapoursynth] Script evaluation failed:",
            @"[ 405.0][f][vapoursynth] RuntimeError: trtexec execution fails, log has been written to C:\Temp\trtexec.log",
        });
        check(ExportRunner.EngineFailure(trtLog)?.Contains("couldn't prepare a model for this graphics card") == true,
              "export: a failed TensorRT build is explained, with what to try");
        var okLog = Path.Combine(dir, "export-ok.log");
        File.WriteAllLines(okLog, new[] { "[   1.0][v][vapoursynth] script loaded", "[   9.0][i][cplayer] Exiting... (End of file)" });
        check(ExportRunner.EngineFailure(okLog) is null, "export: a clean log passes");

        // ---- one before/after frame: a PNG in seconds, no smoothing
        var still = new ExportOptions { Still = true, Enhance = film, StartSeconds = 12.5 };
        var stillEngine = ExportPlan.Engine(new EngineSettings(), still, new EngineAvailability(true, 1, true, true, Array.Empty<string>()), Array.Empty<string>());
        check(!stillEngine.Enabled && stillEngine.Demo && stillEngine.DemoLabels && stillEngine.Enhance is { Preset: "film" },
              "frame preview: the enhance before/after, nothing interpolated");
        var stillArgs = ExportPlan.MpvArguments(rt, "in.mp4", "out.png", "pipe", "log", still, null, 1);
        check(stillArgs.Contains("--ovc=png") && stillArgs.Contains("--of=image2") && stillArgs.Contains("--no-audio")
              && stillArgs.Contains("--frames=2") && stillArgs.Contains("--start=12.5") && !stillArgs.Any(a => a.StartsWith("--oac")),
              $"frame preview: one PNG at the chosen time ({string.Join(" ", stillArgs.Where(a => !a.StartsWith("--vf")))})");
        check(ExportPlan.OutputPath(@"C:\v\Clip.mp4", still, _ => false).EndsWith("Clip (NOVA Enhance frame 12.5 s).png"), "frame preview: named for its time");
        vm.ExportStartText = "";
        vm.PreviewFrameCommand.Execute(null);
        await Task.Delay(80);
        run = exporter.Runs.Last();
        check(run.Options.Still && run.Out.EndsWith(".png") && run.Out.StartsWith(vm.PreviewFolder), $"frame preview: queued as a still ({run.Out})");
        check(vm.HasPreviewVideo && vm.PreviewVideoName == "Episode 01.mkv", "frame preview: the chosen video is named, and Other video is offered");
        var other = Path.Combine(dir, "Clip 2.mp4");
        File.WriteAllText(other, "");
        host.Files = new[] { other };
        vm.ChangePreviewVideoCommand.Execute(null);
        await Task.Delay(80);
        check(exporter.Runs.Last().In == other && vm.PreviewVideoName == "Clip 2.mp4", "frame preview: Other video previews the new one");
        run = exporter.Runs.Last();
        check(vm.HasFramePreview && vm.FramePreview == run.Out && vm.FramePreviewStatus.Contains("Left of the amber line is the original"),
              $"frame preview: shown on the page ({vm.FramePreviewStatus})");
        Directory.CreateDirectory(Path.GetDirectoryName(run.Out)!);
        File.WriteAllText(run.Out, "png");      // the fake export makes no file
        host.Opened.Clear();
        vm.OpenFramePreviewCommand.Execute(null);
        check(host.Opened.LastOrDefault() == run.Out, "frame preview: opens larger on click");

        // ---- a link the site refuses: its own reason, not "isn't a video format"
        check(MainViewModel.SiteReason("ERROR: [youtube] PRkI15fIJ1w: This video is not available") == "This video is not available",
              "site error: yt-dlp's reason extracted");
        var msg = MainViewModel.FriendlyError("unrecognized file format", "https://www.youtube.com/watch?v=PRkI15fIJ1w", "This video is not available");
        check(msg.StartsWith("youtube.com won't play this video: This video is not available.") && msg.Contains("blocked in your country"), $"site error: explained ({msg})");
        check(!MainViewModel.FriendlyError("unrecognized file format", "https://youtu.be/x").Contains("video format"), "site error: an online link is never called a bad format");
        vm.TargetText = "https://www.youtube.com/watch?v=PRkI15fIJ1w";
        vm.PlayCommand.Execute(null);
        await Task.Delay(30);
        backend.Event("log-message", "{\"prefix\":\"ytdl_hook\",\"level\":\"error\",\"text\":\"ERROR: [youtube] PRkI15fIJ1w: This video is not available\"}");
        backend.Event("end-file", "{\"reason\":\"error\",\"file_error\":\"unrecognized file format\"}");
        check(vm.ToastText.Contains("This video is not available"), $"site error: shown to the user ({vm.ToastText})");

        // ---- a video size with no TensorRT engine yet: MVTools, then a switch once it's built
        var now = new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);
        var store2 = new SettingsStore(Path.Combine(dir, "s3"));
        var fb = new FakeBackend();
        var pvm = new MainViewModel(host, store2, rt, () => fb, () => now, exporter);
        foreach (var pr in pvm.Settings.Profiles) pr.Engine.Engine = "rife_trt";
        pvm.TargetText = video;
        pvm.PlayCommand.Execute(null);
        await Task.Delay(30);
        fb.Prop("idle-active", false);
        fb.Event("file-loaded");
        fb.Prop("container-fps", 24);
        fb.Prop("display-fps", 60);
        fb.Prop("video-params/w", 960);
        fb.Prop("video-params/h", 720);
        fb.Prop("vf", System.Text.Json.Nodes.JsonNode.Parse("[{\"label\":\"novamotion\",\"enabled\":true}]"));
        fb.Prop("estimated-vf-fps", 59.9);
        File.WriteAllText(store2.EngineStatePath, "{\"engine\":\"rife_trt\",\"model\":\"mvtools\",\"preparing\":{\"model\":\"v4_6\",\"width\":512,\"height\":384}}");
        now = now.AddSeconds(5); pvm.Tick();
        check(pvm.ModelText.Contains("being prepared") && fb.Applied is null, $"preparing: says so, keeps playing ({pvm.ModelText})");
        Directory.CreateDirectory(Path.Combine(rt.Root, "trt-engines"));
        File.WriteAllText(Path.Combine(rt.Root, "trt-engines", "novamotion-engines.json"), "{\"512x384\":{\"model\":\"v4_6\",\"models\":[\"v4_6\"]}}");
        now = now.AddSeconds(5); pvm.Tick();
        await Task.Delay(30);
        check(fb.Applied is not null && pvm.ToastText.Contains("ready"), $"preparing: switches to the AI once built ({pvm.ToastText})");
        fb.Applied = null;
        now = now.AddSeconds(5); pvm.Tick();
        await Task.Delay(30);
        check(fb.Applied is null, "preparing: switches only once");

        // ---- not set up: offers setup, exports go ahead without AI enhance
        var bare = new MainViewModel(host, new SettingsStore(Path.Combine(dir, "s2")), RuntimeLayout.Find(Path.GetTempPath()) ?? null, () => backend, () => DateTime.UtcNow, exporter);
        bare.SelectEnhancePresetCommand.Execute(bare.EnhancePresets.Single(p => p.Value == "film"));
        check(bare.EnhanceNeedsSetup && !bare.ShowEnhanceOptions, "enhance: without the models it offers to add them");
        bare.SetupEnhanceCommand.Execute(null);
        check(bare.Runtime is null || host.Ran.LastOrDefault()?.StartsWith("setup-enhance.cmd") == true, "enhance: setup runs setup-enhance.cmd");
        vm.SetupEnhanceCommand.Execute(null);
        check(host.Ran.LastOrDefault()?.StartsWith("setup-enhance.cmd") == true, "enhance: setup-enhance.cmd from the app");
    }
}
