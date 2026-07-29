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
    public static QueueWindow? ActiveInstance { get; private set; }
    private readonly MainWindow _mainWindow = (MainWindow)Application.Current.MainWindow;
    private string _contextSource = string.Empty;
    private System.Windows.Threading.DispatcherTimer? _autoCloseTimer;
    private List<DeezerTrack> _fullQueue = new();

    private System.Windows.Threading.DispatcherTimer? _queueWatcherTimer;
    private string _lastQueueSignature = string.Empty;

    public QueueWindow(string currentTitle, string currentArtist)
    {
        ActiveInstance = this;
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

        // 0ms Instant display if cached queue exists
        if (DeezerService.CachedQueue.Count > 0)
        {
            QueueListView.ItemsSource = DeezerService.CachedQueue;
            LoadingBar.Visibility = Visibility.Collapsed;
        }

        if (!SettingsManager.Current.QueuePlaylistSelectorEnabled)
        {
            PlaylistButton.Visibility = Visibility.Collapsed;
        }

        // Force fresh queue check from CDP on opening flyout so playlist changes reflect instantly
        LoadQueue(currentTitle, currentArtist, forceRefresh: true);

        // Event-driven track change listener (0 periodic polling overhead)
        _mainWindow.mediaManager.OnAnyMediaPropertyChanged += MediaManager_OnAnyMediaPropertyChanged;

        // Background watcher (1.5s) to detect reorder, deletion, shuffle or track changes made directly inside Deezer Desktop
        _queueWatcherTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _queueWatcherTimer.Tick += async (s, e) =>
        {
            var activeSession = _mainWindow.GetActiveMediaSession();
            string appId = activeSession?.ControlSession?.SourceAppUserModelId ?? "";
            if (appId.Contains("deezer", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(_contextSource) && _contextSource.Contains("deezer", StringComparison.OrdinalIgnoreCase)))
            {
                string sig = await DeezerCdpService.GetQueueSignatureAsync();
                if (!string.IsNullOrEmpty(sig) && !string.IsNullOrEmpty(_lastQueueSignature) && sig != _lastQueueSignature)
                {
                    _lastQueueSignature = sig;
                    var songInfo = activeSession != null ? MainWindow.TryGetMediaProperties(activeSession.ControlSession) : null;
                    LoadQueue(songInfo?.Title ?? "", songInfo?.Artist ?? "", forceRefresh: true);
                }
                else if (!string.IsNullOrEmpty(sig))
                {
                    _lastQueueSignature = sig;
                }
            }
        };
        _queueWatcherTimer.Start();

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
            if (ActiveInstance == this) ActiveInstance = null;
            _autoCloseTimer?.Stop();
            _queueWatcherTimer?.Stop();
            _mainWindow.mediaManager.OnAnyMediaPropertyChanged -= MediaManager_OnAnyMediaPropertyChanged;
        };
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var activeSession = _mainWindow.GetActiveMediaSession();
        var songInfo = activeSession != null ? MainWindow.TryGetMediaProperties(activeSession.ControlSession) : null;
        LoadQueue(songInfo?.Title ?? "", songInfo?.Artist ?? "", forceRefresh: true);
    }

    private void MediaManager_OnAnyMediaPropertyChanged(MediaManager.MediaSession mediaSession, GlobalSystemMediaTransportControlsSessionMediaProperties mediaProperties)
    {
        Dispatcher.Invoke(() =>
        {
            string appId = mediaSession?.ControlSession?.SourceAppUserModelId ?? "";
            if (appId.Contains("deezer", StringComparison.OrdinalIgnoreCase))
            {
                // Force fresh queue check from CDP on every track change!
                LoadQueue(mediaProperties?.Title ?? "", mediaProperties?.Artist ?? "", forceRefresh: true);
            }
        });
    }

    public async void LoadQueue(string currentTitle, string currentArtist, bool forceRefresh = false)
    {
        bool isInitialLoad = (QueueListView.ItemsSource == null);
        if (isInitialLoad)
        {
            LoadingBar.Visibility = Visibility.Visible;
            EmptyMessage.Visibility = Visibility.Collapsed;
        }

        // Pre-warm CDP debug port in background
        _ = DeezerCdpService.EnsureDeezerRunningWithDebugPortAsync();

        var newTracks = await DeezerService.GetQueueAsync(currentTitle, currentArtist, forceRefresh);
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

        // Auto-scroll to currently playing track
        int currentIdx = _fullQueue.FindIndex(t => t.IsCurrent);
        if (currentIdx >= 0 && currentIdx < _fullQueue.Count)
        {
            var activeItem = _fullQueue[currentIdx];
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                try
                {
                    QueueListView.ScrollIntoView(activeItem);
                }
                catch { }
            });
        }

        _contextSource = DeezerService.LastQueueSource;
        ContextLabel.Text = string.IsNullOrEmpty(_contextSource) ? "" : $"Source : {_contextSource}";
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

    private async void PlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistSelectorContainer.Visibility == Visibility.Visible)
        {
            PlaylistSelectorContainer.Visibility = Visibility.Collapsed;
            return;
        }

        SearchTextBox.Visibility = Visibility.Collapsed;
        PlaylistSelectorContainer.Visibility = Visibility.Visible;

        PlaylistLoadingBar.Visibility = Visibility.Visible;
        var playlists = await DeezerCdpService.GetUserPlaylistsAsync();
        PlaylistLoadingBar.Visibility = Visibility.Collapsed;
        PlaylistListView.ItemsSource = playlists;
    }

    private async void PlaylistListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistListView.SelectedItem is DeezerPlaylist playlist)
        {
            PlaylistListView.SelectedItem = null;
            PlaylistSelectorContainer.Visibility = Visibility.Collapsed;
            LoadingBar.Visibility = Visibility.Visible;

            bool success = await DeezerCdpService.PlayPlaylistAsync(playlist.Id);
            await Task.Delay(300);
            var activeSession = _mainWindow.GetActiveMediaSession();
            var songInfo = activeSession != null ? MainWindow.TryGetMediaProperties(activeSession.ControlSession) : null;
            LoadQueue(songInfo?.Title ?? "", songInfo?.Artist ?? "", forceRefresh: true);
        }
    }

    private void SearchToggleButton_Click(object sender, RoutedEventArgs e)
    {
        PlaylistSelectorContainer.Visibility = Visibility.Collapsed;
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

    private void PlayTrack(DeezerTrack track)
    {
        if (track == null || track.TargetIndex < 0) return;

        // 1. Instant 0ms visual update in UI
        foreach (var t in _fullQueue)
        {
            t.IsCurrent = (t.TargetIndex == track.TargetIndex);
        }
        ApplyFilter();
        DeezerService.UpdateCache(_fullQueue);

        // 2. Perform CDP play track at index immediately in background (1ms latency via persistent WebSocket)
        _ = DeezerCdpService.PlayTrackAtIndexAsync(track.TargetIndex);

        // 3. Close flyout if configured
        if (SettingsManager.Current.QueueCloseOnTrackClick)
        {
            CloseWithAnimation();
        }
    }

    private void PlayTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DeezerTrack track)
        {
            PlayTrack(track);
        }
    }

    // Pure Vertical Card Drag-to-Reorder inside Queue ListView
    private bool _isDraggingItem = false;
    private Border? _draggedBorder;
    private System.Windows.Media.TranslateTransform? _draggedTransform;
    private Point _dragStartPointInList;
    private DeezerTrack? _draggedTrackItem;

    private static bool IsDescendantOfButton(DependencyObject? obj)
    {
        while (obj != null)
        {
            if (obj is System.Windows.Controls.Primitives.ButtonBase || obj is Wpf.Ui.Controls.Button)
                return true;
            obj = System.Windows.Media.VisualTreeHelper.GetParent(obj);
        }
        return false;
    }

    private void QueueItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsDescendantOfButton(e.OriginalSource as DependencyObject)) return;

        if (sender is Border border && border.DataContext is DeezerTrack track)
        {
            _dragStartPointInList = e.GetPosition(QueueListView);
            _draggedBorder = border;
            _draggedTrackItem = track;
            _draggedTransform = border.RenderTransform as System.Windows.Media.TranslateTransform;
            _isDraggingItem = false;
        }
    }

    private void QueueItem_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedBorder == null || _draggedTrackItem == null) return;

        Point currentPos = e.GetPosition(QueueListView);
        double deltaY = currentPos.Y - _dragStartPointInList.Y;

        if (!_isDraggingItem && Math.Abs(deltaY) > 5)
        {
            _isDraggingItem = true;
            _draggedBorder.CaptureMouse();
            Panel.SetZIndex(_draggedBorder, 100);
            _draggedBorder.Opacity = 0.85;
        }

        if (_isDraggingItem && _draggedTransform != null)
        {
            // Pure vertical translation: X is locked to 0!
            _draggedTransform.Y = deltaY;
        }
    }

    private void QueueWindow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        QueueItem_PreviewMouseLeftButtonUp(sender, e);
    }

    private async void QueueItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedBorder != null)
        {
            var border = _draggedBorder;
            var transform = _draggedTransform;
            var trackItem = _draggedTrackItem;
            bool wasDragging = _isDraggingItem;

            double finalDeltaY = transform?.Y ?? 0;

            ResetDragState();

            if (wasDragging && trackItem != null)
            {
                int fromIndex = _fullQueue.IndexOf(trackItem);
                int shiftIndices = (int)Math.Round(finalDeltaY / 44.0); // 44px item row height
                int toIndex = Math.Clamp(fromIndex + shiftIndices, 0, _fullQueue.Count - 1);

                if (fromIndex >= 0 && toIndex >= 0 && fromIndex != toIndex)
                {
                    // 1. Instant 0ms visual reorder in WPF UI
                    _fullQueue.RemoveAt(fromIndex);
                    _fullQueue.Insert(toIndex, trackItem);

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
        }
    }

    private void ResetDragState()
    {
        if (_draggedBorder != null)
        {
            if (_draggedBorder.IsMouseCaptured)
            {
                try { _draggedBorder.ReleaseMouseCapture(); } catch { }
            }
            _draggedBorder.Opacity = 1.0;
            Panel.SetZIndex(_draggedBorder, 0);
        }

        if (_draggedTransform != null)
        {
            _draggedTransform.Y = 0;
        }

        _isDraggingItem = false;
        _draggedBorder = null;
        _draggedTransform = null;
        _draggedTrackItem = null;
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
