// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Services;
using MicaWPF.Controls;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using WindowsMediaController;
using GlobalSystemMediaTransportControlsSessionMediaProperties = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties;

namespace FluentFlyoutWPF.Windows;

public partial class QueueWindow : MicaWindow
{
    private readonly MainWindow _mainWindow = (MainWindow)Application.Current.MainWindow;
    private string _contextSource = string.Empty;
    private System.Windows.Threading.DispatcherTimer? _autoCloseTimer;

    public QueueWindow(string currentTitle, string currentArtist)
    {
        DataContext = SettingsManager.Current;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowHelper.SetNoActivate(this);
        InitializeComponent();
        WindowHelper.SetTopmost(this);
        CustomWindowChrome.CaptionHeight = 0;

        if (SettingsManager.Current.NextUpAcrylicWindowEnabled)
        {
            WindowBlurHelper.EnableBlur(this);
        }
        else
        {
            WindowBlurHelper.DisableBlur(this);
        }

        UpdateAuthStatus();

        // 0ms Instant display if cached queue exists
        if (DeezerService.CachedQueue.Count > 0)
        {
            QueueListView.ItemsSource = DeezerService.CachedQueue;
            LoadingBar.Visibility = Visibility.Collapsed;
        }

        LoadQueue(currentTitle, currentArtist);

        // Event-driven track change listener (0 periodic polling overhead)
        _mainWindow.mediaManager.OnAnyMediaPropertyChanged += MediaManager_OnAnyMediaPropertyChanged;

        MouseEnter += (s, e) => _autoCloseTimer?.Stop();
        MouseLeave += (s, e) =>
        {
            if (SettingsManager.Current.QueueAutoCloseOnLeave)
            {
                _autoCloseTimer?.Stop();
                _autoCloseTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(SettingsManager.Current.QueueAutoCloseDelay)
                };
                _autoCloseTimer.Tick += (st, et) =>
                {
                    _autoCloseTimer.Stop();
                    CloseWithAnimation();
                };
                _autoCloseTimer.Start();
            }
        };

        Closed += (s, e) =>
        {
            _autoCloseTimer?.Stop();
            _mainWindow.mediaManager.OnAnyMediaPropertyChanged -= MediaManager_OnAnyMediaPropertyChanged;
        };
    }

    private void MediaManager_OnAnyMediaPropertyChanged(MediaManager.MediaSession mediaSession, GlobalSystemMediaTransportControlsSessionMediaProperties mediaProperties)
    {
        Dispatcher.Invoke(() =>
        {
            string appId = mediaSession?.ControlSession?.SourceAppUserModelId ?? "";
            if (appId.Contains("deezer", StringComparison.OrdinalIgnoreCase))
            {
                LoadQueue(mediaProperties?.Title ?? "", mediaProperties?.Artist ?? "");
            }
        });
    }

    private void UpdateAuthStatus()
    {
        if (DeezerAuthService.IsAuthenticated)
        {
            AuthStatusText.Text = "Connecté à Deezer";
            AuthButton.Content = "Déconnexion";
        }
        else
        {
            AuthStatusText.Text = "Mode Déconnecté";
            AuthButton.Content = "Se connecter";
        }
    }

    private async void LoadQueue(string currentTitle, string currentArtist)
    {
        bool isInitialLoad = (QueueListView.ItemsSource == null);
        if (isInitialLoad)
        {
            LoadingBar.Visibility = Visibility.Visible;
            EmptyMessage.Visibility = Visibility.Collapsed;
        }

        // Pre-warm CDP debug port in background
        _ = DeezerCdpService.EnsureDeezerRunningWithDebugPortAsync();

        var newTracks = await DeezerService.GetQueueAsync(currentTitle, currentArtist);

        LoadingBar.Visibility = Visibility.Collapsed;

        if (newTracks.Count == 0)
        {
            EmptyMessage.Visibility = Visibility.Visible;
            QueueListView.ItemsSource = null;
            return;
        }

        EmptyMessage.Visibility = Visibility.Collapsed;

        // Check if existing list has same tracks structure (same IDs/titles and count)
        if (QueueListView.ItemsSource is List<DeezerTrack> existingTracks &&
            existingTracks.Count == newTracks.Count &&
            SameTracks(existingTracks, newTracks))
        {
            // Same queue structure! Only update IsCurrent in place with 0 flicker!
            for (int i = 0; i < existingTracks.Count; i++)
            {
                existingTracks[i].IsCurrent = newTracks[i].IsCurrent;
                existingTracks[i].TargetIndex = newTracks[i].TargetIndex;
            }
        }
        else
        {
            // Queue structure changed or initial load: set new ItemsSource
            QueueListView.ItemsSource = newTracks;
            int currentIdx = newTracks.FindIndex(t => t.IsCurrent);
            if (currentIdx > 0)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                {
                    try
                    {
                        var container = QueueListView.ItemContainerGenerator.ContainerFromIndex(currentIdx) as FrameworkElement;
                        container?.BringIntoView();
                    }
                    catch { }
                });
            }
        }

        _contextSource = DeezerService.LastQueueSource;
        bool isCdp = await DeezerCdpService.IsCdpAvailableAsync();
        string modeStr = isCdp ? "Mode Direct CDP ⚡" : "Mode Windows GSMTC";
        ContextLabel.Text = string.IsNullOrEmpty(_contextSource)
            ? $"[{modeStr}]"
            : $"Source : {_contextSource}  •  [{modeStr}]";
    }

    private static bool SameTracks(List<DeezerTrack> a, List<DeezerTrack> b)
    {
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id && a[i].Title != b[i].Title)
                return false;
        }
        return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private static void SendMediaNextKey()
    {
        keybd_event(VK_MEDIA_NEXT_TRACK, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MEDIA_NEXT_TRACK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>
    /// Plays the chosen track by absolute TargetIndex (CDP) or relative skip count with instant 0ms UI response.
    /// </summary>
    private async void PlayTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        try
        {
            // Case A: Direct absolute index playback via CDP (instant 0ms visual feedback!)
            if (fe.DataContext is DeezerTrack track && track.TargetIndex >= 0)
            {
                // 1. Instant 0ms visual feedback: update IsCurrent in memory
                if (QueueListView.ItemsSource is List<DeezerTrack> currentList)
                {
                    foreach (var t in currentList)
                    {
                        t.IsCurrent = (t.TargetIndex == track.TargetIndex);
                    }
                }

                // 2. Trigger CDP playback in Deezer (instant)
                bool cdpSuccess = await DeezerCdpService.PlayTrackAtIndexAsync(track.TargetIndex);
                if (cdpSuccess)
                {
                    await Task.Delay(50); // Minimal 50ms buffer
                    DeezerService.ClearCache();
                    if (SettingsManager.Current.QueueCloseOnTrackClick)
                    {
                        CloseWithAnimation();
                    }
                    else
                    {
                        LoadQueue("", ""); // Quiet background update (0 flicker!)
                    }
                    return;
                }
            }

            // Case B: Fallback to relative skip count if TargetIndex wasn't set or CDP failed
            if (int.TryParse(fe.Tag?.ToString(), out int skipCount) && skipCount > 0)
            {
                var activeSession = _mainWindow.GetActiveMediaSession();
                bool cdpSuccess = await DeezerCdpService.SkipTracksAsync(skipCount);
                if (!cdpSuccess)
                {
                    for (int i = 0; i < skipCount; i++)
                    {
                        bool success = false;
                        if (activeSession != null)
                        {
                            success = await activeSession.ControlSession.TrySkipNextAsync();
                        }

                        if (!success)
                        {
                            SendMediaNextKey();
                        }

                        if (i < skipCount - 1)
                            await Task.Delay(50);
                    }
                }
                await Task.Delay(50);
                DeezerService.ClearCache();
                LoadQueue("", "");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Play track error: {ex.Message}");
        }
    }

    private async void AuthButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeezerAuthService.IsAuthenticated)
        {
            DeezerAuthService.Logout();
            UpdateAuthStatus();
        }
        else
        {
            bool success = await DeezerAuthService.AuthenticateAsync();
            UpdateAuthStatus();
            if (success)
            {
                var activeSession = _mainWindow.GetActiveMediaSession();
                var songInfo = activeSession != null ? MainWindow.TryGetMediaProperties(activeSession.ControlSession) : null;
                LoadQueue(songInfo?.Title ?? "", songInfo?.Artist ?? "");
            }
        }
    }

    private bool _isClosing = false;

    public void ShowWithAnimation()
    {
        var workArea = SystemParameters.WorkArea;
        double margin = 8;
        double queueHeight = Height > 0 ? Height : 380;

        double targetLeft = margin;
        double targetTop = workArea.Bottom - queueHeight - margin;

        Left = targetLeft;
        Top = targetTop + 15;
        Opacity = 0;

        Show();
        Activate();

        var topAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = targetTop + 15,
            To = targetTop,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };

        var opacityAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };

        BeginAnimation(TopProperty, topAnim);
        BeginAnimation(OpacityProperty, opacityAnim);
    }

    public void CloseWithAnimation()
    {
        if (_isClosing) return;
        _isClosing = true;

        double currentTop = Top;
        var topAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = currentTop,
            To = currentTop + 15,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
        };

        var opacityAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
        };

        opacityAnim.Completed += (s, e) => Close();

        BeginAnimation(TopProperty, topAnim);
        BeginAnimation(OpacityProperty, opacityAnim);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseWithAnimation();
    }
}
