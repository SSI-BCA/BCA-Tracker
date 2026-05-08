using Avalonia.Controls;
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
        var rows = Stats.ByBucket(matches, (m, _me) => m.Map);

        BucketTable.Render(RowsHost, rows, nameLabel: "Map");

        SubtitleText.Text = rows.Count == 0
            ? "No matches saved yet."
            : $"{rows.Count} map" + (rows.Count == 1 ? "" : "s") +
              $" played across {matches.Count} match" + (matches.Count == 1 ? "" : "es") + ".";
    }
}
