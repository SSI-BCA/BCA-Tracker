using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BCATracker.Core;
using BCATracker.UI.Services;

namespace BCATracker.UI.Views;

public partial class SettingsPage : UserControl
{
    AppSettings _settings = AppServices.Settings;
    bool _suppressEvents = true;

    DispatcherTimer? _refreshTimer;

    public SettingsPage()
    {
        InitializeComponent();
        AttachedToVisualTree   += (_, _) => Load();
        DetachedFromVisualTree += (_, _) => StopTimer();
    }

    void Load()
    {
        _suppressEvents = true;

        RememberCheck.IsChecked = _settings.RememberLauncherChoice;

        string targetTag = _settings.PreferredUI.ToString();
        foreach (var item in PreferredUICombo.Items)
            if (item is ComboBoxItem cbi && (cbi.Tag as string) == targetTag)
            {
                PreferredUICombo.SelectedItem = cbi;
                break;
            }

        MatchesFolderBox.Text = _settings.MatchesFolder ?? "";

        DiscordEnabledCheck.IsChecked = _settings.DiscordRpcEnabled;
        DiscordClientIdBox.Text       = _settings.DiscordClientId ?? "";

        UpdateProfilePictureUI();

        VersionText.Text = "v" + GetVersion();
        RuntimeText.Text = $".NET {Environment.Version} · {RuntimeInformation.OSDescription}";

        // First populate, then refresh on a 1-second timer while the page is
        // visible. We don't subscribe to LiveMatchService.Tick directly because
        // these strings only need to refresh once a second, not every snapshot.
        RefreshAboutPanel();
        StartTimer();

        _suppressEvents = false;
    }

    void StartTimer()
    {
        StopTimer();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => RefreshAboutPanel();
        _refreshTimer.Start();
    }

    void StopTimer()
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    void RefreshAboutPanel()
    {
        var live = AppServices.LiveMatch;

        // GAME row: process running? attached?
        bool procRunning = Process.GetProcessesByName("BattleCoreArena").Length > 0;
        GameStatusText.Text = procRunning
            ? "Battle Core Arena · process running"
            : "Battle Core Arena · process not detected";

        // READER row: attached state
        ReaderStatusText.Text = live.IsAttached
            ? "attached · reading every 500 ms"
            : procRunning
                ? "process found · attaching… (run as admin if this stays)"
                : "idle · waiting for game";

        // MATCH row: lobby/in-match/post-match summary
        MatchSnapshot? snap = live.Snapshot;
        if (snap is null)
        {
            MatchStatusText.Text = "—";
        }
        else if (snap.InMatch)
        {
            MatchStatusText.Text = $"in match · {snap.CurrentMap} · {snap.ModeName} · {snap.Timer}";
        }
        else if (snap.IsLobby)
        {
            MatchStatusText.Text = $"lobby · {snap.Lobby?.MapName ?? "?"} · {snap.Lobby?.ModeName ?? "?"}";
        }
        else if (snap.IsPostMatch)
        {
            MatchStatusText.Text = "post-match summary";
        }
        else if (snap.IsMainMenu)
        {
            MatchStatusText.Text = "main menu";
        }
        else
        {
            MatchStatusText.Text = snap.StateName;
        }

        // SAVES row: how many json files exist + last modified time
        try
        {
            var root = AppServices.Matches.Root;
            if (Directory.Exists(root))
            {
                var files = Directory.EnumerateFiles(root, "match_*.json", SearchOption.AllDirectories).ToList();
                if (files.Count == 0)
                {
                    SavesText.Text = "0 saved matches";
                }
                else
                {
                    var newest = files.Select(File.GetLastWriteTime).Max();
                    SavesText.Text = $"{files.Count} saved · last write {newest:yyyy-MM-dd HH:mm}";
                }
            }
            else
            {
                SavesText.Text = $"folder not found: {root}";
            }
        }
        catch (Exception ex)
        {
            SavesText.Text = "scan failed: " + ex.Message;
        }
    }

    void Remember_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.RememberLauncherChoice = RememberCheck.IsChecked == true;
        _settings.Save();
    }

    void PreferredUI_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (PreferredUICombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
        {
            if (Enum.TryParse<UIChoice>(tag, out UIChoice choice))
            {
                _settings.PreferredUI = choice;
                _settings.Save();
            }
        }
    }

    void MatchesFolder_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        string text = MatchesFolderBox.Text?.Trim() ?? "";
        _settings.MatchesFolder = string.IsNullOrEmpty(text) ? null : text;
        _settings.Save();
    }

    void ResetMatchesFolder_Click(object? sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        MatchesFolderBox.Text = "";
        _settings.MatchesFolder = null;
        _settings.Save();
        _suppressEvents = false;
    }

    void DiscordEnabled_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.DiscordRpcEnabled = DiscordEnabledCheck.IsChecked == true;
        _settings.Save();
    }

    void DiscordClientId_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.DiscordClientId = DiscordClientIdBox.Text?.Trim() ?? "";
        _settings.Save();
    }

    async void ChoosePicture_Click(object? sender, RoutedEventArgs e)
    {
        var top = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (top is null) return;

        var picked = await top.StorageProvider.OpenFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Choose profile picture",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Images")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" },
                    },
                },
            });

        if (picked.Count == 0) return;
        var file = picked[0];
        string? path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        _settings.ProfilePicturePath = path;
        _settings.Save();
        UpdateProfilePictureUI();
    }

    void ClearPicture_Click(object? sender, RoutedEventArgs e)
    {
        _settings.ProfilePicturePath = null;
        _settings.Save();
        UpdateProfilePictureUI();
    }

    void UpdateProfilePictureUI()
    {
        string? path = _settings.ProfilePicturePath;
        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
        {
            try
            {
                using var fs = System.IO.File.OpenRead(path);
                AvatarPreview.Source = new Avalonia.Media.Imaging.Bitmap(fs);
                AvatarPreviewHost.IsVisible = true;
                AvatarFallback.IsVisible    = false;
                ProfilePictureStatusText.Text = System.IO.Path.GetFileName(path);
                return;
            }
            catch
            {
                // fall through
            }
        }
        AvatarPreviewHost.IsVisible = false;
        AvatarFallback.IsVisible    = true;
        ProfilePictureStatusText.Text = "No picture set — using initial avatar";
    }

    static string GetVersion()
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            int plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        Version? v = asm.GetName().Version;
        return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
