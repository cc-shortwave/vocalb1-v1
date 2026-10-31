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

/// <summary>
/// One minimal voicebank unit below the lyric bar: shows the resolved wav label and its beat length.
/// A unit may also be a rest (no linked segment): the note block is removed while its beat range is kept.
/// The vertical line on the right edge adjusts the beat length in both cases.
/// </summary>
public sealed class LyricUnitRow(double length = 1.0, double beatStart = 0.0) : ObservableObject
{
    /// <summary>Horizontal scale of the lyric strip (pixels per beat); the vertical line drag is converted with it.</summary>
    public const double PixelsPerBeat = 40.0;

    private const double VerticalLineWidth = 10.0;

    /// <summary>Stable identity for undo coalescing while the same unit's line is dragged.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Generated D4 block; null means this unit is a rest.</summary>
    public VoiceSegment? Segment { get; private set; }

    /// <summary>Lyric characters this unit belongs to (the entry text; "休止" for a rest).</summary>
    public string Source { get; private set; } = "休止";

    public string Display { get; private set; } = "休止";

    public double Length { get; private set; } = Math.Max(0.01, length);

    public double BeatStart { get; private set; } = beatStart;

    public double BeatEnd => BeatStart + Length;

    public bool IsRest => Segment is null;

    public double PixelWidth => Math.Max(36, Length * PixelsPerBeat);

    /// <summary>Width of one strip item: the block plus the vertical line handle on its right edge.</summary>
    public double StripWidth => PixelWidth + VerticalLineWidth;

    public string BeatText => $"{Length:0.##} 拍";

    /// <summary>Strip label: the voicebank unit first, then the owning lyric character in parentheses.</summary>
    public string Caption => IsRest ? $"{Display}(空格)" : $"{Display}({Source})";

    public string Detail => IsRest
        ? $"歌词：空格（休止） · {BeatStart:0.##} → {BeatEnd:0.##}"
        : $"歌词「{Source}」 · 音素 {Display} · {BeatStart:0.##} → {BeatEnd:0.##} · {Segment!.DstPitch}";

    /// <summary>Links (or unlinks, for a rest) the generated segment and its strip label.</summary>
    public void SetContent(VoiceSegment? segment, string display, string source)
    {
        Segment = segment;
        Display = display;
        Source = source;
        OnPropertyChanged(nameof(Segment));
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(IsRest));
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(Detail));
    }

    /// <summary>Updates the beat range (the linked segment is written by the view model).</summary>
    public void SetLayout(double beatStart, double length)
    {
        BeatStart = beatStart;
        Length = Math.Max(0.01, length);
        RefreshMetrics();
    }

    /// <summary>Called after the underlying timing changed (drag or external relayout).</summary>
    public void RefreshMetrics()
    {
        OnPropertyChanged(nameof(Length));
        OnPropertyChanged(nameof(BeatStart));
        OnPropertyChanged(nameof(BeatEnd));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(PixelWidth));
        OnPropertyChanged(nameof(StripWidth));
        OnPropertyChanged(nameof(BeatText));
        OnPropertyChanged(nameof(Detail));
    }
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
