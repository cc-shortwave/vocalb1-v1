namespace VocalB1.Core.Models;

/// <summary>
/// Maps a lyric character or phoneme label to the vowel class used by the vowel manager
/// (a / i / u / e / o / n). Kana rows are classified by their vowel, so an imported kana lyric
/// still selects the right formant set.
/// </summary>
public static class LyricVowel
{
    public static string Guess(string? phoneme)
    {
        if (string.IsNullOrWhiteSpace(phoneme))
            return "a";

        var text = phoneme.Trim().TrimStart('_', '-').ToLowerInvariant();
        foreach (var ch in text)
        {
            var vowel = ch switch
            {
                'あ' or 'か' or 'さ' or 'た' or 'な' or 'は' or 'ま' or 'や' or 'ら' or 'わ'
                    or 'が' or 'ざ' or 'だ' or 'ば' or 'ぱ' => "a",
                'い' or 'き' or 'し' or 'ち' or 'に' or 'ひ' or 'み' or 'り'
                    or 'ぎ' or 'じ' or 'ぢ' or 'び' or 'ぴ' => "i",
                'う' or 'く' or 'す' or 'つ' or 'ぬ' or 'ふ' or 'む' or 'ゆ' or 'る'
                    or 'ぐ' or 'ず' or 'づ' or 'ぶ' or 'ぷ' => "u",
                'え' or 'け' or 'せ' or 'て' or 'ね' or 'へ' or 'め' or 'れ'
                    or 'げ' or 'ぜ' or 'で' or 'べ' or 'ぺ' => "e",
                'お' or 'こ' or 'そ' or 'と' or 'の' or 'ほ' or 'も' or 'よ' or 'ろ' or 'を'
                    or 'ご' or 'ぞ' or 'ど' or 'ぼ' or 'ぽ' => "o",
                'ん' => "n",
                _ => string.Empty,
            };
            if (vowel.Length > 0)
                return vowel;
        }

        return text.Length > 0 && "aiueon".Contains(text[^1]) ? text[^1].ToString() : "a";
    }
}
