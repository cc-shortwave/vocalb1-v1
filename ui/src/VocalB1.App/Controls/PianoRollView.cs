using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VocalB1.App.Services;
using VocalB1.Core.Models;
using VocalB1.Core.Pitch;

namespace VocalB1.App.Controls;

/// <summary>Piano roll edit mode: Note mode edits voice segments only, PitchLine mode edits pitch points only.</summary>
public enum PianoRollMode
{
    Notes,
    PitchLine,
}

/// <summary>Raised on right-click in Note mode to request a new note (audio is chosen externally).</summary>
public sealed class NoteAddEventArgs(double beat, int semitone) : EventArgs
{
    public double Beat { get; } = beat;
    public int Semitone { get; } = semitone;
}

/// <summary>Raised after the pencil tool draws a note range (audio is chosen externally).</summary>
public sealed class NoteDrawEventArgs(double beatStart, double beatEnd, int semitone) : EventArgs
{
    public double BeatStart { get; } = beatStart;
    public double BeatEnd { get; } = beatEnd;
    public int Semitone { get; } = semitone;
}

/// <summary>A rest region: a deleted lyric unit keeps its beat range but has no note block.</summary>
public sealed record RestRegion(double BeatStart, double BeatEnd, int Semitone);

/// <summary>
/// Editable piano roll: horizontal axis is beats, vertical axis is pitch.
/// Left button selects and drags a note / pitch point; right button adds a note or pitch point at the mouse position according to the current mode.
/// Edits write directly back to <see cref="Track"/>; external subscribers refresh the list and persist the state.
/// </summary>
public sealed class PianoRollView : FrameworkElement
{
    private const double GutterWidth = 46;
    private const double RulerHeight = 20;
    private const double RightPad = 8;
    private const double BottomPad = 8;
    private const double NodeRadius = 5;
    private const double HitRadius = 8;
    private const double BeatSnap = 0.25;
    private const double MinPxPerBeat = 52;
    private const double PlayheadHitRadius = 7;
    private const double EdgeGrabRadius = 6;
    private const double MinNoteBeats = 0.25;
    private const double BoxSelectThreshold = 4;
    private const double GradientGrabRadius = 6;
    private const double MaxGradientBeats = 4;
    private const double MinBeatZoom = 0.25;
    private const double MaxBeatZoom = 6;
    private const int MaxPitchRows = 120;
    private const int MaxPitchScroll = 60;

    /// <summary>Synthesis frame constant: 12000 / BPM gives frames per beat (matches the Python-side 5 ms frame period).</summary>
    private const double FramesPerBeatConstant = 12000.0;

    private static readonly Typeface TextFace = new(
        new FontFamily("Microsoft YaHei UI, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    // Brushes are rebuilt when the theme changes; see RefreshThemeResources.
    private Brush _panelFill = Brushes.Transparent;
    private Brush _labelBrush = Brushes.Gray;
    private Brush _noteInkBrush = Brushes.White;
    private Brush _selectedNoteInk = Brushes.Black;
    private Brush _pitchFill = Brushes.White;
    private Brush _noteFill = Brushes.Transparent;
    private Brush _noteSelectedFill = Brushes.White;
    private Pen _borderPen = new();
    private Pen _gridPen = new();
    private Pen _barPen = new();
    private Pen _accentPen = new();
    private Pen _playheadPen = new();
    private Pen _selectionPen = new();
    private Pen _subGridPen = new();
    private Brush _selectionFill = Brushes.Transparent;

    private DragState? _drag;

    /// <summary>Box/multi selection: selected notes are tracked by Id and selected points by reference to avoid index drift.</summary>
    private readonly HashSet<string> _selectedSegmentIds = [];
    private readonly List<PitchPoint> _selectedPoints = [];

    /// <summary>Beat at the left edge of the viewport; derived from the playhead position while playing/dragging so the track scrolls.</summary>
    private double _viewStartBeat = 1;

    /// <summary>Vertical scroll in pitch rows (positive = look at higher pitches).</summary>
    private int _pitchScroll;

    /// <summary>Vertical zoom: 1 shows exactly the row span used by the notes, smaller values show more rows.</summary>
    private double _pitchZoom = 1;

    /// <summary>Horizontal zoom relative to the width that fits the whole track.</summary>
    private double _beatZoom = 1;

    public PianoRollView()
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

    private void OnLanguageChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        RefreshThemeResources();
        InvalidateVisual();
    }

    /// <summary>Re-resolve the colors used by the piano roll for the current theme (dark / light share the same drawing logic).</summary>
    private void RefreshThemeResources()
    {
        var light = ThemeManager.IsLight;

        _panelFill = BrushOf("InputBrush", light ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x17, 0x17, 0x17));
        _labelBrush = BrushOf("DimBrush", light ? Color.FromRgb(0x6B, 0x6B, 0x6B) : Color.FromRgb(0x9A, 0x9A, 0x9A));
        _noteInkBrush = BrushOf("TextBrush", light ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Color.FromRgb(0xE4, 0xE4, 0xE4));
        _pitchFill = BrushOf("PitchBrush", light ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Color.FromRgb(0xF0, 0xF0, 0xF0));

        _noteFill = Frozen(new SolidColorBrush(light
            ? Color.FromArgb(0x30, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));
        _noteSelectedFill = Frozen(new SolidColorBrush(light
            ? Color.FromArgb(0xB8, 0x00, 0x00, 0x00)
            : Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)));
        _selectedNoteInk = light
            ? Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)))
            : Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)));

        _borderPen = PenOf("BorderBrush", light ? Color.FromRgb(0xC8, 0xC8, 0xC8) : Color.FromRgb(0x3A, 0x3A, 0x3A), 1);
        _gridPen = PenOf(null, light ? Color.FromRgb(0xEC, 0xEC, 0xEC) : Color.FromRgb(0x28, 0x28, 0x28), 1);
        _subGridPen = PenOf(null, light ? Color.FromRgb(0xF5, 0xF5, 0xF5) : Color.FromRgb(0x20, 0x20, 0x20), 1);
        _barPen = PenOf(null, light ? Color.FromRgb(0xC9, 0xC9, 0xC9) : Color.FromRgb(0x40, 0x40, 0x40), 1);
        _accentPen = PenOf("AccentBrush", light ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xD0, 0xD0, 0xD0), 1);
        _playheadPen = PenOf("PitchBrush", light ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Color.FromRgb(0xF0, 0xF0, 0xF0), 1.4);

        _selectionPen = new Pen(_accentPen.Brush, 1)
        {
            DashStyle = new DashStyle([3, 3], 0),
            DashCap = PenLineCap.Flat,
        };
        _selectionPen.Freeze();
        _selectionFill = Frozen(new SolidColorBrush(light
            ? Color.FromArgb(0x1E, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    }

    // ------------------------------------------------------------------
    // Dependency properties
    // ------------------------------------------------------------------
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(TrackDocument), typeof(PianoRollView),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnTrackChanged));

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PianoRollView view)
            return;

        // After switching tracks or undoing, Track is a new object and the old selection records are no longer valid.
        view._selectedSegmentIds.Clear();
        view._selectedPoints.Clear();
        view._drag = null;
    }

    public static readonly DependencyProperty BpmProperty = DependencyProperty.Register(
        nameof(Bpm), typeof(double), typeof(PianoRollView),
        new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BeatsPerBarProperty = DependencyProperty.Register(
        nameof(BeatsPerBar), typeof(int), typeof(PianoRollView),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(PianoRollMode), typeof(PianoRollView),
        new FrameworkPropertyMetadata(PianoRollMode.Notes, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Pencil tool: left-drag inside the plot draws a new note from press to release.</summary>
    public static readonly DependencyProperty IsPencilProperty = DependencyProperty.Register(
        nameof(IsPencil), typeof(bool), typeof(PianoRollView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Minor grid lines per beat: 0 = off, 2 = eighth notes, 4 = sixteenth notes.</summary>
    public static readonly DependencyProperty GridDivisionProperty = DependencyProperty.Register(
        nameof(GridDivision), typeof(int), typeof(PianoRollView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedNoteIndexProperty = DependencyProperty.Register(
        nameof(SelectedNoteIndex), typeof(int?), typeof(PianoRollView),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SelectedPitchIndexProperty = DependencyProperty.Register(
        nameof(SelectedPitchIndex), typeof(int?), typeof(PianoRollView),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(PianoRollView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Rest regions (deleted lyric units) drawn as dashed blocks of the same length.</summary>
    public static readonly DependencyProperty RestRegionsProperty = DependencyProperty.Register(
        nameof(RestRegions), typeof(IReadOnlyList<RestRegion>), typeof(PianoRollView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayheadBeatProperty = DependencyProperty.Register(
        nameof(PlayheadBeat), typeof(double?), typeof(PianoRollView),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnPlayheadChanged));

    public TrackDocument? Track
    {
        get => (TrackDocument?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public int BeatsPerBar
    {
        get => (int)GetValue(BeatsPerBarProperty);
        set => SetValue(BeatsPerBarProperty, value);
    }

    /// <summary>Tempo: converts segment-level transitions (in frames) into beat widths on the piano roll.</summary>
    public double Bpm
    {
        get => (double)GetValue(BpmProperty);
        set => SetValue(BpmProperty, value);
    }

    public PianoRollMode Mode
    {
        get => (PianoRollMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public bool IsPencil
    {
        get => (bool)GetValue(IsPencilProperty);
        set => SetValue(IsPencilProperty, value);
    }

    public int GridDivision
    {
        get => (int)GetValue(GridDivisionProperty);
        set => SetValue(GridDivisionProperty, value);
    }

    public int? SelectedNoteIndex
    {
        get => (int?)GetValue(SelectedNoteIndexProperty);
        set => SetValue(SelectedNoteIndexProperty, value);
    }

    public int? SelectedPitchIndex
    {
        get => (int?)GetValue(SelectedPitchIndexProperty);
        set => SetValue(SelectedPitchIndexProperty, value);
    }

    /// <summary>Increment this value after an external (ViewModel) model change to trigger a redraw.</summary>
    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <summary>Rest regions (deleted lyric units) drawn as dashed blocks of the same length.</summary>
    public IReadOnlyList<RestRegion>? RestRegions
    {
        get => (IReadOnlyList<RestRegion>?)GetValue(RestRegionsProperty);
        set => SetValue(RestRegionsProperty, value);
    }

    /// <summary>Playhead position (beat); null = hide the playhead.</summary>
    public double? PlayheadBeat
    {
        get => (double?)GetValue(PlayheadBeatProperty);
        set => SetValue(PlayheadBeatProperty, value);
    }

    /// <summary>Playhead beat actually displayed: stays at the beginning before playback/dragging so the playhead is visible on entry.</summary>
    private double PlayheadDisplayBeat => PlayheadBeat ?? 1;

    /// <summary>Raised after a note is dragged/added so external code can refresh the list.</summary>
    public event EventHandler? EditCommitted;

    /// <summary>Raised before an edit actually modifies the model (used to capture an undo snapshot).</summary>
    public event EventHandler? EditStarted;

    /// <summary>Raised when playhead dragging starts; external code can pause playback in response.</summary>
    public event EventHandler? PlayheadGrabbed;

    private static void OnPlayheadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PianoRollView view)
            return;

        // While dragging the playhead it follows the mouse without centering; centering happens on release.
        if (view._drag is { Kind: DragKind.Playhead })
            return;

        if (e.NewValue is double beat)
            view.CenterViewOnPlayhead(beat);
        else
            view.ResetView();
    }

    /// <summary>Shift the viewport so the given beat sits at the center of the plot area (the playhead stays fixed and the content scrolls).</summary>
    private void CenterViewOnPlayhead(double beat)
    {
        var plotWidth = Math.Max(1, ActualWidth - GutterWidth - RightPad);
        var pxPerBeat = Math.Max(MinPxPerBeat, plotWidth / Math.Max(1, TotalBeats()));
        _viewStartBeat = Math.Max(1, beat - plotWidth / pxPerBeat / 2);
        InvalidateVisual();
    }

    /// <summary>Return the viewport to beat 1 when the playhead is hidden.</summary>
    private void ResetView()
    {
        _viewStartBeat = 1;
        InvalidateVisual();
    }

    /// <summary>Raised when a note is added by right-click in Note mode.</summary>
    public event EventHandler<NoteAddEventArgs>? NoteAddRequested;

    /// <summary>Raised when the pencil tool finishes drawing a note range.</summary>
    public event EventHandler<NoteDrawEventArgs>? NoteDrawRequested;

    // ------------------------------------------------------------------
    // Drawing
    // ------------------------------------------------------------------
    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size.Width <= 8 || size.Height <= 8)
            return;

        dc.DrawRoundedRectangle(_panelFill, _borderPen, new Rect(size), 6, 6);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, size.Width, size.Height)));

        var track = Track;
        var segments = track?.Segments ?? [];
        var points = track?.PitchLine ?? [];
        // Curve drawing uses the same compiled sequence that is sent to the engine,
        // so what is on screen is what is rendered (hidden anchors no longer join the curve).
        var curve = track?.BuildRenderPitchLine() ?? [];
        var layout = BuildLayout(size, segments, points);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        DrawPitchRows(dc, layout, dpi);
        DrawBeats(dc, layout, dpi);
        DrawNotes(dc, layout, segments, dpi);
        DrawRests(dc, layout, dpi);
        DrawPencilPreview(dc, layout);
        DrawNoteTransitions(dc, layout);
        DrawPitchLine(dc, layout, curve);
        DrawNoteLyrics(dc, layout, segments, dpi);
        DrawSelectionBox(dc);
        DrawPlayhead(dc, layout);
        // A track that only holds rests is not empty: the dashed rest blocks are already on screen.
        DrawEmptyHint(dc, layout, segments.Count + (RestRegions?.Count ?? 0), dpi);

        dc.Pop();
    }

    private RollLayout BuildLayout(
        Size size,
        IReadOnlyList<VoiceSegment> segments,
        IReadOnlyList<PitchPoint> points)
    {
        var beatsPerBar = Math.Max(1, BeatsPerBar);
        var totalBeats = TotalBeats();

        var (low, high) = PitchWindow(segments, points);
        var plot = new Rect(
            GutterWidth,
            RulerHeight,
            Math.Max(1, size.Width - GutterWidth - RightPad),
            Math.Max(1, size.Height - RulerHeight - BottomPad));
        var pxPerBeat = Math.Max(4, Math.Max(MinPxPerBeat, plot.Width / Math.Max(1, totalBeats)) * _beatZoom);

        return new RollLayout(
            plot, low, high, high - low + 1, totalBeats, beatsPerBar, segments, points,
            pxPerBeat, _viewStartBeat);
    }

    /// <summary>
    /// Visible pitch window: the note span (at least 12 rows) expanded/shrunk by the vertical zoom,
    /// then shifted by the vertical scroll.
    /// </summary>
    private (int Low, int High) PitchWindow(
        IReadOnlyList<VoiceSegment> segments,
        IReadOnlyList<PitchPoint> points)
    {
        var (autoLow, autoHigh) = PitchRange(segments, points, RestRegions);
        var baseRows = autoHigh - autoLow + 1;
        var rows = Math.Clamp((int)Math.Round(baseRows * _pitchZoom), 6, MaxPitchRows);
        var low = autoLow + _pitchScroll - (rows - baseRows) / 2;
        low = Math.Clamp(low, -24, Math.Max(-24, 143 - rows));
        return (low, low + rows - 1);
    }

    /// <summary>Last beat of the whole track (including the pitch line), at least 4 beats.</summary>
    private double TotalBeats()
    {
        var total = 4.0;
        foreach (var segment in Track?.Segments ?? [])
            total = Math.Max(total, Math.Max(segment.BeatStart, segment.BeatEnd));
        foreach (var point in Track?.PitchLine ?? [])
            total = Math.Max(total, point.Beat);
        foreach (var rest in RestRegions ?? [])
            total = Math.Max(total, Math.Max(rest.BeatStart, rest.BeatEnd));
        return total;
    }

    private void DrawPitchRows(DrawingContext dc, RollLayout layout, double dpi)
    {
        var step = layout.RowHeight < 7 ? 3 : layout.RowHeight < 11 ? 2 : 1;

        for (var semitone = layout.Low; semitone <= layout.High; semitone++)
        {
            var y = Math.Round(layout.RowTop(semitone)) + 0.5;
            var isOctave = ((semitone % 12) + 12) % 12 == 0;
            if (!isOctave && (semitone - layout.Low) % step != 0)
                continue;

            dc.DrawLine(isOctave ? _barPen : _gridPen,
                new Point(layout.Plot.Left, y), new Point(layout.Plot.Right, y));

            if ((!isOctave && step > 1) || (layout.RowHeight < 12 && !isOctave))
                continue;

            var text = new FormattedText(
                PitchMath.ToPitchName(semitone),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TextFace,
                Math.Min(10, Math.Max(8, layout.RowHeight - 2)), _labelBrush, dpi);
            dc.DrawText(text, new Point(
                GutterWidth - text.Width - 6,
                y + (layout.RowHeight - text.Height) / 2));
        }
    }

    private void DrawBeats(DrawingContext dc, RollLayout layout, double dpi)
    {
        var step = 1;
        while (layout.PxPerBeat * step < 10)
            step *= 2;

        var firstBeat = Math.Max(1.0, Math.Floor(layout.ViewStartBeat));
        for (var beat = firstBeat; beat <= layout.TotalBeats + 0.001; beat += step)
        {
            if (layout.IsBarStart(beat))
                continue;
            var x = Math.Round(layout.X(beat)) + 0.5;
            if (x > layout.Plot.Right)
                break;
            if (x < layout.Plot.Left)
                continue;
            dc.DrawLine(_gridPen, new Point(x, layout.Plot.Top), new Point(x, layout.Plot.Bottom));
        }

        for (var bar = 1; ; bar++)
        {
            var beat = 1 + (bar - 1) * layout.BeatsPerBar;
            if (beat > layout.TotalBeats + 0.001)
                break;

            var x = Math.Round(layout.X(beat)) + 0.5;
            if (x > layout.Plot.Right)
                break;
            if (x < layout.Plot.Left)
                continue;

            dc.DrawLine(_barPen, new Point(x, layout.Plot.Top), new Point(x, layout.Plot.Bottom));

            var label = new FormattedText(
                bar.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TextFace, 9, _labelBrush, dpi);
            dc.DrawText(label, new Point(x + 3, 3));
        }

        DrawSubBeats(dc, layout);
    }

    /// <summary>Subdivision lines (eighth / sixteenth notes) lighter than the beat grid.</summary>
    private void DrawSubBeats(DrawingContext dc, RollLayout layout)
    {
        var division = GridDivision;
        if (division <= 1 || layout.PxPerBeat / division < 4)
            return;

        var firstStep = (int)Math.Floor((layout.ViewStartBeat - 1) * division);
        for (var step = Math.Max(0, firstStep); ; step++)
        {
            var beat = 1 + step / (double)division;
            if (beat > layout.TotalBeats + 0.001)
                break;
            if (step % division == 0)
                continue;

            var x = Math.Round(layout.X(beat)) + 0.5;
            if (x > layout.Plot.Right)
                break;
            if (x < layout.Plot.Left)
                continue;

            dc.DrawLine(_subGridPen, new Point(x, layout.Plot.Top), new Point(x, layout.Plot.Bottom));
        }
    }

    /// <summary>Ghost note shown while the pencil tool is dragging.</summary>
    private void DrawPencilPreview(DrawingContext dc, RollLayout layout)
    {
        if (_drag is not { Kind: DragKind.Pencil } drag)
            return;

        var box = layout.NoteBox(drag.OriginalBeat, drag.DrawEndBeat, drag.OriginalSemitone);
        if (box.IsEmpty)
            return;

        var pen = new Pen(_accentPen.Brush, 1)
        {
            DashStyle = new DashStyle([3, 3], 0),
            DashCap = PenLineCap.Flat,
        };
        pen.Freeze();
        dc.DrawRectangle(_selectionFill, pen, box);
    }

    private void DrawNotes(
        DrawingContext dc,
        RollLayout layout,
        IReadOnlyList<VoiceSegment> segments,
        double dpi)
    {
        var highlight = Mode == PianoRollMode.Notes ? SelectedNoteIndex : null;

        for (var i = 0; i < segments.Count; i++)
        {
            var (beatStart, beatEnd, semitone) = NoteState(i, segments[i]);
            var box = layout.NoteBox(beatStart, beatEnd, semitone);
            if (box.IsEmpty)
                continue;

            var selected = highlight == i || _selectedSegmentIds.Contains(segments[i].Id);
            dc.DrawRoundedRectangle(
                selected ? _noteSelectedFill : _noteFill,
                selected ? _accentPen : null,
                box, 3, 3);
        }
    }

    /// <summary>
    /// Rest regions: a dashed block at the same beat range and pitch as the deleted note, so a deleted
    /// lyric unit still reads as a rest of the same length. They are not editable here.
    /// </summary>
    private void DrawRests(DrawingContext dc, RollLayout layout, double dpi)
    {
        var rests = RestRegions;
        if (rests is null || rests.Count == 0)
            return;

        var pen = new Pen(_labelBrush, 1)
        {
            DashStyle = new DashStyle([3, 3], 0),
            DashCap = PenLineCap.Flat,
        };
        pen.Freeze();

        var label = new FormattedText(
            LanguageService.Translate("休止"), CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, TextFace, 10, _labelBrush, dpi);

        foreach (var rest in rests)
        {
            if (rest.Semitone < layout.Low || rest.Semitone > layout.High)
                continue;

            var box = layout.NoteBox(rest.BeatStart, rest.BeatEnd, rest.Semitone);
            if (box.IsEmpty)
                continue;

            dc.DrawRoundedRectangle(null, pen, box, 3, 3);
            if (box.Width < label.Width + 4)
                continue;

            dc.PushClip(new RectangleGeometry(box));
            dc.DrawText(label, new Point(box.X + 3, box.Y + (box.Height - label.Height) / 2));
            dc.Pop();
        }
    }

    /// <summary>
    /// Gradient region: dashed lines extend backward from the end of the previous segment and forward from the start of the next one,
    /// with a length equal to the next segment's segment-level transition in beats; a playhead-like vertical handle at the dashed end can be dragged to change it.
    /// </summary>
    private void DrawNoteTransitions(DrawingContext dc, RollLayout layout)
    {
        var pen = new Pen(_accentPen.Brush, 1)
        {
            DashStyle = new DashStyle([3, 3], 0),
            DashCap = PenLineCap.Flat,
        };
        pen.Freeze();

        foreach (var band in GradientBands(layout))
        {
            // The previous segment extends outward from its end; the next segment extends outward from its start.
            DrawGradientBand(dc, pen, layout, band.RightEdgeX, band.RightEdgeX + band.LengthX, band.Top, band.Bottom);
            DrawGradientBand(dc, pen, layout, band.LeftEdgeX - band.LengthX, band.LeftEdgeX, band.Top, band.Bottom);
        }
    }

    /// <summary>Adjacent note index pairs ordered by beat.</summary>
    private static IEnumerable<(int Previous, int Current)> NeighborPairs(
        IReadOnlyList<VoiceSegment> segments)
    {
        var order = new int[segments.Count];
        for (var i = 0; i < order.Length; i++)
            order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            var byStart = segments[a].BeatStart.CompareTo(segments[b].BeatStart);
            return byStart != 0 ? byStart : segments[a].BeatEnd.CompareTo(segments[b].BeatEnd);
        });

        for (var k = 1; k < order.Length; k++)
            yield return (order[k - 1], order[k]);
    }

    /// <summary>A gradient band spans the union of the rows of two adjacent notes; if one is off-screen, fall back to the other.</summary>
    private static (double Top, double Bottom) BandBounds(Rect prevBox, Rect currBox)
    {
        if (prevBox.IsEmpty)
            return (currBox.Top, currBox.Bottom);
        if (currBox.IsEmpty)
            return (prevBox.Top, prevBox.Bottom);
        return (Math.Min(prevBox.Top, currBox.Top), Math.Max(prevBox.Bottom, currBox.Bottom));
    }

    /// <summary>Geometry of a gradient band: the next segment's left/right edges and the gradient length in pixels.</summary>
    private readonly record struct GradientBand(
        int SegmentIndex, double LeftEdgeX, double RightEdgeX, double LengthX, double Top, double Bottom);

    /// <summary>Gradient bands between adjacent notes, with length taken from the next segment's transition beats; shared by drawing and hit testing.</summary>
    private IEnumerable<GradientBand> GradientBands(RollLayout layout)
    {
        var segments = layout.Segments;
        if (segments.Count < 2)
            yield break;

        foreach (var (prevIndex, currentIndex) in NeighborPairs(segments))
        {
            var previous = segments[prevIndex];
            var current = segments[currentIndex];
            var gradient = SegmentGradientBeats(current);
            if (gradient <= 1e-9)
                continue;

            var (prevStart, prevEnd, _) = NoteState(prevIndex, previous);
            var (currStart, _, _) = NoteState(currentIndex, current);
            var prevBox = layout.NoteBox(prevStart, prevEnd, SemitoneOf(previous.DstPitch));
            var currBox = layout.NoteBox(currStart, current.BeatEnd, SemitoneOf(current.DstPitch));
            var (top, bottom) = BandBounds(prevBox, currBox);
            if (bottom <= top)
                continue;

            var rightEdgeX = layout.X(prevEnd);
            yield return new GradientBand(
                currentIndex,
                layout.X(currStart),
                rightEdgeX,
                layout.X(prevEnd + gradient) - rightEdgeX,
                top,
                bottom);
        }
    }

    /// <summary>Draw a dashed gradient band and a vertical handle at its end away from the note.</summary>
    private void DrawGradientBand(
        DrawingContext dc,
        Pen pen,
        RollLayout layout,
        double from,
        double to,
        double top,
        double bottom)
    {
        var left = Math.Min(from, to);
        var right = Math.Max(from, to);
        if (right - left < 1 || right < layout.Plot.Left || left > layout.Plot.Right)
            return;

        dc.DrawRectangle(null, pen, new Rect(left, top, right - left, bottom - top));

        var x = Math.Round(to) + 0.5;
        if (x < layout.Plot.Left || x > layout.Plot.Right)
            return;

        DrawGradientHandle(dc, x, top, bottom);
    }

    /// <summary>Gradient handle: a vertical line spanning the band height with an inverted triangle and a short horizontal line at the top (same style as the playhead).</summary>
    private void DrawGradientHandle(DrawingContext dc, double x, double top, double bottom)
    {
        dc.DrawLine(_accentPen, new Point(x, top), new Point(x, bottom));

        var tip = top - 3;
        var baseY = tip - 7;
        dc.DrawLine(_accentPen, new Point(x - 7, baseY - 4), new Point(x + 7, baseY - 4));

        var triangle = new StreamGeometry();
        using (var ctx = triangle.Open())
        {
            ctx.BeginFigure(new Point(x - 5, baseY), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(x + 5, baseY), true, true);
            ctx.LineTo(new Point(x, tip), true, true);
        }
        triangle.Freeze();
        dc.DrawGeometry(_accentPen.Brush, null, triangle);
    }

    /// <summary>Draggable gradient handle: dragging changes the next segment's transition length.</summary>
    private readonly record struct GradientHandle(
        int SegmentIndex, double X, double Top, double Bottom, int Direction);

    /// <summary>Compute gradient handle positions; drawing and hit testing share the same geometry.</summary>
    private IEnumerable<GradientHandle> GradientHandles(RollLayout layout)
    {
        foreach (var band in GradientBands(layout))
        {
            if (band.LengthX < 1)
                continue;

            // direction = -1: the handle is at the band's left end and dragging left increases the length; +1: at the right end, dragging right increases it.
            yield return new GradientHandle(band.SegmentIndex, band.RightEdgeX + band.LengthX, band.Top, band.Bottom, 1);
            yield return new GradientHandle(band.SegmentIndex, band.LeftEdgeX - band.LengthX, band.Top, band.Bottom, -1);
        }
    }

    private GradientHandle? HitGradientHandle(RollLayout layout, Point position)
    {
        foreach (var handle in GradientHandles(layout))
        {
            if (position.Y < handle.Top - 2 || position.Y > handle.Bottom + 2)
                continue;
            if (Math.Abs(position.X - handle.X) > GradientGrabRadius)
                continue;
            return handle;
        }
        return null;
    }

    /// <summary>Convert the segment-level transition frame count into a beat width.</summary>
    private double SegmentGradientBeats(VoiceSegment segment)
    {
        var bpm = Bpm;
        return bpm <= 0 ? 0 : Math.Max(0, segment.Transition) * bpm / FramesPerBeatConstant;
    }

    /// <summary>Drag the gradient handle: adjust the gradient length (0-4 beats) by horizontal mouse movement, writing back to the model and redrawing live.</summary>
    private void UpdateGradientDrag(DragState drag, RollLayout layout, Point position)
    {
        var track = Track;
        if (track is null || drag.Index < 0 || drag.Index >= track.Segments.Count)
            return;

        var delta = (layout.BeatAt(position.X) - drag.OriginalBeat) * drag.Direction;
        var beats = Math.Clamp(drag.OriginalBeatEnd + delta, 0, MaxGradientBeats);
        var frames = Math.Round(beats * FramesPerBeatConstant / Math.Max(1, Bpm));
        var segment = track.Segments[drag.Index];
        if (Math.Abs(segment.Transition - frames) < 0.5)
            return;

        if (!drag.EditBegun)
        {
            drag.EditBegun = true;
            EditStarted?.Invoke(this, EventArgs.Empty);
        }

        segment.Transition = frames;
        InvalidateVisual();
    }

    private void DrawSelectionBox(DrawingContext dc)
    {
        if (_drag is not { Kind: DragKind.Box } drag)
            return;

        var rect = BoxRect(drag);
        if (rect.Width < 1 || rect.Height < 1)
            return;

        dc.DrawRectangle(_selectionFill, _selectionPen, rect);
    }

    private static Rect BoxRect(DragState drag)
    {
        var x1 = Math.Min(drag.Start.X, drag.Current.X);
        var y1 = Math.Min(drag.Start.Y, drag.Current.Y);
        return new Rect(x1, y1,
            Math.Abs(drag.Current.X - drag.Start.X), Math.Abs(drag.Current.Y - drag.Start.Y));
    }

    /// <summary>
    /// Note lyrics: drawn after the Pitch line so nodes and the curve do not cover them.
    /// Text starts at the left edge of the note; notes that are too narrow are skipped.
    /// </summary>
    private void DrawNoteLyrics(
        DrawingContext dc,
        RollLayout layout,
        IReadOnlyList<VoiceSegment> segments,
        double dpi)
    {
        var textSize = Math.Min(12, Math.Max(9, layout.RowHeight - 2));
        var highlight = Mode == PianoRollMode.Notes ? SelectedNoteIndex : null;

        for (var i = 0; i < segments.Count; i++)
        {
            var (beatStart, beatEnd, semitone) = NoteState(i, segments[i]);
            var box = layout.NoteBox(beatStart, beatEnd, semitone);
            if (box.IsEmpty || box.Width < 20)
                continue;

            var text = new FormattedText(
                segments[i].Lyric, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                TextFace, textSize,
                highlight == i || _selectedSegmentIds.Contains(segments[i].Id)
                    ? _selectedNoteInk
                    : _noteInkBrush, dpi);

            dc.PushClip(new RectangleGeometry(box));
            dc.DrawText(text, new Point(box.X + 3, box.Y + (box.Height - text.Height) / 2));
            dc.Pop();
        }
    }

    private void DrawPitchLine(DrawingContext dc, RollLayout layout, IReadOnlyList<PitchPoint> points)
    {
        var pen = new Pen(_pitchFill, 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        // Control-point handles still come from the stored line (their indices are what editing uses);
        // the curve itself is drawn from the compiled sequence passed in, which is the same list
        // the engine receives.  They only differ by the hidden edge anchors.
        var source = Track?.PitchLine ?? [];
        var indexOf = new Dictionary<PitchPoint, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < source.Count; i++)
            indexOf[source[i]] = i;

        var nodes = new Point?[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            var (beat, maybeSemitone) = PointState(i, source[i]);
            nodes[i] = maybeSemitone is int semitone
                ? new Point(layout.X(beat), layout.RowCenter(semitone))
                : null;
        }

        var segmentById = new Dictionary<string, VoiceSegment>();
        foreach (var segment in layout.Segments)
            segmentById[segment.Id] = segment;

        // Same interpolation as the engine (_apply_pitch_line): in [p_i, p_i+1) the pitch follows a
        // smoothstep from v_i to v_i+1.  Silent / KEEP nodes break the line, and so does a real rest
        // between two notes.  Nodes that share one beat get a 20 ms visual ramp, matching the short
        // jump ramp the engine inserts at such zero-length intervals.
        var rampBeats = Math.Max(0.0, 20.0 * Bpm / 60000.0);
        var runs = new List<List<Point>>();
        List<Point>? run = null;
        PitchPoint? previousPoint = null;
        var lastBeat = double.NegativeInfinity;

        foreach (var point in points)
        {
            var (beat, maybeSemitone) = indexOf.TryGetValue(point, out var index)
                ? PointState(index, point)
                : (point.Beat, PitchSemitone(point));

            if (maybeSemitone is not int semitone)
            {
                run = null;
                previousPoint = null;
                lastBeat = double.NegativeInfinity;
                continue;
            }

            if (previousPoint is not null && HasTimeGap(previousPoint, point, segmentById))
            {
                run = null;
                lastBeat = double.NegativeInfinity;
            }

            var drawBeat = beat > lastBeat + 1e-4 ? beat : lastBeat + Math.Max(1e-4, rampBeats);
            lastBeat = drawBeat;

            if (run is null)
            {
                run = [];
                runs.Add(run);
            }

            run.Add(new Point(layout.X(drawBeat), layout.RowCenter(semitone)));
            previousPoint = point;
        }

        foreach (var runPoints in runs)
            DrawSmoothRun(dc, pen, runPoints);

        // Regular control points inside overlap regions are not drawn (those regions are controlled only by transition nodes).
        var overlaps = SegmentLayout.Overlaps(layout.Segments);

        for (var i = 0; i < nodes.Length; i++)
        {
            // Anchors at note edges only pin the curve and do not show a control point.
            if (nodes[i] is not Point node || source[i].IsEdgeAnchor)
                continue;

            var selected = (Mode == PianoRollMode.PitchLine && SelectedPitchIndex == i)
                || _selectedPoints.Contains(source[i]);

            if (source[i].IsTransition)
            {
                // Transition nodes: exclusive to overlapping sections, drawn with a heavier outline than regular nodes to distinguish them.
                DrawDiamond(dc, _pitchFill, _accentPen, node, selected ? NodeRadius + 1 : NodeRadius);
                continue;
            }

            if (IsInsideOverlap(overlaps, source[i].Beat))
                continue;

            DrawDiamond(dc,
                selected ? _noteSelectedFill : _pitchFill,
                selected ? _accentPen : null,
                node,
                selected ? NodeRadius + 1 : NodeRadius);
        }
    }

    private static bool IsInsideOverlap(
        IReadOnlyList<SegmentLayout.Overlap> overlaps, double beat)
    {
        foreach (var overlap in overlaps)
        {
            if (beat > overlap.Start + 0.001 && beat < overlap.End - 0.001)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether there is a time gap between the notes owning two adjacent nodes.
    /// Touching ends and overlapping times both count as connected (keep smooth); only an actual empty gap breaks the line.
    /// </summary>
    private static bool HasTimeGap(
        PitchPoint previous, PitchPoint current, IReadOnlyDictionary<string, VoiceSegment> segments)
    {
        if (previous.SegmentId is not string previousId
            || current.SegmentId is not string currentId
            || previousId == currentId)
            return false;

        if (!segments.TryGetValue(previousId, out var before)
            || !segments.TryGetValue(currentId, out var after))
            return false;

        var gap = Math.Max(after.BeatStart - before.BeatEnd, before.BeatStart - after.BeatEnd);
        return gap > 0.01;
    }

    /// <summary>
    /// Draws one continuous run of nodes with the same interpolation the engine uses:
    /// x advances linearly while y follows a smoothstep, which is exactly one cubic Bezier
    /// whose two inner control points stay on the endpoint heights.
    /// </summary>
    private static void DrawSmoothRun(DrawingContext dc, Pen pen, List<Point> runPoints)
    {
        if (runPoints.Count < 2)
            return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(runPoints[0], isFilled: false, isClosed: false);
            for (var i = 0; i + 1 < runPoints.Count; i++)
            {
                var a = runPoints[i];
                var b = runPoints[i + 1];
                var dx = b.X - a.X;
                if (dx <= 0)
                    continue;

                ctx.BezierTo(
                    new Point(a.X + dx / 3, a.Y),
                    new Point(b.X - dx / 3, b.Y),
                    b,
                    isStroked: true,
                    isSmoothJoin: true);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>Pitch of a node in semitones; null = silent, KEEP = no defined pitch (the line breaks).</summary>
    private static int? PitchSemitone(PitchPoint point) =>
        point.Pitch is null || string.Equals(point.Pitch.Trim(), "KEEP", StringComparison.OrdinalIgnoreCase)
            ? null
            : SemitoneOf(point.Pitch, 0);

    /// <summary>Diamond drag handle: apex up, all four sides equal.</summary>
    private static void DrawDiamond(DrawingContext dc, Brush fill, Pen? pen, Point center, double half)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(center.X, center.Y - half), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(center.X + half, center.Y), true, true);
            ctx.LineTo(new Point(center.X, center.Y + half), true, true);
            ctx.LineTo(new Point(center.X - half, center.Y), true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(fill, pen, geometry);
    }

    /// <summary>Playhead: a vertical line spanning the plot area with an inverted triangle and a short horizontal line at the top.</summary>
    private void DrawPlayhead(DrawingContext dc, RollLayout layout)
    {
        var x = Math.Round(layout.X(PlayheadDisplayBeat)) + 0.5;
        if (x < layout.Plot.Left || x > layout.Plot.Right)
            return;

        var tip = layout.Plot.Top - 3;
        var baseY = tip - 8;
        dc.DrawLine(_playheadPen, new Point(x, tip), new Point(x, layout.Plot.Bottom));
        dc.DrawLine(_playheadPen, new Point(x - 8, baseY - 5), new Point(x + 8, baseY - 5));

        var triangle = new StreamGeometry();
        using (var ctx = triangle.Open())
        {
            ctx.BeginFigure(new Point(x - 5, baseY), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(x + 5, baseY), true, true);
            ctx.LineTo(new Point(x, tip), true, true);
        }
        triangle.Freeze();
        dc.DrawGeometry(_pitchFill, null, triangle);
    }

    private void DrawEmptyHint(DrawingContext dc, RollLayout layout, int count, double dpi)
    {
        if (count > 0)
            return;

        var hint = new FormattedText(
            Mode == PianoRollMode.Notes
                ? LanguageService.Translate("暂无发音段：右键添加音块，或从左侧声库「加入工程」")
                : LanguageService.Translate("暂无音高线：请先在音符模式添加音块"),
            CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, 12, _labelBrush, dpi);
        dc.DrawText(hint, new Point(
            layout.Plot.Left + (layout.Plot.Width - hint.Width) / 2,
            layout.Plot.Top + (layout.Plot.Height - hint.Height) / 2));
    }

    // ------------------------------------------------------------------
    // Mouse interaction
    // ------------------------------------------------------------------
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        var layout = Layout();
        if (layout is null)
            return;

        var position = e.GetPosition(this);

        // Pencil: dragging inside the plot draws a new note; the press point is the start, the release point the end.
        if (IsPencil && Mode == PianoRollMode.Notes
            && position.X >= layout.Plot.Left && position.X <= layout.Plot.Right
            && position.Y >= layout.Plot.Top && position.Y <= layout.Plot.Bottom)
        {
            var startBeat = Math.Max(1, Snap(layout.BeatAt(position.X)));
            _drag = new DragState(DragKind.Pencil, -1, position, startBeat, startBeat,
                layout.SemitoneAt(position.Y))
            {
                DrawEndBeat = startBeat,
            };
            CaptureMouse();
            Cursor = Cursors.Cross;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // The playhead has hit priority: press and drag to move the playback position to any beat.
        var headBeat = PlayheadDisplayBeat;
        if (Math.Abs(position.X - layout.X(headBeat)) <= PlayheadHitRadius
            && position.Y >= 0 && position.Y <= layout.Plot.Bottom)
        {
            _drag = new DragState(DragKind.Playhead, -1, position, headBeat, 0, 0);
            CaptureMouse();
            Cursor = Cursors.SizeWE;
            PlayheadGrabbed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        // Gradient handle: dragging only adjusts the transition length between adjacent segments, not note positions.
        if (HitGradientHandle(layout, position) is { } handle)
        {
            var segment = Track!.Segments[handle.SegmentIndex];
            _drag = new DragState(DragKind.Gradient, handle.SegmentIndex, position,
                layout.BeatAt(position.X), SegmentGradientBeats(segment), 0)
            {
                Direction = handle.Direction,
            };
            CaptureMouse();
            Cursor = Cursors.SizeWE;
            e.Handled = true;
            return;
        }

        if (Mode == PianoRollMode.Notes)
        {
            var index = HitNote(layout, position);

            if (index >= 0)
            {
                var segment = Track!.Segments[index];
                var box = layout.NoteBox(segment.BeatStart, segment.BeatEnd, SemitoneOf(segment.DstPitch));
                var kind = DragKindAt(box, position);

                // Pressing the middle of an already box-selected note: move all selected notes and points together.
                if (kind == DragKind.Note
                    && _selectedSegmentIds.Contains(segment.Id)
                    && (_selectedSegmentIds.Count > 1 || _selectedPoints.Count > 0))
                {
                    StartMultiDrag(index, position);
                }
                else
                {
                    SetSingleNoteSelection(index);
                    _drag = new DragState(kind, index, position,
                        segment.BeatStart, segment.BeatEnd, SemitoneOf(segment.DstPitch));
                    UpdateDragPreview(Track!, _drag);
                    CaptureMouse();
                    Cursor = kind == DragKind.Note ? Cursors.SizeAll : Cursors.SizeWE;
                }
            }
            else
            {
                // Pressing empty space: start box selection.
                ClearSelection();
                _drag = new DragState(DragKind.Box, -1, position, 0, 0, 0);
                CaptureMouse();
                Cursor = Cursors.Cross;
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var pointIndex = HitPoint(layout, position);

        if (pointIndex >= 0)
        {
            SetSinglePointSelection(pointIndex);
            _drag = new DragState(DragKind.Point, pointIndex, position,
                Track!.PitchLine[pointIndex].Beat, 0, SemitoneOf(Track.PitchLine[pointIndex].Pitch, 0));
            CaptureMouse();
            Cursor = Cursors.SizeAll;
        }
        else
        {
            ClearSelection();
            _drag = new DragState(DragKind.Box, -1, position, 0, 0, 0);
            CaptureMouse();
            Cursor = Cursors.Cross;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Select a single note: clear the multi-selection records.</summary>
    private void SetSingleNoteSelection(int index)
    {
        _selectedSegmentIds.Clear();
        _selectedPoints.Clear();
        if (Track is { } track && index >= 0 && index < track.Segments.Count)
            _selectedSegmentIds.Add(track.Segments[index].Id);
        SelectedNoteIndex = index;
    }

    /// <summary>Select a single Pitch node: clear the multi-selection records.</summary>
    private void SetSinglePointSelection(int index)
    {
        _selectedSegmentIds.Clear();
        _selectedPoints.Clear();
        if (Track is { } track && index >= 0 && index < track.PitchLine.Count)
            _selectedPoints.Add(track.PitchLine[index]);
        SelectedPitchIndex = index;
    }

    private void ClearSelection()
    {
        _selectedSegmentIds.Clear();
        _selectedPoints.Clear();
        SelectedNoteIndex = null;
        SelectedPitchIndex = null;
    }

    /// <summary>Start a group move using an already-selected note as the handle.</summary>
    private void StartMultiDrag(int index, Point position)
    {
        var track = Track!;
        var segment = track.Segments[index];
        _drag = new DragState(DragKind.Note, index, position,
            segment.BeatStart, segment.BeatEnd, SemitoneOf(segment.DstPitch));
        _drag.MultiIndices.AddRange(SelectedSegmentIndices(track));
        _drag.MultiPoints.AddRange(_selectedPoints);
        UpdateDragPreview(track, _drag);
        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    private List<int> SelectedSegmentIndices(TrackDocument track)
    {
        var result = new List<int>(_selectedSegmentIds.Count);
        for (var i = 0; i < track.Segments.Count; i++)
        {
            if (_selectedSegmentIds.Contains(track.Segments[i].Id))
                result.Add(i);
        }
        return result;
    }

    /// <summary>Finish box selection: select all notes and points intersecting the rectangle.</summary>
    private void CompleteBoxSelection(DragState drag)
    {
        var layout = Layout();
        if (layout is null)
            return;

        var rect = BoxRect(drag);
        if (rect.Width < BoxSelectThreshold && rect.Height < BoxSelectThreshold)
        {
            ClearSelection();
            return;
        }

        _selectedSegmentIds.Clear();
        _selectedPoints.Clear();

        var firstNote = -1;
        var segments = layout.Segments;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var box = layout.NoteBox(segment.BeatStart, segment.BeatEnd, SemitoneOf(segment.DstPitch));
            if (box.IsEmpty || !box.IntersectsWith(rect))
                continue;
            _selectedSegmentIds.Add(segment.Id);
            if (firstNote < 0)
                firstNote = i;
        }

        var firstPoint = -1;
        var points = layout.Points;
        var overlaps = SegmentLayout.Overlaps(segments);
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (point.Pitch is null || point.IsEdgeAnchor
                || !PitchMath.TryParseSemitones(point.Pitch, out var semitones))
                continue;
            if (!point.IsTransition && IsInsideOverlap(overlaps, point.Beat))
                continue;

            var node = new Point(layout.X(point.Beat), layout.RowCenter((int)Math.Round(semitones)));
            if (!rect.Contains(node))
                continue;
            _selectedPoints.Add(point);
            if (firstPoint < 0)
                firstPoint = i;
        }

        if (firstNote >= 0)
            SelectedNoteIndex = firstNote;
        else if (Mode == PianoRollMode.Notes)
            SelectedNoteIndex = null;

        if (firstPoint >= 0)
            SelectedPitchIndex = firstPoint;
        else if (Mode == PianoRollMode.PitchLine)
            SelectedPitchIndex = null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_drag is null)
        {
            UpdateHoverCursor(e.GetPosition(this));
            return;
        }

        var layout = Layout();
        if (layout is null)
            return;

        var position = e.GetPosition(this);

        if (_drag.Kind == DragKind.Playhead)
        {
            PlayheadBeat = ClampBeat(_drag.OriginalBeat + (position.X - _drag.Start.X) / layout.PxPerBeat);
            e.Handled = true;
            return;
        }

        if (_drag.Kind == DragKind.Box)
        {
            _drag.Current = position;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_drag.Kind == DragKind.Gradient)
        {
            UpdateGradientDrag(_drag, layout, position);
            e.Handled = true;
            return;
        }

        if (_drag.Kind == DragKind.Pencil)
        {
            _drag.DrawEndBeat = Math.Max(_drag.OriginalBeat, Snap(layout.BeatAt(position.X)));
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var beatDelta = Snap((position.X - _drag.Start.X) / layout.PxPerBeat);
        var semitoneDelta = -(int)Math.Round((position.Y - _drag.Start.Y) / layout.RowHeight);

        // Do not allow dragging before beat 1 (multi-selection moves are clamped to the leftmost note by UpdateDragPreview).
        if (_drag.MultiIndices.Count == 0)
        {
            var minDelta = 1 - _drag.OriginalBeat;
            if (beatDelta < minDelta)
                beatDelta = minDelta;
        }

        _drag.BeatDelta = beatDelta;
        _drag.SemitoneDelta = semitoneDelta;

        // Capture an undo snapshot only after actual movement so a plain click does not create an empty undo step.
        var moved = _drag.Kind switch
        {
            DragKind.Point => semitoneDelta != 0,
            DragKind.Note => beatDelta != 0 || semitoneDelta != 0,
            DragKind.NoteStart or DragKind.NoteEnd => beatDelta != 0,
            _ => false,
        };
        if (moved && !_drag.EditBegun)
        {
            _drag.EditBegun = true;
            EditStarted?.Invoke(this, EventArgs.Empty);
        }

        // Dragging either end only resizes the length, not the pitch.
        UpdateDragPreview(Track!, _drag);

        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// Compute the preview range for this drag and the shifts for subsequent notes:
    /// moving left does not cross the previous note (overlap is forbidden); moving right past the next note shifts it and all following notes.
    /// </summary>
    private static void UpdateDragPreview(TrackDocument track, DragState drag)
    {
        drag.Shifts.Clear();
        drag.Previews.Clear();
        drag.PreviewStart = drag.OriginalBeat;
        drag.PreviewEnd = drag.OriginalBeatEnd;

        if (drag.Kind is not (DragKind.Note or DragKind.NoteStart or DragKind.NoteEnd))
            return;
        if (drag.Index < 0 || drag.Index >= track.Segments.Count)
            return;

        if (drag.Kind == DragKind.Note && drag.MultiIndices.Count > 0)
        {
            UpdateMultiPreview(track, drag);
            if (drag.Previews.TryGetValue(drag.Index, out var main))
            {
                drag.PreviewStart = main.Start;
                drag.PreviewEnd = main.End;
            }
            return;
        }

        var segment = track.Segments[drag.Index];
        var (previous, next) = SegmentLayout.Neighbors(track, segment);

        // When a previous note exists, the start must not cross its end beat (no new overlap).
        var floor = previous is null ? 1 : Math.Max(1, previous.BeatEnd);

        double start;
        double end;
        switch (drag.Kind)
        {
            case DragKind.Note:
                start = Math.Max(floor, Snap(drag.OriginalBeat + drag.BeatDelta));
                end = start + (drag.OriginalBeatEnd - drag.OriginalBeat);
                break;

            case DragKind.NoteStart:
                start = Math.Clamp(Snap(drag.OriginalBeat + drag.BeatDelta),
                    floor, Math.Max(floor, drag.OriginalBeatEnd - MinNoteBeats));
                end = drag.OriginalBeatEnd;
                break;

            default:
                // Dragging the right end changes only the end beat; the left end stays fixed.
                start = drag.OriginalBeat;
                end = Math.Max(drag.OriginalBeat + MinNoteBeats,
                    Snap(drag.OriginalBeatEnd + drag.BeatDelta));
                break;
        }

        drag.PreviewStart = start;
        drag.PreviewEnd = end;

        // When a following note exists, any part past its start shifts that note and all later notes (preserving relative spacing).
        // Existing overlaps are not pushed further; the right end may reach max(original end, next note start).
        if (next is null)
            return;

        var limit = Math.Max(drag.OriginalBeatEnd, next.BeatStart);
        var overflow = end - limit;
        if (overflow <= 1e-9)
            return;

        var ordered = SegmentLayout.Ordered(track);
        var position = ordered.FindIndex(s => ReferenceEquals(s, segment));
        if (position < 0)
            return;

        for (var i = position + 1; i < ordered.Count; i++)
        {
            var index = track.Segments.FindIndex(s => ReferenceEquals(s, ordered[i]));
            if (index >= 0)
                drag.Shifts[index] = overflow;
        }
    }

    /// <summary>
    /// Multi-move preview: all selected notes move together keeping their relative positions; moving left does not cross preceding unselected notes,
    /// and moving right into an unselected note shifts it and everything after it while preserving the original spacing.
    /// </summary>
    private static void UpdateMultiPreview(TrackDocument track, DragState drag)
    {
        var selectedIds = new HashSet<string>();
        foreach (var index in drag.MultiIndices)
        {
            if (index >= 0 && index < track.Segments.Count)
                selectedIds.Add(track.Segments[index].Id);
        }
        if (selectedIds.Count == 0)
            return;

        var ordered = SegmentLayout.Ordered(track);
        var firstSelected = ordered.FindIndex(s => selectedIds.Contains(s.Id));
        if (firstSelected < 0)
            return;

        var minStart = double.MaxValue;
        foreach (var index in drag.MultiIndices)
            minStart = Math.Min(minStart, track.Segments[index].BeatStart);

        // Left bound: the first selected note after any unselected note must not cross that unselected note's end beat.
        var delta = drag.BeatDelta;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (selectedIds.Contains(ordered[i].Id))
                continue;

            for (var j = i + 1; j < ordered.Count; j++)
            {
                if (!selectedIds.Contains(ordered[j].Id))
                    continue;
                delta = Math.Max(delta, ordered[i].BeatEnd - ordered[j].BeatStart);
                break;
            }
        }
        delta = Math.Max(delta, 1 - minStart);

        foreach (var index in drag.MultiIndices)
        {
            var segment = track.Segments[index];
            drag.Previews[index] = new NotePreview(
                segment.BeatStart, segment.BeatEnd,
                segment.BeatStart + delta, segment.BeatEnd + delta);
        }

        // Scan right from the leftmost selected note: when an unselected note is overlapped, shift it and all following notes.
        var cursorEnd = double.MinValue;
        double? push = null;
        for (var i = firstSelected; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var isSelected = selectedIds.Contains(current.Id);
            var start = isSelected ? current.BeatStart + delta : current.BeatStart;
            var end = isSelected ? current.BeatEnd + delta : current.BeatEnd;

            if (!isSelected)
            {
                push ??= cursorEnd > start + 1e-9 ? cursorEnd - start : null;
                if (push is double shift)
                {
                    start += shift;
                    end += shift;
                    var index = track.Segments.FindIndex(s => ReferenceEquals(s, current));
                    if (index >= 0 && shift > 1e-9)
                        drag.Shifts[index] = shift;
                }
            }

            cursorEnd = Math.Max(cursorEnd, end);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_drag is null)
            return;

        if (_drag.Kind == DragKind.Pencil)
        {
            var start = _drag.OriginalBeat;
            var end = Math.Max(start + MinNoteBeats, _drag.DrawEndBeat);
            var semitone = _drag.OriginalSemitone;
            _drag = null;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            NoteDrawRequested?.Invoke(this, new NoteDrawEventArgs(start, end, semitone));
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_drag.Kind == DragKind.Box)
        {
            CompleteBoxSelection(_drag);
            _drag = null;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_drag.Kind == DragKind.Gradient)
        {
            var edited = _drag.EditBegun;
            _drag = null;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            if (edited)
                EditCommitted?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var wasPlayhead = _drag.Kind == DragKind.Playhead;
        var originalPlayhead = _drag.OriginalBeat;
        CommitDrag(_drag);
        _drag = null;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        // After release the playhead returns to the plot center and the track shifts accordingly; a click without dragging keeps the viewport fixed.
        if (wasPlayhead && PlayheadBeat is double beat && Math.Abs(beat - originalPlayhead) > 0.001)
            CenterViewOnPlayhead(beat);

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_drag is null)
            Cursor = Cursors.Arrow;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);

        var layout = Layout();
        if (layout is null)
            return;

        var position = e.GetPosition(this);
        if (position.X < layout.Plot.Left || position.X > layout.Plot.Right
            || position.Y < layout.Plot.Top || position.Y > layout.Plot.Bottom)
            return;

        var beat = Math.Max(1, Snap(layout.BeatAt(position.X)));
        var semitone = Math.Clamp(layout.SemitoneAt(position.Y), layout.Low, layout.High);

        if (Mode == PianoRollMode.Notes)
        {
            NoteAddRequested?.Invoke(this, new NoteAddEventArgs(beat, semitone));
        }
        else
        {
            AddPitchPoint(beat, semitone);
        }

        Focus();
        InvalidateVisual();
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    private void CommitDrag(DragState drag)
    {
        if (drag.Kind == DragKind.Playhead)
            return;

        var track = Track;
        if (track is null)
            return;

        if (drag.Kind == DragKind.Point)
        {
            if (drag.Index < 0 || drag.Index >= track.PitchLine.Count)
                return;
            if (drag.SemitoneDelta == 0)
                return;

            // Nodes lock their beat and move only vertically.
            track.PitchLine[drag.Index].Pitch =
                PitchMath.ToPitchName(drag.OriginalSemitone + drag.SemitoneDelta);
            SelectedPitchIndex = drag.Index;
            return;
        }

        if (drag.Index < 0 || drag.Index >= track.Segments.Count)
            return;

        // A click without real dragging: do not change the model or record an undo step.
        if (!drag.EditBegun)
            return;

        if (drag.MultiIndices.Count > 0)
        {
            CommitMultiDrag(track, drag);
            return;
        }
        var startMoved = Math.Abs(drag.PreviewStart - drag.OriginalBeat) > 1e-9;
        var endMoved = Math.Abs(drag.PreviewEnd - drag.OriginalBeatEnd) > 1e-9;
        var pitchMoved = drag.Kind == DragKind.Note && drag.SemitoneDelta != 0;
        if (!startMoved && !endMoved && !pitchMoved && drag.Shifts.Count == 0)
            return;

        var segment = track.Segments[drag.Index];
        var originalStart = drag.OriginalBeat;
        var originalEnd = drag.OriginalBeatEnd;

        segment.BeatStart = drag.PreviewStart;
        segment.BeatEnd = drag.PreviewEnd;
        if (drag.Kind == DragKind.Note)
            segment.DstPitch = PitchMath.ToPitchName(drag.OriginalSemitone + drag.SemitoneDelta);

        if (drag.Kind == DragKind.Note)
            MovePitchPoints(track, segment.Id, drag.PreviewStart - originalStart,
                drag.SemitoneDelta);
        else
            ScalePitchPoints(track, segment.Id, originalStart, originalEnd,
                drag.PreviewStart, drag.PreviewEnd);

        // Notes pushed by the drag shift as a whole, and their points follow.
        foreach (var (index, delta) in drag.Shifts)
        {
            if (index < 0 || index >= track.Segments.Count)
                continue;

            var other = track.Segments[index];
            other.BeatStart += delta;
            other.BeatEnd += delta;
            MovePitchPoints(track, other.Id, delta, 0);
        }

        // Rebuild transition nodes after overlap changes and push regular control points out of overlap regions.
        SegmentLayout.SyncTransitionPoints(track);

        EditCommitted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Commit a multi-move: selected notes shift together, independently box-selected points move too, and collided notes are pushed back.</summary>
    private void CommitMultiDrag(TrackDocument track, DragState drag)
    {
        var selectedIds = new HashSet<string>();
        foreach (var index in drag.MultiIndices)
        {
            if (index >= 0 && index < track.Segments.Count)
                selectedIds.Add(track.Segments[index].Id);
        }

        var delta = drag.PreviewStart - drag.OriginalBeat;
        var semitone = drag.SemitoneDelta;
        if (Math.Abs(delta) < 1e-9 && semitone == 0)
            return;

        foreach (var (index, preview) in drag.Previews)
        {
            if (index < 0 || index >= track.Segments.Count)
                continue;

            var segment = track.Segments[index];
            var moveDelta = preview.Start - preview.OriginalStart;
            segment.BeatStart = preview.Start;
            segment.BeatEnd = preview.End;
            if (semitone != 0)
                segment.DstPitch = PitchMath.ToPitchName(SemitoneOf(segment.DstPitch) + semitone);
            MovePitchPoints(track, segment.Id, moveDelta, semitone);
        }

        // Points selected on their own whose owning note is not selected also move with the group.
        foreach (var point in drag.MultiPoints)
        {
            if (!track.PitchLine.Contains(point))
                continue;
            if (point.SegmentId is string owner && selectedIds.Contains(owner))
                continue;

            point.Beat = Math.Max(1, point.Beat + delta);
            if (semitone != 0 && !point.IsTransition && point.Pitch is not null)
                point.Pitch = PitchMath.ToPitchName(SemitoneOf(point.Pitch) + semitone);
        }

        foreach (var (index, shift) in drag.Shifts)
        {
            if (index < 0 || index >= track.Segments.Count)
                continue;

            var other = track.Segments[index];
            other.BeatStart += shift;
            other.BeatEnd += shift;
            MovePitchPoints(track, other.Id, shift, 0);
        }

        SegmentLayout.SyncTransitionPoints(track);
        EditCommitted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Batch delete after box selection: Del / BackSpace removes notes together with their points.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Delete or Key.Back))
            return;
        if (_selectedSegmentIds.Count == 0 && _selectedPoints.Count == 0)
            return;

        DeleteSelection();
        e.Handled = true;
    }

    /// <summary>Ctrl+A: select every note and every visible pitch control point of the track.</summary>
    private void SelectAll()
    {
        var track = Track;
        if (track is null)
            return;

        _selectedSegmentIds.Clear();
        _selectedPoints.Clear();

        var firstNote = -1;
        for (var i = 0; i < track.Segments.Count; i++)
        {
            _selectedSegmentIds.Add(track.Segments[i].Id);
            if (firstNote < 0)
                firstNote = i;
        }

        var overlaps = SegmentLayout.Overlaps(track.Segments);
        var firstPoint = -1;
        for (var i = 0; i < track.PitchLine.Count; i++)
        {
            var point = track.PitchLine[i];
            if (point.Pitch is null || point.IsEdgeAnchor
                || !PitchMath.TryParseSemitones(point.Pitch, out _))
                continue;
            if (!point.IsTransition && IsInsideOverlap(overlaps, point.Beat))
                continue;

            _selectedPoints.Add(point);
            if (firstPoint < 0)
                firstPoint = i;
        }

        SelectedNoteIndex = firstNote >= 0 ? firstNote : null;
        if (Mode == PianoRollMode.PitchLine)
            SelectedPitchIndex = firstPoint >= 0 ? firstPoint : null;

        InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Wheel: pan / zoom
    // ------------------------------------------------------------------
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        var layout = Layout();
        if (layout is null)
            return;

        var direction = e.Delta > 0 ? 1 : -1;
        var position = e.GetPosition(this);
        var modifiers = Keyboard.Modifiers;

        if (modifiers.HasFlag(ModifierKeys.Control))
            ZoomBeatsAt(position.X, direction);
        else if (modifiers.HasFlag(ModifierKeys.Alt))
            ZoomPitchAt(position.Y, direction);
        else if (modifiers.HasFlag(ModifierKeys.Shift))
            _viewStartBeat = Math.Max(1, _viewStartBeat - direction * layout.VisibleBeats * 0.12);
        else
            _pitchScroll = Math.Clamp(_pitchScroll + direction, -MaxPitchScroll, MaxPitchScroll);

        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Ctrl+wheel: zoom the time axis while the beat under the cursor stays put.</summary>
    private void ZoomBeatsAt(double x, int direction)
    {
        var layout = Layout();
        if (layout is null)
            return;

        var anchor = layout.BeatAt(x);
        var zoom = Math.Clamp(_beatZoom * (direction > 0 ? 1.25 : 0.8), MinBeatZoom, MaxBeatZoom);
        if (Math.Abs(zoom - _beatZoom) < 1e-6)
            return;
        _beatZoom = zoom;

        var plotWidth = Math.Max(1, ActualWidth - GutterWidth - RightPad);
        var basePxPerBeat = Math.Max(MinPxPerBeat, plotWidth / Math.Max(1, TotalBeats()));
        var pxPerBeat = Math.Max(4, basePxPerBeat * _beatZoom);
        _viewStartBeat = Math.Max(1, anchor - Math.Max(0, x - GutterWidth) / pxPerBeat);
    }

    /// <summary>Alt+wheel: zoom the visible pitch range while the row under the cursor stays put.</summary>
    private void ZoomPitchAt(double y, int direction)
    {
        var layout = Layout();
        if (layout is null)
            return;

        var fraction = Math.Clamp((layout.Plot.Bottom - y) / Math.Max(1, layout.Plot.Height), 0, 1);
        var semitone = layout.SemitoneAt(y);
        var zoom = Math.Clamp(_pitchZoom * (direction > 0 ? 1.2 : 1 / 1.2), 0.4, 3);
        if (Math.Abs(zoom - _pitchZoom) < 1e-6)
            return;
        _pitchZoom = zoom;

        var (autoLow, autoHigh) = PitchRange(Track?.Segments ?? [], Track?.PitchLine ?? [], RestRegions);
        var baseRows = autoHigh - autoLow + 1;
        var rows = Math.Clamp((int)Math.Round(baseRows * _pitchZoom), 6, MaxPitchRows);
        var desiredLow = semitone - fraction * (rows - 1);
        _pitchScroll = Math.Clamp(
            (int)Math.Round(desiredLow) - (autoLow - (rows - baseRows) / 2),
            -MaxPitchScroll, MaxPitchScroll);
    }

    private void DeleteSelection()
    {
        var track = Track;
        if (track is null)
            return;

        EditStarted?.Invoke(this, EventArgs.Empty);

        if (_selectedSegmentIds.Count > 0)
        {
            var ids = new HashSet<string>(_selectedSegmentIds);
            track.Segments.RemoveAll(s => ids.Contains(s.Id));
            track.PitchLine.RemoveAll(p => p.SegmentId is not null && ids.Contains(p.SegmentId));
        }

        if (_selectedPoints.Count > 0)
            track.PitchLine.RemoveAll(p => _selectedPoints.Contains(p));

        _selectedSegmentIds.Clear();
        _selectedPoints.Clear();
        SegmentLayout.SyncTransitionPoints(track);
        track.SortPitchLine();
        SelectedNoteIndex = null;
        SelectedPitchIndex = null;
        EditCommitted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>After resizing a note, its Pitch nodes are mapped proportionally into the new beat range.</summary>
    private static void ScalePitchPoints(
        TrackDocument track, string segmentId,
        double originalStart, double originalEnd, double start, double end)
    {
        var originalLength = originalEnd - originalStart;
        if (originalLength <= 1e-9)
            return;

        var changed = false;
        foreach (var point in track.PitchLine)
        {
            if (point.Pitch is null || point.SegmentId != segmentId)
                continue;
            if (point.IsTransition)
                continue;

            var ratio = (point.Beat - originalStart) / originalLength;
            point.Beat = Math.Max(1, start + ratio * (end - start));
            changed = true;
        }

        if (changed)
            track.SortPitchLine();
    }

    /// <summary>Points owned by the given note move with it (beat and pitch together).</summary>
    private static void MovePitchPoints(
        TrackDocument track, string segmentId, double beatDelta, int semitoneDelta)
    {
        if (Math.Abs(beatDelta) < 1e-9 && semitoneDelta == 0)
            return;

        var changed = false;
        foreach (var point in track.PitchLine)
        {
            if (point.Pitch is null || point.SegmentId != segmentId)
                continue;

            point.Beat = Math.Max(1, point.Beat + beatDelta);
            if (semitoneDelta != 0 && !point.IsTransition)
                point.Pitch = PitchMath.ToPitchName(SemitoneOf(point.Pitch) + semitoneDelta);
            changed = true;
        }

        if (changed)
            track.SortPitchLine();
    }

    private void AddPitchPoint(double beat, int semitone)
    {
        var track = Track;
        if (track is null)
            return;

        // Regular control points cannot be placed in overlap regions: those are controlled only by transition nodes.
        if (SegmentLayout.IsInOverlap(track, beat))
            return;

        // The pitch line only exists where a note block exists: right-clicks in empty regions are ignored.
        var owner = track.Segments.FirstOrDefault(
            s => beat >= s.BeatStart - 1e-9 && beat <= s.BeatEnd + 1e-9);
        if (owner is null)
            return;

        var ownerId = owner.Id;
        EditStarted?.Invoke(this, EventArgs.Empty);

        var existing = track.PitchLine.FirstOrDefault(
            p => Math.Abs(p.Beat - beat) < 0.01 && p.SegmentId == ownerId);
        if (existing is not null)
        {
            // Right-clicking an edge anchor converts it into a regular control point so it is visible and draggable.
            existing.IsEdgeAnchor = false;
            existing.Pitch = PitchMath.ToPitchName(semitone);
            SelectedPitchIndex = track.PitchLine.IndexOf(existing);
        }
        else
        {
            track.PitchLine.Add(new PitchPoint
            {
                SegmentId = owner?.Id,
                Beat = beat,
                Pitch = PitchMath.ToPitchName(semitone),
            });
            track.SortPitchLine();
            SelectedPitchIndex = track.PitchLine.FindIndex(
                p => Math.Abs(p.Beat - beat) < 0.01 && p.SegmentId == ownerId);
        }
    }

    private (double BeatStart, double BeatEnd, int Semitone) NoteState(int index, VoiceSegment segment)
    {
        if (_drag is { } drag)
        {
            // Multi-move: every selected note is drawn using its own preview range.
            if (drag.Previews.TryGetValue(index, out var preview))
                return (preview.Start, preview.End, drag.OriginalSemitone + drag.SemitoneDelta);

            // The dragged note is drawn using its preview range (dragging ends changes length only, not pitch).
            if (drag.Index == index
                && drag.Kind is DragKind.Note or DragKind.NoteStart or DragKind.NoteEnd)
            {
                var semitone = drag.Kind == DragKind.Note
                    ? drag.OriginalSemitone + drag.SemitoneDelta
                    : SemitoneOf(segment.DstPitch);
                return (drag.PreviewStart, drag.PreviewEnd, semitone);
            }

            // Notes pushed by the drag shift as a whole.
            if (drag.Shifts.TryGetValue(index, out var shift))
                return (segment.BeatStart + shift, segment.BeatEnd + shift,
                    SemitoneOf(segment.DstPitch));
        }

        return (segment.BeatStart, segment.BeatEnd, SemitoneOf(segment.DstPitch));
    }

    private (double Beat, int? Semitone) PointState(int index, PitchPoint point)
    {
        // Points lock their beat: dragging changes pitch only (beats are changed uniformly by the branches below when following a note).
        if (_drag is { Kind: DragKind.Point } drag && drag.Index == index)
            return (drag.OriginalBeat, drag.OriginalSemitone + drag.SemitoneDelta);

        var beat = point.Beat;
        var maybeSemitone = PitchSemitone(point);

        // Multi-move: points moving with selected notes, plus independently box-selected points.
        if (_drag is { Kind: DragKind.Note } multi)
        {
            if (multi.Previews.Count > 0 && point.SegmentId is string ownerId)
            {
                var ownerIndex = SegmentIndexOf(ownerId);
                if (ownerIndex >= 0 && multi.Previews.TryGetValue(ownerIndex, out var preview))
                {
                    var semitone = point.IsTransition
                        ? maybeSemitone
                        : maybeSemitone is int offset ? offset + multi.SemitoneDelta : null;
                    return (Math.Max(1, beat + (preview.Start - preview.OriginalStart)), semitone);
                }
            }

            if (multi.MultiPoints.Contains(point))
            {
                var mainDelta = multi.PreviewStart - multi.OriginalBeat;
                var semitone = point.IsTransition
                    ? maybeSemitone
                    : maybeSemitone is int offset ? offset + multi.SemitoneDelta : null;
                return (Math.Max(1, beat + mainDelta), semitone);
            }
        }

        if (_drag is not null && point.SegmentId is string segmentId)
        {
            var ownerIndex = SegmentIndexOf(segmentId);
            if (ownerIndex >= 0)
            {
                // When dragging a note, its points move along (transition nodes follow only the beat; pitch is independent).
                if (_drag.Kind == DragKind.Note && _drag.Index == ownerIndex)
                {
                    var semitone = point.IsTransition
                        ? maybeSemitone
                        : maybeSemitone is int offset ? offset + _drag.SemitoneDelta : null;
                    return (Math.Max(1, beat + (_drag.PreviewStart - _drag.OriginalBeat)), semitone);
                }

                // When resizing a note, its points map proportionally to the new length.
                if (_drag.Kind is DragKind.NoteStart or DragKind.NoteEnd
                    && _drag.Index == ownerIndex && maybeSemitone is int resizeSemitone)
                {
                    var originalLength = _drag.OriginalBeatEnd - _drag.OriginalBeat;
                    if (originalLength <= 1e-9)
                        return (beat, resizeSemitone);

                    var ratio = (beat - _drag.OriginalBeat) / originalLength;
                    return (Math.Max(1, _drag.PreviewStart + ratio * (_drag.PreviewEnd - _drag.PreviewStart)),
                        resizeSemitone);
                }

                // For notes pushed by the drag, their points shift as a whole.
                if (_drag.Shifts.TryGetValue(ownerIndex, out var shift))
                    return (Math.Max(1, beat + shift), maybeSemitone);
            }
        }

        return (beat, maybeSemitone);
    }

    private int SegmentIndexOf(string segmentId)
    {
        var segments = Track?.Segments;
        if (segments is null)
            return -1;

        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].Id == segmentId)
                return i;
        }
        return -1;
    }

    // ------------------------------------------------------------------
    private RollLayout? Layout()
    {
        if (ActualWidth <= 8 || ActualHeight <= 8)
            return null;

        var track = Track;
        return BuildLayout(
            new Size(ActualWidth, ActualHeight),
            track?.Segments ?? [],
            track?.PitchLine ?? []);
    }

    /// <summary>Hovering the playhead shows the horizontal resize cursor; hovering note ends shows the resize cursor.</summary>
    private void UpdateHoverCursor(Point position)
    {
        var layout = Layout();
        if (layout is null)
        {
            Cursor = Cursors.Arrow;
            return;
        }

        if (IsPencil && Mode == PianoRollMode.Notes
            && position.X >= layout.Plot.Left && position.X <= layout.Plot.Right
            && position.Y >= layout.Plot.Top && position.Y <= layout.Plot.Bottom)
        {
            Cursor = Cursors.Cross;
            return;
        }

        if (Math.Abs(position.X - layout.X(PlayheadDisplayBeat)) <= PlayheadHitRadius
            && position.Y >= 0 && position.Y <= layout.Plot.Bottom)
        {
            Cursor = Cursors.SizeWE;
            return;
        }

        if (HitGradientHandle(layout, position) is not null)
        {
            Cursor = Cursors.SizeWE;
            return;
        }

        if (Mode == PianoRollMode.Notes)
        {
            var index = HitNote(layout, position);
            if (index >= 0)
            {
                var segment = layout.Segments[index];
                var box = layout.NoteBox(segment.BeatStart, segment.BeatEnd, SemitoneOf(segment.DstPitch));
                Cursor = DragKindAt(box, position) == DragKind.Note ? Cursors.SizeAll : Cursors.SizeWE;
                return;
            }
        }

        Cursor = Cursors.Arrow;
    }

    /// <summary>Both ends of a note have a resize zone; the middle is used for moving it.</summary>
    private static DragKind DragKindAt(Rect box, Point position)
    {
        if (box.IsEmpty)
            return DragKind.Note;

        var edge = Math.Min(EdgeGrabRadius, Math.Max(2.5, box.Width / 3));
        if (position.X <= box.Left + edge)
            return DragKind.NoteStart;
        if (position.X >= box.Right - edge)
            return DragKind.NoteEnd;
        return DragKind.Note;
    }

    /// <summary>The playhead is clamped between beat 1 and the end of the track.</summary>
    private double ClampBeat(double beat) => Math.Clamp(beat, 1, Math.Max(1, TotalBeats()));

    private static int HitNote(RollLayout layout, Point position)
    {
        var segments = layout.Segments;
        for (var i = segments.Count - 1; i >= 0; i--)
        {
            var segment = segments[i];
            var box = layout.NoteBox(segment.BeatStart, segment.BeatEnd, SemitoneOf(segment.DstPitch));
            if (!box.IsEmpty && box.Contains(position))
                return i;
        }
        return -1;
    }

    private static int HitPoint(RollLayout layout, Point position)
    {
        var best = -1;
        var bestDistance = HitRadius;
        var points = layout.Points;
        var overlaps = SegmentLayout.Overlaps(layout.Segments);

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (point.Pitch is null || point.IsEdgeAnchor
                || !PitchMath.TryParseSemitones(point.Pitch, out var semitones))
                continue;

            // Regular control points in overlap regions are not drawn and do not respond to hit testing.
            if (!point.IsTransition && IsInsideOverlap(overlaps, point.Beat))
                continue;

            var node = new Point(layout.X(point.Beat), layout.RowCenter((int)Math.Round(semitones)));
            var distance = (node - position).Length;
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private static double Snap(double value) => Math.Round(value / BeatSnap) * BeatSnap;

    private static int SemitoneOf(string? pitch, int fallback = 0) =>
        pitch is not null && PitchMath.TryParseSemitones(pitch, out var value)
            ? (int)Math.Round(value)
            : fallback;

    private static (int Low, int High) PitchRange(
        IReadOnlyList<VoiceSegment> segments,
        IReadOnlyList<PitchPoint> points,
        IReadOnlyList<RestRegion>? rests)
    {
        var any = false;
        var low = 0;
        var high = 11;

        void Extend(int semitone)
        {
            if (!any)
            {
                low = high = semitone;
                any = true;
                return;
            }
            low = Math.Min(low, semitone);
            high = Math.Max(high, semitone);
        }

        foreach (var segment in segments)
            Extend(SemitoneOf(segment.DstPitch));
        foreach (var point in points)
        {
            if (point.Pitch is not null && PitchMath.TryParseSemitones(point.Pitch, out var value))
                Extend((int)Math.Round(value));
        }
        foreach (var rest in rests ?? [])
            Extend(rest.Semitone);

        if (!any)
            return (0, 11);

        low -= 2;
        high += 2;
        if (high - low < 11)
        {
            var missing = 12 - (high - low);
            low -= missing / 2;
            high += missing - missing / 2;
        }

        return (low, high);
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

    private static Brush Frozen(Brush brush)
    {
        if (brush.CanFreeze && !brush.IsFrozen)
            brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------------
    private enum DragKind
    {
        Note,
        NoteStart,
        NoteEnd,
        Point,
        Playhead,
        Box,
        Gradient,
        Pencil,
    }

    /// <summary>Original and preview ranges of each note during a multi-selection drag.</summary>
    private readonly record struct NotePreview(
        double OriginalStart, double OriginalEnd, double Start, double End);

    private sealed class DragState(
        DragKind kind,
        int index,
        Point start,
        double originalBeat,
        double originalBeatEnd,
        int originalSemitone)
    {
        public DragKind Kind { get; } = kind;
        public int Index { get; } = index;
        public Point Start { get; } = start;
        public double OriginalBeat { get; } = originalBeat;
        public double OriginalBeatEnd { get; } = originalBeatEnd;
        public int OriginalSemitone { get; } = originalSemitone;
        public double BeatDelta { get; set; }
        public int SemitoneDelta { get; set; }

        /// <summary>Preview range of the dragged note (used when moving/resizing the note as a whole).</summary>
        public double PreviewStart { get; set; } = originalBeat;
        public double PreviewEnd { get; set; } = originalBeatEnd;

        /// <summary>Other notes pushed by the drag: index -> shift amount.</summary>
        public Dictionary<int, double> Shifts { get; } = [];

        /// <summary>Multi-move: indices of notes participating in the group move.</summary>
        public List<int> MultiIndices { get; } = [];

        /// <summary>Multi-move: box-selected points (including points whose note is also selected; deduplicated by ownership on commit).</summary>
        public List<PitchPoint> MultiPoints { get; } = [];

        /// <summary>Per-note preview ranges for a multi-move.</summary>
        public Dictionary<int, NotePreview> Previews { get; } = [];

        /// <summary>Current mouse position of the box-selection rectangle.</summary>
        public Point Current { get; set; } = start;

        /// <summary>Gradient handle drag direction: -1 at the band's left end, +1 at the right end.</summary>
        public int Direction { get; set; } = 1;

        /// <summary>Pencil: current end beat of the drawn range.</summary>
        public double DrawEndBeat { get; set; }

        /// <summary>Whether an undo snapshot has been recorded (set on the first actual movement).</summary>
        public bool EditBegun { get; set; }
    }

    /// <summary>Geometry mapping shared by one draw/hit-test pass.</summary>
    private sealed class RollLayout(
        Rect plot,
        int low,
        int high,
        int rows,
        double totalBeats,
        int beatsPerBar,
        IReadOnlyList<VoiceSegment> segments,
        IReadOnlyList<PitchPoint> points,
        double pxPerBeat,
        double viewStartBeat)
    {
        public Rect Plot { get; } = plot;
        public int Low { get; } = low;
        public int High { get; } = high;
        public double TotalBeats { get; } = totalBeats;
        public int BeatsPerBar { get; } = beatsPerBar;
        public IReadOnlyList<VoiceSegment> Segments { get; } = segments;
        public IReadOnlyList<PitchPoint> Points { get; } = points;
        public double RowHeight => Plot.Height / rows;
        public double PxPerBeat { get; } = pxPerBeat;
        public double ViewStartBeat { get; } = viewStartBeat;
        public double VisibleBeats => Plot.Width / PxPerBeat;

        public double X(double beat) => Plot.Left + (beat - ViewStartBeat) * PxPerBeat;

        public double BeatAt(double x) => ViewStartBeat + (x - Plot.Left) / PxPerBeat;

        public double RowTop(int semitone) => Plot.Bottom - (semitone - Low + 1) * RowHeight;

        public double RowCenter(int semitone) => RowTop(semitone) + RowHeight / 2;

        public int SemitoneAt(double y) =>
            Math.Clamp(Low + (int)Math.Floor((Plot.Bottom - y) / RowHeight), Low, High);

        public Rect NoteBox(double beatStart, double beatEnd, int semitone)
        {
            var x1 = X(beatStart);
            var x2 = X(beatEnd);
            if (x2 <= Plot.Left || x1 >= Plot.Right)
                return Rect.Empty;

            return new Rect(
                x1 + 1,
                RowTop(semitone) + 1,
                Math.Max(2, x2 - x1 - 2),
                Math.Max(4, RowHeight - 2));
        }

        public bool IsBarStart(double beat)
        {
            var offset = beat - 1;
            return Math.Abs(offset - Math.Round(offset / BeatsPerBar) * BeatsPerBar) < 1e-6;
        }
    }
}
