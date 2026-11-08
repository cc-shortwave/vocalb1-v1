using VocalB1.Core.Abstractions;
using VocalB1.Core.Chorus;
using VocalB1.Core.Configuration;
using VocalB1.Core.Dictionaries;
using VocalB1.Core.Models;
using VocalB1.Core.Pitch;
using VocalB1.Core.Serialization;
using VocalB1.Engine.Audio;
using VocalB1.Engine.Bridge;

namespace VocalB1.SelfTest;

/// <summary>
/// Dependency-free self-test program:
///   dotnet run --project ui/tests/VocalB1.SelfTest            (models / pitch / audio / engine handshake)
///   dotnet run --project ui/tests/VocalB1.SelfTest -- --render (also runs one end-to-end synthesis)
/// </summary>
internal static class Program
{
    private static int _failures;
    private static int _total;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var runRender = args.Contains("--render", StringComparer.OrdinalIgnoreCase);

        Console.WriteLine("VocalB1 UI 底层自检");
        Console.WriteLine("──────────────────────────────");

        TestPitchMath();
        TestProjectJson();
        TestDictionary();
        TestLyricPhrase();
        TestWaveform();
        TestChorus();
        TestSegmentLayout();
        await TestEngineHandshakeAsync(runRender);
        await TestRenderAsync(runRender);

        Console.WriteLine("──────────────────────────────");
        Console.WriteLine($"总计 {_total} 项，失败 {_failures} 项");
        return _failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------
    // 1. Pitch parsing
    // ------------------------------------------------------------------
    private static void TestPitchMath()
    {
        CheckPitch("C4", 0);
        CheckPitch("G4", 7);
        CheckPitch("A4", 9);
        CheckPitch("440Hz", 9);
        CheckPitch("1D", 14);
        CheckPitch("-1C", -12);
        CheckPitch("0C#", 1);

        Check("PitchMath 反向格式化 C4", PitchMath.ToPitchName(0) == "C4");
        Check("PitchMath 反向格式化 G4", PitchMath.ToPitchName(7) == "G4");
        Check("PitchMath 反向格式化 C3", PitchMath.ToPitchName(-12) == "C3");
        Check("PitchMath 非法输入被拒绝", !PitchMath.TryParseSemitones("H9", out _));
    }

    private static void CheckPitch(string text, double expected)
    {
        var ok = PitchMath.TryParseSemitones(text, out var value) && Math.Abs(value - expected) < 0.011;
        Check($"PitchMath 解析 \"{text}\" → {expected}", ok, ok ? null : $"实际 {value}");
    }

    // ------------------------------------------------------------------
    // 2. Project JSON round-trip (multiple tracks, independent parameters)
    // ------------------------------------------------------------------
    private static void TestProjectJson()
    {
        var project = new ProjectDocument
        {
            Name = "自检工程",
            Bpm = 100,
            BeatsPerBar = 4,
            BeatUnit = 4,
            Tracks =
            [
                new TrackDocument
                {
                    Name = "主唱",
                    LibraryRoot = @"voicebank\lead",
                    Segments =
                    [
                        new VoiceSegment { Audio = @"voicebank\_で.wav", SrcPitch = "D4", DstPitch = "G4", BeatStart = 1, BeatEnd = 2 },
                        new VoiceSegment { Audio = @"voicebank\_い.wav", SrcPitch = "D4", DstPitch = "E4", BeatStart = 2, BeatEnd = 3.5 },
                    ],
                    PitchLine =
                    [
                        new PitchPoint { Beat = 1, Pitch = "G4" },
                        new PitchPoint { Beat = 3.5, Pitch = null },
                    ],
                },
                new TrackDocument
                {
                    Name = "和声",
                    LibraryRoot = @"voicebank\harmony",
                    Segments =
                    [
                        new VoiceSegment { Audio = @"voicebank\_あ.wav", SrcPitch = "D4", DstPitch = "C4", BeatStart = 1, BeatEnd = 4 },
                    ],
                },
            ],
        };
        project.Tracks[0].Settings.ApScale = 0.42;
        project.Tracks[1].Settings.ApScale = 0.11;
        project.Tracks[1].Settings.BandRatios = [1, 2, 3, 4, 5, 6, 7, 8];

        var json = ProjectJson.Serialize(project);
        var back = ProjectJson.Deserialize(json);

        Check("JSON 往返：名称", back.Name == "自检工程");
        Check("JSON 往返：音轨数", back.Tracks.Count == 2);
        Check("JSON 往返：音轨名与声库独立",
            back.Tracks[0].Name == "主唱" && back.Tracks[1].Name == "和声"
            && back.Tracks[0].LibraryRoot == @"voicebank\lead"
            && back.Tracks[1].LibraryRoot == @"voicebank\harmony");
        Check("JSON 往返：各轨段数", back.Tracks[0].Segments.Count == 2 && back.Tracks[1].Segments.Count == 1);
        Check("JSON 往返：参数按轨独立",
            Math.Abs(back.Tracks[0].Settings.ApScale - 0.42) < 1e-9
            && Math.Abs(back.Tracks[1].Settings.ApScale - 0.11) < 1e-9);
        Check("JSON 往返：频段增益按轨保留",
            back.Tracks[1].Settings.BandRatios is [1, 2, 3, 4, 5, 6, 7, 8]);
        Check("JSON 往返：音高线端点", back.Tracks[0].PitchLine.Count == 2 && back.Tracks[0].PitchLine[0].Pitch == "G4");
        Check("JSON 往返：无声端点保持 null", back.Tracks[0].PitchLine[1].Pitch is null);
        Check("JSON 使用 camelCase", json.Contains("\"beatStart\"") && json.Contains("\"pitchLine\"") && json.Contains("\"tracks\""));

        var issues = ProjectValidator.Validate(project.Tracks[0], project, checkAudioFiles: false);
        Check("校验器接受合法音轨", !ProjectValidator.HasError(issues),
            string.Join("; ", issues.Select(i => i.Message)));

        const string legacyJson = """
            {
              "name": "旧工程",
              "bpm": 120,
              "segments": [
                { "audio": "voicebank\\_あ.wav", "srcPitch": "D4", "dstPitch": "G4", "beatStart": 1, "beatEnd": 2 }
              ],
              "pitchLine": [ { "beat": 1, "pitch": "G4" } ],
              "settings": { "apScale": 0.33 }
            }
            """;
        var legacy = ProjectJson.Deserialize(legacyJson);
        Check("旧格式工程迁移为单音轨",
            legacy.Tracks.Count == 1 && legacy.Tracks[0].Segments.Count == 1
            && Math.Abs(legacy.Tracks[0].Settings.ApScale - 0.33) < 1e-9);
    }

    // ------------------------------------------------------------------
    // 3. User dictionary
    // ------------------------------------------------------------------
    private static void TestDictionary()
    {
        var dir = Path.Combine(Path.GetTempPath(), "VocalB1SelfTest", "dict");
        Directory.CreateDirectory(dir);
        WriteSineWav(Path.Combine(dir, "a.wav"), 44100, 0.2, 440, 0.2);
        WriteSineWav(Path.Combine(dir, "b.wav"), 44100, 0.2, 660, 0.2);

        var dictionary = new UserDictionary
        {
            Name = "自检字典",
            DefaultSrcPitch = "D4",
            Entries =
            [
                new DictionaryEntry
                {
                    Text = "きゃ",
                    DefaultBeats = 2,
                    Parts =
                    [
                        new DictionaryPart { Audio = "a.wav", Weight = 1, KeepUnvoicedHead = true },
                        new DictionaryPart { Audio = "b.wav", Weight = 1, PitchOffset = 12, Transition = 4 },
                    ],
                },
            ],
        };

        var json = DictionaryJson.Serialize(dictionary);
        var back = DictionaryJson.Deserialize(json);

        Check("字典 JSON 往返：名称与条目", back.Name == "自检字典" && back.Entries.Count == 1);
        Check("字典 JSON 使用 camelCase", json.Contains("\"defaultBeats\"") && json.Contains("\"pitchOffset\""));
        Check("字典 JSON 保留 unicode 文本", json.Contains("\"きゃ\""));
        Check("字典结构校验通过", dictionary.Validate().Count == 0, string.Join("; ", dictionary.Validate()));

        var segments = DictionaryResolver.Resolve(back.Entries[0], dir, null, 3, 2, "G4", back.DefaultSrcPitch);
        Check("字典解析：段数等于组合数", segments.Count == 2);
        Check("字典解析：按权重分配拍长",
            Math.Abs(segments[0].BeatStart - 3) < 1e-9
            && Math.Abs(segments[0].BeatEnd - 4) < 1e-9
            && Math.Abs(segments[1].BeatEnd - 5) < 1e-9,
            $"{segments[0].BeatStart:0.##}-{segments[0].BeatEnd:0.##} / {segments[1].BeatStart:0.##}-{segments[1].BeatEnd:0.##}");
        Check("字典解析：音高偏移叠加",
            segments[0].DstPitch == "G4" && segments[1].DstPitch == "G5",
            $"{segments[0].DstPitch} / {segments[1].DstPitch}");
        Check("字典解析：相对音频路径已定位",
            File.Exists(segments[0].Audio)
            && segments[0].Audio.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase),
            segments[0].Audio);
        Check("字典解析：首段 transition 归零", segments[0].Transition == 0 && segments[1].Transition == 4);
        Check("字典解析：保留清音头", segments[0].KeepUnvoicedHead);

        var matches = DictionaryResolver.Split(back, "きゃ きゃ、");
        Check("字典分解：按最长匹配逐条拆开并跳过空白/标点",
            matches.Count == 2 && matches.All(m => m.Entry.Text == "きゃ"),
            string.Join(" | ", matches.Select(m => m.Text)));

        var longest = DictionaryResolver.Split(DefaultDictionary.Create(), "私君");
        Check("字典分解：汉字条目按最长匹配优先",
            longest.Count == 2 && longest[0].Text == "私" && longest[1].Text == "君",
            string.Join(" | ", longest.Select(m => m.Text)));

        var tokens = DictionaryResolver.Tokenize(back, "きゃ きゃ、");
        Check("歌词分行：每个音素一个 token，空白为休止",
            tokens.Count == 5
            && tokens[0].Start == 0 && tokens[1].Start == 0
            && tokens[2].IsRest && tokens[2].Start == 2
            && !tokens[3].IsRest && tokens[3].Start == 3,
            string.Join(" | ", tokens.Select(t => t.IsRest ? "休止" : t.Text)));

        var missing = new DictionaryEntry
        {
            Text = "x",
            Parts = [new DictionaryPart { Audio = "nope.wav" }],
        };
        var threw = false;
        try
        {
            DictionaryResolver.Resolve(missing, dir, null, 1, 1, "G4");
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Check("字典解析：音频缺失时报告错误", threw);

        var samplePath = Path.Combine(EngineOptions.Discover().RepositoryRoot, "ui", "user-dictionary.json");
        if (!File.Exists(samplePath))
        {
            Check("示例字典存在", false, samplePath);
            return;
        }

        var sample = DictionaryJson.Load(samplePath);
        Check("示例字典可读取", sample.Entries.Count > 0, $"{sample.Entries.Count} 条");
        Check("示例字典结构合法", sample.Validate().Count == 0, string.Join("; ", sample.Validate()));

        var libraryRoot = LoadOptions().LibraryRoot;
        var resolved = DictionaryResolver.Resolve(
            sample.Entries[0], Path.GetDirectoryName(samplePath), libraryRoot, 1, 1, "G4", sample.DefaultSrcPitch);
        Check("示例字典音频可在声库中定位", resolved.Count == sample.Entries[0].Parts.Count,
            resolved.FirstOrDefault()?.Audio ?? "（无）");
    }

    // ------------------------------------------------------------------
    // 4. Lyric object model: word/unit parsing, object diff, rests, persistence
    // ------------------------------------------------------------------
    private static void TestLyricPhrase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "VocalB1SelfTest", "lyric");
        Directory.CreateDirectory(dir);
        WriteSineWav(Path.Combine(dir, "a.wav"), 44100, 0.2, 440, 0.2);
        WriteSineWav(Path.Combine(dir, "b.wav"), 44100, 0.2, 660, 0.2);

        var dictionary = new UserDictionary
        {
            Name = "歌词自检字典",
            DefaultSrcPitch = "D4",
            Entries =
            [
                new DictionaryEntry
                {
                    Text = "きゃ",
                    Parts =
                    [
                        new DictionaryPart { Audio = "a.wav" },
                        new DictionaryPart { Audio = "b.wav" },
                    ],
                },
                new DictionaryEntry { Text = "あ", Parts = [new DictionaryPart { Audio = "a.wav" }] },
                new DictionaryEntry { Text = "い", Parts = [new DictionaryPart { Audio = "b.wav" }] },
            ],
        };

        var parsed = LyricPhraseParser.Parse(dictionary, "きゃ あ", dir, null);
        Check("歌词解析：词→发音单位两级结构",
            parsed.Words.Count == 3
            && parsed.Words[0].Units.Count == 2
            && parsed.Words[1].Units.Count == 1
            && parsed.Words[2].Units.Count == 1,
            $"{parsed.Words.Count} 词 / {string.Join(",", parsed.Words.Select(w => w.Units.Count))} 单位");
        Check("歌词解析：音块已定位到音频",
            parsed.Words[0].Units.All(u => !u.IsRest && File.Exists(u.Audio)));
        Check("歌词解析：空白成为休止词",
            parsed.Words[1].IsRest && parsed.Words[1].Projection == " ");

        var track = new TrackDocument();
        var phrase = track.LyricPhrase;
        var applied = LyricPhraseComposer.Apply(
            phrase, parsed.Words, track, 1.0, 0, "D4");
        Check("歌词应用：每个发音单位生成一个音块",
            track.Segments.Count == 3 && applied.Added == 3 && applied.Reused == 0,
            $"{track.Segments.Count} 段 / 新增 {applied.Added}");
        Check("歌词应用：拍位按单位依次排开（休止也占拍）",
            Math.Abs(track.Segments[0].BeatStart - 1) < 1e-9
            && Math.Abs(track.Segments[1].BeatStart - 2) < 1e-9
            && Math.Abs(track.Segments[2].BeatStart - 4) < 1e-9
            && Math.Abs(phrase.Words[1].BeatStart - 3) < 1e-9,
            string.Join(", ", track.Segments.Select(s => s.BeatStart)));
        Check("文本投影：原歌词", phrase.Text == "きゃ あ", phrase.Text);
        Check("对象真值：单位已绑定音块",
            phrase.Words[0].Units.All(u => u.SegmentId is not null)
            && phrase.Words[2].Units[0].SegmentId == track.Segments[2].Id);

        var firstUnitSegment = phrase.Words[0].Units[0].SegmentId;

        // Full re-parse + object diff: "あ" is replaced by "い" while "きゃ" and the space keep their melody.
        var reparsed = LyricPhraseParser.Parse(dictionary, "きゃ い", dir, null);
        var diff = LyricPhraseComposer.Apply(phrase, reparsed.Words, track, 1.0, 0, "D4");
        Check("歌词 diff：相同词复用旋律",
            phrase.Words[0].Units[0].SegmentId == firstUnitSegment
            && Math.Abs(phrase.Words[0].BeatStart - 1) < 1e-9);
        Check("歌词 diff：替换词与移除旧词计数",
            diff.Reused == 2 && diff.Added == 1 && diff.Removed == 1,
            $"reuse={diff.Reused} add={diff.Added} remove={diff.Removed}");
        Check("歌词 diff：新词接在上一词之后",
            phrase.Text == "きゃ い"
            && Math.Abs(phrase.Words[2].BeatStart - 4) < 1e-9
            && Math.Abs(phrase.Words[2].BeatEnd - 5) < 1e-9,
            $"{phrase.Text} / {phrase.Words[2].BeatStart}-{phrase.Words[2].BeatEnd}");
        Check("歌词 diff：旧词音块已移除", track.Segments.Count == 3);

        // Deleting a rest word pulls the following words left.
        var restWord = phrase.Words[1];
        var restLength = restWord.BeatEnd - restWord.BeatStart;
        var followingStart = phrase.Words[2].BeatStart;
        LyricPhraseComposer.RemoveWord(phrase, restWord, track, pullFollowing: true);
        Check("删除休止：后续单位前移",
            phrase.Text == "きゃい"
            && Math.Abs(phrase.Words[1].BeatStart - (followingStart - restLength)) < 1e-9,
            $"{phrase.Text} / {phrase.Words[1].BeatStart}");

        // Deleting a real word mutes every pronunciation unit into a same-length rest.
        var wordToMute = phrase.Words[0];
        var muteStart = wordToMute.BeatStart;
        var muteEnd = wordToMute.BeatEnd;
        LyricPhraseComposer.MuteWord(phrase, wordToMute, track);
        Check("删除歌词：整词发音单位变为同长休止",
            wordToMute.IsRest && wordToMute.Units.Count == 2
            && Math.Abs(wordToMute.BeatStart - muteStart) < 1e-9
            && Math.Abs(wordToMute.BeatEnd - muteEnd) < 1e-9);
        Check("删除歌词：音块移除，文本投影为空格",
            track.Segments.Count == 1
            && phrase.Text == "  " + phrase.Words[1].Surface,
            phrase.Text);
        Check("删除歌词：原字符仍保留在单位上",
            wordToMute.Units.All(u => u.Text == "きゃ" && u.Display == "休止"));

        // Unmatched characters become rests but keep their original character for the detail tooltip.
        var unmatched = LyricPhraseParser.Parse(dictionary, "ん", dir, null);
        Check("歌词解析：字典未匹配字符记录为休止",
            unmatched.Words.Count == 1 && unmatched.Unmatched.Count == 1
            && unmatched.Words[0].IsRest && unmatched.Words[0].Units[0].Text == "ん");
        Check("歌词解析：缺音频记录为休止",
            LyricPhraseParser.Parse(
                new UserDictionary
                {
                    Name = "缺音频",
                    Entries = [new DictionaryEntry { Text = "x", Parts = [new DictionaryPart { Audio = "nope.wav" }] }],
                },
                "x", dir, null).MissingAudio.Count == 1);

        // Persistence: the phrase is part of the track and survives save/load, segment ids included.
        var project = new ProjectDocument { Name = "歌词持久化", Tracks = [track] };
        var json = ProjectJson.Serialize(project);
        var loaded = ProjectJson.Deserialize(json);
        Check("歌词持久化：JSON 往返保留词与单位",
            loaded.Tracks[0].LyricPhrase.Words.Count == phrase.Words.Count
            && loaded.Tracks[0].LyricPhrase.Text == phrase.Text,
            loaded.Tracks[0].LyricPhrase.Text);
        Check("歌词持久化：休止与音块身份保留",
            loaded.Tracks[0].LyricPhrase.Words[0].IsRest
            && loaded.Tracks[0].LyricPhrase.Words[0].Units[0].Text == "きゃ"
            && loaded.Tracks[0].LyricPhrase.Words[1].Units[0].SegmentId == phrase.Words[1].Units[0].SegmentId);

        // Old projects without a lyric phrase rebuild it from their segments.
        var legacyTrack = new TrackDocument
        {
            Segments =
            [
                new VoiceSegment { Audio = "x.wav", BeatStart = 1, BeatEnd = 2, LyricOverride = "あ" },
                new VoiceSegment { Audio = "y.wav", BeatStart = 2, BeatEnd = 3, LyricOverride = "い" },
            ],
        };
        var legacy = ProjectJson.Deserialize(
            ProjectJson.Serialize(new ProjectDocument { Tracks = [legacyTrack] }).Replace(
                "\"lyricPhrase\": {", "\"lyricPhraseDisabled\": {", StringComparison.Ordinal));
        Check("旧工程：无歌词对象时按段重建",
            legacy.Tracks[0].LyricPhrase.Words.Count == 2
            && legacy.Tracks[0].LyricPhrase.Text == "あい",
            legacy.Tracks[0].LyricPhrase.Text);
    }

    // ------------------------------------------------------------------
    // 5. WAV reading
    // ------------------------------------------------------------------
    private static void TestWaveform()
    {
        var dir = Path.Combine(Path.GetTempPath(), "VocalB1SelfTest");
        Directory.CreateDirectory(dir);
        var wav = Path.Combine(dir, "tone.wav");
        WriteSineWav(wav, 44100, 0.5, 440, 0.5);

        var info = WavReader.ReadInfo(wav);
        Check("WAV 时长 ≈ 0.5s", Math.Abs(info.DurationSeconds - 0.5) < 0.01, info.DurationSeconds.ToString("F3"));
        Check("WAV 采样率 44100", info.SampleRate == 44100);
        Check("WAV 声道 2", info.Channels == 2);

        var peaks = WavReader.ReadPeaks(wav, 256);
        Check("波形桶数 256", peaks.BucketCount == 256);
        Check("波形峰值 ≈ 0.5", Math.Abs(peaks.PeakAmplitude - 0.5) < 0.02,
            peaks.PeakAmplitude.ToString("F3"));

        var provider = new WaveformProvider();
        var a = provider.Read(wav, 128);
        var b = provider.Read(wav, 128);
        Check("波形缓存返回同一实例", ReferenceEquals(a, b));

        // Track mixer: the selected tracks are rendered to one shared timeline and summed into one WAV.
        var trackA = Path.Combine(dir, "track_a.wav");
        var trackB = Path.Combine(dir, "track_b.wav");
        WriteMonoSineWav(trackA, 44100, 0.2, 440, 0.3);
        WriteMonoSineWav(trackB, 44100, 0.4, 440, 0.4);
        var mixed = Path.Combine(dir, "track_mix.wav");
        TrackMixer.Mix([trackA, trackB], mixed);
        var mixedInfo = WavReader.ReadInfo(mixed);
        Check("音轨合并：时长取最长音轨", Math.Abs(mixedInfo.DurationSeconds - 0.4) < 0.01,
            mixedInfo.DurationSeconds.ToString("F3"));
        Check("音轨合并：输出为立体声", mixedInfo.Channels == 2);
        Check("音轨合并：振幅按帧相加", Math.Abs(WavReader.ReadPeaks(mixed, 256).PeakAmplitude - 0.7) < 0.02,
            WavReader.ReadPeaks(mixed, 256).PeakAmplitude.ToString("F3"));
    }

    // ------------------------------------------------------------------
    // 5b. Chorus mode (stage math, persistence repair, processor output depth)
    // ------------------------------------------------------------------
    private static void TestChorus()
    {
        var dir = Path.Combine(Path.GetTempPath(), "VocalB1SelfTest");
        Directory.CreateDirectory(dir);

        Check("合唱：拍位吸附到 0.25 拍", Math.Abs(ChorusMath.SnapBeat(2.37) - 2.25) < 1e-9,
            ChorusMath.SnapBeat(2.37).ToString("F3"));
        var (leftGain, rightGain) = ChorusMath.PanGains(-5, 10);
        Check("合唱：舞台最左侧不消失", leftGain > rightGain && leftGain > 0.1,
            $"L={leftGain:F3} R={rightGain:F3}");

        var path = new List<ChorusKeyframe>
        {
            new() { Beat = 1, X = -3, Y = 1 },
            new() { Beat = 5, X = 3, Y = 2 },
        };
        var middle = ChorusMath.Evaluate(path, 3);
        Check("合唱：两点路径中点线性", Math.Abs(middle.X) < 1e-6 && Math.Abs(middle.Y - 1.5) < 1e-6,
            $"{middle.X:F3},{middle.Y:F3}");

        var unsorted = new List<ChorusKeyframe>
        {
            new() { Beat = 5, X = 3, Y = 2 },
            new() { Beat = 1, X = -3, Y = 1 },
        };
        var unsortedMiddle = ChorusMath.Evaluate(unsorted, 3);
        Check("合唱：乱序关键帧仍按拍位插值", Math.Abs(unsortedMiddle.X) < 1e-6,
            unsortedMiddle.X.ToString("F3"));

        var project = new ProjectDocument
        {
            Chorus = new ChorusDocument
            {
                StageWidth = 12,
                MicY = -1.5,
                Singers =
                [
                    new ChorusSinger
                    {
                        Name = "S1",
                        Path = [new ChorusKeyframe { Beat = 2, X = 1, Y = 2 }],
                    },
                ],
                Formations =
                [
                    new ChorusFormationPreset
                    {
                        Name = "队形",
                        Positions = [new ChorusFormationPoint { X = 1, Y = 2 }],
                    },
                ],
            },
        };
        var loaded = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        Check("合唱：工程往返保留舞台与歌手",
            Math.Abs(loaded.Chorus.StageWidth - 12) < 1e-9
            && loaded.Chorus.Singers.Count == 1
            && Math.Abs(loaded.Chorus.Singers[0].Path[0].Y - 2) < 1e-9);
        Check("合唱：工程往返保留导入队形",
            loaded.Chorus.Formations.Count == 1 && loaded.Chorus.Formations[0].Positions.Count == 1);

        // Non-finite values are repaired on load instead of reaching the STFT and silencing the audio.
        var repaired = ProjectJson.Deserialize(
            """
            {
              "chorus": {
                "stageWidth": "NaN",
                "micY": "NaN",
                "singers": [ { "trackId": "t1", "enabled": true,
                  "path": [ { "beat": 1, "x": "NaN", "y": 1 } ] } ]
              }
            }
            """);
        Check("合唱：加载时修复无效舞台参数",
            double.IsFinite(repaired.Chorus.StageWidth) && double.IsFinite(repaired.Chorus.MicY),
            $"{repaired.Chorus.StageWidth}/{repaired.Chorus.MicY}");
        Check("合唱：加载时剔除无效关键帧并回退中心",
            repaired.Chorus.Singers[0].Path.Count == 1 && repaired.Chorus.Singers[0].Path[0].X == 0);

        var invalid = new ProjectDocument
        {
            Chorus = new ChorusDocument
            {
                StageWidth = -1,
                Singers = [new ChorusSinger { Name = "坏歌手", Enabled = true }],
            },
        };
        var track = new TrackDocument();
        invalid.Tracks.Add(track);
        var issues = ProjectValidator.Validate(track, invalid, checkAudioFiles: false);
        Check("合唱：校验报告舞台尺寸非法",
            issues.Any(i => i.Severity == ProjectIssueSeverity.Error && i.Message.Contains("舞台")),
            string.Join(" | ", issues.Select(i => i.Message)));
        Check("合唱：校验报告歌手未绑定音轨",
            issues.Any(i => i.Message.Contains("未绑定音轨")));

        var mono = Path.Combine(dir, "chorus_src.wav");
        WriteMonoSineWav(mono, 44100, 0.5, 440, 0.5);
        var chorus = new ChorusDocument
        {
            StageWidth = 10,
            MicY = -2,
            Singers = [new ChorusSinger { TrackId = "t", Path = [new ChorusKeyframe { Beat = 1, X = -4, Y = 1 }] }],
        };
        var output16 = Path.Combine(dir, "chorus16.wav");
        ChorusProcessor.Process(mono, output16, chorus, chorus.Singers, 120);
        var info16 = WavReader.ReadInfo(output16);
        Check("合唱：默认输出 16-bit 立体声", info16.Channels == 2 && ReadBitDepth(output16) == 16,
            $"channels={info16.Channels} bits={ReadBitDepth(output16)}");
        Check("合唱：输出时长与输入一致", Math.Abs(info16.DurationSeconds - 0.5) < 0.02,
            info16.DurationSeconds.ToString("F3"));
        var (levelLeft, levelRight) = ReadChannelLevels(output16);
        Check("合唱：左侧歌手左声道更响", levelLeft > levelRight * 1.5,
            $"L={levelLeft:F3} R={levelRight:F3}");

        var output24 = Path.Combine(dir, "chorus24.wav");
        ChorusProcessor.Process(mono, output24, chorus, chorus.Singers, 120, 24);
        Check("合唱：按工程位深输出 24-bit", ReadBitDepth(output24) == 24, ReadBitDepth(output24).ToString());
        var (level24Left, _) = ReadChannelLevels(output24);
        Check("合唱：24-bit 输出不是静音", level24Left > 0, level24Left.ToString("F4"));
        Check("合唱：24-bit 输出可读取波形", WavReader.ReadPeaks(output24, 256).PeakAmplitude > 0,
            WavReader.ReadPeaks(output24, 256).PeakAmplitude.ToString("F4"));

        // Undo snapshots must not share the synthesis parameter arrays with the live settings.
        var settings = new SynthesisSettings();
        var snapshot = settings.Clone();
        settings.FollowRatio[0] = 0.99;
        settings.BandRatios[0] = 0.99;
        Check("撤销快照：合成参数数组深拷贝",
            snapshot.FollowRatio[0] != 0.99 && snapshot.BandRatios[0] != 0.99,
            $"{snapshot.FollowRatio[0]}/{snapshot.BandRatios[0]}");
    }

    private static int ReadBitDepth(string path) => BitConverter.ToInt16(File.ReadAllBytes(path), 34);

    /// <summary>Mean absolute sample value per channel, used to compare the stereo image.</summary>
    private static (double Left, double Right) ReadChannelLevels(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var channels = BitConverter.ToInt16(bytes, 22);
        var bytesPerSample = BitConverter.ToInt16(bytes, 34) / 8;
        var frames = (bytes.Length - 44) / (channels * bytesPerSample);
        double left = 0;
        double right = 0;
        for (var i = 0; i < frames; i++)
        {
            var offset = 44 + i * channels * bytesPerSample;
            left += Math.Abs(ReadSample(bytes, offset, bytesPerSample));
            right += Math.Abs(ReadSample(bytes, offset + bytesPerSample, bytesPerSample));
        }

        return (left / Math.Max(1, frames), right / Math.Max(1, frames));
    }

    private static double ReadSample(byte[] bytes, int offset, int bytesPerSample)
    {
        if (bytesPerSample == 3)
        {
            var value = BitConverter.ToUInt16(bytes, offset) | ((sbyte)bytes[offset + 2] << 16);
            return value / 8388608.0;
        }

        return BitConverter.ToInt16(bytes, offset) / 32768.0;
    }

    private static void WriteSineWav(string path, int sampleRate, double seconds, double frequency, double amplitude)
    {
        var frames = (int)(sampleRate * seconds);
        const short channels = 2;
        const short bits = 16;
        var dataBytes = frames * channels * bits / 8;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bits / 8);
        writer.Write((short)(channels * bits / 8));
        writer.Write(bits);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        for (var i = 0; i < frames; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * amplitude * short.MaxValue);
            writer.Write(v);
            writer.Write(v);
        }
    }

    private static void WriteMonoSineWav(string path, int sampleRate, double seconds, double frequency, double amplitude)
    {
        var frames = (int)(sampleRate * seconds);
        const short channels = 1;
        const short bits = 16;
        var dataBytes = frames * channels * bits / 8;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bits / 8);
        writer.Write((short)(channels * bits / 8));
        writer.Write(bits);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        for (var i = 0; i < frames; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * amplitude * short.MaxValue);
            writer.Write(v);
        }
    }

    // ------------------------------------------------------------------
    // 5. Segment timeline rules: overlaps, transition points, and right-side pushing
    // ------------------------------------------------------------------
    private static void TestSegmentLayout()
    {
        var first = new VoiceSegment { Audio = "a.wav", SrcPitch = "D4", DstPitch = "G4", BeatStart = 1, BeatEnd = 2.5 };
        var second = new VoiceSegment { Audio = "b.wav", SrcPitch = "D4", DstPitch = "E4", BeatStart = 2, BeatEnd = 4 };
        var third = new VoiceSegment { Audio = "c.wav", SrcPitch = "D4", DstPitch = "C4", BeatStart = 4, BeatEnd = 5 };

        var track = new TrackDocument
        {
            Segments = [first, second, third],
            PitchLine =
            [
                new PitchPoint { SegmentId = first.Id, Beat = 1, Pitch = "G4" },
                new PitchPoint { SegmentId = first.Id, Beat = 2.2, Pitch = "G4" },
                new PitchPoint { SegmentId = second.Id, Beat = 3, Pitch = "E4" },
                new PitchPoint { SegmentId = third.Id, Beat = 4.5, Pitch = "C4" },
            ],
        };

        var overlaps = SegmentLayout.Overlaps(track);
        Check("布局：识别一处相邻重叠",
            overlaps.Count == 1 && Math.Abs(overlaps[0].Start - 2) < 1e-9 && Math.Abs(overlaps[0].End - 2.5) < 1e-9,
            string.Join("; ", overlaps.Select(o => $"{o.Start:0.##}-{o.End:0.##}")));
        Check("布局：重叠区内部判定", SegmentLayout.IsInOverlap(track, 2.2) && !SegmentLayout.IsInOverlap(track, 2));
        Check("布局：无重叠时不相交",
            SegmentLayout.Overlaps([first, third]).Count == 0);

        SegmentLayout.SyncTransitionPoints(track);
        var transition = track.PitchLine.FirstOrDefault(p => p.IsTransition);
        Check("布局：重叠区生成中心渐变节点",
            transition is not null && transition.SegmentId == second.Id && Math.Abs(transition.Beat - 2.25) < 1e-9,
            transition is null ? "（无）" : $"拍 {transition.Beat:0.###}");
        var clamped = track.PitchLine.Last(p => p.SegmentId == first.Id && !p.IsTransition);
        Check("布局：普通控制点被挤出重叠区", Math.Abs(clamped.Beat - 2) < 1e-9,
            clamped.Beat.ToString("0.###"));

        SegmentLayout.PushAfter(track, first, 2.5);
        Check("布局：向右推挤保持原间隔",
            Math.Abs(second.BeatStart - 2.5) < 1e-9 && Math.Abs(second.BeatEnd - 4.5) < 1e-9
            && Math.Abs(third.BeatStart - 4.5) < 1e-9 && Math.Abs(third.BeatEnd - 5.5) < 1e-9,
            $"{second.BeatStart:0.##}-{second.BeatEnd:0.##} / {third.BeatStart:0.##}-{third.BeatEnd:0.##}");
        Check("布局：被推挤音块的节点同步后移",
            Math.Abs(track.PitchLine.First(p => p.SegmentId == second.Id && !p.IsTransition).Beat - 3.5) < 1e-9);

        SegmentLayout.SyncTransitionPoints(track);
        Check("布局：重叠消失后渐变节点被清理", !track.PitchLine.Any(p => p.IsTransition));
        Check("布局：克隆保留渐变标记",
            new TrackDocument { PitchLine = [new PitchPoint { Beat = 1, Pitch = "G4", IsTransition = true }] }
                .Clone().PitchLine[0].IsTransition);
    }

    // ------------------------------------------------------------------
    // 6. Engine handshake
    // ------------------------------------------------------------------
    private static EngineOptions LoadOptions()
    {
        var discovered = EngineOptions.Discover();
        var settings = Path.Combine(discovered.RepositoryRoot, "ui", "engine.settings.json");
        return File.Exists(settings) ? EngineOptions.Load(settings) : discovered;
    }

    private static async Task TestEngineHandshakeAsync(bool verbose)
    {
        var options = LoadOptions();
        Console.WriteLine($"  仓库根目录: {options.RepositoryRoot}");
        Console.WriteLine($"  Python    : {options.PythonPath}");

        if (!File.Exists(options.BridgeScriptPath))
        {
            Check("桥接脚本存在", false, options.BridgeScriptPath);
            return;
        }

        await using var engine = new PythonEngineClient(options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.StartupTimeoutSeconds));
        try
        {
            await engine.InitializeAsync(timeout.Token);
            Check("引擎握手成功", engine.IsReady, $"Python {engine.PythonVersion}");
            if (verbose)
                Console.WriteLine($"  Python 版本: {engine.PythonVersion}");
        }
        catch (Exception ex)
        {
            Check("引擎握手成功", false, ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // 5. End-to-end render (optional)
    // ------------------------------------------------------------------
    private static async Task TestRenderAsync(bool enabled)
    {
        if (!enabled)
        {
            Console.WriteLine("  （跳端到端渲染，加 -- --render 开启）");
            return;
        }

        var options = LoadOptions();
        var library = options.LibraryRoot;
        var files = new[] { "_で.wav", "_い.wav" }
            .Select(n => Path.Combine(library, n))
            .ToArray();

        if (files.Any(f => !File.Exists(f)))
        {
            Check("端到端渲染", false, "声库示例音频不存在：" + library);
            return;
        }

        var project = new ProjectDocument
        {
            Name = "自检渲染",
            Tracks =
            [
                new TrackDocument
                {
                    Name = "主旋律",
                    Segments =
                    [
                        new VoiceSegment { Audio = files[0], SrcPitch = "D4", DstPitch = "G4", BeatStart = 1, BeatEnd = 2, KeepUnvoicedHead = true },
                        new VoiceSegment { Audio = files[1], SrcPitch = "D4", DstPitch = "G4", BeatStart = 2, BeatEnd = 3 },
                    ],
                    PitchLine =
                    [
                        new PitchPoint { Beat = 1, Pitch = "G4" },
                        new PitchPoint { Beat = 3, Pitch = "G4" },
                    ],
                },
            ],
        };
        project.Tracks[0].Settings.AddBreath = true;
        project.Tracks[0].Settings.BitDepth = 24;

        var output = Path.Combine(Path.GetTempPath(), "VocalB1SelfTest", "render.wav");
        if (File.Exists(output))
            File.Delete(output);

        await using var engine = new PythonEngineClient(options);
        try
        {
            await engine.InitializeAsync();
            var progress = new Progress<EngineEvent>(e =>
            {
                if (e.Kind == EngineEventKind.Log && e.Message.Length > 0)
                    Console.WriteLine("    [engine] " + e.Message);
            });

            var result = await engine.RenderAsync(
                new RenderRequest(project, project.Tracks[0], output), progress);
            Check("端到端渲染产出文件", File.Exists(result.OutputPath) && new FileInfo(result.OutputPath).Length > 44);
            Check("端到端渲染时长 > 0", result.DurationSeconds > 0.05, result.DurationSeconds.ToString("F2"));
        }
        catch (Exception ex)
        {
            Check("端到端渲染", false, ex.Message);
        }
    }

    // ------------------------------------------------------------------
    private static void Check(string name, bool ok, string? detail = null)
    {
        _total++;
        if (ok)
        {
            Console.WriteLine($"  [PASS] {name}");
            return;
        }

        _failures++;
        Console.WriteLine($"  [FAIL] {name}" + (detail is null ? "" : $" — {detail}"));
    }
}
