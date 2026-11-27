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

    /// <summary>Upper bound of the opening articulation in beats; the steady share below always wins.</summary>
    private const double WindowBeats = 0.25;

    /// <summary>Share of the note that must stay on its own steady pitch; the articulation gets the rest.</summary>
    private const double MinSteadyRatio = 0.90;

    /// <summary>Shortest articulation that is still worth drawing, in beats.</summary>
    private const double MinWindowBeats = 0.02;

    /// <summary>
    /// Overshoot peak and counter-swing trough, as fractions of the window. The incoming slide gets
    /// the larger share so the boundary is a visible glide rather than an almost vertical climb.
    /// </summary>
    private const double PeakAt = 0.45;
    private const double TroughAt = 0.75;

    /// <summary>Overshoot depth as a fraction of the interval, clamped to a musical range (semitones).</summary>
    private const double OvershootRatio = 0.30;
    private const double MinOvershoot = 0.12;
    private const double MaxOvershoot = 0.70;

    /// <summary>The counter-swing is a damped echo of the overshoot: one oscillation in total.</summary>
    private const double CounterRatio = 0.45;

    /// <summary>Node samples written when the track still uses the sparse pitch line.</summary>
    private const int NodeSteps = 8;

    /// <summary>Smallest interval (semitones) that still produces an articulation.</summary>
    private const double MinInterval = 0.02;

    /// <summary>
    /// Rewrites the automatic layer of one note: the incoming pitch on its start anchor plus the
    /// opening overshoot. <see cref="PitchRaster"/> cells are only touched once the track already
    /// stores its curve that way; otherwise tagged pitch nodes are used.
    /// </summary>
    public static void Apply(LyricDocument lyric, VoiceSegment? segment)
    {
        if (segment is null)
            return;
        if (!PitchMath.TryParseSemitones(segment.DstPitch, out var target))
            return;

        var (previous, _) = SegmentLayout.Neighbors(lyric, segment);
        var reference = PitchMath.TryParseSemitones(previous?.DstPitch ?? BasePitch, out var incoming)
            ? incoming
            : target;
        var interval = target - reference;

        if (lyric.PitchRaster is { HasValues: true } raster)
        {
            if (TryBuild(segment, interval, out var start, out var end,
                    out var overshoot, out var counter))
                WriteRaster(raster, start, end, target, interval, overshoot, counter);
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

        if (!HasManualNode(lyric, segment)
            && TryBuild(segment, interval, out var contourStart, out var contourEnd,
                out var contourOvershoot, out var contourCounter))
            AddContourNodes(lyric, segment, contourStart, contourEnd, target, interval,
                contourOvershoot, contourCounter);

        lyric.SortPitchLine();
    }

    /// <summary>
    /// Regenerates every note's contour. Only sparse pitch lines are rebuilt: a raster may hold a
    /// hand-painted or imported curve, which must never be overwritten in bulk.
    /// </summary>
    public static void RefreshAll(LyricDocument lyric)
    {
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

    private static bool TryBuild(VoiceSegment segment, double interval,
        out double start, out double end, out double overshoot, out double counter)
    {
        start = end = overshoot = counter = 0;

        if (Math.Abs(interval) < MinInterval)
            return false;

        var length = segment.BeatEnd - segment.BeatStart;
        if (!double.IsFinite(length) || length <= 1e-6)
            return false;

        // The steady pitch must cover at least MinSteadyRatio of the note, so the articulation only
        // ever uses the remaining share (and never more than WindowBeats).
        var window = Math.Min(WindowBeats, length * (1.0 - MinSteadyRatio));
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
        for (var step = 1; step <= NodeSteps; step++)
        {
            var u = (double)step / NodeSteps;
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
