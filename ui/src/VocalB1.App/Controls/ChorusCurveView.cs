using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VocalB1.App.Services;
using VocalB1.Core.Chorus;
using VocalB1.Core.Models;

namespace VocalB1.App.Controls;

/// <summary>
/// Blender-style curve editor for the selected singer's path: the X and Y channels over the beat axis.
/// Values are drawn with one shared meter axis so both channels stay comparable; dragging a keyframe
/// retimes it and edits only the channel whose handle was grabbed. Every edit writes straight into the
/// model, so the stage and the renderer always see the same numbers.
/// </summary>
public sealed class ChorusCurveView : FrameworkElement
{
    private const double OuterPadding = 10;
    private const double ValueGutter = 46;
    private const double BeatGutter = 22;
    private const double KeyRadius = 4.2;
    private const double KeyHitRadius = 9;
    private const double HandleHalo = 9;

    private static readonly Typeface TextFace = new(
        new FontFamily("Microsoft YaHei UI, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private Brush _panelFill = Brushes.Transparent;
    private Brush _plotFill = Brushes.Transparent;
    private Brush _textBrush = Brushes.White;
    private Brush _dimBrush = Brushes.Gray;
    private Brush _cardFill = Brushes.Transparent;
    private Pen _plotPen = new();
    private Pen _gridPen = new();
    private Pen _gridMajorPen = new();
    private Pen _zeroPen = new();
    private Pen _playheadPen = new();
    private Pen _cardPen = new();
    private Brush _playheadBrush = Brushes.White;

    private bool _isLight;

    // Plot rectangle in device-independent pixels.
    private double _plotLeft;
    private double _plotTop;
    private double _plotRight;
    private double _plotBottom;

    private double _valueMin = -2;
    private double _valueMax = 2;
    private double _beatMin;
    private double _beatMax = 8;

    private DragState? _drag;
    private bool _editStarted;
    private (ChorusKeyframe Keyframe, bool IsY)? _hover;

    public ChorusCurveView()
    {
        ClipToBounds = true;
        Focusable = true;
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

    public event EventHandler? EditStarted;
    public event EventHandler? EditCommitted;

    private void OnLanguageChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        RefreshThemeResources();
        InvalidateVisual();
    }

    private void RefreshThemeResources()
    {
        var light = ThemeManager.IsLight;
        _isLight = light;

        _panelFill = BrushOf("InputBrush", light ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x17, 0x17, 0x17));
        _plotFill = Frozen(new SolidColorBrush(light ? Color.FromRgb(0xFB, 0xFC, 0xFD) : Color.FromRgb(0x12, 0x14, 0x18)));
        _textBrush = BrushOf("TextBrush", light ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Color.FromRgb(0xE4, 0xE4, 0xE4));
        _dimBrush = BrushOf("DimBrush", light ? Color.FromRgb(0x6B, 0x6B, 0x6B) : Color.FromRgb(0x9A, 0x9A, 0x9A));
        _cardFill = Frozen(new SolidColorBrush(light
            ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xE8, 0x26, 0x2A, 0x31)));

        _plotPen = PenOf("BorderBrush", light ? Color.FromRgb(0xD2, 0xD6, 0xDB) : Color.FromRgb(0x34, 0x38, 0x3F), 1);
        _gridPen = DashedPen(light ? Color.FromRgb(0xE4, 0xE8, 0xEC) : Color.FromRgb(0x24, 0x28, 0x2E), 1, 3, 4);
        _gridMajorPen = DashedPen(light ? Color.FromRgb(0xD3, 0xD8, 0xDE) : Color.FromRgb(0x33, 0x38, 0x40), 1, 2, 3);
        _zeroPen = SolidPen(light ? Color.FromArgb(0x40, 0x00, 0x00, 0x00) : Color.FromArgb(0x46, 0xFF, 0xFF, 0xFF), 1.2);
        _playheadBrush = Frozen(new SolidColorBrush(light ? Color.FromRgb(0x2F, 0x86, 0xE0) : Color.FromRgb(0x5A, 0xA9, 0xF0)));
        _playheadPen = SolidPen(light ? Color.FromArgb(0xC0, 0x2F, 0x86, 0xE0) : Color.FromArgb(0xC0, 0x5A, 0xA9, 0xF0), 1.2);
        _cardPen = PenOf("BorderBrush", light ? Color.FromRgb(0xD2, 0xD6, 0xDB) : Color.FromRgb(0x3A, 0x3F, 0x48), 1);
    }

    // ------------------------------------------------------------------
    // Dependency properties
    // ------------------------------------------------------------------
    public static readonly DependencyProperty ChorusProperty = DependencyProperty.Register(
        nameof(Chorus), typeof(ChorusDocument), typeof(ChorusCurveView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ChorusDocument? Chorus
    {
        get => (ChorusDocument?)GetValue(ChorusProperty);
        set => SetValue(ChorusProperty, value);
    }

    public static readonly DependencyProperty SingerProperty = DependencyProperty.Register(
        nameof(Singer), typeof(ChorusSinger), typeof(ChorusCurveView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The singer whose path is edited; null shows an empty state.</summary>
    public ChorusSinger? Singer
    {
        get => (ChorusSinger?)GetValue(SingerProperty);
        set => SetValue(SingerProperty, value);
    }

    public static readonly DependencyProperty PlayheadBeatProperty = DependencyProperty.Register(
        nameof(PlayheadBeat), typeof(double?), typeof(ChorusCurveView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? PlayheadBeat
    {
        get => (double?)GetValue(PlayheadBeatProperty);
        set => SetValue(PlayheadBeatProperty, value);
    }

    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(ChorusCurveView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Redraw token; the path model itself has no change notifications.</summary>
    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    // ------------------------------------------------------------------
    // Drawing
    // ------------------------------------------------------------------
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(_panelFill, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var chorus = Chorus;
        if (chorus is null || ActualWidth < 80 || ActualHeight < 80)
            return;

        var singer = Singer;
        ComputeLayout();
        // Keep the view steady while dragging: recomputing the range from the dragged value makes
        // the axis chase the mouse and the handle feel like it slides away.
        if (_drag is null)
            UpdateRange(chorus, singer);

        var plot = new Rect(_plotLeft, _plotTop, _plotRight - _plotLeft, _plotBottom - _plotTop);
        dc.DrawRoundedRectangle(_plotFill, _plotPen, plot, 3, 3);

        DrawGrid(dc, plot);

        if (singer is null)
        {
            DrawEmpty(dc, plot);
            return;
        }

        var index = chorus.Singers.IndexOf(singer);
        var color = ChorusPalette.For(index, _isLight);
        DrawCurves(dc, plot, singer, color);
        DrawKeyframes(dc, singer, color);
        DrawPlayhead(dc, plot);
        DrawLegend(dc, plot, color);
        DrawDragCard(dc, plot);
    }

    private void ComputeLayout()
    {
        _plotLeft = OuterPadding + ValueGutter;
        _plotTop = OuterPadding;
        _plotRight = Math.Max(_plotLeft + 40, ActualWidth - OuterPadding);
        _plotBottom = Math.Max(_plotTop + 30, ActualHeight - BeatGutter - OuterPadding);
    }

    private void UpdateRange(ChorusDocument chorus, ChorusSinger? singer)
    {
        var lastBeat = 1.0;
        var min = -1.0;
        var max = 1.0;

        if (singer is { Path.Count: > 0 })
        {
            lastBeat = singer.Path[^1].Beat;
            min = singer.Path.Min(k => Math.Min(k.X, k.Y));
            max = singer.Path.Max(k => Math.Max(k.X, k.Y));
        }

        var playhead = PlayheadBeat ?? 1;
        var beatSpan = Math.Max(4.0, Math.Max(lastBeat + 1, playhead + 1));
        _beatMin = 0;
        _beatMax = Math.Ceiling(beatSpan / 4) * 4;

        var span = max - min;
        if (span < 2)
        {
            var center = (min + max) / 2;
            min = center - 1;
            max = center + 1;
            span = 2;
        }

        var pad = Math.Max(0.35, span * 0.12);
        _valueMin = Math.Floor((min - pad) * 2) / 2;
        _valueMax = Math.Ceiling((max + pad) * 2) / 2;
    }

    private double BeatToX(double beat) =>
        _plotLeft + (beat - _beatMin) / Math.Max(1e-6, _beatMax - _beatMin) * (_plotRight - _plotLeft);

    private double ValueToY(double value) =>
        _plotBottom - (value - _valueMin) / Math.Max(1e-6, _valueMax - _valueMin) * (_plotBottom - _plotTop);

    private double XToBeat(double x) =>
        _beatMin + (x - _plotLeft) / Math.Max(1e-6, _plotRight - _plotLeft) * (_beatMax - _beatMin);

    private double YToValue(double y) =>
        _valueMin + (_plotBottom - y) / Math.Max(1e-6, _plotBottom - _plotTop) * (_valueMax - _valueMin);

    private void DrawGrid(DrawingContext dc, Rect plot)
    {
        var valueStep = _valueMax - _valueMin <= 10 ? 1.0 : 2.0;
        var firstValue = Math.Ceiling(_valueMin / valueStep) * valueStep;
        for (var value = firstValue; value <= _valueMax + 1e-6; value += valueStep)
        {
            var y = ValueToY(value);
            var zero = Math.Abs(value) < 1e-6;
            dc.DrawLine(zero ? _zeroPen : _gridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            var label = Format(value.ToString("0.#", CultureInfo.InvariantCulture), 9.5, _dimBrush);
            dc.DrawText(label, new Point(plot.Left - 6 - label.Width, y - label.Height / 2));
        }

        var beatStep = 4.0;
        while ((_beatMax - _beatMin) / beatStep > 12)
            beatStep *= 2;

        var minorStep = (_beatMax - _beatMin) <= 64 ? 1.0 : beatStep / 4;
        for (var beat = _beatMin; beat <= _beatMax + 1e-6; beat += minorStep)
        {
            var x = BeatToX(beat);
            var major = Math.Abs(beat % beatStep) < 1e-6;
            dc.DrawLine(major ? _gridMajorPen : _gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

            if (!major || x > plot.Right - 18)
                continue;

            var label = Format(beat.ToString("0", CultureInfo.InvariantCulture), 9.5, _dimBrush);
            dc.DrawText(label, new Point(x - label.Width / 2, plot.Bottom + 3));
        }

        // Axis units, tucked into the gutters so they never collide with the curves.
        DrawText(dc, "m", 4, plot.Top + 1, _dimBrush, 9.5);
        var unit = Format("拍", 9.5, _dimBrush);
        dc.DrawText(unit, new Point(plot.Right - unit.Width, plot.Bottom + 4));
    }

    private void DrawCurves(DrawingContext dc, Rect plot, ChorusSinger singer, Color color)
    {
        var path = singer.Path;
        if (path.Count < 2)
            return;

        var geometryX = BuildCurve(path, false);
        var geometryY = BuildCurve(path, true);

        dc.PushClip(new RectangleGeometry(plot));
        if (geometryX is not null)
        {
            var thickness = _drag is { IsY: false } ? 2.6 : 2;
            dc.DrawGeometry(null, SolidPen(color, thickness), geometryX);
        }

        if (geometryY is not null)
        {
            var brush = Frozen(new SolidColorBrush(Color.FromArgb(0xB4, color.R, color.G, color.B)));
            var pen = new Pen(brush, _drag is { IsY: true } ? 2.2 : 1.6)
            {
                DashStyle = new DashStyle([4, 3], 0),
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geometryY);
        }

        dc.Pop();
    }

    private StreamGeometry? BuildCurve(IReadOnlyList<ChorusKeyframe> path, bool channelY)
    {
        var first = path[0].Beat;
        var last = path[^1].Beat;
        if (last - first < 1e-6)
            return null;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var start = ChorusMath.Evaluate(path, first);
            context.BeginFigure(
                new Point(BeatToX(first), ValueToY(channelY ? start.Y : start.X)), false, false);

            var steps = Math.Clamp((int)((last - first) * 24), 24, 800);
            for (var i = 1; i <= steps; i++)
            {
                var beat = first + (last - first) * i / steps;
                var (x, y) = ChorusMath.Evaluate(path, beat);
                context.LineTo(new Point(BeatToX(beat), ValueToY(channelY ? y : x)), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private void DrawKeyframes(DrawingContext dc, ChorusSinger singer, Color color)
    {
        var stroke = SolidPen(color, 1.6);
        var fill = Frozen(new SolidColorBrush(color));
        var halo = Frozen(new SolidColorBrush(Color.FromArgb(0x38, color.R, color.G, color.B)));

        foreach (var keyframe in singer.Path)
        {
            var x = BeatToX(keyframe.Beat);
            var yx = ValueToY(keyframe.X);
            var yy = ValueToY(keyframe.Y);

            var hovered = _drag is { } drag
                ? ReferenceEquals(drag.Keyframe, keyframe)
                : _hover is { } h && ReferenceEquals(h.Keyframe, keyframe);
            var radius = hovered ? KeyRadius + 1.4 : KeyRadius;

            if (hovered)
            {
                dc.DrawEllipse(halo, null, new Point(x, yx), HandleHalo, HandleHalo);
                dc.DrawEllipse(halo, null, new Point(x, yy), HandleHalo, HandleHalo);
            }

            // X channel: filled handle; Y channel: hollow handle. Matches the solid/dashed curves.
            dc.DrawEllipse(fill, stroke, new Point(x, yx), radius, radius);
            dc.DrawEllipse(null, stroke, new Point(x, yy), radius, radius);
        }
    }

    private void DrawPlayhead(DrawingContext dc, Rect plot)
    {
        if (PlayheadBeat is not { } beat || beat < _beatMin || beat > _beatMax)
            return;

        var x = BeatToX(beat);
        dc.DrawLine(_playheadPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

        var triangle = new StreamGeometry();
        using (var context = triangle.Open())
        {
            context.BeginFigure(new Point(x - 4, plot.Top), true, true);
            context.LineTo(new Point(x + 4, plot.Top), true, true);
            context.LineTo(new Point(x, plot.Top + 6), true, true);
        }

        triangle.Freeze();
        dc.DrawGeometry(_playheadBrush, null, triangle);
    }

    private void DrawLegend(DrawingContext dc, Rect plot, Color color)
    {
        if (plot.Width < 240)
            return;

        var xLabel = Format("X 位置", 9.5, _dimBrush);
        var yLabel = Format("Y 位置", 9.5, _dimBrush);
        var total = 18 + xLabel.Width + 10 + 18 + yLabel.Width;
        var x = plot.Right - total - 6;
        var y = plot.Top + 4;

        dc.DrawLine(SolidPen(color, 2), new Point(x, y + 6), new Point(x + 14, y + 6));
        dc.DrawText(xLabel, new Point(x + 18, y - 1));

        var dashed = new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xB4, color.R, color.G, color.B))), 1.6)
        {
            DashStyle = new DashStyle([3, 2], 0),
        };
        dashed.Freeze();
        var second = x + 18 + xLabel.Width + 10;
        dc.DrawLine(dashed, new Point(second, y + 6), new Point(second + 14, y + 6));
        dc.DrawText(yLabel, new Point(second + 18, y - 1));
    }

    private void DrawDragCard(DrawingContext dc, Rect plot)
    {
        if (_drag is not { } drag)
            return;

        var channel = drag.IsY ? "Y" : "X";
        var value = drag.IsY ? drag.Keyframe.Y : drag.Keyframe.X;
        var text = Format(
            string.Format(CultureInfo.InvariantCulture, "拍 {0:0.##} · {1} {2:0.00} m",
                drag.Keyframe.Beat, channel, value),
            10, _textBrush);

        var rect = new Rect(plot.Left + 8, plot.Top + 8, text.Width + 14, text.Height + 8);
        dc.DrawRoundedRectangle(_cardFill, _cardPen, rect, 6, 6);
        dc.DrawText(text, new Point(rect.X + 7, rect.Y + 4));
    }

    private void DrawEmpty(DrawingContext dc, Rect plot)
    {
        var text = Format("选择一个歌手以编辑走位曲线", 11, _dimBrush);
        dc.DrawText(text, new Point(
            plot.Left + Math.Max(0, (plot.Width - text.Width) / 2),
            plot.Top + Math.Max(0, (plot.Height - text.Height) / 2)));
    }

    private FormattedText Format(string text, double size, Brush brush) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            TextFace,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawText(DrawingContext dc, string text, double x, double y, Brush brush, double size) =>
        dc.DrawText(Format(text, size, brush), new Point(x, y));

    // ------------------------------------------------------------------
    // Interaction
    // ------------------------------------------------------------------
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        var chorus = Chorus;
        var singer = Singer;
        if (chorus is null || singer is null)
            return;

        ComputeLayout();
        UpdateRange(chorus, singer);
        var point = e.GetPosition(this);

        if (e.ClickCount >= 2)
        {
            AddKeyframe(singer, point);
            e.Handled = true;
            return;
        }

        if (FindKeyframe(singer, point) is not { } hit)
            return;

        _drag = new DragState(hit.Keyframe, hit.IsY);
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var chorus = Chorus;
        var singer = Singer;
        if (chorus is null || singer is null)
            return;

        var point = e.GetPosition(this);
        if (_drag is not { } drag)
        {
            var hover = FindKeyframe(singer, point);
            var changed = !SameHandle(_hover, hover);
            _hover = hover;
            Cursor = hover is null ? Cursors.Cross : Cursors.SizeAll;
            if (changed)
                InvalidateVisual();
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        // Snapshot undo before the first model change of this drag.
        if (!_editStarted)
        {
            _editStarted = true;
            EditStarted?.Invoke(this, EventArgs.Empty);
        }

        var keyframe = drag.Keyframe;
        var value = Math.Round(YToValue(point.Y), 2);
        if (drag.IsY)
            keyframe.Y = ChorusMath.ClampY(value, chorus.StageDepth);
        else
            keyframe.X = ChorusMath.ClampX(value, chorus.StageWidth);

        var beat = ChorusMath.SnapBeat(XToBeat(point.X));
        if (Math.Abs(keyframe.Beat - beat) > 1e-6)
        {
            // Two keys on the same beat would make the path ambiguous, so the old one yields.
            var duplicate = singer.Path.FirstOrDefault(
                k => !ReferenceEquals(k, keyframe) && Math.Abs(k.Beat - beat) < 1e-6);
            if (duplicate is not null)
                singer.Path.Remove(duplicate);

            keyframe.Beat = beat;
            singer.Path.Sort((a, b) => a.Beat.CompareTo(b.Beat));
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        FinishDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        FinishDrag();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_drag is null && _hover is not null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        var chorus = Chorus;
        var singer = Singer;
        if (chorus is null || singer is null)
            return;

        ComputeLayout();
        UpdateRange(chorus, singer);
        var point = e.GetPosition(this);
        if (singer.Path.Count <= 1 || FindKeyframe(singer, point) is not { } hit)
            return;

        EditStarted?.Invoke(this, EventArgs.Empty);
        singer.Path.Remove(hit.Keyframe);
        EditCommitted?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        e.Handled = true;
    }

    private void AddKeyframe(ChorusSinger singer, Point point)
    {
        var beat = ChorusMath.SnapBeat(XToBeat(point.X));
        if (singer.Path.Any(k => Math.Abs(k.Beat - beat) < 1e-6))
            return;

        var (x, y) = ChorusMath.Evaluate(singer.Path, beat);
        EditStarted?.Invoke(this, EventArgs.Empty);
        singer.Path.Add(new ChorusKeyframe { Beat = beat, X = x, Y = y });
        singer.Path.Sort((a, b) => a.Beat.CompareTo(b.Beat));
        EditCommitted?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void FinishDrag()
    {
        if (_drag is null)
            return;

        var changed = _editStarted;
        _drag = null;
        _editStarted = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        if (changed)
            EditCommitted?.Invoke(this, EventArgs.Empty);

        InvalidateVisual();
    }

    private static bool SameHandle(
        (ChorusKeyframe Keyframe, bool IsY)? left,
        (ChorusKeyframe Keyframe, bool IsY)? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return ReferenceEquals(left.Value.Keyframe, right.Value.Keyframe)
            && left.Value.IsY == right.Value.IsY;
    }

    private (ChorusKeyframe Keyframe, bool IsY)? FindKeyframe(ChorusSinger singer, Point point)
    {
        (ChorusKeyframe Keyframe, bool IsY)? result = null;
        var best = KeyHitRadius;

        foreach (var keyframe in singer.Path)
        {
            var x = BeatToX(keyframe.Beat);
            var distanceX = Distance(point, new Point(x, ValueToY(keyframe.X)));
            if (distanceX <= best)
            {
                best = distanceX;
                result = (keyframe, false);
            }

            var distanceY = Distance(point, new Point(x, ValueToY(keyframe.Y)));
            if (distanceY <= best)
            {
                best = distanceY;
                result = (keyframe, true);
            }
        }

        return result;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ------------------------------------------------------------------
    // Theme helpers
    // ------------------------------------------------------------------
    private static Brush BrushOf(string key, Color fallback) =>
        Frozen(Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback));

    private static Pen PenOf(string? key, Color fallback, double thickness)
    {
        var brush = key is null
            ? new SolidColorBrush(fallback)
            : Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static Pen DashedPen(Color color, double thickness, double dash, double gap)
    {
        var pen = new Pen(Frozen(new SolidColorBrush(color)), thickness)
        {
            DashStyle = new DashStyle([dash, gap], 0),
        };
        pen.Freeze();
        return pen;
    }

    private static Pen SolidPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(new SolidColorBrush(color)), thickness);
        pen.Freeze();
        return pen;
    }

    private static Brush Frozen(Brush brush)
    {
        if (brush.CanFreeze && !brush.IsFrozen)
            brush.Freeze();
        return brush;
    }

    private sealed record DragState(ChorusKeyframe Keyframe, bool IsY);
}
