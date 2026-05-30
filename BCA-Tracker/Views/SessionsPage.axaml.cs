using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BCATracker.Core;
using BCATracker.UI.Services;

namespace BCATracker.UI.Views;

/// <summary>
/// Group matches into "sessions" - contiguous play periods separated
/// by gaps of at least <see cref="SessionGap"/>. Each session card
/// shows the date range, match count, W/L, K/D, total damage and
/// total playtime.
///
/// The gap threshold is a heuristic: 2 hours is long enough that
/// "lunch break + come back" stays in the same session, but short
/// enough that "played yesterday morning, played today morning"
/// counts as two distinct sessions.
/// </summary>
public partial class SessionsPage : UserControl
{
    static readonly TimeSpan SessionGap = TimeSpan.FromHours(2);

    public SessionsPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Refresh();
    }

    void Refresh()
    {
        var all = AppServices.Matches.LoadAll();
        if (all.Count == 0)
        {
            SubtitleText.Text = "";
            EmptyHintText.IsVisible = true;
            SessionsHost.ItemsSource = null;
            return;
        }

        string? knownAccountId = Stats.FindKnownAccountId(all);
        var sessions = BuildSessions(all, knownAccountId);

        SubtitleText.Text = sessions.Count == 1
            ? "1 session across all your matches."
            : $"{sessions.Count} sessions across all your matches.";
        EmptyHintText.IsVisible = false;
        SessionsHost.ItemsSource = sessions.Select(BuildCard).ToList();
    }

    /// <summary>
    /// Walk matches chronologically and start a new session whenever
    /// the gap from the previous match exceeds SessionGap. Output is
    /// returned newest-session-first to match the way the user
    /// expects "most recent at the top".
    /// </summary>
    static List<Session> BuildSessions(List<MatchRecord> matches, string? knownAccountId)
    {
        // Sort by time so the gap detection works regardless of how
        // the storage layer happens to return them.
        var sorted = matches.OrderBy(m => m.PlayedAt).ToList();

        var sessions = new List<Session>();
        Session? cur = null;
        foreach (var m in sorted)
        {
            if (cur is null || (m.PlayedAt - cur.End) > SessionGap)
            {
                cur = new Session { Start = m.PlayedAt, End = m.PlayedAt };
                sessions.Add(cur);
            }
            else
            {
                cur.End = m.PlayedAt;
            }
            cur.Matches.Add(m);
        }

        // Compute aggregates per session, viewed from the local player.
        foreach (var s in sessions)
        {
            int kills = 0, deaths = 0, wins = 0, decided = 0;
            double dmg = 0;
            foreach (var m in s.Matches)
            {
                var me = Stats.Local(m, knownAccountId);
                if (me is null) continue;
                kills  += me.Kills;
                deaths += me.Deaths;
                dmg    += me.Damage;

                bool? won = Stats.DidLocalWin(m, knownAccountId);
                if (won == true) { wins++; decided++; }
                else if (won == false) decided++;
            }
            s.Kills  = kills;
            s.Deaths = deaths;
            s.Wins   = wins;
            s.Losses = decided - wins;
            s.TotalDamage = dmg;
            s.KD = deaths > 0 ? (double)kills / deaths : kills;
        }

        // Newest first.
        sessions.Reverse();
        return sessions;
    }

    /// <summary>
    /// Build a card Border for a session. We compose this in code
    /// instead of XAML data-templating because the existing card
    /// styles in the codebase are class-based on Border, which is
    /// easier to wire by hand than by DataTemplate.
    /// </summary>
    static Border BuildCard(Session s)
    {
        var card = new Border();
        card.Classes.Add("card");
        card.Padding = new Thickness(20);

        var rootGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };

        // Row 0: title block spanning all 5 columns.
        var titleBlock = new StackPanel();
        titleBlock.Children.Add(new TextBlock
        {
            Text = FormatHeader(s),
            Classes = { "section-title" },
        });
        var subtitle = new TextBlock
        {
            Text = FormatSubtitle(s),
            Classes = { "muted" },
            Margin = new Thickness(0, 4, 0, 16),
        };
        titleBlock.Children.Add(subtitle);
        Grid.SetColumn(titleBlock, 0);
        Grid.SetColumnSpan(titleBlock, 5);
        Grid.SetRow(titleBlock, 0);
        rootGrid.Children.Add(titleBlock);

        // Row 1: five stat cells.
        AddStatCell(rootGrid, 0, "MATCHES",  s.Matches.Count.ToString());
        AddStatCell(rootGrid, 1, "W - L",
            $"{s.Wins}-{s.Losses}");
        AddStatCell(rootGrid, 2, "K/D",
            s.KD.ToString("0.00", CultureInfo.InvariantCulture));
        AddStatCell(rootGrid, 3, "DAMAGE",
            ((int)s.TotalDamage).ToString("N0", CultureInfo.InvariantCulture));
        AddStatCell(rootGrid, 4, "DURATION",
            FormatDuration(s.End - s.Start));

        card.Child = rootGrid;
        return card;
    }

    static void AddStatCell(Grid host, int column, string label, string value)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Classes = { "label" },
        });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            Classes = { "strip-value" },
            Margin = new Thickness(0, 4, 0, 0),
        });
        Grid.SetColumn(panel, column);
        Grid.SetRow(panel, 1);
        host.Children.Add(panel);
    }

    static string FormatHeader(Session s)
    {
        DateTime start = s.Start.ToLocalTime();
        DateTime end   = s.End.ToLocalTime();
        if (start.Date == end.Date)
        {
            return start.ToString("dddd, dd MMM yyyy", CultureInfo.CurrentCulture);
        }
        // Multi-day session (rare but possible if SessionGap is exceeded
        // by a long break that doesn't quite hit our threshold).
        return $"{start:dd MMM} - {end:dd MMM yyyy}";
    }

    static string FormatSubtitle(Session s)
    {
        DateTime start = s.Start.ToLocalTime();
        DateTime end   = s.End.ToLocalTime();
        return $"{start:HH:mm} - {end:HH:mm}";
    }

    static string FormatDuration(TimeSpan d)
    {
        if (d.TotalHours >= 1)
            return $"{(int)d.TotalHours}h {d.Minutes}m";
        return $"{d.Minutes}m";
    }

    sealed class Session
    {
        public DateTime Start;
        public DateTime End;
        public List<MatchRecord> Matches = new();
        public int Kills;
        public int Deaths;
        public int Wins;
        public int Losses;
        public double TotalDamage;
        public double KD;
    }
}
