using System.Globalization;
using Avalonia.Controls;
using BCATracker.UI.Services;

namespace BCATracker.UI.Views;

public partial class LifetimeStatsPage : UserControl
{
    public LifetimeStatsPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Refresh();
    }

    void Refresh()
    {
        var matches = AppServices.Matches.LoadAll();
        StatsSummary s = Stats.ComputeLifetime(matches);

        if (s.Matches == 0)
        {
            string dash = "—";
            KdText.Text = KdaText.Text = AvgKillsText.Text = AvgDeathsText.Text = dash;
            AvgDmgText.Text = TotalDmgText.Text = WeaponAccText.Text = AbilityAccText.Text = dash;
            WinPctText.Text = WinsText.Text = LossesText.Text = MatchesText.Text = dash;
            BestKillsText.Text = BestDamageText.Text = BestKdText.Text = LongestStreakText.Text = dash;
            CurrentStreakText.Text = FlawlessText.Text = MatchMvpText.Text = TeamMvpText.Text = dash;
            TotalTimeText.Text = AvgMatchTimeText.Text = dash;
            SubtitleText.Text = "No matches saved yet.";
            return;
        }

        SubtitleText.Text = $"Across {s.Matches} match" + (s.Matches == 1 ? "" : "es") + ".";

        KdText.Text        = s.KDRatio.ToString("0.00", CultureInfo.InvariantCulture);
        KdaText.Text       = s.KDARatio.ToString("0.00", CultureInfo.InvariantCulture);
        AvgKillsText.Text  = s.AvgKillsPerMatch.ToString("0.0", CultureInfo.InvariantCulture);
        AvgDeathsText.Text = s.AvgDeathsPerMatch.ToString("0.0", CultureInfo.InvariantCulture);

        AvgDmgText.Text     = FormatNumber(s.AvgDamage);
        TotalDmgText.Text   = FormatNumber(s.TotalDamage);
        WeaponAccText.Text  = s.AvgAccuracyPct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        AbilityAccText.Text = s.AvgAbilityAccuracyPct.ToString("0.0", CultureInfo.InvariantCulture) + "%";

        WinPctText.Text  = s.WinRatePct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        WinsText.Text    = s.Wins.ToString();
        LossesText.Text  = s.Losses.ToString();
        MatchesText.Text = s.Matches.ToString();

        // Records group
        BestKillsText.Text     = s.BestMatchKills.ToString();
        BestDamageText.Text    = FormatNumber(s.BestMatchDamage);
        BestKdText.Text        = s.BestMatchKD > 0
            ? s.BestMatchKD.ToString("0.00", CultureInfo.InvariantCulture)
            : "—";
        LongestStreakText.Text = s.LongestWinStreak.ToString();

        // Highlights group
        CurrentStreakText.Text = s.CurrentWinStreak > 0
            ? $"{s.CurrentWinStreak} W"
            : "—";
        FlawlessText.Text  = s.FlawlessMatches.ToString();
        MatchMvpText.Text  = s.MatchMvpCount.ToString();
        TeamMvpText.Text   = s.TeamMvpCount.ToString();

        TotalTimeText.Text    = FormatDuration(s.TotalPlayTimeSecs);
        AvgMatchTimeText.Text = FormatDuration(s.AvgMatchDurationSecs);
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
}
