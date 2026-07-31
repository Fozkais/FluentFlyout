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
        InitPreferredServiceComboBox();
        UpdateCdpStatusAsync();
        UpdateSpotifyStatusAsync();
    }

    private void InitPreferredServiceComboBox()
    {
        if (PreferredServiceComboBox == null) return;
        string preferred = SettingsManager.Current.PreferredMusicService;
        foreach (ComboBoxItem item in PreferredServiceComboBox.Items)
        {
            if (item.Tag?.ToString() == preferred)
            {
                PreferredServiceComboBox.SelectedItem = item;
                break;
            }
        }
        if (PreferredServiceComboBox.SelectedItem == null && PreferredServiceComboBox.Items.Count > 0)
        {
            PreferredServiceComboBox.SelectedIndex = 0;
        }
    }

    private void PreferredServiceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreferredServiceComboBox?.SelectedItem is ComboBoxItem item && item.Tag != null)
        {
            SettingsManager.Current.PreferredMusicService = item.Tag.ToString() ?? "Auto";
            SettingsManager.SaveSettings();
        }
    }

    private async void UpdateSpotifyStatusAsync()
    {
        if (SpotifyStatusText == null || SpotifyAuthButton == null) return;

        bool isAuth = SpotifyAuthService.IsAuthenticated;
        if (isAuth)
        {
            SpotifyStatusText.Text = "Statut : Connecté à Spotify 🟢";
            SpotifyStatusText.Foreground = (System.Windows.Media.Brush)Application.Current.TryFindResource("MicaWPF.Brushes.SystemAccentColorPrimary") ?? System.Windows.Media.Brushes.Green;
            SpotifyAuthButton.Content = "Se déconnecter de Spotify";
        }
        else
        {
            SpotifyStatusText.Text = "Statut : Non connecté";
            SpotifyStatusText.Foreground = System.Windows.Media.Brushes.Gray;
            SpotifyAuthButton.Content = "Se connecter à Spotify";
        }
    }

    private async void SpotifyAuthButton_Click(object sender, RoutedEventArgs e)
    {
        if (SpotifyAuthButton == null) return;

        if (SpotifyAuthService.IsAuthenticated)
        {
            SpotifyAuthService.Logout();
            UpdateSpotifyStatusAsync();
        }
        else
        {
            SpotifyAuthButton.IsEnabled = false;
            SpotifyStatusText.Text = "Connexion en cours dans votre navigateur...";
            bool success = await SpotifyAuthService.AuthenticateAsync();
            UpdateSpotifyStatusAsync();
            SpotifyAuthButton.IsEnabled = true;
        }
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
