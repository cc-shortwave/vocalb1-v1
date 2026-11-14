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
        TestNodeGraph();
        TestDictionary();
        TestLyricPhrase();
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
    // 2. Project JSON round-trip (three data-block libraries)
    // ------------------------------------------------------------------
    private static void TestProjectJson()
    {
        var project = new ProjectDocument
        {
            Name = "自检工程",
            Bpm = 100,
            BeatsPerBar = 4,
            BeatUnit = 4,
        };

        var leadLyric = new LyricDocument
        {
            Name = "主唱歌词",
            Segments =
            [
                new VoiceSegment { Audio = @"voicebank\_で.wav", SrcPitch = "D4", DstPitch = "G4", BeatStart = 1, BeatEnd = 2, Vowel = "e" },
                new VoiceSegment { Audio = @"voicebank\_い.wav", SrcPitch = "D4", DstPitch = "E4", BeatStart = 2, BeatEnd = 3.5, Vowel = "i" },
            ],
            PitchLine =
            [
                new PitchPoint { Beat = 1, Pitch = "G4" },
                new PitchPoint { Beat = 3.5, Pitch = null },
            ],
        };
        var harmonyLyric = new LyricDocument
        {
            Name = "和声歌词",
            Segments =
            [
                new VoiceSegment { Audio = @"voicebank\_あ.wav", SrcPitch = "D4", DstPitch = "C4", BeatStart = 1, BeatEnd = 4, Vowel = "a" },
            ],
        };
        var leadParameters = new ParameterSet { Name = "主唱参数" };
        leadParameters.Base.ApScale = 0.42;
        var harmonyParameters = new ParameterSet { Name = "和声参数" };
        harmonyParameters.Base.ApScale = 0.11;
        harmonyParameters.Base.BandRatios = [1, 2, 3, 4, 5, 6, 7, 8];
        var harmonyVowel = harmonyParameters.Graph.Add(NodeKind.VowelManager, 30, 24);
        harmonyVowel.FormantIndex = 1;
        var harmonyPosition = harmonyParameters.Graph.Add(NodeKind.FormantPosition, 300, 24);
        var harmonyOutput = harmonyParameters.Graph.Add(NodeKind.ParameterOutput, 560, 24);
        harmonyParameters.Graph.Connect(harmonyVowel.Id, 0, harmonyPosition.Id, 1);
        harmonyParameters.Graph.Connect(harmonyPosition.Id, 0, harmonyOutput.Id,
            ParameterNodeGraph.PositionSocket);

        project.Lyrics.AddRange([leadLyric, harmonyLyric]);
        project.ParameterSets.AddRange([leadParameters, harmonyParameters]);
        project.Singers.Add(new SingerDocument
        {
            Name = "主唱",
            LibraryRoot = @"voicebank\lead",
            LyricId = leadLyric.Id,
            ParameterSetId = leadParameters.Id,
        });
        project.Singers.Add(new SingerDocument
        {
            Name = "和声",
            LibraryRoot = @"voicebank\harmony",
            LyricId = harmonyLyric.Id,
            ParameterSetId = harmonyParameters.Id,
        });

        var json = ProjectJson.Serialize(project);
        var back = ProjectJson.Deserialize(json);

        Check("JSON 往返：名称", back.Name == "自检工程");
        Check("JSON 往返：三类数据块数量",
            back.Lyrics.Count == 2 && back.ParameterSets.Count == 2 && back.Singers.Count == 2);
        Check("JSON 往返：歌手的声库与绑定独立",
            back.Singers[0].LibraryRoot == @"voicebank\lead"
            && back.Singers[1].LibraryRoot == @"voicebank\harmony"
            && back.Singers[0].LyricId == back.Lyrics[0].Id
            && back.Singers[1].ParameterSetId == back.ParameterSets[1].Id);
        Check("JSON 往返：歌词段数与元音",
            back.Lyrics[0].Segments.Count == 2 && back.Lyrics[1].Segments.Count == 1
            && back.Lyrics[0].Segments[0].Vowel == "e");
        Check("JSON 往返：参数按数据块独立",
            Math.Abs(back.ParameterSets[0].Base.ApScale - 0.42) < 1e-9
            && Math.Abs(back.ParameterSets[1].Base.ApScale - 0.11) < 1e-9);
        Check("JSON 往返：频段增益保留",
            back.ParameterSets[1].Base.BandRatios is [1, 2, 3, 4, 5, 6, 7, 8]);
        Check("JSON 往返：节点图保留",
            back.ParameterSets[1].Graph.GroupFor("formant.F2") is not null
            && back.ParameterSets[1].Graph.Nodes.Count(n => n.Kind == NodeKind.ParameterOutput) == 1
            && back.ParameterSets[1].Graph.Nodes.Any(n => n.Kind == NodeKind.VowelManager)
            && back.ParameterSets[1].Graph.Links.Count == 2);
        Check("JSON 往返：音高线端点", back.Lyrics[0].PitchLine.Count == 2 && back.Lyrics[0].PitchLine[0].Pitch == "G4");
        Check("JSON 往返：无声端点保持 null", back.Lyrics[0].PitchLine[1].Pitch is null);
        Check("JSON 使用 camelCase",
            json.Contains("\"beatStart\"") && json.Contains("\"pitchLine\"")
            && json.Contains("\"lyrics\"") && json.Contains("\"parameterSets\"") && json.Contains("\"singers\""));
        Check("v3 工程不是只读", !back.IsLegacyReadOnly);

        var issues = ProjectValidator.Validate(project, checkAudioFiles: false);
        Check("校验器接受合法工程", !ProjectValidator.HasError(issues),
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
        Check("旧格式工程转换为 v3 只读对象",
            legacy.IsLegacyReadOnly && legacy.Singers.Count == 1 && legacy.Lyrics.Count == 1
            && legacy.Lyrics[0].Segments.Count == 1
            && Math.Abs(legacy.ParameterSets[0].Base.ApScale - 0.33) < 1e-9);
    }

    // ------------------------------------------------------------------
    // 2b. Node graph evaluation
    // ------------------------------------------------------------------
    private static void TestNodeGraph()
    {
        var profile = VoicebankFormantProfile.Load(null);

        Check("节点目录：包含时间输入、数学与声库/汇总节点",
            NodeCatalog.All.Count >= 26
            && NodeCatalog.All.Any(d => d.Kind == NodeKind.TimeInput)
            && NodeCatalog.All.Any(d => d.Kind == NodeKind.VoicebankData)
            && NodeCatalog.All.Any(d => d.Kind == NodeKind.FieldDecompose)
            && NodeCatalog.All.Count(d => d.Category == NodeCatalog.CategoryMath) >= 10
            && NodeCatalog.All.Count(d => d.Category == NodeCatalog.CategorySummary) == 4);

        // Four arithmetic nodes fill the four breath inputs of the summary node.
        var arithmetic = new ParameterNodeGraph();
        var breath = arithmetic.Add(NodeKind.BreathParameters, 300, 0);
        var outNode = arithmetic.Add(NodeKind.ParameterOutput, 600, 0);
        arithmetic.Connect(breath.Id, 0, outNode.Id, ParameterNodeGraph.BreathSocket);
        foreach (var (kind, first, second, index) in new[]
                 {
                     (NodeKind.Add, 1.0, 2.0, 0),
                     (NodeKind.Subtract, 5.0, 2.0, 1),
                     (NodeKind.Multiply, 3.0, 4.0, 2),
                     (NodeKind.Divide, 8.0, 2.0, 3),
                 })
        {
            var left = arithmetic.Add(NodeKind.Number, 0, 0);
            left.Value = first;
            var right = arithmetic.Add(NodeKind.Number, 0, 100);
            right.Value = second;
            var op = arithmetic.Add(kind, 200, 0);
            arithmetic.Connect(left.Id, 0, op.Id, 0);
            arithmetic.Connect(right.Id, 0, op.Id, 1);
            arithmetic.Connect(op.Id, 0, breath.Id, index);
        }

        var arithmeticValues = new NodeGraphEvaluator(arithmetic, Globals(), profile, key => 0)
            .EvaluateOutputs();
        Check("节点图：加减乘除写入气声汇总",
            Math.Abs(arithmeticValues["breathyThr"] - 3) < 1e-9
            && Math.Abs(arithmeticValues["apScale"] - 3) < 1e-9
            && Math.Abs(arithmeticValues["apMax"] - 12) < 1e-9
            && Math.Abs(arithmeticValues["breathGain"] - 4) < 1e-9);

        var zero = new ParameterNodeGraph();
        var zeroLeft = zero.Add(NodeKind.Number, 0, 0);
        zeroLeft.Value = 7;
        var zeroRight = zero.Add(NodeKind.Number, 0, 100);
        zeroRight.Value = 0;
        var zeroDivide = zero.Add(NodeKind.Divide, 200, 0);
        var zeroBreath = zero.Add(NodeKind.BreathParameters, 400, 0);
        var zeroOutput = zero.Add(NodeKind.ParameterOutput, 600, 0);
        zero.Connect(zeroLeft.Id, 0, zeroDivide.Id, 0);
        zero.Connect(zeroRight.Id, 0, zeroDivide.Id, 1);
        zero.Connect(zeroDivide.Id, 0, zeroBreath.Id, 3);
        zero.Connect(zeroBreath.Id, 0, zeroOutput.Id, ParameterNodeGraph.BreathSocket);
        var zeroValues = new NodeGraphEvaluator(zero, Globals(), profile, key => 0).EvaluateOutputs();
        Check("节点图：除零返回被除数",
            zeroValues.TryGetValue("breathGain", out var divided) && Math.Abs(divided - 7) < 1e-9);

        var graph = new ParameterNodeGraph();
        var number = graph.Add(NodeKind.Number, 0, 0);
        number.Value = 3;
        var graphBreath = graph.Add(NodeKind.BreathParameters, 300, 0);
        var output = graph.Add(NodeKind.ParameterOutput, 600, 0);
        graph.Connect(number.Id, 0, graphBreath.Id, 3);
        graph.Connect(graphBreath.Id, 0, output.Id, ParameterNodeGraph.BreathSocket);

        var values = new NodeGraphEvaluator(graph, Globals(), profile, key => 1.0).EvaluateOutputs();
        Check("节点图：数值节点写入气声汇总",
            values.TryGetValue("breathGain", out var written) && Math.Abs(written - 3) < 1e-9,
            values.TryGetValue("breathGain", out var v) ? v.ToString("F3") : "缺少输出");

        var remap = new ParameterNodeGraph();
        var source = remap.Add(NodeKind.Number, 0, 0);
        source.Value = 5;
        var map = remap.Add(NodeKind.MapRange, 200, 0);
        map.SetInputDefault(1, 0);
        map.SetInputDefault(2, 10);
        map.SetInputDefault(3, 0);
        map.SetInputDefault(4, 1);
        var remapBreath = remap.Add(NodeKind.BreathParameters, 400, 0);
        var remapOutput = remap.Add(NodeKind.ParameterOutput, 600, 0);
        remap.Connect(source.Id, 0, map.Id, 0);
        remap.Connect(map.Id, 0, remapBreath.Id, 1);
        remap.Connect(remapBreath.Id, 0, remapOutput.Id, ParameterNodeGraph.BreathSocket);
        var mapped = new NodeGraphEvaluator(remap, Globals(), profile, key => 1.0).EvaluateOutputs();
        Check("节点图：范围映射钳制并重映射",
            mapped.TryGetValue("apScale", out var ap) && Math.Abs(ap - 0.5) < 1e-9);

        var cycle = new ParameterNodeGraph();
        var a = cycle.Add(NodeKind.MathUnary, 0, 0);
        var b = cycle.Add(NodeKind.MathUnary, 200, 0);
        var cycleBreath = cycle.Add(NodeKind.BreathParameters, 400, 0);
        var cycleOutput = cycle.Add(NodeKind.ParameterOutput, 600, 0);
        cycle.Connect(a.Id, 0, b.Id, 0);
        cycle.Connect(b.Id, 0, a.Id, 0);
        cycle.Connect(b.Id, 0, cycleBreath.Id, 3);
        cycle.Connect(cycleBreath.Id, 0, cycleOutput.Id, ParameterNodeGraph.BreathSocket);
        var cycleDetected = false;
        try
        {
            new NodeGraphEvaluator(cycle, Globals(), profile, key => 1.0).EvaluateOutputs();
        }
        catch (VocalB1.Core.Diagnostics.StageException)
        {
            cycleDetected = true;
        }
        Check("节点图：循环连接抛出结构化错误", cycleDetected);

        var defaultGraph = ParameterNodeGraph.CreateFormantDefault();
        var defaultValues = new NodeGraphEvaluator(defaultGraph, Globals("i"), profile,
            key => ParameterKeys.Get(key).Default).EvaluateOutputs();
        Check("节点图：默认图含四个汇总节点与一个输出节点",
            defaultGraph.Nodes.Count(n => n.Kind == NodeKind.ParameterOutput) == 1
            && defaultGraph.Nodes.Count(n => n.Kind == NodeKind.VoicebankData) == 1
            && defaultGraph.Nodes.Count(n => n.Kind == NodeKind.FormantPosition) == 1
            && defaultGraph.Nodes.Count(n => n.Kind == NodeKind.FormantGain) == 1
            && defaultGraph.Nodes.Count(n => n.Kind == NodeKind.FormantBandwidth) == 1
            && defaultGraph.Nodes.Count(n => n.Kind == NodeKind.BreathParameters) == 1);
        Check("节点图：默认图接通四路输出",
            defaultGraph.WrittenKeys().Contains("formant.F1")
            && defaultGraph.WrittenKeys().Contains("formant.BW7")
            && defaultGraph.WrittenKeys().Contains("breathGain")
            && Enumerable.Range(1, 7).All(i =>
                defaultValues.ContainsKey($"{ParameterKeys.FormantPrefix}{i}")));

        // An unwired summary input keeps the baseline value of its key (voicebank / lyric / settings).
        var fallbackGraph = new ParameterNodeGraph();
        var fallbackGroup = fallbackGraph.Add(NodeKind.FormantPosition, 0, 0);
        var fallbackOutput = fallbackGraph.Add(NodeKind.ParameterOutput, 300, 0);
        fallbackGraph.Connect(fallbackGroup.Id, 0, fallbackOutput.Id, ParameterNodeGraph.PositionSocket);
        var fallbackValues = new NodeGraphEvaluator(fallbackGraph, Globals(), profile, key => 1234)
            .EvaluateOutputs();
        Check("节点图：汇总未连线输入使用默认值",
            Math.Abs(fallbackValues["formant.F3"] - 1234) < 1e-9);

        // Field decompose reads the selected field from the voicebank bundle.
        var fieldGraph = new ParameterNodeGraph();
        var data = fieldGraph.Add(NodeKind.VoicebankData, 0, 0);
        var decompose = fieldGraph.Add(NodeKind.FieldDecompose, 200, 0);
        decompose.Field = "formant.F2";
        var fieldBreath = fieldGraph.Add(NodeKind.BreathParameters, 400, 0);
        var fieldOutput = fieldGraph.Add(NodeKind.ParameterOutput, 600, 0);
        fieldGraph.Connect(data.Id, 0, decompose.Id, 0);
        fieldGraph.Connect(decompose.Id, 0, fieldBreath.Id, 3);
        fieldGraph.Connect(fieldBreath.Id, 0, fieldOutput.Id, ParameterNodeGraph.BreathSocket);
        var fieldValues = new NodeGraphEvaluator(fieldGraph, Globals("i"), profile, key => 1.0)
            .EvaluateOutputs();
        Check("节点图：字段分解输出声库字段",
            Math.Abs(fieldValues["breathGain"] - profile.Resolve("i", string.Empty)[1]) < 1e-9,
            fieldValues["breathGain"].ToString("F1"));

        // Bundle sockets only accept a matching bundle; scalars cannot be plugged into the output.
        var typed = new ParameterNodeGraph();
        var typedNumber = typed.Add(NodeKind.Number, 0, 0);
        var typedOutput = typed.Add(NodeKind.ParameterOutput, 300, 0);
        var typedData = typed.Add(NodeKind.VoicebankData, 0, 200);
        Check("节点图：插口类型不匹配时拒绝连线",
            !typed.Connect(typedNumber.Id, 0, typedOutput.Id, ParameterNodeGraph.BreathSocket)
            && !typed.Connect(typedData.Id, 0, typedOutput.Id, ParameterNodeGraph.PositionSocket));

        // Legacy graphs used one output node per key; migration folds them into the summary sockets.
        var legacyGraph = new ParameterNodeGraph();
        var legacyVowel = legacyGraph.Add(NodeKind.VowelManager, 0, 0);
        legacyVowel.FormantIndex = 1;
        var legacyOutput = legacyGraph.Add(NodeKind.ParameterOutput, 300, 0);
        legacyOutput.Text = "formant.F2";
        legacyGraph.Links.Add(new NodeLink
        {
            FromNode = legacyVowel.Id, FromSocket = 0, ToNode = legacyOutput.Id, ToSocket = 0,
        });
        var migrated = legacyGraph.MigrateLegacyOutputs();
        Check("节点图：旧单键输出迁移为汇总节点",
            migrated && legacyGraph.OutputNode() is not null
            && legacyGraph.GroupFor("formant.F2") is not null
            && legacyGraph.Nodes.Count(n => n.Kind == NodeKind.ParameterOutput) == 1
            && Math.Abs(new NodeGraphEvaluator(legacyGraph, Globals(), profile,
                key => ParameterKeys.Get(key).Default).EvaluateOutputs()["formant.F2"]
                - ParameterKeys.Get("formant.F2").Default) < 1e-9);

        var overlay = new ParameterNodeGraph();
        var overlayOutput = overlay.Add(NodeKind.ParameterOutput, 0, 0);
        var overlayBreath = overlay.Add(NodeKind.BreathParameters, 0, 200);
        overlay.Connect(overlayBreath.Id, 0, overlayOutput.Id, ParameterNodeGraph.BreathSocket);
        var shared = new Dictionary<string, double>(StringComparer.Ordinal) { ["breathGain"] = 2.0 };
        var overlayValues = new NodeGraphEvaluator(overlay, Globals(), profile,
            key => 1.0, key => shared.TryGetValue(key, out var value) ? value : 1.0)
            .EvaluateOutputs();
        Check("节点图：歌手叠加图未连线时继承共享值",
            overlayValues.TryGetValue("breathGain", out var inherited) && Math.Abs(inherited - 2.0) < 1e-9);
    }

    private static FrameGlobals Globals(string vowel = "a") =>
        new(0, 1, 0, 440, 9, vowel, string.Empty, 0, 1, 0);

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

        var lyric = new LyricDocument();
        var phrase = lyric.Phrase;
        var applied = LyricPhraseComposer.Apply(
            phrase, parsed.Words, lyric, 1.0, 0, "D4");
        Check("歌词应用：每个发音单位生成一个音块",
            lyric.Segments.Count == 3 && applied.Added == 3 && applied.Reused == 0,
            $"{lyric.Segments.Count} 段 / 新增 {applied.Added}");
        Check("歌词应用：拍位按单位依次排开（休止也占拍）",
            Math.Abs(lyric.Segments[0].BeatStart - 1) < 1e-9
            && Math.Abs(lyric.Segments[1].BeatStart - 2) < 1e-9
            && Math.Abs(lyric.Segments[2].BeatStart - 4) < 1e-9
            && Math.Abs(phrase.Words[1].BeatStart - 3) < 1e-9,
            string.Join(", ", lyric.Segments.Select(s => s.BeatStart)));
        Check("文本投影：原歌词", phrase.Text == "きゃ あ", phrase.Text);
        Check("对象真值：单位已绑定音块",
            phrase.Words[0].Units.All(u => u.SegmentId is not null)
            && phrase.Words[2].Units[0].SegmentId == lyric.Segments[2].Id);

        var firstUnitSegment = phrase.Words[0].Units[0].SegmentId;

        // Full re-parse + object diff: "あ" is replaced by "い" while "きゃ" and the space keep their melody.
        var reparsed = LyricPhraseParser.Parse(dictionary, "きゃ い", dir, null);
        var diff = LyricPhraseComposer.Apply(phrase, reparsed.Words, lyric, 1.0, 0, "D4");
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
        Check("歌词 diff：旧词音块已移除", lyric.Segments.Count == 3);

        // Deleting a rest word pulls the following words left.
        var restWord = phrase.Words[1];
        var restLength = restWord.BeatEnd - restWord.BeatStart;
        var followingStart = phrase.Words[2].BeatStart;
        LyricPhraseComposer.RemoveWord(phrase, restWord, lyric, pullFollowing: true);
        Check("删除休止：后续单位前移",
            phrase.Text == "きゃい"
            && Math.Abs(phrase.Words[1].BeatStart - (followingStart - restLength)) < 1e-9,
            $"{phrase.Text} / {phrase.Words[1].BeatStart}");

        // Deleting a real word mutes every pronunciation unit into a same-length rest.
        var wordToMute = phrase.Words[0];
        var muteStart = wordToMute.BeatStart;
        var muteEnd = wordToMute.BeatEnd;
        LyricPhraseComposer.MuteWord(phrase, wordToMute, lyric);
        Check("删除歌词：整词发音单位变为同长休止",
            wordToMute.IsRest && wordToMute.Units.Count == 2
            && Math.Abs(wordToMute.BeatStart - muteStart) < 1e-9
            && Math.Abs(wordToMute.BeatEnd - muteEnd) < 1e-9);
        Check("删除歌词：音块移除，文本投影为空格",
            lyric.Segments.Count == 1
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

        // Persistence: the phrase is part of the lyric data-block and survives save/load, segment ids included.
        var project = new ProjectDocument { Name = "歌词持久化", Lyrics = [lyric] };
        var json = ProjectJson.Serialize(project);
        var loaded = ProjectJson.Deserialize(json);
        Check("歌词持久化：JSON 往返保留词与单位",
            loaded.Lyrics[0].Phrase.Words.Count == phrase.Words.Count
            && loaded.Lyrics[0].Phrase.Text == phrase.Text,
            loaded.Lyrics[0].Phrase.Text);
        Check("歌词持久化：休止与音块身份保留",
            loaded.Lyrics[0].Phrase.Words[0].IsRest
            && loaded.Lyrics[0].Phrase.Words[0].Units[0].Text == "きゃ"
            && loaded.Lyrics[0].Phrase.Words[1].Units[0].SegmentId == phrase.Words[1].Units[0].SegmentId);

        // Old data without a lyric phrase rebuilds it from the score segments.
        var legacyLyric = new LyricDocument
        {
            Segments =
            [
                new VoiceSegment { Audio = "x.wav", BeatStart = 1, BeatEnd = 2, LyricOverride = "あ" },
                new VoiceSegment { Audio = "y.wav", BeatStart = 2, BeatEnd = 3, LyricOverride = "い" },
            ],
        };
        var legacy = ProjectJson.Deserialize(
            ProjectJson.Serialize(new ProjectDocument { Lyrics = [legacyLyric] }).Replace(
                "\"phrase\": {", "\"phraseDisabled\": {", StringComparison.Ordinal));
        Check("旧数据：无歌词对象时按段重建",
            legacy.Lyrics[0].Phrase.Words.Count == 2
            && legacy.Lyrics[0].Phrase.Text == "あい",
            legacy.Lyrics[0].Phrase.Text);

        // Legacy projects may contain stacked segments; loading must push them apart (no overlaps allowed).
        var stackedLyric = new LyricDocument
        {
            Segments =
            [
                new VoiceSegment { Audio = "x.wav", BeatStart = 1, BeatEnd = 3 },
                new VoiceSegment { Audio = "y.wav", BeatStart = 2, BeatEnd = 4 },
            ],
        };
        var normalized = ProjectJson.Deserialize(
            ProjectJson.Serialize(new ProjectDocument { Lyrics = [stackedLyric] })).Lyrics[0];
        Check("旧数据：载入时消除重叠发音段",
            SegmentLayout.Overlaps(normalized).Count == 0
            && Math.Abs(normalized.Segments[1].BeatStart - 3) < 1e-9,
            string.Join("; ", normalized.Segments.Select(s => $"{s.BeatStart:0.##}-{s.BeatEnd:0.##}")));
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

        var track = new LyricDocument
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
            new LyricDocument { PitchLine = [new PitchPoint { Beat = 1, Pitch = "G4", IsTransition = true }] }
                .Clone().PitchLine[0].IsTransition);

        var a = new VoiceSegment { Audio = "a.wav", SrcPitch = "D4", DstPitch = "G4", BeatStart = 1, BeatEnd = 4 };
        var b = new VoiceSegment { Audio = "b.wav", SrcPitch = "D4", DstPitch = "E4", BeatStart = 2, BeatEnd = 3.5 };
        var c = new VoiceSegment { Audio = "c.wav", SrcPitch = "D4", DstPitch = "C4", BeatStart = 3, BeatEnd = 5 };
        var stacked = new LyricDocument
        {
            Segments = [a, b, c],
            PitchLine =
            [
                new PitchPoint { SegmentId = b.Id, Beat = 2.5, Pitch = "E4" },
                new PitchPoint { SegmentId = c.Id, Beat = 4, Pitch = "C4" },
            ],
        };
        Check("布局：强制消除重叠", SegmentLayout.RemoveOverlaps(stacked)
            && SegmentLayout.Overlaps(stacked).Count == 0
            && Math.Abs(b.BeatStart - 4) < 1e-9 && Math.Abs(c.BeatStart - 5.5) < 1e-9
            && Math.Abs(c.BeatEnd - 7.5) < 1e-9,
            $"{a.BeatStart:0.##}-{a.BeatEnd:0.##} / {b.BeatStart:0.##}-{b.BeatEnd:0.##} / {c.BeatStart:0.##}-{c.BeatEnd:0.##}");
        Check("布局：被消除重叠的节点同步平移",
            Math.Abs(stacked.PitchLine.First(p => p.SegmentId == b.Id).Beat - 4.5) < 1e-9);

        var roomy = new LyricDocument
        {
            Segments =
            [
                new VoiceSegment { Audio = "a.wav", BeatStart = 1, BeatEnd = 3 },
                new VoiceSegment { Audio = "b.wav", BeatStart = 4, BeatEnd = 6 },
            ],
        };
        Check("布局：新音符插入空档保持原位", Math.Abs(SegmentLayout.FindFreeStart(roomy, 3, 0.5) - 3) < 1e-9);
        Check("布局：新音符压住旧音符时后移",
            Math.Abs(SegmentLayout.FindFreeStart(roomy, 2.5, 1) - 3) < 1e-9
            && Math.Abs(SegmentLayout.FindFreeStart(roomy, 3, 2) - 6) < 1e-9);
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

        var project = new ProjectDocument { Name = "自检渲染" };
        var lyric = new LyricDocument
        {
            Name = "主旋律",
            Segments =
            [
                new VoiceSegment { Audio = files[0], SrcPitch = "D4", DstPitch = "G4", BeatStart = 1, BeatEnd = 2, KeepUnvoicedHead = true, Vowel = "e" },
                new VoiceSegment { Audio = files[1], SrcPitch = "D4", DstPitch = "G4", BeatStart = 2, BeatEnd = 3, Vowel = "i" },
            ],
            PitchLine =
            [
                new PitchPoint { Beat = 1, Pitch = "G4" },
                new PitchPoint { Beat = 3, Pitch = "G4" },
            ],
        };
        lyric.Phrase = LyricPhrase.FromSegments(lyric.Segments);
        var parameters = new ParameterSet { Name = "主旋律参数" };
        parameters.Base.AddBreath = true;
        parameters.Base.BitDepth = 24;
        var singer = new SingerDocument
        {
            Name = "主旋律",
            LibraryRoot = library,
            LyricId = lyric.Id,
            ParameterSetId = parameters.Id,
        };
        project.Lyrics.Add(lyric);
        project.ParameterSets.Add(parameters);
        project.Singers.Add(singer);

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

            var plan = SingerRenderPlan.Build(project, singer, 0, 1);
            var result = await engine.RenderAsync(
                new RenderRequest(project, plan, output), progress);
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
