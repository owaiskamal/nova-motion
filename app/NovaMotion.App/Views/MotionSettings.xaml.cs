// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Windows.Controls;

namespace NovaMotion.App.Views;

public partial class MotionSettings : UserControl
{
    public MotionSettings() => InitializeComponent();

    private void DemoSide_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is NovaMotion.Core.ViewModels.MainViewModel vm) vm.DemoSideBySide = true;
    }

    private void DemoSplit_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is NovaMotion.Core.ViewModels.MainViewModel vm) vm.DemoSideBySide = false;
    }
}
