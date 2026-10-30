// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using NovaMotion.Core;
using NovaMotion.Core.ViewModels;

internal sealed class FakeExporter : IExportRunner
{
    public readonly List<(string In, string Out, EngineSettings Engine, ExportOptions Options)> Runs = new();
    public Func<string, string?> Outcome = _ => null;
    public TaskCompletionSource? Gate;

    public async Task<string?> RunAsync(string input, string output, EngineSettings engine, ExportOptions options,
                                        IProgress<double> progress, CancellationToken ct)
    {
        Runs.Add((input, output, engine, options));
        progress.Report(0.5);
        if (Gate is not null)
        {
            try { await Gate.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { return "Cancelled"; }
        }
        progress.Report(1);
        return Outcome(input);
    }
}

internal static class ExportTests
{
    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        check(ExportRunner.ParseProbe("Playing: x.mp4\nNMPROBE 1440 23.976024\nExiting") == new SourceInfo(1440, 23.976024)
              && ExportRunner.ParseProbe("NMPROBE  ") == new SourceInfo(null, null), "export: frame count and rate read from mpv's probe");
        var e60 = new EngineSettings { TargetText = "60" };
        check(FrameRate.Output(e60, 25.510204) == new FrameRate(60, 1) && FrameRate.Snap(25.510204) == new FrameRate(25, 1) && FrameRate.Output(e60, 23.976) == new FrameRate(60, 1)
              && FrameRate.Output(new EngineSettings { TargetText = "x2" }, 23.976023) == new FrameRate(48000, 1001)
              && FrameRate.Output(new EngineSettings { TargetText = "x2" }, 25.34) == new FrameRate(50, 1)
              && FrameRate.Output(e60, 59.94) == new FrameRate(60000, 1001)
              && FrameRate.Output(new EngineSettings { TargetText = "60", Enabled = false }, 24) == new FrameRate(24, 1),
              "export: output rate follows the engine's rules (snap, x2, no change at 60)");

        check(!System.Text.Json.JsonSerializer.Serialize(new EngineSettings()).Contains("source_frames")
              && System.Text.Json.JsonSerializer.Serialize(new EngineSettings { SourceFrames = 50 }).Contains("\"source_frames\":50"),
              "export: source_frames only in export engine configs");
        // ---- plan
        var o = new ExportOptions { Target = "60" };
        var v = Path.Combine(Path.GetTempPath(), "v");          // real paths on every OS (Windows rewrites "/v")
        var outDir = Path.Combine(Path.GetTempPath(), "out");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(v, "Match (NOVA Motion 60 fps).mp4") };
        check(ExportPlan.OutputPath(Path.Combine(v, "Match.mkv"), o, p => taken.Contains(p)) == Path.Combine(v, "Match (NOVA Motion 60 fps) (2).mp4"),
              "export: output goes next to the original and never overwrites");
        check(ExportPlan.OutputPath(Path.Combine(v, "Match.mkv"), o with { Target = "x2", OutputFolder = outDir }, _ => false) == Path.Combine(outDir, "Match (NOVA Motion x2).mp4"),
              "export: output folder and 2x naming");
        var trt = new EngineAvailability(true, 3, true, true, Array.Empty<string>());
        var vulkan = new EngineAvailability(false, 3, true, true, Array.Empty<string>());
        var models = new[] { "rife-v4.22_lite_ensembleFalse", "rife-v4.6_ensembleFalse", "rife-v4.26_ensembleFalse" };
        var playing = new EngineSettings { Engine = "rife_trt", Demo = true, RtxVsr = true, QualityStep = 2, TrtModel = "auto" };
        var e = ExportPlan.Engine(playing, o, trt, models);
        check(e.Engine == "rife_trt" && e.TrtModel == "rife-v4.26" && !e.Demo && !e.RtxVsr && e.QualityStep == 0 && e.Dedup && e.SceneDetect && e.TargetText == "60",
              "export: best quality uses the newest TensorRT model, no demo/VSR/step-down");
        e = ExportPlan.Engine(playing, o, vulkan, models);
        check(e.Engine == "rife" && e.RifeModel == "rife-v4.26_ensembleFalse", $"export: without TensorRT, the newest full Vulkan model ({e.RifeModel})");
        e = ExportPlan.Engine(playing, o with { Quality = ExportQuality.Faster }, trt, models);
        check(e.TrtModel == "auto", "export: 'faster' uses the model live playback would");
        check(ExportPlan.Engine(playing, o, new EngineAvailability(false, 0, true, false, Array.Empty<string>()), Array.Empty<string>()).Engine == "mvtools",
              "export: MVTools when no RIFE is installed");
        var rtDir = Path.Combine(Path.GetTempPath(), "nm-ex-" + Guid.NewGuid().ToString("N")[..6], "runtime");
        Directory.CreateDirectory(rtDir);
        File.WriteAllText(Path.Combine(rtDir, "novamotion.vpy"), "");
        var rt = RuntimeLayout.Find(Path.GetDirectoryName(rtDir))!;
        var args = ExportPlan.MpvArguments(rt, "/v/a b.mkv", "/v/out.mp4", "pipe", "log", o);
        check(args[^1] == "/v/a b.mkv" && args[^2] == "--" && args.Contains("--o=/v/out.mp4") && args.Contains("--no-config") && args.Contains("--idle=no")
              && args.Contains("--ovc=hevc_nvenc") && args.Any(a => a.StartsWith("--vf=lavfi=[tpad=stop_mode=clone:stop=8],@novamotion:vapoursynth=file=%")),
              "export: mpv encodes through the engine, input after --");
        check(ExportPlan.MpvArguments(rt, "i", "o", "p", "l", o with { HardwareEncoder = false }).Contains("--ovc=libx264"), "export: CPU encoder when asked");
        check(ExportOptions.ParseTime("3:15") == 195 && ExportOptions.ParseTime("1:02:03") == 3723 && ExportOptions.ParseTime("20") == 20
              && ExportOptions.ParseTime("") is null && ExportOptions.ParseTime("abc") is null, "export: times read as 3:15 or seconds");
        var cmp = o with { Comparison = true, StartSeconds = 195, LengthSeconds = 20 };
        var cmpArgs = ExportPlan.MpvArguments(rt, "i", "o", "p", "l", cmp);
        check(cmpArgs.Contains("--start=195") && cmpArgs.Contains("--length=20") && cmpArgs.Contains("--ovc=h264_nvenc")
              && cmpArgs.Any(a => a.Contains("scale=-2:'min(720,ih)'"))
              && ExportPlan.OutputPath("/v/Tears of Steel.mkv", cmp, _ => false).EndsWith("Tears of Steel (NOVA Motion before-after 60 fps).mp4"),
              "export: a before/after part of a video: start, length and its own file name");
        check(ExportPlan.MpvArguments(rt, "i", "o", "p", "l", o, new FrameRate(60000, 1001)).Any(x => x.EndsWith(",lavfi=[setpts=N*1001/(60000*TB)]")),
              "export: frames numbered at the output rate after the engine");
        check(FrameRate.Frames(50, new FrameRate(25, 1), new FrameRate(60, 1)) == 120
              && FrameRate.Frames(1000, new FrameRate(24000, 1001), new FrameRate(60, 1)) == 2502
              && ExportPlan.MpvArguments(rt, "i", "o", "p", "l", o, new FrameRate(60, 1), 120).Contains("--frames=121"),
              "export: the encoder stops at the engine's real frame count");
        check(ExportPlan.Remaining(0.25, TimeSpan.FromMinutes(3)) == "About 9 min left" && ExportPlan.Remaining(0.0, TimeSpan.FromSeconds(1)) == "Estimating time…",
              $"export: time left ({ExportPlan.Remaining(0.25, TimeSpan.FromMinutes(3))})");

        // ---- queue in the view model
        var dir = Path.Combine(Path.GetTempPath(), "nm-exvm-" + Guid.NewGuid().ToString("N")[..6]);
        var store = new SettingsStore(dir);
        var fake = new FakeBackend();
        var host = new FakeHost();
        var exporter = new FakeExporter { Gate = new TaskCompletionSource() };
        var vm = new MainViewModel(host, store, rt, () => fake, null, exporter);
        vm.SetGpu(new GpuInfo(new[] { "NVIDIA GeForce GTX 1650" }));
        vm.SelectExportTargetCommand.Execute(vm.ExportTargets.First(t => t.Value == "120"));
        vm.QueueExports(new[] { Path.Combine(v, "one.mkv"), Path.Combine(v, "two.mkv"), "https://youtube.com/watch?v=x" });
        await WaitFor(() => exporter.Runs.Count == 1);
        check(vm.Exports.Count == 2 && vm.Exports[0].IsRunning && vm.Exports[1].Status == ExportStatus.Waiting,
              "export: files run one at a time; links are left out");
        check(exporter.Runs.Count == 1 && exporter.Runs[0].Options.Target == "120" && exporter.Runs[0].Options.HardwareEncoder,
              "export: chosen frame rate and the NVIDIA encoder on a GTX card");
        check(Math.Abs(vm.Exports[0].Progress - 0.5) < 1e-9 && vm.Exports[0].PercentText.StartsWith("50"), "export: progress shown");
        vm.CancelExportCommand.Execute(vm.Exports[1]);
        check(vm.Exports[1].Status == ExportStatus.Cancelled, "export: a waiting export can be cancelled");
        exporter.Outcome = _ => null;
        exporter.Gate.SetResult();
        await WaitFor(() => vm.Exports[0].IsDone);
        check(vm.Exports[0].IsDone && vm.Exports[0].Detail.StartsWith("Saved as") && exporter.Runs.Count == 1,
              "export: finished, and the cancelled one never ran");
        exporter.Gate = null;
        exporter.Outcome = _ => "Out of video memory";
        vm.QueueExports(new[] { Path.Combine(v, "three.mkv") });
        await WaitFor(() => vm.Exports[^1].IsFailed);
        check(vm.Exports[^1].IsFailed && vm.Exports[^1].Detail == "Out of video memory", "export: a failure says why");
        vm.ClearFinishedExportsCommand.Execute(null);
        check(!vm.HasExports, "export: clear finished");
        Directory.Delete(dir, true);
    }
}
