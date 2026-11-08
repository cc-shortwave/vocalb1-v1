using System.IO;
using System.Text;
using VocalB1.App;
using VocalB1.App.ViewModels;
using VocalB1.Core.Dictionaries;
using VocalB1.Engine.Audio;

namespace VocalB1.StartupTest;

/// <summary>
/// Developer-only smoke test: runs the real WPF startup path (App.OnStartup → services → view model → main window),
/// prints PASS/FAIL for each checkpoint, then exits. Excluded from release packaging.
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
                Console.WriteLine($"FAIL  {name}: {ex.Message}");
                failures++;
            }
        }

        var app = new VocalB1.App.App();
        app.InitializeComponent();

        app.Startup += async (_, _) =>
        {
            // App.OnStartup creates the services, view model, and window; let it finish first.
            await Task.Delay(500);

            var window = app.MainWindow;
            var viewModel = window?.DataContext as MainViewModel;

            Check("主窗口已创建", () => window is not null);
            Check("主窗口已显示", () => window?.IsVisible == true);
            Check("ViewModel 已绑定", () => viewModel is not null);
            Check("至少有一个音轨", () => viewModel!.Tracks.Count >= 1);

            Check("启动日志已写入", () => viewModel!.Logs.Count >= 1);
            Check("声库列表已初始化", () => viewModel!.Voicebanks is not null);
            Check("状态文本已就绪", () => !string.IsNullOrWhiteSpace(viewModel!.StatusText));

            // Lyric bar: commit decomposes text into minimal units and appends one D4 block per unit.
            var bank = Path.Combine(Path.GetTempPath(), "VocalB1StartupTest", "bank");
            Directory.CreateDirectory(bank);
            foreach (var name in new[] { "_あ.wav", "_い.wav", "_う.wav" })
                File.WriteAllBytes(Path.Combine(bank, name), []);

            Check("歌词栏：分解为最小单位并生成 D4 音块", () =>
            {
                viewModel!.LibraryRoot = bank;
                viewModel.RefreshLibraryCommand.Execute(null);
                viewModel.SelectedLibraryItem = viewModel.Library.First(
                    e => Path.GetFileNameWithoutExtension(e.Path).TrimStart('_') == "あ");
                viewModel.LyricInput = "あいう";
                viewModel.CommitLyricInput();
                return viewModel.LyricUnits.Count == 3
                    && viewModel.Segments.Count == 3
                    && viewModel.Segments.All(s => s.Model.DstPitch == "D4")
                    && Math.Abs(viewModel.Segments[0].Model.BeatStart - 1.0) < 1e-9
                    && Math.Abs(viewModel.Segments[2].Model.BeatStart - 3.0) < 1e-9;
            });

            Check("歌词栏：竖线拖动调整单位拍长并推移后续音块", () =>
            {
                viewModel!.ResizeLyricUnit(viewModel.LyricUnits[0], 0.5);
                return Math.Abs(viewModel.LyricUnits[0].Length - 1.5) < 1e-6
                    && Math.Abs(viewModel.Segments[1].Model.BeatStart - 2.5) < 1e-9
                    && Math.Abs(viewModel.Segments[2].Model.BeatStart - 3.5) < 1e-9;
            });

            // Deleting the last lyric replaces it with a space: the block is removed but the same gap remains.
            Check("歌词栏：删除歌词后音块删除并留下同长休止", () =>
            {
                var edit = viewModel!.TryBuildLyricDeletion("あいう", 3, 0, backspace: true);
                if (edit is null)
                    return false;
                viewModel.LyricInput = edit.Text;
                viewModel.CommitLyricInput();
                return edit.Text == "あい "
                    && viewModel.LyricUnits.Count == 3
                    && viewModel.LyricUnits[2].IsRest
                    && Math.Abs(viewModel.LyricUnits[2].Length - 1.0) < 1e-6
                    && Math.Abs(viewModel.LyricUnits[2].BeatStart - 3.5) < 1e-9
                    && viewModel.Segments.Count == 2
                    && viewModel.RollRestRegions.Count == 1;
            });

            // Deleting the space removes the rest entirely.
            Check("歌词栏：删除空格即删除休止", () =>
            {
                var edit = viewModel!.TryBuildLyricDeletion("あい ", 3, 0, backspace: true);
                if (edit is null)
                    return false;
                viewModel.LyricInput = edit.Text;
                viewModel.CommitLyricInput();
                return edit.Text == "あい"
                    && viewModel.LyricUnits.Count == 2
                    && viewModel.Segments.Count == 2
                    && viewModel.RollRestRegions.Count == 0;
            });

            // A note added manually in the roll gains a placeholder lyric so the lyric bar stays aligned.
            Check("歌词栏：手动添加音块后同步占位歌词", () =>
            {
                viewModel!.AddNoteAt(4.0, 9);
                return viewModel.LyricInput == "あいあ"
                    && viewModel.LyricUnits.Count == 3
                    && viewModel.LyricUnits[^1].Model.SegmentId is not null
                    && viewModel.LyricUnits[^1].Source == "あ"
                    && Math.Abs(viewModel.LyricUnits[^1].BeatStart - 4.0) < 1e-9
                    && Math.Abs(viewModel.LyricUnits[^1].Length - 1.0) < 1e-9
                    && viewModel.Segments.Count == 3;
            });

            // Re-committing a longer lyric keeps the existing melody and appends the extra units after it.
            Check("歌词栏：重新填写歌词复用原旋律并在末尾追加", () =>
            {
                viewModel!.Segments[0].Model.DstPitch = "C5";
                viewModel.LyricInput = "あいうあい";
                viewModel.CommitLyricInput();
                return viewModel.LyricUnits.Count == 5
                    && viewModel.Segments.Count == 5
                    && viewModel.Segments[0].Model.DstPitch == "C5"
                    && Math.Abs(viewModel.LyricUnits[0].BeatStart - 1.0) < 1e-9
                    && Math.Abs(viewModel.LyricUnits[0].Length - 1.5) < 1e-6
                    && Math.Abs(viewModel.LyricUnits[1].BeatStart - 2.5) < 1e-9
                    && Math.Abs(viewModel.LyricUnits[2].BeatStart - 3.5) < 1e-9
                    && Math.Abs(viewModel.LyricUnits[3].BeatStart - 4.0) < 1e-9
                    && Math.Abs(viewModel.LyricUnits[4].BeatStart - 5.0) < 1e-9
                    && Math.Abs(viewModel.LyricUnits[3].Length - 1.0) < 1e-9
                    && viewModel.Segments[3].Model.DstPitch == "A4";
            });

            // BGM is project-wide: importing resets the start to the project start (第 0 小节第 0 音).
            Check("BGM：导入 WAV 后全局生效且默认从工程起点开始", () =>
            {
                var bgmPath = Path.Combine(bank, "bgm.wav");
                WritePcm16Wav(bgmPath, 8000, 1, 8000, 4000);
                viewModel!.SetBgm(bgmPath);
                return viewModel.HasBgm
                    && string.Equals(viewModel.BgmPath, bgmPath, StringComparison.OrdinalIgnoreCase)
                    && viewModel.BgmName == "bgm.wav"
                    && Math.Abs(viewModel.BgmStartBeat) < 1e-9;
            });

            // The BGM block in the roll keeps a fixed width taken from the WAV length, and the start never goes negative.
            Check("BGM：卷帘音块长度按拍计算且起点不小于 0", () =>
            {
                var lengthOk = Math.Abs(viewModel!.BgmDurationBeats - 2.0) < 1e-6; // 1 s at 120 BPM = 2 beats
                viewModel.BgmStartBeat = -4.0;
                var clampedOk = Math.Abs(viewModel.BgmStartBeat) < 1e-9;
                viewModel.BgmStartBeat = 2.0;
                return lengthOk && clampedOk && Math.Abs(viewModel.BgmStartBeat - 2.0) < 1e-9;
            });

            // The start beat is adjustable; the mix keeps the original speed and only shifts the BGM.
            Check("BGM：开始拍可调并混入原速播放音频", () =>
            {
                viewModel!.BgmStartBeat = 2.0;
                var vocalPath = Path.Combine(bank, "vocal.wav");
                WritePcm16Wav(vocalPath, 8000, 1, 4000, 1000);
                var mixed = BgmMixer.Mix(vocalPath, viewModel.BgmPath!,
                    2.0 * 60.0 / viewModel.Bpm, Path.Combine(bank, "mixed.wav"));
                var info = WavReader.ReadInfo(mixed);
                return Math.Abs(viewModel.BgmStartBeat - 2.0) < 1e-9
                    && info.SampleRate == 8000
                    && info.Channels == 2
                    && info.BitsPerSample == 16
                    && info.FrameCount == 8000 + (long)Math.Round(2.0 * 60.0 / viewModel.Bpm * 8000);
            });

            Check("BGM：清除后工程不再带伴奏", () =>
            {
                viewModel!.ClearBgm();
                return !viewModel.HasBgm && Math.Abs(viewModel.BgmStartBeat) < 1e-9;
            });

            // The expanded Japanese dictionary: common words resolve as whole entries (longest match),
            // and every part maps to a single-kana sample of the voicebank.
            Check("默认字典：扩充后的常用词按假名分解", () =>
            {
                var dictionary = DefaultDictionary.Create();
                var tokens = DictionaryResolver.Tokenize(dictionary, "東京で歌声を聞く");
                return dictionary.Entries.Count >= 500
                    && tokens.Count == 11 // 東京 4 + で 1 + 歌声 4 + 聞く 2（「を」无声库音频，忽略）
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
