using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BCATracker.UI.Services;

namespace BCATracker.UI;

public partial class LauncherWindow : Window
{
    readonly AppSettings _settings;

    public bool PickWasMade { get; private set; }
    public event EventHandler<UIChoice>? PickConfirmed;

    public LauncherWindow() : this(AppSettings.Load()) { }

    public LauncherWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        DontAskAgainCheck.IsChecked = _settings.RememberLauncherChoice;
    }


    void ModernCard_Click(object? sender, RoutedEventArgs e) => Pick(UIChoice.Modern);
    void LegacyCard_Click(object? sender, RoutedEventArgs e) => Pick(UIChoice.Legacy);

    void Pick(UIChoice choice)
    {
        PickWasMade = true;

        _settings.PreferredUI = choice;
        _settings.RememberLauncherChoice = DontAskAgainCheck.IsChecked == true;
        _settings.Save();

        PickConfirmed?.Invoke(this, choice);
    }
}
