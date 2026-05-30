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
/// Head-to-head kill tally between the local player and every other
/// player they've shared a match with. We walk every saved match's
/// KillFeed and count:
///   - kills the local player got on each opponent
///   - kills each opponent got on the local player
///
/// Bots are folded into the same view since killing/being-killed by
/// the same bot repeatedly is still a relationship worth showing,
/// but they're labelled so it's clear they're not real people.
/// </summary>
public partial class RivalsPage : UserControl
{
    public RivalsPage()
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
            RivalsHost.ItemsSource = null;
            return;
        }

        string? known = Stats.FindKnownAccountId(all);

        // Build a name -> Rival aggregate by walking every kill feed
        // entry across all matches. We also need to know the local
        // player's display name per-match because PlayerRecord.Name
        // might vary across matches if the user renames in BCA.
        var rivals = new Dictionary<string, Rival>(StringComparer.OrdinalIgnoreCase);

        // Track which other players are bots so we can label them.
        // A name is "bot" if any PlayerRecord with that name has
        // IsBot=true. (If a real player has the same handle as a bot
        // they'd get mis-labelled - the bot names BCA generates are
        // distinctive enough that this is acceptable in practice.)
        var botNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in all)
        {
            var me = Stats.Local(match, known);
            if (me is null || string.IsNullOrEmpty(me.Name)) continue;

            // Note bot names from this match's player roster.
            foreach (var p in match.Players)
            {
                if (p.IsBot && !string.IsNullOrEmpty(p.Name))
                    botNames.Add(p.Name);
            }

            // Walk the kill feed.
            foreach (var k in match.KillFeed)
            {
                if (string.Equals(k.KillerName, me.Name, StringComparison.OrdinalIgnoreCase))
                {
                    // We killed someone. Skip self-kills (suicide) and
                    // environment kills (KillerName same as VictimName,
                    // or KillerName empty).
                    if (string.IsNullOrEmpty(k.VictimName)) continue;
                    if (string.Equals(k.VictimName, me.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    GetOrCreate(rivals, k.VictimName).Killed++;
                }
                else if (string.Equals(k.VictimName, me.Name, StringComparison.OrdinalIgnoreCase))
                {
                    // Someone killed us. Skip environment kills (no
                    // killer name, or killer = victim).
                    if (string.IsNullOrEmpty(k.KillerName)) continue;
                    if (string.Equals(k.KillerName, me.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    GetOrCreate(rivals, k.KillerName).KilledBy++;
                }
            }
        }

        // Tag bot rivals and compute totals.
        foreach (var r in rivals.Values)
        {
            r.IsBot = botNames.Contains(r.Name);
            r.Total = r.Killed + r.KilledBy;
            // K/D against this player. Avoid div-by-zero by treating
            // 0 deaths as 1 for the ratio (mirrors how the rest of
            // the app handles K/D).
            r.Ratio = r.KilledBy > 0 ? (double)r.Killed / r.KilledBy : r.Killed;
        }

        var sorted = rivals.Values
            .Where(r => r.Total > 0)
            .OrderByDescending(r => r.Total)
            .ThenByDescending(r => r.Killed)
            .ToList();

        if (sorted.Count == 0)
        {
            SubtitleText.Text = "No rival data yet.";
            EmptyHintText.IsVisible = true;
            RivalsHost.ItemsSource = null;
            return;
        }

        SubtitleText.Text = $"{sorted.Count} opponent{(sorted.Count == 1 ? "" : "s")} you've traded kills with.";
        EmptyHintText.IsVisible = false;
        RivalsHost.ItemsSource = sorted.Select(BuildRow).ToList();
    }

    static Rival GetOrCreate(Dictionary<string, Rival> dict, string name)
    {
        if (!dict.TryGetValue(name, out var r))
        {
            r = new Rival { Name = name };
            dict[name] = r;
        }
        return r;
    }

    static Border BuildRow(Rival r)
    {
        var card = new Border();
        card.Classes.Add("card");
        card.Padding = new Thickness(20, 16);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,Auto,Auto,Auto"),
        };

        // Left: name + bot tag.
        var nameBlock = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        nameBlock.Children.Add(new TextBlock
        {
            Text = r.Name,
            Classes = { "section-title" },
        });
        if (r.IsBot)
        {
            nameBlock.Children.Add(new TextBlock
            {
                Text = "Bot",
                Classes = { "muted" },
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
        Grid.SetColumn(nameBlock, 0);
        grid.Children.Add(nameBlock);

        // Right: 3 stat cells. "YOU KILLED" / "KILLED YOU" / "RATIO".
        AddCell(grid, 1, "YOU KILLED", r.Killed.ToString());
        AddCell(grid, 2, "KILLED YOU", r.KilledBy.ToString());
        AddCell(grid, 3, "RATIO",
            r.Ratio.ToString("0.00", CultureInfo.InvariantCulture));

        card.Child = grid;
        return card;
    }

    static void AddCell(Grid host, int col, string label, string value)
    {
        var sp = new StackPanel
        {
            Margin = new Thickness(24, 0, 0, 0),
            MinWidth = 80,
        };
        sp.Children.Add(new TextBlock { Text = label, Classes = { "label" } });
        sp.Children.Add(new TextBlock
        {
            Text = value,
            Classes = { "strip-value" },
            Margin = new Thickness(0, 4, 0, 0),
        });
        Grid.SetColumn(sp, col);
        host.Children.Add(sp);
    }

    sealed class Rival
    {
        public string Name = "";
        public bool   IsBot;
        public int    Killed;     // we killed them this many times
        public int    KilledBy;   // they killed us this many times
        public int    Total;      // computed
        public double Ratio;      // killed / killedBy
    }
}
