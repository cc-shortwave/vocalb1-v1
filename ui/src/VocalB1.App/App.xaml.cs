using System.Windows;
using VocalB1.App.Services;
using VocalB1.App.ViewModels;

namespace VocalB1.App;

public partial class App : Application
{
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _services = AppServices.Create();
        var viewModel = new MainViewModel(_services);

        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            var shutdown = _services?.DisposeAsync().AsTask();
            if (shutdown is not null && !shutdown.Wait(TimeSpan.FromSeconds(5)))
            {
                // The engine client has already been asked to stop; do not block the UI
                // forever if a child process hangs during teardown.
            }
        }
        catch
        {
            // Do not throw during shutdown
        }

        base.OnExit(e);
    }
}
