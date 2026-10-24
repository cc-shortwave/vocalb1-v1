using System.Globalization;
using System.Text.RegularExpressions;

namespace VocalB1.Core.Pitch;

/// <summary>
/// Parsing and formatting of pitch representations — shares the same semantics as
/// vocal_lib.pitch._parse_semitones on the Python side (C4 = 0 semitones), so the UI display and the synthesis engine agree.
/// </summary>
public static partial class PitchMath
{
    private static readonly string[] NoteNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private static readonly Dictionary<string, int> NoteSemitones = new(StringComparer.Ordinal)
    {
        ["C"] = 0, ["D"] = 2, ["E"] = 4, ["F"] = 5, ["G"] = 7, ["A"] = 9, ["B"] = 11,
        ["C#"] = 1, ["Db"] = 1, ["D#"] = 3, ["Eb"] = 3,
        ["F#"] = 6, ["Gb"] = 6, ["G#"] = 8, ["Ab"] = 8, ["A#"] = 10, ["Bb"] = 10,
    };

    private const double A4Hz = 440.0;
    private const int A4Midi = 69;

    /// <summary>Parses a pitch into semitones (relative to C4). Supports "G4", "440Hz", and project style "1D".</summary>
    public static bool TryParseSemitones(string? pitch, out double semitones)
    {
        semitones = 0;
        if (string.IsNullOrWhiteSpace(pitch))
            return false;

        var s = pitch.Trim();

        if (HzPattern().Match(s) is { Success: true } hz)
        {
            var value = double.Parse(hz.Groups[1].Value, CultureInfo.InvariantCulture);
            if (value <= 0)
                return false;
            semitones = HzToSemitones(value);
            return true;
        }

        var upper = s.ToUpperInvariant();

        if (ScientificPitchPattern().Match(upper) is { Success: true } sci)
        {
            var name = sci.Groups[1].Value + NormalizeAccidental(sci.Groups[2].Value);
            var octave = int.Parse(sci.Groups[3].Value, CultureInfo.InvariantCulture);
            if (!NoteSemitones.TryGetValue(name, out var offset))
                return false;
            semitones = (octave + 1) * 12 + offset - 60;
            return true;
        }

        if (ProjectStylePattern().Match(upper) is { Success: true } proj)
        {
            var octaveOffset = int.Parse(proj.Groups[1].Value, CultureInfo.InvariantCulture);
            var name = proj.Groups[2].Value + NormalizeAccidental(proj.Groups[3].Value);
            if (!NoteSemitones.TryGetValue(name, out var offset))
                return false;
            semitones = octaveOffset * 12 + offset;
            return true;
        }

        // Plain numeric string: treated as semitones ("0" / "-5" / "3.5"), matching Python's numeric argument semantics.
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            semitones = numeric;
            return true;
        }

        return false;
    }

    /// <summary>Semitones (relative to C4) → scientific pitch name; non-integers are rounded.</summary>
    public static string ToPitchName(double semitones)
    {
        var rounded = (int)Math.Round(semitones, MidpointRounding.AwayFromZero);
        var note = NoteNames[((rounded % 12) + 12) % 12];
        var octave = (int)Math.Floor(rounded / 12.0) + 4;
        return $"{note}{octave.ToString(CultureInfo.InvariantCulture)}";
    }

    public static double HzToSemitones(double hz)
    {
        var c4Hz = A4Hz * Math.Pow(2.0, (60 - A4Midi) / 12.0);
        return 12.0 * Math.Log2(hz / c4Hz);
    }

    public static double SemitonesToHz(double semitones)
    {
        var c4Hz = A4Hz * Math.Pow(2.0, (60 - A4Midi) / 12.0);
        return c4Hz * Math.Pow(2.0, semitones / 12.0);
    }

    /// <summary>Normalizes any valid pitch to a scientific pitch name; returns it unchanged on failure.</summary>
    public static string Normalize(string? pitch) =>
        TryParseSemitones(pitch, out var st) ? ToPitchName(st) : pitch ?? string.Empty;

    private static string NormalizeAccidental(string accidental) =>
        accidental.Equals("B", StringComparison.Ordinal) ? "b" : accidental;

    [GeneratedRegex(@"^([+-]?\d+(?:\.\d+)?)\s*[hH][zZ]$")]
    private static partial Regex HzPattern();

    [GeneratedRegex(@"^([A-G])([#bB]?)(-?\d+)$")]
    private static partial Regex ScientificPitchPattern();

    [GeneratedRegex(@"^([+-]?\d+)([A-G])([#bB]?)$")]
    private static partial Regex ProjectStylePattern();
}
