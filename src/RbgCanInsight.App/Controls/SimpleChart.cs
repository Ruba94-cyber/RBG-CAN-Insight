using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RbgCanInsight.Analysis.Analytics;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App.Controls;

/// <summary>
/// Minimal theme-aware chart that renders bars (histogram), scatter points (with an optional
/// regression line) or a polyline (spectrum) using DrawingContext, with 1-2-5 axis ticks and a
/// hover read-out that shows exact values. No external libraries.
/// </summary>
public sealed class SimpleChart : FrameworkElement
{
    private enum Kind { None, Bars, Scatter, Line }

    private const double LeftMargin = 62, BottomMargin = 38, TopMargin = 12, RightMargin = 14;
    private const int MaxScatterPoints = 6000;

    private Kind _kind = Kind.None;
    private string _message = "Nothing to show yet.";
    private string _xLabel = "", _yLabel = "";
    private double[] _edges = [];
    private double[] _barValues = [];
    private double[] _xs = [], _ys = [];
    private double[] _lineX = [], _lineY = [];
    private (double Slope, double Intercept)? _regression;
    private double[] _xTicks = [0, 1], _yTicks = [0, 1];
    private Point? _mouse;

    private Brush _background = Frozen(0xF2, 0xF4, 0xF6);
    private Brush _label = Frozen(0xFF, 0xFF, 0xFF);
    private Brush _text = Frozen(0x1F, 0x27, 0x33);
    private Brush _series = Frozen(0x2A, 0x78, 0xD6);
    private Brush _accent2 = Frozen(0xE3, 0x49, 0x48);
    private Pen _gridPen = new(Frozen(0xD8, 0xDE, 0xE6), 1);
    private Pen _axisPen = new(Frozen(0x1F, 0x27, 0x33), 1);

    public SimpleChart()
    {
        ClipToBounds = true;
        Focusable = false;
        Loaded += (_, _) => { ResolveBrushes(); ThemeManager.ThemeChanged += OnTheme; };
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnTheme;
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void OnTheme(object? sender, EventArgs e) => ResolveBrushes();

    private void ResolveBrushes()
    {
        _background = Res("CanvasBackgroundBrush", _background);
        _label = Res("CanvasLabelBrush", _label);
        _text = Res("CanvasTextBrush", _text);
        _series = Res("PlotSeries1Brush", _series);
        _accent2 = Res("PlotSeries8Brush", _accent2);
        _gridPen = new Pen(Res("CanvasGridLineBrush", _gridPen.Brush), 1);
        _axisPen = new Pen(_text, 1);
        InvalidateVisual();
    }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    // ---------------------------------------------------------------- data

    /// <summary>Shows nothing but an explanatory message (e.g. why a result cannot be computed).</summary>
    public void ShowMessage(string message)
    {
        _kind = Kind.None;
        _message = message;
        _mouse = null;
        InvalidateVisual();
    }

    /// <summary>Bars: bin i spans edges[i]..edges[i+1] with height values[i].</summary>
    public void ShowBars(double[] edges, double[] values, string xLabel, string yLabel)
    {
        _kind = Kind.Bars;
        _edges = edges; _barValues = values; _xLabel = xLabel; _yLabel = yLabel;
        double vmax = values.Length == 0 ? 1 : values.Max();
        _xTicks = ChartMath.NiceTicks(edges[0], edges[^1]);
        _yTicks = ChartMath.NiceTicks(0, vmax <= 0 ? 1 : vmax);
        _mouse = null;
        InvalidateVisual();
    }

    /// <summary>Scatter points (decimated for drawing only) with an optional y = slope*x + intercept line.</summary>
    public void ShowScatter(IReadOnlyList<(double X, double Y)> points, (double Slope, double Intercept)? regression,
        string xLabel, string yLabel)
    {
        if (points.Count == 0) { ShowMessage("No points to draw."); return; }
        _kind = Kind.Scatter;
        int stride = Math.Max(1, (points.Count + MaxScatterPoints - 1) / MaxScatterPoints);
        var xs = new List<double>(); var ys = new List<double>();
        for (int i = 0; i < points.Count; i += stride) { xs.Add(points[i].X); ys.Add(points[i].Y); }
        _xs = xs.ToArray(); _ys = ys.ToArray();
        _regression = regression; _xLabel = xLabel; _yLabel = yLabel;
        _xTicks = ChartMath.NiceTicks(_xs.Min(), _xs.Max());
        _yTicks = ChartMath.NiceTicks(_ys.Min(), _ys.Max());
        _mouse = null;
        InvalidateVisual();
    }

    public void ShowLine(double[] x, double[] y, string xLabel, string yLabel)
    {
        if (x.Length == 0) { ShowMessage("No points to draw."); return; }
        _kind = Kind.Line;
        _lineX = x; _lineY = y; _xLabel = xLabel; _yLabel = yLabel;
        _xTicks = ChartMath.NiceTicks(x[0], x[^1]);
        _yTicks = ChartMath.NiceTicks(0, Math.Max(y.Max(), 1e-12));
        _mouse = null;
        InvalidateVisual();
    }

    // --------------------------------------------------------------- input

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        InvalidateVisual();
    }

    // -------------------------------------------------------------- render

    private Rect PlotRect => new(LeftMargin, TopMargin,
        Math.Max(10, ActualWidth - LeftMargin - RightMargin), Math.Max(10, ActualHeight - TopMargin - BottomMargin));

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    private FormattedText Text(string s, double size = 11, Brush? brush = null) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size,
            brush ?? _text, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(_background, null, new Rect(RenderSize));
        if (ActualWidth < 80 || ActualHeight < 80) return;
        if (_kind == Kind.None)
        {
            var t = Text(_message, 13);
            t.MaxTextWidth = Math.Max(40, ActualWidth - 40);
            t.TextAlignment = TextAlignment.Center;
            dc.DrawText(t, new Point(20, Math.Max(10, ActualHeight / 2 - t.Height / 2)));
            return;
        }

        var plot = PlotRect;
        double x0 = _xTicks[0], x1 = _xTicks[^1], y0 = _yTicks[0], y1 = _yTicks[^1];
        double MapX(double v) => plot.Left + (v - x0) / (x1 - x0) * plot.Width;
        double MapY(double v) => plot.Bottom - (v - y0) / (y1 - y0) * plot.Height;

        foreach (var ty in _yTicks)
        {
            double py = MapY(ty);
            dc.DrawLine(_gridPen, new Point(plot.Left, py), new Point(plot.Right, py));
            var lbl = Text(F(ty));
            dc.DrawText(lbl, new Point(plot.Left - 6 - lbl.Width, py - lbl.Height / 2));
        }
        foreach (var tx in _xTicks)
        {
            double px = MapX(tx);
            dc.DrawLine(_gridPen, new Point(px, plot.Top), new Point(px, plot.Bottom));
            var lbl = Text(F(tx));
            dc.DrawText(lbl, new Point(px - lbl.Width / 2, plot.Bottom + 3));
        }
        dc.DrawLine(_axisPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
        dc.DrawLine(_axisPen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        var xl = Text(_xLabel, 11);
        dc.DrawText(xl, new Point(plot.Left + plot.Width / 2 - xl.Width / 2, ActualHeight - xl.Height - 1));
        var yl = Text(_yLabel, 11);
        dc.PushTransform(new RotateTransform(-90, 10, plot.Top + plot.Height / 2));
        dc.DrawText(yl, new Point(10 - yl.Width / 2, plot.Top + plot.Height / 2 - yl.Height / 2));
        dc.Pop();

        dc.PushClip(new RectangleGeometry(plot));
        string? hover = null;
        Point? hoverAt = null;
        switch (_kind)
        {
            case Kind.Bars:
                for (int i = 0; i < _barValues.Length; i++)
                {
                    double l = MapX(_edges[i]), r = MapX(_edges[i + 1]), top = MapY(_barValues[i]);
                    var rect = new Rect(l + 0.5, top, Math.Max(1, r - l - 1), Math.Max(0, plot.Bottom - top));
                    bool hit = _mouse is { } m && m.X >= l && m.X < r && plot.Contains(m);
                    dc.DrawRectangle(hit ? _accent2 : _series, null, rect);
                    if (r - l >= 34 && _barValues[i] > 0 && top - 14 > plot.Top)
                    {
                        var vl = Text(F(Math.Round(_barValues[i], 3)), 10);
                        dc.DrawText(vl, new Point((l + r) / 2 - vl.Width / 2, top - vl.Height - 1));
                    }
                    if (hit)
                    {
                        hover = $"[{F(_edges[i])} .. {F(_edges[i + 1])}{(i == _barValues.Length - 1 ? "]" : ")")}\n{_yLabel}: {F(_barValues[i])}";
                        hoverAt = new Point((l + r) / 2, top);
                    }
                }
                break;
            case Kind.Scatter:
                // Fit line first so the data points are always drawn on top of it.
                if (_regression is { } rg && double.IsFinite(rg.Slope))
                    dc.DrawLine(new Pen(_accent2, 2), new Point(MapX(x0), MapY(rg.Slope * x0 + rg.Intercept)),
                        new Point(MapX(x1), MapY(rg.Slope * x1 + rg.Intercept)));
                int best = -1; double bestD = 12 * 12;
                var pointPen = new Pen(_background, 1);
                for (int i = 0; i < _xs.Length; i++)
                {
                    var p = new Point(MapX(_xs[i]), MapY(_ys[i]));
                    dc.DrawEllipse(_series, pointPen, p, 3.5, 3.5);
                    if (_mouse is { } m)
                    {
                        double d = (p.X - m.X) * (p.X - m.X) + (p.Y - m.Y) * (p.Y - m.Y);
                        if (d < bestD) { bestD = d; best = i; }
                    }
                }
                if (best >= 0)
                {
                    hover = $"{_xLabel}: {F(_xs[best])}\n{_yLabel}: {F(_ys[best])}";
                    hoverAt = new Point(MapX(_xs[best]), MapY(_ys[best]));
                    dc.DrawEllipse(null, new Pen(_accent2, 2), hoverAt.Value, 5, 5);
                }
                break;
            case Kind.Line:
                var pen = new Pen(_series, 1.5);
                for (int i = 1; i < _lineX.Length; i++)
                    dc.DrawLine(pen, new Point(MapX(_lineX[i - 1]), MapY(_lineY[i - 1])), new Point(MapX(_lineX[i]), MapY(_lineY[i])));
                if (_mouse is { } mm && plot.Contains(mm) && _lineX.Length > 0)
                {
                    double fx = x0 + (mm.X - plot.Left) / plot.Width * (x1 - x0);
                    int idx = Array.BinarySearch(_lineX, fx);
                    if (idx < 0) idx = ~idx;
                    idx = Math.Clamp(idx, 0, _lineX.Length - 1);
                    if (idx > 0 && Math.Abs(_lineX[idx - 1] - fx) < Math.Abs(_lineX[idx] - fx)) idx--;
                    hover = $"{_xLabel}: {F(_lineX[idx])}\n{_yLabel}: {F(_lineY[idx])}";
                    hoverAt = new Point(MapX(_lineX[idx]), MapY(_lineY[idx]));
                    dc.DrawEllipse(_accent2, null, hoverAt.Value, 3.5, 3.5);
                }
                break;
        }
        dc.Pop();

        if (_kind == Kind.Scatter)
        {
            var cnt = Text($"{_xs.Length:N0} points", 11);
            dc.DrawText(cnt, new Point(plot.Right - cnt.Width - 8, plot.Top + 4));
        }
        else if (_kind == Kind.Bars)
        {
            var cnt = Text($"{_barValues.Length:N0} bins", 11);
            dc.DrawText(cnt, new Point(plot.Right - cnt.Width - 8, plot.Top + 4));
        }

        if (hover is not null && hoverAt is { } at)
        {
            var t = Text(hover, 11.5);
            double w = t.Width + 12, h = t.Height + 8;
            double bx = Math.Min(Math.Max(plot.Left, at.X + 10), Math.Max(plot.Left, ActualWidth - w - 2));
            double by = Math.Max(2, at.Y - h - 8);
            dc.DrawRectangle(_label, new Pen(_text, 1), new Rect(bx, by, w, h));
            dc.DrawText(t, new Point(bx + 6, by + 4));
        }
    }
}
