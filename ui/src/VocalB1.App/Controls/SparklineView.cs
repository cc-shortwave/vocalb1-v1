using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using VocalB1.App.Services;

namespace VocalB1.App.Controls;

/// <summary>
/// Minimal line chart for a modifier preview (time curve / gradient). Draws a soft area, the curve,
/// the zero line and the min/max values, so the shape of a node is visible without reading numbers.
/// </summary>
public sealed class SparklineView : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(SparklineView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(SparklineView),
        new FrameworkPropertyMetadata("该节点没有可预览的曲线", FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 8 || height < 8)
            return;

        var data = Resource("DataBrush", Color.FromRgb(0x5A, 0xA9, 0xFF));
        var dim = Resource("DimBrush", Color.FromRgb(0x9A, 0x9A, 0x9A));
        var grid = Resource("BorderBrush", Color.FromRgb(0x3A, 0x3A, 0x3A));
        var panel = Resource("PanelAltBrush", Color.FromRgb(0x2A, 0x2A, 0x2A));

        dc.DrawRoundedRectangle(panel, null, new Rect(0, 0, width, height), 4, 4);

        var values = Values;
        if (values is null || values.Count < 2)
        {
            DrawText(dc, LanguageService.Translate(EmptyText), width / 2, height / 2, dim, 10, true);
            return;
        }

        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var value in values)
        {
            if (!double.IsFinite(value))
                continue;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }
        if (!double.IsFinite(min) || !double.IsFinite(max))
            return;
        if (max - min < 1e-9)
        {
            max = min + 1;
            min -= 1;
        }

        const double pad = 10;
        var plotWidth = width - pad * 2;
        var plotHeight = height - pad * 2;

        double MapY(double value) => pad + (1 - (value - min) / (max - min)) * plotHeight;

        if (min < 0 && max > 0)
        {
            var zero = MapY(0);
            dc.DrawLine(new Pen(grid, 1), new Point(pad, zero), new Point(width - pad, zero));
        }

        var step = plotWidth / (values.Count - 1);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(pad, MapY(values[0])), true, true);
            for (var i = 1; i < values.Count; i++)
                ctx.LineTo(new Point(pad + step * i, MapY(values[i])), true, false);
        }
        geometry.Freeze();

        var outline = new StreamGeometry();
        using (var ctx = outline.Open())
        {
            ctx.BeginFigure(new Point(pad, MapY(values[0])), false, false);
            for (var i = 1; i < values.Count; i++)
                ctx.LineTo(new Point(pad + step * i, MapY(values[i])), false, false);
        }
        outline.Freeze();

        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(46, data.Color.R, data.Color.G, data.Color.B)),
            null, geometry);
        var pen = new Pen(data, 1.6);
        pen.Freeze();
        dc.DrawGeometry(null, pen, outline);

        DrawText(dc, max.ToString("0.##", CultureInfo.InvariantCulture), pad + 2, pad + 4, dim, 9, false);
        DrawText(dc, min.ToString("0.##", CultureInfo.InvariantCulture), pad + 2, height - pad - 4, dim, 9, false);
    }

    private SolidColorBrush Resource(string key, Color fallback)
    {
        var brush = TryFindResource(key) as SolidColorBrush;
        var result = new SolidColorBrush(brush?.Color ?? fallback);
        result.Freeze();
        return result;
    }

    private void DrawText(DrawingContext dc, string text, double centerX, double centerY,
        SolidColorBrush brush, double size, bool centered)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var x = centered ? centerX - formatted.Width / 2 : centerX;
        dc.DrawText(formatted, new Point(x, centerY - formatted.Height / 2));
    }
}
