using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using BCATracker.Core;
using BCATracker.UI.Controls;
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

        // ── Identity ─────────────────────────────────────────────────
        // Pick the most recent display name we've seen for the local player.
        string handle = all
            .Select(m => Stats.Local(m, knownAccountId)?.Name)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
            ?? "PLAYER";

        HandleText.Text = handle;
        AvatarInitial.Text = handle.Length > 0 ? char.ToUpperInvariant(handle[0]).ToString() : "?";
        MatchCountText.Text = $"{s.Matches} MATCHES";

        // Profile picture: render if user configured one and the file exists.
        // Falls back to the gradient+initial avatar otherwise.
        TryLoadProfilePicture();

        // ── Hero stats ──────────────────────────────────────────────
        if (s.Matches == 0)
        {
            WinPctText.Text = KdText.Text = AccuracyText.Text = DmgText.Text = "—";
        }
        else
        {
            WinPctText.Text   = s.WinRatePct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            KdText.Text       = s.KDRatio.ToString("0.00", CultureInfo.InvariantCulture);
            AccuracyText.Text = s.AvgAccuracyPct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            DmgText.Text      = FormatNumber(s.AvgDamage);
        }

        // ── Sub-stats ───────────────────────────────────────────────
        WinsText.Text    = s.Wins.ToString();
        LossesText.Text  = s.Losses.ToString();
        MatchesText.Text = s.Matches.ToString();
        KillsText.Text   = s.Kills.ToString();
        DeathsText.Text  = s.Deaths.ToString();
        AssistsText.Text = s.Assists.ToString();

        // ── Favourites ──────────────────────────────────────────────
        FavMapText.Text     = s.FavouriteMap     ?? "—";
        FavWeaponText.Text  = s.FavouriteWeapon  ?? "—";
        FavAbilityText.Text = s.FavouriteAbility ?? "—";

        FavMapSubText.Text     = SubtitleFor(all, m => m.Map,                                                 s.FavouriteMap);
        FavWeaponSubText.Text  = SubtitleFor(all, m => Stats.Local(m, knownAccountId)?.Weapon,                s.FavouriteWeapon);
        FavAbilitySubText.Text = SubtitleFor(all, m => Stats.Local(m, knownAccountId)?.Ability,               s.FavouriteAbility);

        // ── Recent matches (max 8) ──────────────────────────────────
        RecentList.ItemsSource = null;
        var cards = new List<MatchCardView>();
        const int maxRecent = 8;
        foreach (MatchRecord m in all.Take(maxRecent))
        {
            var card = new MatchCardView();
            card.Bind(m, knownAccountId);
            card.Clicked += OnMatchCardClicked;
            cards.Add(card);
        }
        RecentList.ItemsSource = cards;

        RecentSubtitle.Text = all.Count == 0
            ? "no matches saved yet"
            : $"showing {cards.Count} of {all.Count}";
    }

    void OnMatchCardClicked(object? sender, MatchRecord m)
    {
        // Walk up to MainWindow and tell it to navigate to a detail page.
        if (Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<MainWindow>(this) is MainWindow win)
            win.NavigateTo(new MatchDetailPage(m));
    }

    /// <summary>
    /// Try to load the user's configured profile picture. If the path is
    /// missing or the file fails to decode (corrupt, unreadable), we fall
    /// back to the initial-on-gradient avatar.
    /// </summary>
    void TryLoadProfilePicture()
    {
        string? path = AppServices.Settings.ProfilePicturePath;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            AvatarImageHost.IsVisible = false;
            AvatarFallback.IsVisible  = true;
            return;
        }

        try
        {
            // Reading via FileStream rather than passing the path directly
            // avoids Avalonia caching the image — the user can replace the
            // file and click around to refresh.
            using var fs = System.IO.File.OpenRead(path);
            AvatarImage.Source = new Avalonia.Media.Imaging.Bitmap(fs);
            AvatarImageHost.IsVisible = true;
            AvatarFallback.IsVisible  = false;
        }
        catch
        {
            AvatarImageHost.IsVisible = false;
            AvatarFallback.IsVisible  = true;
        }
    }

    static string SubtitleFor(IEnumerable<MatchRecord> all, Func<MatchRecord, string?> keyOf, string? key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        int n = all.Count(m => string.Equals(keyOf(m), key, StringComparison.OrdinalIgnoreCase));
        return $"{n} match" + (n == 1 ? "" : "es");
    }

    static string FormatNumber(double v)
    {
        if (v >= 10_000) return (v / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        if (v >= 1000)   return v.ToString("#,0", CultureInfo.InvariantCulture);
        return v.ToString("0", CultureInfo.InvariantCulture);
    }
}
