using VocalB1.Core.Abstractions;
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
        TestWaveform();
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
    // 4. WAV reading
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

        // Stereo balance: the engine outputs mono; after rendering, convert to stereo and split left/right energy.
        var mono = Path.Combine(dir, "mono.wav");
        WriteMonoSineWav(mono, 44100, 0.2, 440, 0.5);
        Check("声音占比 50 保持原文件", !StereoBalanceProcessor.Apply(mono, 50) && WavReader.ReadInfo(mono).Channels == 1);

        Check("声音占比 100 转为立体声", StereoBalanceProcessor.Apply(mono, 100) && WavReader.ReadInfo(mono).Channels == 2);
        var bytes = File.ReadAllBytes(mono);
        var frameOffset = 44 + 10 * 4;
        var left = BitConverter.ToInt16(bytes, frameOffset);
        var right = BitConverter.ToInt16(bytes, frameOffset + 2);
        Check("声音占比 100 仅保留右声道", Math.Abs(left) <= 1 && right > 1000, $"L={left} R={right}");
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

            // Same as MainViewModel: the engine outputs mono; after rendering, convert to stereo by stereo balance.
            var applied = StereoBalanceProcessor.Apply(result.OutputPath, 100);
            Check("端到端声音占比 100 输出立体声",
                applied && WavReader.ReadInfo(result.OutputPath).Channels == 2);
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
