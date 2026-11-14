using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VocalB1.App.Controls;

/// <summary>
/// Compact bar chart of the seven formant targets (F1~F7) of the selected syllable.
/// Heights are mapped on a square-root scale so F1 (a few hundred Hz) and F7 (several kHz)
/// are both readable; the exact Hz value is printed above every bar.
/// </summary>
public sealed class FormantBarsView : FrameworkElement
{
    private const double MaxHz = 6000.0;
    private const double LabelBand = 14.0;
    private const double ValueBand = 13.0;

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(FormantBarsView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(FormantBarsView),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 8 || height < 8)
            return;

        var accent = Resource("DataBrush", Color.FromRgb(0x4C, 0x9A, 0xFF));
        var dim = Resource("DimBrush", Color.FromRgb(0x88, 0x88, 0x88));
        var grid = Resource("BorderBrush", Color.FromRgb(0x3A, 0x3A, 0x3A));
        var panel = Resource("PanelAltBrush", Color.FromRgb(0x22, 0x22, 0x22));

        dc.DrawRoundedRectangle(panel, null, new Rect(0, 0, width, height), 4, 4);

        var values = Values;
        if (values is null || values.Count == 0)
        {
            DrawText(dc, Hint ?? "未选择音节", width / 2, height / 2, dim, 10, TextAlignment.Center);
            return;
        }

        var plotTop = ValueBand;
        var plotBottom = height - LabelBand;
        var plotHeight = plotBottom - plotTop;
        if (plotHeight <= 4)
            return;

        var slot = width / values.Count;
        var barWidth = Math.Min(30.0, slot * 0.58);

        // Reference grid at 1k / 2k / 4k Hz.
        foreach (var hz in new[] { 1000.0, 2000.0, 4000.0 })
        {
            var y = plotBottom - Fraction(hz) * plotHeight;
            dc.DrawLine(new Pen(grid, 1), new Point(2, y), new Point(width - 2, y));
        }

        var fill = accent;

        for (var i = 0; i < values.Count; i++)
        {
            var hz = values[i];
            var center = slot * (i + 0.5);
            var barHeight = Math.Max(2.0, Fraction(hz) * plotHeight);
            var rect = new Rect(center - barWidth / 2, plotBottom - barHeight, barWidth, barHeight);
            dc.DrawRoundedRectangle(fill, null, rect, 2, 2);

            DrawText(dc, $"F{i + 1}", center, height - LabelBand / 2, dim, 9, TextAlignment.Center);
            if (hz > 0)
                DrawText(dc, hz.ToString("0", CultureInfo.InvariantCulture), center,
                    rect.Top - ValueBand / 2, dim, 9, TextAlignment.Center);
        }
    }

    private static double Fraction(double hz)
    {
        if (!double.IsFinite(hz) || hz <= 0)
            return 0;
        return Math.Min(1.0, Math.Sqrt(Math.Min(hz, MaxHz) / MaxHz));
    }

    private SolidColorBrush Resource(string key, Color fallback)
    {
        var brush = TryFindResource(key) as SolidColorBrush;
        var color = brush?.Color ?? fallback;
        var result = new SolidColorBrush(color);
        result.Freeze();
        return result;
    }

    private void DrawText(DrawingContext dc, string text, double centerX, double centerY,
        SolidColorBrush brush, double size, TextAlignment alignment)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var x = alignment == TextAlignment.Center ? centerX - formatted.Width / 2 : centerX;
        dc.DrawText(formatted, new Point(x, centerY - formatted.Height / 2));
    }
}
