using System.Text.Json.Serialization;

namespace VocalB1.Core.Models;

/// <summary>
/// The lyric of one track: an ordered list of words, each carrying its minimal pronunciation units.
/// This object graph is the single source of truth for the lyric bar; the text box only shows
/// <see cref="Text"/>, which is a projection of these objects (rests appear as spaces).
/// </summary>
public sealed class LyricPhrase
{
    public List<LyricWord> Words { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => Words.Count == 0;

    [JsonIgnore]
    public double StartBeat => Words.Count == 0 ? 0 : Words.Min(w => w.BeatStart);

    [JsonIgnore]
    public double EndBeat => Words.Count == 0 ? 0 : Words.Max(w => w.BeatEnd);

    /// <summary>Text projection of the whole phrase: rests are rendered as one space per unit.</summary>
    [JsonIgnore]
    public string Text => string.Concat(Words.Select(w => w.Projection));

    /// <summary>All minimal units in phrase order (what the unit strip below the lyric bar shows).</summary>
    [JsonIgnore]
    public List<LyricUnit> AllUnits => Words.SelectMany(w => w.Units).ToList();

    public LyricPhrase Clone() => new()
    {
        Words = Words.Select(w => w.Clone()).ToList(),
    };

    /// <summary>Rebuilds the phrase from existing segments (old projects without stored lyric objects).</summary>
    public static LyricPhrase FromSegments(IEnumerable<VoiceSegment> segments)
    {
        var phrase = new LyricPhrase();
        foreach (var segment in segments.OrderBy(s => s.BeatStart))
            phrase.Words.Add(WordFromSegment(segment));
        return phrase;
    }

    /// <summary>Builds a one-unit word bound to a segment.</summary>
    public static LyricWord WordFromSegment(VoiceSegment segment)
    {
        var text = segment.Lyric;
        var unit = new LyricUnit
        {
            Text = text,
            Audio = segment.Audio,
            Display = SampleLabel(segment.Audio),
            SrcPitch = segment.SrcPitch,
            KeepUnvoicedHead = segment.KeepUnvoicedHead,
            IsRest = false,
            SegmentId = segment.Id,
            BeatStart = segment.BeatStart,
            Length = Math.Max(0.01, segment.BeatEnd - segment.BeatStart),
            Vowel = segment.Vowel,
            FormantHz = segment.FormantHz?.ToArray(),
            FormantBandwidthHz = segment.FormantBandwidthHz?.ToArray(),
            FormantGainDb = segment.FormantGainDb?.ToArray(),
            Phoneme = segment.Phoneme,
            PreferAbsoluteAudio = segment.PreferAbsoluteAudio,
        };
        return new LyricWord { Surface = text, Units = [unit] };
    }

    /// <summary>Voicebank sample label: file name without directory and leading underscore.</summary>
    public static string SampleLabel(string? audio)
    {
        if (string.IsNullOrWhiteSpace(audio))
            return "休止";

        var name = Path.GetFileNameWithoutExtension(audio).TrimStart('_');
        return string.IsNullOrEmpty(name) ? audio : name;
    }
}

/// <summary>
/// One word of the lyric text: may have several pronunciation units (e.g. "きゃ" = _き + _ゃ).
/// <see cref="Surface"/> keeps the original text even after the word was turned into a rest.
/// </summary>
public sealed class LyricWord
{
    public string Surface { get; set; } = " ";

    public List<LyricUnit> Units { get; set; } = [];

    /// <summary>True when every pronunciation unit of this word has become a rest.</summary>
    [JsonIgnore]
    public bool IsRest => Units.Count > 0 && Units.All(u => u.IsRest);

    [JsonIgnore]
    public double BeatStart => Units.Count == 0 ? 0 : Units.Min(u => u.BeatStart);

    [JsonIgnore]
    public double BeatEnd => Units.Count == 0 ? 0 : Units.Max(u => u.BeatEnd);

    /// <summary>Text shown in the lyric box: rests show one space per unit, otherwise the original word text.</summary>
    [JsonIgnore]
    public string Projection => IsRest ? new string(' ', Units.Count) : Surface;

    /// <summary>
    /// Identity used to reuse the same word (and its melody) across a text edit:
    /// lyric text + audio path + rest flag. Rests are identified by their unit count so a deleted
    /// word can still match the spaces produced for it.
    /// </summary>
    [JsonIgnore]
    public string IdentityKey => IsRest
        ? "R" + Units.Count
        : Surface + "#" + string.Join(",",
            Units.Select(u => $"{u.Text}|{u.Audio}|{(u.IsRest ? 1 : 0)}"));

    public LyricWord Clone() => new()
    {
        Surface = Surface,
        Units = Units.Select(u => u.Clone()).ToList(),
    };
}

/// <summary>
/// One minimal voicebank unit. A rest keeps its beat span but has no segment.
/// <see cref="Text"/> keeps the original lyric character even after it became a rest (shown in Detail).
/// </summary>
public sealed class LyricUnit
{
    /// <summary>Stable identity, used for undo coalescing and row identity.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Lyric character this unit belongs to (original text is retained for rests).</summary>
    public string Text { get; set; } = " ";

    /// <summary>Resolved wav path; empty for a rest.</summary>
    public string Audio { get; set; } = string.Empty;

    /// <summary>Voicebank sample label shown on the unit strip.</summary>
    public string Display { get; set; } = "休止";

    public string SrcPitch { get; set; } = VoiceSegment.DefaultSrcPitch;

    public bool KeepUnvoicedHead { get; set; }

    public bool IsRest { get; set; }

    /// <summary>Linked segment; null for a rest.</summary>
    public string? SegmentId { get; set; }

    /// <summary>Start beat of this unit (kept in sync with the linked segment when there is one).</summary>
    public double BeatStart { get; set; } = 1.0;

    public double Length { get; set; } = 1.0;

    [JsonIgnore]
    public double BeatEnd => BeatStart + Length;

    public LyricUnit Clone() => new()
    {
        Id = Id,
        Text = Text,
        Audio = Audio,
        Display = Display,
        SrcPitch = SrcPitch,
        KeepUnvoicedHead = KeepUnvoicedHead,
        IsRest = IsRest,
        SegmentId = SegmentId,
        BeatStart = BeatStart,
        Length = Length,
        Vowel = Vowel,
        FormantHz = FormantHz?.ToArray(),
        FormantBandwidthHz = FormantBandwidthHz?.ToArray(),
        FormantGainDb = FormantGainDb?.ToArray(),
        Phoneme = Phoneme,
        PreferAbsoluteAudio = PreferAbsoluteAudio,
    };

    /// <summary>Vowel class driving the vowel manager (a/i/u/e/o/N/-).</summary>
    public string Vowel { get; set; } = "a";

    /// <summary>Manual formant targets F1~F7 in Hz; null = automatic (vowel manager).</summary>
    public double[]? FormantHz { get; set; }

    /// <summary>Optional per-formant bandwidths in Hz.</summary>
    public double[]? FormantBandwidthHz { get; set; }

    /// <summary>Optional per-formant gain multipliers.</summary>
    public double[]? FormantGainDb { get; set; }

    /// <summary>Phoneme/sample name resolved against the singer's voicebank (dual mode).</summary>
    public string? Phoneme { get; set; }

    /// <summary>Per-unit override: use the absolute audio path even when voicebank resolution is on.</summary>
    public bool PreferAbsoluteAudio { get; set; }
}
