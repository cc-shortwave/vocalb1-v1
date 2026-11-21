using System.Globalization;
using System.Text;
using VocalB1.Core.Models;
using VocalB1.Core.Pitch;

namespace VocalB1.Core.Serialization;

/// <summary>
/// Reads a UTAU sequence file (.ust) as a lyric data-block. A UST is an INI-like list of note
/// sections ([#0000] … [#TRACKEND]); each section carries Length (480 ticks per quarter note),
/// Lyric and NoteNum (MIDI note number).
/// <para>
/// One typed note becomes one segment + one lyric unit, with the lyric doubling as the phoneme so the
/// singer's voicebank resolves the sample at render time; a "R" note becomes a rest of the same span and
/// NoteNum becomes the target pitch. The UST tempo is reported but not applied, because the project owns BPM.
/// </para>
/// </summary>
public static class UstLyricImporter
{
    /// <summary>UTAU ticks per quarter note; the lyric score uses one beat per quarter note.</summary>
    public const double TicksPerBeat = 480.0;

    private const string RestLyric = "R";

    /// <summary>UTAU continuation: the note extends the previous one instead of starting a new segment.</summary>
    private const string ContinuationLyric = "-";

    private static readonly Encoding? ShiftJis = TryGetCodePage(932);

    private static readonly Encoding? Gbk = TryGetCodePage(936);

    /// <summary>Import outcome: the lyric block plus the source tempo and note counts for logging.</summary>
    public sealed record Result(LyricDocument Lyric, double? Tempo, int NoteCount, int RestCount);

    public static Result ImportFile(string path, double transitionFrames = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(ReadText(path), Path.GetFileNameWithoutExtension(path), transitionFrames);
    }

    public static Result Parse(string text, string name, double transitionFrames = 0)
    {
        ArgumentNullException.ThrowIfNull(text);

        var notes = ParseNotes(text, out var tempo);
        if (notes.Count == 0)
            throw new InvalidDataException("UST 文件中没有可导入的音符。");

        var lyric = new LyricDocument
        {
            Name = string.IsNullOrWhiteSpace(name) ? "UST 歌词" : name.Trim(),
        };

        var cursor = 1.0;
        var rests = 0;
        foreach (var note in notes)
        {
            var length = note.LengthTicks / TicksPerBeat;
            if (note.IsRest)
            {
                lyric.Phrase.Words.Add(new LyricWord
                {
                    Surface = " ",
                    Units =
                    [
                        new LyricUnit
                        {
                            Text = note.Lyric,
                            Display = "休止",
                            IsRest = true,
                            BeatStart = cursor,
                            Length = length,
                        },
                    ],
                });
                rests++;
                cursor += length;
                continue;
            }

            var segment = new VoiceSegment
            {
                Audio = string.Empty,
                SrcPitch = VoiceSegment.DefaultSrcPitch,
                DstPitch = PitchMath.ToPitchName(note.NoteNumber - 60),
                BeatStart = cursor,
                BeatEnd = cursor + length,
                Transition = lyric.Segments.Count > 0 ? transitionFrames : 0,
                LyricOverride = note.Lyric,
                Phoneme = note.Lyric,
            };
            lyric.Segments.Add(segment);
            lyric.AddPitchAnchor(segment);

            lyric.Phrase.Words.Add(new LyricWord
            {
                Surface = note.Lyric,
                Units =
                [
                    new LyricUnit
                    {
                        Text = note.Lyric,
                        Audio = string.Empty,
                        Display = note.Lyric,
                        SrcPitch = VoiceSegment.DefaultSrcPitch,
                        IsRest = false,
                        SegmentId = segment.Id,
                        BeatStart = cursor,
                        Length = length,
                        Phoneme = note.Lyric,
                    },
                ],
            });

            cursor += length;
        }

        return new Result(lyric, tempo, lyric.Segments.Count, rests);
    }

    private sealed record ParsedNote(string Lyric, int NoteNumber, double LengthTicks)
    {
        /// <summary>A note without lyric text, or with UTAU's "R", occupies time but has no segment.</summary>
        public bool IsRest => string.IsNullOrWhiteSpace(Lyric)
            || string.Equals(Lyric, RestLyric, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the note sections in file order; the first [#TRACKEND] ends the sequence.</summary>
    private static List<ParsedNote> ParseNotes(string text, out double? tempo)
    {
        var notes = new List<ParsedNote>();
        tempo = null;
        string? section = null;
        double? length = null;
        string? lyric = null;
        int? noteNumber = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                Flush();
                section = line[1..^1].Trim();
                if (section.StartsWith('#'))
                    section = section[1..];
                if (section.Equals("TRACKEND", StringComparison.OrdinalIgnoreCase))
                    break;
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0 || section is null)
                continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (section.Equals("SETTING", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("Tempo", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm)
                    && bpm > 0)
                {
                    tempo = bpm;
                }
                continue;
            }

            // [#PREV] / [#NEXT] / [#VERSION] reuse the note keys but describe neighbouring notes,
            // not the sequence itself. Only sections whose name is purely numeric are real notes.
            if (!IsNoteSection(section))
                continue;

            switch (key.ToLowerInvariant())
            {
                case "length":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ticks))
                        length = ticks;
                    break;
                case "lyric":
                    lyric = value;
                    break;
                case "notenum":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        noteNumber = (int)Math.Round(number);
                    break;
            }
        }

        Flush();
        return notes;

        void Flush()
        {
            if (length is { } ticks && section is not null && IsNoteSection(section))
            {
                if (ticks <= 0)
                    throw new InvalidDataException(
                        $"UST 第 {section} 段的 Length 必须大于 0（实际 {ticks}）。");

                var noteLyric = (lyric ?? string.Empty).Trim();
                if (string.Equals(noteLyric, ContinuationLyric, StringComparison.Ordinal))
                {
                    // A "-" note has no pitch of its own: it lengthens the previous note (or rest).
                    if (notes.Count == 0)
                        throw new InvalidDataException(
                            $"UST 第 {section} 段是连音符 \"-\"，但前面没有可延长的音符。");

                    notes[^1] = notes[^1] with { LengthTicks = notes[^1].LengthTicks + ticks };
                }
                else
                {
                    notes.Add(new ParsedNote(
                        noteLyric,
                        Math.Clamp(noteNumber ?? 60, 0, 127),
                        ticks));
                }
            }

            length = null;
            lyric = null;
            noteNumber = null;
        }
    }

    /// <summary>Note sections are the numbered blocks ([#0000], [#0001], …), not named metadata blocks.</summary>
    private static bool IsNoteSection(string section)
    {
        if (section.Length == 0)
            return false;

        foreach (var ch in section)
        {
            if (ch is < '0' or > '9')
                return false;
        }

        return true;
    }

    /// <summary>
    /// UTF-8 (with or without BOM), UTF-16 LE/BE (with or without BOM), otherwise the locale code page:
    /// UTAU editors save Shift-JIS on Japanese systems and GBK on Chinese ones.
    /// </summary>
    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        if (DetectUtf16(bytes) is { } utf16)
            return utf16;

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not valid UTF-8: fall through to the locale code pages.
        }

        return DecodeLegacy(bytes);
    }

    /// <summary>
    /// UTF-16 without a BOM would otherwise pass the UTF-8 check with a NUL beside every ASCII
    /// character, so the section headers would never match. Detect it by the zero-byte pattern.
    /// </summary>
    private static string? DetectUtf16(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes.Length % 2 != 0)
            return null;

        var sample = Math.Min(bytes.Length, 8192);
        var pairs = sample / 2;
        var evenZeros = 0;
        var oddZeros = 0;
        for (var i = 0; i < sample; i += 2)
        {
            if (bytes[i] == 0)
                evenZeros++;
            if (bytes[i + 1] == 0)
                oddZeros++;
        }

        if (oddZeros > pairs * 0.2 && oddZeros > evenZeros * 3)
            return Encoding.Unicode.GetString(bytes);
        if (evenZeros > pairs * 0.2 && evenZeros > oddZeros * 3)
            return Encoding.BigEndianUnicode.GetString(bytes);
        return null;
    }

    /// <summary>
    /// Shift-JIS and GBK both decode almost any byte sequence without errors, so pick the candidate
    /// whose result looks like real lyrics instead of trusting the first code page.
    /// </summary>
    private static string DecodeLegacy(byte[] bytes)
    {
        var shiftJis = Decode(ShiftJis, bytes);
        var gbk = Decode(Gbk, bytes);
        if (shiftJis is null)
            return gbk ?? Encoding.Latin1.GetString(bytes);
        if (gbk is null)
            return shiftJis;
        return ScoreText(gbk) > ScoreText(shiftJis) ? gbk : shiftJis;
    }

    private static string? Decode(Encoding? encoding, byte[] bytes)
    {
        if (encoding is null)
            return null;

        var lenient = (Encoding)encoding.Clone();
        lenient.DecoderFallback = new DecoderReplacementFallback("\uFFFD");
        return lenient.GetString(bytes);
    }

    /// <summary>
    /// Lyric plausibility: kana and Han characters score positively, while half-width katakana and
    /// replacement characters are the fingerprint of text decoded with the wrong code page.
    /// </summary>
    private static int ScoreText(string text)
    {
        var score = 0;
        foreach (var ch in text)
        {
            switch (ch)
            {
                case >= '\u3040' and <= '\u30FF':
                    score += 3;
                    break;
                case >= '\uFF61' and <= '\uFF9F':
                    score -= 4;
                    break;
                case >= '\u4E00' and <= '\u9FFF':
                    score += 1;
                    break;
                case '\uFFFD':
                    score -= 6;
                    break;
                case '\r' or '\n' or '\t':
                    break;
                default:
                    if (char.IsControl(ch))
                        score -= 6;
                    break;
            }
        }

        return score;
    }

    private static Encoding? TryGetCodePage(int codePage)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
