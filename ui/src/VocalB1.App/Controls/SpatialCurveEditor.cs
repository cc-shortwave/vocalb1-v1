using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VocalB1.App.Services;
using VocalB1.Core.Models;

namespace VocalB1.App.Controls;

/// <summary>
/// Editable X/Y/Z spatial trajectory for one singer. Three stacked lanes share a beat axis:
/// X = pan left/right, Y = distance, Z = height. Left click adds or drags a keyframe,
/// right click removes one; edits are written straight into <see cref="Trajectory"/>.
/// </summary>
public sealed class SpatialCurveEditor : FrameworkElement
{
    private const double GutterWidth = 66;
    private const double RightPad = 8;
    private const double TopPad = 6;
    private const double BottomPad = 6;
    private const double LaneGap = 8;
    private const double NodeRadius = 4.5;
    private const double HitRadius = 9;
    private const double BeatSnap = 0.25;
    private const double MinBeats = 8;
    private const double DefaultBeats = 16;

    private static readonly Typeface TextFace = new(
        new FontFamily("Microsoft YaHei UI, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Color[] AxisColors =
    [
        Color.FromRgb(0x4F, 0xA3, 0xFF), // X: pan
        Color.FromRgb(0x5E, 0xCB, 0x9B), // Y: distance
        Color.FromRgb(0xE8, 0xA3, 0x3D), // Z: height
    ];

    private static readonly string[] AxisTitles = ["X 声像", "Y 远近", "Z 高度"];

    private Brush _panelFill = Brushes.Transparent;
    private Brush _labelBrush = Brushes.Gray;
    private Brush _textBrush = Brushes.White;
    private Pen _borderPen = new();
    private Pen _gridPen = new();
    private Pen _centerPen = new();
    private Pen _hintPen = new();

    private (int Axis, SpatialPoint Point)? _drag;

    public SpatialCurveEditor()
    {
        Focusable = true;
        ClipToBounds = true;
        RefreshThemeResources();
        Loaded += (_, _) =>
        {
            ThemeManager.Changed += OnThemeChanged;
            LanguageService.Changed += OnLanguageChanged;
        };
        Unloaded += (_, _) =>
        {
            ThemeManager.Changed -= OnThemeChanged;
            LanguageService.Changed -= OnLanguageChanged;
        };
    }

    public static readonly DependencyProperty TrajectoryProperty = DependencyProperty.Register(
        nameof(Trajectory), typeof(SpatialTrajectory), typeof(SpatialCurveEditor),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender, OnTrajectoryChanged));

    public static readonly DependencyProperty TotalBeatsProperty = DependencyProperty.Register(
        nameof(TotalBeats), typeof(double), typeof(SpatialCurveEditor),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Trajectory being edited (bound to the selected singer).</summary>
    public SpatialTrajectory? Trajectory
    {
        get => (SpatialTrajectory?)GetValue(TrajectoryProperty);
        set => SetValue(TrajectoryProperty, value);
    }

    /// <summary>Beat span of the bound lyric; a shorter score falls back to a usable default width.</summary>
    public double TotalBeats
    {
        get => (double)GetValue(TotalBeatsProperty);
        set => SetValue(TotalBeatsProperty, value);
    }

    private static void OnTrajectoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpatialCurveEditor editor)
            editor._drag = null;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        RefreshThemeResources();
        InvalidateVisual();
    }

    private void RefreshThemeResources()
    {
        var light = ThemeManager.IsLight;
        _panelFill = BrushOf("InputBrush", light ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x17, 0x17, 0x17));
        _labelBrush = BrushOf("DimBrush", light ? Color.FromRgb(0x6B, 0x6B, 0x6B) : Color.FromRgb(0x9A, 0x9A, 0x9A));
        _textBrush = BrushOf("TextBrush", light ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Color.FromRgb(0xE4, 0xE4, 0xE4));
        _borderPen = PenOf("BorderBrush", light ? Color.FromRgb(0xC8, 0xC8, 0xC8) : Color.FromRgb(0x3A, 0x3A, 0x3A), 1);
        _gridPen = PenOf(null, light ? Color.FromRgb(0xE8, 0xE8, 0xE8) : Color.FromRgb(0x27, 0x27, 0x27), 1);
        _centerPen = PenOf(null, light ? Color.FromRgb(0xB0, 0xB0, 0xB0) : Color.FromRgb(0x4A, 0x4A, 0x4A), 1);
        _hintPen = PenOf(null, light ? Color.FromRgb(0xD0, 0xD0, 0xD0) : Color.FromRgb(0x38, 0x38, 0x38), 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        // A filled background also makes the whole control hit-testable for click-to-add.
        dc.DrawRoundedRectangle(_panelFill, _borderPen, new Rect(0.5, 0.5, width - 1, height - 1), 4, 4);

        var layout = GetLayout();
        if (layout.LaneHeight <= 2)
            return;

        var beats = GetBeats();
        for (var axis = 0; axis < 3; axis++)
        {
            var lane = layout.Lane(axis);
            DrawLane(dc, axis, lane, layout, beats);
        }
    }

    private void DrawLane(DrawingContext dc, int axis, Rect lane, Layout layout, double beats)
    {
        dc.DrawRoundedRectangle(null, _hintPen, lane, 3, 3);

        // Beat grid; every fourth beat is emphasised.
        for (var beat = 0; beat <= beats + 1e-6; beat += 1)
        {
            var x = layout.X(beat, beats);
            dc.DrawLine(Math.Abs(beat % 4) < 1e-6 ? _centerPen : _gridPen,
                new Point(x, lane.Top), new Point(x, lane.Bottom));
        }

        var centerY = lane.Top + lane.Height / 2;
        dc.DrawLine(_centerPen, new Point(layout.PlotLeft, centerY), new Point(layout.PlotRight, centerY));

        var label = Text(LanguageService.Translate(AxisTitles[axis]), 11, _textBrush);
        dc.DrawText(label, new Point(8, lane.Top + 5));

        var points = Points(axis);
        var stroke = new Pen(Brush(AxisColors[axis]), 1.6);
        stroke.Freeze();

        Point Position(SpatialPoint p) => new(layout.X(Math.Clamp(p.Beat, 0, beats), beats),
            layout.Y(Math.Clamp(p.Value, -1, 1), lane));

        if (points.Count == 0)
        {
            var hint = Text(LanguageService.Translate("无关键帧（居中）"), 10, _labelBrush);
            dc.DrawText(hint, new Point(layout.PlotLeft + 8, lane.Top + 4));
            return;
        }

        if (points.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(Position(points[0]), false, false);
                for (var i = 1; i < points.Count; i++)
                    ctx.LineTo(Position(points[i]), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, stroke, geometry);
        }

        foreach (var point in points)
        {
            var center = Position(point);
            var selected = _drag is { } drag && ReferenceEquals(drag.Point, point);
            var fill = selected ? _textBrush : Brush(AxisColors[axis]);
            dc.DrawEllipse(fill, null, center, NodeRadius, NodeRadius);
        }
    }

    // ------------------------------------------------------------------
    // Editing
    // ------------------------------------------------------------------
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var trajectory = Trajectory;
        if (trajectory is null)
            return;

        var layout = GetLayout();
        var beats = GetBeats();
        var position = e.GetPosition(this);

        if (TryHit(position, layout, beats, out var hit))
        {
            _drag = hit;
        }
        else
        {
            var axis = layout.AxisAt(position.Y);
            if (axis < 0)
                return;

            var lane = layout.Lane(axis);
            var point = SpatialTrajectory.CreatePoint(
                Math.Clamp(SnapBeat(layout.BeatAt(position.X, beats)), 0, beats),
                Math.Clamp(1 - 2 * (position.Y - lane.Top) / Math.Max(1, lane.Height), -1, 1));
            point.Value = Math.Round(point.Value, 3);
            Points(axis).Add(point);
            Sort(axis);
            _drag = (axis, point);
        }

        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag is not { } drag)
            return;

        var layout = GetLayout();
        var beats = GetBeats();
        var position = e.GetPosition(this);
        var lane = layout.Lane(drag.Axis);

        drag.Point.Beat = Math.Clamp(SnapBeat(layout.BeatAt(position.X, beats)), 0, beats);
        drag.Point.Value = Math.Round(
            Math.Clamp(1 - 2 * (position.Y - lane.Top) / Math.Max(1, lane.Height), -1, 1), 3);
        Sort(drag.Axis);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        _drag = null;
        Trajectory?.Normalize();
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        var layout = GetLayout();
        var beats = GetBeats();
        if (!TryHit(e.GetPosition(this), layout, beats, out var hit))
            return;

        if (Points(hit.Axis).Remove(hit.Point))
        {
            InvalidateVisual();
            e.Handled = true;
        }
    }

    private bool TryHit(Point position, Layout layout, double beats,
        out (int Axis, SpatialPoint Point) hit)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            var lane = layout.Lane(axis);
            foreach (var point in Points(axis))
            {
                var center = new Point(layout.X(Math.Clamp(point.Beat, 0, beats), beats),
                    layout.Y(Math.Clamp(point.Value, -1, 1), lane));
                if ((center - position).Length <= HitRadius)
                {
                    hit = (axis, point);
                    return true;
                }
            }
        }

        hit = default;
        return false;
    }

    private static double SnapBeat(double beat) =>
        Math.Round(beat / BeatSnap, MidpointRounding.AwayFromZero) * BeatSnap;

    private double GetBeats()
    {
        var beats = double.IsFinite(TotalBeats) && TotalBeats > 0 ? TotalBeats : DefaultBeats;
        return Math.Max(MinBeats, beats);
    }

    private List<SpatialPoint> Points(int axis)
    {
        var trajectory = Trajectory;
        if (trajectory is null)
            return [];

        List<SpatialPoint>? list = axis switch
        {
            0 => trajectory.X,
            1 => trajectory.Y,
            _ => trajectory.Z,
        };

        if (list is null)
        {
            list = [];
            switch (axis)
            {
                case 0: trajectory.X = list; break;
                case 1: trajectory.Y = list; break;
                default: trajectory.Z = list; break;
            }
        }

        return list;
    }

    private void Sort(int axis) => Points(axis).Sort((a, b) => a.Beat.CompareTo(b.Beat));

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------
    private Layout GetLayout()
    {
        var height = ActualHeight;
        var laneHeight = Math.Max(0, (height - TopPad - BottomPad - 2 * LaneGap) / 3);
        return new Layout(GutterWidth, ActualWidth - RightPad, TopPad, laneHeight);
    }

    private readonly record struct Layout(double PlotLeft, double PlotRight, double Top, double LaneHeight)
    {
        public Rect Lane(int axis)
        {
            var top = Top + axis * (LaneHeight + LaneGap);
            return new Rect(PlotLeft, top, Math.Max(0, PlotRight - PlotLeft), LaneHeight);
        }

        public int AxisAt(double y)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                var lane = Lane(axis);
                if (y >= lane.Top - LaneGap / 2 && y <= lane.Bottom + LaneGap / 2)
                    return axis;
            }
            return -1;
        }

        public double X(double beat, double beats) =>
            PlotLeft + Math.Max(0, PlotRight - PlotLeft) * Math.Clamp(beat / Math.Max(1e-9, beats), 0, 1);

        public double BeatAt(double x, double beats)
        {
            var width = Math.Max(1e-9, PlotRight - PlotLeft);
            return Math.Clamp((x - PlotLeft) / width, 0, 1) * beats;
        }

        public double Y(double value, Rect lane) =>
            lane.Top + lane.Height / 2 * (1 - Math.Clamp(value, -1, 1));
    }

    // ------------------------------------------------------------------
    // Drawing helpers
    // ------------------------------------------------------------------
    private FormattedText Text(string text, double size, Brush brush) => new(
        text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, size, brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush BrushOf(string key, Color fallback) =>
        Freeze(Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback));

    private static Pen PenOf(string? key, Color fallback, double thickness)
    {
        var brush = key is null
            ? new SolidColorBrush(fallback)
            : Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static Brush Freeze(Brush brush)
    {
        if (brush.CanFreeze && !brush.IsFrozen)
            brush.Freeze();
        return brush;
    }
}
