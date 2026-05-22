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
/// Visual additions over the basic version:
///   - Average reference line (dashed) across the chart so each point can be
///     read as "above/below my norm" at a glance.
///   - Last-value badge in the top-right with the most recent reading and
///     a ▲/▼ delta vs the average.
///   - Min/max points highlighted with hollow rings.
///   - Line color shifts toward green when the trend (last vs first half) is
///     improving and red when worsening; neutral otherwise.
///
/// Usage:
///   var chart = new LineChart();
///   chart.SetData(values, "0.00");
///   chart.SetData(values, "0", suffix: "%");
///   chart.SetData(values, "#,0", lineColor: Brushes.Red);
///   chart.HigherIsBetter = false;       // for "deaths" or "match length"
///
/// Re-call SetData to update.
/// </summary>
public class LineChart : Control
{
    double[] _values   = Array.Empty<double>();
    string   _yFormat  = "0.00";
    string   _ySuffix  = "";
    IBrush?  _explicitLineBrush;
    IBrush?  _explicitFillBrush;

    /// <summary>
    /// When true (default), an upward trend in the data is rendered as a
    /// "good" colour (green-ish) and a downward trend as "danger" (red-ish).
    /// Set to false for series where lower is better (deaths, time-to-die).
    /// </summary>
    public bool HigherIsBetter { get; set; } = true;

    /// <summary>
    /// Whether to draw the dashed average reference line across the plot.
    /// On by default.
    /// </summary>
    public bool ShowAverageLine { get; set; } = true;

    /// <summary>
    /// Whether to draw a small badge in the top-right showing the latest
    /// value and its delta vs the average. On by default.
    /// </summary>
    public bool ShowLastValueBadge { get; set; } = true;

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
        _explicitLineBrush = lineColor;
        _explicitFillBrush = fillColor;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 60 || h < 60) return;

        // Theme brushes.
        IBrush axisBrush  = LookupBrush("Border.Subtle", Brushes.DimGray);
        IBrush labelBrush = LookupBrush("Fg.Muted",      Brushes.Gray);
        IBrush fgBrush    = LookupBrush("Fg.Primary",    Brushes.White);
        IBrush goodBrush  = LookupBrush("Good",          new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)));
        IBrush dangerBrush = LookupBrush("Danger",       new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
        IBrush accentBrush = LookupBrush("Accent",       new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)));

        // Plot bounds.
        const double leftPad   = 50;
        const double rightPad  = 12;
        const double topPad    = 18;   // a bit more room than before for the badge
        const double bottomPad = 24;
        double plotL = leftPad;
        double plotR = w - rightPad;
        double plotT = topPad;
        double plotB = h - bottomPad;
        double plotW = plotR - plotL;
        double plotH = plotB - plotT;

        // Empty state.
        if (_values.Length == 0)
        {
            DrawCentered(ctx, "no data", labelBrush, plotL + plotW / 2, plotT + plotH / 2);
            DrawFrame(ctx, plotL, plotR, plotT, plotB, axisBrush);
            return;
        }

        // Decide line/fill colours: caller-provided > trend-based > accent default.
        IBrush lineBrush;
        if (_explicitLineBrush is not null)
        {
            lineBrush = _explicitLineBrush;
        }
        else
        {
            int trend = ComputeTrendDirection(_values);  // -1, 0, +1
            if (trend == 0)        lineBrush = accentBrush;
            else if (HigherIsBetter ? trend > 0 : trend < 0)
                                   lineBrush = goodBrush;
            else                   lineBrush = dangerBrush;
        }
        IBrush fillBrush = _explicitFillBrush ?? MakeTransparent(lineBrush, 0.18);

        // Y range.
        double yMax = _values.Max();
        if (yMax <= 0) yMax = 1;
        double yMin = 0;
        if (_values.Min() < 0) yMin = _values.Min();
        // Add a little headroom so the line doesn't kiss the top.
        yMax += (yMax - yMin) * 0.10;
        if (yMax == yMin) yMax = yMin + 1;

        // Frame + grid + y-axis labels.
        DrawFrame(ctx, plotL, plotR, plotT, plotB, axisBrush);
        var gridPen = new Pen(axisBrush, 1, dashStyle: new DashStyle(new[] { 2.0, 4.0 }, 0));
        for (int i = 0; i <= 4; i++)
        {
            double frac = i / 4.0;
            double y = plotB - frac * plotH;
            if (i > 0 && i < 4)
                ctx.DrawLine(gridPen, new Point(plotL, y), new Point(plotR, y));
            double yValue = yMin + frac * (yMax - yMin);
            DrawRightAligned(ctx, FormatY(yValue), labelBrush, plotL - 6, y);
        }

        // Average reference line.
        double avg = _values.Average();
        if (ShowAverageLine && _values.Length >= 2)
        {
            double avgFrac = (avg - yMin) / (yMax - yMin);
            double avgY = plotB - avgFrac * plotH;
            var avgPen = new Pen(labelBrush, 1, dashStyle: new DashStyle(new[] { 4.0, 4.0 }, 0));
            ctx.DrawLine(avgPen, new Point(plotL, avgY), new Point(plotR, avgY));
            // "avg" label at the right edge above the line.
            DrawAt(ctx, "avg " + FormatY(avg), labelBrush, plotR - 50, avgY - 14);
        }

        // Build polyline points.
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
            ctx.DrawGeometry(fillBrush, null, geo);
        }

        // Line.
        if (pts.Length >= 2)
        {
            var linePen = new Pen(lineBrush, 2)
            {
                LineJoin = PenLineJoin.Round,
                LineCap  = PenLineCap.Round,
            };
            for (int i = 1; i < pts.Length; i++)
                ctx.DrawLine(linePen, pts[i - 1], pts[i]);
        }

        // Min/max highlights — hollow rings on the extremes.
        int minIdx = IndexOfMin(_values);
        int maxIdx = IndexOfMax(_values);
        var ringPen = new Pen(lineBrush, 1.5);
        if (pts.Length > 1)
        {
            ctx.DrawEllipse(null, ringPen, pts[minIdx], 5, 5);
            ctx.DrawEllipse(null, ringPen, pts[maxIdx], 5, 5);
        }

        // Solid dot on every point (smaller than the rings so they don't fight).
        foreach (var p in pts)
        {
            ctx.DrawEllipse(lineBrush, null, p, 2.5, 2.5);
        }

        // Last-value badge in the top-right.
        if (ShowLastValueBadge && pts.Length >= 1)
        {
            double last = _values[^1];
            string lastText = FormatY(last);

            string deltaText = "";
            IBrush deltaBrush = labelBrush;
            if (_values.Length >= 2)
            {
                double delta = last - avg;
                bool up = delta > 0;
                bool down = delta < 0;
                bool good = HigherIsBetter ? up : down;
                bool bad  = HigherIsBetter ? down : up;
                string arrow = up ? "▲" : (down ? "▼" : "·");
                deltaText = $" {arrow} {Math.Abs(delta).ToString(_yFormat, CultureInfo.InvariantCulture)}{_ySuffix}";
                if (good) deltaBrush = goodBrush;
                else if (bad) deltaBrush = dangerBrush;
            }

            var badgeLast = new FormattedText(
                lastText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold),
                13,
                fgBrush);
            var badgeDelta = new FormattedText(
                deltaText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                11,
                deltaBrush);

            double badgeX = plotR - badgeLast.Width - badgeDelta.Width;
            double badgeY = plotT - 14;
            ctx.DrawText(badgeLast,  new Point(badgeX, badgeY));
            ctx.DrawText(badgeDelta, new Point(badgeX + badgeLast.Width, badgeY + 2));
        }

        // X-axis hints.
        DrawAt(ctx, "oldest", labelBrush, plotL,      plotB + 6);
        DrawAt(ctx, "newest", labelBrush, plotR - 32, plotB + 6);
    }

    string FormatY(double v)
        => v.ToString(_yFormat, CultureInfo.InvariantCulture) + _ySuffix;

    static void DrawFrame(DrawingContext ctx, double l, double r, double t, double b, IBrush brush)
    {
        var pen = new Pen(brush, 1);
        ctx.DrawLine(pen, new Point(l, t), new Point(l, b));
        ctx.DrawLine(pen, new Point(l, b), new Point(r, b));
    }

    /// <summary>Returns -1 / 0 / +1 by comparing the average of the second
    /// half of the series to the first half. 0 means flat (≤2% delta).</summary>
    static int ComputeTrendDirection(double[] values)
    {
        if (values.Length < 4) return 0;
        int half = values.Length / 2;
        double a = values.Take(half).Average();
        double b = values.Skip(half).Average();
        if (a == 0) return Math.Sign(b);
        double rel = (b - a) / Math.Abs(a);
        if (rel > 0.02) return +1;
        if (rel < -0.02) return -1;
        return 0;
    }

    static int IndexOfMin(double[] arr)
    {
        int idx = 0;
        for (int i = 1; i < arr.Length; i++) if (arr[i] < arr[idx]) idx = i;
        return idx;
    }
    static int IndexOfMax(double[] arr)
    {
        int idx = 0;
        for (int i = 1; i < arr.Length; i++) if (arr[i] > arr[idx]) idx = i;
        return idx;
    }

    static IBrush MakeTransparent(IBrush b, double opacity)
    {
        if (b is SolidColorBrush scb)
        {
            var c = scb.Color;
            byte alpha = (byte)Math.Clamp(opacity * 255.0, 0, 255);
            return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }
        return new SolidColorBrush(Color.FromArgb(40, 0x8B, 0x5C, 0xF6));
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
        ctx.DrawText(ft, new Point(rightX - ft.Width, centerY - ft.Height / 2));
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
