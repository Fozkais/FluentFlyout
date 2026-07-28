// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Services;
using System.Windows;
using System.Windows.Controls;

namespace FluentFlyoutWPF.Pages;

public partial class QueueSettingsPage : Page
{
    public QueueSettingsPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        UpdateCdpStatusAsync();
    }

    private async void UpdateCdpStatusAsync()
    {
        if (CdpStatusText == null) return;

        bool isAvailable = await DeezerCdpService.IsCdpAvailableAsync();
        if (isAvailable)
        {
            CdpStatusText.Text = "Statut : Mode Direct CDP Actif ⚡ (Port 9222)";
            CdpStatusText.Foreground = (System.Windows.Media.Brush)Application.Current.TryFindResource("MicaWPF.Brushes.SystemAccentColorPrimary") ?? System.Windows.Media.Brushes.Green;
        }
        else
        {
            CdpStatusText.Text = "Statut : Deezer inactif ou sans port debug";
            CdpStatusText.Foreground = System.Windows.Media.Brushes.Gray;
        }
    }

    private async void TestCdpButton_Click(object sender, RoutedEventArgs e)
    {
        if (TestCdpButton == null) return;
        TestCdpButton.IsEnabled = false;
        CdpStatusText.Text = "Vérification / Lancement de Deezer...";

        await DeezerCdpService.EnsureDeezerRunningWithDebugPortAsync();
        await Task.Delay(1000);

        UpdateCdpStatusAsync();
        TestCdpButton.IsEnabled = true;
    }
}
