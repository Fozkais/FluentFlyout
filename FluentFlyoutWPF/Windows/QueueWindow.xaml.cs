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
    private List<DeezerTrack> _fullQueue = new();
    private Point _dragStartPoint;

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
        _fullQueue = newTracks ?? new List<DeezerTrack>();

        LoadingBar.Visibility = Visibility.Collapsed;

        if (_fullQueue.Count == 0)
        {
            EmptyMessage.Visibility = Visibility.Visible;
            QueueListView.ItemsSource = null;
            return;
        }

        EmptyMessage.Visibility = Visibility.Collapsed;

        // Apply any active search filter
        ApplyFilter();

        // Auto-scroll to current track
        int currentIdx = _fullQueue.FindIndex(t => t.IsCurrent);
        if (currentIdx >= 0)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                try
                {
                    var activeItem = _fullQueue[currentIdx];
                    QueueListView.ScrollIntoView(activeItem);
                }
                catch { }
            });
        }

        _contextSource = DeezerService.LastQueueSource;
        bool isCdp = await DeezerCdpService.IsCdpAvailableAsync();
        string modeStr = isCdp ? "Mode Direct CDP ⚡" : "Mode Windows GSMTC";
        ContextLabel.Text = string.IsNullOrEmpty(_contextSource)
            ? $"[{modeStr}]"
            : $"Source : {_contextSource}  •  [{modeStr}]";
    }

    private void SearchToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SearchTextBox.Visibility == Visibility.Visible)
        {
            SearchTextBox.Visibility = Visibility.Collapsed;
            SearchTextBox.Text = string.Empty;
        }
        else
        {
            SearchTextBox.Visibility = Visibility.Visible;
            SearchTextBox.Focus();
        }
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = SearchTextBox.Text?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(query))
        {
            QueueListView.ItemsSource = _fullQueue;
        }
        else
        {
            QueueListView.ItemsSource = _fullQueue
                .Where(t => t.Title.ToLowerInvariant().Contains(query) || t.Artist.ToLowerInvariant().Contains(query))
                .ToList();
        }
    }

    private void PlayCover_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DeezerTrack track)
        {
            PlayTrack(track);
        }
    }

    private async void RemoveTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DeezerTrack track)
        {
            bool success = await DeezerCdpService.RemoveTrackAsync(track.TargetIndex);
            if (success)
            {
                DeezerService.ClearCache();
                LoadQueue("", "");
            }
        }
    }

    private async void PlayTrack(DeezerTrack track)
    {
        if (track == null || track.TargetIndex < 0) return;

        if (QueueListView.ItemsSource is List<DeezerTrack> currentList)
        {
            foreach (var t in currentList)
            {
                t.IsCurrent = (t.TargetIndex == track.TargetIndex);
            }
        }

        bool cdpSuccess = await DeezerCdpService.PlayTrackAtIndexAsync(track.TargetIndex);
        if (cdpSuccess)
        {
            await Task.Delay(50);
            DeezerService.ClearCache();
            if (SettingsManager.Current.QueueCloseOnTrackClick)
            {
                CloseWithAnimation();
            }
            else
            {
                LoadQueue("", "");
            }
        }
    }

    private void PlayTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DeezerTrack track)
        {
            PlayTrack(track);
        }
    }

    // Drag & Drop Reordering
    private void QueueListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void QueueListView_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        Vector diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var listView = sender as ListView;
            var listViewItem = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);
            if (listViewItem == null) return;

            var draggedTrack = (DeezerTrack)listView.ItemContainerGenerator.ItemFromContainer(listViewItem);
            if (draggedTrack == null) return;

            DragDrop.DoDragDrop(listViewItem, draggedTrack, DragDropEffects.Move);
        }
    }

    private async void QueueListView_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(DeezerTrack))) return;

        var droppedTrack = (DeezerTrack)e.Data.GetData(typeof(DeezerTrack));
        var targetItem = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);
        if (targetItem == null) return;

        var targetTrack = (DeezerTrack)QueueListView.ItemContainerGenerator.ItemFromContainer(targetItem);
        if (targetTrack == null || droppedTrack == targetTrack) return;

        int fromIndex = droppedTrack.TargetIndex;
        int toIndex = targetTrack.TargetIndex;

        if (fromIndex >= 0 && toIndex >= 0)
        {
            bool success = await DeezerCdpService.MoveTrackAsync(fromIndex, toIndex);
            if (success)
            {
                DeezerService.ClearCache();
                LoadQueue("", "");
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        do
        {
            if (current is T ancestor) return ancestor;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        } while (current != null);
        return null;
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
