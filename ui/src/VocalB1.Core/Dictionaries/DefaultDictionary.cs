namespace VocalB1.Core.Dictionaries;

/// <summary>
/// Built-in default dictionary: kana entries plus common kanji (and a few common kana words).
/// Kana entries map each basic kana directly to the voicebank's "_&lt;kana&gt;.wav";
/// kanji entries are spelled out with the kana reading, so they resolve through the same files.
/// No absolute path is baked in — audio names are resolved against the active voicebank root.
/// </summary>
public static class DefaultDictionary
{
    /// <summary>Display name shown in the dictionary panel.</summary>
    public const string Name = "默认字典（假名 + 常见汉字）";

    /// <summary>The 142 basic kana of a single-note voicebank, separated by spaces.</summary>
    private const string KanaList =
        "あ い いぇ う うぃ うぇ うぉ え お おぁ " +
        "か が き きぇ きゃ きゅ きょ ぎ ぎぇ ぎゃ ぎゅ ぎょ く ぐ け げ こ ご " +
        "さ ざ し しぇ しゃ しゅ しょ じ じぇ じゃ じゅ じょ す すぃ ず ずぃ せ ぜ そ ぞ " +
        "た だ ち ちぇ ちゃ ちゅ ちょ つ つぁ つぃ つぇ つぉ て てぃ てゅ で でぃ でゅ と とぅ ど どぅ " +
        "な に にぇ にゃ にゅ にょ ぬ ね の " +
        "は ば ぱ ひ ひぇ ひゃ ひゅ ひょ び びぇ びゃ びゅ びょ ぴ ぴぇ ぴゃ ぴゅ ぴょ ふ ふぁ ふぃ ふぇ ふぉ ぶ ぷ へ べ ぺ ほ ぼ ぽ " +
        "ま み みぇ みゃ みゅ みょ む め も " +
        "や ゆ よ " +
        "ら り りぇ りゃ りゅ りょ る れ ろ " +
        "わ ん カ キ ク ケ コ ヴぁ ヴぃ ヴぇ ヴぉ";

    /// <summary>Common kanji (and a few mixed words); value is the hiragana reading.</summary>
    private static readonly (string Text, string Reading)[] KanjiWords =
    [
        ("私", "わたし"), ("君", "きみ"), ("僕", "ぼく"), ("人", "ひと"), ("日", "ひ"),
        ("月", "つき"), ("火", "ひ"), ("水", "みず"), ("木", "き"), ("金", "かね"),
        ("土", "つち"), ("山", "やま"), ("川", "かわ"), ("海", "うみ"), ("空", "そら"),
        ("雨", "あめ"), ("雪", "ゆき"), ("風", "かぜ"), ("花", "はな"), ("草", "くさ"),
        ("星", "ほし"), ("雲", "くも"), ("光", "ひかり"), ("影", "かげ"), ("音", "おと"),
        ("声", "こえ"), ("歌", "うた"), ("夢", "ゆめ"), ("愛", "あい"), ("心", "こころ"),
        ("手", "て"), ("足", "あし"), ("目", "め"), ("耳", "みみ"), ("口", "くち"),
        ("顔", "かお"), ("頭", "あたま"), ("体", "からだ"), ("今", "いま"), ("涙", "なみだ"),
        ("恋", "こい"), ("時間", "じかん"), ("世界", "せかい"), ("明日", "あした"),
        ("今日", "きょう"), ("昨日", "きのう"), ("未来", "みらい"), ("過去", "かこ"),
        ("現在", "げんざい"), ("学校", "がっこう"), ("先生", "せんせい"), ("学生", "がくせい"),
        ("友達", "ともだち"), ("家族", "かぞく"), ("名前", "なまえ"), ("音楽", "おんがく"),
        ("日本", "にほん"), ("言葉", "ことば"), ("気持ち", "きもち"), ("笑顔", "えがお"),
        ("希望", "きぼう"), ("幸せ", "しあわせ"), ("自由", "じゆう"), ("平和", "へいわ"),
        ("勇気", "ゆうき"), ("元気", "げんき"), ("本当", "ほんとう"), ("全部", "ぜんぶ"),
        ("一緒", "いっしょ"), ("大丈夫", "だいじょうぶ"), ("大好き", "だいすき"),
    ];

    /// <summary>Common greetings written in kana: each is spelled out with single-kana audio.</summary>
    private static readonly string[] KanaWords =
    [
        "ありがとう", "おはよう", "こんにちは", "さようなら", "すみません", "ごめんなさい", "おやすみ",
    ];

    private static readonly string[] Kana = KanaList.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static readonly HashSet<string> KanaSet = Kana.ToHashSet(StringComparer.Ordinal);

    /// <summary>Creates a fresh dictionary instance (callers may modify it freely).</summary>
    public static UserDictionary Create()
    {
        var entries = new List<DictionaryEntry>();

        foreach (var kana in Kana)
        {
            entries.Add(new DictionaryEntry
            {
                Text = kana,
                DefaultBeats = 1.0,
                Parts =
                [
                    new DictionaryPart
                    {
                        Audio = $"_{kana}.wav",
                        Weight = 1.0,
                        KeepUnvoicedHead = true,
                    },
                ],
            });
        }

        foreach (var word in KanaWords)
            AddWordEntry(entries, word, word);

        foreach (var (text, reading) in KanjiWords)
            AddWordEntry(entries, text, reading);

        return new UserDictionary
        {
            Name = Name,
            Description = "内置默认字典：假名直接对应声库音频，常见汉字与常用词按假名读音逐音拼接；可导入 .json 字典覆盖。",
            DefaultSrcPitch = "D4",
            Entries = entries,
        };
    }

    private static void AddWordEntry(List<DictionaryEntry> entries, string text, string reading)
    {
        var mora = SplitReading(reading);
        if (mora.Count == 0)
            return;

        var parts = new List<DictionaryPart>(mora.Count);
        for (var i = 0; i < mora.Count; i++)
        {
            parts.Add(new DictionaryPart
            {
                Audio = $"_{mora[i]}.wav",
                Weight = 1.0,
                Transition = i == 0 ? 0 : 4,
                KeepUnvoicedHead = i == 0,
            });
        }

        entries.Add(new DictionaryEntry
        {
            Text = text,
            DefaultBeats = mora.Count,
            Parts = parts,
        });
    }

    /// <summary>
    /// Splits a reading into kana parts with the longest match, skipping sounds the
    /// voicebank has no sample for (small tsu, long-vowel mark) after normalizing ぢ/づ/を.
    /// </summary>
    private static List<string> SplitReading(string reading)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < reading.Length)
        {
            // No audio for small tsu / long-vowel mark, so they carry no part.
            if (reading[i] is 'っ' or 'ッ' or 'ー')
            {
                i++;
                continue;
            }

            var max = Math.Min(2, reading.Length - i);
            var matched = false;
            for (var len = max; len >= 1; len--)
            {
                var candidate = Normalize(reading.Substring(i, len));
                if (!KanaSet.Contains(candidate))
                    continue;
                parts.Add(candidate);
                i += len;
                matched = true;
                break;
            }

            if (!matched)
                i++;
        }

        return parts;
    }

    private static string Normalize(string text) => text
        .Replace("ぢ", "じ")
        .Replace("づ", "ず")
        .Replace("を", "お");
}
