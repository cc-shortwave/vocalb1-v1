namespace VocalB1.Core.Models;

/// <summary>
/// A singer data-block: the render-facing wrapper around one voicebank, one lyric score and one
/// parameter set. It owns no stage position; the synthesizer is called once per singer and the
/// resulting WAVs are summed.
/// </summary>
public sealed class SingerDocument : IDatablock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "歌手";

    public bool FakeUser { get; set; }

    /// <summary>Voicebank root directory used to resolve phonemes and measure formants.</summary>
    public string LibraryRoot { get; set; } = string.Empty;

    /// <summary>Bound lyric score; a lyric belongs to exactly one singer.</summary>
    public string? LyricId { get; set; }

    /// <summary>Bound shared parameter set; many singers may reference the same one.</summary>
    public string? ParameterSetId { get; set; }

    /// <summary>When false the singer is ignored by validation and rendering.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Linear mix gain applied after synthesis (1.0 = unchanged).</summary>
    public double MixGain { get; set; } = 1.0;

    /// <summary>
    /// Optional X/Y/Z motion curves. When enabled the singer is rendered as stereo with a per-frame pan and
    /// distance rolloff computed by <see cref="SpatialSolver"/>; disabled keeps the mono output untouched.
    /// </summary>
    public SpatialTrajectory Spatial { get; set; } = new();

    /// <summary>
    /// Singer-level overlay graph. It is evaluated after the shared parameter set's graph; an output node
    /// whose input is unwired receives the shared value for that key, so a singer can adjust a shared
    /// parameter without forking the data-block.
    /// </summary>
    public ParameterNodeGraph Graph { get; set; } = new();

    public SingerDocument Clone() => new()
    {
        Id = Id,
        Name = Name,
        FakeUser = FakeUser,
        LibraryRoot = LibraryRoot,
        LyricId = LyricId,
        ParameterSetId = ParameterSetId,
        Enabled = Enabled,
        MixGain = MixGain,
        Spatial = Spatial.Clone(),
        Graph = Graph.Clone(),
    };
}
