using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using BCATracker.Core;
using BCATracker.UI.Services;

namespace BCATracker.UI.Views;

/// <summary>
/// Per-loadout aggregate stats. For every weapon+ability+module combo
/// the local player has used, we compute matches played, win rate,
/// K/D, and average damage. Sorted by win rate descending so the
/// "best" combos surface first.
///
/// Sample-size filter: combos with fewer than N matches don't show
/// (so a single lucky 100%-win 1-match combo doesn't dominate). User
/// can dial the threshold via the buttons at top-right.
/// </summary>
public partial class LoadoutsPage : UserControl
{
    int _minSamples = 3;

    public LoadoutsPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Refresh();
    }

    void MinSamples_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string s && int.TryParse(s, out int n))
        {
            _minSamples = n;
            Refresh();
        }
    }

    void Refresh()
    {
        UpdateButtonStyles();

        var all = AppServices.Matches.LoadAll();
        if (all.Count == 0)
        {
            SubtitleText.Text = "";
            EmptyHintText.IsVisible = true;
            LoadoutsHost.ItemsSource = null;
            return;
        }

        string? known = Stats.FindKnownAccountId(all);

        // Group every match by the loadout the local player ran. Key is
        // a tuple of (weapon, ability, module) name strings; matches
        // where any of those are blank get bucketed under "Unknown"
        // labels rather than skipped so we don't lose data silently.
        var grouped = new Dictionary<(string W, string A, string M), Bucket>();
        foreach (var match in all)
        {
            var me = Stats.Local(match, known);
            if (me is null) continue;

            var key = (
                W: string.IsNullOrEmpty(me.Weapon)  ? "Unknown" : me.Weapon,
                A: string.IsNullOrEmpty(me.Ability) ? "Unknown" : me.Ability,
                M: string.IsNullOrEmpty(me.Module)  ? "Unknown" : me.Module
            );

            if (!grouped.TryGetValue(key, out var b))
            {
                b = new Bucket { Weapon = key.W, Ability = key.A, Module = key.M };
                grouped[key] = b;
            }

            b.Matches++;
            b.Kills  += me.Kills;
            b.Deaths += me.Deaths;
            b.Damage += me.Damage;

            bool? won = Stats.DidLocalWin(match, known);
            if (won == true)  { b.Wins++; b.Decided++; }
            else if (won == false) b.Decided++;
        }

        var combos = grouped.Values
            .Where(b => b.Matches >= _minSamples)
            .Select(b =>
            {
                b.KD       = b.Deaths > 0 ? (double)b.Kills / b.Deaths : b.Kills;
                b.AvgDmg   = b.Matches > 0 ? b.Damage / b.Matches : 0;
                b.WinRate  = b.Decided > 0 ? 100.0 * b.Wins / b.Decided : 0;
                return b;
            })
            .OrderByDescending(b => b.WinRate)
            .ThenByDescending(b => b.Matches)
            .ToList();

        if (combos.Count == 0)
        {
            SubtitleText.Text = $"No loadout has reached the {_minSamples}-match threshold yet.";
            EmptyHintText.IsVisible = false;
            LoadoutsHost.ItemsSource = null;
            return;
        }

        SubtitleText.Text = $"{combos.Count} loadout{(combos.Count == 1 ? "" : "s")} with at least {_minSamples} matches.";
        EmptyHintText.IsVisible = false;
        LoadoutsHost.ItemsSource = combos.Select(BuildRow).ToList();
    }

    static Border BuildRow(Bucket b)
    {
        var card = new Border();
        card.Classes.Add("card");
        card.Padding = new Thickness(20, 16);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,Auto,Auto,Auto,Auto"),
        };

        // Left: loadout description.
        var loadoutBlock = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        loadoutBlock.Children.Add(new TextBlock
        {
            Text = $"{b.Weapon} + {b.Ability}",
            Classes = { "section-title" },
        });
        loadoutBlock.Children.Add(new TextBlock
        {
            Text = b.Module,
            Classes = { "muted" },
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(loadoutBlock, 0);
        grid.Children.Add(loadoutBlock);

        // Right: 4 stat cells.
        AddCell(grid, 1, "MATCHES",  b.Matches.ToString());
        AddCell(grid, 2, "W - L",
            $"{b.Wins}-{b.Decided - b.Wins}");
        AddCell(grid, 3, "WIN RATE",
            b.WinRate.ToString("0.0", CultureInfo.InvariantCulture) + "%");
        AddCell(grid, 4, "K/D",
            b.KD.ToString("0.00", CultureInfo.InvariantCulture));

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

    void UpdateButtonStyles()
    {
        // Mirror the "ghost = inactive" convention from Trends and Stats.
        MinSamples1Btn .Classes.Set("ghost", _minSamples != 1);
        MinSamples3Btn .Classes.Set("ghost", _minSamples != 3);
        MinSamples5Btn .Classes.Set("ghost", _minSamples != 5);
        MinSamples10Btn.Classes.Set("ghost", _minSamples != 10);
    }

    sealed class Bucket
    {
        public string Weapon  = "";
        public string Ability = "";
        public string Module  = "";

        public int    Matches;
        public int    Kills;
        public int    Deaths;
        public double Damage;
        public int    Wins;
        public int    Decided;

        // Computed
        public double KD;
        public double AvgDmg;
        public double WinRate;
    }
}
