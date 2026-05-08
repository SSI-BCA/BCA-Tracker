using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BCATracker.UI.Services;

namespace BCATracker.UI.Controls;

/// <summary>
/// Builds a simple aligned-columns stats table into a StackPanel host.
/// Used by Maps and Weapons. Container's Grid.IsSharedSizeScope must be True
/// for column widths to align across rows.
/// </summary>
public static class BucketTable
{
    static readonly string[] s_headers = { "NAME", "MATCHES", "WIN %", "K/D", "AVG DMG", "AVG ACC" };
    static readonly string[] s_groups  = { "Col0",   "Col1",    "Col2",  "Col3", "Col4",   "Col5" };
    static readonly GridLength[] s_widths =
    {
        new(1, GridUnitType.Star),
        new(110),
        new(110),
        new(80),
        new(110),
        new(110),
    };

    public static void Render(StackPanel host, IReadOnlyList<BucketStats> rows, string nameLabel = "Name")
    {
        host.Children.Clear();

        var resAccent     = GetBrush("Accent");
        var resGood       = GetBrush("Good");
        var resDanger     = GetBrush("Danger");
        var resBorderSub  = GetBrush("Border.Subtle");
        var resSurfaceHov = GetBrush("Bg.SurfaceHover");

        // Header
        Grid hdr = MakeRowGrid();
        AddCell(hdr, 0, MakeHeaderText(nameLabel.ToUpperInvariant()));
        for (int i = 1; i < s_headers.Length; i++)
            AddCell(hdr, i, MakeHeaderText(s_headers[i]));
        host.Children.Add(WrapRow(hdr, isHeader: true, borderBrush: resBorderSub));

        if (rows.Count == 0)
        {
            host.Children.Add(MakeEmptyState());
            return;
        }

        bool alt = false;
        foreach (BucketStats r in rows)
        {
            Grid g = MakeRowGrid();

            var name = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 6, Height = 6,
                Fill = resAccent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 10, 0),
            });
            name.Children.Add(MakeBodyText(r.Key));
            AddCell(g, 0, name);

            AddCell(g, 1, MakeBodyText(r.Matches.ToString()));
            AddCell(g, 2, MakeBodyText(r.WinPct.ToString("0.0", CultureInfo.InvariantCulture) + "%",
                                       r.WinPct >= 50 ? resGood : resDanger));
            AddCell(g, 3, MakeBodyText(r.KD.ToString("0.00", CultureInfo.InvariantCulture)));
            AddCell(g, 4, MakeBodyText(FormatNumber(r.AvgDmg)));
            AddCell(g, 5, MakeBodyText(r.AvgAcc.ToString("0.0", CultureInfo.InvariantCulture) + "%"));

            host.Children.Add(WrapRow(g, isHeader: false, alt: alt, altBrush: resSurfaceHov));
            alt = !alt;
        }
    }

    static Grid MakeRowGrid()
    {
        var g = new Grid();
        for (int i = 0; i < s_headers.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = s_widths[i],
                SharedSizeGroup = s_groups[i],
            });
        }
        return g;
    }

    static void AddCell(Grid g, int col, Control content)
    {
        Grid.SetColumn(content, col);
        g.Children.Add(content);
    }

    static Control WrapRow(Grid inner, bool isHeader, bool alt = false,
                           IBrush? borderBrush = null, IBrush? altBrush = null)
    {
        var bd = new Border
        {
            Padding = new Thickness(0, isHeader ? 12 : 10, 0, isHeader ? 12 : 10),
            Child = inner,
        };
        if (isHeader && borderBrush is not null)
        {
            bd.BorderBrush = borderBrush;
            bd.BorderThickness = new Thickness(0, 0, 0, 1);
        }
        else if (alt && altBrush is not null)
        {
            bd.Background = altBrush;
            bd.Opacity = 0.4;
        }
        return bd;
    }

    static TextBlock MakeHeaderText(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
        };
        tb.Classes.Add("label");
        return tb;
    }

    static TextBlock MakeBodyText(string text, IBrush? colour = null)
    {
        var tb = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
        };
        if (colour is not null) tb.Foreground = colour;
        return tb;
    }

    static Control MakeEmptyState()
    {
        var tb = new TextBlock
        {
            Text = "No data yet.",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 24),
        };
        tb.Classes.Add("muted");
        return tb;
    }

    static IBrush GetBrush(string key)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
            && value is IBrush brush)
            return brush;
        return Brushes.Gray;
    }

    static string FormatNumber(double v)
    {
        if (v >= 10_000) return (v / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        if (v >= 1000)   return v.ToString("#,0", CultureInfo.InvariantCulture);
        return v.ToString("0", CultureInfo.InvariantCulture);
    }
}
