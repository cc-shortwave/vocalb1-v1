using System.IO;
using System.Text;
using VocalB1.App;
using VocalB1.App.ViewModels;

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
}
