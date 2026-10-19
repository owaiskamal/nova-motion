// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
// Graphics card classes and the defaults each one gets.
using System.Text.Json.Nodes;
using NovaMotion.Core;
using NovaMotion.Core.ViewModels;

internal static class GpuTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        GpuInfo G(params string[] n) => new(n);
        check(G("NVIDIA GeForce GTX 1650", "Intel(R) UHD Graphics 630").Tier == GpuTier.Entry, "gpu: GTX 1650 laptop is entry class");
        check(G("NVIDIA GeForce GTX 1650").SupportsTensorRt && G("NVIDIA GeForce RTX 3060 Laptop GPU").SupportsTensorRt
              && !G("NVIDIA GeForce GTX 1060 6GB").SupportsTensorRt, "gpu: TensorRT on GTX 16 and RTX, not on GTX 10");
        check(G("NVIDIA GeForce RTX 4070").Tier == GpuTier.Performance && G("NVIDIA GeForce RTX 3050 Laptop GPU").Tier == GpuTier.Entry
              && G("AMD Radeon RX 6500 XT").Tier == GpuTier.Entry && G("AMD Radeon RX 7900 XTX").Tier == GpuTier.Performance,
              "gpu: RTX 4070 and RX 7900 are performance class, RTX 3050 and RX 6500 entry");
        check(G("Intel(R) Iris(R) Xe Graphics").Tier == GpuTier.Integrated && G().Tier == GpuTier.Unknown, "gpu: integrated and unknown");

        var entry = AppSettings.CreateDefaults(G("NVIDIA GeForce GTX 1650", "Intel(R) UHD Graphics 630"));
        var bal = entry.Active.Engine;
        check(entry.Active.Name == "Balanced" && bal.Engine == "rife_trt" && bal.TargetText == "60"
              && bal.RifeModel.Contains("lite") && bal.GpuClass == "entry",
              "defaults: GTX 1650 gets TensorRT, 60 fps, the light model, and the entry class for the engine");
        var gtx10 = AppSettings.CreateDefaults(G("NVIDIA GeForce GTX 1060 6GB"));
        check(gtx10.Active.Engine.Engine == "rife" && gtx10.Active.Engine.TargetText == "60", "defaults: GTX 10 gets Vulkan RIFE at 60 fps");
        var rtx = AppSettings.CreateDefaults(G("NVIDIA GeForce RTX 4070"));
        check(rtx.Active.Engine.TargetText == "display" && rtx.Active.Engine.GpuClass == "", "defaults: RTX 4070 keeps the display rate");

        // Settings from before entry defaults existed, on a GTX card: profiles are redone once
        var dir = Path.Combine(Path.GetTempPath(), "nm-gpu-" + Guid.NewGuid().ToString("N")[..6]);
        var store = new SettingsStore(dir);
        var old = AppSettings.CreateDefaults(G("NVIDIA GeForce RTX 4070"));
        old.FirstRunDone = true; old.DefaultsVersion = 0; old.Volume = 42; old.ResumePlayback = false;
        store.Save(old);
        var vm = new MainViewModel(new FakeHost(), store, null, () => new FakeBackend(), () => DateTime.UtcNow);
        vm.SetGpu(G("NVIDIA GeForce GTX 1650"));
        var after = store.Load();
        check(after.Active.Engine.TargetText == "60" && after.Active.Engine.GpuClass == "entry" && after.DefaultsVersion == AppSettings.CurrentDefaults
              && after.Volume == 42 && !after.ResumePlayback,
              "migration: a GTX card's old profiles are redone, other settings kept");
        check(JsonNode.Parse(File.ReadAllText(store.EngineConfigPath))!["gpu_class"]!.GetValue<string>() == "entry", "migration: engine.json carries gpu_class");
        check(vm.ShowGpuAdvice && vm.GpuAdvice.Contains("entry-level") && !vm.ShowTunePrompt,
              "home: entry card advice shown; no tune card without TensorRT");
        after.Active.Engine.TargetText = "120"; store.Save(after);
        var vm2 = new MainViewModel(new FakeHost(), store, null, () => new FakeBackend(), () => DateTime.UtcNow);
        vm2.SetGpu(G("NVIDIA GeForce GTX 1650"));
        check(store.Load().Active.Engine.TargetText == "120", "migration: runs once; the user's own choices stay after it");
        await Task.CompletedTask;
    }
}
