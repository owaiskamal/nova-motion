// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
// Enhance a photo: setup detection, options, naming, the run, and the before/after flick.
using System.Text.Json;
using NovaMotion.Core;
using NovaMotion.Core.ViewModels;

internal sealed class FakePhotoEnhancer : IPhotoRunner
{
    public PhotoOptions? Options;
    public string? Output;
    public TaskCompletionSource Gate = new();

    public (double X, double Y)? Spot;

    public async Task RunAsync(string image, string output, PhotoOptions options, IProgress<string> stage, CancellationToken ct,
                               string? compare = null, (double X, double Y)? spot = null, IProgress<double>? done = null)
    {
        Options = options; Output = output; Spot = spot;
        stage.Report(Photo.StageText("enhance"));
        done?.Report(0.5);
        await Gate.Task.WaitAsync(ct);
        await File.WriteAllTextAsync(output, "png", ct);
        if (compare is not null) { Directory.CreateDirectory(Path.GetDirectoryName(compare)!); await File.WriteAllTextAsync(compare, "cmp", ct); }
    }

    public async Task CompareAsync(string image, string result, string compare, (double X, double Y) spot, CancellationToken ct)
    {
        Spot = spot;
        Directory.CreateDirectory(Path.GetDirectoryName(compare)!);
        await File.WriteAllTextAsync(compare, "cmp", ct);
    }
}

internal static class PhotoTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nm-photo-" + Guid.NewGuid().ToString("N")[..6]);
        var root = Path.Combine(dir, "runtime");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "novamotion.vpy"), "");
        var rt = RuntimeLayout.Find(dir)!;

        // ---- names and the script's settings
        var o = new PhotoOptions("anime", 4, "strong", MaxDetail: true, FixCompression: true);
        check(Photo.OutputPath(@"C:\p\Beach.JPG", new PhotoOptions(), dir).EndsWith("Beach (NOVA Enhance 2x).jpg")
              && Photo.OutputPath(@"C:\p\Logo.webp", o, dir).EndsWith("Logo (NOVA Enhance 4x max).png")
              && Photo.OutputPath(@"C:\p\a.png", new PhotoOptions(Scale: 1), dir).EndsWith("a (NOVA Enhance).png"),
              "photo: named for what it is; JPEG photos stay JPEG, the rest PNG");
        var json = JsonDocument.Parse(Photo.SettingsJson(o)).RootElement;
        check(json.GetProperty("preset").GetString() == "anime" && json.GetProperty("scale").GetInt32() == 4 && json.GetProperty("model").GetString() == "max"
              && json.GetProperty("fix_compression").GetBoolean() && json.GetProperty("strength").GetString() == "full",
              $"photo: settings for the script ({Photo.SettingsJson(o)})");
        var args = Photo.Arguments(rt, "in.png", "out.png", o, 1, "entry").ToList();
        check(args[1] == rt.PhotoScript && args[args.IndexOf("--gpu") + 1] == "1" && args[args.IndexOf("--gpu-class") + 1] == "entry"
              && args[args.IndexOf("--packages") + 1] == rt.StudioPackages, "photo: script, GPU and packages passed");

        // ---- setup detection
        check(!Photo.IsReady(rt), "photo: not ready before setup-enhance.cmd");
        var plugins = Path.Combine(rt.MpvDir, "vs-plugins");
        foreach (var m in EnhanceCatalog.RequiredModels)
        {
            var p = Path.Combine(plugins, "models", m.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "");
        }
        File.WriteAllText(Path.Combine(rt.MpvDir, "vsmlrt.py"), "");
        File.WriteAllText(Path.Combine(rt.Root, "enhance.py"), "");
        File.WriteAllText(Path.Combine(plugins, "vstrt.dll"), "");
        File.WriteAllText(rt.PhotoScript, "");
        check(!Photo.IsReady(rt), "photo: needs Pillow as well as the models");
        Directory.CreateDirectory(rt.StudioPackages);
        File.WriteAllText(Path.Combine(rt.StudioPackages, "pillow-13.0.0.installed"), "");
        check(Photo.IsReady(rt) && !Photo.HasMaxDetail(rt), "photo: ready; Max detail models not there yet");

        // ---- the card
        var host = new FakeHost();
        var runner = new FakePhotoEnhancer();
        var vm = new MainViewModel(host, new SettingsStore(Path.Combine(dir, "settings")), rt, () => new FakeBackend(), () => DateTime.UtcNow)
        {
            PhotoEnhancer = runner,
            PhotoFolder = Path.Combine(dir, "out"),
        };
        check(vm.PhotoReady && !vm.CanEnhancePhoto && vm.PhotoScales.Single(s => s.IsSelected).Value == "1" && vm.PhotoPresetItems.Single(i => i.IsSelected).Name == "Phone photo", "photo: starts as the Phone photo preset; needs a picture");
        vm.SetPhoto(Path.Combine(dir, "notes.txt"));
        check(!vm.HasPhoto, "photo: a non-picture is refused");
        var picture = Path.Combine(dir, "Portrait.jpg");
        File.WriteAllText(picture, "jpg");
        host.Image = picture;
        vm.ChoosePhotoCommand.Execute(null);
        check(vm.HasPhoto && vm.CanEnhancePhoto && vm.PhotoShown == picture && vm.PhotoShownLabel == "Original", "photo: chosen and shown");

        check(vm.PhotoRestore && vm.PhotoModelNote.Contains("Remove noise and blur needs its model") && vm.PhotoSummary.EndsWith("Noise and blur removed"),
              $"photo: Phone photo removes noise and blur, and says when its model is missing ({vm.PhotoSummary})");
        check(JsonDocument.Parse(Photo.SettingsJson(vm.PhotoOptions)).RootElement.GetProperty("restore").GetDouble() == Photo.RestoreAmount,
              "photo: Remove noise and blur reaches the script");
        vm.PhotoRestore = false;
        check(!vm.PhotoRestore && vm.PhotoOptions.Restore == 0 && vm.PhotoSummary.EndsWith("Same size · Max detail"), $"photo: Remove noise and blur can be turned off ({vm.PhotoSummary})");
        vm.PhotoRestore = true;
        var at1 = Estimate.Photo(3000, 4000, vm.PhotoOptions, "entry");
        var enlarging = Estimate.Photo(3000, 4000, vm.PhotoOptions with { Restore = 0 }, "entry");
        check(at1 < enlarging && at1 > TimeSpan.FromSeconds(5), $"photo: at the same size only NAFNet's work is counted ({at1} against {enlarging})");

        vm.SelectPhotoModelCommand.Execute(vm.PhotoModels.Single(m => m.Value == "max"));
        check(!vm.PhotoCleanupEnabled && vm.PhotoModelNote.Contains("setup-enhance"), "photo: Max detail says when its models are missing");

        // ---- Generative (SeedVR2)
        vm.SelectPhotoModelCommand.Execute(vm.PhotoModels.Single(m => m.Value == "generative"));
        check(vm.PhotoOptions.Generative && !vm.PhotoOptions.MaxDetail && !vm.CanEnhancePhoto && vm.PhotoModelNote.Contains("setup-generate.cmd")
              && !vm.PhotoRestoreEnabled && vm.PhotoSummary.EndsWith("Generative"),
              $"photo: Generative before its setup says what to run, and can't start ({vm.PhotoModelNote})");
        Directory.CreateDirectory(Path.GetDirectoryName(rt.GeneratePython)!);
        File.WriteAllText(rt.GeneratePython, "");
        File.WriteAllText(rt.GenerateScript, "");
        File.WriteAllText(rt.GenerateReady, "runner 4490bd1\nmodel seedvr2_ema_3b_fp8_e4m3fn.safetensors\nvram_mb 4096\n");
        vm.SelectPhotoModelCommand.Execute(vm.PhotoModels.Single(m => m.Value == "generative"));
        check(vm.CanEnhancePhoto && vm.PhotoModelNote.Contains("1.5 minutes a megapixel"), $"photo: Generative on a 4 GB card: offered, with how slow it is there ({vm.PhotoModelNote})");
        File.WriteAllText(rt.GenerateReady, "runner 4490bd1\nmodel seedvr2_ema_3b-Q4_K_M.gguf\nvram_mb 2048\n");
        vm.SelectPhotoModelCommand.Execute(vm.PhotoModels.Single(m => m.Value == "generative"));
        check(!vm.CanEnhancePhoto && vm.PhotoModelNote.Contains("this one has 2 GB"), $"photo: Generative on a 2 GB card: says why not ({vm.PhotoModelNote})");
        File.WriteAllText(rt.GenerateReady, "runner 4490bd1\nmodel seedvr2_ema_3b-Q4_K_M.gguf\nvram_mb 4096\n");
        var q4 = Photo.GenerateArguments(rt, "in.jpg", "out.jpg", vm.PhotoOptions with { Part = PhotoPart.From(0.6, 0.2, 0.1, 0.5) }).ToList();
        check(q4[q4.IndexOf("--dit-model") + 1] == "seedvr2_ema_3b-Q4_K_M.gguf" && q4[q4.IndexOf("--part") + 1] == "0.1,0.2,0.6,0.5",
              "photo: the installed model and the chosen part (corners in any order) reach the script");
        check(PhotoPart.From(0.1, 0.1, 0.11, 0.5) is null, "photo: a part too thin to mean anything is no part");
        var sz12 = vm.PhotoOptions with { Scale = 1 };
        var whole = Estimate.Photo(3000, 4000, sz12, "entry");
        var face = Estimate.Photo(3000, 4000, sz12 with { Part = new PhotoPart(0.4, 0.2, 0.6, 0.35) }, "entry");
        check(whole > TimeSpan.FromMinutes(15) && whole < TimeSpan.FromMinutes(25) && face < TimeSpan.FromMinutes(2),
              $"photo: on a GTX 1650 a 12 MP photo about 20 minutes, a face about a minute ({whole}, {face})");
        vm.PhotoGenerativeAmount = 0.6;
        check(vm.PhotoOptions.Amount == 0.6 && vm.PhotoGenerativeAmountText == "60%"
              && JsonDocument.Parse(Photo.SettingsJson(vm.PhotoOptions)).RootElement.GetProperty("amount").GetDouble() == 0.6,
              "photo: Generative's strength reaches the script");
        vm.PhotoPart = PhotoPart.From(0.2, 0.2, 0.5, 0.5);
        check(vm.HasPhotoPart && vm.ClearPhotoPartCommand.CanExecute(null), "photo: a part can be chosen");
        vm.ClearPhotoPartCommand.Execute(null);
        check(!vm.HasPhotoPart, "photo: and cleared for the whole photo");
        vm.PhotoGenerativeAmount = 0.8;
        File.WriteAllText(rt.GenerateReady, "runner 4490bd1\nmodel seedvr2_ema_3b_fp8_e4m3fn.safetensors\nvram_mb 12282\n");
        vm.SelectPhotoModelCommand.Execute(vm.PhotoModels.Single(m => m.Value == "generative"));
        check(vm.CanEnhancePhoto && Photo.GenerateVramGb(rt) == 12, "photo: Generative ready on a 12 GB card");
        var gargs = Photo.GenerateArguments(rt, "in.jpg", "out.jpg", vm.PhotoOptions, 1, "c.png", (0.5, 0.5)).ToList();
        check(gargs[1] == rt.GenerateScript && gargs[gargs.IndexOf("--runner") + 1] == rt.GenerateRunner && gargs[gargs.IndexOf("--vram") + 1] == "12"
              && gargs[gargs.IndexOf("--gpu") + 1] == "1" && gargs.Contains("--compare"), "photo: Generative's script, runner, card and memory passed");
        check(Photo.OutputPath(@"C:\p\Kid.jpg", vm.PhotoOptions with { Scale = 2 }, dir).EndsWith("Kid (NOVA Enhance 2x generative).jpg"), "photo: a generative result is named so");
        var gen = Estimate.Photo(3000, 4000, vm.PhotoOptions with { Scale = 1 }, "");
        check(gen > TimeSpan.FromSeconds(50) && gen < TimeSpan.FromMinutes(5), $"photo: Generative estimated by the megapixels it makes ({gen})");
        vm.SelectPhotoModelCommand.Execute(vm.PhotoModels.Single(m => m.Value == "max"));
        vm.SelectPhotoScaleCommand.Execute(vm.PhotoScales.Single(m => m.Value == "4"));
        vm.SelectPhotoPresetCommand.Execute(vm.PhotoPresets.Single(m => m.Value == "anime"));
        vm.PhotoFixCompression = true;

        vm.EnhancePhotoCommand.Execute(null);
        await Task.Delay(50);
        check(vm.IsEnhancingPhoto && vm.PhotoStatus == "Enhancing…" && !vm.CanEnhancePhoto, $"photo: running ({vm.PhotoStatus})");
        check(runner.Options == new PhotoOptions("anime", 4, "light", true, true, Restore: Photo.RestoreAmount), $"photo: the chosen options are used ({runner.Options})");
        check(vm.PhotoProgress is > 0.45 and < 0.5 && vm.PhotoProgressText.StartsWith("49%") && vm.ActivityProgress == vm.PhotoProgress,
              $"photo: a real progress bar, with the time left ({vm.PhotoProgress:P0}: {vm.PhotoProgressText})");
        check(Photo.ParseProgress("3/12") == 0.25 && Photo.ParseProgress("x") is null, "photo: progress lines read");
        runner.Gate.SetResult();
        await Task.Delay(50);
        var result = Path.Combine(dir, "out", "Portrait (NOVA Enhance 4x max).jpg");
        check(!vm.IsEnhancingPhoto && vm.HasPhotoResult && vm.PhotoShown == result,
              $"photo: finished, the result shown ({vm.PhotoShown})");
        check(vm.HasPhotoCompare && File.Exists(vm.PhotoCompare) && runner.Spot == (0.5, 0.5),
              "photo: a before/after at 100% of the middle comes with the result");
        var first = vm.PhotoCompare;
        await vm.SetPhotoSpotAsync(0.2, 1.4);
        check(vm.PhotoCompare != first && File.Exists(vm.PhotoCompare) && !File.Exists(first) && runner.Spot == (0.2, 1.0),
              "photo: clicking elsewhere on the picture compares there (a new file, the old one removed)");
        vm.PhotoShowBefore = true;
        check(vm.PhotoShown == picture, "photo: the whole original can still be shown");
        var cargs = Photo.CompareArguments(rt, "a.png", "b.png", "c.png", (0.25, 0.75)).ToList();
        check(cargs.Contains("--compare-only") && cargs[cargs.IndexOf("--spot") + 1] == "0.25,0.75", "photo: compare-only arguments");
        vm.ShowPhotoResultCommand.Execute(null);
        check(host.Opened.LastOrDefault() == Path.Combine(dir, "out"), "photo: Show in folder opens Pictures\\NOVA Studio");
    }
}
