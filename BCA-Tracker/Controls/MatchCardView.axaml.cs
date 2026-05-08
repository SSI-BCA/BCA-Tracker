using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using BCATracker.Core;
using BCATracker.UI.Services;

namespace BCATracker.UI.Controls;

public partial class MatchCardView : UserControl
{
    public MatchRecord? Record { get; private set; }
    public event EventHandler<MatchRecord>? Clicked;

    public MatchCardView() => InitializeComponent();


    /// <summary>
    /// Bind the card to a match record. Caller passes in the user's known
    /// AccountId so cross-match identity works for the rare matches that
    /// have it populated; falls back to IsLocalPlayer when null.
    /// </summary>
    public void Bind(MatchRecord match, string? knownAccountId = null)
    {
        Record = match;

        PlayerRecord? me = Stats.Local(match, knownAccountId);
        bool? win = Stats.DidLocalWin(match, knownAccountId);

        // Win/loss bar
        ResultBar.Background = win switch
        {
            true  => GetBrush("Good"),
            false => GetBrush("Danger"),
            _     => GetBrush("Border.Strong"),
        };

        MapText.Text = string.IsNullOrEmpty(match.Map) ? "Unknown Map" : match.Map;
        ModeAgoText.Text = $"{match.GameMode} · {AgoString(match.PlayedAt)}";

        if (win == true)
        {
            ResultText.Text = "VICTORY";
            ResultText.Foreground = GetBrush("Good");
        }
        else if (win == false)
        {
            ResultText.Text = "DEFEAT";
            ResultText.Foreground = GetBrush("Danger");
        }
        else
        {
            ResultText.Text = "FINISHED";
            ResultText.Foreground = GetBrush("Fg.Secondary");
        }

        if (me is not null)
        {
            LoadoutText.Text = JoinNonEmpty(" · ", me.Weapon, me.Ability, me.Module);
            KdaText.Text     = $"{me.Kills} / {me.Deaths} / {me.Assists}";

            double kd = me.Deaths > 0 ? (double)me.Kills / me.Deaths : me.Kills;
            KdRatioText.Text = kd.ToString("0.00", CultureInfo.InvariantCulture);
        }
        else
        {
            LoadoutText.Text = "(no local-player record)";
            KdaText.Text = "—";
            KdRatioText.Text = "—";
        }
    }

    void Root_Click(object? sender, RoutedEventArgs e)
    {
        if (Record is not null) Clicked?.Invoke(this, Record);
    }

    static IBrush GetBrush(string key)
    {
        // Look up a brush from Application resources. We use the App-level
        // resource so this works whether the control is currently in the
        // visual tree or not.
        if (Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
            && value is IBrush brush)
            return brush;
        return Brushes.Transparent;
    }

    static string JoinNonEmpty(string sep, params string?[] parts)
    {
        var live = new System.Collections.Generic.List<string>();
        foreach (string? p in parts)
            if (!string.IsNullOrWhiteSpace(p)) live.Add(p!);
        return string.Join(sep, live);
    }

    static string AgoString(DateTime utc)
    {
        TimeSpan ago = DateTime.UtcNow - utc.ToUniversalTime();
        if (ago.TotalSeconds < 60)  return "just now";
        if (ago.TotalMinutes < 60)  return $"{(int)ago.TotalMinutes}m ago";
        if (ago.TotalHours < 24)    return $"{(int)ago.TotalHours}h ago";
        if (ago.TotalDays < 7)      return $"{(int)ago.TotalDays}d ago";
        return utc.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);
    }
}
