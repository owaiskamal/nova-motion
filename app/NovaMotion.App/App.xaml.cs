// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using NovaMotion.Core;

namespace NovaMotion.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private MainWindow? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => WriteCrash(ex.ExceptionObject as Exception);
        base.OnStartup(e);
        var arg = e.Args.FirstOrDefault();

        _instance = new SingleInstance(UiTest.Requested ? "NovaMotion.UiTest" : "NovaMotion.App");
        if (!_instance.IsFirst)
        {
            // Already running: hand over the file/link and quit
            _instance.Forward(arg ?? "--show");
            Shutdown();
            return;
        }

        try
        {
            if (Environment.ProcessPath is { } exe && !UiTest.Requested) NovaMotionLink.RegisterProtocol(exe);
        }
        catch (Exception) { /* registry locked down: links just won't work */ }

        _main = new MainWindow();
        _instance.ArgumentReceived += a => Dispatcher.Invoke(() => _main.HandleArgument(a));
        if (_main.ViewModel.Settings.StartMinimizedToTray && arg is null && !UiTest.Requested)
        {
            // Create the window (so the player has a surface) without showing it
            new System.Windows.Interop.WindowInteropHelper(_main).EnsureHandle();
        }
        else _main.Show();
        if (arg is not null) _main.HandleArgument(arg);
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);
        if (UiTest.Requested) { UiTest.Note("UNHANDLED " + e.Exception); }
        // Keep the app alive for errors in event handlers; the details are in crash.log
        e.Handled = !UiTest.Requested || e.Exception is not System.Windows.Markup.XamlParseException;
        if (!e.Handled) Shutdown(1);
    }

    private static void WriteCrash(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaMotion");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:u}\n{ex}\n\n");
            if (UiTest.Requested) UiTest.Note("CRASH " + ex);
        }
        catch (IOException) { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
