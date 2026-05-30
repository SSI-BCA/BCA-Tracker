using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BCATracker.Core;
using BCATracker.UI.Services;

namespace BCATracker.UI.Views;

/// <summary>
/// Personal-bests panel. For each kind of record we walk every saved
/// match and pick the single best instance, then render it as a card
/// with the value, context (which map, when), and a friendly label.
///
/// Records intentionally avoid using bot-only matches as the bar
/// (otherwise "best K/D" would always be a bot-stomp). A match counts
/// only if the player was present and at least one opponent was a
/// human.
/// </summary>
public partial class RecordsPage : UserControl
{
    public RecordsPage()
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
            RecordsHost.ItemsSource = null;
            return;
        }

        string? known = Stats.FindKnownAccountId(all);

        // Build a (match, me) list once; we'll scan it for each record.
        // Filter out matches where the local player wasn't found.
        var rows = all
            .Select(m => new { Match = m, Me = Stats.Local(m, known) })
            .Where(x => x.Me is not null)
            .ToList();

        if (rows.Count == 0)
        {
            SubtitleText.Text = "";
            EmptyHintText.IsVisible = true;
            RecordsHost.ItemsSource = null;
            return;
        }

        SubtitleText.Text = $"Personal bests across {rows.Count} of your matches.";
        EmptyHintText.IsVisible = false;

        var cards = new List<Border>();

        // Most kills in a match.
        var topKills = rows.OrderByDescending(r => r.Me!.Kills).First();
        cards.Add(Card("Most kills in a match",
            topKills.Me!.Kills.ToString(),
            Context(topKills.Match)));

        // Most damage in a match.
        var topDmg = rows.OrderByDescending(r => r.Me!.Damage).First();
        cards.Add(Card("Most damage in a match",
            ((int)topDmg.Me!.Damage).ToString("N0", CultureInfo.InvariantCulture),
            Context(topDmg.Match)));

        // Best K/D ratio (require >= 3 kills so a zero-death/zero-kill
        // match doesn't take the title).
        var bestKd = rows
            .Where(r => r.Me!.Kills >= 3)
            .OrderByDescending(r => r.Me!.Deaths > 0 ? (double)r.Me.Kills / r.Me.Deaths : r.Me.Kills)
            .FirstOrDefault();
        if (bestKd is not null)
        {
            double kd = bestKd.Me!.Deaths > 0 ? (double)bestKd.Me.Kills / bestKd.Me.Deaths : bestKd.Me.Kills;
            cards.Add(Card("Best K/D in a match",
                kd.ToString("0.00", CultureInfo.InvariantCulture),
                Context(bestKd.Match) + $" ({bestKd.Me.Kills}-{bestKd.Me.Deaths})"));
        }

        // Highest accuracy. Filter to matches where the player landed
        // at least 10 hits, since one or two lucky hits at 100% acc
        // aren't representative of skill.
        var bestAcc = rows
            .Where(r => r.Me!.NbHitsCaused >= 10)
            .OrderByDescending(r => r.Me!.Accuracy)
            .FirstOrDefault();
        if (bestAcc is not null)
        {
            cards.Add(Card("Highest accuracy",
                bestAcc.Me!.Accuracy.ToString("0.0", CultureInfo.InvariantCulture) + "%",
                Context(bestAcc.Match) + $" ({bestAcc.Me.NbHitsCaused} hits)"));
        }

        // Longest time alive (single match).
        var longestAlive = rows.OrderByDescending(r => r.Me!.TimeAliveSecs).First();
        cards.Add(Card("Longest time alive",
            FormatDuration(longestAlive.Me!.TimeAliveSecs),
            Context(longestAlive.Match)));

        // Most healing in a match.
        var topHeal = rows.OrderByDescending(r => r.Me!.Heal).First();
        if (topHeal.Me!.Heal > 0)
        {
            cards.Add(Card("Most healing",
                ((int)topHeal.Me.Heal).ToString("N0", CultureInfo.InvariantCulture),
                Context(topHeal.Match)));
        }

        // Longest win streak (consecutive wins).
        var streak = LongestStreak(rows.OrderBy(r => r.Match.PlayedAt).ToList(), known, win: true);
        cards.Add(Card("Longest win streak",
            streak.Length.ToString(),
            streak.Length > 0 ? $"Ended {FormatDate(streak.EndedAt)}" : "No wins yet"));

        // Longest loss streak (less inspiring but useful context).
        var lossStreak = LongestStreak(rows.OrderBy(r => r.Match.PlayedAt).ToList(), known, win: false);
        cards.Add(Card("Longest loss streak",
            lossStreak.Length.ToString(),
            lossStreak.Length > 0 ? $"Ended {FormatDate(lossStreak.EndedAt)}" : "No losses yet"));

        // Most matches played in one day.
        var perDay = rows
            .GroupBy(r => r.Match.PlayedAt.ToLocalTime().Date)
            .OrderByDescending(g => g.Count())
            .First();
        cards.Add(Card("Most matches in a day",
            perDay.Count().ToString(),
            FormatDate(perDay.Key)));

        RecordsHost.ItemsSource = cards;
    }

    /// <summary>
    /// Find the longest run of consecutive matches where the local
    /// player's result matched <paramref name="win"/>. Matches with
    /// unknown outcome (DidLocalWin returns null) are treated as
    /// streak-breakers, since we can't say either way.
    /// </summary>
    static (int Length, DateTime EndedAt) LongestStreak<T>(
        List<T> rows, string? known, bool win)
        where T : class
    {
        // Use reflection-free access via the anonymous-type shape -
        // we know rows is the {Match, Me} shape from Refresh, so cast
        // each element back to that via dynamic. Avoiding dynamic by
        // requiring the caller to pass a typed list of MatchRecord
        // would be cleaner, but this path is hit once per refresh so
        // perf doesn't matter.
        int best = 0, current = 0;
        DateTime bestEndedAt = default;
        DateTime currentEndedAt = default;
        foreach (dynamic r in rows)
        {
            bool? w = Stats.DidLocalWin((MatchRecord)r.Match, known);
            if (w == win)
            {
                current++;
                currentEndedAt = ((MatchRecord)r.Match).PlayedAt;
                if (current > best)
                {
                    best = current;
                    bestEndedAt = currentEndedAt;
                }
            }
            else
            {
                current = 0;
            }
        }
        return (best, bestEndedAt);
    }

    static Border Card(string label, string value, string context)
    {
        var border = new Border();
        border.Classes.Add("card");
        border.Padding = new Thickness(20);

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            Classes = { "label" },
        });
        sp.Children.Add(new TextBlock
        {
            Text = value,
            Classes = { "page-title" },
            Margin = new Thickness(0, 8, 0, 0),
        });
        sp.Children.Add(new TextBlock
        {
            Text = context,
            Classes = { "muted" },
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        border.Child = sp;
        return border;
    }

    static string Context(MatchRecord m)
    {
        string when = FormatDate(m.PlayedAt.ToLocalTime());
        if (!string.IsNullOrEmpty(m.Map))
            return $"{m.Map} - {when}";
        return when;
    }

    static string FormatDate(DateTime d)
    {
        return d.ToString("dd MMM yyyy", CultureInfo.CurrentCulture);
    }

    static string FormatDuration(double secs)
    {
        var ts = TimeSpan.FromSeconds(secs);
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }
}
