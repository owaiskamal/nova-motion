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
                               string? compare = null, (double X, double Y)? spot = null)
    {
        Options = options; Output = output; Spot = spot;
        stage.Report(Photo.StageText("enhance"));
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
        check(vm.PhotoReady && !vm.CanEnhancePhoto && vm.PhotoScales.Single(s => s.IsSelected).Value == "2", "photo: 2x by default; needs a picture");
        vm.SetPhoto(Path.Combine(dir, "notes.txt"));
        check(!vm.HasPhoto, "photo: a non-picture is refused");
        var picture = Path.Combine(dir, "Portrait.jpg");
        File.WriteAllText(picture, "jpg");
        host.Image = picture;
        vm.ChoosePhotoCommand.Execute(null);
        check(vm.HasPhoto && vm.CanEnhancePhoto && vm.PhotoShown == picture && vm.PhotoShownLabel == "Original", "photo: chosen and shown");

        vm.SelectPhotoModelCommand.Execute(vm.PhotoModels.Single(m => m.Value == "max"));
        check(!vm.PhotoCleanupEnabled && vm.PhotoModelNote.Contains("setup-enhance"), "photo: Max detail says when its models are missing");
        vm.SelectPhotoScaleCommand.Execute(vm.PhotoScales.Single(m => m.Value == "4"));
        vm.SelectPhotoPresetCommand.Execute(vm.PhotoPresets.Single(m => m.Value == "anime"));
        vm.PhotoFixCompression = true;

        vm.EnhancePhotoCommand.Execute(null);
        await Task.Delay(50);
        check(vm.IsEnhancingPhoto && vm.PhotoStatus == "Enhancing…" && !vm.CanEnhancePhoto, $"photo: running ({vm.PhotoStatus})");
        check(runner.Options == new PhotoOptions("anime", 4, "light", true, true), $"photo: the chosen options are used ({runner.Options})");
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
