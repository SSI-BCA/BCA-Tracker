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

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Refresh();
    }

    void Refresh()
    {
        List<MatchRecord> all = AppServices.Matches.LoadAll();
        string? knownAccountId = Stats.FindKnownAccountId(all);
        StatsSummary s = Stats.ComputeLifetime(all, knownAccountId);

        // Identity — most recent display name we've seen for the local player.
        string handle = all
            .Select(m => Stats.Local(m, knownAccountId)?.Name)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
            ?? "PLAYER";

        HandleText.Text = handle;
        AvatarInitial.Text = handle.Length > 0 ? char.ToUpperInvariant(handle[0]).ToString() : "?";
        MatchCountText.Text = s.Matches == 0
            ? "NO MATCHES YET"
            : $"{s.Matches} MATCH" + (s.Matches == 1 ? "" : "ES");
        TryLoadProfilePicture();

        if (s.Matches == 0)
        {
            ApplyEmptyState();
            return;
        }

        // ── Hero tiles ──────────────────────────────────────────────────────
        HeroKdText.Text       = s.KDRatio.ToString("0.00", CultureInfo.InvariantCulture);
        HeroKdaSubtext.Text   = "K/D/A " + s.KDARatio.ToString("0.00", CultureInfo.InvariantCulture);

        HeroWinPctText.Text   = s.WinRatePct.ToString("0", CultureInfo.InvariantCulture) + "%";
        HeroWinPctText.Classes.Remove("good");
        HeroWinPctText.Classes.Remove("danger");
        if      (s.WinRatePct >= 60) HeroWinPctText.Classes.Add("good");
        else if (s.WinRatePct < 50)  HeroWinPctText.Classes.Add("danger");
        HeroWinPctSubtext.Text = $"{s.Wins}W · {s.Losses}L";

        HeroMatchesText.Text  = s.Matches.ToString(CultureInfo.InvariantCulture);
        HeroPlayTimeText.Text = FormatDuration(s.TotalPlayTimeSecs) + " played";

        // ── Stat strip ──────────────────────────────────────────────────────
        StripWinsText.Text     = s.Wins.ToString();
        StripLossesText.Text   = s.Losses.ToString();
        StripKillsText.Text    = s.Kills.ToString();
        StripDeathsText.Text   = s.Deaths.ToString();
        StripAssistsText.Text  = s.Assists.ToString();
        StripFlawlessText.Text = s.FlawlessMatches.ToString();
        StripMatchMvpText.Text = s.MatchMvpCount.ToString();
        StripTeamMvpText.Text  = s.TeamMvpCount.ToString();

        // ── Donut + breakdown ───────────────────────────────────────────────
        WinDonut.FillBrush = LookupBrush("Good", new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)));
        WinDonut.SetRatio(s.Wins, s.Matches, "{0:0}%", "win rate");
        WinsCountText.Text   = s.Wins.ToString();
        LossesCountText.Text = s.Losses.ToString();

        CurrentStreakText.Text = s.CurrentWinStreak switch
        {
            > 0 => $"{s.CurrentWinStreak} W",
            < 0 => $"{-s.CurrentWinStreak} L",
            _   => "—",
        };
        LongestStreakText.Text = s.LongestWinStreak > 0 ? $"{s.LongestWinStreak} W" : "—";
        TotalTimeText.Text     = FormatDuration(s.TotalPlayTimeSecs);
        AvgMatchTimeText.Text  = FormatDuration(s.AvgMatchDurationSecs);
        NetWinDeltaText.Text   = s.NetWinDelta > 0
            ? $"+{s.NetWinDelta}"
            : s.NetWinDelta.ToString();
        AvgKdaText.Text        = s.KDARatio.ToString("0.00", CultureInfo.InvariantCulture);
        FavMapText.Text        = string.IsNullOrEmpty(s.FavouriteMap)    ? "—" : s.FavouriteMap!;
        FavWeaponText.Text     = string.IsNullOrEmpty(s.FavouriteWeapon) ? "—" : s.FavouriteWeapon!;

        // ── Performance ────────────────────────────────────────────────────
        AvgKillsText.Text  = s.AvgKillsPerMatch.ToString("0.0", CultureInfo.InvariantCulture);
        AvgDeathsText.Text = s.AvgDeathsPerMatch.ToString("0.0", CultureInfo.InvariantCulture);
        AvgDmgText.Text    = FormatNumber(s.AvgDamage);
        AvgHealText.Text   = s.Matches > 0
            ? FormatNumber(s.TotalHeal / s.Matches)
            : "—";
        TotalDmgText.Text  = FormatNumber(s.TotalDamage);

        WeaponAccBar.Value  = s.AvgAccuracyPct;
        AbilityAccBar.Value = s.AvgAbilityAccuracyPct;
        WeaponAccBar.FillBrush  = LookupBrush("Accent",       new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)));
        AbilityAccBar.FillBrush = LookupBrush("Accent.Hover", new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)));

        double dmgMax = Math.Max(s.AvgDamage, s.AvgDamageTaken);
        if (dmgMax <= 0) dmgMax = 1;
        DamageDealtBar.Max = dmgMax;
        DamageTakenBar.Max = dmgMax;
        DamageDealtBar.Value = s.AvgDamage;
        DamageTakenBar.Value = s.AvgDamageTaken;
        DamageDealtBar.FillBrush = LookupBrush("Good",   new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)));
        DamageTakenBar.FillBrush = LookupBrush("Danger", new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));

        // ── Personal bests ─────────────────────────────────────────────────
        BestKillsText.Text  = s.BestMatchKills.ToString();
        BestDamageText.Text = FormatNumber(s.BestMatchDamage);
        BestKdText.Text     = s.BestMatchKD > 0
            ? s.BestMatchKD.ToString("0.00", CultureInfo.InvariantCulture)
            : "—";
    }

    /// <summary>
    /// Try to load the user's configured profile picture. Reads via
    /// FileStream rather than passing the path so Avalonia doesn't cache
    /// it — if the user replaces the file with the same name, navigating
    /// away and back picks up the new content.
    ///
    /// GIFs are accepted by the file picker but render as a single frame
    /// (their first frame). True animation would need a third-party
    /// package (AnimatedImage.Avalonia targets Avalonia 11; we're on 12),
    /// and the trade-off didn't seem worth it for a profile picture.
    /// </summary>
    void TryLoadProfilePicture()
    {
        string? path = AppServices.Settings.ProfilePicturePath;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            AvatarImageHost.IsVisible = false;
            AvatarFallback.IsVisible  = true;
            AvatarImage.Source = null;
            return;
        }

        try
        {
            // Decode at target resolution so downscaling happens once with
            // high quality, not on every render. The avatar shows at 128
            // logical pixels but we decode to 512 — that's 4× the display
            // size, which keeps it crisp on 4K displays and on the rare
            // case the avatar gets scaled up anywhere. A 512×512 RGBA
            // bitmap is ~1MB which is trivial.
            using var fs = System.IO.File.OpenRead(path);
            AvatarImage.Source = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(
                fs, 512, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);

            // Belt-and-braces: tell the Image control itself to use
            // high-quality scaling at render time too, in case the
            // visual size is even bigger than the decoded bitmap.
            Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(
                AvatarImage,
                Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);

            AvatarImageHost.IsVisible = true;
            AvatarFallback.IsVisible  = false;
        }
        catch
        {
            AvatarImageHost.IsVisible = false;
            AvatarFallback.IsVisible  = true;
        }
    }

    void ApplyEmptyState()
    {
        const string dash = "—";

        HeroKdText.Text = dash; HeroKdaSubtext.Text = "";
        HeroWinPctText.Text = dash; HeroWinPctSubtext.Text = "";
        HeroMatchesText.Text = "0"; HeroPlayTimeText.Text = "no playtime yet";

        StripWinsText.Text = StripLossesText.Text = "0";
        StripKillsText.Text = StripDeathsText.Text = StripAssistsText.Text = "0";
        StripFlawlessText.Text = StripMatchMvpText.Text = StripTeamMvpText.Text = "0";

        WinDonut.SetRatio(0, 0);
        WinsCountText.Text = "0";
        LossesCountText.Text = "0";

        CurrentStreakText.Text = LongestStreakText.Text = dash;
        TotalTimeText.Text = AvgMatchTimeText.Text = dash;
        NetWinDeltaText.Text = AvgKdaText.Text = dash;
        FavMapText.Text = FavWeaponText.Text = dash;

        AvgKillsText.Text = AvgDeathsText.Text = AvgDmgText.Text = dash;
        AvgHealText.Text = TotalDmgText.Text = dash;

        WeaponAccBar.Value = AbilityAccBar.Value = 0;
        DamageDealtBar.Value = DamageTakenBar.Value = 0;

        BestKillsText.Text = BestDamageText.Text = BestKdText.Text = dash;
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

    static IBrush LookupBrush(string key, IBrush fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
            && value is IBrush b)
            return b;
        return fallback;
    }
}
