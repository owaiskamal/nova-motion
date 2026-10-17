// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System;
using System.Linq;
using System.Windows;
using NovaMotion.Core;

namespace NovaMotion.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private MainWindow? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var arg = e.Args.FirstOrDefault();

        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
            // Already running: hand over the file/link and quit
            _instance.Forward(arg ?? "--show");
            Shutdown();
            return;
        }

        try
        {
            if (Environment.ProcessPath is { } exe) NovaMotionLink.RegisterProtocol(exe);
        }
        catch (Exception) { /* registry locked down: links just won't work */ }

        _main = new MainWindow();
        _instance.ArgumentReceived += a => Dispatcher.Invoke(() => _main.HandleArgument(a));
        _main.Show();
        if (arg is not null) _main.HandleArgument(arg);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
