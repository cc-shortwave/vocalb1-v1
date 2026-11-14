using System.IO;
using System.Text;
using VocalB1.App;
using VocalB1.App.ViewModels;
using VocalB1.Core.Dictionaries;
using VocalB1.Engine.Audio;

namespace VocalB1.StartupTest;

/// <summary>
/// Developer-only smoke test: runs the real WPF startup path (App.OnStartup → services → view model → main
/// window) against the v3 object-library model, prints PASS/FAIL for each checkpoint, then exits.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        var failures = 0;

        void Check(string name, Func<bool> probe)
        {
            try
            {
                var ok = probe();
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
                if (!ok)
                    failures++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL  {name}: {ex}");
                failures++;
            }
        }

        var app = new VocalB1.App.App();
        app.InitializeComponent();

        app.Startup += async (_, _) =>
        {
            await Task.Delay(500);

            var window = app.MainWindow;
            var viewModel = window?.DataContext as MainViewModel;

            Check("主窗口已创建", () => window is not null);
            Check("主窗口已显示", () => window?.IsVisible == true);
            Check("ViewModel 已绑定", () => viewModel is not null);
            Check("三类数据块库已初始化",
                () => viewModel!.Lyrics.Count == 1 && viewModel.ParameterSets.Count == 1 && viewModel.Singers.Count == 1);
            Check("启动时可编辑（v3 工程）", () => viewModel!.CanEdit);
            Check("启动日志已写入", () => viewModel!.Logs.Count >= 1);
            Check("状态文本已就绪", () => !string.IsNullOrWhiteSpace(viewModel!.StatusText));

            var bank = Path.Combine(Path.GetTempPath(), "VocalB1StartupTest", "bank");
            if (Directory.Exists(bank))
                Directory.Delete(bank, true);
            Directory.CreateDirectory(bank);
            foreach (var name in new[] { "_あ.wav", "_い.wav", "_う.wav" })
                File.WriteAllBytes(Path.Combine(bank, name), []);

            Check("歌手绑定声库后刷新音频库", () =>
            {
                viewModel!.SelectedSingerVoicebank = bank;
                viewModel.RefreshLibraryCommand.Execute(null);
                viewModel.SelectedLibraryItem = viewModel.Library.First(
                    e => Path.GetFileNameWithoutExtension(e.Path).TrimStart('_') == "あ");
                return viewModel.Library.Count == 3 && viewModel.SelectedLibraryItem is not null;
            });

            Check("歌词栏：分解为最小单位并生成 D4 音块", () =>
            {
                viewModel!.LyricInput = "あいう";
                viewModel.CommitLyricCommand.Execute(null);
                return viewModel.LyricUnits.Count == 3
                    && viewModel.Segments.Count == 3
                    && viewModel.Segments.All(s => s.Model.DstPitch == "D4")
                    && Math.Abs(viewModel.Segments[0].Model.BeatStart - 1.0) < 1e-9
                    && Math.Abs(viewModel.Segments[2].Model.BeatStart - 3.0) < 1e-9
                    && viewModel.ActiveLyric!.Phrase.Text == "あいう";
            });

            Check("元音管理器：音节元音传播到发音段", () =>
            {
                viewModel!.SelectedUnit = viewModel.LyricUnits[0];
                viewModel.UnitVowel = "i";
                var segment = viewModel.Segments.First(s => s.Model.Id == viewModel.SelectedUnit!.Model.SegmentId);
                return viewModel.SelectedUnit!.Model.Vowel == "i" && segment.Model.Vowel == "i";
            });

            Check("手动共振峰目标写入音节与发音段", () =>
            {
                viewModel!.SelectedUnit = viewModel.LyricUnits[0];
                viewModel.UnitFormantText = "700, 1200, 2600, 3300, 3900, 4400, 4900";
                viewModel.ApplyUnitFormantsCommand.Execute(null);
                var unit = viewModel.SelectedUnit!.Model;
                var segment = viewModel.Segments.First(s => s.Model.Id == unit.SegmentId);
                return unit.FormantHz is { Length: 7 } hz
                    && Math.Abs(hz[0] - 700) < 1e-9
                    && segment.Model.FormantHz is { Length: 7 } segHz
                    && Math.Abs(segHz[1] - 1200) < 1e-9;
            });

            Check("恢复自动：手动共振峰被清除", () =>
            {
                viewModel!.ClearUnitFormantsCommand.Execute(null);
                return viewModel.SelectedUnit!.Model.FormantHz is null;
            });

            Check("节点图：默认参数集含四个汇总节点与一个输出节点", () =>
            {
                var graph = viewModel!.SelectedParameterSet!.Model.Graph;
                return graph.Nodes.Count(n => n.Kind == VocalB1.Core.Models.NodeKind.ParameterOutput) == 1
                    && graph.Nodes.Count(n => n.Kind == VocalB1.Core.Models.NodeKind.VoicebankData) == 1
                    && graph.Nodes.Count(n => n.Kind == VocalB1.Core.Models.NodeKind.FormantPosition) == 1
                    && graph.Nodes.Count(n => n.Kind == VocalB1.Core.Models.NodeKind.FormantGain) == 1
                    && graph.Nodes.Count(n => n.Kind == VocalB1.Core.Models.NodeKind.FormantBandwidth) == 1
                    && graph.Nodes.Count(n => n.Kind == VocalB1.Core.Models.NodeKind.BreathParameters) == 1
                    && graph.WrittenKeys().Contains("formant.F1")
                    && graph.WrittenKeys().Contains("breathGain");
            });

            Check("节点图：添加输出节点接通当前参数键", () =>
            {
                viewModel!.SelectedParameterKey = viewModel.ParameterKeyRows.First(k => k.Key == "breathGain");
                viewModel.AddOutputNodeCommand.Execute(null);
                return viewModel.ActiveGraph!.GroupFor("breathGain") is not null
                    && viewModel.SelectedNode?.Kind == VocalB1.Core.Models.NodeKind.BreathParameters;
            });

            Check("节点图：歌手叠加图与共享图互不影响", () =>
            {
                viewModel!.EditSingerGraph = true;
                viewModel.SelectedParameterKey = viewModel.ParameterKeyRows.First(k => k.Key == "breathGain");
                viewModel.AddOutputNodeCommand.Execute(null);
                var singer = viewModel.SelectedSinger!.Model;
                return singer.Graph.GroupFor("breathGain") is not null
                    && viewModel.SelectedParameterSet!.Model.Graph
                        .Nodes.Count(n => n.Kind == VocalB1.Core.Models.NodeKind.BreathParameters) == 1;
            });

            Check("手动添加音块后同步占位歌词", () =>
            {
                viewModel!.AddNoteAt(4.0, 9);
                return viewModel.Segments.Count == 4
                    && viewModel.LyricUnits.Count == 4
                    && Math.Abs(viewModel.LyricUnits[^1].Model.BeatStart - 4.0) < 1e-9;
            });

            Check("复制歌手：歌词独立、参数共享", () =>
            {
                var originalParam = viewModel!.SelectedSinger!.Model.ParameterSetId;
                var originalLyric = viewModel.SelectedSinger!.Model.LyricId;
                viewModel.DuplicateSingerCommand.Execute(null);
                return viewModel.Singers.Count == 2
                    && viewModel.SelectedSinger!.Model.LyricId != originalLyric
                    && viewModel.SelectedSinger!.Model.ParameterSetId == originalParam;
            });

            Check("歌词与歌手一对一：切换绑定会解除其他歌手", () =>
            {
                var lyricId = viewModel!.Singers[0].Model.LyricId;
                viewModel.SelectedSinger = viewModel.Singers[1];
                viewModel.SelectedSingerLyric = viewModel.Lyrics.First(l => l.Id == lyricId);
                return viewModel.Singers.Count(s => s.Model.LyricId == lyricId) == 1
                    && viewModel.Singers[0].Model.LyricId is null;
            });

            Check("孤立清理：只清理 0 用户且无伪用户的数据块", () =>
            {
                viewModel!.AddLyricCommand.Execute(null);
                var before = viewModel.Lyrics.Count;
                viewModel.RemoveSingerCommand.Execute(null);
                var purged = viewModel.Lyrics.Count;
                return before >= purged;
            });

            Check("旧版 v2 JSON 加载为只读且可渲染", () =>
            {
                const string legacy = """
                    {"formatVersion":"2.0","name":"old","bpm":120,"beatsPerBar":3,"beatUnit":4,
                     "segments":[{"audio":"x.wav","srcPitch":"D4","dstPitch":"G4","beatStart":1,"beatEnd":2}],
                     "settings":{}}
                    """;
                var project = VocalB1.Core.Serialization.ProjectJson.Deserialize(legacy);
                return project.IsLegacyReadOnly && project.Singers.Count == 1 && project.Lyrics.Count == 1;
            });

            Check("BGM：导入后全局生效且默认从工程起点开始", () =>
            {
                var bgmPath = Path.Combine(bank, "bgm.wav");
                WritePcm16Wav(bgmPath, 8000, 1, 8000, 4000);
                viewModel!.SetBgm(bgmPath);
                return viewModel.HasBgm
                    && string.Equals(viewModel.BgmPath, bgmPath, StringComparison.OrdinalIgnoreCase)
                    && viewModel.BgmName == "bgm.wav"
                    && Math.Abs(viewModel.BgmStartBeat) < 1e-9;
            });

            Check("BGM：卷帘音块长度按拍计算且起点不小于 0", () =>
            {
                var lengthOk = Math.Abs(viewModel!.BgmDurationBeats - 2.0) < 1e-6;
                viewModel.BgmStartBeat = -4.0;
                var clampedOk = Math.Abs(viewModel.BgmStartBeat) < 1e-9;
                viewModel.BgmStartBeat = 2.0;
                return lengthOk && clampedOk && Math.Abs(viewModel.BgmStartBeat - 2.0) < 1e-9;
            });

            Check("BGM：开始拍可调并混入原速播放音频", () =>
            {
                var vocalPath = Path.Combine(bank, "vocal.wav");
                WritePcm16Wav(vocalPath, 8000, 1, 4000, 1000);
                var mixed = BgmMixer.Mix(vocalPath, viewModel!.BgmPath!,
                    2.0 * 60.0 / viewModel.Bpm, Path.Combine(bank, "mixed.wav"));
                var info = WavReader.ReadInfo(mixed);
                return info.SampleRate == 8000
                    && info.Channels == 2
                    && info.BitsPerSample == 16
                    && info.FrameCount == 8000 + (long)Math.Round(2.0 * 60.0 / viewModel.Bpm * 8000);
            });

            Check("BGM：清除后工程不再带伴奏", () =>
            {
                viewModel!.ClearBgm();
                return !viewModel.HasBgm && Math.Abs(viewModel.BgmStartBeat) < 1e-9;
            });

            Check("默认字典：常用词按假名分解", () =>
            {
                var dictionary = DefaultDictionary.Create();
                var tokens = DictionaryResolver.Tokenize(dictionary, "東京で歌声を聞く");
                return dictionary.Entries.Count >= 500
                    && tokens.Count == 11
                    && tokens.All(t => t.Part is null || t.Part.Audio!.StartsWith('_'));
            });

            app.Shutdown(failures == 0 ? 0 : 1);
        };

        int exitCode;
        try
        {
            exitCode = app.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  启动流程抛出异常：" + ex);
            return 1;
        }

        Console.WriteLine(failures == 0 ? "启动流程测试通过。" : $"启动流程测试失败：{failures} 项。");
        return exitCode;
    }

    /// <summary>Writes a flat 16-bit PCM WAV used by the BGM checks (no external audio files).</summary>
    private static void WritePcm16Wav(string path, int sampleRate, int channels, int frames, short value)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var dataBytes = frames * channels * 2;

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);

        for (var i = 0; i < frames * channels; i++)
            writer.Write(value);
    }
}
