using Avalonia.Controls;
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
        var rows = Stats.ByBucket(matches, (_m, me) => me.Weapon);

        BucketTable.Render(RowsHost, rows, nameLabel: "Weapon");

        SubtitleText.Text = rows.Count == 0
            ? "No matches saved yet."
            : $"{rows.Count} weapon" + (rows.Count == 1 ? "" : "s") + " used.";
    }
}
