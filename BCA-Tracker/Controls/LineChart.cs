using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BCATracker.UI.Controls;

/// <summary>
/// Lightweight line chart drawn directly to a DrawingContext. No external
/// dependency. Designed for sparkline-style data: a single series of doubles,
/// linearly spaced on the x-axis.
///
/// Usage:
///   var chart = new LineChart();
///   chart.SetData(values, "0.00");                       // K/D
///   chart.SetData(values, "0", suffix: "%");             // accuracy
///   chart.SetData(values, "#,0", lineColor: Brushes.Red); // damage
///
/// Re-call SetData to update — the chart re-measures and re-renders.
/// </summary>
public class LineChart : Control
{
    double[] _values = Array.Empty<double>();
    string   _yFormat = "0.00";
    string   _ySuffix = "";
    IBrush   _lineBrush = Brushes.MediumPurple;
    IBrush   _fillBrush = new SolidColorBrush(Color.FromArgb(0x44, 0x8B, 0x5C, 0xF6));

    public void SetData(
        IEnumerable<double> values,
        string yFormat = "0.00",
        string suffix = "",
        IBrush? lineColor = null,
        IBrush? fillColor = null)
    {
        _values  = values.ToArray();
        _yFormat = yFormat;
        _ySuffix = suffix;
        if (lineColor is not null) _lineBrush = lineColor;
        if (fillColor is not null) _fillBrush = fillColor;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 40 || h < 40) return;

        // Look up palette brushes from app resources at draw time so theme
        // swaps update charts on next repaint.
        IBrush axisBrush  = LookupBrush("Border.Subtle",  Brushes.DimGray);
        IBrush labelBrush = LookupBrush("Fg.Muted",       Brushes.Gray);

        // Reserve space for left-side y-axis labels (~50px) and bottom
        // padding for x-axis labels (~24px).
        const double leftPad   = 50;
        const double rightPad  = 12;
        const double topPad    = 12;
        const double bottomPad = 24;

        double plotL = leftPad;
        double plotR = w - rightPad;
        double plotT = topPad;
        double plotB = h - bottomPad;
        double plotW = plotR - plotL;
        double plotH = plotB - plotT;

        // Frame
        var framePen = new Pen(axisBrush, 1);
        ctx.DrawLine(framePen, new Point(plotL, plotT), new Point(plotL, plotB));
        ctx.DrawLine(framePen, new Point(plotL, plotB), new Point(plotR, plotB));

        if (_values.Length == 0)
        {
            DrawCentered(ctx, "no data", labelBrush, plotL + plotW / 2, plotT + plotH / 2);
            return;
        }

        // Y-axis range. Always include 0 on the bottom; round max up sensibly.
        double yMax = _values.Max();
        if (yMax <= 0) yMax = 1; // avoid divide by zero
        double yMin = 0;
        // If values are very small (like K/D ratios near zero) we'd lose
        // resolution — start the y-axis just below the actual min in those
        // cases.
        if (_values.Min() < 0) yMin = _values.Min();

        // Grid + axis labels (3 horizontal grid lines: bottom, mid, top)
        var gridPen = new Pen(axisBrush, 1, dashStyle: new DashStyle(new[] { 2.0, 4.0 }, 0));
        for (int i = 0; i <= 4; i++)
        {
            double frac = i / 4.0;
            double y = plotB - frac * plotH;
            if (i > 0 && i < 4)
                ctx.DrawLine(gridPen, new Point(plotL, y), new Point(plotR, y));

            double yValue = yMin + frac * (yMax - yMin);
            string lbl = yValue.ToString(_yFormat, CultureInfo.InvariantCulture) + _ySuffix;
            DrawRightAligned(ctx, lbl, labelBrush, plotL - 6, y);
        }

        // Build the polyline points.
        var pts = new Point[_values.Length];
        for (int i = 0; i < _values.Length; i++)
        {
            double xFrac = _values.Length == 1 ? 0.5 : (double)i / (_values.Length - 1);
            double yFrac = (yMax - yMin) > 0 ? (_values[i] - yMin) / (yMax - yMin) : 0.5;
            pts[i] = new Point(plotL + xFrac * plotW, plotB - yFrac * plotH);
        }

        // Filled area under the line.
        if (pts.Length >= 2)
        {
            var fig = new PathFigure
            {
                StartPoint = new Point(pts[0].X, plotB),
                IsClosed   = true,
                IsFilled   = true,
            };
            foreach (var p in pts)
                fig.Segments!.Add(new LineSegment { Point = p });
            fig.Segments.Add(new LineSegment { Point = new Point(pts[^1].X, plotB) });
            var geo = new PathGeometry();
            geo.Figures!.Add(fig);
            ctx.DrawGeometry(_fillBrush, null, geo);
        }

        // Line itself.
        if (pts.Length >= 2)
        {
            var linePen = new Pen(_lineBrush, 2)
            {
                LineJoin = PenLineJoin.Round,
                LineCap  = PenLineCap.Round,
            };
            for (int i = 1; i < pts.Length; i++)
                ctx.DrawLine(linePen, pts[i - 1], pts[i]);
        }

        // Dots on each data point.
        foreach (var p in pts)
        {
            ctx.DrawEllipse(_lineBrush, null, p, 3, 3);
        }

        // X-axis hint labels: "oldest" and "newest"
        DrawAt(ctx, "oldest", labelBrush, plotL,           plotB + 6);
        DrawAt(ctx, "newest", labelBrush, plotR - 32,      plotB + 6);
    }

    static void DrawAt(DrawingContext ctx, string text, IBrush brush, double x, double y)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            10,
            brush);
        ctx.DrawText(ft, new Point(x, y));
    }

    static void DrawRightAligned(DrawingContext ctx, string text, IBrush brush, double rightX, double centerY)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            10,
            brush);
        double x = rightX - ft.Width;
        double y = centerY - ft.Height / 2;
        ctx.DrawText(ft, new Point(x, y));
    }

    static void DrawCentered(DrawingContext ctx, string text, IBrush brush, double cx, double cy)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            12,
            brush);
        ctx.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
    }

    static IBrush LookupBrush(string key, IBrush fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
            && value is IBrush b)
            return b;
        return fallback;
    }
}
