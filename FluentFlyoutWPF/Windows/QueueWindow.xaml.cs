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

        // Smooth Auto-scroll to current track with 0 delay
        int currentIdx = _fullQueue.FindIndex(t => t.IsCurrent);
        if (currentIdx >= 0)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                try
                {
                    var scrollViewer = FindVisualChild<ScrollViewer>(QueueListView);
                    if (scrollViewer != null)
                    {
                        double itemHeight = 44.0;
                        double targetOffset = (currentIdx * itemHeight) - (scrollViewer.ViewportHeight / 2) + (itemHeight / 2);
                        if (targetOffset < 0) targetOffset = 0;
                        if (targetOffset > scrollViewer.ScrollableHeight) targetOffset = scrollViewer.ScrollableHeight;
                        
                        SmoothScrollToOffset(scrollViewer, targetOffset);
                    }
                    else
                    {
                        var activeItem = _fullQueue[currentIdx];
                        QueueListView.ScrollIntoView(activeItem);
                    }
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

    private static void SmoothScrollToOffset(ScrollViewer scrollViewer, double targetOffset)
    {
        if (scrollViewer == null) return;
        double startOffset = scrollViewer.VerticalOffset;
        if (Math.Abs(startOffset - targetOffset) < 2)
        {
            scrollViewer.ScrollToVerticalOffset(targetOffset);
            return;
        }

        int steps = 15;
        int currentStep = 0;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(12)
        };

        timer.Tick += (s, e) =>
        {
            currentStep++;
            double progress = (double)currentStep / steps;
            double ease = 1.0 - Math.Pow(1.0 - progress, 3); // Cubic ease out
            double currentOffset = startOffset + (targetOffset - startOffset) * ease;
            scrollViewer.ScrollToVerticalOffset(currentOffset);

            if (currentStep >= steps)
            {
                timer.Stop();
                scrollViewer.ScrollToVerticalOffset(targetOffset);
            }
        };
        timer.Start();
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
            QueueListView.ItemsSource = null;
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
            // 1. Instant 0ms visual removal from list
            int idx = _fullQueue.IndexOf(track);
            int targetIndexToRemove = track.TargetIndex;

            if (idx >= 0)
            {
                _fullQueue.RemoveAt(idx);
                // Re-index remaining target indices
                for (int i = 0; i < _fullQueue.Count; i++)
                {
                    _fullQueue[i].TargetIndex = i;
                }
                ApplyFilter();
                DeezerService.UpdateCache(_fullQueue);
            }

            // 2. Perform CDP removal asynchronously in background
            await DeezerCdpService.RemoveTrackAsync(targetIndexToRemove);
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
            DeezerService.UpdateCache(_fullQueue);
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

    // Drag & Drop Reordering with Visual Ghost Card
    private DeezerTrack? _draggedTrack;

    private void QueueItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DeezerTrack track)
        {
            _dragStartPoint = e.GetPosition(null);
            _draggedTrack = track;
        }
    }

    private void QueueItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedTrack == null) return;

        Vector diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            // Populate floating drag ghost card
            if (!string.IsNullOrEmpty(_draggedTrack.CoverUrl))
            {
                try { DragGhostCover.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(_draggedTrack.CoverUrl)); } catch { }
            }
            DragGhostTitle.Text = _draggedTrack.Title;
            DragGhostArtist.Text = _draggedTrack.Artist;

            Point mousePos = e.GetPosition(this);
            Point screenPos = PointToScreen(mousePos);
            DragGhostPopup.HorizontalOffset = screenPos.X + 12;
            DragGhostPopup.VerticalOffset = screenPos.Y + 12;
            DragGhostPopup.IsOpen = true;

            var dataObj = new DataObject("DeezerTrack", _draggedTrack);
            DragDrop.DoDragDrop((DependencyObject)sender, dataObj, DragDropEffects.Move);

            // Hide ghost after drag ends
            DragGhostPopup.IsOpen = false;
            _draggedTrack = null;
        }
    }

    private void QueueListView_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("DeezerTrack"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            // Move visual floating ghost card with mouse
            Point mousePos = e.GetPosition(this);
            Point screenPos = PointToScreen(mousePos);
            DragGhostPopup.HorizontalOffset = screenPos.X + 12;
            DragGhostPopup.VerticalOffset = screenPos.Y + 12;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private async void QueueListView_Drop(object sender, DragEventArgs e)
    {
        DragGhostPopup.IsOpen = false;
        if (!e.Data.GetDataPresent("DeezerTrack")) return;

        var droppedTrack = e.Data.GetData("DeezerTrack") as DeezerTrack;
        if (droppedTrack == null) return;

        var targetItem = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);
        if (targetItem == null || targetItem.DataContext is not DeezerTrack targetTrack) return;

        if (droppedTrack == targetTrack) return;

        int fromIndex = _fullQueue.IndexOf(droppedTrack);
        int toIndex = _fullQueue.IndexOf(targetTrack);

        if (fromIndex >= 0 && toIndex >= 0)
        {
            // 1. Instant 0ms visual reorder in WPF UI
            _fullQueue.RemoveAt(fromIndex);
            _fullQueue.Insert(toIndex, droppedTrack);

            for (int i = 0; i < _fullQueue.Count; i++)
            {
                _fullQueue[i].TargetIndex = i;
            }

            ApplyFilter();
            DeezerService.UpdateCache(_fullQueue);

            // 2. Perform CDP reorder asynchronously in background
            await DeezerCdpService.MoveTrackAsync(fromIndex, toIndex);
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

    private static T? FindVisualChild<T>(DependencyObject obj) where T : DependencyObject
    {
        if (obj == null) return null;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(obj, i);
            if (child is T t)
                return t;
            var childOfChild = FindVisualChild<T>(child);
            if (childOfChild != null)
                return childOfChild;
        }
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
