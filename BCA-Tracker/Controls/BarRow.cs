using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BCATracker.UI.Controls;

/// <summary>
/// A single horizontal bar with a label, value, and proportional fill.
/// Useful for "weapon accuracy 78%" where the percentage and the bar
/// length should both be visible.
///
/// Layout (entire row is one BarRow):
///   ┌──────────────────────────────────────────────┐
///   │ Weapon         ████████████░░░░░░  78%       │
///   └──────────────────────────────────────────────┘
///
/// Stack multiple BarRows vertically for comparisons:
///   <StackPanel>
///       <ctl:BarRow Label="Weapon"  Value="78"     Max="100" Format="{}{0:0.0}%"/>
///       <ctl:BarRow Label="Ability" Value="52"     Max="100" Format="{}{0:0.0}%"/>
///   </StackPanel>
///
/// `Max` lets bars share a scale across rows so the lengths are comparable
/// (the longer of two bars is meaningfully longer, not just rescaled to fill).
/// </summary>
public class BarRow : Control
{
    // ── Inputs ──────────────────────────────────────────────────────────────

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<BarRow, string?>(nameof(Label));

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<BarRow, double>(nameof(Value), 0);

    public static readonly StyledProperty<double> MaxProperty =
        AvaloniaProperty.Register<BarRow, double>(nameof(Max), 100);

    public static readonly StyledProperty<string> FormatProperty =
        AvaloniaProperty.Register<BarRow, string>(nameof(Format), "{0:0.0}");

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<BarRow, IBrush?>(nameof(FillBrush));

    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double  Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double  Max   { get => GetValue(MaxProperty);   set => SetValue(MaxProperty, value); }
    public string  Format { get => GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public IBrush? FillBrush { get => GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    static BarRow()
    {
        AffectsRender<BarRow>(LabelProperty, ValueProperty, MaxProperty, FormatProperty, FillBrushProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Default height — enough for label + bar + a little breathing room.
        double h = 28;
        double w = double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width;
        return new Size(w, h);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 50 || h < 14) return;

        IBrush primaryFg = LookupBrush("Fg.Primary",   Brushes.White);
        IBrush mutedFg   = LookupBrush("Fg.Secondary", Brushes.LightGray);
        IBrush track     = LookupBrush("Bg.SurfaceRaised", Brushes.DimGray);
        IBrush fill      = FillBrush ?? LookupBrush("Accent", Brushes.MediumPurple);

        // Fixed-width left label area, fixed-width right value area, bar fills the rest.
        const double labelWidth = 90;
        const double valueWidth = 70;
        const double gap        = 12;

        double barL = labelWidth + gap;
        double barR = w - valueWidth - gap;
        double barW = Math.Max(0, barR - barL);
        double barH = 8;
        double barY = (h - barH) / 2;

        // Label (left)
        if (!string.IsNullOrEmpty(Label))
        {
            var lbl = new FormattedText(
                Label!,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Medium),
                12,
                mutedFg);
            ctx.DrawText(lbl, new Point(0, (h - lbl.Height) / 2));
        }

        // Track
        var trackRect = new Rect(barL, barY, barW, barH);
        ctx.DrawRectangle(track, null, trackRect, barH / 2, barH / 2);

        // Fill
        double frac = Max > 0 ? Math.Clamp(Value / Max, 0.0, 1.0) : 0;
        if (frac > 0)
        {
            var fillRect = new Rect(barL, barY, barW * frac, barH);
            ctx.DrawRectangle(fill, null, fillRect, barH / 2, barH / 2);
        }

        // Value (right, right-aligned within its column)
        string text;
        try { text = string.Format(CultureInfo.InvariantCulture, Format, Value); }
        catch { text = Value.ToString("0.0", CultureInfo.InvariantCulture); }

        var val = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold),
            13,
            primaryFg);
        ctx.DrawText(val, new Point(w - val.Width, (h - val.Height) / 2));
    }

    static IBrush LookupBrush(string key, IBrush fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
            && value is IBrush b)
            return b;
        return fallback;
    }
}
