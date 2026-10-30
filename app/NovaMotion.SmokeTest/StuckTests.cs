// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
// A player stuck inside the engine: the app must always get the video going again.
using System.Text.Json.Nodes;
using NovaMotion.Core;
using NovaMotion.Core.ViewModels;

internal static class StuckTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nm-stuck-" + Guid.NewGuid().ToString("N")[..6]);
        var store = new SettingsStore(dir);
        var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        var fake = new FakeBackend();
        var vm = new MainViewModel(new FakeHost(), store, null, () => fake, () => now);
        vm.SetGpu(new GpuInfo(new[] { "NVIDIA GeForce RTX 4070" }));

        async Task Play(string file, double at)
        {
            vm.TargetText = file;
            vm.PlayCommand.Execute(null);
            await Task.Delay(20);
            fake.Prop("idle-active", false);
            fake.Event("file-loaded");
            fake.Prop("duration", 600);
            fake.Prop("time-pos", at);
            fake.Prop("container-fps", 24);
            fake.Prop("display-fps", 60);
            fake.Prop("video-params/w", 1920);
            fake.Prop("video-params/h", 1080);
        }
        void Run(double seconds) { for (var t = 0.0; t < seconds; t += 0.5) { now = now.AddSeconds(0.5); vm.Tick(); } }

        await Play("https://example.com/a.mp4", 70.5);
        fake.Prop("vf", JsonNode.Parse("[{\"label\":\"novamotion\",\"enabled\":true}]"));
        fake.Prop("estimated-vf-fps", 60);

        // Quiet but answering (no position events for a while): not stuck
        Run(12);
        await Task.Delay(20);
        check(!vm.IsStuck && vm.SmoothingEnabled, "stuck: a player that answers isn't called stuck");

        // Frozen inside the engine (e.g. a TensorRT build that never ends): no answers
        fake.Frozen = true;
        Run(10);
        await Task.Delay(50);
        check(vm.IsStuck, "stuck: a player that stops answering is shown as stuck, with Play without smoothing");

        // The way out by hand: restart at the same moment, without smoothing
        var starts = fake.Starts;
        vm.PlayWithoutSmoothingCommand.Execute(null);
        await Task.Delay(50);
        check(fake.Starts == starts + 1 && !vm.SmoothingEnabled && fake.LastSet("start")?[2] as string == "70.5"
              && fake.Last("loadfile")?[1] as string == "https://example.com/a.mp4" && !vm.IsStuck,
              $"stuck: Play without smoothing restarts the player at the same moment ({fake.LastSet("start")?[2]})");

        // Ctrl+S on a stuck player takes the same way out (the player can't take commands)
        fake.Prop("idle-active", false);
        fake.Event("file-loaded");
        vm.SmoothingEnabled = true;
        fake.Prop("time-pos", 80.0);
        Run(2);
        fake.Frozen = true;
        Run(10);
        await Task.Delay(50);
        starts = fake.Starts;
        vm.SmoothingEnabled = false;
        await Task.Delay(50);
        check(fake.Starts == starts + 1 && !vm.SmoothingEnabled, "stuck: smoothing off on a stuck player restarts it without smoothing");

        // Stuck with no TensorRT build under way: recovers on its own
        await Play("https://example.com/b.mp4", 10);
        vm.SmoothingEnabled = true;
        fake.Prop("time-pos", 12.0);
        Run(2);
        fake.Frozen = true;
        starts = fake.Starts;
        for (var i = 0; i < 80 && fake.Starts == starts; i++) { now = now.AddSeconds(0.5); vm.Tick(); await Task.Delay(2); }
        check(fake.Starts == starts + 1 && !vm.SmoothingEnabled && vm.ToastText.Contains("got stuck"),
              "stuck: no build going on, so it restarts without smoothing by itself");
    }
}
