using System.Windows;
using System.Windows.Media;

namespace WinIsland.Controls;

/// <summary>A tiny area chart used by the system monitor.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Values
    {
        get => (double[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Fixed maximum; when 0 the chart auto-scales.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var values = Values;
        var width = ActualWidth;
        var height = ActualHeight;
        if (values is null || values.Length < 2 || width <= 2 || height <= 2)
        {
            return;
        }

        var maximum = Maximum;
        if (maximum <= 0)
        {
            maximum = values.Max(v => double.IsFinite(v) ? v : 0);
            if (maximum <= 0)
            {
                maximum = 1;
            }
        }

        const double padding = 1.5;
        var chartWidth = width - padding * 2;
        var chartHeight = height - padding * 2;
        var points = new Point[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            var value = double.IsFinite(values[i]) ? values[i] : 0;
            var ratio = Math.Clamp(value / maximum, 0, 1);
            points[i] = new Point(
                padding + i * chartWidth / (values.Length - 1),
                padding + chartHeight - ratio * chartHeight);
        }

        var areaGeometry = new StreamGeometry();
        using (var context = areaGeometry.Open())
        {
            context.BeginFigure(new Point(points[0].X, height - padding), true, true);
            context.PolyLineTo(points, true, false);
            context.LineTo(new Point(points[^1].X, height - padding), true, false);
        }

        areaGeometry.Freeze();
        drawingContext.DrawGeometry(Fill, null, areaGeometry);

        var lineGeometry = new StreamGeometry();
        using (var context = lineGeometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points[1..], true, false);
        }

        lineGeometry.Freeze();
        var pen = new Pen(Stroke, 1.4) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, lineGeometry);
    }
}
