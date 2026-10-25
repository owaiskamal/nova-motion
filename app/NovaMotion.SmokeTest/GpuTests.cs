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
        // TensorRT model and AI size pickers
        var root = Path.Combine(Path.GetTempPath(), "nm-trt-" + Guid.NewGuid().ToString("N")[..6]);
        var rtDir = Path.Combine(root, "runtime");
        var onnx = Path.Combine(rtDir, "mpv", "vs-plugins", "models", "rife");
        Directory.CreateDirectory(onnx);
        foreach (var f in new[] { "rife_v4.6.onnx", "rife_v4.22_lite.onnx", "rife_v4.26.onnx", "rife_v4.25_lite.onnx" }) File.WriteAllText(Path.Combine(onnx, f), "");
        File.WriteAllText(Path.Combine(rtDir, "mpv", "vs-plugins", "vstrt.dll"), "");
        File.WriteAllText(Path.Combine(rtDir, "novamotion.vpy"), "");
        var rt = RuntimeLayout.Find(root)!;
        check(rt.TrtModels().SequenceEqual(new[] { "rife-v4.26", "rife-v4.6", "rife-v4.25_lite", "rife-v4.22_lite" }),
              $"trt models: installed ones listed, full before lite, newest first ({string.Join(", ", rt.TrtModels())})");
        var store3 = new SettingsStore(Path.Combine(root, "cfg"));
        var vm3 = new MainViewModel(new FakeHost(), store3, rt, () => new FakeBackend(), () => DateTime.UtcNow);
        vm3.SetGpu(G("NVIDIA GeForce GTX 1650"));
        check(vm3.IsTensorRt && vm3.HasModels && vm3.Models[0].Value == "auto" && vm3.Models[0].IsSelected && vm3.Models.Count == 5,
              $"pickers: TensorRT lists Auto plus the 4 installed models ({vm3.IsTensorRt} {vm3.Models.Count})");
        vm3.SelectModelCommand.Execute(vm3.Models.Single(m => m.Value == "rife-v4.22_lite"));
        vm3.SelectAiSizeCommand.Execute(vm3.AiSizes.Single(a => a.Value == "0.5"));
        var eng = JsonNode.Parse(File.ReadAllText(store3.EngineConfigPath))!;
        check(eng["trt_model"]!.GetValue<string>() == "rife-v4.22_lite" && eng["ai_scale"]!.GetValue<string>() == "0.5"
              && vm3.AiSizes.Single(a => a.IsSelected).Value == "0.5" && vm3.Models.Single(m => m.IsSelected).Value == "rife-v4.22_lite",
              "pickers: chosen model and AI size reach the engine");
        await Task.CompletedTask;
    }
}
