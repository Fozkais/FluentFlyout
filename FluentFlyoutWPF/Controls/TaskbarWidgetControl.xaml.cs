// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF;
using FluentFlyoutWPF.Classes.Services;
using FluentFlyoutWPF.Windows;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Wpf.Ui.Controls;

namespace FluentFlyout.Controls;

/// <summary>
/// Interaction logic for TaskbarWidgetControl.xaml
/// </summary>
public partial class TaskbarWidgetControl : UserControl
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly double _scale = 0.9;
    private readonly int _nativeWidgetsPadding = 216;

    private readonly int _coverImageMargin = 55;

    // Cached width calculations
    private string _cachedTitleText = string.Empty;
    private string _cachedArtistText = string.Empty;
    private double _cachedTitleWidth = 0;
    private double _cachedArtistWidth = 0;
    private double _cachedTitleContainerWidth = -1;
    private double _cachedArtistContainerWidth = -1;
    private readonly int _extraMarginForText = 6; // additional margin to avoid text clipping

    private double _cachedTitleOpacityMaskWidth = -1;
    private double _cachedArtistOpacityMaskWidth = -1;
    private LinearGradientBrush? _cachedTitleOpacityMask;
    private LinearGradientBrush? _cachedArtistOpacityMask;

    private string _actualTitle = string.Empty;
    private string _actualArtist = string.Empty;

    // reference to main window for flyout functions
    private MainWindow? _mainWindow;
    private bool _isPaused;

    public TaskbarWidgetControl()
    {
        InitializeComponent();

        // Apply Windows theme colors (independent of the app theme setting)
        ApplyWindowsTheme();

        // Set DataContext for bindings
        DataContext = SettingsManager.Current;

        MainBorder.SizeChanged += (s, e) =>
        {
            var rect = new RectangleGeometry(new Rect(0, 0, MainBorder.ActualWidth, MainBorder.ActualHeight), 6, 6);
            MainBorder.Clip = rect;
        };

        // for hover animation
        if (MainBorder.Background is not SolidColorBrush)
        {
            MainBorder.Background = new SolidColorBrush(Colors.Transparent);
            MainBorder.Background.Opacity = 0;
        }

        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

        // Initialize control order
        ReorderControls();

        SettingsManager.Current.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SettingsManager.Current.DeezerQueueEnabled))
            {
                _ = UpdateQueueButtonVisibilityAsync();
            }
        };
    }

    public void ReorderControls()
    {
        // Remove ControlsStackPanel from MainStackPanel
        MainStackPanel.Children.Remove(ControlsStackPanel);

        // Reorder based on position setting
        if (SettingsManager.Current.TaskbarWidgetControlsPosition == 0)
        {
            // Left: Controls, Image, Info
            MainStackPanel.Children.Insert(0, ControlsStackPanel);
            ControlsStackPanel.Margin = new Thickness(2, 0, 6, 0); // for some reason margins are weird on left side
        }
        else
        {
            // Right: Image, Info, Controls
            MainStackPanel.Children.Add(ControlsStackPanel);
            ControlsStackPanel.Margin = new Thickness(8, 0, 0, 0);
        }
    }

    public void SetVerticalMode(bool isVertical)
    {
        var counterRotate = isVertical ? new RotateTransform(-90) : null;

        SongImageBorder.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        SongImageBorder.RenderTransform = (Transform?)counterRotate ?? Transform.Identity;

        foreach (var button in new Wpf.Ui.Controls.Button[] { PreviousButton, PlayPauseButton, NextButton })
        {
            button.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            button.RenderTransform = (Transform?)counterRotate ?? Transform.Identity;
        }
    }

    public void SetMainWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
    }

    public void ApplyWindowsTheme()
    {
        WindowsThemeDetector.GetWindowsTheme(out _, out var systemTheme);
        bool isDark = systemTheme == WindowsThemeDetector.ThemeMode.Dark;

        var foreground = new SolidColorBrush(isDark
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xE4, 0x1C, 0x1C, 0x1C));

        SongTitle.Foreground = foreground;
        SongArtist.Foreground = foreground;
        PreviousButton.Foreground = foreground;
        PlayPauseButton.Foreground = foreground;
        NextButton.Foreground = foreground;
    }

    private void Grid_MouseEnter(object sender, MouseEventArgs e)
    {
        SolidColorBrush targetBackgroundBrush;
        WindowsThemeDetector.GetWindowsTheme(out _, out var systemTheme);
        bool isDark = systemTheme == WindowsThemeDetector.ThemeMode.Dark;

        if (isDark)
        { // dark mode
            targetBackgroundBrush = new SolidColorBrush(Color.FromArgb(197, 255, 255, 255)) { Opacity = 0.12 };
            TopBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(93, 255, 255, 255)) { Opacity = 0.35 };
        }
        else
        { // light mode
            targetBackgroundBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)) { Opacity = 0.75 };
            TopBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(93, 255, 255, 255)) { Opacity = 1 };
        }

        // Animate background
        var backgroundAnimation = new ColorAnimation
        {
            To = targetBackgroundBrush.Color,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var backgroundOpacityAnimation = new DoubleAnimation
        {
            To = targetBackgroundBrush.Opacity,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        if (MainBorder.Background is not SolidColorBrush)
        {
            MainBorder.Background = new SolidColorBrush(Colors.Transparent) { Opacity = 0 };
        }

        MainBorder.Background.BeginAnimation(SolidColorBrush.ColorProperty, backgroundAnimation);
        MainBorder.Background.BeginAnimation(SolidColorBrush.OpacityProperty, backgroundOpacityAnimation);

        // Hover scale feedback for music note icon button
        DoubleAnimation scaleAnim = new DoubleAnimation(1.10, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        SongImageScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        SongImageScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private void Grid_MouseLeave(object sender, MouseEventArgs e)
    {
        // Animate back to transparent
        var backgroundAnimation = new ColorAnimation
        {
            To = Colors.Transparent,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        var backgroundOpacityAnimation = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        MainBorder.Background?.BeginAnimation(SolidColorBrush.ColorProperty, backgroundAnimation);
        MainBorder.Background?.BeginAnimation(SolidColorBrush.OpacityProperty, backgroundOpacityAnimation);

        TopBorder.BorderBrush = Brushes.Transparent;

        // Reset scale back to 1.0
        DoubleAnimation scaleAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        SongImageScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        SongImageScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Press-down tactile feedback animation
        DoubleAnimation scaleAnim = new DoubleAnimation(0.88, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        SongImageScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        SongImageScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private bool _isLaunchingDeezer = false;

    private void SetLoadingState(bool isLoading)
    {
        Dispatcher.Invoke(() =>
        {
            if (isLoading)
            {
                SongImagePlaceholder.Symbol = SymbolRegular.ArrowSync24;
                SongImagePlaceholder.Visibility = Visibility.Visible;
                ToolTipService.SetToolTip(SongImageBorder, "Lancement de Deezer en cours...");
                ToolTipService.SetToolTip(MainBorder, "Lancement de Deezer en cours...");

                DoubleAnimation rotateAnim = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1)))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };
                RotateTransform rotateTransform = new RotateTransform();
                SongImagePlaceholder.RenderTransformOrigin = new Point(0.5, 0.5);
                SongImagePlaceholder.RenderTransform = rotateTransform;
                rotateTransform.BeginAnimation(RotateTransform.AngleProperty, rotateAnim);
            }
            else
            {
                SongImagePlaceholder.RenderTransform = null;
                SongImagePlaceholder.Symbol = SymbolRegular.MusicNote220;
            }
        });
    }

    private void Grid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Release bounce-back scale animation
        DoubleAnimation scaleAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut }
        };
        SongImageScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        SongImageScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

        if (_mainWindow == null) return;

        // If no media is playing (fallback music note is displayed) and Deezer Integration is enabled:
        // Launch (or restart with CDP) Deezer Desktop!
        if (string.IsNullOrEmpty(_actualTitle) && string.IsNullOrEmpty(_actualArtist))
        {
            if (SettingsManager.Current.PreferredMusicService == "Spotify" || (!SettingsManager.Current.DeezerQueueEnabled && SpotifyAuthService.IsAuthenticated))
            {
                if (!_isLaunchingDeezer)
                {
                    _isLaunchingDeezer = true;
                    SetLoadingState(true);
                    _ = Task.Run(async () =>
                    {
                        await SpotifyService.EnsureSpotifyRunningAsync();
                    });
                }
            }
            else if (SettingsManager.Current.DeezerQueueEnabled)
            {
                if (!_isLaunchingDeezer)
                {
                    _isLaunchingDeezer = true;
                    SetLoadingState(true);
                    _ = Task.Run(async () =>
                    {
                        await DeezerCdpService.LaunchDeezerWithDebugPortAsync(forceRestartIfNoCdp: true);
                    });
                }
            }
        }

        // Toggle media flyout window when clicked
        _mainWindow.ShowMediaFlyout(toggleMode: true, forceShow: true);
    }

    public (double logicalWidth, double logicalHeight) CalculateSize(double dpiScale)
    {
        if (string.IsNullOrEmpty(_actualTitle) && string.IsNullOrEmpty(_actualArtist))
        {
            MainStackPanel.Margin = new Thickness(4, 0, 4, 0);
            return (44, 40);
        }
        else
        {
            MainStackPanel.Margin = new Thickness(4, 0, -100, 0);
        }

        // calculate widget width - use cached values if text hasn't changed
        string currentTitle = _actualTitle;
        string currentArtist = _actualArtist;

        bool textChanged = false;

        if (!string.Equals(currentTitle, _cachedTitleText, StringComparison.Ordinal))
        {
            _cachedTitleWidth = Math.Round(StringWidth.GetStringWidth(currentTitle, 400), 2);
            _cachedTitleText = currentTitle;
            textChanged = true;
        }
        if (!string.Equals(currentArtist, _cachedArtistText, StringComparison.Ordinal))
        {
            _cachedArtistWidth = Math.Round(StringWidth.GetStringWidth(currentArtist, 400), 2);
            _cachedArtistText = currentArtist;
            textChanged = true;
        }

        // maximum width limit, same as Windows native widget
        double maxLogicalWidth = _nativeWidgetsPadding / _scale;
        double logicalWidth;

        if (SettingsManager.Current.TaskbarWidgetFixedWidth)
        {
            // pin to maximum width so right-aligned controls don't shift between songs
            logicalWidth = maxLogicalWidth;
        }
        else
        {
            logicalWidth = Math.Max(_cachedTitleWidth, _cachedArtistWidth) + _coverImageMargin + _extraMarginForText; // add margin for cover image
            logicalWidth = Math.Min(logicalWidth, maxLogicalWidth);
        }

        double newTitleContainerWidth = Math.Max(logicalWidth - _coverImageMargin, 0);
        double newArtistContainerWidth = Math.Max(logicalWidth - _coverImageMargin, 0);
        bool widthChanged = false;

        if (_cachedTitleContainerWidth != newTitleContainerWidth)
        {
            SongTitleContainer.Width = newTitleContainerWidth;
            _cachedTitleContainerWidth = newTitleContainerWidth;
            widthChanged = true;
        }

        if (_cachedArtistContainerWidth != newArtistContainerWidth)
        {
            SongArtistContainer.Width = newArtistContainerWidth;
            _cachedArtistContainerWidth = newArtistContainerWidth;
            widthChanged = true;
        }

        // Refresh animations if layout bounds or text contents change
        if (textChanged || widthChanged)
        {
            UpdateMarquees();
        }

        // add space for playback controls if enabled and visible
        if (SettingsManager.Current.TaskbarWidgetControlsEnabled && ControlsStackPanel.Visibility == Visibility.Visible)
        {
            logicalWidth += 138;
        }

        double logicalHeight = 40; // default height

        return (logicalWidth, logicalHeight);
    }

    public void UpdateMarquees()
    {
        double titleAvailableWidth = double.IsNaN(SongTitleContainer.Width) ? 0 : SongTitleContainer.Width;
        double artistAvailableWidth = double.IsNaN(SongArtistContainer.Width) ? 0 : SongArtistContainer.Width;

        bool isScrollingEnabled = SettingsManager.Current.TaskbarWidgetScrollingEnabled;

        UpdateMarquee(SongTitle, SongTitleContainer, _cachedTitleWidth, titleAvailableWidth, isScrollingEnabled);
        UpdateMarquee(SongArtist, SongArtistContainer, _cachedArtistWidth, artistAvailableWidth, isScrollingEnabled);
    }

    private void UpdateMarquee(System.Windows.Controls.TextBlock textBlock, Canvas container, double textWidth, double availableWidth, bool isEnabled)
    {
        if (textBlock.RenderTransform as TranslateTransform is not { } transform) return;

        int speed = SettingsManager.Current.TaskbarWidgetScrollingTextSpeed;
        bool loopForever = SettingsManager.Current.TaskbarWidgetScrollingTextLoopForever;
        bool isTitle = textBlock == SongTitle;
        double containerWidth = container.Width;

        // references moved outside so they may be called in the else block later
        ref double cachedMaskWidth = ref (isTitle ? ref _cachedTitleOpacityMaskWidth : ref _cachedArtistOpacityMaskWidth);
        ref LinearGradientBrush? cachedMask = ref (isTitle ? ref _cachedTitleOpacityMask : ref _cachedArtistOpacityMask);

        if (isEnabled && textWidth > availableWidth && containerWidth > 0 && !double.IsNaN(containerWidth))
        {
            textBlock.Width = double.NaN;
            textBlock.TextTrimming = TextTrimming.None;

            string origText = isTitle ? _actualTitle : _actualArtist;

            if (cachedMask == null || Math.Abs(containerWidth - cachedMaskWidth) > 0.5)
            {
                // 12.0 is the width in pixels of the gradient fade on the left and right hand edges of the 
                // text container.
                double fadeFraction = 12.0 / containerWidth;
                if (fadeFraction > 0.5) fadeFraction = 0.5;

                cachedMask = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(containerWidth, 0),
                    MappingMode = BrushMappingMode.Absolute
                };

                cachedMask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.0));
                cachedMask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), fadeFraction));
                cachedMask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 1.0 - fadeFraction));
                cachedMask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0));
                cachedMaskWidth = containerWidth;
            }

            container.OpacityMask = cachedMask;

            if (loopForever)
            {
                // continuous looping should have the fades constantly active (as its infinite)
                cachedMask.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, null);
                cachedMask.GradientStops[3].BeginAnimation(GradientStop.ColorProperty, null);
                cachedMask.GradientStops[0].Color = Color.FromArgb(0, 255, 255, 255);
                cachedMask.GradientStops[3].Color = Color.FromArgb(0, 255, 255, 255);

                // \u00A0 are non-breaking spaces, which prevents WPF from collapsing and/or trimming
                // them
                string spacer = "\u00A0\u00A0\u00A0\u00A0\u00A0";
                textBlock.Text = origText + spacer + origText;

                double spacerWidth = StringWidth.GetStringWidth(spacer, 400);
                double scrollDistance = textWidth + spacerWidth;

                double durationToScroll = scrollDistance / speed;
                var animation = new DoubleAnimation
                {
                    From = 0,
                    To = -scrollDistance,
                    Duration = TimeSpan.FromSeconds(durationToScroll),
                    RepeatBehavior = RepeatBehavior.Forever
                };

                transform.BeginAnimation(TranslateTransform.XProperty, animation);
            }
            else
            {
                // Adding 10 pixels gives extra padding so the text scrolls past the container's edge before
                // resetting or reversing; this prevents abrupt cutoffs
                double scrollDistance = textWidth - containerWidth + 10;
                textBlock.Text = origText;

                double durationSeconds = scrollDistance / speed;
                double pauseDuration = 2.0; // wait 2 seconds at the start and end of the scroll
                double tWaitStart = pauseDuration;
                double tScrollEnd = tWaitStart + durationSeconds;
                double tWaitEnd = tScrollEnd + pauseDuration;
                double tScrollBackEnd = tWaitEnd + durationSeconds;
                double tTotalCycle = tScrollBackEnd + pauseDuration;

                var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitStart))));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(-scrollDistance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollEnd))));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(-scrollDistance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd))));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollBackEnd))));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tTotalCycle))));

                // sync fades with the "ping pong" movement
                Color transparentWhite = Color.FromArgb(0, 255, 255, 255);
                Color solidWhite = Color.FromArgb(255, 255, 255, 255);

                // 300 ms is the capped duration for the fade transition; we clamp it so that the fade animation
                // doesn't overlap with the scroll animation on certain shorter texts
                TimeSpan fadeTime = TimeSpan.FromMilliseconds(Math.Min(300, durationSeconds * 1000 / 2.0));

                var leftColorAnim = new ColorAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                leftColorAnim.KeyFrames.Add(new DiscreteColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                leftColorAnim.KeyFrames.Add(new LinearColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitStart))));
                leftColorAnim.KeyFrames.Add(new LinearColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitStart) + fadeTime)));
                leftColorAnim.KeyFrames.Add(new LinearColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd) - fadeTime)));
                leftColorAnim.KeyFrames.Add(new LinearColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd))));
                leftColorAnim.KeyFrames.Add(new LinearColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tTotalCycle))));

                var rightColorAnim = new ColorAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                rightColorAnim.KeyFrames.Add(new DiscreteColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                rightColorAnim.KeyFrames.Add(new LinearColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollEnd) - fadeTime)));
                rightColorAnim.KeyFrames.Add(new LinearColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollEnd))));
                rightColorAnim.KeyFrames.Add(new LinearColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd))));
                rightColorAnim.KeyFrames.Add(new LinearColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd) + fadeTime)));
                rightColorAnim.KeyFrames.Add(new LinearColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tTotalCycle))));

                cachedMask.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, leftColorAnim);
                cachedMask.GradientStops[3].BeginAnimation(GradientStop.ColorProperty, rightColorAnim);

                transform.BeginAnimation(TranslateTransform.XProperty, animation);
            }
        }
        else
        {
            if (cachedMask != null)
            {
                // Prevent memory leaks and/or unwanted behavior by clearing the color animations when the mask is hidden
                cachedMask.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, null);
                cachedMask.GradientStops[3].BeginAnimation(GradientStop.ColorProperty, null);
            }

            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
            textBlock.Text = isTitle ? _actualTitle : _actualArtist;
            textBlock.Width = containerWidth;
            textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            container.OpacityMask = null;
        }
    }

    public void UpdateUi(string title, string artist, BitmapImage? icon, GlobalSystemMediaTransportControlsSessionPlaybackStatus? playbackStatus, GlobalSystemMediaTransportControlsSessionPlaybackControls? playbackControls = null)
    {
        if (title == "-" && artist == "-")
        {
            // No media playing, hide UI
            Dispatcher.Invoke(() =>
            {
                _actualTitle = string.Empty;
                _actualArtist = string.Empty;

                if (SettingsManager.Current.TaskbarWidgetHideCompletely)
                {
                    Visibility = Visibility.Collapsed;
                    return;
                }

                ControlsStackPanel.Visibility = Visibility.Collapsed;
                SongTitle.Text = string.Empty;
                SongArtist.Text = string.Empty;
                SongInfoStackPanel.Visibility = Visibility.Collapsed;
                SongInfoStackPanel.ToolTip = string.Empty;
                SongImagePlaceholder.Symbol = SymbolRegular.MusicNote220;
                SongImagePlaceholder.Visibility = Visibility.Visible;
                SongImage.ImageSource = null;
                BackgroundImage.Source = null;
                SongImageBorder.Margin = new Thickness(0, 0, 0, -3); // align music note better when no cover

                if (SettingsManager.Current.DeezerQueueEnabled)
                {
                    SongImageBorder.ToolTip = "Lancer Deezer (Mode CDP)";
                }
                else
                {
                    SongImageBorder.ToolTip = null;
                }

                MainBorder.Background = new SolidColorBrush(Colors.Transparent);
                MainBorder.Background.Opacity = 0;
                TopBorder.BorderBrush = Brushes.Transparent;

                Visibility = Visibility.Visible;
            });
            return;
        }

        _isPaused = false;
        if (playbackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            _isPaused = true;
        }

        // adjust UI based on available controls
        Dispatcher.Invoke(() =>
        {
            if (SettingsManager.Current.TaskbarWidgetControlsEnabled)
            {
                bool isPrevEnabled = playbackControls?.IsPreviousEnabled ?? true;
                bool isNextEnabled = playbackControls?.IsNextEnabled ?? true;

                PreviousButton.IsHitTestVisible = isPrevEnabled;
                PlayPauseButton.IsHitTestVisible = true; // Always allow Play/Pause button to resume playback when paused!
                NextButton.IsHitTestVisible = isNextEnabled;

                PreviousButton.Opacity = isPrevEnabled ? 1 : 0.5;
                PlayPauseButton.Opacity = 1;
                NextButton.Opacity = isNextEnabled ? 1 : 0.5;
            }
            else
            {
                PreviousButton.IsHitTestVisible = false;
                PlayPauseButton.IsHitTestVisible = false;
                NextButton.IsHitTestVisible = false;

                PreviousButton.Opacity = 0.5;
                NextButton.Opacity = 0.5;
                PlayPauseButton.Opacity = 0.5;
            }
        });

        Dispatcher.Invoke(() =>
        {
            string newTitle = !string.IsNullOrEmpty(title) ? title : "-";
            string newArtist = !string.IsNullOrEmpty(artist) ? artist : "-";

            if (_actualTitle != newTitle || _actualArtist != newArtist)
            {
                // changed info
                if (SettingsManager.Current.TaskbarWidgetAnimated)
                {
                    AnimateEntrance();
                }

                _actualTitle = newTitle;
                _actualArtist = newArtist;

                SongTitle.Text = _actualTitle;
                SongArtist.Text = _actualArtist;
            }

            // Update tooltip with song info
            SongInfoStackPanel.ToolTip = string.Empty;
            SongInfoStackPanel.ToolTip += !string.IsNullOrEmpty(title) ? title : string.Empty;
            SongInfoStackPanel.ToolTip += !string.IsNullOrEmpty(artist) ? "\n\n" + artist : string.Empty;

            if (SettingsManager.Current.TaskbarWidgetControlsEnabled)
            {
                PlayPauseButton.Icon = _isPaused ? new SymbolIcon(SymbolRegular.Play24, filled: true) : new SymbolIcon(SymbolRegular.Pause24, filled: true);
            }

            // change color of icon
            SolidColorBrush brush = BitmapHelper.SavedDominantColors.Count > 0 ?
                BitmapHelper.SavedDominantColors.Last()
                : (SolidColorBrush)Application.Current.TryFindResource("MicaWPF.Brushes.SystemAccentColorTertiary");
            SongImagePlaceholder.Foreground = brush;

            if (icon != null)
            {
                if (_isPaused && SettingsManager.Current.TaskbarWidgetShowPauseOverlay)
                { // show pause icon overlay
                    SongImagePlaceholder.Symbol = SymbolRegular.Pause24;
                    SongImagePlaceholder.Visibility = Visibility.Visible;
                    SongImage.Opacity = 0.4;
                }
                else
                {
                    SongImagePlaceholder.Visibility = Visibility.Collapsed;
                    SongImage.Opacity = 1;
                }
                SongImage.ImageSource = icon;
                BackgroundImage.Source = icon;
                SongImageBorder.Margin = new Thickness(0, 0, 0, -2); // align image better when cover is present
                MainGrid.Cursor = Cursors.Arrow;
                SongImageBorder.Cursor = Cursors.Arrow;
                _isLaunchingDeezer = false;
                SetLoadingState(false);
            }
            else
            {
                SongImagePlaceholder.Symbol = SymbolRegular.MusicNote220;
                SongImagePlaceholder.Visibility = Visibility.Visible;
                SongImage.ImageSource = null;
                BackgroundImage.Source = null;

                if (string.IsNullOrEmpty(_actualTitle) && string.IsNullOrEmpty(_actualArtist) && SettingsManager.Current.DeezerQueueEnabled)
                {
                    MainGrid.Cursor = Cursors.Hand;
                    SongImageBorder.Cursor = Cursors.Hand;
                    ToolTipService.SetToolTip(SongImageBorder, "Lancer Deezer Desktop (Mode CDP)");
                    ToolTipService.SetToolTip(MainBorder, "Lancer Deezer Desktop (Mode CDP)");
                }
            }

            SongTitle.Visibility = Visibility.Visible;
            SongArtist.Visibility = !string.IsNullOrEmpty(artist) ? Visibility.Visible : Visibility.Collapsed; // hide artist if it's not available
            SongInfoStackPanel.Visibility = Visibility.Visible;
            BackgroundImage.Visibility = SettingsManager.Current.TaskbarWidgetBackgroundBlur ? Visibility.Visible : Visibility.Collapsed;

            // on top of XAML visibility binding (XAML binding only hides when disabled in settings)
            ControlsStackPanel.Visibility = SettingsManager.Current.TaskbarWidgetControlsEnabled
                ? Visibility.Visible
                : Visibility.Collapsed;

            _ = UpdateQueueButtonVisibilityAsync();

            Visibility = Visibility.Visible;
        });
    }

    private async Task UpdateQueueButtonVisibilityAsync()
    {
        if (!SettingsManager.Current.DeezerQueueEnabled && !SettingsManager.Current.SpotifyQueueEnabled)
        {
            Dispatcher.Invoke(() => QueueButton.Visibility = Visibility.Collapsed);
            return;
        }

        var session = _mainWindow?.GetActiveMediaSession();
        string appId = session?.ControlSession?.SourceAppUserModelId ?? "";
        bool isDeezer = appId.Contains("deezer", StringComparison.OrdinalIgnoreCase);
        bool isSpotify = appId.Contains("spotify", StringComparison.OrdinalIgnoreCase);
        bool isCdpAvailable = await DeezerCdpService.IsCdpAvailableAsync();
        bool isSpotifyAvailable = SpotifyAuthService.IsAuthenticated;

        Dispatcher.Invoke(() =>
        {
            QueueButton.Visibility = (isDeezer || isSpotify || isCdpAvailable || isSpotifyAvailable) ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private async void AnimateEntrance()
    {
        try
        {
            int msDuration = MainWindow.getDuration();

            // opacity and left to right animation for SongInfoStackPanel
            DoubleAnimation opacityAnimation = new()
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(msDuration),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            DoubleAnimation translateAnimation = new()
            {
                From = -10,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(msDuration),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            // Apply animations
            SongInfoStackPanel.BeginAnimation(OpacityProperty, opacityAnimation);
            TranslateTransform translateTransform = new();
            SongInfoStackPanel.RenderTransform = translateTransform;
            translateTransform.BeginAnimation(TranslateTransform.XProperty, translateAnimation);

            // don't play ControlsStackPanel animation if it's not enabled
            if (!SettingsManager.Current.TaskbarWidgetControlsEnabled)
                return;

            ControlsStackPanel.BeginAnimation(OpacityProperty, opacityAnimation);
            TranslateTransform translateTransform2 = new();
            ControlsStackPanel.RenderTransform = translateTransform2;
            translateTransform2.BeginAnimation(TranslateTransform.XProperty, translateAnimation);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Taskbar Widget error during entrance animation");
        }
    }

    // event handlers for media control buttons
    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var focusedSession = _mainWindow.GetActiveMediaSession();
        if (focusedSession != null)
        {
            await focusedSession.ControlSession.TrySkipPreviousAsync();
        }
        else if (SpotifyAuthService.IsAuthenticated && (SettingsManager.Current.PreferredMusicService == "Spotify" || !SettingsManager.Current.DeezerQueueEnabled))
        {
            await SpotifyService.PrevTrackAsync();
            await Task.Delay(300);
            _mainWindow.ForceRefreshTaskbarWidget();
        }
        else if (SettingsManager.Current.DeezerQueueEnabled && await DeezerCdpService.IsCdpAvailableAsync())
        {
            await DeezerCdpService.PrevTrackAsync();
            await Task.Delay(300);
            _mainWindow.ForceRefreshTaskbarWidget();
        }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var focusedSession = _mainWindow.GetActiveMediaSession();
        if (focusedSession != null)
        {
            await focusedSession.ControlSession.TryTogglePlayPauseAsync();
        }
        else if (SpotifyAuthService.IsAuthenticated && (SettingsManager.Current.PreferredMusicService == "Spotify" || !SettingsManager.Current.DeezerQueueEnabled))
        {
            await SpotifyService.TogglePlayPauseAsync();
            await Task.Delay(300);
            _mainWindow.ForceRefreshTaskbarWidget();
        }
        else if (SettingsManager.Current.DeezerQueueEnabled && await DeezerCdpService.IsCdpAvailableAsync())
        {
            await DeezerCdpService.TogglePlayPauseAsync();
            await Task.Delay(300);
            _mainWindow.ForceRefreshTaskbarWidget();
        }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var focusedSession = _mainWindow.GetActiveMediaSession();
        if (focusedSession != null)
        {
            await focusedSession.ControlSession.TrySkipNextAsync();
        }
        else if (SpotifyAuthService.IsAuthenticated && (SettingsManager.Current.PreferredMusicService == "Spotify" || !SettingsManager.Current.DeezerQueueEnabled))
        {
            await SpotifyService.NextTrackAsync();
            await Task.Delay(300);
            _mainWindow.ForceRefreshTaskbarWidget();
        }
        else if (SettingsManager.Current.DeezerQueueEnabled && await DeezerCdpService.IsCdpAvailableAsync())
        {
            await DeezerCdpService.NextTrackAsync();
            await Task.Delay(300);
            _mainWindow.ForceRefreshTaskbarWidget();
        }
    }

    private static QueueWindow? _activeQueueWindow;

    private void QueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        // Toggle behavior: if queue is already open, close it with animation!
        if (_activeQueueWindow != null && _activeQueueWindow.IsLoaded && _activeQueueWindow.IsVisible)
        {
            _activeQueueWindow.CloseWithAnimation();
            _activeQueueWindow = null;
            return;
        }

        var focusedSession = _mainWindow.GetActiveMediaSession();
        var songInfo = focusedSession != null ? MainWindow.TryGetMediaProperties(focusedSession.ControlSession) : null;

        var queueWin = new QueueWindow(songInfo?.Title ?? "", songInfo?.Artist ?? "");
        _activeQueueWindow = queueWin;
        queueWin.Closed += (s, args) => { if (_activeQueueWindow == queueWin) _activeQueueWindow = null; };

        queueWin.ShowWithAnimation();
    }
}