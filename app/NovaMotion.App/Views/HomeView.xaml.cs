// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NovaMotion.Core.ViewModels;

namespace NovaMotion.App.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private void LinkBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainViewModel vm) return;
        if (vm.PlayCommand.CanExecute(null)) vm.PlayCommand.Execute(null);
        e.Handled = true;
    }

    private void SmoothingSettings_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.OpenSettings("smoothing");

    public void FocusLink() => LinkBox.Focus();
}
