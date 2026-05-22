using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BCATracker.Core;
using BCATracker.UI.Services;

namespace BCATracker.UI.Views;

/// <summary>
/// View-model row for the "Connected players" card. We don't pull in a
/// full MVVM framework — Avalonia binds to plain CLR objects fine, and
/// these are throwaway DTOs we rebuild every refresh anyway.
/// </summary>
public sealed class ConnectedMemberRow
{
    public string Name      { get; set; } = "";
    public string NetBirdIP { get; set; } = "";
    public string RoleLabel { get; set; } = "";
    public bool   HasRoleBadge { get; set; }
    /// <summary>Brush for the role pill background. Host gets accent
    /// purple, guest gets a muted gray.</summary>
    public IBrush RoleBackground { get; set; } = Brushes.Gray;
}

public partial class LobbyBrowserPage : UserControl
{
    DispatcherTimer? _refreshTimer;
    DispatcherTimer? _membersTimer;
    bool _suppressEvents;

    public LobbyBrowserPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            LoadHostingControls();
            _ = RefreshAsync();
            _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(30),
                DispatcherPriority.Background, async (_, _) => await RefreshAsync());
            _refreshTimer.Start();
            // The members list updates more often than the lobby browser
            // does — 5 s feels live without spamming the backend.
            _membersTimer = new DispatcherTimer(TimeSpan.FromSeconds(5),
                DispatcherPriority.Background, async (_, _) => await RefreshMembersAsync());
            _membersTimer.Start();
            UpdateHostingStatus();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _refreshTimer?.Stop();
            _refreshTimer = null;
            _membersTimer?.Stop();
            _membersTimer = null;
        };
    }

    void LoadHostingControls()
    {
        _suppressEvents = true;
        try
        {
            var s = AppServices.Settings;
            LobbyAdvertiseCheck.IsChecked = s.LobbyAdvertisingEnabled;
            LobbyForceHostCheck.IsChecked = s.LobbyForceHost;
            LobbyNameBox.Text             = s.LobbyAdvertisedName ?? "";
        }
        finally { _suppressEvents = false; }
    }

    void LobbyAdvertise_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var s = AppServices.Settings;
        s.LobbyAdvertisingEnabled = LobbyAdvertiseCheck.IsChecked == true;
        s.Save();
        AppServices.ApplyUploaderConfig();
        UpdateHostingStatus();
    }

    void LobbyForceHost_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var s = AppServices.Settings;
        s.LobbyForceHost = LobbyForceHostCheck.IsChecked == true;
        s.Save();
        UpdateHostingStatus();
    }

    void LobbyName_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var s = AppServices.Settings;
        s.LobbyAdvertisedName = LobbyNameBox.Text?.Trim() ?? "";
        s.Save();
    }

    void Refresh_Click(object? sender, RoutedEventArgs e) => _ = RefreshAsync();

    void UpdateHostingStatus()
    {
        try
        {
            var s = AppServices.Settings;
            if (!s.LobbyAdvertisingEnabled)
            {
                HostingStatusText.Text = "disabled — toggle on to advertise";
            }
            else if (string.IsNullOrEmpty(s.DataSubmissionEndpoint))
            {
                HostingStatusText.Text = "no server endpoint configured (Settings → Data submission)";
            }
            else
            {
                var lobby = AppServices.Lobby;
                HostingStatusText.Text = string.IsNullOrEmpty(lobby.StatusText) ? "(idle)" : lobby.StatusText;
            }

            // The "Connected players" card shows for two cases:
            //   • we're hosting (IsAdvertising + CurrentGroupId set)
            //   • we joined someone else's lobby (IsJoined)
            // Headline text + Leave-button visibility differ; the
            // member list itself is identical.
            var lobbySvc = AppServices.Lobby;
            bool isHostMode = lobbySvc.IsAdvertising && !string.IsNullOrEmpty(lobbySvc.CurrentGroupId);
            bool isGuestMode = lobbySvc.IsJoined;

            if (isHostMode)
            {
                ConnectedBanner.IsVisible = true;
                ConnectedHeaderLabel.Text = "HOSTING LOBBY";
                ConnectedLobbyText.Text   = string.IsNullOrEmpty(s.LobbyAdvertisedName)
                    ? "Your lobby"
                    : s.LobbyAdvertisedName;
                ConnectedAddressText.Text = string.IsNullOrEmpty(lobbySvc.ExternalEndpoint)
                    ? "(waiting for NetBird IP)"
                    : lobbySvc.ExternalEndpoint;
                ConnectedCopyBtn.IsVisible  = !string.IsNullOrEmpty(lobbySvc.ExternalEndpoint);
                // Hosts don't "leave" — they stop hosting via the
                // advertise toggle. Hiding the button avoids confusion.
                ConnectedLeaveBtn.IsVisible = false;
            }
            else if (isGuestMode)
            {
                ConnectedBanner.IsVisible = true;
                ConnectedHeaderLabel.Text = "CONNECTED TO LOBBY";
                string lobbyTitle = string.IsNullOrEmpty(lobbySvc.JoinedLobbyName)
                    ? "(unnamed lobby)" : lobbySvc.JoinedLobbyName;
                string host = string.IsNullOrEmpty(lobbySvc.JoinedHostName)
                    ? "unknown host" : lobbySvc.JoinedHostName;
                ConnectedLobbyText.Text   = $"{lobbyTitle} · hosted by {host}";
                ConnectedAddressText.Text = lobbySvc.JoinedConnectString;
                ConnectedCopyBtn.IsVisible  = true;
                ConnectedLeaveBtn.IsVisible = true;
            }
            else
            {
                ConnectedBanner.IsVisible = false;
            }
        }
        catch { }
    }

    /// <summary>
    /// Pull the live member list for whatever lobby network we're in
    /// (hosting or joined) and bind it to the card. Cheap and
    /// silent — failures just leave the previous list visible.
    /// </summary>
    async Task RefreshMembersAsync()
    {
        // Always update headline state too — handles the case where
        // hosting just came up between timer ticks.
        UpdateHostingStatus();

        var lobbySvc = AppServices.Lobby;
        string groupId =
            (lobbySvc.IsAdvertising && !string.IsNullOrEmpty(lobbySvc.CurrentGroupId))
                ? lobbySvc.CurrentGroupId
                : lobbySvc.JoinedGroupId;

        if (string.IsNullOrEmpty(groupId))
        {
            ConnectedMembersList.ItemsSource = null;
            ConnectedMembersEmpty.IsVisible = false;
            return;
        }

        List<LobbyHostingService.LobbyMember> members;
        try
        {
            members = await lobbySvc.GetLobbyMembersAsync(groupId);
        }
        catch
        {
            members = new();
        }

        // Sort: host first, then by join time (oldest first), then by name.
        members = members
            .OrderByDescending(m => m.IsHost)
            .ThenBy(m => m.JoinedAtUnix)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = members.Select(m => new ConnectedMemberRow
        {
            Name = string.IsNullOrEmpty(m.Name) ? "(unknown)" : m.Name,
            NetBirdIP = m.NetBirdIP,
            HasRoleBadge = true,
            RoleLabel    = m.IsHost ? "HOST" : "GUEST",
            RoleBackground = m.IsHost ? LookupBrush("Accent",
                                            new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)))
                                      : new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),
        }).ToList();

        ConnectedMembersList.ItemsSource = rows;
        ConnectedMembersEmpty.IsVisible  = rows.Count == 0;
    }

    async void ConnectedCopy_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var lobbySvc = AppServices.Lobby;
            string toCopy =
                (lobbySvc.IsAdvertising && !string.IsNullOrEmpty(lobbySvc.ExternalEndpoint))
                    ? lobbySvc.ExternalEndpoint
                    : lobbySvc.JoinedConnectString;
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip is not null && !string.IsNullOrEmpty(toCopy))
                await clip.SetTextAsync(toCopy);
            ConnectedCopyBtn.Content = "Copied!";
            // Brief flash, then restore.
            await Task.Delay(1200);
            ConnectedCopyBtn.Content = "Copy IP";
        }
        catch { ConnectedCopyBtn.Content = "Copy failed"; }
    }

    async void ConnectedLeave_Click(object? sender, RoutedEventArgs e)
    {
        ConnectedLeaveBtn.IsEnabled = false;
        ConnectedLeaveBtn.Content   = "Leaving…";
        try
        {
            await AppServices.Lobby.LeaveJoinedLobbyAsync();
        }
        finally
        {
            ConnectedLeaveBtn.Content   = "Leave";
            ConnectedLeaveBtn.IsEnabled = true;
            UpdateHostingStatus();
        }
    }

    async Task RefreshAsync()
    {
        UpdateHostingStatus();

        List<LobbyInfo> lobbies;
        try
        {
            lobbies = await AppServices.Lobby.Publisher.FetchLobbiesAsync();
        }
        catch
        {
            lobbies = new();
        }

        // Filter out the user's own lobby — joining your own is pointless
        // and creates duplicate UI. We match by NetBird group ID (set
        // when we're hosting) since profile IDs aren't always populated.
        string ownGroup = AppServices.Lobby.CurrentGroupId ?? "";
        if (!string.IsNullOrEmpty(ownGroup))
            lobbies = lobbies.Where(l => l.NetBirdGroupId != ownGroup).ToList();

        LobbyList.ItemsSource = null;
        if (lobbies.Count == 0)
        {
            EmptyText.IsVisible = true;
            ConnectInfoCard.IsVisible = false;
            SubtitleText.Text = AppServices.Lobby.IsAdvertising
                ? "No other lobbies open right now."
                : "No lobbies advertised right now.";
            return;
        }

        EmptyText.IsVisible = false;
        ConnectInfoCard.IsVisible = true;
        SubtitleText.Text =
            $"{lobbies.Count} lobby" + (lobbies.Count == 1 ? "" : "s") + " open.";

        var rows = new List<Control>();
        foreach (var l in lobbies) rows.Add(BuildLobbyRow(l));
        LobbyList.ItemsSource = rows;
    }

    Control BuildLobbyRow(LobbyInfo lobby)
    {
        IBrush primaryFg = LookupBrush("Fg.Primary",   Brushes.White);
        IBrush mutedFg   = LookupBrush("Fg.Muted",     Brushes.Gray);
        IBrush track     = LookupBrush("Bg.SurfaceRaised", Brushes.DimGray);
        IBrush good      = LookupBrush("Good",         new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)));

        int cap = Math.Max(1, lobby.MaxTeamSize * 2);
        bool isFull = lobby.CurrentPlayerCount >= cap;

        var card = new Button
        {
            Classes = { "card-button" },
            Padding = new Thickness(20, 16),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
        };

        // Avatar circle with first initial of host name
        string initial = !string.IsNullOrEmpty(lobby.HostName)
            ? lobby.HostName[0].ToString().ToUpperInvariant()
            : "?";
        var avatar = new Border
        {
            Width = 44, Height = 44,
            CornerRadius = new CornerRadius(22),
            Background = track,
            Margin = new Thickness(0, 0, 16, 0),
            Child = new TextBlock
            {
                Text = initial,
                FontSize = 20, FontWeight = FontWeight.Bold,
                Foreground = primaryFg,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
            [Grid.ColumnProperty] = 0,
        };
        grid.Children.Add(avatar);

        // Center: name + subtitle
        var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(new TextBlock
        {
            Text = lobby.LobbyName,
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            Foreground = primaryFg,
        });
        if (lobby.HasPassword)
        {
            nameRow.Children.Add(new Border
            {
                Background = track,
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(5, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "🔒", FontSize = 10, Foreground = mutedFg },
            });
        }
        nameStack.Children.Add(nameRow);

        string mapDisplay  = BCAEnums.MapRowNameToDisplayName(lobby.MapRowName) ?? lobby.MapRowName;
        string subtitle = $"{mapDisplay} · hosted by {lobby.HostName}";
        nameStack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = mutedFg,
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(nameStack, 1);
        grid.Children.Add(nameStack);

        // Player count
        grid.Children.Add(new TextBlock
        {
            Text = $"{lobby.CurrentPlayerCount} / {cap}",
            FontSize = 14, FontWeight = FontWeight.SemiBold,
            Foreground = isFull ? mutedFg : primaryFg,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
            [Grid.ColumnProperty] = 2,
        });

        // Join button
        var joinBtn = new Button
        {
            Content = isFull ? "Full" : "Join",
            Classes = { "primary" },
            IsEnabled = !isFull,
            [Grid.ColumnProperty] = 3,
        };
        joinBtn.Click += (_, _) => ShowJoinDialog(lobby);
        grid.Children.Add(joinBtn);

        card.Content = grid;
        card.Click += (_, _) => { if (!isFull) ShowJoinDialog(lobby); };
        return card;
    }

    async void ShowJoinDialog(LobbyInfo lobby)
    {
        var dlg = new Window
        {
            Title = "Join Lobby",
            Width = 460, Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false, ShowInTaskbar = false,
        };

        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock
        {
            Text = lobby.LobbyName,
            FontSize = 18, FontWeight = FontWeight.Bold,
            Foreground = LookupBrush("Fg.Primary", Brushes.White),
            Margin = new Thickness(0, 0, 0, 4),
        });

        // We start with a "joining…" status that gets replaced by the
        // connect string once the NetBird network is up.
        var statusText = new TextBlock
        {
            Text = "Connecting to lobby network…",
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = LookupBrush("Fg.Secondary", Brushes.LightGray),
            Margin = new Thickness(0, 0, 0, 12),
        };
        content.Children.Add(statusText);

        var connectBox = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas, monospace"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 12),
            IsVisible = false,
        };
        content.Children.Add(connectBox);

        if (lobby.HasPassword)
        {
            content.Children.Add(new TextBlock
            {
                Text = "🔒 This lobby has a password. Get it from the host before joining.",
                FontSize = 11,
                Foreground = LookupBrush("Fg.Muted", Brushes.Gray),
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var copyBtn  = new Button {
            Content = "Copy", Classes = { "primary" },
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = false,
        };
        var closeBtn = new Button { Content = "Close" };
        copyBtn.Click += async (_, _) =>
        {
            try
            {
                var clip = TopLevel.GetTopLevel(dlg)?.Clipboard;
                if (clip is not null) await clip.SetTextAsync(connectBox.Text ?? "");
                copyBtn.Content = "Copied!";
            }
            catch { copyBtn.Content = "Copy failed"; }
        };
        closeBtn.Click += (_, _) => dlg.Close();
        buttons.Children.Add(copyBtn);
        buttons.Children.Add(closeBtn);
        content.Children.Add(buttons);

        dlg.Content = content;

        // Show the dialog non-modally first so we can update the UI as
        // the join progresses.
        Window? owner = Avalonia.VisualTree.VisualExtensions
            .FindAncestorOfType<Window>(this);

        // Kick off the NetBird join in the background; while it runs,
        // the dialog shows "Connecting to lobby network…".
        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            string virtualIp = await AppServices.Lobby.JoinLobbyAsync(lobby);
            if (string.IsNullOrEmpty(virtualIp))
            {
                statusText.Text = "Failed to join the lobby network. " +
                    "Check that the NetBird service is running and the server endpoint is set in Settings.";
                statusText.Foreground = LookupBrush("Danger", Brushes.Red);
                return;
            }
            statusText.Text = "Connected! Paste this into BCA's direct-connect field:";
            connectBox.Text = $"{virtualIp}:{lobby.HostExternalPort}";
            connectBox.IsVisible = true;
            copyBtn.IsEnabled = true;
        });

        if (owner is not null) await dlg.ShowDialog(owner);
        else dlg.Show();
    }

    static IBrush LookupBrush(string key, IBrush fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
            && value is IBrush b)
            return b;
        return fallback;
    }
}
