// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
// The AI Models page: what's installed, installing and preparing in the app with progress, the order
// parts install in, cancelling and failures.
using NovaMotion.Core;
using NovaMotion.Core.ViewModels;

internal sealed class FakePartRunner : IPartRunner
{
    public readonly List<string> Ran = new();
    public Func<AiPart, Task>? OnInstall;
    public TaskCompletionSource Gate = new();
    public bool Fail;

    public async Task InstallAsync(AiPart part, IProgress<PartProgress> progress, CancellationToken ct)
    {
        Ran.Add(part.Id);
        var reader = new PartProgressReader();
        foreach (var line in new[] { "@@step 0.0000 0.8000 Downloading model.onnx", "@@progress 0.5000 model.onnx: 100 MB of 200 MB, 9.0 MB/s" })
            if (reader.Read(line) is { } p) progress.Report(p);
        await Gate.Task.WaitAsync(ct);
        if (Fail) { reader.Read("@@error The download stopped: no internet connection"); throw new InvalidOperationException(reader.Error); }
        if (OnInstall is not null) await OnInstall(part);
    }

    public Task PrepareAsync(IProgress<PartProgress> progress, CancellationToken ct)
    {
        Ran.Add("prepare");
        progress.Report(new PartProgress(0.5, "Preparing realesr-general-mid-x4v3 for this graphics card (1 of 2)"));
        return Task.CompletedTask;
    }
}

internal static class ModelsTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        // ---- the progress lines the setup scripts print
        var r = new PartProgressReader();
        check(r.Read("==> Enhance models") is null && r.Read("@@step 0.2000 0.6000 Downloading a.onnx") is { Overall: 0.2 } s && s.Text == "Downloading a.onnx",
              "progress: a step starts where the script says, other lines are ignored");
        check(r.Read("@@progress 0.5000 a.onnx: 50 MB of 100 MB") is { } h && Math.Abs(h.Overall!.Value - 0.4) < 1e-9 && h.Text.StartsWith("a.onnx: 50"),
              "progress: half of a step from 0.2 to 0.6 is 0.4 of the whole");
        check(r.Read("@@progress -1 Unpacking") is { Overall: 0.2 } && new PartProgressReader().Read("@@step -1 -1 Working") is { Overall: null },
              "progress: a step with no measure holds the bar and says what it's doing");
        r.Read("@@error The graphics card isn't an NVIDIA one");
        check(r.Error == "The graphics card isn't an NVIDIA one", "progress: the script's own reason when it stops");

        // ---- a PC with the player engine only
        var dir = Path.Combine(Path.GetTempPath(), "nm-models-" + Guid.NewGuid().ToString("N")[..6]);
        var root = Path.Combine(dir, "runtime");
        Directory.CreateDirectory(Path.Combine(root, "mpv"));
        File.WriteAllText(Path.Combine(root, "novamotion.vpy"), "");
        File.WriteAllText(Path.Combine(root, "mpv", "vapoursynth.dll"), "");
        var rt = RuntimeLayout.Find(dir)!;
        var host = new FakeHost();
        var runner = new FakePartRunner();
        var vm = new MainViewModel(host, new SettingsStore(Path.Combine(dir, "settings")), rt, () => new FakeBackend(), () => DateTime.UtcNow)
        {
            PartRunner = runner,
        };
        vm.SetGpu(new GpuInfo(new[] { "NVIDIA GeForce GTX 1650" }));
        vm.SetMemory(4);
        AiPartItem Item(string id) => vm.AiPartItems.Single(i => i.Id == id);
        check(vm.AiPartItems.Count == AiParts.All.Count && Item(AiParts.Player).IsReady && Item(AiParts.Enhance).State == PartState.NotInstalled
              && Item(AiParts.Generative).State == PartState.NotInstalled && Item(AiParts.Enhance).ButtonText == "Download",
              "models page: lists every part, what's ready and what to download");
        check(vm.HasPartsNeedingAction && vm.PartsSummary.Contains("to install"), $"models page: says what's left ({vm.PartsSummary})");

        // ---- not for this PC
        vm.SetGpu(new GpuInfo(new[] { "Intel(R) Iris(R) Xe Graphics" }));
        check(Item(AiParts.TensorRt).IsBlocked && Item(AiParts.TensorRt).Detail.Contains("NVIDIA") && !Item(AiParts.TensorRt).HasButton
              && Item(AiParts.Generative).IsBlocked, "models page: TensorRT and Generative say why they're not for Intel graphics, with no button");
        vm.SetGpu(new GpuInfo(new[] { "NVIDIA GeForce GTX 1650" }));
        vm.SetMemory(2);
        check(Item(AiParts.Generative).IsBlocked && Item(AiParts.Generative).Detail.Contains("2 GB"), "models page: Generative on a 2 GB card says why not");
        vm.SetMemory(4);

        // ---- Generative needs the Enhance models first: both queue, one at a time, with progress
        vm.OpenModelsCommand.Execute(AiParts.Generative);
        await Task.Delay(30);
        check(vm.IsModelsOpen && runner.Ran.SequenceEqual(new[] { AiParts.Enhance }) && Item(AiParts.Enhance).State == PartState.Installing
              && Item(AiParts.Generative).State == PartState.Waiting,
              $"install: Generative asks for the Enhance models first, one download at a time ({string.Join(",", runner.Ran)})");
        var e = Item(AiParts.Enhance);
        check(e.HasProgress && Math.Abs(e.ProgressValue - 0.4) < 1e-9 && e.PercentText == "40%" && e.Detail.Contains("100 MB of 200 MB"),
              $"install: a real progress bar with what it's downloading ({e.PercentText}: {e.Detail})");
        // The Enhance models arrive
        runner.OnInstall = part =>
        {
            if (part.Id == AiParts.Enhance) EnhanceFiles(rt);
            if (part.Id == AiParts.Generative) GenerateFiles(rt);
            return Task.CompletedTask;
        };
        var gate = runner.Gate;
        runner.Gate = new TaskCompletionSource();
        gate.SetResult();
        await Task.Delay(50);
        check(Item(AiParts.Enhance).IsReady && Item(AiParts.Generative).State == PartState.Installing && runner.Ran.Last() == AiParts.Generative,
              $"install: when the Enhance models are ready, Generative starts ({Item(AiParts.Enhance).State}, {Item(AiParts.Generative).State})");
        check(vm.EnhanceReady && vm.PhotoReady, "install: Enhance unlocks without a restart");
        // Cancel, then a failure with the script's reason, then success
        vm.CancelPartCommand.Execute(Item(AiParts.Generative));
        await Task.Delay(30);
        check(Item(AiParts.Generative).State == PartState.NotInstalled && Item(AiParts.Generative).Detail.Contains("picks up where it left off"),
              "install: cancelling stops it; downloading again resumes");
        runner.Fail = true;
        runner.Gate = new TaskCompletionSource();
        vm.PartActionCommand.Execute(Item(AiParts.Generative));
        runner.Gate.SetResult();
        await Task.Delay(30);
        check(Item(AiParts.Generative).IsFailed && Item(AiParts.Generative).Error.Contains("no internet") && Item(AiParts.Generative).ButtonText == "Try again",
              $"install: a failure says why in plain words and offers to try again ({Item(AiParts.Generative).Error})");
        runner.Fail = false;
        runner.Gate = new TaskCompletionSource();
        runner.Gate.SetResult();
        vm.PartActionCommand.Execute(Item(AiParts.Generative));
        await Task.Delay(30);
        check(Item(AiParts.Generative).IsReady && Photo.HasGenerative(rt), "install: and after trying again it's ready");

        // ---- with TensorRT, the Enhance models are prepared for the card: needs preparing, then prepared
        var plugins = Path.Combine(rt.MpvDir, "vs-plugins");
        File.WriteAllText(Path.Combine(plugins, "vstrt.dll"), "");
        vm.RefreshParts();
        check(Item(AiParts.Enhance).State == PartState.NeedsPreparing && Item(AiParts.Enhance).ButtonText == "Prepare" && Item(AiParts.TensorRt).IsReady,
              "prepare: with TensorRT, the models need preparing for this graphics card");
        runner.Ran.Clear();
        File.WriteAllLines(Path.Combine(rt.Root, "enhance-prepared.txt"), AiParts.EnhanceModelFiles(rt).Select(Path.GetFileName)!);
        vm.PartActionCommand.Execute(Item(AiParts.Enhance));
        await Task.Delay(30);
        check(runner.Ran.SequenceEqual(new[] { "prepare" }) && Item(AiParts.Enhance).IsReady, "prepare: runs, and afterwards the models are ready");
        check(vm.PartsNeedingAction == 1 && Item(AiParts.Animate).State == PartState.NotInstalled, "models page: only Animate is left");
    }

    private static void EnhanceFiles(RuntimeLayout rt)
    {
        var plugins = Path.Combine(rt.MpvDir, "vs-plugins");
        foreach (var m in EnhanceCatalog.RequiredModels.Append("enhance\\RealESRGAN_x4plus.onnx").Append("restore\\nafnet_reds64.onnx"))
        {
            var p = Path.Combine(plugins, "models", m.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "");
        }
        File.WriteAllText(Path.Combine(rt.MpvDir, "vsmlrt.py"), "");
        File.WriteAllText(Path.Combine(plugins, "vsncnn.dll"), "");
        File.WriteAllText(Path.Combine(rt.Root, "enhance.py"), "");
        File.WriteAllText(rt.PhotoScript, "");
        Directory.CreateDirectory(rt.StudioPackages);
        File.WriteAllText(Path.Combine(rt.StudioPackages, "pillow-13.0.0.installed"), "");
    }

    private static void GenerateFiles(RuntimeLayout rt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(rt.GeneratePython)!);
        File.WriteAllText(rt.GeneratePython, "");
        File.WriteAllText(rt.GenerateScript, "");
        File.WriteAllText(rt.GenerateReady, "runner x\nmodel seedvr2_ema_3b-Q4_K_M.gguf\nvram_mb 4096\n");
    }
}
