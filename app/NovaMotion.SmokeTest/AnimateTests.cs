// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
// Generate's Animate a picture: setup detection, options, the run and its result.
using NovaMotion.Core;
using NovaMotion.Core.ViewModels;

internal sealed class FakeAnimator : IAnimateRunner
{
    public AnimateOptions? Options;
    public string? Output;
    public bool Fail;
    public TaskCompletionSource Gate = new();

    public async Task RunAsync(string image, string output, AnimateOptions options, IProgress<(string Stage, double Progress)> progress, CancellationToken ct)
    {
        Options = options; Output = output;
        progress.Report(("Working out depth…", 0.05));
        await Gate.Task.WaitAsync(ct);
        if (Fail) throw new InvalidOperationException("The depth model didn't load");
        await File.WriteAllTextAsync(output, "video", ct);
    }
}

internal static class AnimateTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(Animate.Parse("progress 0.250")?.Progress == 0.25 && Animate.Parse("stage depth")?.Text == "depth"
              && Animate.Parse("error no model")?.Text == "no model" && Animate.Parse("took 2 s") is null, "animate: script output parsed");

        var dir = Path.Combine(Path.GetTempPath(), "nm-animate-" + Guid.NewGuid().ToString("N")[..6]);
        var rt = Path.Combine(dir, "runtime");
        Directory.CreateDirectory(rt);
        File.WriteAllText(Path.Combine(rt, "novamotion.vpy"), "");
        var runtime = RuntimeLayout.Find(dir)!;
        check(!Animate.IsReady(runtime), "animate: not ready before setup-studio.cmd");
        Directory.CreateDirectory(runtime.StudioPackages);
        File.WriteAllText(runtime.ParallaxScript, "");
        File.WriteAllText(runtime.DepthModel, "");
        File.WriteAllText(Path.Combine(runtime.StudioPackages, "onnxruntime-directml-1.24.4.installed"), "");
        check(Animate.IsReady(runtime), "animate: ready once the model and packages are there");

        var args = Animate.Arguments(runtime, "C:/p/a.png", "C:/v/a.mp4", new AnimateOptions(CameraMove.Sway, 8, 1.6, 60));
        string After(string flag) => args[args.ToList().IndexOf(flag) + 1];
        check(After("--move") == "sway" && After("--seconds") == "8" && After("--strength") == "1.6" && After("--fps") == "60"
              && After("--model") == runtime.DepthModel, "animate: options passed to the script");

        var host = new FakeHost();
        var animator = new FakeAnimator();
        var vm = new MainViewModel(host, new SettingsStore(Path.Combine(dir, "settings")), runtime, () => new FakeBackend(), () => DateTime.UtcNow)
        {
            Animator = animator,
            AnimateFolder = Path.Combine(dir, "out"),
        };
        check(vm.AnimateReady && !vm.CanAnimate && !vm.StartAnimateCommand.CanExecute(null), "animate: needs a picture first");
        vm.SetAnimateImage(Path.Combine(dir, "notes.txt"));
        check(!vm.HasAnimateImage, "animate: a non-picture is refused");
        var picture = Path.Combine(dir, "Lighthouse.png");
        File.WriteAllText(picture, "png");
        host.Image = picture;
        vm.ChooseAnimateImageCommand.Execute(null);
        check(vm.HasAnimateImage && vm.AnimateImageName == "Lighthouse.png" && vm.CanAnimate, "animate: picture chosen");

        vm.SelectAnimateMoveCommand.Execute(vm.AnimateMoves.Single(m => m.Value == "drift"));
        vm.SelectAnimateLengthCommand.Execute(vm.AnimateLengths.Single(m => m.Value == "4"));
        vm.SelectAnimateStrengthCommand.Execute(vm.AnimateStrengths.Single(m => m.Label == "Subtle"));
        vm.Animate60 = true;
        check(vm.AnimateMoves.Single(m => m.IsSelected).Value == "drift" && vm.AnimateMoveNote.Contains("sideways"), "animate: move chosen and described");

        vm.StartAnimateCommand.Execute(null);
        await Task.Delay(50);
        check(vm.IsAnimating && !vm.CanAnimate && vm.AnimateStatus.StartsWith("Working out depth"), $"animate: running ({vm.AnimateStatus})");
        check(animator.Options == new AnimateOptions(CameraMove.Drift, 4, 0.6, 60), "animate: the chosen options are used");
        check(animator.Output == Path.Combine(dir, "out", "Lighthouse - drift.mp4"), $"animate: named after the picture and move ({animator.Output})");
        animator.Gate.SetResult();
        await Task.Delay(50);
        check(!vm.IsAnimating && vm.HasAnimateResult && vm.AnimateResultName == "Lighthouse - drift.mp4", "animate: finished, result shown");
        check(Animate.OutputPath(picture, CameraMove.Drift, Path.Combine(dir, "out")).EndsWith("Lighthouse - drift (2).mp4"), "animate: a second run doesn't overwrite the first");
        vm.ShowAnimateResultCommand.Execute(null);
        check(host.Opened.LastOrDefault() == Path.Combine(dir, "out"), "animate: Show in folder opens Videos\\NOVA Studio");

        animator.Gate = new();
        animator.Fail = true;
        vm.StartAnimateCommand.Execute(null);
        await Task.Delay(30);
        animator.Gate.SetResult();
        await Task.Delay(50);
        check(!vm.IsAnimating && !vm.HasAnimateResult && vm.AnimateStatus == "The depth model didn't load", "animate: a failure says why");

        animator.Gate = new();
        animator.Fail = false;
        vm.StartAnimateCommand.Execute(null);
        await Task.Delay(30);
        vm.CancelAnimateCommand.Execute(null);
        await Task.Delay(50);
        check(!vm.IsAnimating && vm.AnimateStatus == "Cancelled", "animate: cancel stops it");

        var setupVm = new MainViewModel(host, new SettingsStore(Path.Combine(dir, "s2")), null, () => new FakeBackend(), () => DateTime.UtcNow);
        check(setupVm.AnimateNeedsSetup && !setupVm.CanAnimate, "animate: without the runtime it offers setup");
        vm.SetupAnimateCommand.Execute(null);
        check(host.Ran.LastOrDefault()?.StartsWith("setup-studio.cmd") == true, "animate: setup runs setup-studio.cmd");
    }
}
