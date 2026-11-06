// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.IO.Compression;
using System.Text;

namespace NovaMotion.Core;

/// <summary>
/// "Save diagnostics" in Settings: every log and setting that helps find a problem, in one zip to send
/// (what collect-logs.cmd gathered, without a console window). Nothing personal beyond file names of
/// recent exports and photos inside the logs.
/// </summary>
public static class Diagnostics
{
    private static readonly string[] AppFiles = { "mpv.log", "engine-debug.log", "engine.json", "engine-state.json", "settings.json" };

    private static readonly string[] RuntimeFiles =
    {
        "tuning.json", "tune.log", "benchmark.log", "setup.log", "setup-tensorrt.log", "setup-studio.log", "setup-enhance.log",
        "setup-generate.log", "animate.log", "versions.json", "prepare-models.log", "enhance-prepared.txt", "photo.log",
        "photo-compare.log", "generate-runner.log", Path.Combine("trt-engines", "background-build.log"), Path.Combine("generate", "ready.txt"),
    };

    /// <summary>Writes the zip and returns the files it holds.</summary>
    public static IReadOnlyList<string> Save(string zipPath, string appDir, RuntimeLayout? rt, string systemText)
    {
        var added = new List<string>();
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        void Add(string path, string name)
        {
            if (!File.Exists(path)) return;
            try
            {
                // Logs may be open for writing (mpv.log while a video plays): read them shared
                using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                src.CopyTo(dst);
                added.Add(name);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void Text(string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
            w.Write(text);
            added.Add(name);
        }

        foreach (var f in AppFiles) Add(Path.Combine(appDir, f), "app/" + f);
        var exports = Path.Combine(appDir, "exports");
        if (Directory.Exists(exports))
            foreach (var f in new DirectoryInfo(exports).GetFiles("export-*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(3))
                Add(f.FullName, "exports/" + f.Name);
        if (rt is not null)
        {
            foreach (var f in RuntimeFiles) Add(Path.Combine(rt.Root, f), "runtime/" + f.Replace('\\', '/'));
            var logs = Path.Combine(rt.Root, "logs");
            if (Directory.Exists(logs))
                foreach (var f in Directory.GetFiles(logs)) Add(f, "runtime/logs/" + Path.GetFileName(f));
            Text("models.txt", Listing(Path.Combine(rt.MpvDir, "vs-plugins", "models")));
            Text("trt-engines.txt", Listing(rt.TrtEngineDir));
        }
        foreach (var f in new DirectoryInfo(Path.GetTempPath()).GetFiles("trtexec_*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(3))
            Add(f.FullName, "trtexec/" + f.Name);
        Text("system.txt", systemText);
        return added;
    }

    /// <summary>Every model and engine file with its size and date: what's installed and prepared.</summary>
    private static string Listing(string dir)
    {
        if (!Directory.Exists(dir)) return "(none)\n";
        var sb = new StringBuilder();
        foreach (var f in new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"{Path.GetRelativePath(dir, f.FullName),-80} {f.Length,14:N0}  {f.LastWriteTime:yyyy-MM-dd HH:mm}");
        return sb.ToString();
    }
}
