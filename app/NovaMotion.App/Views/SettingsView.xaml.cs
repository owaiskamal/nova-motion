// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Windows.Controls;

namespace NovaMotion.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>playback, smoothing, online, shortcuts, about.</summary>
    public void ShowSection(string section)
    {
        var target = section switch
        {
            "smoothing" => NavSmoothing,
            "online" => NavOnline,
            "shortcuts" => NavShortcuts,
            "about" => NavAbout,
            _ => NavPlayback,
        };
        target.IsChecked = true;
    }
}
