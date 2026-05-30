using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using BCATracker.Core;
using BCATracker.UI.Controls;
using BCATracker.UI.Services;

namespace BCATracker.UI.Views;

public enum BucketDetailKind
{
    Map,
    Weapon,
}

public partial class BucketDetailPage : UserControl
{
    readonly BucketDetailKind _kind;
    readonly string _bucketKey;

    /// <summary>Default ctor for the XAML previewer.</summary>
    public BucketDetailPage() : this(BucketDetailKind.Map, "") { }

    public BucketDetailPage(BucketDetailKind kind, string bucketKey)
    {
        _kind      = kind;
        _bucketKey = bucketKey ?? "";
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Refresh();
    }

    void Refresh()
    {
        var allMatches = AppServices.Matches.LoadAll();
        string? knownAccountId = Stats.FindKnownAccountId(allMatches);

        // Filter to matches that belong to this bucket. Map filter compares
        // m.Map; Weapon filter compares the local player's chosen weapon.
        var filtered = allMatches
            .Where(m =>
            {
                PlayerRecord? me = Stats.Local(m, knownAccountId);
                if (me is null) return false;
                string? key = _kind == BucketDetailKind.Map ? m.Map : me.Weapon;
                return string.Equals(key ?? "", _bucketKey, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        KindText.Text  = _kind == BucketDetailKind.Map ? "MAP" : "WEAPON";
        TitleText.Text = string.IsNullOrEmpty(_bucketKey) ? "(unknown)" : _bucketKey;

        if (filtered.Count == 0)
        {
            ApplyEmptyState();
            return;
        }

        // Lifetime-style summary, but computed only over the filtered set.
        StatsSummary s = Stats.ComputeLifetime(filtered, knownAccountId);

        // Hero
        HeroKdText.Text         = s.KDRatio.ToString("0.00", CultureInfo.InvariantCulture);
        HeroKdSubtext.Text      = "K/D/A " + s.KDARatio.ToString("0.00", CultureInfo.InvariantCulture);

        HeroWinPctText.Text     = s.WinRatePct.ToString("0", CultureInfo.InvariantCulture) + "%";
        HeroWinPctText.Classes.Remove("good");
        HeroWinPctText.Classes.Remove("danger");
        if      (s.WinRatePct >= 60) HeroWinPctText.Classes.Add("good");
        else if (s.WinRatePct < 50)  HeroWinPctText.Classes.Add("danger");
        HeroWinPctSubtext.Text  = $"{s.Wins}W · {s.Losses}L";

        HeroMatchesText.Text    = s.Matches.ToString();
        HeroPlaytimeText.Text   = FormatDuration(s.TotalPlayTimeSecs) + " played";

        // Strip
        StripWinsText.Text   = s.Wins.ToString();
        StripLossesText.Text = s.Losses.ToString();
        StripKillsText.Text  = s.Kills.ToString();
        StripDeathsText.Text = s.Deaths.ToString();
        StripAvgDmgText.Text = FormatNumber(s.AvgDamage);
        StripAvgAccText.Text = s.AvgAccuracyPct.ToString("0.0", CultureInfo.InvariantCulture) + "%";

        // Donut + side panel
        WinDonut.FillBrush = LookupBrush("Good", new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)));
        WinDonut.SetRatio(s.Wins, s.Matches, "{0:0}%", "win rate");
        DonutSubtitleText.Text = _kind == BucketDetailKind.Map
            ? $"{_bucketKey} across {s.Matches} matches"
            : $"{_bucketKey} loadout across {s.Matches} matches";
        StreakText.Text = s.LongestWinStreak > 0
            ? $"Best streak: {s.LongestWinStreak} W"
            : "No win streak yet";

        // Best performance — pull the per-match peaks from the filtered set.
        var rows = filtered
            .Select(m => new { Match = m, Me = Stats.Local(m, knownAccountId) })
            .Where(x => x.Me is not null)
            .ToList();

        var bestKills = rows.OrderByDescending(r => r.Me!.Kills).First();
        var bestDmg   = rows.OrderByDescending(r => r.Me!.Damage).First();
        var bestKd    = rows.OrderByDescending(r =>
            r.Me!.Deaths > 0 ? (double)r.Me.Kills / r.Me.Deaths : (double)r.Me.Kills).First();
        var bestAcc   = rows.OrderByDescending(r => r.Me!.Accuracy).First();
        var lastPlayed = rows.OrderByDescending(r => r.Match.PlayedAt).First();

        BestKillsText.Text = bestKills.Me!.Kills.ToString();
        BestDmgText.Text   = FormatNumber(bestDmg.Me!.Damage);
        double bestKdValue = bestKd.Me!.Deaths > 0
            ? (double)bestKd.Me.Kills / bestKd.Me.Deaths
            : bestKd.Me.Kills;
        BestKdText.Text    = bestKdValue.ToString("0.00", CultureInfo.InvariantCulture);
        BestAccText.Text   = bestAcc.Me!.Accuracy.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        LastPlayedText.Text = FormatRelative(lastPlayed.Match.PlayedAt);

        // Match list — same MatchCardView used elsewhere.
        var cards = new List<MatchCardView>();
        foreach (var m in filtered.OrderByDescending(m => m.PlayedAt).Take(20))
        {
            var card = new MatchCardView();
            card.Bind(m, knownAccountId);
            card.Clicked += OnMatchCardClicked;
            cards.Add(card);
        }
        MatchList.ItemsSource = cards;
        MatchListSubtitle.Text = filtered.Count > cards.Count
            ? $"showing {cards.Count} of {filtered.Count}"
            : $"{cards.Count} match" + (cards.Count == 1 ? "" : "es");
    }

    void ApplyEmptyState()
    {
        const string dash = "—";
        HeroKdText.Text = HeroWinPctText.Text = HeroMatchesText.Text = dash;
        HeroKdSubtext.Text = HeroWinPctSubtext.Text = HeroPlaytimeText.Text = "";

        StripWinsText.Text = StripLossesText.Text = "0";
        StripKillsText.Text = StripDeathsText.Text = "0";
        StripAvgDmgText.Text = StripAvgAccText.Text = dash;

        WinDonut.SetRatio(0, 0);
        DonutSubtitleText.Text = "No matches found.";
        StreakText.Text = "";

        BestKillsText.Text = BestDmgText.Text = BestKdText.Text = BestAccText.Text = LastPlayedText.Text = dash;

        MatchList.ItemsSource = null;
        MatchListSubtitle.Text = "";
    }

    void OnMatchCardClicked(object? sender, MatchRecord m)
    {
        if (Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<MainWindow>(this) is MainWindow win)
            win.NavigateTo(new MatchDetailPage(m));
    }

    void Back_Click(object? sender, RoutedEventArgs e)
    {
        if (Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<MainWindow>(this) is MainWindow win)
        {
            // Go back to whichever index page (Maps or Weapons) we came from.
            // Both Maps and Weapons live under the unified Stats page
            // now. We set the initial tab so the user lands back on
            // the same view they came from, then navigate.
            StatsPage.InitialTab = _kind == BucketDetailKind.Map
                ? StatsPage.StatsTab.Maps
                : StatsPage.StatsTab.Weapons;
            win.NavigateTo(typeof(StatsPage));
        }
    }

    static string FormatNumber(double v)
    {
        if (v >= 1_000_000) return (v / 1_000_000.0).ToString("0.00", CultureInfo.InvariantCulture) + "M";
        if (v >= 10_000)    return (v / 1000.0).ToString("0.0",  CultureInfo.InvariantCulture) + "k";
        if (v >= 1000)      return v.ToString("#,0", CultureInfo.InvariantCulture);
        return v.ToString("0", CultureInfo.InvariantCulture);
    }

    static string FormatDuration(double secs)
    {
        if (secs < 60)      return $"{(int)secs}s";
        if (secs < 3600)    return $"{(int)(secs/60)}m {(int)(secs%60)}s";
        long hours   = (long)(secs / 3600);
        long minutes = (long)((secs % 3600) / 60);
        return $"{hours}h {minutes}m";
    }

    static string FormatRelative(DateTime when)
    {
        TimeSpan ago = DateTime.Now - when.ToLocalTime();
        if (ago.TotalSeconds < 60)    return "just now";
        if (ago.TotalMinutes < 60)    return $"{(int)ago.TotalMinutes}m ago";
        if (ago.TotalHours   < 24)    return $"{(int)ago.TotalHours}h ago";
        if (ago.TotalDays    < 7)     return $"{(int)ago.TotalDays}d ago";
        return when.ToLocalTime().ToString("yyyy-MM-dd");
    }

    static IBrush LookupBrush(string key, IBrush fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
            && value is IBrush b)
            return b;
        return fallback;
    }
}
