// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Windows.Controls;

namespace FluentFlyoutWPF.Pages;

public partial class QueueSettingsPage : Page
{
    public QueueSettingsPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
    }
}
