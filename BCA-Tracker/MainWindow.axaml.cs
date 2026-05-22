using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BCATracker.Core;
using BCATracker.UI.Controls;
using BCATracker.UI.Services;
using BCATracker.UI.Views;

namespace BCATracker.UI;

public partial class MainWindow : Window
{
    DispatcherTimer? _updateBannerTimer;
    bool             _updateDismissedThisSession;

    public MainWindow()
    {
        InitializeComponent();

        VersionText.Text = $"v{GetVersion()}";
        UpdateStatusText();

        BuildNav();
        Nav.RefreshTabs();
        Nav.Navigated += OnNavRequested;

        // Reactive status — show "Game attached" / "Tracking match" alongside
        // saved-matches count.
        var live = AppServices.LiveMatch;
        live.Attached     += UpdateStatusText;
        live.Detached     += UpdateStatusText;
        live.Tick         += _ => UpdateStatusText();
        live.MatchStarted += OnMatchStarted;

        Opened += (_, _) => NavigateTo(typeof(HomePage));

        // Poll UpdateChecker for results. The check itself is fired
        // off by AppServices.Initialise(); here we only watch for the
        // result to land so we can show the banner. 5s poll is fine —
        // a network round-trip takes longer than that anyway.
        _updateBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _updateBannerTimer.Tick += (_, _) => RefreshUpdateBanner();
        _updateBannerTimer.Start();
        // Also try immediately in case the check finished before the
        // window opened (unlikely but cheap).
        RefreshUpdateBanner();

        Closed += (_, _) =>
        {
            _updateBannerTimer?.Stop();
            live.Attached     -= UpdateStatusText;
            live.Detached     -= UpdateStatusText;
            live.MatchStarted -= OnMatchStarted;
            AppServices.Shutdown();
        };
    }

    void RefreshUpdateBanner()
    {
        try
        {
            var u = AppServices.UpdateChecker;
            if (_updateDismissedThisSession || !u.UpdateAvailable)
            {
                UpdateBanner.IsVisible = false;
                return;
            }
            UpdateBannerText.Text =
                $"Update available: v{u.LatestVersion} (you have v{u.CurrentVersion}).";
            UpdateBanner.IsVisible = true;
        }
        catch
        {
            // AppServices not ready yet or some other transient issue —
            // just leave the banner hidden.
        }
    }

    async void UpdateDownload_Click(object? sender, RoutedEventArgs e)
    {
        var u = AppServices.UpdateChecker;
        if (string.IsNullOrEmpty(u.LatestSetupUrl)) return;

        UpdateDownloadBtn.IsEnabled = false;
        UpdateDownloadBtn.Content   = "Downloading…";

        try
        {
            // Drop the new installer into %TEMP% with a distinctive name.
            string tempPath = Path.Combine(
                Path.GetTempPath(),
                $"BCA-Tracker-Setup-{u.LatestVersion}.exe");
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromMinutes(5);
                using var stream = await http.GetStreamAsync(u.LatestSetupUrl);
                using var file = File.Create(tempPath);
                await stream.CopyToAsync(file);
            }

            DiagLog.Write($"[Update] Downloaded {tempPath}, launching installer.");

            // Launch the installer, then quit so it can overwrite us.
            // /SILENT shows progress without prompts; the user already
            // agreed by clicking Download. Use /VERYSILENT instead if
            // you want zero UI from the installer.
            var psi = new ProcessStartInfo(tempPath)
            {
                UseShellExecute = true,
                Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            };
            Process.Start(psi);

            // Give the installer a moment to fire up before we exit so
            // Inno can see the running process and ask to close it.
            await Task.Delay(500);
            Close();
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[Update] Download failed: {ex.Message}");
            UpdateDownloadBtn.IsEnabled = true;
            UpdateDownloadBtn.Content   = "Retry";
            UpdateBannerText.Text       = $"Download failed: {ex.Message}";
        }
    }

    void UpdateDismiss_Click(object? sender, RoutedEventArgs e)
    {
        // "Later" hides the banner for this session only — next launch
        // it'll appear again if still applicable. No persistent state.
        _updateDismissedThisSession = true;
        UpdateBanner.IsVisible = false;
    }

    void BuildNav()
    {
        // Live Match is intentionally first — it's where you go when the
        // game is running. Home and the rest are post-match analysis tools.
        Nav.Items.Add(new NavItem("Live Match",     typeof(LiveMatchPage)));
        Nav.Items.Add(new NavItem("Home",           typeof(HomePage)));
        Nav.Items.Add(new NavItem("Match History",  typeof(MatchHistoryPage)));
        Nav.Items.Add(new NavItem("Trends",         typeof(TrendsPage)));
        Nav.Items.Add(new NavItem("Maps",           typeof(MapsPage)));
        Nav.Items.Add(new NavItem("Weapons",        typeof(WeaponsPage)));
        Nav.Items.Add(new NavItem("Lobbies",        typeof(LobbyBrowserPage)));
        Nav.Items.Add(new NavItem("Settings",       typeof(SettingsPage)));
    }

    void OnNavRequested(object? sender, NavItem item) => NavigateTo(item.PageType);

    /// <summary>
    /// Navigate to a page by type. Public so other controls (like
    /// MatchCardView's click handler) can reach it.
    /// </summary>
    public void NavigateTo(Type pageType)
    {
        if (Activator.CreateInstance(pageType) is Control page)
        {
            ContentHost.Content = page;
            Nav.SetActive(pageType);
        }
    }

    /// <summary>Navigate to an already-constructed page. Used by the match
    /// detail view, which needs a constructor argument.</summary>
    public void NavigateTo(Control page)
    {
        ContentHost.Content = page;
        Nav.SetActive(page.GetType());
    }

    /// <summary>
    /// Auto-jump to Live Match when a game starts. We only auto-switch if the
    /// user is currently on a non-live page so we don't yank them away mid-edit
    /// of Settings or similar.
    /// </summary>
    void OnMatchStarted(MatchSnapshot snap)
    {
        if (ContentHost.Content is LiveMatchPage) return;  // already there
        NavigateTo(typeof(LiveMatchPage));
    }

    void UpdateStatusText()
    {
        var live = AppServices.LiveMatch;
        var store = AppServices.Matches;

        int count;
        try
        {
            count = Directory.EnumerateFiles(store.Root, "match_*.json", SearchOption.AllDirectories).Count();
        }
        catch { count = 0; }

        string left;
        var snap = live.Snapshot;
        if (!live.IsAttached)
        {
            left = "Waiting for game";
        }
        else if (snap is null)
        {
            left = "Game attached · reading…";
        }
        else if (snap.InMatch)
        {
            left = $"In match · {snap.CurrentMap} · {snap.Timer}";
        }
        else if (snap.IsLobby)
        {
            left = $"Lobby · {snap.Lobby?.MapName ?? "?"} · {snap.Lobby?.ModeName ?? "?"}";
        }
        else if (snap.IsPostMatch)
        {
            left = "Post-match";
        }
        else if (snap.IsMainMenu)
        {
            left = $"Main menu · {BCATracker.Core.BCAEnums.MainMenuStateName(snap.MainMenuState)}";
        }
        else
        {
            left = $"{snap.StateName}";
        }

        StatusText.Text = $"{left}    ·    {count} saved matches";
    }

    static string GetVersion()
    {
        // Prefer InformationalVersion: it includes the suffix (e.g. "0.4.0-beta")
        // because csproj writes <VersionSuffix> into AssemblyInformationalVersion.
        Assembly asm = Assembly.GetExecutingAssembly();
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            // .NET appends a build-metadata suffix like "+abcd1234" — strip it.
            int plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        Version? v = asm.GetName().Version;
        return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
