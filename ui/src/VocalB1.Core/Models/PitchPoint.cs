namespace VocalB1.Core.Models;

/// <summary>
/// Pitch-line endpoint (corresponds to one tuple of PITCH_LINE on the Python side).
/// Between adjacent endpoints, points are sampled frame by frame along a cubic Bézier curve.
/// </summary>
public sealed class PitchPoint
{
    /// <summary><see cref="VoiceSegment.Id"/> of the owning segment; null = unowned (a free node from legacy projects).</summary>
    public string? SegmentId { get; set; }

    /// <summary>
    /// Anchor at the start/end of a segment: only pins the curve to the segment edge; no dot is drawn and it cannot be dragged.
    /// When a point is explicitly added at the edge via right-click, it becomes a normal node.
    /// </summary>
    public bool IsEdgeAnchor { get; set; }

    /// <summary>
    /// Dedicated transition node for overlapping segments: sits at the midpoint of the time overlap between two segments.
    /// Normal control points may not fall inside the overlap; the overlap is adjusted only by this node.
    /// </summary>
    public bool IsTransition { get; set; }

    /// <summary>Beat position (1-based).</summary>
    public double Beat { get; set; }

    /// <summary>
    /// Pitch: "G4" / "440Hz" / project style "1D";
    /// null = silent (muted from this endpoint); "KEEP" = keep the concatenated original pitch.
    /// </summary>
    public string? Pitch { get; set; }

    public bool IsSilent => Pitch is null;

    public PitchPoint Clone() => new()
    {
        SegmentId = SegmentId,
        IsEdgeAnchor = IsEdgeAnchor,
        IsTransition = IsTransition,
        Beat = Beat,
        Pitch = Pitch,
    };
}
