using System.Globalization;
using VocalB1.Core.Pitch;

namespace VocalB1.Core.Models;

/// <summary>
/// Rasterised pitch curve: one value per fixed-size beat cell instead of draggable Bézier control points.
/// A cell stores a semitone value, <see cref="double.NaN"/> for a silent cell, or null when the cell is not
/// covered (the engine keeps the spliced original f0 there). Painting writes cells directly; the render
/// path converts runs to dense pitch points so the Python engine receives the curve without resampling.
/// </summary>
public sealed class PitchRaster
{
    /// <summary>Default horizontal resolution: 1/128 beat (≈4 ms at 120 BPM, finer than a 5 ms frame).</summary>
    public const double DefaultCellBeats = 1.0 / 128.0;

    /// <summary>Smoothing window used by the brush anti-shake, in cells (4 cells ≈ 31 ms at 120 BPM).</summary>
    public const int SmoothRadius = 4;

    public double CellBeats { get; set; } = DefaultCellBeats;

    /// <summary>Beat position of cell 0 (the project timeline starts at beat 1).</summary>
    public double StartBeat { get; set; } = 1.0;

    public List<double?> Values { get; set; } = [];

    public bool HasValues => Values.Any(v => v is not null);

    public int IndexOfBeat(double beat)
    {
        if (!double.IsFinite(beat))
            return -1;
        return (int)Math.Floor((beat - StartBeat) / Math.Max(1e-9, CellBeats) + 1e-9);
    }

    public double BeatOfIndex(int index) =>
        StartBeat + Math.Max(0, index) * Math.Max(1e-9, CellBeats);

    public double? ValueAt(double beat)
    {
        var index = IndexOfBeat(beat);
        return index >= 0 && index < Values.Count ? Values[index] : null;
    }

    public void EnsureSize(int count)
    {
        if (count <= Values.Count)
            return;
        if (count > Values.Capacity)
            Values.Capacity = count;
        while (Values.Count < count)
            Values.Add(null);
    }

    public void SetValue(int index, double? value)
    {
        if (index < 0)
            return;
        EnsureSize(index + 1);
        Values[index] = Sanitize(value);
    }

    /// <summary>Paints every cell touched by the beat range; used by the brush and by programmatic edits.</summary>
    public void Paint(double fromBeat, double toBeat, double semitone)
    {
        var a = Math.Min(fromBeat, toBeat);
        var b = Math.Max(fromBeat, toBeat);
        var start = Math.Max(0, IndexOfBeat(a));
        var end = IndexOfBeat(b);
        if (end < start)
            return;

        EnsureSize(end + 1);
        for (var i = start; i <= end; i++)
            Values[i] = semitone;
    }

    /// <summary>Box-averages the given index range in place (anti-shake while painting).</summary>
    public void Smooth(int fromIndex, int toIndex, int radius = SmoothRadius)
    {
        if (Values.Count == 0)
            return;

        var start = Math.Max(0, Math.Min(fromIndex, toIndex) - radius);
        var end = Math.Min(Values.Count - 1, Math.Max(fromIndex, toIndex) + radius);
        if (end <= start)
            return;

        radius = Math.Max(1, radius);
        var snapshot = new double?[end - start + 1];
        Values.CopyTo(start, snapshot, 0, snapshot.Length);

        for (var i = start; i <= end; i++)
        {
            if (snapshot[i - start] is null)
                continue;

            double sum = 0;
            var count = 0;
            for (var j = i - radius; j <= i + radius; j++)
            {
                var index = j - start;
                if (index < 0 || index >= snapshot.Length)
                    continue;
                var value = snapshot[index];
                if (value is not { } v || double.IsNaN(v))
                    continue;
                sum += v;
                count++;
            }

            if (count > 0)
                Values[i] = Math.Round(sum / count, 3);
        }
    }

    /// <summary>Clears the cells covered by a beat range (note deletion).</summary>
    public void ClearRange(double fromBeat, double toBeat)
    {
        var start = Math.Max(0, IndexOfBeat(Math.Min(fromBeat, toBeat)));
        var end = Math.Min(Values.Count - 1, IndexOfBeat(Math.Max(fromBeat, toBeat)));
        for (var i = start; i <= end; i++)
            Values[i] = null;
    }

    /// <summary>Moves the cells inside a beat range by a beat offset, optionally transposing their pitch.</summary>
    public void ShiftRange(double fromBeat, double toBeat, double beatDelta, double semitoneDelta = 0)
    {
        var start = Math.Max(0, IndexOfBeat(Math.Min(fromBeat, toBeat)));
        var end = Math.Min(Values.Count - 1, IndexOfBeat(Math.Max(fromBeat, toBeat)));
        if (end < start)
            return;

        var shiftCells = (int)Math.Round(beatDelta / Math.Max(1e-9, CellBeats));
        if (shiftCells == 0 && Math.Abs(semitoneDelta) < 1e-9)
            return;

        var moved = new List<(int Index, double? Value)>();
        for (var i = start; i <= end; i++)
        {
            var value = Values[i];
            if (value is { } v && !double.IsNaN(v) && Math.Abs(semitoneDelta) > 1e-9)
                value = v + semitoneDelta;
            moved.Add((i + shiftCells, value));
        }

        for (var i = start; i <= end; i++)
            Values[i] = null;
        foreach (var (index, value) in moved)
            SetValue(index, value);
    }

    /// <summary>Time-stretches the cells of a beat range onto a new range (note resize).</summary>
    public void ScaleRange(double oldStart, double oldEnd, double newStart, double newEnd)
    {
        var oldLength = oldEnd - oldStart;
        var newLength = newEnd - newStart;
        if (oldLength <= 1e-9 || newLength <= 1e-9)
            return;

        var start = Math.Max(0, IndexOfBeat(oldStart));
        var end = Math.Min(Values.Count - 1, IndexOfBeat(oldEnd));
        if (end < start)
            return;

        var snapshot = Values.GetRange(start, end - start + 1).ToArray();
        for (var i = start; i <= end; i++)
            Values[i] = null;

        for (var i = 0; i < snapshot.Length; i++)
        {
            var value = snapshot[i];
            if (value is null)
                continue;
            var beat = oldStart + i * CellBeats;
            var ratio = Math.Clamp((beat - oldStart) / oldLength, 0, 1);
            var target = IndexOfBeat(newStart + ratio * newLength);
            SetValue(target, value);
        }
    }

    /// <summary>Converts the raster to the dense pitch-point list consumed by the synthesizer.</summary>
    public List<PitchPoint> ToPitchPoints()
    {
        var result = new List<PitchPoint>();
        var index = 0;
        while (index < Values.Count && Values[index] is null)
            index++;

        string? previous = null;
        var first = true;
        for (; index < Values.Count; index++)
        {
            var encoded = Encode(Values[index]);
            if (!first && string.Equals(encoded, previous, StringComparison.Ordinal))
                continue;

            result.Add(new PitchPoint
            {
                Beat = BeatOfIndex(index),
                Pitch = encoded,
            });
            previous = encoded;
            first = false;
        }

        return result;
    }

    public PitchRaster Clone() => new()
    {
        CellBeats = CellBeats,
        StartBeat = StartBeat,
        Values = [.. Values],
    };

    public void Normalize()
    {
        if (!double.IsFinite(CellBeats) || CellBeats <= 0)
            CellBeats = DefaultCellBeats;
        if (!double.IsFinite(StartBeat))
            StartBeat = 1.0;

        Values ??= [];
        for (var i = 0; i < Values.Count; i++)
            Values[i] = Sanitize(Values[i]);

        // Trailing uncovered cells would only add a KEEP endpoint; drop them.
        while (Values.Count > 0 && Values[^1] is null)
            Values.RemoveAt(Values.Count - 1);
    }

    private static double? Sanitize(double? value) =>
        value is { } v && !double.IsNaN(v) && !double.IsFinite(v) ? null : value;

    private static string? Encode(double? value) => value switch
    {
        null => "KEEP",
        { } v when double.IsNaN(v) => null,
        { } v => v.ToString("0.####", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Rasterises a legacy sparse pitch line using the same semantics as the Python engine
    /// (left-endpoint intervals, smoothstep glide, null = silent, "KEEP" = uncovered).
    /// </summary>
    public static PitchRaster? FromPitchLine(IReadOnlyList<PitchPoint>? compiled)
    {
        if (compiled is null || compiled.Count < 2)
            return null;

        var raster = new PitchRaster();
        for (var i = 0; i + 1 < compiled.Count; i++)
        {
            var a = compiled[i];
            var b = compiled[i + 1];
            if (b.Beat <= a.Beat)
                continue;

            var start = Math.Max(0, raster.IndexOfBeat(a.Beat));
            var end = raster.IndexOfBeat(b.Beat);
            if (end <= start)
                continue;

            raster.EnsureSize(end);

            var stateA = Classify(a.Pitch);
            var stateB = Classify(b.Pitch);

            if (stateA.Keep || stateB.Keep)
            {
                for (var c = start; c < end; c++)
                    raster.Values[c] = null;
                continue;
            }

            if (stateA.Silent)
            {
                for (var c = start; c < end; c++)
                    raster.Values[c] = double.NaN;
                continue;
            }

            var from = stateA.Value;
            var to = stateB.Silent ? from : stateB.Value;
            for (var c = start; c < end; c++)
            {
                var u = Math.Clamp((raster.BeatOfIndex(c) - a.Beat) / (b.Beat - a.Beat), 0, 1);
                var w = 3 * u * u - 2 * u * u * u;
                raster.Values[c] = Math.Round(from + (to - from) * w, 4);
            }
        }

        raster.Normalize();
        return raster.HasValues ? raster : null;

        static (bool Silent, bool Keep, double Value) Classify(string? pitch)
        {
            if (pitch is null)
                return (true, false, 0);
            if (string.Equals(pitch.Trim(), "KEEP", StringComparison.OrdinalIgnoreCase))
                return (false, true, 0);
            return PitchMath.TryParseSemitones(pitch, out var value)
                ? (false, false, value)
                : (false, true, 0);
        }
    }
}
