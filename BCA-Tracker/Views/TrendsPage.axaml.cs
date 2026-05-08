using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using BCATracker.Core;
using BCATracker.UI.Services;

namespace BCATracker.UI.Views;

public partial class TrendsPage : UserControl
{
    /// <summary>0 = all, otherwise count of trailing matches.</summary>
    int _rangeLimit = 25;

    public TrendsPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Refresh();
    }

    void Range_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string s && int.TryParse(s, out int n))
        {
            _rangeLimit = n;
            Refresh();
        }
    }

    void Refresh()
    {
        var matches = AppServices.Matches.LoadAll();
        string? knownAccountId = Stats.FindKnownAccountId(matches);

        // Oldest → newest within the selected window. Take the most recent
        // _rangeLimit matches (or all if 0), then re-sort chronologically.
        var ordered = matches.OrderBy(m => m.PlayedAt).ToList();
        var window  = _rangeLimit > 0 && ordered.Count > _rangeLimit
            ? ordered.Skip(ordered.Count - _rangeLimit).ToList()
            : ordered;

        var rows = window
            .Select(m => new
            {
                Match = m,
                Me = Stats.Local(m, knownAccountId),
            })
            .Where(x => x.Me is not null)
            .ToList();

        // Highlight the active range button
        Range10Btn.Classes.Set("ghost",  _rangeLimit != 10);
        Range25Btn.Classes.Set("ghost",  _rangeLimit != 25);
        Range50Btn.Classes.Set("ghost",  _rangeLimit != 50);
        RangeAllBtn.Classes.Set("ghost", _rangeLimit != 0);

        if (rows.Count == 0)
        {
            SubtitleText.Text = "No matches saved yet.";
            WinPctTotal.Text = AvgKdTotal.Text = AvgDmgTotal.Text = MatchCountTotal.Text = "—";
            ClearAllCharts();
            return;
        }

        SubtitleText.Text = _rangeLimit == 0
            ? $"All {rows.Count} match" + (rows.Count == 1 ? "" : "es") + " — oldest on the left."
            : $"Last {rows.Count} match" + (rows.Count == 1 ? "" : "es") + " — oldest on the left.";

        // Per-match series
        double[] kd = rows.Select(r =>
            r.Me!.Deaths > 0 ? (double)r.Me.Kills / r.Me.Deaths : (double)r.Me.Kills
        ).ToArray();
        double[] acc       = rows.Select(r => (double)r.Me!.Accuracy).ToArray();
        double[] dmg       = rows.Select(r => r.Me!.Damage).ToArray();
        double[] delta     = rows.Select(r => r.Me!.Damage - r.Me.ReceivedShieldDmg).ToArray();
        double[] timeAlive = rows.Select(r => r.Me!.TimeAliveSecs).ToArray();

        // Rolling 10-match win rate.
        double[] winRate = new double[rows.Count];
        const int Window = 10;
        for (int i = 0; i < rows.Count; i++)
        {
            int start = System.Math.Max(0, i - Window + 1);
            int wins = 0, total = 0;
            for (int j = start; j <= i; j++)
            {
                bool? w = Stats.DidLocalWin(rows[j].Match, knownAccountId);
                if (w == true) { wins++; total++; }
                else if (w == false) total++;
            }
            winRate[i] = total > 0 ? 100.0 * wins / total : 0;
        }

        // Summary stats over the window
        int totalWins = 0, totalDecided = 0;
        foreach (var r in rows)
        {
            bool? w = Stats.DidLocalWin(r.Match, knownAccountId);
            if (w == true)  { totalWins++; totalDecided++; }
            else if (w == false) totalDecided++;
        }
        double winPct = totalDecided > 0 ? 100.0 * totalWins / totalDecided : 0;
        double avgKd  = kd.Length > 0 ? kd.Average() : 0;
        double avgDmg = dmg.Length > 0 ? dmg.Average() : 0;

        WinPctTotal.Text     = winPct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        AvgKdTotal.Text      = avgKd .ToString("0.00", CultureInfo.InvariantCulture);
        AvgDmgTotal.Text     = ((int)avgDmg).ToString();
        MatchCountTotal.Text = rows.Count.ToString();

        IBrush accent = LookupBrush("Accent",   Brushes.MediumPurple);
        IBrush good   = LookupBrush("Good",     Brushes.MediumSeaGreen);
        IBrush danger = LookupBrush("Danger",   Brushes.IndianRed);
        IBrush teamA  = LookupBrush("Team.A",   Brushes.SteelBlue);
        IBrush muted  = LookupBrush("Fg.Muted", Brushes.Gray);

        IBrush accentFill = TranslucentVariant(accent);
        IBrush goodFill   = TranslucentVariant(good);
        IBrush dangerFill = TranslucentVariant(danger);
        IBrush teamAFill  = TranslucentVariant(teamA);
        IBrush mutedFill  = TranslucentVariant(muted);

        KdChart.SetData       (kd,        "0.00",          lineColor: accent, fillColor: accentFill);
        AccChart.SetData      (acc,       "0",  suffix: "%", lineColor: good,   fillColor: goodFill);
        DmgChart.SetData      (dmg,       "#,0",           lineColor: danger, fillColor: dangerFill);
        WinRateChart.SetData  (winRate,   "0",  suffix: "%", lineColor: good,   fillColor: goodFill);
        DeltaChart.SetData    (delta,     "#,0",           lineColor: teamA,  fillColor: teamAFill);
        TimeAliveChart.SetData(timeAlive, "0",  suffix: "s", lineColor: muted,  fillColor: mutedFill);
    }

    void ClearAllCharts()
    {
        var empty = System.Array.Empty<double>();
        KdChart.SetData(empty);
        AccChart.SetData(empty);
        DmgChart.SetData(empty);
        WinRateChart.SetData(empty);
        DeltaChart.SetData(empty);
        TimeAliveChart.SetData(empty);
    }

    static IBrush LookupBrush(string key, IBrush fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
            && value is IBrush b)
            return b;
        return fallback;
    }

    static IBrush TranslucentVariant(IBrush b)
    {
        if (b is ISolidColorBrush sc)
        {
            var c = sc.Color;
            return new SolidColorBrush(Color.FromArgb(0x44, c.R, c.G, c.B));
        }
        return new SolidColorBrush(Color.FromArgb(0x44, 0x80, 0x80, 0x80));
    }
}
