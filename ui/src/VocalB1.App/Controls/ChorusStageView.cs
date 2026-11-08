using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VocalB1.App.Services;
using VocalB1.Core.Chorus;
using VocalB1.Core.Models;

namespace VocalB1.App.Controls;

/// <summary>Raised when the user clicks a singer on the stage.</summary>
public sealed class ChorusSingerEventArgs(ChorusSinger? singer) : EventArgs
{
    public ChorusSinger? Singer { get; } = singer;
}

/// <summary>
/// Top-down stage editor for chorus mode: draws the stage, the microphone and every singer's path.
/// Dragging the microphone or a singer writes straight into the model; the ViewModel snapshots undo on
/// <see cref="EditStarted"/> and refreshes renders on <see cref="EditCommitted"/>.
/// The 2D view is intentionally isolated from the model, so other stage views can be added later.
/// </summary>
public sealed class ChorusStageView : FrameworkElement
{
    private const double Padding = 34;
    private const double BottomBand = 62;
    private const double KeyframeRadius = 4.3;
    private const double KeyframeHitRadius = 9;
    private const double SingerRadius = 8;
    private const double SingerHitRadius = 15;
    private const double MicHitRadius = 16;

    private static readonly Typeface TextFace = new(
        new FontFamily("Microsoft YaHei UI, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private Brush _panelFill = Brushes.Transparent;
    private Brush _textBrush = Brushes.White;
    private Brush _dimBrush = Brushes.Gray;
    private Brush _accentBrush = Brushes.White;
    private Brush _micFill = Brushes.White;
    private Brush _keyframeCore = Brushes.White;
    private Brush _cardFill = Brushes.Transparent;
    private Pen _stagePen = new();
    private Pen _gridPen = new();
    private Pen _gridMajorPen = new();
    private Pen _centerGuidePen = new();
    private Pen _frontPen = new();
    private Pen _micPen = new();
    private Pen _micStemPen = new();
    private Pen _ringPen = new();
    private Pen _crossPen = new();
    private Pen _cardPen = new();

    private bool _isLight;
    private Color _stageBack;
    private Color _stageFront;
    private Color _haloColor;

    private DragState? _drag;
    private bool _editStarted;

    // Layout computed for the current size/stage; used by drawing and hit-testing.
    private double _left;
    private double _top;
    private double _bottom;
    private double _scaleX;
    private double _scaleY;
    private double _stageWidth = 1;
    private double _stageDepth = 1;
    private double _yMin;

    public ChorusStageView()
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
    public event EventHandler<ChorusSingerEventArgs>? SingerSelected;

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
        _textBrush = BrushOf("TextBrush", light ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Color.FromRgb(0xE4, 0xE4, 0xE4));
        _dimBrush = BrushOf("DimBrush", light ? Color.FromRgb(0x6B, 0x6B, 0x6B) : Color.FromRgb(0x9A, 0x9A, 0x9A));
        _accentBrush = BrushOf("AccentBrush", light ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Color.FromRgb(0xF0, 0xF0, 0xF0));

        // Stage floor: a soft "back to front" gradient reads as depth from a top-down camera.
        _stageBack = light ? Color.FromRgb(0xFA, 0xFB, 0xFC) : Color.FromRgb(0x16, 0x18, 0x1C);
        _stageFront = light ? Color.FromRgb(0xEF, 0xF2, 0xF5) : Color.FromRgb(0x1E, 0x22, 0x28);
        _haloColor = light ? Color.FromArgb(0x18, 0x00, 0x00, 0x00) : Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);

        _micFill = BrushOf("AccentBrush", light ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xD0, 0xD0, 0xD0));
        _keyframeCore = Frozen(new SolidColorBrush(light
            ? Color.FromRgb(0xFF, 0xFF, 0xFF)
            : Color.FromRgb(0xF4, 0xF4, 0xF4)));
        _cardFill = Frozen(new SolidColorBrush(light
            ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xD8, 0x26, 0x2A, 0x31)));

        _stagePen = PenOf("BorderBrush", light ? Color.FromRgb(0xD2, 0xD6, 0xDB) : Color.FromRgb(0x34, 0x38, 0x3F), 1.2);
        _frontPen = PenOf("BorderBrush", light ? Color.FromRgb(0xB8, 0xBE, 0xC6) : Color.FromRgb(0x4C, 0x52, 0x5C), 1.6);
        _gridPen = DashedPen(light ? Color.FromRgb(0xE4, 0xE8, 0xEC) : Color.FromRgb(0x25, 0x29, 0x2F), 1, 3, 4);
        _gridMajorPen = DashedPen(light ? Color.FromRgb(0xD5, 0xDA, 0xE0) : Color.FromRgb(0x32, 0x37, 0x3F), 1, 2, 3);
        _centerGuidePen = SolidPen(light ? Color.FromArgb(0x38, 0x00, 0x00, 0x00) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 1);
        _ringPen = DashedPen(light ? Color.FromRgb(0xC8, 0xCF, 0xD6) : Color.FromRgb(0x3E, 0x44, 0x4D), 1, 3, 5);
        _crossPen = DashedPen(light ? Color.FromRgb(0x9A, 0xA1, 0xA9) : Color.FromRgb(0x6E, 0x76, 0x80), 1, 3, 3);
        _cardPen = PenOf("BorderBrush", light ? Color.FromRgb(0xD2, 0xD6, 0xDB) : Color.FromRgb(0x3A, 0x3F, 0x48), 1);
        _micPen = new Pen(_micFill, 1.3);
        _micPen.Freeze();
        _micStemPen = new Pen(_micFill, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _micStemPen.Freeze();
    }

    // ------------------------------------------------------------------
    // Dependency properties
    // ------------------------------------------------------------------
    public static readonly DependencyProperty ChorusProperty = DependencyProperty.Register(
        nameof(Chorus), typeof(ChorusDocument), typeof(ChorusStageView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ChorusDocument? Chorus
    {
        get => (ChorusDocument?)GetValue(ChorusProperty);
        set => SetValue(ChorusProperty, value);
    }

    public static readonly DependencyProperty SelectedSingerProperty = DependencyProperty.Register(
        nameof(SelectedSinger), typeof(ChorusSinger), typeof(ChorusStageView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ChorusSinger? SelectedSinger
    {
        get => (ChorusSinger?)GetValue(SelectedSingerProperty);
        set => SetValue(SelectedSingerProperty, value);
    }

    public static readonly DependencyProperty PlayheadBeatProperty = DependencyProperty.Register(
        nameof(PlayheadBeat), typeof(double?), typeof(ChorusStageView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Current playhead beat; the position at which singer drags write keyframes.</summary>
    public double? PlayheadBeat
    {
        get => (double?)GetValue(PlayheadBeatProperty);
        set => SetValue(PlayheadBeatProperty, value);
    }

    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(ChorusStageView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Redraw token; the stage model itself has no change notifications.</summary>
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
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(_panelFill, null, bounds);

        var chorus = Chorus;
        if (chorus is null || bounds.Width < 60 || bounds.Height < 120)
            return;

        ComputeLayout(chorus);

        var stage = new Rect(_left, _top, _stageWidth * _scaleX, _stageDepth * _scaleY);
        dc.DrawGeometry(StageFill(), _stagePen, new RectangleGeometry(stage, 4, 4));

        DrawSpotlight(dc, stage);
        DrawGrid(dc, chorus);
        DrawFrontAndAudience(dc, stage);
        DrawAxisLabels(dc, chorus);
        DrawMicRings(dc, chorus);
        DrawMic(dc, chorus);

        for (var i = 0; i < chorus.Singers.Count; i++)
            DrawSinger(dc, chorus.Singers[i], i, chorus);

        DrawText(dc, HintText(), _left, ActualHeight - 14, _dimBrush, 10);
        DrawDragOverlay(dc, chorus);
    }

    private string HintText() =>
        $"拖动歌手：在当前拍写关键帧（拍 {ChorusMath.SnapBeat(PlayheadBeat ?? 1):0.##}） · "
        + "拖动节点：移动 · 双击节点：对齐当前拍 · 右键节点：删除";

    private void ComputeLayout(ChorusDocument chorus)
    {
        _stageWidth = Math.Max(1, chorus.StageWidth);
        _stageDepth = Math.Max(1, chorus.StageDepth);
        _yMin = -_stageDepth * 0.5;

        _left = Padding;
        var right = Math.Max(_left + 10, ActualWidth - Padding);
        _top = Padding - 2;
        _bottom = Math.Max(_top + 10, ActualHeight - BottomBand);

        _scaleX = (right - _left) / _stageWidth;
        _scaleY = (_bottom - _top) / (_stageDepth - _yMin);
    }

    private double ScreenX(double x) => _left + (x + _stageWidth / 2) * _scaleX;

    private double ScreenY(double y) => _bottom - (y - _yMin) * _scaleY;

    private (double X, double Y) StagePoint(Point point) =>
        ((point.X - _left) / _scaleX - _stageWidth / 2, (point.Y - _bottom) / -_scaleY + _yMin);

    private Brush StageFill()
    {
        var brush = new LinearGradientBrush(_stageBack, _stageFront, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>Soft light spilling from the stage front, where the microphone and audience are.</summary>
    private void DrawSpotlight(DrawingContext dc, Rect stage)
    {
        var top = _bottom - 26;
        if (top <= _top)
            return;

        var brush = new LinearGradientBrush(
            Color.FromArgb(0, _haloColor.R, _haloColor.G, _haloColor.B), _haloColor,
            new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        dc.DrawRectangle(brush, null, new Rect(stage.Left, top, stage.Width, _bottom - top));
    }

    private void DrawGrid(DrawingContext dc, ChorusDocument chorus)
    {
        var halfWidth = _stageWidth / 2;
        var right = _left + _stageWidth * _scaleX;

        for (var x = Math.Ceiling(-halfWidth); x <= halfWidth; x += 1)
        {
            if (Math.Abs(x) < 1e-6)
                continue;
            var sx = ScreenX(x);
            var major = Math.Abs(x % 2) < 1e-6;
            dc.DrawLine(major ? _gridMajorPen : _gridPen, new Point(sx, _top), new Point(sx, _bottom));
        }

        // Center line (X = 0) is the reference for panning, so it stays visible.
        dc.DrawLine(_centerGuidePen, new Point(ScreenX(0), _top), new Point(ScreenX(0), _bottom));

        for (var y = 1; y <= chorus.StageDepth; y += 1)
        {
            var sy = ScreenY(y);
            var major = Math.Abs(y % 2) < 1e-6;
            dc.DrawLine(major ? _gridMajorPen : _gridPen, new Point(_left, sy), new Point(right, sy));
        }
    }

    private void DrawFrontAndAudience(DrawingContext dc, Rect stage)
    {
        var right = stage.Right;
        dc.DrawLine(_frontPen, new Point(_left, _bottom), new Point(right, _bottom));

        // Audience / microphone band: a shaded strip instead of a text row across the stage.
        var band = new Rect(_left, _bottom, stage.Width, Math.Max(0, ActualHeight - _bottom - 1));
        var bandBrush = new LinearGradientBrush(
            Color.FromArgb(_isLight ? (byte)0x10 : (byte)0x24, 0x00, 0x00, 0x00),
            Color.FromArgb(_isLight ? (byte)0x02 : (byte)0x06, 0x00, 0x00, 0x00),
            new Point(0, 0), new Point(0, 1));
        bandBrush.Freeze();
        dc.DrawRectangle(bandBrush, null, band);
        dc.DrawLine(_ringPen, new Point(_left, _bottom + 1), new Point(right, _bottom + 1));

        DrawText(dc, "观众席 / 麦克风区", _left, _bottom + 12, _dimBrush, 10);
    }

    private void DrawAxisLabels(DrawingContext dc, ChorusDocument chorus)
    {
        var halfWidth = _stageWidth / 2;
        // Keep the X ticks just inside the stage front edge: below it sits the microphone zone,
        // where the centre label would be hidden under the microphone icon.
        var xLabelY = ScreenY(0) - 15;
        foreach (var value in new[] { -halfWidth, 0.0, halfWidth })
        {
            var text = value switch
            {
                < 0 => $"-{Math.Abs(value):0.#}",
                > 0 => $"+{value:0.#}",
                _ => "0",
            };
            var formatted = Format(text, 9.5, _dimBrush);
            // Stay inside the stage: at the far left the label would otherwise sit in the value
            // gutter next to the Y tick and read as one number.
            var x = Math.Clamp(
                ScreenX(value) - formatted.Width / 2,
                _left + 2,
                _left + _stageWidth * _scaleX - formatted.Width - 2);
            dc.DrawText(formatted, new Point(x, xLabelY));
        }

        for (var y = 0; y <= chorus.StageDepth; y += 2)
        {
            var formatted = Format($"{y:0}", 9.5, _dimBrush);
            dc.DrawText(formatted, new Point(_left - 10 - formatted.Width, ScreenY(y) - formatted.Height / 2));
        }

        // Back-of-stage marker, kept out of the working area.
        var back = Format("舞台后方", 9.5, _dimBrush);
        dc.DrawText(back, new Point(_left + _stageWidth * _scaleX - back.Width - 8, _top + 6));
    }

    private void DrawMicRings(DrawingContext dc, ChorusDocument chorus)
    {
        var center = new Point(ScreenX(chorus.MicX), ScreenY(chorus.MicY));
        var scale = (_scaleX + _scaleY) / 2;
        foreach (var meters in new[] { 1.0, 2.5, 5.0 })
        {
            var radius = meters * scale;
            if (radius < 6 || radius > ActualWidth * 1.2)
                continue;
            dc.DrawEllipse(null, _ringPen, center, radius, radius);
        }
    }

    private void DrawMic(DrawingContext dc, ChorusDocument chorus)
    {
        var point = new Point(ScreenX(chorus.MicX), ScreenY(chorus.MicY));
        dc.DrawEllipse(null, _micPen, point, 10, 10);
        dc.DrawEllipse(_micFill, null, point, 3.4, 3.4);
        // Short stem pointing at the audience, so the mic reads as "in front of" the stage.
        dc.DrawLine(_micStemPen, new Point(point.X, point.Y + 10), new Point(point.X, point.Y + 18));
        dc.DrawEllipse(_micFill, null, new Point(point.X, point.Y + 19), 1.6, 1.6);

        var label = Format("麦克风", 10.5, _textBrush);
        var x = Math.Clamp(point.X + 14, 4, ActualWidth - label.Width - 4);
        dc.DrawText(label, new Point(x, point.Y - label.Height / 2));
    }

    private void DrawSinger(DrawingContext dc, ChorusSinger singer, int index, ChorusDocument chorus)
    {
        var selected = ReferenceEquals(singer, SelectedSinger);
        var color = ChorusPalette.For(index, _isLight);
        var colorBrush = Frozen(new SolidColorBrush(color));

        var opacity = selected ? 1.0 : singer.Enabled ? 0.64 : 0.42;
        dc.PushOpacity(opacity);

        var beat = PlayheadBeat ?? singer.Path.FirstOrDefault()?.Beat ?? 1;
        DrawPath(dc, singer, color, beat, selected);

        var ringPen = new Pen(colorBrush, selected ? 1.7 : 1.2);
        ringPen.Freeze();
        foreach (var keyframe in singer.Path)
        {
            var point = new Point(ScreenX(keyframe.X), ScreenY(keyframe.Y));
            dc.DrawEllipse(_keyframeCore, null, point, 2.1, 2.1);
            dc.DrawEllipse(null, ringPen, point, KeyframeRadius, KeyframeRadius);
            if (selected)
            {
                DrawText(dc, keyframe.Beat.ToString("0.##", CultureInfo.InvariantCulture),
                    point.X + 8, point.Y - 16, _dimBrush, 9.5);
            }
        }

        var (currentX, currentY) = ChorusMath.Evaluate(singer.Path, beat);
        var marker = new Point(ScreenX(currentX), ScreenY(currentY));

        if (selected)
        {
            var halo = Frozen(new SolidColorBrush(Color.FromArgb(0x2E, color.R, color.G, color.B)));
            dc.DrawEllipse(halo, null, marker, 16, 16);
            var haloPen = new Pen(colorBrush, 1.5);
            haloPen.Freeze();
            dc.DrawEllipse(null, haloPen, marker, 11.5, 11.5);
        }

        if (singer.Enabled)
        {
            dc.DrawEllipse(colorBrush, null, marker, SingerRadius, SingerRadius);
        }
        else
        {
            // Disabled singers stay visible but hollow, so the stage still shows their planned position.
            var dashed = new Pen(colorBrush, 1.4) { DashStyle = new DashStyle([2.5, 2.5], 0) };
            dashed.Freeze();
            dc.DrawEllipse(null, dashed, marker, SingerRadius, SingerRadius);
        }

        if (selected)
        {
            DrawNameBadge(dc, singer.Name, marker, color);
        }
        else
        {
            var label = Format(singer.Name, 10.5, _dimBrush);
            var x = Math.Clamp(marker.X + 12, 4, ActualWidth - label.Width - 4);
            dc.DrawText(label, new Point(x, marker.Y - label.Height / 2));
        }

        dc.Pop();
    }

    private void DrawNameBadge(DrawingContext dc, string name, Point marker, Color color)
    {
        var text = Format(name, 11, _textBrush);
        var rect = new Rect(marker.X + 15, marker.Y - text.Height / 2 - 4, text.Width + 14, text.Height + 8);
        rect.X = Math.Clamp(rect.X, 4, Math.Max(4, ActualWidth - rect.Width - 4));
        rect.Y = Math.Clamp(rect.Y, _top, Math.Max(_top, ActualHeight - rect.Height - 4));

        var fill = Frozen(new SolidColorBrush(_isLight
            ? Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xE8, 0x22, 0x26, 0x2C)));
        var pen = new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xC0, color.R, color.G, color.B))), 1);
        pen.Freeze();
        dc.DrawRoundedRectangle(fill, pen, rect, 8, 8);
        dc.DrawText(text, new Point(rect.X + 7, rect.Y + 4));
    }

    private void DrawPath(DrawingContext dc, ChorusSinger singer, Color color, double beat, bool selected)
    {
        var path = singer.Path;
        if (path.Count < 2)
            return;

        var first = path[0].Beat;
        var last = path[^1].Beat;
        var split = Math.Clamp(beat, first, last);
        var colorBrush = Frozen(new SolidColorBrush(color));

        if (selected)
        {
            var glow = BuildPathGeometry(path, first, last);
            if (glow is not null)
            {
                var glowPen = new Pen(
                    Frozen(new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B))), 7);
                glowPen.Freeze();
                dc.DrawGeometry(null, glowPen, glow);
            }
        }

        // Travel up to the playhead is solid; planned positions after it are dashed.
        var past = BuildPathGeometry(path, first, split);
        if (past is not null)
        {
            var pen = new Pen(colorBrush, selected ? 2 : 1.5);
            pen.Freeze();
            dc.DrawGeometry(null, pen, past);
        }

        var future = BuildPathGeometry(path, split, last);
        if (future is not null)
        {
            var alpha = selected ? (byte)0xB4 : (byte)0x80;
            var brush = Frozen(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)));
            var pen = new Pen(brush, selected ? 1.6 : 1.2) { DashStyle = new DashStyle([4, 3], 0) };
            pen.Freeze();
            dc.DrawGeometry(null, pen, future);
        }
    }

    private StreamGeometry? BuildPathGeometry(IReadOnlyList<ChorusKeyframe> path, double fromBeat, double toBeat)
    {
        var span = toBeat - fromBeat;
        if (path.Count < 2 || span <= 1e-6)
            return null;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var start = ChorusMath.Evaluate(path, fromBeat);
            context.BeginFigure(new Point(ScreenX(start.X), ScreenY(start.Y)), false, false);

            var steps = Math.Clamp((int)(span * 16), 8, 600);
            for (var i = 1; i <= steps; i++)
            {
                var (x, y) = ChorusMath.Evaluate(path, fromBeat + span * i / steps);
                context.LineTo(new Point(ScreenX(x), ScreenY(y)), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private void DrawDragOverlay(DrawingContext dc, ChorusDocument chorus)
    {
        if (_drag?.Kind != DragKind.Mic)
            return;

        var origin = new Point(ScreenX(chorus.MicX), ScreenY(chorus.MicY));
        dc.DrawLine(_crossPen, new Point(0, origin.Y), new Point(ActualWidth, origin.Y));
        dc.DrawLine(_crossPen, new Point(origin.X, 0), new Point(origin.X, ActualHeight));

        var text = Format($"X {chorus.MicX:+0.00;-0.00;0.00} m · Y {chorus.MicY:+0.00;-0.00;0.00} m", 10.5, _textBrush);
        var rect = new Rect(
            Math.Clamp(origin.X + 16, 4, Math.Max(4, ActualWidth - text.Width - 18)),
            Math.Clamp(origin.Y + 14, 4, Math.Max(4, ActualHeight - text.Height - 16)),
            text.Width + 12,
            text.Height + 8);
        dc.DrawRoundedRectangle(_cardFill, _cardPen, rect, 6, 6);
        dc.DrawText(text, new Point(rect.X + 6, rect.Y + 4));
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
        if (chorus is null)
            return;

        var point = e.GetPosition(this);
        ComputeLayout(chorus);

        if (e.ClickCount >= 2)
        {
            TryRetimeKeyframe(chorus, point);
            e.Handled = true;
            return;
        }

        if (Distance(point, new Point(ScreenX(chorus.MicX), ScreenY(chorus.MicY))) <= MicHitRadius)
        {
            _drag = new DragState(DragKind.Mic, null, null, ChorusMath.SnapBeat(PlayheadBeat ?? 1));
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (SelectedSinger is { } selected && FindKeyframe(selected, point) is { } keyframe)
        {
            _drag = new DragState(DragKind.Keyframe, selected, keyframe, keyframe.Beat);
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        var singer = FindSinger(chorus, point);
        if (singer is not null)
        {
            SingerSelected?.Invoke(this, new ChorusSingerEventArgs(singer));
            _drag = new DragState(DragKind.Singer, singer, null, ChorusMath.SnapBeat(PlayheadBeat ?? 1));
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var chorus = Chorus;
        if (chorus is null)
            return;

        if (_drag is null)
        {
            UpdateCursor(chorus, e.GetPosition(this));
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        ComputeLayout(chorus);
        var point = e.GetPosition(this);
        var (x, y) = StagePoint(point);
        x = ChorusMath.ClampX(x, chorus.StageWidth);

        // Snapshot undo before the first model change of this drag.
        if (!_editStarted)
        {
            _editStarted = true;
            EditStarted?.Invoke(this, EventArgs.Empty);
        }

        switch (_drag.Kind)
        {
            case DragKind.Mic:
                chorus.MicX = Math.Round(x, 2);
                chorus.MicY = Math.Round(Math.Clamp(y, -chorus.StageDepth * 0.5, 0), 2);
                break;

            case DragKind.Keyframe when _drag.Keyframe is { } keyframe:
                keyframe.X = Math.Round(x, 2);
                keyframe.Y = Math.Round(ChorusMath.ClampY(y, chorus.StageDepth), 2);
                break;

            case DragKind.Singer when _drag.Singer is { } singer:
                _drag.Keyframe ??= EnsureKeyframe(singer, _drag.Beat);
                _drag.Keyframe.X = Math.Round(x, 2);
                _drag.Keyframe.Y = Math.Round(ChorusMath.ClampY(y, chorus.StageDepth), 2);
                break;
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
        FinishDrag(commit: false);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        var chorus = Chorus;
        if (chorus is null)
            return;

        ComputeLayout(chorus);
        var point = e.GetPosition(this);

        if (SelectedSinger is { } selected && selected.Path.Count > 1
            && FindKeyframe(selected, point) is { } keyframe)
        {
            EditStarted?.Invoke(this, EventArgs.Empty);
            selected.Path.Remove(keyframe);
            EditCommitted?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (FindSinger(chorus, point) is { } singer)
        {
            SingerSelected?.Invoke(this, new ChorusSingerEventArgs(singer));
            e.Handled = true;
        }
    }

    private void FinishDrag(bool commit = true)
    {
        if (_drag is null)
            return;

        var changed = _editStarted;
        _drag = null;
        _editStarted = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        if (changed && commit)
            EditCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCursor(ChorusDocument chorus, Point point)
    {
        var overMic = Distance(point, new Point(ScreenX(chorus.MicX), ScreenY(chorus.MicY))) <= MicHitRadius;
        var overKeyframe = SelectedSinger is { } selected && FindKeyframe(selected, point) is not null;
        Cursor = overMic || overKeyframe ? Cursors.SizeAll : Cursors.Arrow;
    }

    private ChorusKeyframe? FindKeyframe(ChorusSinger singer, Point point)
    {
        ChorusKeyframe? result = null;
        var best = KeyframeHitRadius;
        foreach (var keyframe in singer.Path)
        {
            var distance = Distance(point, new Point(ScreenX(keyframe.X), ScreenY(keyframe.Y)));
            if (distance <= best)
            {
                best = distance;
                result = keyframe;
            }
        }

        return result;
    }

    private ChorusSinger? FindSinger(ChorusDocument chorus, Point point)
    {
        var beat = PlayheadBeat ?? 1;
        ChorusSinger? result = null;
        var best = SingerHitRadius;

        foreach (var singer in chorus.Singers)
        {
            var (x, y) = ChorusMath.Evaluate(singer.Path, beat);
            var distance = Distance(point, new Point(ScreenX(x), ScreenY(y)));
            if (distance > best)
                continue;
            best = distance;
            result = singer;
        }

        // A click directly on another singer's keyframe also selects that singer.
        if (result is null)
        {
            foreach (var singer in chorus.Singers)
            {
                if (singer.Path.Any(k => Distance(point, new Point(ScreenX(k.X), ScreenY(k.Y))) <= KeyframeHitRadius))
                    return singer;
            }
        }

        return result;
    }

    private ChorusKeyframe EnsureKeyframe(ChorusSinger singer, double beat)
    {
        var existing = singer.Path.FirstOrDefault(p => Math.Abs(p.Beat - beat) < 1e-6);
        if (existing is not null)
            return existing;

        var (x, y) = ChorusMath.Evaluate(singer.Path, beat);
        var keyframe = new ChorusKeyframe { Beat = beat, X = x, Y = y };
        singer.Path.Add(keyframe);
        singer.Path.Sort((a, b) => a.Beat.CompareTo(b.Beat));
        return keyframe;
    }

    private void TryRetimeKeyframe(ChorusDocument chorus, Point point)
    {
        if (SelectedSinger is not { } singer || FindKeyframe(singer, point) is not { } keyframe)
            return;

        var beat = ChorusMath.SnapBeat(PlayheadBeat ?? 1);
        if (Math.Abs(keyframe.Beat - beat) < 1e-6)
            return;

        EditStarted?.Invoke(this, EventArgs.Empty);
        // A keyframe already on the target beat absorbs this one.
        var duplicate = singer.Path.FirstOrDefault(
            p => !ReferenceEquals(p, keyframe) && Math.Abs(p.Beat - beat) < 1e-6);
        if (duplicate is not null)
            singer.Path.Remove(duplicate);
        keyframe.Beat = beat;
        singer.Path.Sort((a, b) => a.Beat.CompareTo(b.Beat));
        EditCommitted?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

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

    private enum DragKind
    {
        Mic,
        Keyframe,
        Singer,
    }

    private sealed class DragState(DragKind kind, ChorusSinger? singer, ChorusKeyframe? keyframe, double beat)
    {
        public DragKind Kind { get; } = kind;
        public ChorusSinger? Singer { get; } = singer;
        public double Beat { get; } = beat;
        public ChorusKeyframe? Keyframe { get; set; } = keyframe;
    }
}
