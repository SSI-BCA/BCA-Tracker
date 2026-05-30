using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace BCATracker.UI.Services;

/// <summary>
/// Runtime accent-color swapping. The base palette in Palette.axaml
/// declares four resources (Accent.Color, Accent.Hover.Color,
/// Accent.Pressed.Color, Accent.Glow.Color) that everything in the
/// UI binds to via DynamicResource. We just replace those four entries
/// at the application level and Avalonia repaints automatically.
///
/// Picking the names: kept simple ("purple", "red", "blue", ...) so
/// the JSON setting file is human-readable and the picker UI is just
/// labels. Hex values were picked to roughly match the brightness of
/// the default purple #8B5CF6 so layouts don't shift in appearance
/// when the user swaps colors.
/// </summary>
public static class AccentTheme
{
    public sealed record Palette(string Name, Color Base, Color Hover, Color Pressed, Color Glow);

    /// <summary>Canonical list, in the order the picker shows them.</summary>
    public static readonly IReadOnlyList<Palette> All = new[]
    {
        // The default. blueviolet, matches the icon and most badges.
        new Palette("purple", FromHex("#8B5CF6"), FromHex("#A78BFA"), FromHex("#7C3AED"), FromHex("#188B5CF6")),
        new Palette("red",    FromHex("#EF4444"), FromHex("#F87171"), FromHex("#DC2626"), FromHex("#18EF4444")),
        new Palette("blue",   FromHex("#3B82F6"), FromHex("#60A5FA"), FromHex("#2563EB"), FromHex("#183B82F6")),
        new Palette("green",  FromHex("#22C55E"), FromHex("#4ADE80"), FromHex("#16A34A"), FromHex("#1822C55E")),
        new Palette("orange", FromHex("#F97316"), FromHex("#FB923C"), FromHex("#EA580C"), FromHex("#18F97316")),
        new Palette("teal",   FromHex("#14B8A6"), FromHex("#2DD4BF"), FromHex("#0D9488"), FromHex("#1814B8A6")),
        new Palette("yellow", FromHex("#EAB308"), FromHex("#FACC15"), FromHex("#CA8A04"), FromHex("#18EAB308")),
    };

    /// <summary>
    /// Swap the live application resources to the named palette. No-op
    /// if Application.Current is null (i.e. called too early during
    /// startup). Unknown palette names fall back to "purple".
    /// </summary>
    public static void Apply(string name)
    {
        var app = Application.Current;
        if (app is null) return;

        var palette = Find(name);
        // Update the *.Color entries. The brush resources (Accent,
        // Accent.Hover, ...) are defined as SolidColorBrushes that
        // bind to these colors via DynamicResource, so swapping the
        // colors here repaints every button, badge, and progress bar
        // that references them.
        app.Resources["Accent.Color"]         = palette.Base;
        app.Resources["Accent.Hover.Color"]   = palette.Hover;
        app.Resources["Accent.Pressed.Color"] = palette.Pressed;
        app.Resources["Accent.Glow.Color"]    = palette.Glow;
    }

    static Palette Find(string name)
    {
        foreach (var p in All)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p;
        return All[0]; // purple fallback
    }

    static Color FromHex(string hex)
    {
        // Accept "#RRGGBB" or "#AARRGGBB". Avalonia's Color.Parse
        // handles both cases natively; we just wrap so the caller
        // doesn't have to.
        return Color.Parse(hex);
    }
}
