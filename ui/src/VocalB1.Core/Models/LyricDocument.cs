using VocalB1.Core.Pitch;

namespace VocalB1.Core.Models;

/// <summary>
/// A lyric data-block: a complete score (words, pronunciation units, segments, pitch line and beat grid)
/// that belongs to exactly one singer. The singer supplies the voicebank and resolves each syllable's
/// phoneme to a wav sample; the lyric itself never stores a track.
/// </summary>
public sealed class LyricDocument : IDatablock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "歌词";

    public bool FakeUser { get; set; }

    /// <summary>
    /// True (default) = syllable samples are phoneme names resolved against the singer's voicebank.
    /// A unit may still override with <see cref="LyricUnit.Audio"/> (dual mode).
    /// </summary>
    public bool ResolveFromVoicebank { get; set; } = true;

    public LyricPhrase Phrase { get; set; } = new();

    public List<VoiceSegment> Segments { get; set; } = [];

    public List<PitchPoint> PitchLine { get; set; } = [];

    /// <summary>
    /// Rasterised pitch curve (1/128-beat cells, continuous semitones) painted with the brush. When it holds values it replaces
    /// <see cref="PitchLine"/> as the pitch source; the render path converts it back to dense points.
    /// </summary>
    public PitchRaster? PitchRaster { get; set; }

    public bool HasPitchRaster => PitchRaster is { HasValues: true };

    /// <summary>End beat of the last segment.</summary>
    public double TotalBeats => Segments.Count == 0 ? 0 : Segments.Max(s => s.BeatEnd);

    /// <summary>
    /// Gives a segment its default pitch line without any visible control point: hidden edge anchors
    /// at the segment start (and at the track end). The synthesiser and the piano roll both sample the
    /// cubic Bézier (smoothstep) curve between neighbouring anchors, so the pitch glides smoothly from
    /// one note into the next instead of stepping at the note border.
    /// </summary>
    public void AddPitchAnchor(VoiceSegment segment)
    {
        if (!PitchMath.TryParseSemitones(segment.DstPitch, out var semitone))
            return;

        // A painted raster is the authoritative curve: only fill this segment's own range so a
        // user-brushed curve on the neighbouring notes is never overwritten.
        if (PitchRaster is { HasValues: true } raster)
        {
            // Half-open range: the cell containing the segment end belongs to the next note.
            var start = Math.Max(0, raster.IndexOfBeat(segment.BeatStart));
            var end = raster.IndexOfBeat(segment.BeatEnd - 1e-9);
            var value = Math.Round(semitone, 3);
            for (var index = start; index <= end; index++)
                raster.SetValue(index, value);
            return;
        }

        if (!PitchLine.Any(p => SameAnchor(p, segment, segment.BeatStart)))
        {
            PitchLine.Add(new PitchPoint
            {
                SegmentId = segment.Id,
                IsEdgeAnchor = true,
                Beat = segment.BeatStart,
                Pitch = segment.DstPitch,
            });
        }

        SortPitchLine();
        RefreshPitchEndAnchor();

        bool SameAnchor(PitchPoint p, VoiceSegment owner, double beat) =>
            p.IsEdgeAnchor && !p.IsTransition && p.SegmentId == owner.Id
            && Math.Abs(p.Beat - beat) < 1e-6;
    }

    /// <summary>
    /// Keeps exactly one closing anchor: it belongs to the last segment so the curve is covered up to
    /// the track end. Anchors that used to close the track are dropped once another note follows them.
    /// </summary>
    private void RefreshPitchEndAnchor()
    {
        if (Segments.Count == 0)
            return;

        var owner = Segments.OrderBy(s => s.BeatStart).Last();
        PitchLine.RemoveAll(p =>
            p.IsEdgeAnchor && !p.IsTransition && p.SegmentId != owner.Id
            && Segments.Any(s => s.Id == p.SegmentId && Math.Abs(p.Beat - s.BeatEnd) < 1e-6));

        if (PitchLine.Any(p => p.IsEdgeAnchor && p.SegmentId == owner.Id
                               && Math.Abs(p.Beat - owner.BeatEnd) < 1e-6))
            return;
        if (!PitchMath.TryParseSemitones(owner.DstPitch, out _))
            return;

        PitchLine.Add(new PitchPoint
        {
            SegmentId = owner.Id,
            IsEdgeAnchor = true,
            Beat = owner.BeatEnd,
            Pitch = owner.DstPitch,
        });
        SortPitchLine();
    }

    public void SortPitchLine() => PitchLine.Sort((a, b) => a.Beat.CompareTo(b.Beat));

    /// <summary>
    /// The pitch sequence used for drawing and synthesis; trailing edge anchors are folded away.
    /// (Same rule as the former per-track implementation, now owned by the lyric score.)
    /// </summary>
    public List<PitchPoint> BuildRenderPitchLine()
    {
        // The painted raster is the authoritative curve; legacy point lists remain supported for
        // projects that were constructed in memory (and for the migration path).
        if (PitchRaster is { } raster && raster.HasValues)
            return raster.ToPitchPoints();

        if (PitchLine.Count == 0)
            return [];

        var sorted = PitchLine.OrderBy(p => p.Beat).ToList();
        var segmentById = new Dictionary<string, VoiceSegment>();
        foreach (var segment in Segments)
            segmentById[segment.Id] = segment;

        var render = sorted.Where(p =>
        {
            if (!p.IsEdgeAnchor || p.IsTransition)
                return true;
            if (ReferenceEquals(p, sorted[0]) || ReferenceEquals(p, sorted[^1]))
                return true;
            return p.SegmentId is not string id
                || !segmentById.TryGetValue(id, out var owner)
                || p.Beat < owner.BeatEnd - 0.001;
        }).ToList();

        foreach (var segment in Segments)
        {
            if (render.Any(p => p.SegmentId == segment.Id))
                continue;

            var anchor = sorted.FirstOrDefault(
                p => p.SegmentId == segment.Id && p.IsEdgeAnchor && !p.IsTransition);
            if (anchor is not null)
                render.Add(anchor);
        }

        var deduped = new List<PitchPoint>();
        foreach (var point in render.OrderBy(p => p.Beat))
        {
            if (deduped.Count > 0
                && Math.Abs(deduped[^1].Beat - point.Beat) < 0.001
                && IsPlainPitch(deduped[^1]) && IsPlainPitch(point))
            {
                if (SegmentStart(deduped[^1]) <= SegmentStart(point))
                    deduped[^1] = point;
                continue;
            }

            deduped.Add(point);
        }

        return deduped;

        double SegmentStart(PitchPoint p) =>
            p.SegmentId is string id && segmentById.TryGetValue(id, out var owner)
                ? owner.BeatStart
                : double.NegativeInfinity;
    }

    private static bool IsPlainPitch(PitchPoint p) =>
        p.Pitch is not null
        && !string.Equals(p.Pitch.Trim(), "KEEP", StringComparison.OrdinalIgnoreCase);

    public LyricDocument Clone() => new()
    {
        Id = Id,
        Name = Name,
        FakeUser = FakeUser,
        ResolveFromVoicebank = ResolveFromVoicebank,
        Phrase = Phrase.Clone(),
        Segments = Segments.Select(s => s.Clone()).ToList(),
        PitchLine = PitchLine.Select(p => p.Clone()).ToList(),
        PitchRaster = PitchRaster?.Clone(),
    };
}
