namespace VocalB1.Core.Models;

/// <summary>One keyframe of a spatial axis: a beat position and a normalised value in [-1, 1].</summary>
public sealed class SpatialPoint
{
    public double Beat { get; set; } = 1.0;

    /// <summary>Axis value; -1 ~ +1 with 0 meaning "no offset from the centre".</summary>
    public double Value { get; set; }

    public SpatialPoint Clone() => new() { Beat = Beat, Value = Value };
}

/// <summary>
/// Per-singer spatial trajectory. Three independent curves drive the spatial solver:
/// X = left/right pan, Y = distance (far side attenuates and rolls off highs), Z = height
/// (small brightness offset). A curve with no points means "stay at the centre".
/// </summary>
public sealed class SpatialTrajectory
{
    public const int MaxPointsPerAxis = 512;

    public bool Enabled { get; set; }

    public List<SpatialPoint> X { get; set; } = [];

    public List<SpatialPoint> Y { get; set; } = [];

    public List<SpatialPoint> Z { get; set; } = [];

    public bool HasPoints => X is { Count: > 0 } || Y is { Count: > 0 } || Z is { Count: > 0 };

    /// <summary>Linear interpolation on one axis; outside the keyframe range the nearest value holds.</summary>
    public static double Sample(IReadOnlyList<SpatialPoint>? points, double beat)
    {
        if (points is null || points.Count == 0)
            return 0.0;

        var first = points[0];
        if (beat <= first.Beat)
            return first.Value;

        var previous = first;
        for (var i = 1; i < points.Count; i++)
        {
            var point = points[i];
            if (beat > point.Beat)
            {
                previous = point;
                continue;
            }

            var span = point.Beat - previous.Beat;
            if (span <= 1e-9)
                return point.Value;
            var t = Math.Clamp((beat - previous.Beat) / span, 0, 1);
            return previous.Value + (point.Value - previous.Value) * t;
        }

        return points[^1].Value;
    }

    /// <summary>Builds a planar point on the given axis, clamped to the valid value range.</summary>
    public static SpatialPoint CreatePoint(double beat, double value) => new()
    {
        Beat = Math.Max(0, beat),
        Value = Math.Clamp(value, -1, 1),
    };

    /// <summary>Drops non-finite keys, clamps values, sorts by beat and removes duplicates.</summary>
    public void Normalize()
    {
        X = NormalizeAxis(X);
        Y = NormalizeAxis(Y);
        Z = NormalizeAxis(Z);
    }

    private static List<SpatialPoint> NormalizeAxis(List<SpatialPoint>? points)
    {
        var result = new List<SpatialPoint>();
        if (points is null)
            return result;

        foreach (var point in points)
        {
            if (point is null || !double.IsFinite(point.Beat) || !double.IsFinite(point.Value))
                continue;
            result.Add(new SpatialPoint
            {
                Beat = Math.Max(0, point.Beat),
                Value = Math.Clamp(point.Value, -1, 1),
            });
        }

        result.Sort((a, b) => a.Beat.CompareTo(b.Beat));

        // Keep the last point of a duplicate beat: the curve is a function, not a bag of keys.
        for (var i = result.Count - 1; i > 0; i--)
        {
            if (Math.Abs(result[i].Beat - result[i - 1].Beat) < 1e-4)
                result.RemoveAt(i - 1);
        }

        if (result.Count > MaxPointsPerAxis)
            result.RemoveRange(MaxPointsPerAxis, result.Count - MaxPointsPerAxis);

        return result;
    }

    public SpatialTrajectory Clone() => new()
    {
        Enabled = Enabled,
        X = X?.Select(p => p.Clone()).ToList() ?? [],
        Y = Y?.Select(p => p.Clone()).ToList() ?? [],
        Z = Z?.Select(p => p.Clone()).ToList() ?? [],
    };
}
