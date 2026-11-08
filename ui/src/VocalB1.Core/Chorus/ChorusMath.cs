using VocalB1.Core.Models;

namespace VocalB1.Core.Chorus;

/// <summary>
/// Geometry shared by the stage editor and the audio renderer, so what is drawn is exactly what is heard.
/// All lengths are meters and beats are 1-based, matching the piano roll.
/// </summary>
public static class ChorusMath
{
    /// <summary>Path keyframes snap to a quarter beat.</summary>
    public const double BeatSnap = 0.25;

    /// <summary>Distance at which a singer is at unity gain.</summary>
    public const double ReferenceDistance = 1.0;

    /// <summary>Closest possible distance; prevents the 1/r law from exploding on top of the microphone.</summary>
    public const double MinDistance = 0.4;

    /// <summary>Hard pan limit so a singer at the stage edge never disappears from one channel.</summary>
    public const double PanWidth = 0.85;

    public static double SnapBeat(double beat) =>
        Math.Round(Math.Max(1.0, beat) / BeatSnap, MidpointRounding.AwayFromZero) * BeatSnap;

    public static double ClampX(double x, double stageWidth) =>
        Math.Clamp(x, -stageWidth / 2, stageWidth / 2);

    public static double ClampY(double y, double stageDepth) =>
        Math.Clamp(y, 0, stageDepth);

    public static double Distance(double x, double y, double micX, double micY)
    {
        var dx = x - micX;
        var dy = y - micY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Distance attenuation relative to the reference distance (1/r law).</summary>
    public static double DistanceGain(double distance) =>
        ReferenceDistance / Math.Max(MinDistance, distance);

    /// <summary>
    /// High-frequency air absorption in dB per meter. Grows with frequency, so distant singers lose
    /// treble first; the engine turns this into a spectral gain.
    /// </summary>
    public static double AirAbsorptionDbPerMeter(double frequencyHz)
    {
        var kilo = Math.Max(0.0, frequencyHz) / 1000.0;
        return 0.0014 * kilo * kilo + 0.002 * kilo;
    }

    /// <summary>Constant-power pan gains from the singer's X position on the stage.</summary>
    public static (double Left, double Right) PanGains(double x, double stageWidth)
    {
        var half = Math.Max(0.001, stageWidth / 2);
        var pan = Math.Clamp(x / half, -1, 1) * PanWidth;
        var angle = (pan + 1) * Math.PI / 4;
        return (Math.Cos(angle), Math.Sin(angle));
    }

    /// <summary>
    /// Position of a singer's path at the given beat: clamped outside the keyframe range and smoothly
    /// interpolated with a time-aware Catmull-Rom (Hermite) spline in between.
    /// </summary>
    public static (double X, double Y) Evaluate(IReadOnlyList<ChorusKeyframe> path, double beat)
    {
        if (path.Count == 0)
            return (0, 0);
        if (path.Count == 1)
            return (path[0].X, path[0].Y);

        // Keyframes are normally kept sorted by the editor and the project loader, but a caller-built
        // list may be out of order; sort a copy instead of interpolating against wrong neighbours.
        var points = IsSorted(path) ? path : SortByBeat(path);
        if (beat <= points[0].Beat)
            return (points[0].X, points[0].Y);
        if (beat >= points[^1].Beat)
            return (points[^1].X, points[^1].Y);

        var index = 0;
        while (index < points.Count - 2 && beat >= points[index + 1].Beat)
            index++;

        var p1 = points[index];
        var p2 = points[index + 1];
        var span = Math.Max(1e-6, p2.Beat - p1.Beat);
        var t = Math.Clamp((beat - p1.Beat) / span, 0, 1);

        var p0 = index > 0 ? points[index - 1] : p1;
        var p3 = index + 2 < points.Count ? points[index + 2] : p2;

        var tangent1 = Tangent(p0, p2, p1, p2);
        var tangent2 = Tangent(p1, p3, p1, p2);

        return (Hermite(p1.X, p2.X, tangent1.X, tangent2.X, t),
                Hermite(p1.Y, p2.Y, tangent1.Y, tangent2.Y, t));
    }

    private static bool IsSorted(IReadOnlyList<ChorusKeyframe> path)
    {
        for (var i = 1; i < path.Count; i++)
        {
            if (path[i].Beat < path[i - 1].Beat)
                return false;
        }

        return true;
    }

    private static List<ChorusKeyframe> SortByBeat(IReadOnlyList<ChorusKeyframe> path)
    {
        var copy = path.ToList();
        copy.Sort((a, b) => a.Beat.CompareTo(b.Beat));
        return copy;
    }

    /// <summary>Scale of the Hermite tangents so uneven keyframe spacing still follows time smoothly.</summary>
    private static (double X, double Y) Tangent(
        ChorusKeyframe previous, ChorusKeyframe next, ChorusKeyframe start, ChorusKeyframe end)
    {
        var outer = Math.Max(1e-6, next.Beat - previous.Beat);
        var inner = Math.Max(1e-6, end.Beat - start.Beat);
        var scale = inner / outer;
        return ((next.X - previous.X) * scale, (next.Y - previous.Y) * scale);
    }

    private static double Hermite(double start, double end, double tangentStart, double tangentEnd, double t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return (2 * t3 - 3 * t2 + 1) * start
            + (t3 - 2 * t2 + t) * tangentStart
            + (-2 * t3 + 3 * t2) * end
            + (t3 - t2) * tangentEnd;
    }

    /// <summary>
    /// Default standing slot for a newly added singer: evenly spread along the front of the stage,
    /// ordered left to right, so added singers never start stacked on top of each other.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> DefaultPositions(
        int count, double stageWidth, double stageDepth)
    {
        var result = new List<(double X, double Y)>(Math.Max(0, count));
        var spread = stageWidth / 2 * 0.76;
        var y = stageDepth * 0.22;
        for (var i = 0; i < count; i++)
        {
            var t = count == 1 ? 0.5 : i / (double)(count - 1);
            result.Add((-spread + 2 * spread * t, y));
        }

        return result;
    }
}
