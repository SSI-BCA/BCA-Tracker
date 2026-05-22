using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BCATracker.UI.Controls;

/// <summary>
/// Donut chart for two-value ratios (wins vs losses, kills vs deaths, etc.).
/// Draws a thick ring with a coloured "filled" arc on top of a muted "empty"
/// arc, plus large center text showing the percentage of the foreground value.
///
/// Designed to be glanceable: the user shouldn't have to read numbers, the
/// arc length itself should communicate the ratio.
///
/// Usage:
///   <ctl:DonutChart Width="180" Height="180"
///                   FilledValue="64" TotalValue="100"
///                   FillBrush="{DynamicResource Good}"
///                   CenterText="64%"/>
///
/// Set values from code-behind:
///   donut.SetRatio(81, 127, "{0:0}%");      // 81/127 wins → "64%"
///   donut.SetRatio(81, 127, "{0:0.0}%");    // → "63.8%"
/// </summary>
public class DonutChart : Control
{
    // ── Inputs ──────────────────────────────────────────────────────────────

    public static readonly StyledProperty<double> FilledValueProperty =
        AvaloniaProperty.Register<DonutChart, double>(nameof(FilledValue), 0);

    public static readonly StyledProperty<double> TotalValueProperty =
        AvaloniaProperty.Register<DonutChart, double>(nameof(TotalValue), 1);

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<DonutChart, IBrush?>(nameof(FillBrush));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<DonutChart, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<string?> CenterTextProperty =
        AvaloniaProperty.Register<DonutChart, string?>(nameof(CenterText));

    public static readonly StyledProperty<string?> CenterSubtextProperty =
        AvaloniaProperty.Register<DonutChart, string?>(nameof(CenterSubtext));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<DonutChart, double>(nameof(Thickness), 14);

    public double FilledValue { get => GetValue(FilledValueProperty); set => SetValue(FilledValueProperty, value); }
    public double TotalValue { get => GetValue(TotalValueProperty); set => SetValue(TotalValueProperty, value); }
    public IBrush? FillBrush { get => GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public string? CenterText { get => GetValue(CenterTextProperty); set => SetValue(CenterTextProperty, value); }
    public string? CenterSubtext { get => GetValue(CenterSubtextProperty); set => SetValue(CenterSubtextProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    static DonutChart()
    {
        AffectsRender<DonutChart>(
            FilledValueProperty, TotalValueProperty,
            FillBrushProperty, TrackBrushProperty,
            CenterTextProperty, CenterSubtextProperty,
            ThicknessProperty);
    }

    /// <summary>Convenience setter: sets values and the center text in one call.</summary>
    public void SetRatio(double filled, double total, string centerFormat = "{0:0}%", string? subtext = null)
    {
        FilledValue = filled;
        TotalValue  = total;
        double pct  = total > 0 ? (filled / total) * 100.0 : 0;
        CenterText  = string.Format(CultureInfo.InvariantCulture, centerFormat, pct);
        CenterSubtext = subtext;
    }

    // ── Rendering ───────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 30 || h < 30) return;

        IBrush track  = TrackBrush ?? LookupBrush("Bg.SurfaceRaised", Brushes.DimGray);
        IBrush fill   = FillBrush  ?? LookupBrush("Accent",          Brushes.MediumPurple);
        IBrush primaryFg = LookupBrush("Fg.Primary",   Brushes.White);
        IBrush mutedFg   = LookupBrush("Fg.Muted",     Brushes.Gray);

        double size  = Math.Min(w, h);
        double cx    = w / 2;
        double cy    = h / 2;
        double thick = Math.Min(Thickness, size / 4);
        double r     = (size - thick) / 2;

        // Track ring
        var trackPen = new Pen(track, thick) { LineCap = PenLineCap.Round };
        ctx.DrawEllipse(null, trackPen, new Point(cx, cy), r, r);

        // Filled arc (only if there's a meaningful ratio)
        double total = TotalValue;
        double filled = FilledValue;
        if (total > 0 && filled > 0)
        {
            double frac = Math.Min(1.0, filled / total);
            double sweepAngle = frac * 360.0;
            DrawArc(ctx, cx, cy, r, -90, -90 + sweepAngle, fill, thick);
        }

        // Center text — large
        if (!string.IsNullOrEmpty(CenterText))
        {
            // Scale font with ring radius so it doesn't overflow on small donuts.
            double titleFontSize = Math.Max(14, size * 0.22);
            var ft = new FormattedText(
                CenterText!,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold),
                titleFontSize,
                primaryFg);
            ctx.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2 - (string.IsNullOrEmpty(CenterSubtext) ? 0 : ft.Height * 0.3)));
        }

        // Center subtext — small
        if (!string.IsNullOrEmpty(CenterSubtext))
        {
            double subtitleFontSize = Math.Max(9, size * 0.08);
            var ft = new FormattedText(
                CenterSubtext!,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                subtitleFontSize,
                mutedFg);
            ctx.DrawText(ft, new Point(cx - ft.Width / 2, cy + (Math.Max(14, size * 0.22)) * 0.15));
        }
    }

    /// <summary>
    /// Approximate an arc by stitching small line segments. Avalonia has
    /// ArcSegment for proper paths, but for a rendered ring this gives us
    /// finer control over caps and is plenty smooth at 1° steps.
    /// </summary>
    static void DrawArc(DrawingContext ctx, double cx, double cy, double r,
                        double startDeg, double endDeg, IBrush brush, double thick)
    {
        var pen = new Pen(brush, thick)
        {
            LineCap  = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        double step = 1.0;
        Point? prev = null;
        for (double a = startDeg; a <= endDeg + 0.0001; a += step)
        {
            double rad = a * Math.PI / 180.0;
            var pt = new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
            if (prev.HasValue)
                ctx.DrawLine(pen, prev.Value, pt);
            prev = pt;
        }
    }

    static IBrush LookupBrush(string key, IBrush fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
            && value is IBrush b)
            return b;
        return fallback;
    }
}
