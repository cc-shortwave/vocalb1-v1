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

    // Press-and-hold panning: the timer keeps the piano roll moving at a constant speed while a
    // direction button is held down, instead of one jump per click.
    private readonly DispatcherTimer _rollHoldTimer = new(DispatcherPriority.Input)
    {
        Interval = TimeSpan.FromMilliseconds(16),
    };
    private double _rollHoldPanPerTick;
    private double _rollHoldRowsPerTick;
    private double _rollHoldRowsPending;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel();
        Loaded += (_, _) =>
        {
            _viewModel?.BeginLanguageSelection();
            LanguageService.Apply(this);
        };
        _rollHoldTimer.Tick += (_, _) => AdvanceRollHold();
        Deactivated += (_, _) => StopRollHold();
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

    // Pan / pitch scroll: the piano roll keeps its own view state, so the buttons drive it directly.
    // A press nudges once and then keeps moving until the mouse button is released.
    private void OnRollPanLeft(object sender, MouseButtonEventArgs e) => StartRollHold(-0.05, -0.5, 0, 0);

    private void OnRollPanRight(object sender, MouseButtonEventArgs e) => StartRollHold(0.05, 0.5, 0, 0);

    private void OnRollScrollUp(object sender, MouseButtonEventArgs e) => StartRollHold(0, 0, 1, 8);

    private void OnRollScrollDown(object sender, MouseButtonEventArgs e) => StartRollHold(0, 0, -1, -8);

    private void OnRollHoldMouseUp(object sender, MouseButtonEventArgs e) => StopRollHold();

    private void OnRollHoldLostCapture(object sender, MouseEventArgs e) => StopRollHold();

    /// <summary>
    /// Starts the constant-speed move: an immediate nudge (so a plain click still works) followed by a
    /// per-frame step until the button is released.
    /// </summary>
    private void StartRollHold(double firstPan, double panPerSecond, int firstRows, double rowsPerSecond)
    {
        StopRollHold();
        if (firstPan != 0)
            PianoRoll.PanBeats(firstPan);
        if (firstRows != 0)
            PianoRoll.ScrollPitchRows(firstRows);

        _rollHoldPanPerTick = panPerSecond * 0.016;
        _rollHoldRowsPerTick = rowsPerSecond * 0.016;
        _rollHoldRowsPending = 0;
        _rollHoldTimer.Start();
    }

    private void AdvanceRollHold()
    {
        if (_rollHoldPanPerTick != 0)
            PianoRoll.PanBeats(_rollHoldPanPerTick);

        if (_rollHoldRowsPerTick == 0)
            return;

        _rollHoldRowsPending += _rollHoldRowsPerTick;
        var rows = (int)_rollHoldRowsPending;
        if (rows == 0)
            return;

        _rollHoldRowsPending -= rows;
        PianoRoll.ScrollPitchRows(rows);
    }

    private void StopRollHold()
    {
        _rollHoldTimer.Stop();
        _rollHoldPanPerTick = 0;
        _rollHoldRowsPerTick = 0;
        _rollHoldRowsPending = 0;
    }

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

    /// <summary>While a voicebank is decomposed the overlay is modal: every shortcut is swallowed.</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel?.IsAnalyzingVoicebank == true)
            e.Handled = true;
    }

    /// <summary>Moves keyboard focus onto the overlay so no editor keeps receiving text input.</summary>
    private void OnAnalysisOverlayIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is UIElement overlay && e.NewValue is true)
            overlay.Focus();
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        // The analysis overlay is modal; a file dragged in must not bypass it.
        if (_viewModel?.IsAnalyzingVoicebank == true)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = IsDroppable(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Project files and UTAU sequences can be dropped; anything else is rejected.</summary>
    private static bool IsDroppable(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop)
        && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths
        && File.Exists(paths[0])
        && (ProjectJson.IsProjectFile(paths[0])
            || string.Equals(Path.GetExtension(paths[0]), ".ust", StringComparison.OrdinalIgnoreCase));

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_viewModel?.IsAnalyzingVoicebank == true)
            return;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            return;

        var path = paths[0];
        if (!File.Exists(path))
            return;

        if (ProjectJson.IsProjectFile(path))
            Dispatcher.BeginInvoke(new Action(() => _viewModel?.OpenProjectFromPath(path)));
        else if (string.Equals(Path.GetExtension(path), ".ust", StringComparison.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(new Action(() => _viewModel?.ImportUstFromPath(path)));
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel is not null && !_viewModel.ConfirmDiscardChanges("退出"))
            e.Cancel = true;
    }
}
