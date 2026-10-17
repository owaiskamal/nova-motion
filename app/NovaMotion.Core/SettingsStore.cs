// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Text.Json;

namespace NovaMotion.Core;

/// <summary>Persists app settings and writes the active profile as engine.json for the player.</summary>
public sealed class SettingsStore
{
    public string Directory { get; }
    public string SettingsPath => Path.Combine(Directory, "settings.json");
    public string EngineConfigPath => Path.Combine(Directory, "engine.json");

    public SettingsStore(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaMotion");
        System.IO.Directory.CreateDirectory(Directory);
    }

    public AppSettings Load(GpuInfo? gpu = null)
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json.Options);
                if (s is { Profiles.Count: > 0 }) return s;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Corrupt file: keep a copy for inspection and start fresh
            TryCopy(SettingsPath, SettingsPath + ".bad");
        }
        return AppSettings.CreateDefaults(gpu);
    }

    public void Save(AppSettings settings)
    {
        WriteAtomic(SettingsPath, JsonSerializer.Serialize(settings, Json.Options));
        WriteEngineConfig(settings.Active.Engine);
    }

    public void WriteEngineConfig(EngineSettings engine) =>
        WriteAtomic(EngineConfigPath, JsonSerializer.Serialize(engine, Json.Options));

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryCopy(string from, string to)
    {
        try { File.Copy(from, to, overwrite: true); } catch (IOException) { }
    }
}
