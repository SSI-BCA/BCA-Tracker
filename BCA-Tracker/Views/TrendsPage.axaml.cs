using System;
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

    /// <summary>Which metric is shown in the big featured chart at the top.</summary>
    string _featuredMetric = "kd";

    // Cached series for the current window — recomputed in Refresh and reused
    // when only the featured-metric selection changes.
    double[] _kd = Array.Empty<double>();
    double[] _acc = Array.Empty<double>();
    double[] _dmg = Array.Empty<double>();
    double[] _winRate = Array.Empty<double>();
    double[] _delta = Array.Empty<double>();
    double[] _alive = Array.Empty<double>();

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

    void Metric_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
        {
            _featuredMetric = tag;
            UpdateFeaturedChart();
            UpdateMetricButtonStyles();
        }
    }

    void Refresh()
    {
        var matches = AppServices.Matches.LoadAll();
        string? knownAccountId = Stats.FindKnownAccountId(matches);

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
            WinPctTotal.Text = AvgKdTotal.Text = AvgDmgTotal.Text = MatchCountTotal.Text = AvgAccTotal.Text = "—";
            _kd = _acc = _dmg = _winRate = _delta = _alive = Array.Empty<double>();
            UpdateFeaturedChart();
            UpdateMiniCharts();
            UpdateMetricButtonStyles();
            return;
        }

        SubtitleText.Text = _rangeLimit == 0
            ? $"All {rows.Count} match" + (rows.Count == 1 ? "" : "es") + " — oldest on the left."
            : $"Last {rows.Count} match" + (rows.Count == 1 ? "" : "es") + " — oldest on the left.";

        // Per-match series
        _kd = rows.Select(r =>
            r.Me!.Deaths > 0 ? (double)r.Me.Kills / r.Me.Deaths : (double)r.Me.Kills
        ).ToArray();
        _acc   = rows.Select(r => (double)r.Me!.Accuracy).ToArray();
        _dmg   = rows.Select(r => r.Me!.Damage).ToArray();
        _delta = rows.Select(r => r.Me!.Damage - r.Me.ReceivedShieldDmg).ToArray();
        _alive = rows.Select(r => r.Me!.TimeAliveSecs).ToArray();

        // Rolling 10-match win rate.
        _winRate = new double[rows.Count];
        const int RollingWindow = 10;
        for (int i = 0; i < rows.Count; i++)
        {
            int start = Math.Max(0, i - RollingWindow + 1);
            int wins = 0, total = 0;
            for (int j = start; j <= i; j++)
            {
                bool? w = Stats.DidLocalWin(rows[j].Match, knownAccountId);
                if (w == true) { wins++; total++; }
                else if (w == false) total++;
            }
            _winRate[i] = total > 0 ? 100.0 * wins / total : 0;
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
        double avgKd  = _kd.Length  > 0 ? _kd.Average()  : 0;
        double avgDmg = _dmg.Length > 0 ? _dmg.Average() : 0;
        double avgAcc = _acc.Length > 0 ? _acc.Average() : 0;

        MatchCountTotal.Text = rows.Count.ToString();
        WinPctTotal.Text     = winPct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        AvgKdTotal.Text      = avgKd .ToString("0.00", CultureInfo.InvariantCulture);
        AvgDmgTotal.Text     = ((int)avgDmg).ToString();
        AvgAccTotal.Text     = avgAcc.ToString("0.0", CultureInfo.InvariantCulture) + "%";

        UpdateFeaturedChart();
        UpdateMiniCharts();
        UpdateMetricButtonStyles();
    }

    /// <summary>
    /// Repaint the big featured chart based on which metric is selected.
    /// Called both on Refresh (after data update) and on Metric_Click
    /// (when only the selection changes).
    /// </summary>
    void UpdateFeaturedChart()
    {
        IBrush accent = LookupBrush("Accent",   Brushes.MediumPurple);
        IBrush good   = LookupBrush("Good",     Brushes.MediumSeaGreen);
        IBrush danger = LookupBrush("Danger",   Brushes.IndianRed);
        IBrush teamA  = LookupBrush("Team.A",   Brushes.SteelBlue);
        IBrush muted  = LookupBrush("Fg.Muted", Brushes.Gray);

        switch (_featuredMetric)
        {
            case "kd":
                FeaturedChartTitle.Text = "K/D RATIO PER MATCH";
                FeaturedChart.HigherIsBetter = true;
                FeaturedChart.SetData(_kd, "0.00", lineColor: accent, fillColor: Translucent(accent));
                break;
            case "acc":
                FeaturedChartTitle.Text = "WEAPON ACCURACY (%)";
                FeaturedChart.HigherIsBetter = true;
                FeaturedChart.SetData(_acc, "0", suffix: "%", lineColor: good, fillColor: Translucent(good));
                break;
            case "dmg":
                FeaturedChartTitle.Text = "DAMAGE PER MATCH";
                FeaturedChart.HigherIsBetter = true;
                FeaturedChart.SetData(_dmg, "#,0", lineColor: danger, fillColor: Translucent(danger));
                break;
            case "winrate":
                FeaturedChartTitle.Text = "WIN RATE — 10-MATCH ROLLING (%)";
                FeaturedChart.HigherIsBetter = true;
                FeaturedChart.SetData(_winRate, "0", suffix: "%", lineColor: good, fillColor: Translucent(good));
                break;
            case "delta":
                FeaturedChartTitle.Text = "DAMAGE DELTA (DEALT − TAKEN)";
                FeaturedChart.HigherIsBetter = true;
                FeaturedChart.SetData(_delta, "#,0", lineColor: teamA, fillColor: Translucent(teamA));
                break;
            case "alive":
                FeaturedChartTitle.Text = "TIME ALIVE PER MATCH (SECONDS)";
                FeaturedChart.HigherIsBetter = true;  // longer = better in this game
                FeaturedChart.SetData(_alive, "0", suffix: "s", lineColor: muted, fillColor: Translucent(muted));
                break;
        }
    }

    /// <summary>
    /// Repaint the three small overview charts. These always show the same
    /// metrics regardless of which is in the featured slot — the idea is the
    /// user can scan the small ones and click into whichever looks
    /// interesting (future enhancement: make them clickable to swap).
    /// </summary>
    void UpdateMiniCharts()
    {
        IBrush accent = LookupBrush("Accent",   Brushes.MediumPurple);
        IBrush good   = LookupBrush("Good",     Brushes.MediumSeaGreen);
        IBrush danger = LookupBrush("Danger",   Brushes.IndianRed);

        MiniAccChart.HigherIsBetter = true;
        MiniAccChart.SetData(_acc, "0", suffix: "%", lineColor: good, fillColor: Translucent(good));

        MiniDmgChart.HigherIsBetter = true;
        MiniDmgChart.SetData(_dmg, "#,0", lineColor: danger, fillColor: Translucent(danger));

        MiniWinRateChart.HigherIsBetter = true;
        MiniWinRateChart.SetData(_winRate, "0", suffix: "%", lineColor: accent, fillColor: Translucent(accent));
    }

    void UpdateMetricButtonStyles()
    {
        // The active metric pill loses its "ghost" class so the styling
        // file can render it filled. All others get "ghost".
        MetricKdBtn.Classes.Set("ghost",      _featuredMetric != "kd");
        MetricAccBtn.Classes.Set("ghost",     _featuredMetric != "acc");
        MetricDmgBtn.Classes.Set("ghost",     _featuredMetric != "dmg");
        MetricWinRateBtn.Classes.Set("ghost", _featuredMetric != "winrate");
        MetricDeltaBtn.Classes.Set("ghost",   _featuredMetric != "delta");
        MetricAliveBtn.Classes.Set("ghost",   _featuredMetric != "alive");
    }

    static IBrush LookupBrush(string key, IBrush fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
            && value is IBrush b)
            return b;
        return fallback;
    }

    static IBrush Translucent(IBrush b)
    {
        if (b is ISolidColorBrush sc)
        {
            var c = sc.Color;
            return new SolidColorBrush(Color.FromArgb(0x44, c.R, c.G, c.B));
        }
        return new SolidColorBrush(Color.FromArgb(0x44, 0x80, 0x80, 0x80));
    }
}
