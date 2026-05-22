using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using BCATracker.UI.Services;

namespace BCATracker.UI.Controls;

/// <summary>
/// A clickable card showing per-bucket stats — one per map or weapon on
/// the Maps/Weapons pages. Click → caller navigates to the detail page
/// (signaled via the <see cref="Clicked"/> event).
///
/// Layout:
/// ┌─────────────────────────────────────────┐
/// │ MAP NAME                  12 matches    │
/// │                                         │
/// │ Win rate                                │
/// │ ████████████░░░░░░░░  64%               │
/// │                                         │
/// │ K/D 1.42 · Avg dmg 1,203 · Acc 47%      │
/// └─────────────────────────────────────────┘
///
/// Built in code (not XAML) so it can be instantiated cheaply in a loop
/// when the grid populates and so the styling stays consistent regardless
/// of where it ends up in the visual tree.
/// </summary>
public class BucketCard : Button
{
    /// <summary>
    /// Tell Avalonia to style this control as a plain Button so the
    /// existing <c>Button.card-button</c> selector applies. Avalonia's
    /// type selectors don't match derived types by default — without
    /// this override, our custom style sheet wouldn't paint a card
    /// background or border on the BucketCard, leaving it as bare text
    /// floating on the page.
    /// </summary>
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>The bucket key this card is showing — map name or weapon
    /// name. Caller reads this in the Clicked handler to know which
    /// detail page to open.</summary>
    public string Key { get; private set; } = "";

    /// <summary>Bucket type label, e.g. "MAP" or "WEAPON" — shown above
    /// the title for reading order context.</summary>
    public string Kind { get; private set; } = "";

    /// <summary>
    /// Fired when the user clicks the card. Named CardClicked rather than
    /// Clicked to avoid confusion with the inherited Button.Click event.
    /// </summary>
    public event EventHandler<BucketCard>? CardClicked;

    public BucketCard()
    {
        // Use the existing card-button style so hover/focus states match
        // the rest of the UI.
        Classes.Add("card-button");
        MinHeight = 140;
        Padding   = new Thickness(20, 16);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment   = VerticalAlignment.Top;

        Click += (_, _) => CardClicked?.Invoke(this, this);
    }

    public void Bind(string kind, BucketStats s)
    {
        Key  = s.Key;
        Kind = kind;

        // Unplayed buckets — show the name with a "not played yet" pill
        // instead of zeroed-out stats, which would imply 0% win rate.
        if (s.Matches == 0)
        {
            BindUnplayed(kind, s.Key);
            return;
        }

        Opacity = 1.0;

        IBrush primaryFg = LookupBrush("Fg.Primary",   Brushes.White);
        IBrush secondFg  = LookupBrush("Fg.Secondary", Brushes.LightGray);
        IBrush mutedFg   = LookupBrush("Fg.Muted",     Brushes.Gray);
        IBrush track     = LookupBrush("Bg.SurfaceRaised", Brushes.DimGray);
        IBrush good      = LookupBrush("Good",         new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)));
        IBrush warn      = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        IBrush danger    = LookupBrush("Danger",       new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));

        // Header row: title + match-count chip
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 12),
        };

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = kind.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = mutedFg,
            LetterSpacing = 1.5,
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(s.Key) ? "(unknown)" : s.Key,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = primaryFg,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(titleStack, 0);
        header.Children.Add(titleStack);

        var matchChip = new Border
        {
            Background = track,
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = $"{s.Matches} match" + (s.Matches == 1 ? "" : "es"),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = secondFg,
            }
        };
        Grid.SetColumn(matchChip, 1);
        header.Children.Add(matchChip);

        // Win-rate bar — the big visual cue. Bar length = win %, color
        // shifts: red <40, amber 40–55, green ≥55.
        IBrush winColor = s.WinPct >= 55 ? good : (s.WinPct >= 40 ? warn : danger);

        var winRow = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        winRow.Children.Add(new TextBlock
        {
            Text = "WIN RATE",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = mutedFg,
            LetterSpacing = 1.5,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var barRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var barHost = new Border
        {
            Background = track,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        // Inner fill — width set by binding to barHost via a child Grid.
        var fillRect = new Rectangle
        {
            Fill = winColor,
            HorizontalAlignment = HorizontalAlignment.Left,
            RadiusX = 4,
            RadiusY = 4,
            Height = 8,
        };
        // Computing pixel width directly off s.WinPct is fine because the
        // card itself has a fixed pad; the Border will report its layout
        // bounds once attached. Use a binding so the bar resizes with
        // the grid column.
        barHost.Child = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*"),
            Children = { fillRect },
        };
        // Simpler: drive width as a fraction of the host's bounds.
        barHost.LayoutUpdated += (_, _) =>
        {
            double frac = Math.Clamp(s.WinPct / 100.0, 0, 1);
            fillRect.Width = barHost.Bounds.Width * frac;
        };
        Grid.SetColumn(barHost, 0);
        barRow.Children.Add(barHost);

        var winText = new TextBlock
        {
            Text = s.WinPct.ToString("0", CultureInfo.InvariantCulture) + "%",
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = winColor,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 36,
            TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(winText, 1);
        barRow.Children.Add(winText);

        winRow.Children.Add(barRow);

        // Footer row — three quick stats separated by middle-dots. Built
        // as a flow of TextBlocks rather than Inlines to keep things
        // simple and let WrapPanel handle overflow if a card is narrow.
        var footer = new WrapPanel { Orientation = Orientation.Horizontal };
        AddFooterStat(footer, "K/D",     s.KD.ToString("0.00", CultureInfo.InvariantCulture), primaryFg, mutedFg);
        AddFooterSeparator(footer, mutedFg);
        AddFooterStat(footer, "Avg dmg", FormatNumber(s.AvgDmg), primaryFg, mutedFg);
        AddFooterSeparator(footer, mutedFg);
        AddFooterStat(footer, "Acc",     s.AvgAcc.ToString("0", CultureInfo.InvariantCulture) + "%", primaryFg, mutedFg);

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(winRow);
        content.Children.Add(footer);
        Content = content;
    }

    /// <summary>
    /// Render a card for a map/weapon the player hasn't played yet.
    /// Same outer layout as a normal card so the grid keeps a consistent
    /// rhythm; just no stats inside.
    /// </summary>
    void BindUnplayed(string kind, string name)
    {
        IBrush primaryFg = LookupBrush("Fg.Primary",   Brushes.White);
        IBrush mutedFg   = LookupBrush("Fg.Muted",     Brushes.Gray);
        IBrush track     = LookupBrush("Bg.SurfaceRaised", Brushes.DimGray);

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = kind.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = mutedFg,
            LetterSpacing = 1.5,
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(name) ? "(unknown)" : name,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = primaryFg,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });

        var pill = new Border
        {
            Background = track,
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(10, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 16, 0, 0),
            Child = new TextBlock
            {
                Text = "Not played yet",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = mutedFg,
            }
        };

        var content = new StackPanel();
        content.Children.Add(titleStack);
        content.Children.Add(pill);
        Content = content;

        // Slight visual fade so the unplayed cards recede next to played ones.
        Opacity = 0.65;
    }

    static void AddFooterStat(WrapPanel host, string label, string value, IBrush valueFg, IBrush labelFg)
    {
        host.Children.Add(new TextBlock
        {
            Text = label + " ",
            FontSize = 12,
            Foreground = labelFg,
            VerticalAlignment = VerticalAlignment.Center,
        });
        host.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = valueFg,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    static void AddFooterSeparator(WrapPanel host, IBrush brush)
    {
        host.Children.Add(new TextBlock
        {
            Text = " · ",
            FontSize = 12,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    static string FormatNumber(double v)
    {
        if (v >= 1_000_000) return (v / 1_000_000.0).ToString("0.00", CultureInfo.InvariantCulture) + "M";
        if (v >= 10_000)    return (v / 1000.0).ToString("0.0",  CultureInfo.InvariantCulture) + "k";
        if (v >= 1000)      return v.ToString("#,0", CultureInfo.InvariantCulture);
        return v.ToString("0", CultureInfo.InvariantCulture);
    }

    static IBrush LookupBrush(string key, IBrush fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
            && value is IBrush b)
            return b;
        return fallback;
    }
}
