// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
using System.Windows.Controls;
using System.Windows.Input;
using NovaMotion.Core.ViewModels;

namespace NovaMotion.App.Views;

public partial class ExportView : UserControl
{
    public ExportView() => InitializeComponent();

    /// <summary>Enhance a photo: a click on the whole picture compares that part at 100%.</summary>
    private async void PhotoOverview_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not Image img || img.ActualWidth <= 0 || img.ActualHeight <= 0) return;
        var p = e.GetPosition(img);
        await vm.SetPhotoSpotAsync(p.X / img.ActualWidth, p.Y / img.ActualHeight);
    }
}
