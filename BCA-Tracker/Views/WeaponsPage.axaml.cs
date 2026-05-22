using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using BCATracker.Core;
using BCATracker.UI.Controls;
using BCATracker.UI.Services;

namespace BCATracker.UI.Views;

public partial class WeaponsPage : UserControl
{
    public WeaponsPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Refresh();
    }

    void Refresh()
    {
        var matches = AppServices.Matches.LoadAll();

        var played = Stats.ByBucket(matches, (_m, me) => me.Weapon)
            .ToDictionary(b => b.Key, b => b, System.StringComparer.OrdinalIgnoreCase);

        var allKnown = BCAEnums.AllWeaponNames();
        var ordered = new List<BucketStats>();

        foreach (var b in played.Values.OrderByDescending(b => b.Matches))
            ordered.Add(b);

        foreach (var name in allKnown)
        {
            if (!played.ContainsKey(name))
                ordered.Add(new BucketStats { Key = name, Matches = 0 });
        }

        var cards = new List<BucketCard>();
        foreach (var row in ordered)
        {
            var card = new BucketCard();
            card.Bind("WEAPON", row);
            card.CardClicked += OnCardClicked;
            cards.Add(card);
        }
        CardsHost.ItemsSource = cards;

        int playedCount = played.Count;
        SubtitleText.Text = playedCount == 0
            ? $"{allKnown.Count} weapons · none used yet."
            : $"{playedCount} of {allKnown.Count} weapons used" +
              $" across {matches.Count} match" + (matches.Count == 1 ? "" : "es") + ".";
    }

    void OnCardClicked(object? sender, BucketCard card)
    {
        if (Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<MainWindow>(this) is MainWindow win)
            win.NavigateTo(new BucketDetailPage(BucketDetailKind.Weapon, card.Key));
    }
}
