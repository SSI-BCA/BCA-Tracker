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
    IBrush?  _upBrush;
    IBrush?  _downBrush;

    /// <summary>Optional per-point labels (e.g. match dates). Same
    /// length as <see cref="_values"/>; shown in the hover tooltip.
    /// </summary>
    string[] _labels = Array.Empty<string>();

    /// <summary>Last screen positions of the data points; populated
    /// during Render and consumed by the hover handler. Avoids
    /// recomputing geometry on every mouse move.</summary>
    Point[] _pts = Array.Empty<Point>();

    /// <summary>Index of the data point currently under the cursor,
    /// or -1 when the cursor is outside the plot. Triggers a repaint
    /// when set so the tooltip updates.</summary>
    int _hoverIndex = -1;

    /// <summary>
    /// When true (default), an upward trend in the data is rendered as a
    /// "good" colour (green-ish) and a downward trend as "danger" (red-ish).
    /// Set to false for series where lower is better (deaths, time-to-die).
    /// </summary>
    public bool HigherIsBetter { get; set; } = true;

    /// <summary>
    /// When true, the line is drawn segment-by-segment in either the
    /// <see cref="_upBrush"/> or <see cref="_downBrush"/> colour
    /// depending on whether each segment goes up or down. Transitions
    /// between adjacent segments of different colours blend smoothly
    /// via per-segment LinearGradientBrushes. Set by SetDirectionalData.
    /// </summary>
    bool _directional;

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

    public LineChart()
    {
        // Pointer events let us show a per-point tooltip on hover. We
        // don't use Avalonia's ToolTip because we want it positioned
        // next to the *data point*, not the cursor, and styled to fit
        // the chart aesthetic.
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
    }

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
        _directional = false;
        _upBrush = null;
        _downBrush = null;
        InvalidateVisual();
    }

    /// <summary>
    /// Same as SetData but with per-segment up/down colouring. Each
    /// segment of the line is drawn in <paramref name="upColor"/> if
    /// the segment goes higher than the previous point, or
    /// <paramref name="downColor"/> if it goes lower. Segments that
    /// straddle a direction change use a linear-gradient pen that
    /// blends from the previous segment's colour to this one, giving
    /// the line a soft handover instead of a hard step.
    /// </summary>
    public void SetDirectionalData(
        IEnumerable<double> values,
        IBrush upColor,
        IBrush downColor,
        string yFormat = "0.00",
        string suffix  = "")
    {
        _values  = values.ToArray();
        _yFormat = yFormat;
        _ySuffix = suffix;
        _upBrush   = upColor;
        _downBrush = downColor;
        _directional = true;
        // Clear the single-color overrides so the directional branch
        // wins at render time.
        _explicitLineBrush = null;
        _explicitFillBrush = null;
        InvalidateVisual();
    }

    /// <summary>
    /// Set the per-point hover labels. Typical use: match dates, e.g.
    /// "2026-05-20 14:33". Same order as the values array; missing
    /// entries (shorter array) just don't show in the tooltip.
    /// Call after SetData / SetDirectionalData.
    /// </summary>
    public void SetLabels(IEnumerable<string> labels)
    {
        _labels = labels.ToArray();
        InvalidateVisual();
    }

    void OnPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_pts.Length == 0) { SetHover(-1); return; }
        var pos = e.GetPosition(this);

        // Nearest point in screen space. We use squared distance so we
        // avoid Math.Sqrt for every comparison; the threshold is in
        // squared units too. 25^2 = 625 means "within ~25 pixels".
        int bestIdx = -1;
        double bestSq = 625; // 25^2 pixel radius
        for (int i = 0; i < _pts.Length; i++)
        {
            double dx = pos.X - _pts[i].X;
            double dy = pos.Y - _pts[i].Y;
            double sq = dx * dx + dy * dy;
            if (sq < bestSq) { bestSq = sq; bestIdx = i; }
        }
        SetHover(bestIdx);
    }

    void OnPointerExited(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        SetHover(-1);
    }

    void SetHover(int idx)
    {
        if (idx == _hoverIndex) return;
        _hoverIndex = idx;
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
        if (_directional && _upBrush is not null && _downBrush is not null)
        {
            // In directional mode, the actual line is drawn segment-
            // by-segment in the loop further down. The `lineBrush`
            // variable is still used for the fill area, the min/max
            // rings, and the per-point dots; pick the overall trend
            // direction's colour for those so they read as a single
            // cohesive visual rather than fighting the line colour.
            int trend = ComputeTrendDirection(_values);
            lineBrush = trend >= 0 ? _upBrush : _downBrush;
        }
        else if (_explicitLineBrush is not null)
        {
            lineBrush = _explicitLineBrush;
        }
        else
        {
            int trend = ComputeTrendDirection(_values);  // -1, 0, +1
            if (trend == 0) lineBrush = accentBrush;
            else if (HigherIsBetter ? trend > 0 : trend < 0)
                lineBrush = goodBrush;
            else
                lineBrush = dangerBrush;
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
        // Cache for hit-testing in the pointer handlers.
        _pts = pts;

        // Filled area under the line.
        if (pts.Length >= 2)
        {
            if (_directional && _upBrush is not null && _downBrush is not null)
            {
                // Mirror the per-segment colouring used for the line.
                // For each segment we render a trapezoid from
                // (pts[i-1].X, plotB) up to pts[i-1], across to pts[i],
                // and back down to (pts[i].X, plotB). Same brush logic
                // as the line below, but with reduced alpha so the
                // line stays the visual lead.
                IBrush?[] segBrush = new IBrush?[pts.Length - 1];
                for (int i = 1; i < pts.Length; i++)
                {
                    bool segUp   = pts[i].Y < pts[i - 1].Y;
                    bool segDown = pts[i].Y > pts[i - 1].Y;
                    if (segUp)        segBrush[i - 1] = _upBrush;
                    else if (segDown) segBrush[i - 1] = _downBrush;
                    else              segBrush[i - 1] = i >= 2 ? segBrush[i - 2] : _upBrush;
                }

                for (int i = 1; i < pts.Length; i++)
                {
                    IBrush? prev = i >= 2 ? segBrush[i - 2] : segBrush[i - 1];
                    IBrush  curr = segBrush[i - 1]!;
                    IBrush fillForSeg;
                    if (prev is not null && !ReferenceEquals(prev, curr))
                    {
                        // Direction change. Gradient from the previous
                        // segment's colour to the current one, with the
                        // same 0.18 alpha we use for static fills so
                        // the area blends gently into the background.
                        var prevC = ((SolidColorBrush)prev).Color;
                        var currC = ((SolidColorBrush)curr).Color;
                        fillForSeg = new LinearGradientBrush
                        {
                            StartPoint = new RelativePoint(pts[i - 1], RelativeUnit.Absolute),
                            EndPoint   = new RelativePoint(pts[i],     RelativeUnit.Absolute),
                            GradientStops =
                            {
                                new GradientStop(WithAlpha(prevC, 0.18), 0.0),
                                new GradientStop(WithAlpha(currC, 0.18), 1.0),
                            },
                        };
                    }
                    else
                    {
                        fillForSeg = MakeTransparent(curr, 0.18);
                    }

                    var trap = new PathFigure
                    {
                        StartPoint = new Point(pts[i - 1].X, plotB),
                        IsClosed   = true,
                        IsFilled   = true,
                    };
                    trap.Segments!.Add(new LineSegment { Point = pts[i - 1] });
                    trap.Segments.Add(new LineSegment { Point = pts[i] });
                    trap.Segments.Add(new LineSegment { Point = new Point(pts[i].X, plotB) });
                    var trapGeo = new PathGeometry();
                    trapGeo.Figures!.Add(trap);
                    ctx.DrawGeometry(fillForSeg, null, trapGeo);
                }
            }
            else
            {
                // Original single-colour fill path.
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
        }

        // Line.
        if (pts.Length >= 2)
        {
            if (_directional && _upBrush is not null && _downBrush is not null)
            {
                // Per-segment colouring with smooth transitions.
                //
                // For each segment, decide its "intended" colour by
                // looking at whether y went up (visually: y decreased,
                // since screen y is flipped) or down. Adjacent segments
                // that have different intended colours get a gradient
                // pen so the visual jump is smoothed; same-direction
                // adjacents use a flat brush so we avoid pointless
                // gradient overhead.
                IBrush?[] segBrush = new IBrush?[pts.Length - 1];
                for (int i = 1; i < pts.Length; i++)
                {
                    // pts[i].Y < pts[i-1].Y means screen-y decreased,
                    // which means the value went up. Equal -> use the
                    // previous segment's colour to avoid a flicker.
                    bool segUp = pts[i].Y < pts[i - 1].Y;
                    bool segDown = pts[i].Y > pts[i - 1].Y;
                    if (segUp)        segBrush[i - 1] = _upBrush;
                    else if (segDown) segBrush[i - 1] = _downBrush;
                    else              segBrush[i - 1] = i >= 2 ? segBrush[i - 2] : _upBrush;
                }

                for (int i = 1; i < pts.Length; i++)
                {
                    IBrush? prev = i >= 2 ? segBrush[i - 2] : segBrush[i - 1];
                    IBrush  curr = segBrush[i - 1]!;
                    IBrush  pen;
                    if (prev is not null && !ReferenceEquals(prev, curr))
                    {
                        // Direction change at pts[i-1]. Use a linear
                        // gradient that starts in the previous colour
                        // and ends in the current one across this
                        // segment. The next segment will draw entirely
                        // in `curr`, so the transition spans roughly
                        // one segment - short enough to look like a
                        // smooth blend rather than a hard break.
                        var lg = new LinearGradientBrush
                        {
                            StartPoint = new RelativePoint(pts[i - 1], RelativeUnit.Absolute),
                            EndPoint   = new RelativePoint(pts[i],     RelativeUnit.Absolute),
                            GradientStops =
                            {
                                new GradientStop(((SolidColorBrush)prev).Color, 0.0),
                                new GradientStop(((SolidColorBrush)curr).Color, 1.0),
                            },
                        };
                        pen = lg;
                    }
                    else
                    {
                        pen = curr;
                    }
                    var segPen = new Pen(pen, 2)
                    {
                        LineJoin = PenLineJoin.Round,
                        LineCap  = PenLineCap.Round,
                    };
                    ctx.DrawLine(segPen, pts[i - 1], pts[i]);
                }
            }
            else
            {
                // Original single-colour rendering path.
                var linePen = new Pen(lineBrush, 2)
                {
                    LineJoin = PenLineJoin.Round,
                    LineCap  = PenLineCap.Round,
                };
                for (int i = 1; i < pts.Length; i++)
                    ctx.DrawLine(linePen, pts[i - 1], pts[i]);
            }
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

        // Hover tooltip. Drawn last so it overlays everything else.
        if (_hoverIndex >= 0 && _hoverIndex < pts.Length)
        {
            var hp = pts[_hoverIndex];
            string valueText = FormatY(_values[_hoverIndex]);
            string labelText = _hoverIndex < _labels.Length ? _labels[_hoverIndex] : "";

            // Highlight the hovered point: bigger filled ring on top
            // of whatever was already drawn there.
            ctx.DrawEllipse(fgBrush, null, hp, 4.5, 4.5);
            ctx.DrawEllipse(lineBrush, null, hp, 3, 3);

            // Build the tooltip text blocks.
            var valFt = new FormattedText(
                valueText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold),
                13,
                fgBrush);
            FormattedText? labFt = null;
            if (!string.IsNullOrEmpty(labelText))
            {
                labFt = new FormattedText(
                    labelText,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    11,
                    labelBrush);
            }

            // Tooltip box dimensions.
            double pad = 8;
            double textW = Math.Max(valFt.Width, labFt?.Width ?? 0);
            double textH = valFt.Height + (labFt is not null ? labFt.Height + 2 : 0);
            double boxW = textW + pad * 2;
            double boxH = textH + pad * 2;

            // Position above the point by default. If that would clip
            // the top of the plot, position below instead.
            double boxX = hp.X - boxW / 2;
            double boxY = hp.Y - boxH - 12;
            if (boxY < plotT) boxY = hp.Y + 12;
            // Clamp horizontally so the box stays within the plot.
            if (boxX < plotL) boxX = plotL;
            if (boxX + boxW > plotR) boxX = plotR - boxW;

            // Background: dark surface with the line/series colour as a
            // 2px left accent stripe. Subtle border so the box reads
            // against the chart background.
            IBrush boxBg = new SolidColorBrush(Color.FromArgb(0xE0, 0x1F, 0x1F, 0x2A));
            ctx.DrawRectangle(boxBg, new Pen(axisBrush, 1),
                new Rect(boxX, boxY, boxW, boxH), 4, 4);
            ctx.DrawRectangle(lineBrush, null,
                new Rect(boxX, boxY, 2, boxH), 1, 1);

            // Text content.
            ctx.DrawText(valFt, new Point(boxX + pad, boxY + pad));
            if (labFt is not null)
                ctx.DrawText(labFt, new Point(boxX + pad, boxY + pad + valFt.Height + 2));
        }
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

    /// <summary>Same idea as MakeTransparent but for a bare Color
    /// value. Used by the gradient-fill code to bake the alpha into
    /// each stop without allocating an intermediate brush.</summary>
    static Color WithAlpha(Color c, double opacity)
    {
        byte alpha = (byte)Math.Clamp(opacity * 255.0, 0, 255);
        return Color.FromArgb(alpha, c.R, c.G, c.B);
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
