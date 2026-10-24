using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VocalB1.App.Controls;
using VocalB1.App.Services;
using VocalB1.App.ViewModels;

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

    private void OnRollPlayheadGrabbed(object? sender, EventArgs e) =>
        _viewModel?.PauseForPlayheadDrag();
}
