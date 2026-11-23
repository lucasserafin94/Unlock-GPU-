using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UnlockGpu.Core.Model;

namespace UnlockGpu.App.Controls;

/// <summary>
/// Lightweight history graph drawn with <see cref="DrawingVisual"/> and <see cref="StreamGeometry"/>: power (left axis,
/// W), core clock (right axis, MHz) and memory clock (scaled to its own maximum), the enforced power-limit line, axis
/// labels and a hover read-out. Lines break where readings are missing.
/// </summary>
public sealed class PowerGraph : FrameworkElement
{
    const double PadLeft = 34, PadRight = 44, PadTop = 8, PadBottom = 18;
    static readonly TimeSpan GapBreak = TimeSpan.FromSeconds(10);

    readonly DrawingVisual _plot = new();
    readonly DrawingVisual _hover = new();
    readonly VisualCollection _children;
    Point? _mouse;
    Frame? _frame;

    /// <summary>Creates the control.</summary>
    public PowerGraph()
    {
        _children = new VisualCollection(this) { _plot, _hover };
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    #region dependency properties

    /// <summary>Samples, oldest first.</summary>
    public static readonly DependencyProperty SamplesProperty = Register(nameof(Samples), typeof(IReadOnlyList<GpuSample>), null);
    /// <summary>Window in minutes.</summary>
    public static readonly DependencyProperty MinutesProperty = Register(nameof(Minutes), typeof(int), 3);
    /// <summary>Plot background (also makes the whole area hit-testable for hover).</summary>
    public static readonly DependencyProperty BackgroundProperty = Register(nameof(Background), typeof(Brush), Brushes.Transparent);
    /// <summary>Power line.</summary>
    public static readonly DependencyProperty PowerBrushProperty = Register(nameof(PowerBrush), typeof(Brush), Brushes.LightGreen);
    /// <summary>Core line.</summary>
    public static readonly DependencyProperty CoreBrushProperty = Register(nameof(CoreBrush), typeof(Brush), Brushes.SkyBlue);
    /// <summary>Memory line.</summary>
    public static readonly DependencyProperty MemBrushProperty = Register(nameof(MemBrush), typeof(Brush), Brushes.Orange);
    /// <summary>Limit line.</summary>
    public static readonly DependencyProperty LimitBrushProperty = Register(nameof(LimitBrush), typeof(Brush), Brushes.IndianRed);
    /// <summary>Grid lines.</summary>
    public static readonly DependencyProperty GridBrushProperty = Register(nameof(GridBrush), typeof(Brush), Brushes.DimGray);
    /// <summary>Axis text.</summary>
    public static readonly DependencyProperty TextBrushProperty = Register(nameof(TextBrush), typeof(Brush), Brushes.Gray);
    /// <summary>Hover box background.</summary>
    public static readonly DependencyProperty TooltipBackgroundProperty = Register(nameof(TooltipBackground), typeof(Brush), Brushes.Black);
    /// <summary>Hover box text.</summary>
    public static readonly DependencyProperty TooltipForegroundProperty = Register(nameof(TooltipForeground), typeof(Brush), Brushes.White);
    /// <summary>Label for power in the read-out.</summary>
    public static readonly DependencyProperty PowerLabelProperty = Register(nameof(PowerLabel), typeof(string), "Power");
    /// <summary>Label for core in the read-out.</summary>
    public static readonly DependencyProperty CoreLabelProperty = Register(nameof(CoreLabel), typeof(string), "Core");
    /// <summary>Label for memory in the read-out.</summary>
    public static readonly DependencyProperty MemLabelProperty = Register(nameof(MemLabel), typeof(string), "Memory");
    /// <summary>Label for the limit line.</summary>
    public static readonly DependencyProperty LimitLabelProperty = Register(nameof(LimitLabel), typeof(string), "Limit");
    /// <summary>Label for the right edge of the time axis.</summary>
    public static readonly DependencyProperty NowLabelProperty = Register(nameof(NowLabel), typeof(string), "now");
    /// <summary>Text shown when there is nothing to draw.</summary>
    public static readonly DependencyProperty EmptyTextProperty = Register(nameof(EmptyText), typeof(string), "");
    /// <summary>Culture for numbers.</summary>
    public static readonly DependencyProperty CultureProperty = Register(nameof(Culture), typeof(CultureInfo), CultureInfo.InvariantCulture);

    /// <summary>Samples, oldest first.</summary>
    public IReadOnlyList<GpuSample>? Samples { get => (IReadOnlyList<GpuSample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    /// <summary>Window in minutes.</summary>
    public int Minutes { get => (int)GetValue(MinutesProperty); set => SetValue(MinutesProperty, value); }
    /// <summary>Plot background.</summary>
    public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    /// <summary>Power line.</summary>
    public Brush PowerBrush { get => (Brush)GetValue(PowerBrushProperty); set => SetValue(PowerBrushProperty, value); }
    /// <summary>Core line.</summary>
    public Brush CoreBrush { get => (Brush)GetValue(CoreBrushProperty); set => SetValue(CoreBrushProperty, value); }
    /// <summary>Memory line.</summary>
    public Brush MemBrush { get => (Brush)GetValue(MemBrushProperty); set => SetValue(MemBrushProperty, value); }
    /// <summary>Limit line.</summary>
    public Brush LimitBrush { get => (Brush)GetValue(LimitBrushProperty); set => SetValue(LimitBrushProperty, value); }
    /// <summary>Grid lines.</summary>
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    /// <summary>Axis text.</summary>
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    /// <summary>Hover box background.</summary>
    public Brush TooltipBackground { get => (Brush)GetValue(TooltipBackgroundProperty); set => SetValue(TooltipBackgroundProperty, value); }
    /// <summary>Hover box text.</summary>
    public Brush TooltipForeground { get => (Brush)GetValue(TooltipForegroundProperty); set => SetValue(TooltipForegroundProperty, value); }
    /// <summary>Label for power.</summary>
    public string PowerLabel { get => (string)GetValue(PowerLabelProperty); set => SetValue(PowerLabelProperty, value); }
    /// <summary>Label for core.</summary>
    public string CoreLabel { get => (string)GetValue(CoreLabelProperty); set => SetValue(CoreLabelProperty, value); }
    /// <summary>Label for memory.</summary>
    public string MemLabel { get => (string)GetValue(MemLabelProperty); set => SetValue(MemLabelProperty, value); }
    /// <summary>Label for the limit line.</summary>
    public string LimitLabel { get => (string)GetValue(LimitLabelProperty); set => SetValue(LimitLabelProperty, value); }
    /// <summary>Label for "now".</summary>
    public string NowLabel { get => (string)GetValue(NowLabelProperty); set => SetValue(NowLabelProperty, value); }
    /// <summary>Text shown when there is nothing to draw.</summary>
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }
    /// <summary>Culture for numbers.</summary>
    public CultureInfo Culture { get => (CultureInfo)GetValue(CultureProperty); set => SetValue(CultureProperty, value); }

    static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(PowerGraph),
            new FrameworkPropertyMetadata(defaultValue, (d, _) => ((PowerGraph)d).Redraw()));

    #endregion

    /// <inheritdoc/>
    protected override int VisualChildrenCount => _children.Count;

    /// <inheritdoc/>
    protected override Visual GetVisualChild(int index) => _children[index];

    /// <inheritdoc/>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Redraw();
    }

    /// <inheritdoc/>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // Text is laid out for a given pixels-per-DIP; redraw so it stays sharp after a move to another monitor.
        Redraw();
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        DrawHover();
    }

    /// <inheritdoc/>
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        DrawHover();
    }

    /// <summary>Scales and visible samples of the last drawing (used by the hover read-out).</summary>
    sealed record Frame(Rect Plot, DateTime Start, DateTime End, double MaxW, double MaxCore, double MaxMem, IReadOnlyList<GpuSample> Visible)
    {
        public double X(DateTime t) => Plot.Left + (Plot.Width * (t - Start).TotalMilliseconds / Math.Max(1, (End - Start).TotalMilliseconds));
        public double Y(double v, double max) => Plot.Bottom - (Plot.Height * Math.Clamp(v / max, 0, 1));
    }

    void Redraw()
    {
        double w = ActualWidth, h = ActualHeight;
        using var dc = _plot.RenderOpen();
        dc.DrawRectangle(Background, null, new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)));
        _frame = null;
        if (w < PadLeft + PadRight + 20 || h < PadTop + PadBottom + 20) return;

        var plot = new Rect(PadLeft, PadTop, w - PadLeft - PadRight, h - PadTop - PadBottom);
        var all = Samples ?? [];
        int minutes = Math.Max(1, Minutes);
        DateTime end = all.Count > 0 ? all[^1].Time : DateTime.Now;
        DateTime start = end - TimeSpan.FromMinutes(minutes);
        var visible = all.Where(s => s.Time >= start).ToArray();
        var ok = visible.Where(s => s.Ok).ToArray();

        double limit = ok.Length > 0 ? ok[^1].LimitW : double.NaN;
        double peakW = ok.Select(s => s.PowerW).Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max();
        double maxW = RoundUp(Math.Max(Math.Max(peakW, double.IsNaN(limit) ? 0 : limit) * 1.1, 50), 25);
        double maxCore = RoundUp(Math.Max(ok.Select(s => (double)s.CoreMHz).DefaultIfEmpty(0).Max() * 1.1, 1000), 500);
        double maxMem = RoundUp(Math.Max(ok.Select(s => (double)s.MemMHz).DefaultIfEmpty(0).Max() * 1.1, 1000), 1000);
        var frame = new Frame(plot, start, end, maxW, maxCore, maxMem, visible);
        _frame = frame;

        var grid = new Pen(GridBrush, 1) { DashStyle = new DashStyle([2, 3], 0) };
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var culture = Culture ?? CultureInfo.InvariantCulture;
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Bottom - (plot.Height * i / 4.0);
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            if (i % 2 == 0)
            {
                DrawText(dc, (maxW * i / 4.0).ToString("0", culture) + " W", new Point(plot.Left - 4, y), TextBrush, 10, dip, TextAlignment.Right);
                DrawText(dc, (maxCore * i / 4.0).ToString("#,0", culture), new Point(plot.Right + 4, y), CoreBrush, 10, dip, TextAlignment.Left);
            }
        }
        for (int m = 0; m <= minutes; m++)
        {
            double x = plot.Right - (plot.Width * m / minutes);
            if (minutes > 3 && m % 2 == 1 && m != minutes) continue;
            dc.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            string label = m == 0 ? NowLabel : "-" + m.ToString(culture) + " min";
            var align = m == 0 ? TextAlignment.Right : m == minutes ? TextAlignment.Left : TextAlignment.Center;
            DrawText(dc, label, new Point(x, plot.Bottom + 9), TextBrush, 10, dip, align);
        }

        if (ok.Length == 0)
        {
            if (!string.IsNullOrEmpty(EmptyText))
                DrawText(dc, EmptyText, new Point(plot.Left + (plot.Width / 2), plot.Top + (plot.Height / 2)), TextBrush, 12, dip, TextAlignment.Center);
            DrawHover();
            return;
        }

        dc.PushClip(new RectangleGeometry(plot));
        DrawSeries(dc, frame, visible, s => (double)s.MemMHz, maxMem, MemBrush, 1.3);
        DrawSeries(dc, frame, visible, s => (double)s.CoreMHz, maxCore, CoreBrush, 1.3);
        DrawSeries(dc, frame, visible, s => s.PowerW, maxW, PowerBrush, 2.0);
        if (!double.IsNaN(limit) && limit > 0)
        {
            double y = frame.Y(limit, maxW);
            var pen = new Pen(LimitBrush, 1.2) { DashStyle = new DashStyle([6, 4], 0) };
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(dc, LimitLabel + " " + limit.ToString("0", culture) + " W", new Point(plot.Right - 4, Math.Max(plot.Top + 7, y - 8)),
                LimitBrush, 10, dip, TextAlignment.Right);
        }
        dc.Pop();
        DrawHover();
    }

    static void DrawSeries(DrawingContext dc, Frame f, IReadOnlyList<GpuSample> samples, Func<GpuSample, double> value, double max, Brush brush, double thickness)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool open = false;
            DateTime prev = DateTime.MinValue;
            foreach (var s in samples)
            {
                double v = s.Ok ? value(s) : double.NaN;
                if (double.IsNaN(v)) { open = false; continue; }
                var p = new Point(f.X(s.Time), f.Y(v, max));
                if (!open || s.Time - prev > GapBreak) { ctx.BeginFigure(p, false, false); open = true; }
                else ctx.LineTo(p, true, false);
                prev = s.Time;
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(brush, thickness) { LineJoin = PenLineJoin.Round }, geo);
    }

    void DrawHover()
    {
        using var dc = _hover.RenderOpen();
        var f = _frame;
        if (_mouse is not Point m || f == null || f.Visible.Count == 0 || !f.Plot.Contains(m)) return;

        GpuSample? best = null;
        double bestDx = double.MaxValue;
        foreach (var s in f.Visible)
        {
            if (!s.Ok) continue;
            double dx = Math.Abs(f.X(s.Time) - m.X);
            if (dx < bestDx) { bestDx = dx; best = s; }
        }
        if (best == null) return;

        double x = f.X(best.Time);
        dc.DrawLine(new Pen(TextBrush, 1), new Point(x, f.Plot.Top), new Point(x, f.Plot.Bottom));
        if (!double.IsNaN(best.PowerW)) dc.DrawEllipse(PowerBrush, null, new Point(x, f.Y(best.PowerW, f.MaxW)), 3, 3);
        dc.DrawEllipse(CoreBrush, null, new Point(x, f.Y(best.CoreMHz, f.MaxCore)), 2.5, 2.5);
        dc.DrawEllipse(MemBrush, null, new Point(x, f.Y(best.MemMHz, f.MaxMem)), 2.5, 2.5);

        var culture = Culture ?? CultureInfo.InvariantCulture;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string text = best.Time.ToString("T", culture) + Environment.NewLine
            + PowerLabel + ": " + (double.IsNaN(best.PowerW) ? "—" : best.PowerW.ToString("0.0", culture)) + " W" + Environment.NewLine
            + CoreLabel + ": " + best.CoreMHz.ToString("#,0", culture) + " MHz" + Environment.NewLine
            + MemLabel + ": " + best.MemMHz.ToString("#,0", culture) + " MHz";
        var ft = Text(text, TooltipForeground, 11, dip);
        double bw = ft.Width + 12, bh = ft.Height + 8;
        double bx = x + 10 + bw > f.Plot.Right ? x - 10 - bw : x + 10;
        double by = Math.Clamp(m.Y - (bh / 2), f.Plot.Top, Math.Max(f.Plot.Top, f.Plot.Bottom - bh));
        dc.DrawRoundedRectangle(TooltipBackground, new Pen(GridBrush, 1), new Rect(bx, by, bw, bh), 4, 4);
        dc.DrawText(ft, new Point(bx + 6, by + 4));
    }

    FormattedText Text(string s, Brush brush, double size, double dip) =>
        new(s, Culture ?? CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, dip);

    void DrawText(DrawingContext dc, string s, Point anchor, Brush brush, double size, double dip, TextAlignment align)
    {
        var ft = Text(s, brush, size, dip);
        double x = align switch
        {
            TextAlignment.Right => anchor.X - ft.Width,
            TextAlignment.Center => anchor.X - (ft.Width / 2),
            _ => anchor.X,
        };
        dc.DrawText(ft, new Point(x, anchor.Y - (ft.Height / 2)));
    }

    static double RoundUp(double v, double step) => Math.Ceiling(v / step) * step;
}
