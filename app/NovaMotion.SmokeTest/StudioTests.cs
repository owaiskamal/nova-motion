// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
// NOVA Studio's modules and the Generate page's hardware check.
using NovaMotion.Core;
using NovaMotion.Core.ViewModels;

internal static class StudioTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        GpuInfo G(params string[] n) => new(n);
        ModelFit Fit(string id, GpuInfo gpu, double? gb) =>
            GenerateCatalog.Assess(GenerateCatalog.Models.First(m => m.Id == id), gpu, gb).Fit;

        // --- what each card can run ---------------------------------------
        var rtx4070 = G("NVIDIA GeForce RTX 4070");
        check(Fit("sdxl", rtx4070, 12) == ModelFit.Good && Fit("flux-schnell", rtx4070, 12) == ModelFit.Slow
              && Fit("wan22-5b", rtx4070, 12) == ModelFit.Slow && Fit("wan22-14b", rtx4070, 12) == ModelFit.Slow,
              "generate: RTX 4070 (12 GB) runs SDXL fully, Flux and Wan 2.2 with compressed weights");
        check(Fit("wan22-5b", rtx4070, 11.99) == ModelFit.Slow, "generate: 11.99 GB as reported still counts as a 12 GB card");
        var rtx4090 = G("NVIDIA GeForce RTX 4090");
        check(GenerateCatalog.AssessAll(rtx4090, 24).Count(v => v.Fit == ModelFit.Good) == 3 && Fit("wan22-14b", rtx4090, 24) == ModelFit.Slow,
              "generate: RTX 4090 runs all but the 14B video model at full size");
        var gtx1650 = G("NVIDIA GeForce GTX 1650", "Intel(R) UHD Graphics");
        check(GenerateCatalog.AssessAll(gtx1650, 4).All(v => v.Fit == ModelFit.TooHeavy), "generate: GTX 1650 (4 GB) is too small for every model");
        check(GenerateCatalog.AssessAll(G("Intel(R) Iris(R) Xe Graphics"), 2).All(v => v.Fit == ModelFit.Unsupported)
              && GenerateCatalog.AssessAll(G("AMD Radeon RX 7900 XTX"), 24).All(v => v.Fit == ModelFit.Unsupported),
              "generate: Intel and AMD graphics are not supported yet");
        var intel = GenerateCatalog.AssessAll(G("Intel(R) Iris(R) Xe Graphics"), 2);
        check(GenerateCatalog.Summary(intel, 16).StartsWith("Generate needs an NVIDIA") && intel.All(v => v.Text == v.Model.Note),
              "generate: the reason shows once in the summary; rows keep their descriptions");
        check(GenerateCatalog.AssessAll(rtx4070, null).All(v => v.Fit == ModelFit.Unsupported), "generate: unknown graphics memory is never guessed");

        // --- the summary line ----------------------------------------------
        check(GenerateCatalog.Summary(GenerateCatalog.AssessAll(rtx4070, 12), 32) == "This PC can make both images and video.",
              "summary: RTX 4070 with 32 GB makes both");
        check(GenerateCatalog.Summary(GenerateCatalog.AssessAll(rtx4070, 12), 16).Contains("32 GB of system memory is recommended"),
              "summary: 16 GB of system memory gets the video memory advice");
        check(GenerateCatalog.Summary(GenerateCatalog.AssessAll(G("NVIDIA GeForce RTX 3060 Ti"), 8), 32).StartsWith("This PC can make images. Video needs"),
              "summary: an 8 GB card makes images only");
        check(GenerateCatalog.Summary(GenerateCatalog.AssessAll(gtx1650, 4), 16).Contains("Motion and Enhance work fully"),
              "summary: a 4 GB card is pointed back at Motion and Enhance");
        check(GenerateCatalog.Models.All(m => m.License is "Apache 2.0" or "CreativeML Open RAIL++-M"),
              "catalog: only models whose licence allows commercial use");

        // --- reading the card's memory ---------------------------------------
        check(SystemMemory.ParseLargestGb("1073741824\r\n12878610432\r\n\r\n") == 12, "memory: largest adapter, bytes to GB");
        check(SystemMemory.ParseLargestGb("") is null && SystemMemory.ParseLargestGb("n/a\n0") is null, "memory: nothing readable gives no size");

        // --- switching modules -------------------------------------------------
        var dir = Path.Combine(Path.GetTempPath(), "nm-studio-" + Guid.NewGuid().ToString("N")[..6]);
        var vm = new MainViewModel(new FakeHost(), new SettingsStore(dir), null, () => new FakeBackend());
        check(vm.ActiveModule == StudioModule.Motion && vm.GenerateSummary == "Checking this PC…", "studio: starts on Motion, Generate still checking");
        vm.SelectModuleCommand.Execute("generate");
        check(vm.IsGenerateModule && vm.IsGenerateOpen && !vm.IsExportOpen, "studio: Generate tab");
        vm.SelectModuleCommand.Execute(StudioModule.Enhance);
        check(vm.IsEnhanceModule && vm.IsExportOpen && !vm.IsGenerateOpen, "studio: Enhance tab shows the export page");
        vm.OpenSettingsCommand.Execute(null);
        check(vm.IsSettingsOpen && vm.IsEnhanceModule, "studio: settings open over the current module");
        vm.CloseSettingsCommand.Execute(null);
        check(!vm.IsSettingsOpen && vm.IsEnhanceModule, "studio: closing settings returns to it");
        vm.SelectModuleCommand.Execute(StudioModule.Generate);
        vm.OpenSettingsCommand.Execute(null);
        vm.SelectModuleCommand.Execute("motion");
        check(vm.IsMotionModule && !vm.IsSettingsOpen && !vm.IsExportOpen && !vm.IsGenerateOpen, "studio: a tab closes settings too");
        vm.CloseExportCommand.Execute(null);
        check(vm.IsMotionModule, "studio: leaving Enhance goes to Motion");

        vm.SetGpu(rtx4070);
        vm.SetMemory(12, 32);
        check(vm.GenerateModels.Count == GenerateCatalog.Models.Count && vm.GraphicsMemoryText == "12 GB" && vm.SystemMemoryText == "32 GB"
              && vm.GenerateSummary == "This PC can make both images and video.", "studio: Generate page fills in once the card is known");

        vm.SelectModuleCommand.Execute(StudioModule.Generate);
        await vm.OpenAsync(new[] { "/tmp/clip.mkv" });
        check(vm.IsMotionModule && !vm.IsGenerateOpen, "studio: playing a video switches to Motion");
        await vm.DisposeAsync();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
