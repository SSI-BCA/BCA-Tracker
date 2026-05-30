using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using BCATracker.Core;
using BCATracker.UI.Services;

namespace BCATracker.UI.Views;

public partial class LiveMatchPage : UserControl
{
    public LiveMatchPage()
    {
        InitializeComponent();

        // Subscribe to live updates while attached to the visual tree;
        // unsubscribe on detach so multiple navigations don't stack handlers.
        AttachedToVisualTree   += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var live = AppServices.LiveMatch;
        live.Tick     += OnTick;
        live.Attached += OnGameAttached;
        live.Detached += OnGameDetached;

        // Populate immediately with whatever the latest snapshot is so the
        // page isn't blank on first load.
        if (live.Snapshot is not null) OnTick(live.Snapshot);
        if (live.IsAttached) OnGameAttached();
        else OnGameDetached();
    }

    void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var live = AppServices.LiveMatch;
        live.Tick     -= OnTick;
        live.Attached -= OnGameAttached;
        live.Detached -= OnGameDetached;
    }

    void OnGameAttached()
    {
        LiveDot.Fill = GetBrush("Good");
        StatusLabel.Text = "ATTACHED";
        StatusLabel.Foreground = GetBrush("Good");
    }

    void OnGameDetached()
    {
        LiveDot.Fill = GetBrush("Fg.Disabled");
        StatusLabel.Text = "WAITING FOR GAME";
        StatusLabel.Foreground = GetBrush("Fg.Secondary");
    }

    void OnTick(MatchSnapshot snap)
    {
        // Header. Map line is mostly meaningless in main-menu screens, so
        // we suppress it there and show a more useful state line instead.
        if (snap.IsMainMenu)
        {
            MapText.Text  = BCATracker.Core.BCAEnums.MainMenuStateName(snap.MainMenuState);
            ModeText.Text = "Main menu";
        }
        else if (snap.IsLobby)
        {
            MapText.Text  = string.IsNullOrEmpty(snap.Lobby?.MapName) ? "Lobby" : snap.Lobby!.MapName;
            ModeText.Text = $"Lobby · {snap.Lobby?.ModeName ?? snap.ModeName}";
        }
        else if (snap.InMatch)
        {
            MapText.Text  = string.IsNullOrEmpty(snap.CurrentMap) ? "Unknown Map" : snap.CurrentMap;
            ModeText.Text = $"{snap.ModeName} · {snap.StateName}";
        }
        else if (snap.IsPostMatch)
        {
            MapText.Text  = string.IsNullOrEmpty(snap.CurrentMap) ? "Match ended" : snap.CurrentMap;
            ModeText.Text = $"{snap.ModeName} · post-match";
        }
        else if (snap.IsWaiting)
        {
            MapText.Text  = "Loading...";
            ModeText.Text = snap.StateName;
        }
        else
        {
            MapText.Text  = string.IsNullOrEmpty(snap.CurrentMap) ? "—" : snap.CurrentMap;
            ModeText.Text = snap.StateName;
        }

        TimerText.Text = snap.InMatch ? snap.Timer : "—";

        // Lives are only meaningful in match
        if (snap.InMatch)
        {
            MyLivesText.Text    = snap.MyLives.ToString();
            EnemyLivesText.Text = snap.EnemyLives.ToString();
        }
        else
        {
            MyLivesText.Text    = "—";
            EnemyLivesText.Text = "—";
        }

        // Post-match banner: visible only when the round just finished and we
        // still have player data on screen.
        UpdatePostMatchBanner(snap);

        // Scoreboard
        BuildScoreboard(snap);

        // Kill feed (newest first)
        BuildKillFeed(snap);
    }

    void UpdatePostMatchBanner(MatchSnapshot snap)
    {
        if (!snap.IsPostMatch || snap.Players.Count == 0)
        {
            PostMatchBanner.IsVisible = false;
            return;
        }

        PlayerInfo? me = snap.Players.FirstOrDefault(p => p.IsLocal);
        if (me is null)
        {
            PostMatchBanner.IsVisible = false;
            return;
        }

        bool? won = me.IsWinner ? true : (snap.Players.Any(p => p.IsWinner) ? false : (bool?)null);

        if (won == true)
        {
            OutcomeText.Text = "VICTORY";
            OutcomeText.Foreground = GetBrush("Good");
        }
        else if (won == false)
        {
            OutcomeText.Text = "DEFEAT";
            OutcomeText.Foreground = GetBrush("Danger");
        }
        else
        {
            OutcomeText.Text = "MATCH ENDED";
            OutcomeText.Foreground = GetBrush("Fg.Primary");
        }

        OutcomeSubText.Text = $"{snap.CurrentMap} · {snap.ModeName}";

        FinalKdaText.Text = $"{me.Kills} / {me.Deaths} / {me.Assists}";
        FinalDamageText.Text = ((int)me.Damage).ToString();

        PostMatchBanner.IsVisible = true;
    }

    void BuildScoreboard(MatchSnapshot snap)
    {
        ScoreboardHost.Children.Clear();

        // Group by team, present in team order.
        var byTeam = snap.Players
            .GroupBy(p => p.Team)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var team in byTeam)
        {
            // Team header
            var teamHeader = new TextBlock
            {
                Text = $"TEAM {team.Key}",
                Margin = new Thickness(0, 8, 0, 6),
            };
            teamHeader.Classes.Add("label");
            // Tint header by team
            teamHeader.Foreground = team.Key == 0 ? GetBrush("Team.A") : GetBrush("Team.B");
            ScoreboardHost.Children.Add(teamHeader);

            foreach (PlayerInfo p in team.OrderByDescending(x => x.Kills))
            {
                ScoreboardHost.Children.Add(BuildPlayerRow(p, team.Key));
            }
        }

        if (ScoreboardHost.Children.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "No players visible. Waiting for game state…",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 24, 0, 24),
            };
            empty.Classes.Add("muted");
            ScoreboardHost.Children.Add(empty);
        }
    }

    Border BuildPlayerRow(PlayerInfo p, int team)
    {
        // Column layout (must match the header strip in LiveMatchPage.axaml):
        //   0: 4px team bar
        //   1: name (*)
        //   2: 1px separator
        //   3: KDA (110)
        //   4: 1px separator
        //   5: damage (80)
        //   6: 1px separator
        //   7: accuracy (80)
        //   8: 1px separator
        //   9: loadout (*)
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("4,*,1,110,1,80,1,80,1,*"),
            Height = 44,
        };

        // Team color bar
        grid.Children.Add(new Border
        {
            Background = team == 0 ? GetBrush("Team.A") : GetBrush("Team.B"),
            CornerRadius = new CornerRadius(2, 0, 0, 2),
            [Grid.ColumnProperty] = 0,
        });

        // Name + badges
        var nameStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
            [Grid.ColumnProperty] = 1,
        };
        nameStack.Children.Add(new TextBlock
        {
            Text = p.Name,
            FontWeight = p.IsLocal ? FontWeight.SemiBold : FontWeight.Medium,
            Foreground = p.IsLocal ? GetBrush("Accent") : GetBrush("Fg.Primary"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (p.IsBot) nameStack.Children.Add(MakeTag($"BOT{p.BotLevel}"));
        if (p.IsHost) nameStack.Children.Add(MakeTag("HOST"));
        grid.Children.Add(nameStack);

        // Separators (columns 2, 4, 6, 8)
        AddSeparator(grid, 2);
        AddSeparator(grid, 4);
        AddSeparator(grid, 6);
        AddSeparator(grid, 8);

        // KDA (col 3)
        var kda = new TextBlock
        {
            Text = $"{p.Kills}/{p.Deaths}/{p.Assists}",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 16, 0),
            FontFamily = GetFontFamily("Font.Display"),
            FontWeight = FontWeight.SemiBold,
            [Grid.ColumnProperty] = 3,
        };
        grid.Children.Add(kda);

        // Damage (col 5)
        var dmg = new TextBlock
        {
            Text = ((int)p.Damage).ToString(),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetBrush("Fg.Secondary"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 16, 0),
            [Grid.ColumnProperty] = 5,
        };
        grid.Children.Add(dmg);

        // Accuracy (col 7)
        var acc = new TextBlock
        {
            Text = p.Accuracy.ToString("0.0", CultureInfo.InvariantCulture) + "%",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetBrush("Fg.Secondary"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 16, 0),
            [Grid.ColumnProperty] = 7,
        };
        grid.Children.Add(acc);

        // Loadout (col 9)
        var loadout = new TextBlock
        {
            Text = JoinNonEmpty(" · ", p.WeaponName, p.AbilityName, p.ModuleName),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = GetBrush("Fg.Muted"),
            Margin = new Thickness(12, 0, 0, 0),
            [Grid.ColumnProperty] = 9,
        };
        grid.Children.Add(loadout);

        return new Border
        {
            Background = p.IsLocal ? GetBrush("Bg.SurfaceHover") : GetBrush("Bg.SurfaceRaised"),
            CornerRadius = new CornerRadius(2),
            Child = grid,
            Margin = new Thickness(0, 0, 0, 4),
        };
    }

    void AddSeparator(Grid g, int col)
    {
        var sep = new Border
        {
            Background = GetBrush("Border.Subtle"),
            Margin = new Thickness(0, 8, 0, 8),
            [Grid.ColumnProperty] = col,
        };
        g.Children.Add(sep);
    }

    Border MakeTag(string text)
    {
        return new Border
        {
            Background = GetBrush("Bg.Surface"),
            BorderBrush = GetBrush("Border.Default"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(5, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = GetBrush("Fg.Muted"),
                FontWeight = FontWeight.SemiBold,
            },
        };
    }

    void BuildKillFeed(MatchSnapshot snap)
    {
        KillFeedHost.Children.Clear();

        // Newest first, capped at 50 entries.
        var entries = snap.KillFeed
            .OrderByDescending(k => k.Time)
            .Take(50)
            .ToList();

        if (entries.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "No kills yet.",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 16),
            };
            empty.Classes.Add("muted");
            KillFeedHost.Children.Add(empty);
            return;
        }

        foreach (var e in entries)
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 0, 0, 4),
            };

            var line = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                [Grid.ColumnProperty] = 0,
            };
            // killer (colored by team) → cause → victim
            var killerRun = new Run { Text = e.KillerName, Foreground = e.KillerTeam == 0 ? GetBrush("Team.A") : GetBrush("Team.B") };
            var causeRun  = new Run { Text = $"  ▸  ", Foreground = GetBrush("Fg.Muted") };
            var victimRun = new Run { Text = e.VictimName, Foreground = GetBrush("Fg.Secondary") };
            line.Inlines!.Add(killerRun);
            line.Inlines.Add(causeRun);
            line.Inlines.Add(victimRun);
            if (!string.IsNullOrEmpty(e.Cause))
            {
                line.Inlines.Add(new Run
                {
                    Text = $"  · {e.Cause}",
                    Foreground = GetBrush("Fg.Muted"),
                    FontSize = 11,
                });
            }
            grid.Children.Add(line);

            var time = new TextBlock
            {
                Text = e.ElapsedSecs > 0
                    ? $"{(int)e.ElapsedSecs / 60}:{(int)e.ElapsedSecs % 60:D2}"
                    : e.Time.ToLocalTime().ToString("HH:mm:ss"),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                Foreground = GetBrush("Fg.Disabled"),
                Margin = new Thickness(8, 0, 0, 0),
                [Grid.ColumnProperty] = 1,
            };
            grid.Children.Add(time);

            KillFeedHost.Children.Add(grid);
        }
    }

    static string JoinNonEmpty(string sep, params string?[] parts)
    {
        var live = new System.Collections.Generic.List<string>();
        foreach (string? p in parts)
            if (!string.IsNullOrWhiteSpace(p)) live.Add(p!);
        return string.Join(sep, live);
    }

    static IBrush GetBrush(string key)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
            && value is IBrush brush)
            return brush;
        return Brushes.Gray;
    }

    static FontFamily GetFontFamily(string key)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
            && value is FontFamily ff)
            return ff;
        return FontFamily.Default;
    }
}
