// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using NovaMotion.Core.ViewModels;

namespace NovaMotion.App;

/// <summary>
/// Scripted walk through the UI that saves screenshots, for CI on a real Windows desktop.
/// Set NOVAMOTION_UITEST to an output folder to run it; the app quits when done.
/// </summary>
internal static class UiTest
{
    public static readonly string? OutDir = Environment.GetEnvironmentVariable("NOVAMOTION_UITEST");
    public static bool Requested => !string.IsNullOrWhiteSpace(OutDir);
    private static readonly StringBuilder Log = new();

    public static void Note(string line)
    {
        lock (Log) Log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        if (Requested) try { File.WriteAllText(Path.Combine(OutDir!, "uitest.log"), Log.ToString()); } catch (IOException) { }
    }

    public static async Task RunAsync(MainWindow w, MainViewModel vm)
    {
        Directory.CreateDirectory(OutDir!);
        try
        {
            Note($"screen {SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight}, window {w.ActualWidth}x{w.ActualHeight}");
            await Task.Delay(3000);
            Shot(w, "01-home");
            w.OpenSettings("smoothing");
            await Task.Delay(900);
            Shot(w, "02-settings-smoothing");
            w.OpenSettings("playback");
            await Task.Delay(500);
            Shot(w, "03-settings-playback");
            vm.CloseSettingsCommand.Execute(null);

            var video = Environment.GetEnvironmentVariable("NOVAMOTION_UITEST_VIDEO") is { Length: > 0 } v
                ? v : "av://lavfi:testsrc2=size=1920x1080:rate=25";
            await vm.OpenAsync(new[] { video });
            for (var i = 0; i < 24 && vm.EngineState is not (EngineState.Active or EngineState.Failed); i++) await Task.Delay(500);
            await Task.Delay(2500);
            State(vm);
            Shot(w, "04-player");
            vm.ToggleDrawerCommand.Execute("motion");
            await Task.Delay(1000);
            Shot(w, "05-motion-panel");
            vm.ToggleDrawerCommand.Execute("tracks");
            await Task.Delay(700);
            Shot(w, "06-tracks-panel");
            vm.CloseDrawerCommand.Execute(null);
            vm.IsFullscreen = true;
            await Task.Delay(1500);
            Shot(w, "07-fullscreen");
            vm.IsFullscreen = false;
            await Task.Delay(800);
            await vm.StopAsync();
            await Task.Delay(1500);
            Shot(w, "08-home-after-playing");
            State(vm);
        }
        catch (Exception ex) { Note("ERROR " + ex); }
        Note("done");
        w.Quit();
    }

    private static void State(MainViewModel vm) =>
        Note($"state: media={vm.HasMedia} engine={vm.EngineState} badge='{vm.BadgeText}' src={vm.SourceFpsText} out={vm.OutputFpsText} " +
             $"display={vm.DisplayText} video={vm.VideoSizeText} decoder={vm.DecoderText} title='{vm.Title}' toast='{vm.ToastText}'");

    private static void Shot(Window w, string name)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(hwnd, out r);
            var width = Math.Max(1, r.Right - r.Left); var height = Math.Max(1, r.Bottom - r.Top);
            using var bmp = new System.Drawing.Bitmap(width, height);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(width, height));
            bmp.Save(Path.Combine(OutDir!, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            Note($"shot {name} {width}x{height}");
        }
        catch (Exception ex) { Note($"shot {name} failed: {ex.Message}"); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
