// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Collections.ObjectModel;

namespace NovaMotion.Core.ViewModels;

/// <summary>The parts of NOVA Studio, shown as tabs in the title bar.</summary>
public enum StudioModule { Motion, Enhance, Generate }

/// <summary>NOVA Studio's modules: which one is showing, and the Generate page's hardware check.</summary>
public sealed partial class MainViewModel
{
    public const string ProductName = "NOVA Studio";

    private double? _graphicsGb, _systemGb;

    private void InitStudio()
    {
        SelectModuleCommand = new Command(p =>
        {
            if (p is StudioModule m) SelectModule(m);
            else if (Enum.TryParse<StudioModule>(p as string, ignoreCase: true, out var parsed)) SelectModule(parsed);
        });
        _systemGb = SystemMemory.SystemGb();
        RebuildGenerate();
    }

    public Command SelectModuleCommand { get; private set; } = null!;

    /// <summary>The module on screen. Settings opens over any of them and closes back to it.</summary>
    public StudioModule ActiveModule =>
        IsGenerateOpen ? StudioModule.Generate : IsExportOpen ? StudioModule.Enhance : StudioModule.Motion;

    public bool IsMotionModule => ActiveModule == StudioModule.Motion;
    public bool IsEnhanceModule => ActiveModule == StudioModule.Enhance;
    public bool IsGenerateModule => ActiveModule == StudioModule.Generate;

    private bool _isGenerateOpen;
    public bool IsGenerateOpen
    {
        get => _isGenerateOpen;
        set { if (Set(ref _isGenerateOpen, value)) RaiseModule(); }
    }

    public void SelectModule(StudioModule module)
    {
        IsSettingsOpen = false;
        IsExportOpen = module == StudioModule.Enhance;
        IsGenerateOpen = module == StudioModule.Generate;
        RaiseModule();
    }

    private void RaiseModule() =>
        Raise(nameof(ActiveModule), nameof(IsMotionModule), nameof(IsEnhanceModule), nameof(IsGenerateModule));

    // ------------------------------------------------------------------ Generate

    public ObservableCollection<ModelVerdict> GenerateModels { get; } = new();
    public string GenerateSummary { get; private set; } = "Checking this PC…";

    public string GraphicsMemoryText => _graphicsGb is { } g ? $"{g:0.#} GB" : "Unknown";
    public string SystemMemoryText => _systemGb is { } s ? $"{s:0} GB" : "Unknown";

    /// <summary>Called once the graphics card's memory is known (after <see cref="SetGpu"/>).</summary>
    public void SetMemory(double? graphicsGb, double? systemGb = null)
    {
        _graphicsGb = graphicsGb;
        if (systemGb is not null) _systemGb = systemGb;
        RebuildGenerate();
    }

    private void RebuildGenerate()
    {
        var verdicts = GenerateCatalog.AssessAll(_gpu, _graphicsGb);
        GenerateModels.Clear();
        foreach (var v in verdicts) GenerateModels.Add(v);
        GenerateSummary = _gpu is null ? "Checking this PC…" : GenerateCatalog.Summary(verdicts, _systemGb);
        Raise(nameof(GenerateSummary), nameof(GraphicsMemoryText), nameof(SystemMemoryText));
    }
}
