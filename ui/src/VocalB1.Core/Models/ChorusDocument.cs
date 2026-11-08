namespace VocalB1.Core.Models;

/// <summary>
/// Chorus mode state: a 2D top-down stage with one microphone in front of it and a set of singers.
/// Coordinates are in meters: X runs left (-) to right (+), Y runs from the stage front (0) to the back (positive).
/// The stage itself is only a layout description; audio is produced from the same coordinates.
/// </summary>
public sealed class ChorusDocument
{
    /// <summary>Stage width in meters (X from -StageWidth/2 to +StageWidth/2).</summary>
    public double StageWidth { get; set; } = 10.0;

    /// <summary>Stage depth in meters (Y from 0 at the front edge to StageDepth at the back).</summary>
    public double StageDepth { get; set; } = 6.0;

    /// <summary>Microphone position, default in front of the stage center.</summary>
    public double MicX { get; set; }

    public double MicY { get; set; } = -2.0;

    public List<ChorusSinger> Singers { get; set; } = [];

    /// <summary>
    /// Custom formations imported from JSON, kept with the project so the same staging is available
    /// after reopening it.
    /// </summary>
    public List<ChorusFormationPreset> Formations { get; set; } = [];

    public ChorusDocument Clone() => new()
    {
        StageWidth = StageWidth,
        StageDepth = StageDepth,
        MicX = MicX,
        MicY = MicY,
        Singers = Singers.Select(s => s.Clone()).ToList(),
        Formations = Formations.Select(f => f.Clone()).ToList(),
    };
}

/// <summary>
/// One singer on the stage. A singer binds exactly one track (the score it sings); several singers may
/// share one track and are then rendered once per singer at their own positions before being summed.
/// </summary>
public sealed class ChorusSinger
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "歌手";

    /// <summary>Id of the bound track; null = not bound (for example after the track was deleted).</summary>
    public string? TrackId { get; set; }

    /// <summary>When false the singer is ignored both on stage and during rendering.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Spatial keyframes ordered by beat; between them the path is interpolated smoothly.</summary>
    public List<ChorusKeyframe> Path { get; set; } = [];

    public ChorusSinger Clone() => new()
    {
        Id = Id,
        Name = Name,
        TrackId = TrackId,
        Enabled = Enabled,
        Path = Path.Select(p => p.Clone()).ToList(),
    };
}

/// <summary>A point of a singer's path: position on the stage at a given 1-based beat.</summary>
public sealed class ChorusKeyframe
{
    public double Beat { get; set; } = 1.0;

    public double X { get; set; }

    public double Y { get; set; }

    public ChorusKeyframe Clone() => new()
    {
        Beat = Beat,
        X = X,
        Y = Y,
    };
}

/// <summary>
/// An imported formation: a named list of stage positions in meters (X from the stage center, Y from
/// the stage front). Singers are placed in list order; extra singers cycle through the list.
/// </summary>
public sealed class ChorusFormationPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "导入队形";

    public List<ChorusFormationPoint> Positions { get; set; } = [];

    public ChorusFormationPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        Positions = Positions.Select(p => p.Clone()).ToList(),
    };

    public override string ToString() => Name;
}

/// <summary>One stage position of an imported formation, in meters.</summary>
public sealed class ChorusFormationPoint
{
    public double X { get; set; }

    public double Y { get; set; }

    public ChorusFormationPoint Clone() => new()
    {
        X = X,
        Y = Y,
    };
}
