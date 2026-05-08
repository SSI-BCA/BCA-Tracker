using System;

namespace BCATracker.UI.Controls;

/// <summary>
/// Single entry in the top nav. The icon is intentionally typed as `string?`
/// rather than `IControl` — the consumer either passes a glyph character or
/// leaves it null. We render it as text inside the nav template, no need to
/// hold a UIElement.
/// </summary>
public class NavItem
{
    public string Label    { get; }
    public Type   PageType { get; }
    public string? Glyph   { get; }

    public NavItem(string label, Type pageType, string? glyph = null)
    {
        Label    = label;
        PageType = pageType;
        Glyph    = glyph;
    }

    public override string ToString() => Label;
}
