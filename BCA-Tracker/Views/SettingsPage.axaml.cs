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

        DataSubmissionCheck.IsChecked = _settings.DataSubmissionEnabled;
        EndpointBox.Text              = _settings.DataSubmissionEndpoint ?? "";
        AccountIdBox.Text             = _settings.AnonymousAccountId   ?? "";

        MatchesFolderBox.Text = _settings.MatchesFolder ?? "";

        PlayerNameBox.Text = _settings.PlayerNameOverride ?? "";

        // Pick the ComboBoxItem whose Tag matches the saved value.
        // ComboBox doesn't bind to a tag value out of the box; we
        // iterate the items and select by tag string.
        SelectComboByTag(AccentColorCombo, _settings.AccentColor ?? "purple");
        SelectComboByTag(CloseBehaviorCombo, _settings.CloseBehavior ?? "quit");
        AutoJumpCheck.IsChecked = _settings.AutoJumpToLiveMatch;

        DiscordEnabledCheck.IsChecked = _settings.DiscordRpcEnabled;
        DiscordClientIdBox.Text       = _settings.DiscordClientId ?? "";

        UpdateProfilePictureUI();

        VersionText.Text = "v" + GetVersion();
        RuntimeText.Text = $".NET {Environment.Version} · {RuntimeInformation.OSDescription}";

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

        // Upload status — pulled live from the running uploader.
        try
        {
            var up = AppServices.Uploader;
            if (!_settings.DataSubmissionEnabled)
            {
                UploadStatusText.Text = "disabled";
            }
            else if (string.IsNullOrEmpty(_settings.DataSubmissionEndpoint))
            {
                UploadStatusText.Text = "no endpoint set";
            }
            else
            {
                string lastOk = up.LastSuccessUtc == DateTime.MinValue
                    ? "never"
                    : up.LastSuccessUtc.ToLocalTime().ToString("HH:mm:ss");
                string err = string.IsNullOrEmpty(up.LastError) ? "" : $" · err={up.LastError}";
                UploadStatusText.Text =
                    $"sent={up.UploadedCount} pending={up.PendingCount} failed={up.FailedCount} · last ok={lastOk}{err}";
            }
        }
        catch
        {
            // best-effort
        }
    }

    void DataSubmission_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.DataSubmissionEnabled = DataSubmissionCheck.IsChecked == true;
        _settings.Save();
        AppServices.ApplyUploaderConfig();
    }

    void Endpoint_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.DataSubmissionEndpoint = EndpointBox.Text?.Trim() ?? "";
        _settings.Save();
        AppServices.ApplyUploaderConfig();
    }

    void PlayerName_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        // Trim and length-cap. BCA's own player names are capped at
        // 32 characters; we mirror that so an excessively long fallback
        // doesn't break layout downstream.
        string name = (PlayerNameBox.Text ?? "").Trim();
        if (name.Length > 32) name = name.Substring(0, 32);
        _settings.PlayerNameOverride = name;
        _settings.Save();
    }

    void AccentColor_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (AccentColorCombo.SelectedItem is not ComboBoxItem item) return;
        string name = item.Tag?.ToString() ?? "purple";
        _settings.AccentColor = name;
        _settings.Save();
        // Live update so the user sees the change without restarting.
        AccentTheme.Apply(name);
    }

    /// <summary>
    /// "Open config file" button next to the chart-colors description.
    /// Opens chart-colors.json in the system default editor (Notepad on
    /// Windows), creating the file with defaults first if it doesn't
    /// exist. Users edit, save, and the change picks up next time
    /// they open the Trends page.
    /// </summary>
    void ChartColorsOpen_Click(object? sender, RoutedEventArgs e)
    {
        // Force-create the file with defaults so the user always has
        // something concrete to edit, even on a fresh install.
        ChartColors.Reload();
        string path = ChartColors.ConfigPath();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch
        {
            // If shell-execute fails (no editor associated with .json),
            // fall back to opening the containing folder so the user
            // can locate the file themselves.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = System.IO.Path.GetDirectoryName(path)!,
                    UseShellExecute = true,
                });
            }
            catch { }
        }
    }

    void AutoJump_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AutoJumpToLiveMatch = AutoJumpCheck.IsChecked == true;
        _settings.Save();
    }

    void CloseBehavior_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (CloseBehaviorCombo.SelectedItem is not ComboBoxItem item) return;
        _settings.CloseBehavior = item.Tag?.ToString() ?? "quit";
        _settings.Save();
    }

    /// <summary>Pick the ComboBoxItem whose Tag string equals the
    /// supplied value, case-insensitively. No match -> select index 0.
    /// </summary>
    static void SelectComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.ItemCount; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    async void RequestData_Click(object? sender, RoutedEventArgs e)
    {
        string endpoint = (_settings.DataSubmissionEndpoint ?? "").TrimEnd('/');
        string accountId = _settings.AnonymousAccountId ?? "";
        if (string.IsNullOrEmpty(endpoint))
        {
            DataActionStatusText.Text = "Set a server endpoint first.";
            return;
        }
        if (string.IsNullOrEmpty(accountId))
        {
            DataActionStatusText.Text = "No installation ID. Nothing to request.";
            return;
        }

        RequestDataBtn.IsEnabled = false;
        DataActionStatusText.Text = "Requesting...";
        try
        {
            using var http = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
            string url = $"{endpoint}/v1/matches/by-account/{accountId}";
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                DataActionStatusText.Text = $"Server returned {(int)resp.StatusCode} {resp.ReasonPhrase}.";
                return;
            }
            string json = await resp.Content.ReadAsStringAsync();

            // Save the dump next to the matches folder so the user can
            // open it from File Explorer.
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir  = Path.Combine(root, "BCA-Tracker");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"data-export-{DateTime.Now:yyyy-MM-dd-HHmmss}.json");
            await File.WriteAllTextAsync(file, json);

            DataActionStatusText.Text = $"Saved to {file}";

            // Open the containing folder so the user can grab the file.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                });
            }
            catch { }
        }
        catch (Exception ex)
        {
            DataActionStatusText.Text = $"Request failed: {ex.Message}";
        }
        finally
        {
            RequestDataBtn.IsEnabled = true;
        }
    }

    async void DeleteData_Click(object? sender, RoutedEventArgs e)
    {
        string endpoint = (_settings.DataSubmissionEndpoint ?? "").TrimEnd('/');
        string accountId = _settings.AnonymousAccountId ?? "";
        if (string.IsNullOrEmpty(endpoint))
        {
            DataActionStatusText.Text = "Set a server endpoint first.";
            return;
        }
        if (string.IsNullOrEmpty(accountId))
        {
            DataActionStatusText.Text = "No installation ID. Nothing to delete.";
            return;
        }

        // Confirmation dialog: this is destructive and server-side
        // irreversible, so do not just rip on a single click.
        var owner = TopLevel.GetTopLevel(this) as Window;
        var confirm = new Window
        {
            Title = "Delete server data?",
            Width = 420,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        bool? confirmed = null;
        var yes = new Button { Content = "Delete everything", Classes = { "primary" } };
        var no  = new Button { Content = "Cancel", Margin = new Avalonia.Thickness(8, 0, 0, 0) };
        yes.Click += (_, _) => { confirmed = true;  confirm.Close(); };
        no.Click  += (_, _) => { confirmed = false; confirm.Close(); };

        confirm.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "This will permanently delete all match data the server has stored under your installation ID. Local files on this machine are not affected.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { yes, no },
                },
            },
        };
        if (owner is not null) await confirm.ShowDialog(owner);
        else confirm.Show();
        if (confirmed != true) { DataActionStatusText.Text = "Delete cancelled."; return; }

        DeleteDataBtn.IsEnabled = false;
        DataActionStatusText.Text = "Deleting...";
        try
        {
            using var http = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
            string url = $"{endpoint}/v1/matches/by-account/{accountId}";
            using var resp = await http.DeleteAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                DataActionStatusText.Text = $"Server returned {(int)resp.StatusCode} {resp.ReasonPhrase}.";
                return;
            }
            DataActionStatusText.Text = "Done. All your match data has been removed from the server.";
        }
        catch (Exception ex)
        {
            DataActionStatusText.Text = $"Delete failed: {ex.Message}";
        }
        finally
        {
            DeleteDataBtn.IsEnabled = true;
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
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif" },
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
                // Decode at 256px — the settings preview is small but on a
                // 2x DPI display the actual pixels are 2× the logical size,
                // so this keeps the thumbnail sharp without bloating memory.
                using var fs = System.IO.File.OpenRead(path);
                AvatarPreview.Source = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(
                    fs, 256, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
                Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(
                    AvatarPreview,
                    Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
                AvatarPreviewHost.IsVisible = true;
                AvatarFallback.IsVisible    = false;
                ProfilePictureStatusText.Text = System.IO.Path.GetFileName(path);
                return;
            }
            catch
            {
                // fall through to the fallback avatar
            }
        }

        // No picture, or it failed to load — show the fallback gradient.
        AvatarPreview.Source = null;
        AvatarPreviewHost.IsVisible = false;
        AvatarFallback.IsVisible    = true;
        ProfilePictureStatusText.Text = "No picture set - using initial avatar";
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
