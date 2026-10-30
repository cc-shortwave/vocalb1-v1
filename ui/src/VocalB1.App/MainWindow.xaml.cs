using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VocalB1.App.Controls;
using VocalB1.App.Services;
using VocalB1.App.ViewModels;
using VocalB1.Core.Serialization;

namespace VocalB1.App;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel();
        // After the first layout pass, sync the language combo box and replace static UI text with the previously selected language.
        Loaded += (_, _) =>
        {
            _viewModel?.BeginLanguageSelection();
            LanguageService.Apply(this);
        };
        Closing += OnWindowClosing;
    }

    private void AttachViewModel()
    {
        if (_viewModel is not null)
            _viewModel.LogAppended -= OnLogAppended;

        _viewModel = DataContext as MainViewModel;

        if (_viewModel is not null)
            _viewModel.LogAppended += OnLogAppended;
    }

    private void OnLogAppended(object? sender, EventArgs e)
    {
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    /// <summary>The track tab strip stays on a single row: the wheel scrolls horizontally, and tabs never wrap no matter how many there are.</summary>
    private void OnTrackTabsPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TrackTabs.ApplyTemplate();
        if (TrackTabs.Template.FindName("TabScroller", TrackTabs) is not ScrollViewer scroller)
            return;

        scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnRollEditStarted(object? sender, EventArgs e) => _viewModel?.BeginEdit();

    private void OnRollEditCommitted(object? sender, EventArgs e) => _viewModel?.RefreshActiveTrack();

    private void OnRollNoteAddRequested(object? sender, NoteAddEventArgs e) =>
        _viewModel?.AddNoteAt(e.Beat, e.Semitone);

    private void OnRollNoteDrawRequested(object? sender, NoteDrawEventArgs e) =>
        _viewModel?.AddNoteFromDraw(e.BeatStart, e.BeatEnd, e.Semitone);

    private void OnRollPlayheadGrabbed(object? sender, EventArgs e) =>
        _viewModel?.PauseForPlayheadDrag();

    /// <summary>Accept files dropped anywhere on the window (voicebank folders and project files).</summary>
    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        // Release the drag source (Explorer waits for this handler) before any blocking work or dialog.
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            return;

        var path = paths[0];
        Dispatcher.BeginInvoke(new Action(() => _ = HandleDroppedPathAsync(path)), DispatcherPriority.Background);
    }

    private async Task HandleDroppedPathAsync(string path)
    {
        if (_viewModel is not { } viewModel)
            return;

        if (Directory.Exists(path))
        {
            if (await viewModel.InstallVoicebankAsync(path) is { } error)
                MessageBox.Show(error, "安装声库", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (File.Exists(path) && path.EndsWith(ProjectJson.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            viewModel.OpenProjectFromPath(path);
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel is not null && !_viewModel.ConfirmDiscardChanges("退出"))
            e.Cancel = true;
    }
}
