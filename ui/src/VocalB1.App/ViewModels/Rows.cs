using System.IO;
using System.Globalization;
using VocalB1.App.Infrastructure;
using VocalB1.App.Services;
using VocalB1.Core.Abstractions;
using VocalB1.Core.Dictionaries;
using VocalB1.Core.Models;

namespace VocalB1.App.ViewModels;

/// <summary>A row of the track tabs; name edits are written straight back to the model.</summary>
public sealed class TrackRow(TrackDocument model) : ObservableObject
{
    public TrackDocument Model { get; } = model;

    public string Name
    {
        get => Model.Name;
        set
        {
            if (Model.Name == value)
                return;
            Model.Name = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Solo toggle: preview playback prefers a soloed track's rendered result.</summary>
    public bool Solo
    {
        get => Model.Solo;
        set
        {
            if (Model.Solo == value)
                return;
            Model.Solo = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Used for both the accessible name and the default text of the tab header.</summary>
    public override string ToString() => Model.Name;
}

/// <summary>A row of the segment list: the lyric is directly editable, all other fields are read-only.</summary>
public sealed class SegmentRow(VoiceSegment model, int index) : ObservableObject
{
    public int Number { get; } = index + 1;

    public string Lyric
    {
        get => Model.Lyric;
        set
        {
            if (Model.LyricOverride == value)
                return;
            Model.LyricOverride = string.IsNullOrEmpty(value) ? null : value;
            OnPropertyChanged();
        }
    }

    public string RangeText { get; } = $"{model.BeatStart:0.##} → {model.BeatEnd:0.##}";
    public string PitchText { get; } = $"{model.SrcPitch} → {model.DstPitch}";
    public VoiceSegment Model { get; } = model;

    /// <summary>The accessible name is a one-line summary, making it easy for screen readers and automation to check beats/pitches.</summary>
    public override string ToString() => $"{Number} {Lyric} {RangeText} {PitchText}";
}

/// <summary>A row of the user dictionary list.</summary>
public sealed class DictionaryEntryRow(DictionaryEntry model)
{
    public string Text { get; } = model.Text;
    public DictionaryEntry Model { get; } = model;

    /// <summary>For example, "2 notes · _し + _や".</summary>
    public string Detail { get; } =
        $"{model.Parts.Count} 音 · " +
        string.Join(" + ", model.Parts.Select(p => Path.GetFileNameWithoutExtension(p.Audio)));
}

public sealed record LogEntry(DateTime Time, string Kind, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public bool IsWarning => Kind is "warn" or "error";
}

/// <summary>A row of the language combo box: shows the name declared by the language pack.</summary>
public sealed class LanguageRow(LanguagePack pack)
{
    public LanguagePack Pack { get; } = pack;
    public string Code => Pack.Code;
    public string Name => Pack.Name;

    public override string ToString() => Name;
}

/// <summary>A row of the voicebank combo box: the singer name is the folder name under voicebank/.</summary>
public sealed class VoicebankOption(string name, string path)
{
    public string Name { get; } = name;
    public string Path { get; } = path;

    public override string ToString() => Name;
}

/// <summary>A row of a parameter slider: dragging writes back to the model and keeps the value text on the right in sync.</summary>
public sealed class ParameterRow(
    string label, double minimum, double maximum, double step, double value, Action<double> apply)
    : ObservableObject
{
    private double _value = value;

    public string Label { get; } = label;
    public double Minimum { get; } = minimum;
    public double Maximum { get; } = maximum;
    public double Step { get; } = step;
    public double LargeStep => Step * 10;
    public string ValueText => _value.ToString("0.##", CultureInfo.InvariantCulture);

    public double Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, value))
                return;
            OnPropertyChanged(nameof(ValueText));
            apply(value);
        }
    }
}
