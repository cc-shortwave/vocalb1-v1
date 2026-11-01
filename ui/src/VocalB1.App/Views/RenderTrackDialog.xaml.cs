using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using VocalB1.App.Services;
using VocalB1.App.ViewModels;
using VocalB1.Core.Models;

namespace VocalB1.App.Views;

/// <summary>
/// Pre-render dialog: every track is listed with a checkbox, all checked by default.
/// The caller reads <see cref="SelectedTracks"/> after a positive <c>ShowDialog()</c> result.
/// </summary>
public partial class RenderTrackDialog : Window
{
    public RenderTrackDialog(IEnumerable<TrackDocument> tracks)
    {
        InitializeComponent();

        Options = new ObservableCollection<RenderTrackOption>(tracks.Select(t => new RenderTrackOption(t)));

        foreach (var option in Options)
            option.PropertyChanged += OnOptionChanged;

        DataContext = this;

        if (Application.Current?.MainWindow is { } owner && owner != this)
            Owner = owner;

        Loaded += (_, _) =>
        {
            LanguageService.Apply(this);
            UpdateState();
        };
    }

    public ObservableCollection<RenderTrackOption> Options { get; }

    /// <summary>Tracks whose checkbox is still checked when the dialog is confirmed.</summary>
    public IReadOnlyList<TrackDocument> SelectedTracks =>
        Options.Where(o => o.IsSelected).Select(o => o.Model).ToList();

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RenderTrackOption.IsSelected))
            UpdateState();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => SetAll(true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool selected)
    {
        foreach (var option in Options)
            option.IsSelected = selected;
    }

    private void OnRender(object sender, RoutedEventArgs e)
    {
        if (Options.Any(o => o.IsSelected))
            DialogResult = true;
    }

    private void UpdateState()
    {
        var count = Options.Count(o => o.IsSelected);
        CountText.Text = $"已选 {count} / {Options.Count} 条音轨";
        RenderButton.IsEnabled = count > 0;
    }
}
