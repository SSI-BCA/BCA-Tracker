using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using BCATracker.Core;
using BCATracker.UI.Controls;
using BCATracker.UI.Services;

namespace BCATracker.UI.Views;

public partial class MapsPage : UserControl
{
    public MapsPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Refresh();
    }

    void Refresh()
    {
        var matches = AppServices.Matches.LoadAll();

        // Played stats, indexed by display name (case-insensitive).
        var played = Stats.ByBucket(matches, (m, _me) => m.Map)
            .ToDictionary(b => b.Key, b => b, System.StringComparer.OrdinalIgnoreCase);

        // Build the full ordered list: played maps first (most matches), then
        // any remaining known-but-unplayed maps in their declared order.
        var allKnown = BCAEnums.AllMapDisplayNames();
        var ordered = new List<BucketStats>();

        // Played, sorted by match count.
        foreach (var b in played.Values.OrderByDescending(b => b.Matches))
            ordered.Add(b);

        // Unplayed.
        foreach (var name in allKnown)
        {
            if (!played.ContainsKey(name))
                ordered.Add(new BucketStats { Key = name, Matches = 0 });
        }

        // Render.
        var cards = new List<BucketCard>();
        foreach (var row in ordered)
        {
            var card = new BucketCard();
            card.Bind("MAP", row);
            card.CardClicked += OnCardClicked;
            cards.Add(card);
        }
        CardsHost.ItemsSource = cards;

        int playedCount = played.Count;
        SubtitleText.Text = playedCount == 0
            ? $"{allKnown.Count} maps · none played yet."
            : $"{playedCount} of {allKnown.Count} maps played" +
              $" across {matches.Count} match" + (matches.Count == 1 ? "" : "es") + ".";
    }

    void OnCardClicked(object? sender, BucketCard card)
    {
        if (Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<MainWindow>(this) is MainWindow win)
            win.NavigateTo(new BucketDetailPage(BucketDetailKind.Map, card.Key));
    }
}
