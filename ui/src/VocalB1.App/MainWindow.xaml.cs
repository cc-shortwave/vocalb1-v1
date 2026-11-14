using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            _viewModel.Logs.CollectionChanged -= OnLogsChanged;

        _viewModel = DataContext as MainViewModel;

        if (_viewModel is not null)
            _viewModel.Logs.CollectionChanged += OnLogsChanged;
    }

    private void OnLogsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Scroll after the ItemsControl finishes its own collection-changed pass; scrolling synchronously
        // here makes the item-container generator observe a half-applied collection and throw.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[0]);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnRollEditCommitted(object? sender, EventArgs e) => _viewModel?.OnRollEdited();

    private void OnLyricInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (sender is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _viewModel?.CommitLyricCommand.Execute(null);
        e.Handled = true;
    }

    private void OnLyricInputLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _viewModel?.CommitLyricCommand.Execute(null);
    }

    private void OnRollNoteAddRequested(object? sender, NoteAddEventArgs e) =>
        _viewModel?.AddNoteAt(e.Beat, e.Semitone);

    private void OnRollNoteDrawRequested(object? sender, NoteDrawEventArgs e) =>
        _viewModel?.DrawNoteAt(e.BeatStart, e.BeatEnd, e.Semitone);

    private void OnRollPlayheadGrabbed(object? sender, EventArgs e)
    {
        if (sender is PianoRollView { PlayheadBeat: { } beat })
            _viewModel?.SeekToBeat(beat);
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            return;

        var path = paths[0];
        if (File.Exists(path) && path.EndsWith(ProjectJson.FileExtension, StringComparison.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(new Action(() => _viewModel?.OpenProjectFromPath(path)));
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel is not null && !_viewModel.ConfirmDiscardChanges("退出"))
            e.Cancel = true;
    }
}
