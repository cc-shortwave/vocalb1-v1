using System.Globalization;
using VocalB1.Core.Pitch;

namespace VocalB1.Core.Models;

/// <summary>
/// Automatic PIT contour: a note starts from the previous note's pitch (D4 for the first note),
/// sweeps past its own pitch in the direction of the interval (overshoot), makes one counter-swing,
/// settles, and then holds its pitch until the note ends. The boundary between two notes is therefore
/// a continuous slide inside the new note's articulation window instead of a vertical jump, and the
/// steady pitch keeps at least <see cref="MinSteadyRatio"/> of every note.
/// The contour is written into the pitch curve itself (raster cells, or tagged pitch nodes when the
/// track still uses the sparse line), so the piano roll shows exactly what the engine renders.
/// Adding a manual pitch node inside a note takes it over: the generated part is dropped and never
/// recreated, which is how the contour stays adjustable without extra parameters.
/// </summary>
public static class AutoPitchCurve
{
    /// <summary>Reference pitch of the first note (the legacy voicebank's recorded base pitch).</summary>
    public const string BasePitch = "D4";

    /// <summary>
    /// Full attack transient (voice onset -> flat steady pitch) in milliseconds. Human singing measures
    /// 120-220 ms; the upper half is used so the return after the peak can be unhurried.
    /// </summary>
    private const double AttackMs = 200.0;

    /// <summary>Time from onset to the overshoot peak; physiology: 20-60 ms.</summary>
    private const double PeakMs = 40.0;

    /// <summary>Time to the damped counter-swing before the pitch settles; one oscillation in total.</summary>
    private const double TroughMs = 130.0;

    /// <summary>Share of the note that must stay on its own steady pitch; the articulation gets the rest.</summary>
    private const double MinSteadyRatio = 0.90;

    /// <summary>Shortest articulation that is still worth drawing, in beats.</summary>
    private const double MinWindowBeats = 0.02;

    /// <summary>
    /// Overshoot peak and counter-swing trough, as fractions of the window. The incoming slide gets
    /// the larger share so the boundary is a visible glide rather than an almost vertical climb.
    /// </summary>
    private const double PeakAt = PeakMs / AttackMs;
    private const double TroughAt = TroughMs / AttackMs;

    /// <summary>Overshoot depth as a fraction of the interval, clamped to a musical range (semitones).</summary>
    private const double OvershootRatio = 0.45;
    private const double MinOvershoot = 0.30;
    private const double MaxOvershoot = 1.00;

    /// <summary>The counter-swing is a damped echo of the overshoot: one oscillation in total.</summary>
    private const double CounterRatio = 0.40;

    /// <summary>Node samples written when the track still uses the sparse pitch line.</summary>
    private const int NodeSteps = 8;

    /// <summary>Smallest interval (semitones) that still produces an articulation.</summary>
    private const double MinInterval = 0.02;

    /// <summary>
    /// Rewrites the automatic layer of one note: the incoming pitch on its start anchor plus the
    /// opening overshoot. <see cref="PitchRaster"/> cells are only touched once the track already
    /// stores its curve that way; otherwise tagged pitch nodes are used.
    /// </summary>
    public static void Apply(LyricDocument lyric, VoiceSegment? segment, double bpm = 0)
    {
        if (segment is null)
            return;
        if (bpm > 0)
            lyric.Bpm = bpm;
        if (!PitchMath.TryParseSemitones(segment.DstPitch, out var target))
            return;

        var (previous, next) = SegmentLayout.Neighbors(lyric, segment);

        // A note starts from where the previous curve ended; a gap keeps that rule because the
        // previous curve now simply ends on its own pitch.
        var reference = PitchMath.TryParseSemitones(previous?.DstPitch ?? BasePitch, out var incoming)
            ? incoming
            : target;
        var interval = target - reference;
        var manual = HasManualNode(lyric, segment);

        if (lyric.PitchRaster is { HasValues: true } raster)
        {
            var attackEnd = segment.BeatStart;
            if (TryBuild(segment, interval, lyric.Bpm, out var start, out var end,
                    out var overshoot, out var counter))
            {
                WriteRaster(raster, start, end, target, interval, overshoot, counter);
                attackEnd = end;
            }

            // Clear a previously written closing fall and keep the tail on the steady pitch.
            var steadyFrom = Math.Max(attackEnd,
                segment.BeatEnd - (segment.BeatEnd - segment.BeatStart) * (1.0 - MinSteadyRatio));
            WriteSteady(raster, steadyFrom, segment.BeatEnd, target);

            // The steady body is authored by the automatic layer as well: without it a raster that
            // only holds articulation cells would leave the note without a drawn pitch line. Existing
            // (possibly hand-painted) cells are kept.
            if (!manual)
                FillMissingSteady(raster, attackEnd, steadyFrom, target);
            return;
        }

        // The generated layer owns the sparse curve unless a hand-placed node sits inside the note.
        RemoveNodes(lyric, segment);

        // The hidden start anchor carries the incoming pitch: the previous note's pitch flows into
        // this one instead of stepping at the boundary, and a note too short for an articulation
        // window still slides smoothly across its whole length.
        var startAnchor = FindStartAnchor(lyric, segment);
        if (startAnchor is not null)
            startAnchor.Pitch = reference.ToString("0.####", CultureInfo.InvariantCulture);

        if (!manual
            && TryBuild(segment, interval, lyric.Bpm, out var contourStart, out var contourEnd,
                out var contourOvershoot, out var contourCounter))
            AddContourNodes(lyric, segment, contourStart, contourEnd, target, interval,
                contourOvershoot, contourCounter);

        // Hold the steady pitch up to the end of the note. Without this node a note followed by a
        // rest would end its curve right after the attack, leaving the body without a drawn line.
        if (!manual && lyric.PitchLine.All(p =>
                p.SegmentId != segment.Id || Math.Abs(p.Beat - segment.BeatEnd) > 1e-3))
        {
            lyric.PitchLine.Add(new PitchPoint
            {
                SegmentId = segment.Id,
                IsAutoPitch = true,
                Beat = segment.BeatEnd - 1e-4,
                Pitch = target.ToString("0.####", CultureInfo.InvariantCulture),
            });
        }

        // A closing anchor left at a released pitch by an older project returns to the note pitch.
        if (next is null && !manual)
        {
            var closing = lyric.PitchLine.FirstOrDefault(p =>
                p.SegmentId == segment.Id && p.IsEdgeAnchor && !p.IsTransition
                && Math.Abs(p.Beat - segment.BeatEnd) < 1e-6);
            if (closing is not null)
                closing.Pitch = target.ToString("0.####", CultureInfo.InvariantCulture);
        }

        lyric.SortPitchLine();
    }

    /// <summary>Rebuilds a note and both neighbours: moving or resizing a note changes their connectivity.</summary>
    public static void RefreshAround(LyricDocument lyric, VoiceSegment? segment, double bpm = 0)
    {
        if (segment is null)
            return;
        var (previous, next) = SegmentLayout.Neighbors(lyric, segment);
        Apply(lyric, previous, bpm);
        Apply(lyric, segment, bpm);
        Apply(lyric, next, bpm);
    }

    /// <summary>
    /// Regenerates every note's contour. Only sparse pitch lines are rebuilt: a raster may hold a
    /// hand-painted or imported curve, which must never be overwritten in bulk.
    /// </summary>
    public static void RefreshAll(LyricDocument lyric, double bpm = 0)
    {
        if (bpm > 0)
            lyric.Bpm = bpm;
        if (lyric.PitchRaster is { HasValues: true })
            return;
        foreach (var segment in lyric.Segments)
            Apply(lyric, segment);
    }

    /// <summary>Pitch offset (semitones) of the contour at <paramref name="u"/> in [0, 1]; 0 = the note's own pitch.</summary>
    public static double OffsetAt(double u, double interval, double overshoot, double counter)
    {
        if (u <= 0)
            return -interval;              // the previous note's pitch
        if (u >= 1)
            return 0;
        if (u < PeakAt)
            return Lerp(-interval, overshoot, Smooth(u / PeakAt));
        if (u < TroughAt)
            return Lerp(overshoot, counter, Smooth((u - PeakAt) / (TroughAt - PeakAt)));
        return Lerp(counter, 0, Smooth((u - TroughAt) / (1 - TroughAt)));
    }

    private static bool TryBuild(VoiceSegment segment, double interval, double bpm,
        out double start, out double end, out double overshoot, out double counter)
    {
        start = end = overshoot = counter = 0;

        if (Math.Abs(interval) < MinInterval)
            return false;

        var length = segment.BeatEnd - segment.BeatStart;
        if (!double.IsFinite(length) || length <= 1e-6)
            return false;

        // The transient has a physiological duration in milliseconds, so convert it with the tempo.
        var beatMs = 60000.0 / Math.Clamp(bpm > 0 ? bpm : 120.0, 20.0, 400.0);

        // The steady pitch must cover at least MinSteadyRatio of the note, so the articulation only
        // ever uses the remaining share (and never more than the physiological window).
        var window = Math.Min(AttackMs / beatMs, length * (1.0 - MinSteadyRatio));
        if (window < MinWindowBeats)
            return false;

        start = segment.BeatStart;
        end = start + window;

        var depth = Math.Clamp(Math.Abs(interval) * OvershootRatio, MinOvershoot, MaxOvershoot);
        overshoot = Math.Sign(interval) * depth;
        counter = -overshoot * CounterRatio;
        return true;
    }

    /// <summary>A hand-placed node anywhere inside the note means the user owns its curve.</summary>
    private static bool HasManualNode(LyricDocument lyric, VoiceSegment segment) =>
        lyric.PitchLine.Any(p =>
            p.SegmentId == segment.Id
            && !p.IsAutoPitch && !p.IsEdgeAnchor && !p.IsTransition
            && p.Beat >= segment.BeatStart - 1e-6 && p.Beat <= segment.BeatEnd + 1e-6);

    private static PitchPoint? FindStartAnchor(LyricDocument lyric, VoiceSegment segment) =>
        lyric.PitchLine.FirstOrDefault(p =>
            p.SegmentId == segment.Id && p.IsEdgeAnchor && !p.IsTransition
            && Math.Abs(p.Beat - segment.BeatStart) < 1e-6);

    private static void AddContourNodes(LyricDocument lyric, VoiceSegment segment,
        double start, double end, double target, double interval, double overshoot, double counter)
    {
        var span = end - start;

        // Keep the overshoot peak and the counter-swing trough as exact samples: the peak is the
        // audible part of the articulation and a uniform grid can cut its corner.
        var samples = new List<double>(NodeSteps + 2);
        for (var step = 1; step <= NodeSteps; step++)
            samples.Add((double)step / NodeSteps);
        samples.Add(PeakAt);
        samples.Add(TroughAt);
        samples.Sort();

        var previous = double.NaN;
        foreach (var u in samples)
        {
            if (u <= 1e-9 || Math.Abs(u - previous) < 1e-9)
                continue;
            previous = u;
            lyric.PitchLine.Add(new PitchPoint
            {
                SegmentId = segment.Id,
                IsAutoPitch = true,
                Beat = start + u * span,
                Pitch = (target + OffsetAt(u, interval, overshoot, counter))
                    .ToString("0.####", CultureInfo.InvariantCulture),
            });
        }
    }

    /// <summary>
    /// Restores missing steady cells in a loaded raster: a project saved while a note body was empty
    /// would otherwise draw no pitch line until the note is edited.
    /// </summary>
    public static void RepairMissing(LyricDocument lyric)
    {
        if (lyric.PitchRaster is not { HasValues: true } raster)
            return;
        foreach (var segment in lyric.Segments)
        {
            if (PitchMath.TryParseSemitones(segment.DstPitch, out var target))
                FillMissingSteady(raster, segment.BeatStart, segment.BeatEnd, target);
        }
    }

    /// <summary>Fills a raster span with the steady pitch (body of a note, or restored tail).</summary>
    private static void WriteSteady(PitchRaster raster, double start, double end, double target)
    {
        var first = Math.Max(0, raster.IndexOfBeat(start));
        var last = raster.IndexOfBeat(end - 1e-9);
        for (var index = first; index <= last; index++)
            raster.SetValue(index, Math.Round(target, 4));
    }

    /// <summary>Writes the steady pitch only where the raster has no value at all.</summary>
    private static void FillMissingSteady(PitchRaster raster, double start, double end, double target)
    {
        var first = Math.Max(0, raster.IndexOfBeat(start));
        var last = raster.IndexOfBeat(end - 1e-9);
        for (var index = first; index <= last; index++)
        {
            if (index >= raster.Values.Count)
                break;
            if (raster.Values[index] is not { } value || double.IsNaN(value))
                raster.SetValue(index, Math.Round(target, 4));
        }
    }

    private static void WriteRaster(PitchRaster raster, double start, double end,
        double target, double interval, double overshoot, double counter)
    {
        var first = Math.Max(0, raster.IndexOfBeat(start));
        // Half-open window: the cell containing the end belongs to the next note.
        var last = raster.IndexOfBeat(end - 1e-9);
        var span = Math.Max(1e-9, end - start);
        for (var index = first; index <= last; index++)
        {
            // The boundary cell keeps the incoming pitch so the previous note's last cell and this
            // one meet without a step; the sweep starts from the following cell.
            if (index == first)
            {
                raster.SetValue(index, Math.Round(target - interval, 4));
                continue;
            }

            var beat = raster.BeatOfIndex(index) + raster.CellBeats * 0.5;
            var u = Math.Clamp((beat - start) / span, 0, 1);
            raster.SetValue(index, Math.Round(target + OffsetAt(u, interval, overshoot, counter), 4));
        }
    }

    private static void RemoveNodes(LyricDocument lyric, VoiceSegment segment) =>
        lyric.PitchLine.RemoveAll(p => p.IsAutoPitch && p.SegmentId == segment.Id);

    private static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return 3 * t * t - 2 * t * t * t;
    }

    private static double Lerp(double from, double to, double weight) => from + (to - from) * weight;
}
