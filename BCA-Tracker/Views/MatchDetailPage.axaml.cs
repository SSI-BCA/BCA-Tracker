using System;
using System.Collections.Generic;
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

public partial class MatchDetailPage : UserControl
{
    MatchRecord? _match;

    /// <summary>Default constructor for navigation framework. Match is set
    /// via SetMatch() before the page becomes visible.</summary>
    public MatchDetailPage()
    {
        InitializeComponent();
    }

    public MatchDetailPage(MatchRecord match) : this()
    {
        SetMatch(match);
    }

    public void SetMatch(MatchRecord match)
    {
        _match = match;
        Render();
    }

    void Back_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<MainWindow>(this) is MainWindow win)
            win.NavigateTo(typeof(MatchHistoryPage));
    }

    void Render()
    {
        if (_match is null) return;

        string? knownAccountId = AppServices.Matches.LoadAll() is var all && all.Count > 0
            ? Stats.FindKnownAccountId(all)
            : null;
        PlayerRecord? me = Stats.Local(_match, knownAccountId);
        bool? won = Stats.DidLocalWin(_match, knownAccountId);

        // ── Header ───────────────────────────────────────────────────
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
            OutcomeText.Text = "FINISHED";
            OutcomeText.Foreground = GetBrush("Fg.Secondary");
        }

        MapText.Text  = string.IsNullOrEmpty(_match.Map) ? "Unknown Map" : _match.Map;
        ModeText.Text = _match.GameMode ?? "";

        DurationText.Text = FormatDuration(_match.DurationSecs);
        WhenText.Text     = _match.PlayedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm",
                                                                    CultureInfo.InvariantCulture);

        // ── Hero stats ───────────────────────────────────────────────
        if (me is not null)
        {
            MeKdaText.Text = $"{me.Kills}/{me.Deaths}/{me.Assists}";
            double kd = me.Deaths > 0 ? (double)me.Kills / me.Deaths : me.Kills;
            MeKdRatioText.Text = kd.ToString("0.00", CultureInfo.InvariantCulture);
            MeDamageText.Text  = ((int)me.Damage).ToString();
            MeAccText.Text     = me.Accuracy.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            MeHealText.Text    = ((int)me.Heal).ToString();
        }
        else
        {
            MeKdaText.Text = MeKdRatioText.Text = MeDamageText.Text = MeAccText.Text = MeHealText.Text = "—";
        }

        BuildScoreboard();
        BuildKillFeed();
        BuildDetailedStats(me);
        BuildHighlights(knownAccountId);
    }

    void BuildHighlights(string? knownAccountId)
    {
        if (_match is null) return;
        HighlightsHost.Items.Clear();

        var hl = Stats.ComputeHighlights(_match, knownAccountId);

        var pills = new System.Collections.Generic.List<(string label, IBrush color)>();
        if (hl.MatchMvp)   pills.Add(("MATCH MVP",   GetBrush("Accent")));
        if (hl.TeamMvp && !hl.MatchMvp)
                           pills.Add(("TEAM MVP",    GetBrush("Team.A")));
        if (hl.Flawless)   pills.Add(("FLAWLESS",    GetBrush("Good")));
        if (hl.FirstBlood) pills.Add(("FIRST BLOOD", GetBrush("Good")));
        if (hl.FirstDeath) pills.Add(("FIRST DEATH", GetBrush("Danger")));

        // Damage delta — show as a pill with the actual number.
        string deltaSign = hl.DamageDelta >= 0 ? "+" : "";
        IBrush deltaColor = hl.DamageDelta >= 0 ? GetBrush("Good") : GetBrush("Danger");
        pills.Add(($"Δ DMG  {deltaSign}{(int)hl.DamageDelta}", deltaColor));

        // Rank pill
        if (hl.OverallRank > 0)
            pills.Add(($"RANK #{hl.OverallRank} OVERALL", GetBrush("Fg.Secondary")));

        if (pills.Count == 0)
        {
            NoHighlightsText.IsVisible = true;
            return;
        }
        NoHighlightsText.IsVisible = false;

        foreach (var (label, color) in pills)
        {
            var pill = new Border
            {
                Background      = GetBrush("Bg.SurfaceRaised"),
                BorderBrush     = color,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(2),
                Padding         = new Thickness(10, 4),
                Margin          = new Thickness(0, 0, 8, 8),
                Child = new TextBlock
                {
                    Text       = label,
                    FontSize   = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = color,
                },
            };
            HighlightsHost.Items.Add(pill);
        }
    }

    void BuildScoreboard()
    {
        ScoreboardHost.Children.Clear();
        if (_match?.Players is null) return;

        var byTeam = _match.Players
            .GroupBy(p => p.Team)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var team in byTeam)
        {
            var hdr = new TextBlock
            {
                Text = $"TEAM {team.Key}",
                Margin = new Thickness(0, 8, 0, 6),
                Foreground = team.Key == 0 ? GetBrush("Team.A") : GetBrush("Team.B"),
            };
            hdr.Classes.Add("label");
            ScoreboardHost.Children.Add(hdr);

            foreach (PlayerRecord p in team.OrderByDescending(x => x.Kills))
                ScoreboardHost.Children.Add(BuildPlayerRow(p));
        }
    }

    Border BuildPlayerRow(PlayerRecord p)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("4,*,1,110,1,80,1,80,1,*"),
            Height = 44,
        };

        grid.Children.Add(new Border
        {
            Background = p.Team == 0 ? GetBrush("Team.A") : GetBrush("Team.B"),
            CornerRadius = new CornerRadius(2, 0, 0, 2),
            [Grid.ColumnProperty] = 0,
        });

        var nameStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
            [Grid.ColumnProperty] = 1,
        };
        nameStack.Children.Add(new TextBlock
        {
            Text = p.Name ?? "?",
            FontWeight = p.IsLocalPlayer ? FontWeight.SemiBold : FontWeight.Medium,
            Foreground = p.IsLocalPlayer ? GetBrush("Accent") : GetBrush("Fg.Primary"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (p.IsBot) nameStack.Children.Add(MakeTag($"BOT{p.BotLevel}"));
        grid.Children.Add(nameStack);

        // Separators
        for (int col = 2; col <= 8; col += 2)
        {
            grid.Children.Add(new Border
            {
                Background = GetBrush("Border.Subtle"),
                Margin = new Thickness(0, 8, 0, 8),
                [Grid.ColumnProperty] = col,
            });
        }

        grid.Children.Add(new TextBlock
        {
            Text = $"{p.Kills}/{p.Deaths}/{p.Assists}",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 16, 0),
            FontFamily = GetFontFamily("Font.Display"),
            FontWeight = FontWeight.SemiBold,
            [Grid.ColumnProperty] = 3,
        });

        grid.Children.Add(new TextBlock
        {
            Text = ((int)p.Damage).ToString(),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = GetBrush("Fg.Secondary"),
            Margin = new Thickness(0, 0, 16, 0),
            [Grid.ColumnProperty] = 5,
        });

        grid.Children.Add(new TextBlock
        {
            Text = p.Accuracy.ToString("0.0", CultureInfo.InvariantCulture) + "%",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = GetBrush("Fg.Secondary"),
            Margin = new Thickness(0, 0, 16, 0),
            [Grid.ColumnProperty] = 7,
        });

        grid.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", new[] { p.Weapon, p.Ability, p.Module }
                                          .Where(s => !string.IsNullOrEmpty(s))),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = GetBrush("Fg.Muted"),
            Margin = new Thickness(12, 0, 0, 0),
            [Grid.ColumnProperty] = 9,
        });

        return new Border
        {
            Background = p.IsLocalPlayer ? GetBrush("Bg.SurfaceHover") : GetBrush("Bg.SurfaceRaised"),
            CornerRadius = new CornerRadius(2),
            Child = grid,
            Margin = new Thickness(0, 0, 0, 4),
        };
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

    void BuildKillFeed()
    {
        KillFeedHost.Children.Clear();
        if (_match?.KillFeed is null || _match.KillFeed.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "No kills recorded.",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 16),
            };
            empty.Classes.Add("muted");
            KillFeedHost.Children.Add(empty);
            return;
        }

        foreach (var e in _match.KillFeed)
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
            var killerRun = new Run
            {
                Text = e.KillerName ?? "?",
                Foreground = e.KillerTeam == 0 ? GetBrush("Team.A") : GetBrush("Team.B"),
            };
            line.Inlines!.Add(killerRun);
            line.Inlines.Add(new Run { Text = "  ▸  ", Foreground = GetBrush("Fg.Muted") });
            line.Inlines.Add(new Run { Text = e.VictimName ?? "?", Foreground = GetBrush("Fg.Secondary") });
            if (!string.IsNullOrEmpty(e.Cause))
                line.Inlines.Add(new Run
                {
                    Text = $"  · {e.Cause}",
                    Foreground = GetBrush("Fg.Muted"),
                    FontSize = 11,
                });
            grid.Children.Add(line);

            var time = new TextBlock
            {
                Text = e.TimeInMatch ?? "",
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

    void BuildDetailedStats(PlayerRecord? me)
    {
        DetailedStatsGrid.Children.Clear();
        DetailedStatsGrid.RowDefinitions.Clear();
        if (me is null) return;

        // Each stat is a (label, value) pair. We layout 4 columns × N rows.
        var stats = new List<(string Label, string Value)>
        {
            ("KILLS",                 me.Kills.ToString()),
            ("DEATHS",                me.Deaths.ToString()),
            ("ASSISTS",               me.Assists.ToString()),
            ("K/D",                   (me.Deaths > 0 ? (double)me.Kills / me.Deaths : me.Kills).ToString("0.00", CultureInfo.InvariantCulture)),

            ("DAMAGE",                ((int)me.Damage).ToString()),
            ("HEAL",                  ((int)me.Heal).ToString()),
            ("WEAPON ACC.",           me.Accuracy.ToString("0.0", CultureInfo.InvariantCulture) + "%"),
            ("ABILITY ACC.",          me.AbilityAccuracy.ToString("0.0", CultureInfo.InvariantCulture) + "%"),

            ("HITS DEALT",            me.NbHitsCaused.ToString()),
            ("HITS TAKEN",            me.NbHitsReceived.ToString()),
            ("ABILITIES USED",        me.AbilitiesUsed.ToString()),
            ("ABILITIES HIT",         me.NbAbilitiesHit.ToString()),

            ("SHIELD DMG DEALT",      ((int)me.WeaponShieldDmgDealt).ToString()),
            ("SHIELD DMG TAKEN",      ((int)me.ReceivedShieldDmg).ToString()),
            ("SHIELD PICKUPS",        me.ShieldPickups.ToString()),
            ("EMPOWERMENTS",          me.Empowerments.ToString()),

            ("DASHES",                me.Dashes.ToString()),
            ("JUMPS",                 me.Jumps.ToString()),
            ("TIME ALIVE",            FormatDuration(me.TimeAliveSecs)),
            ("GRAVITY USE",           me.GravityUseCount.ToString()),
        };

        const int cols = 4;
        int rows = (stats.Count + cols - 1) / cols;
        for (int r = 0; r < rows; r++)
            DetailedStatsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int i = 0; i < stats.Count; i++)
        {
            int row = i / cols;
            int col = i % cols;
            var sp = new StackPanel { Margin = new Thickness(0, 0, 16, 14) };
            var lbl = new TextBlock { Text = stats[i].Label };
            lbl.Classes.Add("label");
            sp.Children.Add(lbl);
            sp.Children.Add(new TextBlock
            {
                Text = stats[i].Value,
                FontFamily = GetFontFamily("Font.Display"),
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 4, 0, 0),
            });
            Grid.SetRow(sp, row);
            Grid.SetColumn(sp, col);
            DetailedStatsGrid.Children.Add(sp);
        }
    }

    static string FormatDuration(double secs)
    {
        if (secs < 60)      return $"{(int)secs}s";
        if (secs < 3600)    return $"{(int)(secs/60)}m {(int)(secs%60)}s";
        long hours   = (long)(secs / 3600);
        long minutes = (long)((secs % 3600) / 60);
        return $"{hours}h {minutes}m";
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
