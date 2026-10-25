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
