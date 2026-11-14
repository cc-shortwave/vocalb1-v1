namespace VocalB1.Core.Models;

/// <summary>Per-frame spatial output: pan position, distance gain and high-frequency rolloff cutoff.</summary>
public sealed class SpatialFrameData
{
    /// <summary>X axis in [-1, 1]; -1 = hard left, 0 = centre, +1 = hard right.</summary>
    public required double[] Pan { get; init; }

    /// <summary>Linear distance gain, 1.0 at the near/centre position.</summary>
    public required double[] Gain { get; init; }

    /// <summary>Cutoff of the 12 dB/oct high-frequency rolloff applied by the synthesizer.</summary>
    public required double[] CutoffHz { get; init; }
}

/// <summary>
/// Turns a singer's X/Y/Z trajectory into the per-frame data the Python stage needs. Pan is kept as a
/// position so the synthesizer can use equal-power gains; distance only attenuates (the near side is not
/// boosted), and height gives a small brightness offset so a high voice stays intelligible when far away.
/// </summary>
public static class SpatialSolver
{
    public const double MaxCutoffHz = 18000.0;
    public const double MinCutoffHz = 2700.0;

    /// <summary>Height axis: one octave of cutoff per unit, and ±1 dB of level.</summary>
    private const double HeightOctaves = 1.0;
    private const double HeightGainDb = 1.0;

    /// <summary>
    /// Samples the trajectory on the synthesis frame grid (beat = 1 + frame / framesPerBeat) and returns
    /// null when spatial processing is disabled or the trajectory has no keyframe at all.
    /// </summary>
    public static SpatialFrameData? Solve(SpatialTrajectory? trajectory, int frameCount,
        double framesPerBeat)
    {
        if (trajectory is null || !trajectory.Enabled || frameCount <= 0 || !double.IsFinite(framesPerBeat)
            || framesPerBeat <= 0)
            return null;

        trajectory.Normalize();
        if (!trajectory.HasPoints)
            return null;

        var pan = new double[frameCount];
        var gain = new double[frameCount];
        var cutoff = new double[frameCount];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var beat = 1.0 + frame / framesPerBeat;
            var x = SpatialTrajectory.Sample(trajectory.X, beat);
            var y = SpatialTrajectory.Sample(trajectory.Y, beat);
            var z = SpatialTrajectory.Sample(trajectory.Z, beat);

            // Only the far half attenuates: y = 0 (centre) keeps the singer at unity gain, y = 1 is 1/3.
            var far = Math.Clamp(y, 0, 1);
            var heightGain = Math.Pow(10.0, HeightGainDb * Math.Clamp(z, -1, 1) / 20.0);

            pan[frame] = Math.Clamp(x, -1, 1);
            gain[frame] = Math.Clamp(heightGain / (1.0 + 2.0 * far), 0.05, 2.0);
            cutoff[frame] = Math.Clamp(
                MaxCutoffHz * Math.Pow(MinCutoffHz / MaxCutoffHz, far)
                * Math.Pow(2.0, HeightOctaves * Math.Clamp(z, -1, 1)),
                500.0, MaxCutoffHz);
        }

        return new SpatialFrameData { Pan = pan, Gain = gain, CutoffHz = cutoff };
    }
}
