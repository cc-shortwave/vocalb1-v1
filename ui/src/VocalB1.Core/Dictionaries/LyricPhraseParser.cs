using VocalB1.Core.Models;

namespace VocalB1.Core.Dictionaries;

/// <summary>Result of parsing lyric text into word/unit objects.</summary>
public sealed record LyricParseResult(
    List<LyricWord> Words,
    List<string> MissingAudio,
    List<string> Unmatched);

/// <summary>
/// Parses lyric text into <see cref="LyricWord"/> / <see cref="LyricUnit"/> objects.
/// The scan is longest-match-first; whitespace becomes a rest word, punctuation is skipped,
/// and characters missing from the dictionary become a one-unit rest (the original character is kept in Text).
/// </summary>
public static class LyricPhraseParser
{
    private const string RestDisplay = "休止";

    private static readonly char[] Punctuation = ['、', '，', '。', '！', '？'];

    public static LyricParseResult Parse(
        UserDictionary dictionary,
        string? text,
        string? dictionaryDirectory,
        string? libraryRoot,
        string? defaultSrcPitch = null)
    {
        ArgumentNullException.ThrowIfNull(dictionary);

        var words = new List<LyricWord>();
        var missing = new List<string>();
        var unmatched = new List<string>();
        if (string.IsNullOrEmpty(text))
            return new LyricParseResult(words, missing, unmatched);

        var byText = new Dictionary<string, DictionaryEntry>(StringComparer.Ordinal);
        var maxLength = 0;
        foreach (var entry in dictionary.Entries)
        {
            if (string.IsNullOrEmpty(entry.Text))
                continue;
            byText.TryAdd(entry.Text, entry);
            maxLength = Math.Max(maxLength, entry.Text.Length);
        }

        var index = 0;
        while (index < text.Length)
        {
            var current = text[index];

            if (char.IsWhiteSpace(current))
            {
                words.Add(RestWord(" "));
                index++;
                continue;
            }

            if (Punctuation.Contains(current))
            {
                index++;
                continue;
            }

            if (maxLength > 0 && TryMatch(byText, text, index, maxLength, out var candidate, out var entry))
            {
                words.Add(WordFromEntry(candidate, entry, dictionaryDirectory, libraryRoot,
                    defaultSrcPitch ?? dictionary.DefaultSrcPitch, missing));
                index += candidate.Length;
                continue;
            }

            // No dictionary entry: keep the character in the rest detail, show a space in the text box.
            unmatched.Add(current.ToString());
            words.Add(RestWord(current.ToString()));
            index++;
        }

        return new LyricParseResult(words, missing, unmatched);
    }

    private static bool TryMatch(
        IReadOnlyDictionary<string, DictionaryEntry> byText,
        string text,
        int index,
        int maxLength,
        out string candidate,
        out DictionaryEntry entry)
    {
        for (var length = Math.Min(maxLength, text.Length - index); length >= 1; length--)
        {
            candidate = text.Substring(index, length);
            if (byText.TryGetValue(candidate, out entry!))
                return true;
        }

        candidate = string.Empty;
        entry = null!;
        return false;
    }

    private static LyricWord WordFromEntry(
        string surface,
        DictionaryEntry entry,
        string? dictionaryDirectory,
        string? libraryRoot,
        string? defaultSrcPitch,
        List<string> missing)
    {
        var word = new LyricWord { Surface = surface };
        foreach (var part in entry.Parts)
        {
            var audio = DictionaryResolver.ResolveAudioPath(part.Audio, dictionaryDirectory, libraryRoot);
            if (audio is null)
            {
                missing.Add(part.Audio);
                word.Units.Add(new LyricUnit
                {
                    Text = surface,
                    Display = RestDisplay,
                    SrcPitch = FirstNonEmpty(part.SrcPitch, entry.SrcPitch, defaultSrcPitch),
                    IsRest = true,
                });
                continue;
            }

            word.Units.Add(new LyricUnit
            {
                Text = surface,
                Audio = audio,
                Display = LyricPhrase.SampleLabel(audio),
                SrcPitch = FirstNonEmpty(part.SrcPitch, entry.SrcPitch, defaultSrcPitch),
                KeepUnvoicedHead = part.KeepUnvoicedHead,
                IsRest = false,
            });
        }

        // A dictionary entry without parts would otherwise produce an empty word.
        if (word.Units.Count == 0)
            word.Units.Add(new LyricUnit { Text = surface, Display = RestDisplay, IsRest = true });

        return word;
    }

    private static LyricWord RestWord(string text) => new()
    {
        Surface = text,
        Units =
        [
            new LyricUnit
            {
                Text = text,
                Display = RestDisplay,
                IsRest = true,
            },
        ],
    };

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? VoiceSegment.DefaultSrcPitch;
}
