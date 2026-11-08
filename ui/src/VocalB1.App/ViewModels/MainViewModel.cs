using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using VocalB1.App.Controls;
using VocalB1.App.Infrastructure;
using VocalB1.App.Services;
using VocalB1.Core.Abstractions;
using VocalB1.Core.Chorus;
using VocalB1.Core.Dictionaries;
using VocalB1.Core.Library;
using VocalB1.Core.Models;
using VocalB1.Core.Pitch;
using VocalB1.Core.Serialization;
using VocalB1.Engine.Audio;

namespace VocalB1.App.ViewModels;

/// <summary>
/// Main window ViewModel: only owns project state and command orchestration;
/// all engine/audio/serialization capabilities come from the Core / Engine projects.
/// A project contains multiple tracks; parameters and library are per track, while BPM and beat type are shared.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogEntries = 500;
    private const int MaxUndoSteps = 100;

    /// <summary>Synthesis frame constant: 12000 / BPM gives the frames per beat (frame period 5 ms).</summary>
    private const double FramesPerBeatConstant = 12000.0;

    /// <summary>Default crossfade between adjacent notes: 0.25 beat at the current tempo, stored as 5 ms frames.</summary>
    private double DefaultTransitionFrames => Math.Round(0.25 * FramesPerBeatConstant / Math.Max(1, _bpm));

    private readonly AppServices _services;
    private readonly List<ProjectDocument> _undoStack = [];
    private readonly List<ProjectDocument> _redoStack = [];
    private string? _undoCoalesceKey;
    private ProjectDocument _project;
    private TrackDocument _activeTrack = new();
    private string? _projectPath;
    /// <summary>Project snapshot taken on load/save, used to tell whether there are unsaved changes.</summary>
    private string _savedSnapshot = string.Empty;
    private ImageSource? _libraryIcon;
    private CancellationTokenSource? _renderCts;
    private readonly Dictionary<TrackDocument, RenderedTrack> _trackOutputs = [];
    private readonly DispatcherTimer _playheadTimer;

    private string _projectName = string.Empty;
    private double _bpm = 120;
    private int _beatsPerBar = 3;
    private int _beatUnit = 4;
    private TrackRow? _selectedTrack;
    private LibraryEntry? _selectedLibraryItem;
    private SegmentRow? _selectedSegment;
    private PianoRollMode _rollMode = PianoRollMode.Notes;
    private bool _isPencil;
    private int _gridDivisionIndex;
    private int? _selectedNoteIndex;
    private int? _selectedPitchIndex;
    private int _rollRevision = 1;
    private string _previewInfo = "尚未渲染";
    private bool _isBusy;
    private bool _isPlaying;
    private string _statusText = "就绪";
    private string? _lastRenderPath;
    /// <summary>Merged WAV of the last render and the tracks it contains; playback prefers it.</summary>
    private string? _lastMixPath;
    private List<TrackDocument>? _lastMixTracks;
    private double _lastMixDurationSeconds;
    /// <summary>Cached WAV that already contains the BGM, rebuilt when the vocal/BGM/start time changes.</summary>
    private string? _bgmMixPath;
    private string? _bgmMixKey;
    private double? _playheadBeat;
    private bool _isLightTheme;
    private LanguageRow? _selectedLanguage;
    private bool _languageReady;
    private int _chorusRevision = 1;
    private ChorusSingerRow? _selectedChorusSinger;
    private ChorusFormationPreset? _selectedChorusFormation;

    private UserDictionary? _dictionary;
    private string? _dictionaryPath;
    private DictionaryEntryRow? _selectedDictionaryEntry;
    private double _dictionaryStartBeat = 1.0;
    private double _dictionaryBeats = 1.0;
    private string _dictionaryDstPitch = "G4";

    /// <summary>Default beat length of one minimal unit: one quarter note (= one beat in a 4/4 grid).</summary>
    private const double DefaultLyricUnitBeats = 1.0;
    private const double MinLyricUnitBeats = 0.25;
    /// <summary>Pitch of every block generated from the lyric bar.</summary>
    private const string LyricBlockPitch = "D4";

    private string _lyricInput = string.Empty;
    private string _lyricStatus = "输入歌词后按回车或点击别处，按字典分解为最小声库单位";

    public MainViewModel(AppServices services)
    {
        _services = services;
        // Startup no longer loads a demo project; it starts with an empty project. The demo can still be loaded from the "Demo" button in the top bar.
        _project = ProjectFactory.CreateEmpty(services.Options.LibraryRoot);

        // Restore the last used language (Chinese when no record exists) and list the available language packs.
        LanguageService.Initialize(services.Options.RepositoryRoot);
        RebuildLanguages();

        NewProjectCommand = new RelayCommand(_ => NewProject());
        LoadDemoCommand = new RelayCommand(_ => LoadDemo());
        OpenProjectCommand = new RelayCommand(_ => OpenProject());
        SaveProjectCommand = new RelayCommand(_ => SaveProject());
        UndoCommand = new RelayCommand(_ => Undo(), _ => _undoStack.Count > 0);
        RedoCommand = new RelayCommand(_ => Redo(), _ => _redoStack.Count > 0);
        DeleteSelectionCommand = new RelayCommand(_ => DeleteSelection(), _ => CanDeleteSelection);
        RefreshLibraryCommand = new RelayCommand(_ => RefreshLibrary());
        AddLibraryItemCommand = new RelayCommand(p => AddLibraryItem(p as LibraryEntry));
        AddTrackCommand = new RelayCommand(_ => AddTrack());
        RemoveTrackCommand = new RelayCommand(_ => RemoveTrack(), _ => _project.Tracks.Count > 1);
        ToggleRollModeCommand = new RelayCommand(_ => ToggleRollMode());
        ImportDictionaryCommand = new RelayCommand(_ => ImportDictionary());
        ImportBgmCommand = new RelayCommand(_ => ImportBgm());
        ClearBgmCommand = new RelayCommand(_ => ClearBgm(), _ => HasBgm);
        InsertDictionaryEntryCommand = new RelayCommand(
            _ => InsertDictionaryEntry(),
            _ => _dictionary is not null && SelectedDictionaryEntry is not null);
        RenderCommand = new AsyncRelayCommand(_ => RenderAsync(), _ => !IsBusy, ReportError);
        CancelRenderCommand = new RelayCommand(_ => _renderCts?.Cancel(), _ => IsBusy);
        PlayCommand = new RelayCommand(_ => TogglePlay());
        StopCommand = new RelayCommand(_ => StopPlayback());
        ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
        ImportLanguageCommand = new RelayCommand(_ => ImportLanguagePack());
        ExportTimbreCommand = new RelayCommand(_ => ExportTimbre());
        ImportTimbreCommand = new RelayCommand(_ => ImportTimbre());
        OpenHelpCommand = new RelayCommand(_ => OpenHelp());

        AddChorusSingerCommand = new RelayCommand(_ => AddChorusSinger());
        RemoveChorusSingerCommand = new RelayCommand(
            _ => RemoveChorusSinger(), _ => _selectedChorusSinger is not null);
        ImportChorusFormationCommand = new RelayCommand(_ => ImportChorusFormation());
        ApplyChorusFormationPresetCommand = new RelayCommand(
            _ => ApplyChorusFormationPreset(), _ => _selectedChorusFormation is not null);
        JumpToChorusTrackCommand = new RelayCommand(
            _ => JumpToSingerTrack(), _ => SelectedSingerTrack is not null);

        // The playhead position is converted from the player position to beats at 30fps; the piano roll scrolls the track accordingly.
        _playheadTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _playheadTimer.Tick += (_, _) => SyncPlayheadFromPlayback();

        _services.Playback.PlaybackStopped += (_, _) => Dispatch(() =>
        {
            _playheadTimer.Stop();
            IsPlaying = false;
        });

        LoadProject(_project, null);
        LoadDefaultDictionary();
        AddLog("info", $"Repository root: {services.Options.RepositoryRoot}");
        AddLog("info", "Empty project created. Use \"Add to project\" in the library or \"Demo\" in the top bar to start.");
    }

    // ------------------------------------------------------------------
    // Collections and binding properties
    // ------------------------------------------------------------------
    public ObservableCollection<TrackRow> Tracks { get; } = [];
    public ObservableCollection<SegmentRow> Segments { get; } = [];
    public ObservableCollection<LibraryEntry> Library { get; } = [];
    public ObservableCollection<DictionaryEntryRow> DictionaryEntries { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ObservableCollection<VoicebankOption> Voicebanks { get; } = [];
    public ObservableCollection<ChorusSingerRow> ChorusSingers { get; } = [];
    public ObservableCollection<ChorusFormationPreset> ChorusFormations { get; } = [];

    /// <summary>Chorus stage, microphone, singers and imported formations; persisted with the project.</summary>
    public ChorusDocument Chorus => _project.Chorus;

    public bool HasChorusSingers => ChorusSingers.Count > 0;

    public bool HasChorusFormations => ChorusFormations.Count > 0;

    /// <summary>Imported formation selected in the chorus tab; applied with the matching command.</summary>
    public ChorusFormationPreset? SelectedChorusFormation
    {
        get => _selectedChorusFormation;
        set
        {
            if (!SetProperty(ref _selectedChorusFormation, value))
                return;
            (ApplyChorusFormationPresetCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Redraw token for the stage editor; incremented whenever the stage model changes.</summary>
    public int ChorusRevision => _chorusRevision;

    public ChorusSingerRow? SelectedChorusSinger
    {
        get => _selectedChorusSinger;
        set
        {
            if (!SetProperty(ref _selectedChorusSinger, value))
                return;
            OnPropertyChanged(nameof(SelectedChorusSingerModel));
            OnPropertyChanged(nameof(SelectedSingerTrack));
            OnPropertyChanged(nameof(SelectedSingerText));
            (RemoveChorusSingerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (JumpToChorusTrackCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Selected singer model handed to the stage editor for drawing and dragging.</summary>
    public ChorusSinger? SelectedChorusSingerModel => _selectedChorusSinger?.Model;

    /// <summary>Track bound to the selected singer; setting it rebinds that singer's score.</summary>
    public TrackRow? SelectedSingerTrack
    {
        get
        {
            var trackId = _selectedChorusSinger?.Model.TrackId;
            return trackId is null ? null : Tracks.FirstOrDefault(t => t.Model.Id == trackId);
        }
        set
        {
            var singer = _selectedChorusSinger?.Model;
            if (singer is null || value is null || singer.TrackId == value.Model.Id)
                return;

            PushUndo();
            singer.TrackId = value.Model.Id;
            _selectedChorusSinger?.Refresh();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedSingerText));
            TouchChorus();
            AddLog("info", $"Singer \"{singer.Name}\" now uses track \"{value.Name}\".");
        }
    }

    public string SelectedSingerText
    {
        get
        {
            if (_selectedChorusSinger is null)
                return "未选择歌手（单击舞台上的歌手）";

            var track = SelectedSingerTrack;
            return track is null
                ? $"{_selectedChorusSinger.Name} · 未绑定音轨"
                : $"{_selectedChorusSinger.Name} · 音轨：{track.Name}";
        }
    }

    public string ChorusStageInfo =>
        $"舞台 {Chorus.StageWidth:0.#} m × {Chorus.StageDepth:0.#} m · "
        + $"麦克风 ({Chorus.MicX:0.0}, {Chorus.MicY:0.0})";

    /// <summary>Stage width in meters, editable from the chorus panel; only a 1 m minimum is enforced.</summary>
    public double StageWidth
    {
        get => Chorus.StageWidth;
        set
        {
            var width = Math.Max(1, Math.Round(value, 2));
            if (Math.Abs(Chorus.StageWidth - width) < 1e-6)
                return;

            BeginChorusEdit();
            Chorus.StageWidth = width;
            Chorus.MicX = Math.Round(Math.Clamp(Chorus.MicX, -width / 2, width / 2), 2);
            foreach (var keyframe in Chorus.Singers.SelectMany(s => s.Path))
                keyframe.X = Math.Round(ChorusMath.ClampX(keyframe.X, width), 2);
            CommitChorusEdit();
            OnPropertyChanged();
        }
    }

    /// <summary>Stage depth in meters, editable from the chorus panel; only a 1 m minimum is enforced.</summary>
    public double StageDepth
    {
        get => Chorus.StageDepth;
        set
        {
            var depth = Math.Max(1, Math.Round(value, 2));
            if (Math.Abs(Chorus.StageDepth - depth) < 1e-6)
                return;

            BeginChorusEdit();
            Chorus.StageDepth = depth;
            Chorus.MicY = Math.Round(Math.Clamp(Chorus.MicY, -depth / 2, 0), 2);
            foreach (var keyframe in Chorus.Singers.SelectMany(s => s.Path))
                keyframe.Y = Math.Round(ChorusMath.ClampY(keyframe.Y, depth), 2);
            CommitChorusEdit();
            OnPropertyChanged();
        }
    }

    /// <summary>Top-level tab: 0 = track editor, 1 = chorus.</summary>
    private int _mainTabIndex;

    public int MainTabIndex
    {
        get => _mainTabIndex;
        set => SetProperty(ref _mainTabIndex, value);
    }

    /// <summary>声库目录下与音频同级的 icon.jpg，显示在顶栏「新建」旁。</summary>
    public ImageSource? LibraryIcon => _libraryIcon;

    public bool HasLibraryIcon => _libraryIcon is not null;

    /// <summary>当前声库是否有可用音频：没有时整个主界面变灰并锁定，只保留拖入声库的安装入口。</summary>
    public bool HasVoicebank => Library.Count > 0;

    public string ProjectName
    {
        get => _projectName;
        set
        {
            if (SetProperty(ref _projectName, value))
                _project.Name = value;
        }
    }

    public double Bpm
    {
        get => _bpm;
        set
        {
            if (SetProperty(ref _bpm, value))
            {
                _project.Bpm = value;
                // Transition parameters are stored as frames; the derived beat width changes when BPM changes.
                OnPropertyChanged(nameof(SelectedTransitionBeats));
                // The BGM block width is expressed in beats, so it changes with the tempo.
                OnPropertyChanged(nameof(BgmDurationBeats));
            }
        }
    }

    public int BeatsPerBar
    {
        get => _beatsPerBar;
        set
        {
            if (SetProperty(ref _beatsPerBar, value))
                _project.BeatsPerBar = value;
        }
    }

    public int BeatUnit
    {
        get => _beatUnit;
        set
        {
            if (SetProperty(ref _beatUnit, value))
                _project.BeatUnit = value;
        }
    }

    /// <summary>Library path of the current track (independent per track).</summary>
    public string LibraryRoot
    {
        get => _activeTrack.LibraryRoot;
        set
        {
            if (string.Equals(_activeTrack.LibraryRoot, value, StringComparison.Ordinal))
                return;
            _activeTrack.LibraryRoot = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Voicebank picked for the current track: singer names are the folder names under voicebank/.</summary>
    public VoicebankOption? SelectedVoicebank
    {
        get
        {
            var current = _activeTrack.LibraryRoot;
            if (string.IsNullOrWhiteSpace(current) || SamePath(current, VoicebankRoot))
                return Voicebanks.FirstOrDefault();
            return Voicebanks.FirstOrDefault(option => SamePath(option.Path, current));
        }
        set
        {
            if (value is null || SamePath(_activeTrack.LibraryRoot, value.Path))
                return;

            // Notes store absolute wav paths, so a switch alone would keep the old singer in the project.
            var previousRoot = EffectiveLibraryRoot;
            LibraryRoot = value.Path;
            var missing = RetargetSegments(previousRoot, value.Path);

            InvalidateRender();
            RefreshSegments();
            OnPropertyChanged();
            RefreshLibrary();
            StatusText = missing > 0
                ? $"声库：{value.Name}（{missing} 个音符无同名采样）"
                : $"声库：{value.Name}";
        }
    }

    /// <summary>True when the project differs from the last loaded/saved snapshot (prompted on load and on close).</summary>
    public bool HasUnsavedChanges =>
        !string.Equals(_savedSnapshot, ProjectJson.Serialize(_project, _services.Options.RepositoryRoot), StringComparison.Ordinal);

    private string VoicebankRoot => Path.Combine(_services.Options.RepositoryRoot, "voicebank");

    public TrackRow? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (!SetProperty(ref _selectedTrack, value) || value is null)
                return;
            ActivateTrack(value.Model);
        }
    }

    // ---- Independent synthesis parameters of the current track ----

    /// <summary>Formant follow ratios (7 bands, one slider per band).</summary>
    public ObservableCollection<ParameterRow> FollowRatioRows { get; } = [];

    /// <summary>Band energy distribution (formant gains, 8 bands, one slider per band).</summary>
    public ObservableCollection<ParameterRow> BandRatioRows { get; } = [];

    /// <summary>Pitch jitter intensity (0-100).</summary>
    public double Jitter
    {
        get => _activeTrack.Settings.Jitter;
        set
        {
            if (_activeTrack.Settings.Jitter == value)
                return;
            _activeTrack.Settings.Jitter = value;
            OnPropertyChanged();
            InvalidateRender();
        }
    }

    /// <summary>Amplitude shimmer intensity (0-100).</summary>
    public double Shimmer
    {
        get => _activeTrack.Settings.Shimmer;
        set
        {
            if (_activeTrack.Settings.Shimmer == value)
                return;
            _activeTrack.Settings.Shimmer = value;
            OnPropertyChanged();
            InvalidateRender();
        }
    }

    public double BreathyThr
    {
        get => _activeTrack.Settings.BreathyThr;
        set
        {
            if (_activeTrack.Settings.BreathyThr == value)
                return;
            _activeTrack.Settings.BreathyThr = value;
            OnPropertyChanged();
            InvalidateRender();
        }
    }

    public double ApScale
    {
        get => _activeTrack.Settings.ApScale;
        set
        {
            if (_activeTrack.Settings.ApScale == value)
                return;
            _activeTrack.Settings.ApScale = value;
            OnPropertyChanged();
            InvalidateRender();
        }
    }

    public double ApMax
    {
        get => _activeTrack.Settings.ApMax;
        set
        {
            if (_activeTrack.Settings.ApMax == value)
                return;
            _activeTrack.Settings.ApMax = value;
            OnPropertyChanged();
            InvalidateRender();
        }
    }

    public int BitDepth
    {
        get => _activeTrack.Settings.BitDepth;
        set
        {
            if (_activeTrack.Settings.BitDepth == value)
                return;
            _activeTrack.Settings.BitDepth = value;
            OnPropertyChanged();
        }
    }

    private string EffectiveLibraryRoot =>
        string.IsNullOrWhiteSpace(_activeTrack.LibraryRoot)
            ? _services.Options.LibraryRoot
            : _activeTrack.LibraryRoot;

    public string ProjectPathText => LanguageService.Translate(_projectPath ?? "（未保存）");

    public string EngineInfo =>
        $"{Path.GetFileName(_services.Options.PythonPath)} · {Path.GetFileName(_services.Options.BridgeScriptPath)}";

    public LibraryEntry? SelectedLibraryItem
    {
        get => _selectedLibraryItem;
        set => SetProperty(ref _selectedLibraryItem, value);
    }

    public SegmentRow? SelectedSegment
    {
        get => _selectedSegment;
        set
        {
            if (!SetProperty(ref _selectedSegment, value))
                return;

            var index = value is null ? (int?)null : IndexOfSegment(value);
            if (_selectedNoteIndex != index)
            {
                _selectedNoteIndex = index;
                OnPropertyChanged(nameof(SelectedNoteIndex));
            }

            OnPropertyChanged(nameof(HasSelectedSegment));
            OnPropertyChanged(nameof(SelectedTransitionBeats));
        }
    }

    /// <summary>Whether a segment is selected (the transition slider is enabled based on this).</summary>
    public bool HasSelectedSegment => _selectedSegment is not null;

    /// <summary>
    /// Transition length (in beats) between the selected segment and the previous one. The underlying synthesis parameter is a
    /// frame count (frame period 5 ms); here it is converted to beats at the current BPM for comparison on the piano roll.
    /// </summary>
    public double SelectedTransitionBeats
    {
        get => _selectedSegment is null
            ? 0
            : _selectedSegment.Model.Transition * _bpm / FramesPerBeatConstant;
        set
        {
            var row = _selectedSegment;
            if (row is null)
                return;

            var frames = Math.Round(Math.Max(0, value) * FramesPerBeatConstant / Math.Max(1, _bpm));
            if (Math.Abs(row.Model.Transition - frames) < 0.5)
                return;

            PushUndo("transition:" + row.Model.Id);
            row.Model.Transition = frames;
            OnPropertyChanged();
            RollRevision++;
        }
    }

    /// <summary>Index of the selected segment in the piano roll (kept in sync with the segment list).</summary>
    public int? SelectedNoteIndex
    {
        get => _selectedNoteIndex;
        set
        {
            if (!SetProperty(ref _selectedNoteIndex, value))
                return;

            SelectedSegment = value is int index && index >= 0 && index < Segments.Count
                ? Segments[index]
                : null;
            RaiseEditCommandStates();
        }
    }

    /// <summary>Index of the selected point in Pitch line mode.</summary>
    public int? SelectedPitchIndex
    {
        get => _selectedPitchIndex;
        set
        {
            if (!SetProperty(ref _selectedPitchIndex, value))
                return;
            RaiseEditCommandStates();
        }
    }

    /// <summary>Revision number used to trigger piano roll redraws; incremented when the model changes externally.</summary>
    public int RollRevision
    {
        get => _rollRevision;
        private set => SetProperty(ref _rollRevision, value);
    }

    /// <summary>Current track model, read and written directly by the piano roll.</summary>
    public TrackDocument ActiveTrack => _activeTrack;

    public PianoRollMode RollMode
    {
        get => _rollMode;
        set
        {
            if (!SetProperty(ref _rollMode, value))
                return;
            OnPropertyChanged(nameof(RollModeText));
            RaiseEditCommandStates();
        }
    }

    public string RollModeText => LanguageService.Translate(
        RollMode == PianoRollMode.Notes ? "模式：音块（右键加块）" : "模式：Pitch 线（右键加节点）");

    public ICommand OpenHelpCommand { get; }

    /// <summary>Open the bundled README that matches the current UI language (zh-CN / ja / en).</summary>
    private void OpenHelp()
    {
        var root = _services.Options.RepositoryRoot;
        var fileName = LanguageService.Current.Code.ToLowerInvariant() switch
        {
            "ja" => "README.ja.md",
            "zh" => "README.zh-CN.md",
            _ => "README.md",
        };

        var path = Path.Combine(root, fileName);
        if (!File.Exists(path))
            path = Path.Combine(root, "README.md");
        if (!File.Exists(path))
        {
            AddLog("warn", $"Help file not found: {fileName}");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            AddLog("info", $"Opened help: {path}");
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    /// <summary>Pencil tool: left-drag inside the piano roll draws a new note from press to release.</summary>
    public bool IsPencil
    {
        get => _isPencil;
        set => SetProperty(ref _isPencil, value);
    }

    /// <summary>Index of the piano-roll minor-grid choice: 0 = off, 1 = eighth notes, 2 = sixteenth notes.</summary>
    public int GridDivisionIndex
    {
        get => _gridDivisionIndex;
        set
        {
            if (!SetProperty(ref _gridDivisionIndex, value))
                return;
            OnPropertyChanged(nameof(GridDivision));
        }
    }

    /// <summary>Minor grid lines per beat passed to the piano roll: 0 = off, 2 = eighth, 4 = sixteenth.</summary>
    public int GridDivision => _gridDivisionIndex switch
    {
        1 => 2,
        2 => 4,
        _ => 0,
    };

    // ------------------------------------------------------------------
    // User dictionary
    // ------------------------------------------------------------------
    public string DictionaryName => _dictionary?.Name ?? LanguageService.Translate("（未导入）");

    public string DictionaryPathText => _dictionaryPath is null
        ? LanguageService.Translate("内置默认字典：假名 + 常见汉字（可导入 .json 覆盖）")
        : _dictionaryPath;

    public DictionaryEntryRow? SelectedDictionaryEntry
    {
        get => _selectedDictionaryEntry;
        set
        {
            if (!SetProperty(ref _selectedDictionaryEntry, value))
                return;

            if (value?.Model.DefaultBeats is double beats && beats > 0)
                DictionaryBeats = beats;
            if (!string.IsNullOrWhiteSpace(value?.Model.DstPitch))
                DictionaryDstPitch = value.Model.DstPitch!;

            (InsertDictionaryEntryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Start beat for inserting a dictionary entry (1-based).</summary>
    public double DictionaryStartBeat
    {
        get => _dictionaryStartBeat;
        set => SetProperty(ref _dictionaryStartBeat, value);
    }

    /// <summary>Total beat length occupied by an inserted dictionary entry; values of 0 or less use the entry default.</summary>
    public double DictionaryBeats
    {
        get => _dictionaryBeats;
        set => SetProperty(ref _dictionaryBeats, value);
    }

    /// <summary>Target pitch string for inserting a dictionary entry.</summary>
    public string DictionaryDstPitch
    {
        get => _dictionaryDstPitch;
        set => SetProperty(ref _dictionaryDstPitch, value);
    }

    // ------------------------------------------------------------------
    // Lyric bar (top of the editor): type text, commit -> minimal voicebank units + D4 blocks
    // ------------------------------------------------------------------

    /// <summary>Raw lyric text typed in the top bar.</summary>
    public string LyricInput
    {
        get => _lyricInput;
        set => SetProperty(ref _lyricInput, value);
    }

    /// <summary>One-line feedback under the lyric bar (decomposed unit count, skipped samples, etc.).</summary>
    public string LyricStatus
    {
        get => LanguageService.Translate(_lyricStatus);
        private set => SetProperty(ref _lyricStatus, value);
    }

    /// <summary>Minimal units of the last commit; each item is one generated D4 block.</summary>
    public ObservableCollection<LyricUnitRow> LyricUnits { get; } = [];

    public bool HasLyricUnits => LyricUnits.Count > 0;

    /// <summary>Rest regions (deleted lyric units) drawn as dashed blocks in the piano roll.</summary>
    public IReadOnlyList<RestRegion> RollRestRegions { get; private set; } = [];

    /// <summary>Result of a lyric deletion key press: the new text and where the caret should sit.</summary>
    public sealed record LyricTextEdit(string Text, int Caret);

    /// <summary>
    /// Called when the lyric input loses focus or Enter is pressed: parses the whole text into word/unit
    /// objects and applies an object-level diff onto the track's persisted <see cref="LyricPhrase"/>.
    /// Matched words keep their melody; dropped words lose their segments; new words are appended.
    /// The text box is then rewritten to the phrase projection (missing audio / unmatched characters
    /// become rests and therefore appear as spaces).
    /// </summary>
    public void CommitLyricInput()
    {
        if (_dictionary is null)
        {
            AddLog("warn", "当前没有可用字典，无法分解歌词。");
            return;
        }

        var text = _lyricInput ?? string.Empty;
        var phrase = _activeTrack.LyricPhrase;

        // The box already shows the projection: nothing to diff.
        if (string.Equals(text, phrase.Text, StringComparison.Ordinal))
            return;

        var dictionaryDirectory = string.IsNullOrWhiteSpace(_dictionaryPath)
            ? null
            : Path.GetDirectoryName(_dictionaryPath);
        var parsed = LyricPhraseParser.Parse(
            _dictionary, text, dictionaryDirectory, ResolveDictionaryLibraryRoot(), _dictionary.DefaultSrcPitch);

        PushUndo("lyric:" + text);
        var result = LyricPhraseComposer.Apply(
            phrase, parsed.Words, _activeTrack,
            DefaultLyricUnitBeats, DefaultTransitionFrames, LyricBlockPitch);

        // Full refresh: kept units follow their segments, new units get rows, the box shows the projection.
        RefreshSegments();

        var units = phrase.AllUnits;
        var realCount = units.Count(u => !u.IsRest);
        var restCount = units.Count - realCount;

        if (units.Count == 0)
        {
            LyricStatus = parsed.MissingAudio.Count > 0
                ? $"声库中找不到音频：{string.Join("、", parsed.MissingAudio.Distinct().Take(6))}"
                : "歌词与休止已全部删除";
            AddLog("info", "Lyric bar cleared: all generated blocks and rests removed.");
            return;
        }

        var message = $"已分解为 {units.Count} 个最小单位：{realCount} 个音块";
        if (result.Reused > 0 || result.Removed > 0)
        {
            message += $"（复用原旋律 {result.Reused} 个词";
            if (result.Added > 0)
                message += $"，新增 {result.Added} 个词";
            if (result.Removed > 0)
                message += $"，移除 {result.Removed} 个词";
            message += "）";
        }

        if (restCount > 0)
            message += $"，{restCount} 个休止";
        if (parsed.Unmatched.Count > 0)
            message += $"（字典未匹配，已保存为休止：{string.Join("、", parsed.Unmatched.Distinct().Take(6))}）";
        if (parsed.MissingAudio.Count > 0)
            message += $"（音频缺失，已保存为休止：{string.Join("、", parsed.MissingAudio.Distinct().Take(6))}）";

        LyricStatus = message;
        AddLog("info", $"Lyric \"{text}\" -> {units.Count} unit(s): "
            + string.Join(" + ", units.Select(u => u.Display)) + $", {_activeTrack.Name}");
        foreach (var character in parsed.Unmatched.Distinct())
            AddLog("warn", $"Lyric character \"{character}\" has no dictionary entry; saved as a rest.");
    }

    /// <summary>
    /// Backspace/Delete on committed lyrics, expressed on the object graph: deleting a word mutes all of
    /// its pronunciation units (each becomes a rest of the same beat span); deleting a rest word removes
    /// it and pulls the following words left. Returns null when the text box no longer matches the
    /// projection (uncommitted typing), so the box keeps its default editing behavior.
    /// </summary>
    public LyricTextEdit? TryBuildLyricDeletion(string text, int caret, int selectionLength, bool backspace)
    {
        var phrase = _activeTrack.LyricPhrase;
        if (phrase.Words.Count == 0)
            return null;

        text ??= string.Empty;
        var projection = phrase.Text;
        if (!string.Equals(text, projection, StringComparison.Ordinal))
            return null;

        var spans = WordTextSpans(phrase);
        int start;
        int end;
        if (selectionLength > 0)
        {
            start = caret;
            end = caret + selectionLength;
        }
        else if (backspace)
        {
            if (caret <= 0)
                return null;
            start = caret - 1;
            end = caret;
        }
        else
        {
            if (caret >= text.Length)
                return null;
            start = caret;
            end = caret + 1;
        }

        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        if (end <= start)
            return null;

        var affected = new List<int>();
        for (var i = 0; i < spans.Count; i++)
        {
            if (spans[i].Start < end && spans[i].End > start)
                affected.Add(i);
        }

        if (affected.Count == 0)
            return null;

        PushUndo();

        if (affected.Count == 1)
        {
            var index = affected[0];
            var word = phrase.Words[index];
            if (word.IsRest)
            {
                LyricPhraseComposer.RemoveWord(phrase, word, _activeTrack, pullFollowing: true);
                caret = WordOffset(phrase, index);
            }
            else
            {
                var endLength = word.Units.Count;
                LyricPhraseComposer.MuteWord(phrase, word, _activeTrack);
                caret = WordOffset(phrase, index) + endLength;
            }
        }
        else
        {
            var firstIndex = affected[0];
            for (var i = affected.Count - 1; i >= 0; i--)
            {
                var word = phrase.Words[affected[i]];
                if (word.IsRest)
                    LyricPhraseComposer.RemoveWord(phrase, word, _activeTrack, pullFollowing: false);
                else
                    LyricPhraseComposer.MuteWord(phrase, word, _activeTrack);
            }

            caret = WordOffset(phrase, firstIndex);
        }

        RefreshSegments();
        if (phrase.Words.Count == 0)
            LyricStatus = "歌词与休止已全部删除";
        else
            LyricStatus = "已删除歌词：对应音块已变为同拍长休止";

        return new LyricTextEdit(phrase.Text, Math.Clamp(caret, 0, phrase.Text.Length));
    }

    /// <summary>Character spans of each word in the projected text.</summary>
    private static List<(int Start, int End)> WordTextSpans(LyricPhrase phrase)
    {
        var spans = new List<(int, int)>(phrase.Words.Count);
        var offset = 0;
        foreach (var word in phrase.Words)
        {
            var length = word.Projection.Length;
            spans.Add((offset, offset + length));
            offset += length;
        }

        return spans;
    }

    /// <summary>Character offset where the given word starts in the current projection.</summary>
    private static int WordOffset(LyricPhrase phrase, int wordIndex)
    {
        var offset = 0;
        for (var i = 0; i < wordIndex && i < phrase.Words.Count; i++)
            offset += phrase.Words[i].Projection.Length;
        return offset;
    }

    /// <summary>
    /// Drag on the vertical line at the right edge of one unit: changes that unit's beat length and
    /// shifts the following units so the strip stays contiguous. Works for rests as well.
    /// </summary>
    public void ResizeLyricUnit(LyricUnitRow? row, double deltaBeats)
    {
        if (row is null)
            return;

        var flat = _activeTrack.LyricPhrase.AllUnits;
        var index = flat.IndexOf(row.Model);
        if (index < 0)
            return;

        var unit = row.Model;
        var newLength = Math.Max(MinLyricUnitBeats, unit.Length + deltaBeats);
        if (Math.Abs(newLength - unit.Length) < 0.001)
            return;

        PushUndo("lyric-length:" + unit.Id);

        var delta = newLength - unit.Length;
        RelayoutUnit(unit, unit.BeatStart, newLength);
        foreach (var following in flat.Skip(index + 1))
            RelayoutUnit(following, following.BeatStart + delta, following.Length);

        foreach (var item in LyricUnits)
            item.RefreshMetrics();

        RefreshRollRestRegions();
        RollRevision++;
    }

    /// <summary>Moves/resizes a unit object and keeps its linked segment and pitch nodes in sync.</summary>
    private void RelayoutUnit(LyricUnit unit, double beatStart, double length)
    {
        var segment = FindSegment(unit.SegmentId);
        if (segment is not null)
        {
            var oldStart = segment.BeatStart;
            var oldEnd = segment.BeatEnd;
            var newEnd = beatStart + length;
            if (Math.Abs(oldStart - beatStart) > 1e-9 || Math.Abs(oldEnd - newEnd) > 1e-9)
            {
                ScaleSegmentPoints(segment, oldStart, oldEnd, beatStart, newEnd);
                segment.BeatStart = beatStart;
                segment.BeatEnd = newEnd;
            }
        }

        unit.BeatStart = beatStart;
        unit.Length = Math.Max(0.01, length);
    }

    private VoiceSegment? FindSegment(string? segmentId) => segmentId is null
        ? null
        : _activeTrack.Segments.FirstOrDefault(s => s.Id == segmentId);

    /// <summary>Rebuilds the unit strip from the track's persisted lyric objects and rewrites the text projection.</summary>
    private void RebuildLyricRows()
    {
        LyricUnits.Clear();
        foreach (var word in _activeTrack.LyricPhrase.Words)
        foreach (var unit in word.Units)
            LyricUnits.Add(new LyricUnitRow(unit));

        var projection = _activeTrack.LyricPhrase.Text;
        if (!string.Equals(_lyricInput, projection, StringComparison.Ordinal))
        {
            _lyricInput = projection;
            OnPropertyChanged(nameof(LyricInput));
        }

        OnPropertyChanged(nameof(HasLyricUnits));
        RefreshRollRestRegions();
    }

    /// <summary>Shows the active track's persisted lyric phrase (track switch / project load / undo).</summary>
    private void LoadLyricFromTrack()
    {
        RebuildLyricRows();
        LyricStatus = "输入歌词后按回车或点击别处，按字典分解为最小声库单位";
    }

    /// <summary>Keeps the piano-roll rest markers in sync with the unit strip.</summary>
    private void RefreshRollRestRegions()
    {
        var semitone = PitchMath.TryParseSemitones(LyricBlockPitch, out var value)
            ? (int)Math.Round(value)
            : 0;
        RollRestRegions = _activeTrack.LyricPhrase.AllUnits
            .Where(u => u.IsRest)
            .Select(u => new RestRegion(u.BeatStart, u.BeatEnd, semitone))
            .ToList();
        OnPropertyChanged(nameof(RollRestRegions));
    }

    /// <summary>Maps a segment's pitch nodes from one beat range onto another (first/last stay at the ends).</summary>
    private void ScaleSegmentPoints(
        VoiceSegment segment, double oldStart, double oldEnd, double newStart, double newEnd)
    {
        var oldSpan = Math.Max(0.001, oldEnd - oldStart);
        var newSpan = Math.Max(0.001, newEnd - newStart);
        foreach (var point in _activeTrack.PitchLine)
        {
            if (point.SegmentId != segment.Id || point.IsTransition)
                continue;
            var t = Math.Clamp((point.Beat - oldStart) / oldSpan, 0, 1);
            point.Beat = newStart + t * newSpan;
        }

        _activeTrack.SortPitchLine();
    }

    /// <summary>Adds a lyric word object for a segment created outside the lyric bar.</summary>
    private void AddLyricWordForSegment(VoiceSegment segment)
    {
        var surface = FindPlaceholderLyric(segment.Audio) ?? segment.Lyric;
        segment.LyricOverride ??= surface;

        var word = new LyricWord
        {
            Surface = surface,
            Units =
            [
                new LyricUnit
                {
                    Text = surface,
                    Audio = segment.Audio,
                    Display = LyricPhrase.SampleLabel(segment.Audio),
                    SrcPitch = segment.SrcPitch,
                    KeepUnvoicedHead = segment.KeepUnvoicedHead,
                    IsRest = false,
                    SegmentId = segment.Id,
                    BeatStart = segment.BeatStart,
                    Length = Math.Max(0.01, segment.BeatEnd - segment.BeatStart),
                },
            ],
        };
        InsertLyricWord(word, segment.BeatStart);
    }

    /// <summary>Adds one word object covering the segments of a dictionary entry inserted from the panel.</summary>
    private void AddLyricWordForSegments(string surface, IReadOnlyList<VoiceSegment> segments)
    {
        if (segments.Count == 0)
            return;

        var word = new LyricWord { Surface = surface };
        foreach (var segment in segments)
        {
            segment.LyricOverride ??= surface;
            word.Units.Add(new LyricUnit
            {
                Text = surface,
                Audio = segment.Audio,
                Display = LyricPhrase.SampleLabel(segment.Audio),
                SrcPitch = segment.SrcPitch,
                KeepUnvoicedHead = segment.KeepUnvoicedHead,
                IsRest = false,
                SegmentId = segment.Id,
                BeatStart = segment.BeatStart,
                Length = Math.Max(0.01, segment.BeatEnd - segment.BeatStart),
            });
        }

        InsertLyricWord(word, segments[0].BeatStart);
    }

    /// <summary>Inserts a word at its time position so the phrase order matches the timeline.</summary>
    private void InsertLyricWord(LyricWord word, double startBeat)
    {
        var words = _activeTrack.LyricPhrase.Words;
        var index = words.FindIndex(w => w.BeatStart > startBeat + 1e-9);
        if (index < 0)
            words.Add(word);
        else
            words.Insert(index, word);
    }

    public string PreviewInfo
    {
        get => LanguageService.Translate(_previewInfo);
        private set => SetProperty(ref _previewInfo, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;
            (RenderCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CancelRenderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    public string StatusText
    {
        get => LanguageService.Translate(_statusText);
        private set => SetProperty(ref _statusText, value);
    }

    public string? LastRenderPath
    {
        get => _lastRenderPath;
        private set => SetProperty(ref _lastRenderPath, value);
    }

    /// <summary>Playhead position (1-based beat); null = playhead hidden.</summary>
    public double? PlayheadBeat
    {
        get => _playheadBeat;
        set => SetProperty(ref _playheadBeat, value);
    }

    // ------------------------------------------------------------------
    // BGM (backing track): project-wide, original speed, adjustable start
    // ------------------------------------------------------------------

    /// <summary>Project-wide BGM WAV, shared by every track; null = no BGM.</summary>
    public string? BgmPath => _project.BgmPath;

    public bool HasBgm => !string.IsNullOrWhiteSpace(_project.BgmPath);

    /// <summary>File name shown in the BGM bar below the piano roll.</summary>
    public string BgmName => string.IsNullOrWhiteSpace(_project.BgmPath)
        ? "未导入伴奏"
        : Path.GetFileName(_project.BgmPath);

    /// <summary>
    /// BGM start offset in beats from the project start (0 = 第 0 小节第 0 音, the default).
    /// The BGM always plays at its original speed, so this only moves it in time.
    /// </summary>
    public double BgmStartBeat
    {
        get => _project.BgmStartBeat;
        set
        {
            var clamped = Math.Max(0, value);
            if (Math.Abs(_project.BgmStartBeat - clamped) < 1e-9)
                return;
            _project.BgmStartBeat = clamped;
            _bgmMixKey = null;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// BGM length in beats at the current tempo. The BGM block in the piano roll uses it as a fixed
    /// width, so the block can only be moved, never resized.
    /// </summary>
    public double BgmDurationBeats
    {
        get
        {
            if (!HasBgm)
                return 0;
            try
            {
                return WavReader.ReadInfo(_project.BgmPath!).DurationSeconds * _bpm / 60.0;
            }
            catch
            {
                return 0;
            }
        }
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------
    public ICommand NewProjectCommand { get; }
    public ICommand LoadDemoCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand SaveProjectCommand { get; }
    public ICommand ExportTimbreCommand { get; }
    public ICommand ImportTimbreCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand DeleteSelectionCommand { get; }
    public ICommand RefreshLibraryCommand { get; }
    public ICommand AddLibraryItemCommand { get; }
    public ICommand AddTrackCommand { get; }
    public ICommand RemoveTrackCommand { get; }
    public ICommand ToggleRollModeCommand { get; }
    public ICommand ImportDictionaryCommand { get; }
    public ICommand ImportBgmCommand { get; }
    public ICommand ClearBgmCommand { get; }
    public ICommand InsertDictionaryEntryCommand { get; }
    public ICommand RenderCommand { get; }
    public ICommand CancelRenderCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ImportLanguageCommand { get; }
    public ICommand AddChorusSingerCommand { get; }
    public ICommand RemoveChorusSingerCommand { get; }
    public ICommand ImportChorusFormationCommand { get; }
    public ICommand ApplyChorusFormationPresetCommand { get; }
    public ICommand JumpToChorusTrackCommand { get; }

    /// <summary>Theme button text: shows the mode that will be applied when clicked.</summary>
    public string ThemeToggleText => LanguageService.Translate(_isLightTheme ? "深色模式" : "浅色模式");

    private void ToggleTheme()
    {
        _isLightTheme = !_isLightTheme;
        ThemeManager.Apply(_isLightTheme);
        OnPropertyChanged(nameof(ThemeToggleText));
    }

    // ------------------------------------------------------------------
    // UI language
    // ------------------------------------------------------------------
    public ObservableCollection<LanguageRow> Languages { get; } = [];

    /// <summary>Language selected in the drop-down; changes are applied to the current window immediately.</summary>
    public LanguageRow? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            // The first item is selected while the drop-down initializes and the list is rebuilt; that must not be treated as a user switch.
            if (!_languageReady || value is null)
                return;
            if (!SetProperty(ref _selectedLanguage, value))
                return;

            LanguageService.Select(value.Pack.Code);
            RefreshLanguageText();
        }
    }

    /// <summary>Called after the window loads: align the drop-down with the restored language, then allow user switching.</summary>
    public void BeginLanguageSelection()
    {
        var row = Languages.FirstOrDefault(r => r.Pack.Code == LanguageService.Current.Code)
            ?? Languages.FirstOrDefault();
        if (row is not null && !ReferenceEquals(_selectedLanguage, row))
        {
            _selectedLanguage = row;
            OnPropertyChanged(nameof(SelectedLanguage));
        }

        RefreshLanguageText();
        _languageReady = true;
    }

    private void RebuildLanguages()
    {
        var ready = _languageReady;
        _languageReady = false;

        Languages.Clear();
        foreach (var pack in LanguageService.Available)
            Languages.Add(new LanguageRow(pack));

        _selectedLanguage = Languages.FirstOrDefault(r => r.Pack.Code == LanguageService.Current.Code)
            ?? Languages.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedLanguage));

        _languageReady = ready;
    }

    private void ImportLanguagePack()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入语言包",
            Filter = "JSON 语言包 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;

        var pack = LanguageService.Import(dialog.FileName, out var error);
        if (pack is null)
        {
            AddLog("error", "Language pack import failed: " + error);
            return;
        }

        RebuildLanguages();
        RefreshLanguageText();
        AddLog("info", $"Language pack imported: {pack.Name} ({pack.Code})");
    }

    /// <summary>Refresh all texts generated for the current language after a language switch.</summary>
    public void RefreshLanguageText()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PreviewInfo));
        OnPropertyChanged(nameof(RollModeText));
        OnPropertyChanged(nameof(ThemeToggleText));
        OnPropertyChanged(nameof(ProjectPathText));
        OnPropertyChanged(nameof(DictionaryName));
        OnPropertyChanged(nameof(DictionaryPathText));
        OnPropertyChanged(nameof(LyricStatus));
        RefreshParameterRows();
    }

    // ------------------------------------------------------------------
    // Project
    // ------------------------------------------------------------------
    private void NewProject()
    {
        LoadProject(ProjectFactory.CreateEmpty(_services.Options.LibraryRoot), null);
        AddLog("info", "New empty project created.");
    }

    private void LoadDemo()
    {
        var demo = ProjectFactory.CreateDemo(_services.Options.LibraryRoot);
        if (demo.Tracks.Count == 0 || demo.Tracks[0].Segments.Count == 0)
        {
            AddLog("warn", "Demo audio not found in the library; cannot load the demo project.");
            StatusText = "示例音频缺失";
            return;
        }
        LoadProject(demo, null);
        AddLog("info", "Demo project loaded (G4 -> G4 -> E4).");
    }

    /// <summary>Export the core timbre parameters of the active track to a JSON file.</summary>
    private void ExportTimbre()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出音色参数",
            Filter = $"音色参数 (*{TimbreProfile.FileExtension})|*{TimbreProfile.FileExtension}|JSON (*.json)|*.json",
            FileName = SafeFileName(_activeTrack.Name) + TimbreProfile.FileExtension,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            TimbreProfile.From(_activeTrack.Settings).Save(dialog.FileName);
            AddLog("info", "Timbre profile exported: " + dialog.FileName);
            StatusText = "音色参数已导出：" + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex)
        {
            AddLog("error", "Export timbre profile failed: " + ex.Message);
            StatusText = "导出失败";
            MessageBox.Show("无法导出音色参数：" + ex.Message, "导出音色参数",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Read a timbre profile and apply it to the active track.</summary>
    private void ImportTimbre()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入音色参数",
            Filter = $"音色参数 (*{TimbreProfile.FileExtension})|*{TimbreProfile.FileExtension}|JSON (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;

        TimbreProfile profile;
        try
        {
            profile = TimbreProfile.Load(dialog.FileName);
        }
        catch (Exception ex)
        {
            AddLog("error", "Import timbre profile failed: " + ex.Message);
            StatusText = "导入失败";
            MessageBox.Show("无法读取音色参数：" + ex.Message, "导入音色参数",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PushUndo();
        foreach (var item in profile.ApplyTo(_activeTrack.Settings))
            AddLog("warn", "Timbre import skipped " + item);

        // The sliders read straight from the track settings, so refresh both the scalar bindings and the band rows.
        OnPropertyChanged(nameof(BreathyThr));
        OnPropertyChanged(nameof(ApScale));
        OnPropertyChanged(nameof(ApMax));
        OnPropertyChanged(nameof(Jitter));
        OnPropertyChanged(nameof(Shimmer));
        RefreshParameterRows();

        AddLog("info", "Timbre profile imported: " + dialog.FileName);
        StatusText = "音色参数已导入：" + Path.GetFileName(dialog.FileName);
    }

    /// <summary>File name of the active track without characters the file system rejects.</summary>
    private static string SafeFileName(string? name)
    {
        var text = string.IsNullOrWhiteSpace(name) ? "音色参数" : name.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars())
            text = text.Replace(ch, '_');
        return text;
    }

    private void OpenProject()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开工程",
            Filter = $"VocalB1 工程 (*{ProjectJson.FileExtension})|*{ProjectJson.FileExtension}|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;

        OpenProjectFromPath(dialog.FileName);
    }

    /// <summary>Load a project file dropped onto the window: prompt for unsaved changes, then replace the workspace.</summary>
    public void OpenProjectFromPath(string path)
    {
        // Ask before touching the current project: a canceled open must not read (or replace) anything.
        if (!ConfirmDiscardChanges("载入工程"))
            return;

        ProjectDocument project;
        try
        {
            project = ProjectJson.Load(path, _services.Options.RepositoryRoot);
        }
        catch (Exception ex)
        {
            AddLog("error", "Failed to open project: " + ex.Message);
            StatusText = "打开失败";
            MessageBox.Show("无法打开工程文件：" + ex.Message, "载入工程", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LoadProject(project, path);
        AddLog("info", $"Project opened: {path}");
        StatusText = "已载入：" + Path.GetFileName(path);
    }

    /// <summary>Ask whether to save unsaved changes; returns false when the user cancels the action.</summary>
    public bool ConfirmDiscardChanges(string action)
    {
        if (!HasUnsavedChanges)
            return true;

        return MessageBox.Show(
            "当前工程有未保存的更改，是否保存？",
            action,
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => SaveProject(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private bool SaveProject()
    {
        if (string.IsNullOrWhiteSpace(_projectPath))
            return SaveProjectAs();

        return SaveTo(_projectPath);
    }

    private bool SaveProjectAs()
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存工程",
            Filter = $"VocalB1 工程 (*{ProjectJson.FileExtension})|*{ProjectJson.FileExtension}",
            FileName = string.IsNullOrWhiteSpace(ProjectName) ? "untitled" + ProjectJson.FileExtension : ProjectName + ProjectJson.FileExtension,
        };
        if (dialog.ShowDialog() != true)
            return false;

        return SaveTo(dialog.FileName);
    }

    private bool SaveTo(string path)
    {
        try
        {
            ProjectJson.Save(_project, path, _services.Options.RepositoryRoot);
            _projectPath = path;
            _savedSnapshot = ProjectJson.Serialize(_project, _services.Options.RepositoryRoot);
            OnPropertyChanged(nameof(ProjectPathText));
            StatusText = "已保存：" + Path.GetFileName(path);
            AddLog("info", "Project saved: " + path);
            return true;
        }
        catch (Exception ex)
        {
            AddLog("error", "Save failed: " + ex.Message);
            StatusText = "保存失败";
            return false;
        }
    }

    private void LoadProject(ProjectDocument project, string? path)
    {
        _project = project;
        _projectPath = path;

        // The previous edit history no longer applies after switching projects.
        _undoStack.Clear();
        _redoStack.Clear();
        _undoCoalesceKey = null;
        RaiseEditCommandStates();

        _projectName = project.Name;
        _bpm = project.Bpm;
        _beatsPerBar = project.BeatsPerBar;
        _beatUnit = project.BeatUnit;

        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(Bpm));
        OnPropertyChanged(nameof(BeatsPerBar));
        OnPropertyChanged(nameof(BeatUnit));
        OnPropertyChanged(nameof(ProjectPathText));

        Tracks.Clear();
        foreach (var track in project.Tracks)
            Tracks.Add(new TrackRow(track));

        _trackOutputs.Clear();
        ClearMergedRender();
        _selectedTrack = Tracks[0];
        OnPropertyChanged(nameof(SelectedTrack));

        _selectedChorusSinger = null;
        RebuildChorusRows();
        RebuildChorusFormations();
        _chorusRevision++;
        OnPropertyChanged(nameof(ChorusRevision));
        OnPropertyChanged(nameof(Chorus));
        OnPropertyChanged(nameof(ChorusStageInfo));
        OnPropertyChanged(nameof(StageWidth));
        OnPropertyChanged(nameof(StageDepth));

        _bgmMixKey = null;
        RaiseBgmChanged();
        ActivateTrack(_project.Tracks[0]);
        _savedSnapshot = ProjectJson.Serialize(_project, _services.Options.RepositoryRoot);
        (RemoveTrackCommand as RelayCommand)?.RaiseCanExecuteChanged();
        StatusText = "就绪";
    }

    private void RefreshSegments()
    {
        var selectedIndex = _selectedNoteIndex;

        // Keep the lyric object graph in sync with segments edited outside the lyric bar
        // (piano roll moves/resizes, segment deletion, undo, voicebank retargeting).
        LyricPhraseComposer.SyncWithSegments(_activeTrack.LyricPhrase, _activeTrack);

        // Rebuild transition points after overlap changes so that regular control points never fall inside overlapping segments.
        SegmentLayout.SyncTransitionPoints(_activeTrack);

        Segments.Clear();
        for (var i = 0; i < _activeTrack.Segments.Count; i++)
            Segments.Add(new SegmentRow(_activeTrack.Segments[i], i));

        RollRevision++;
        RebuildLyricRows();

        _selectedSegment = selectedIndex is int index && index >= 0 && index < Segments.Count
            ? Segments[index]
            : null;
        OnPropertyChanged(nameof(SelectedSegment));
        OnPropertyChanged(nameof(HasSelectedSegment));
        // After dragging a transition handle in the piano roll, the slider on the right must show the new transition length.
        OnPropertyChanged(nameof(SelectedTransitionBeats));
    }

    private int? IndexOfSegment(SegmentRow row)
    {
        var index = Segments.IndexOf(row);
        return index >= 0 ? index : null;
    }

    private void ToggleRollMode()
    {
        RollMode = RollMode == PianoRollMode.Notes ? PianoRollMode.PitchLine : PianoRollMode.Notes;
        AddLog("info", RollMode == PianoRollMode.Notes
            ? "Switched to note mode: left click selects/drags notes, right click adds a note."
            : "Switched to pitch-line mode: left click selects/drags points, right click adds a point.");
    }

    /// <summary>Refresh the segment list after piano roll editing (affected labels and indices).</summary>
    public void RefreshActiveTrack() => RefreshSegments();

    // ------------------------------------------------------------------
    // Undo / Redo / Delete
    // ------------------------------------------------------------------

    /// <summary>Called before a real modification starts in the piano roll; records an undo snapshot.</summary>
    public void BeginEdit() => PushUndo();

    /// <summary>
    /// Record a snapshot of the current project; consecutive operations sharing the same <paramref name="coalesceKey"/>
    /// are recorded only once (for example, typing a lyric character by character is coalesced into a single undo step).
    /// </summary>
    private void PushUndo(string? coalesceKey = null)
    {
        if (coalesceKey is not null && coalesceKey == _undoCoalesceKey)
            return;

        _undoCoalesceKey = coalesceKey;
        _undoStack.Add(_project.Clone());
        if (_undoStack.Count > MaxUndoSteps)
            _undoStack.RemoveAt(0);
        _redoStack.Clear();
        RaiseEditCommandStates();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
            return;

        _undoCoalesceKey = null;
        _redoStack.Add(_project.Clone());
        var snapshot = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RestoreProject(snapshot);
        AddLog("info", "Undid the last edit.");
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
            return;

        _undoCoalesceKey = null;
        _undoStack.Add(_project.Clone());
        var snapshot = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        RestoreProject(snapshot);
        AddLog("info", "Redid the last edit.");
    }

    /// <summary>Replace the current project with a snapshot, preserving the current track and view state where possible.</summary>
    private void RestoreProject(ProjectDocument project)
    {
        var trackIndex = _selectedTrack is null ? 0 : Tracks.IndexOf(_selectedTrack);

        _project = project;
        _projectName = project.Name;
        _bpm = project.Bpm;
        _beatsPerBar = project.BeatsPerBar;
        _beatUnit = project.BeatUnit;

        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(Bpm));
        OnPropertyChanged(nameof(BeatsPerBar));
        OnPropertyChanged(nameof(BeatUnit));

        Tracks.Clear();
        foreach (var track in project.Tracks)
            Tracks.Add(new TrackRow(track));

        _trackOutputs.Clear();
        ClearMergedRender();
        _bgmMixKey = null;
        RaiseBgmChanged();
        trackIndex = Math.Clamp(trackIndex, 0, Math.Max(0, Tracks.Count - 1));
        _selectedTrack = Tracks.Count > 0 ? Tracks[trackIndex] : null;
        OnPropertyChanged(nameof(SelectedTrack));

        RebuildChorusRows();
        RebuildChorusFormations();
        _chorusRevision++;
        OnPropertyChanged(nameof(ChorusRevision));
        OnPropertyChanged(nameof(Chorus));
        OnPropertyChanged(nameof(ChorusStageInfo));
        OnPropertyChanged(nameof(StageWidth));
        OnPropertyChanged(nameof(StageDepth));

        if (_project.Tracks.Count > 0)
            ActivateTrack(_project.Tracks[Math.Clamp(trackIndex, 0, _project.Tracks.Count - 1)]);

        (RemoveTrackCommand as RelayCommand)?.RaiseCanExecuteChanged();
        RaiseEditCommandStates();
        StatusText = "就绪";
    }

    private void RaiseEditCommandStates()
    {
        (UndoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RedoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteSelectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>Delete the selection: Pitch line mode removes a point, note mode removes a segment (together with its points).</summary>
    private bool CanDeleteSelection =>
        RollMode == PianoRollMode.PitchLine
        && SelectedPitchIndex is int pointIndex
        && pointIndex >= 0 && pointIndex < _activeTrack.PitchLine.Count
        || SelectedNoteIndex is int noteIndex
        && noteIndex >= 0 && noteIndex < _activeTrack.Segments.Count;

    private void DeleteSelection()
    {
        if (RollMode == PianoRollMode.PitchLine
            && SelectedPitchIndex is int pointIndex
            && pointIndex >= 0 && pointIndex < _activeTrack.PitchLine.Count)
        {
            PushUndo();
            _activeTrack.PitchLine.RemoveAt(pointIndex);
            SelectedPitchIndex = null;
            RollRevision++;
            AddLog("info", "Pitch point deleted.");
            return;
        }

        if (SelectedNoteIndex is not int index
            || index < 0 || index >= _activeTrack.Segments.Count)
            return;

        PushUndo();
        var segment = _activeTrack.Segments[index];
        _activeTrack.Segments.RemoveAt(index);
        _activeTrack.PitchLine.RemoveAll(p => p.SegmentId == segment.Id);
        SegmentLayout.SyncTransitionPoints(_activeTrack);
        SelectedNoteIndex = null;
        RefreshSegments();
        AddLog("info", $"Segment deleted: {segment.Lyric}");
    }

    /// <summary>Right click in note mode: create a segment at the currently selected library audio.</summary>
    public void AddNoteAt(double beat, int semitone)
    {
        var entry = SelectedLibraryItem ?? Library.FirstOrDefault();
        if (entry is null)
        {
            AddLog("warn", "Library is empty; refresh the library before adding notes.");
            return;
        }

        PushUndo();
        var start = FreeStartAfter(beat, 1.0);
        var segment = new VoiceSegment
        {
            Audio = entry.Path,
            SrcPitch = VoiceSegment.DefaultSrcPitch,
            DstPitch = PitchMath.ToPitchName(semitone),
            BeatStart = start,
            BeatEnd = start + 1.0,
            Transition = DefaultTransitionFrames,
        };
        _activeTrack.Segments.Add(segment);
        _activeTrack.Segments.Sort((a, b) => a.BeatStart.CompareTo(b.BeatStart));
        _activeTrack.AddPitchNodes(segment, 5);
        AddLyricWordForSegment(segment);
        RefreshSegments();

        AddLog("info",
            $"Note added: {entry.DisplayName} | beat {start:0.##} -> {start + 1:0.##} | {PitchMath.ToPitchName(semitone)}");
    }

    /// <summary>Pencil drag in the piano roll: create a segment spanning the drawn beat range.</summary>
    public void AddNoteFromDraw(double beatStart, double beatEnd, int semitone)
    {
        var entry = SelectedLibraryItem ?? Library.FirstOrDefault();
        if (entry is null)
        {
            AddLog("warn", "Library is empty; refresh the library before adding notes.");
            return;
        }

        PushUndo();
        var length = Math.Max(0.25, beatEnd - beatStart);
        var start = FreeStartAfter(beatStart, length);
        var segment = new VoiceSegment
        {
            Audio = entry.Path,
            SrcPitch = VoiceSegment.DefaultSrcPitch,
            DstPitch = PitchMath.ToPitchName(semitone),
            BeatStart = start,
            BeatEnd = start + length,
            Transition = DefaultTransitionFrames,
        };
        _activeTrack.Segments.Add(segment);
        _activeTrack.Segments.Sort((a, b) => a.BeatStart.CompareTo(b.BeatStart));
        _activeTrack.AddPitchNodes(segment, 5);
        AddLyricWordForSegment(segment);
        RefreshSegments();

        AddLog("info",
            $"Note drawn: {entry.DisplayName} | beat {start:0.##} -> {start + length:0.##} | {PitchMath.ToPitchName(semitone)}");
    }

    /// <summary>Finds the dictionary character whose single-part audio is the given voicebank sample.</summary>
    private string? FindPlaceholderLyric(string audio)
    {
        if (_dictionary is null)
            return null;

        // Voicebank names may or may not carry the leading underscore ("_あ.wav" vs "あ.wav").
        var name = Path.GetFileNameWithoutExtension(audio).TrimStart('_');
        var match = _dictionary.Entries.FirstOrDefault(entry =>
            entry.Parts.Count == 1
            && Path.GetFileNameWithoutExtension(entry.Parts[0].Audio).TrimStart('_')
                .Equals(name, StringComparison.OrdinalIgnoreCase));
        return match?.Text;
    }

    /// <summary>Starting from <paramref name="desired"/>, find the first free slot that can hold <paramref name="length"/> beats (shifting forward).</summary>
    private double FreeStartAfter(double desired, double length)
    {
        var start = Math.Max(1, desired);
        length = Math.Max(0.25, length);
        foreach (var segment in SegmentLayout.Ordered(_activeTrack))
        {
            if (start < segment.BeatEnd - 0.001 && start + length > segment.BeatStart + 0.001)
                start = segment.BeatEnd;
        }

        return start;
    }

    private void AddTrack()
    {
        var track = ProjectFactory.CreateTrack(_project.Tracks.Count + 1, EffectiveLibraryRoot);
        track.Name = MakeUniqueTrackName(track.Name);

        _project.Tracks.Add(track);
        var row = new TrackRow(track);
        Tracks.Add(row);

        (RemoveTrackCommand as RelayCommand)?.RaiseCanExecuteChanged();
        SelectedTrack = row;
        AddLog("info", $"Track created: {track.Name} (its own library and parameters)");
    }

    private void RemoveTrack()
    {
        if (_project.Tracks.Count <= 1)
        {
            AddLog("warn", "A project must keep at least one track.");
            return;
        }

        var row = SelectedTrack;
        if (row is null)
            return;

        var index = Tracks.IndexOf(row);
        _project.Tracks.Remove(row.Model);
        _trackOutputs.Remove(row.Model);
        if (_lastMixTracks?.Contains(row.Model) == true)
            ClearMergedRender();
        Tracks.Remove(row);

        // Singers lose their score when the bound track disappears; they stay on stage as unbound.
        var unbound = 0;
        foreach (var singer in _project.Chorus.Singers.Where(s => s.TrackId == row.Model.Id))
        {
            singer.TrackId = null;
            unbound++;
        }

        if (unbound > 0)
            RebuildChorusRows();

        (RemoveTrackCommand as RelayCommand)?.RaiseCanExecuteChanged();
        SelectedTrack = Tracks[Math.Min(index, Tracks.Count - 1)];
        AddLog("info", $"Track removed: {row.Name}");
        if (unbound > 0)
            AddLog("warn", $"{unbound} chorus singer(s) unbound because track \"{row.Name}\" was removed.");
    }

    /// <summary>Switch the active track: sync the library, segments, parameters and the last render result.</summary>
    private void ActivateTrack(TrackDocument track)
    {
        StopPlayback();
        PlayheadBeat = null;
        _activeTrack = track;

        OnPropertyChanged(nameof(ActiveTrack));
        OnPropertyChanged(nameof(LibraryRoot));
        OnPropertyChanged(nameof(BreathyThr));
        OnPropertyChanged(nameof(ApScale));
        OnPropertyChanged(nameof(ApMax));
        OnPropertyChanged(nameof(BitDepth));
        OnPropertyChanged(nameof(Jitter));
        OnPropertyChanged(nameof(Shimmer));
        RefreshParameterRows();

        SelectedSegment = null;
        SelectedPitchIndex = null;
        RefreshLibrary();
        RefreshSegments();
        DictionaryStartBeat = track.TotalBeats > 0 ? track.TotalBeats : 1.0;

        // The lyric strip shows this track's persisted lyric objects (kept across track switches and restarts).
        LoadLyricFromTrack();

        if (_lastMixTracks is { } mixTracks && _lastMixPath is { } mixPath
            && mixTracks.Contains(track) && File.Exists(mixPath))
        {
            // Playback of this track replays the merged WAV so every rendered track is heard together.
            LastRenderPath = mixPath;
            PreviewInfo = $"{Path.GetFileName(mixPath)} · {_lastMixDurationSeconds:F2}s";
        }
        else if (_trackOutputs.TryGetValue(track, out var output) && File.Exists(output.Path))
        {
            LastRenderPath = output.Path;
            PreviewInfo = $"{Path.GetFileName(output.Path)} · {output.DurationSeconds:F2}s";
        }
        else
        {
            LastRenderPath = null;
            PreviewInfo = "尚未渲染";
        }
    }

    private string MakeUniqueTrackName(string name)
    {
        var candidate = name;
        var suffix = 2;
        while (_project.Tracks.Any(t => string.Equals(t.Name, candidate, StringComparison.Ordinal)))
            candidate = $"{name} ({suffix++})";
        return candidate;
    }

    // ------------------------------------------------------------------
    // Chorus mode: stage, microphone and singers
    // ------------------------------------------------------------------

    private void AddChorusSinger()
    {
        PushUndo();

        var index = _project.Chorus.Singers.Count;
        var positions = ChorusMath.DefaultPositions(index + 1, Chorus.StageWidth, Chorus.StageDepth);

        // Pick the candidate slot that is farthest from the singers already on stage, so added singers
        // never start stacked on top of each other.
        var existing = _project.Chorus.Singers
            .Select(s => ChorusMath.Evaluate(s.Path, 1))
            .ToList();
        var position = positions.Count == 0
            ? (X: 0.0, Y: Chorus.StageDepth * 0.22)
            : positions
                .OrderByDescending(candidate => existing.Count == 0
                    ? 0
                    : existing.Min(p => ChorusMath.Distance(candidate.X, candidate.Y, p.X, p.Y)))
                .First();

        var singer = new ChorusSinger
        {
            Name = $"歌手 {index + 1}",
            TrackId = _activeTrack.Id,
            Path = [new ChorusKeyframe { Beat = 1, X = position.X, Y = position.Y }],
        };
        _project.Chorus.Singers.Add(singer);
        RebuildChorusRows();
        SelectedChorusSinger = ChorusSingers.FirstOrDefault(r => r.Model.Id == singer.Id);
        TouchChorus();
        AddLog("info", $"Chorus singer added: {singer.Name} -> {_activeTrack.Name}.");
    }

    private void RemoveChorusSinger()
    {
        if (_selectedChorusSinger is null)
            return;

        PushUndo();
        var singer = _selectedChorusSinger.Model;
        _project.Chorus.Singers.Remove(singer);
        RebuildChorusRows();
        TouchChorus();
        AddLog("info", $"Chorus singer removed: {singer.Name}.");
    }

    private void JumpToSingerTrack()
    {
        if (SelectedSingerTrack is not { } track)
            return;

        SelectedTrack = track;
        // The score being edited lives in the track editor tab, so bring the user there.
        MainTabIndex = 0;
    }

    /// <summary>Loads formation presets (name + positions in meters) from one or more JSON files.</summary>
    private void ImportChorusFormation()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入合唱队形",
            Filter = "队形 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var imported = new List<ChorusFormationPreset>();
            foreach (var path in dialog.FileNames)
                imported.AddRange(ChorusFormationJson.Parse(File.ReadAllText(path)));
            if (imported.Count == 0)
                return;

            PushUndo();
            foreach (var preset in imported)
            {
                preset.Name = MakeUniqueFormationName(preset.Name);
                _project.Chorus.Formations.Add(preset);
            }

            RebuildChorusFormations();
            SelectedChorusFormation = ChorusFormations.FirstOrDefault(f => f.Id == imported[0].Id);
            AddLog("info", $"Imported {imported.Count} chorus formation(s).");
        }
        catch (Exception ex)
        {
            AddLog("warn", "Chorus formation import failed: " + ex.Message);
            StatusText = "队形导入失败";
        }
    }

    private string MakeUniqueFormationName(string name)
    {
        var candidate = string.IsNullOrWhiteSpace(name) ? "导入队形" : name.Trim();
        var baseName = candidate;
        var suffix = 2;
        while (_project.Chorus.Formations.Any(f => string.Equals(f.Name, candidate, StringComparison.Ordinal)))
            candidate = $"{baseName} ({suffix++})";
        return candidate;
    }

    /// <summary>Applies the selected imported formation at the current playhead beat (undoable).</summary>
    private void ApplyChorusFormationPreset()
    {
        var preset = _selectedChorusFormation;
        if (preset is null || preset.Positions.Count == 0)
            return;

        var singers = _project.Chorus.Singers;
        if (singers.Count == 0)
        {
            AddLog("warn", "No chorus singer to arrange; add a singer first.");
            StatusText = "请先添加歌手";
            return;
        }

        PushUndo();
        var beat = ChorusMath.SnapBeat(PlayheadBeat ?? 1);
        for (var i = 0; i < singers.Count; i++)
        {
            var singer = singers[i];
            var point = preset.Positions[i % preset.Positions.Count];
            singer.Path.RemoveAll(p => Math.Abs(p.Beat - beat) < 1e-6);
            singer.Path.Add(new ChorusKeyframe
            {
                Beat = beat,
                X = ChorusMath.ClampX(point.X, Chorus.StageWidth),
                Y = ChorusMath.ClampY(point.Y, Chorus.StageDepth),
            });
            singer.Path.Sort((a, b) => a.Beat.CompareTo(b.Beat));
        }

        TouchChorus();
        AddLog("info", $"Imported formation \"{preset.Name}\" applied to {singers.Count} singer(s) at beat {beat:0.##}.");
    }

    /// <summary>Called by the stage editor when a singer is clicked.</summary>
    public void SelectChorusSinger(ChorusSinger? singer)
    {
        SelectedChorusSinger = singer is null
            ? null
            : ChorusSingers.FirstOrDefault(r => ReferenceEquals(r.Model, singer));
    }

    /// <summary>Called by the stage editor before a drag starts; snapshots the project for undo.</summary>
    public void BeginChorusEdit() => PushUndo();

    /// <summary>Called by the stage editor after a drag; makes the change visible and drops stale renders.</summary>
    public void CommitChorusEdit() => TouchChorus();

    /// <summary>Bumps the redraw token and, in chorus mode, discards renders that used the old layout.</summary>
    private void TouchChorus()
    {
        _chorusRevision++;
        OnPropertyChanged(nameof(ChorusRevision));
        OnPropertyChanged(nameof(ChorusStageInfo));
        OnPropertyChanged(nameof(StageWidth));
        OnPropertyChanged(nameof(StageDepth));
        foreach (var row in ChorusSingers)
            row.Refresh();
        InvalidateChorusRender();
    }

    private void RebuildChorusRows()
    {
        var selectedId = _selectedChorusSinger?.Model.Id;

        ChorusSingers.Clear();
        foreach (var singer in _project.Chorus.Singers)
        {
            ChorusSingers.Add(new ChorusSingerRow(singer, ResolveTrackName, () =>
            {
                _chorusRevision++;
                OnPropertyChanged(nameof(ChorusRevision));
            }, TouchChorus));
        }

        _selectedChorusSinger = selectedId is null
            ? null
            : ChorusSingers.FirstOrDefault(r => r.Model.Id == selectedId);
        _selectedChorusSinger ??= ChorusSingers.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedChorusSinger));
        OnPropertyChanged(nameof(SelectedChorusSingerModel));
        OnPropertyChanged(nameof(SelectedSingerTrack));
        OnPropertyChanged(nameof(SelectedSingerText));
        OnPropertyChanged(nameof(HasChorusSingers));
        (RemoveChorusSingerCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (JumpToChorusTrackCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private string? ResolveTrackName(string? trackId) => trackId is null
        ? null
        : _project.Tracks.FirstOrDefault(t => t.Id == trackId)?.Name;

    private void RebuildChorusFormations()
    {
        var selectedId = _selectedChorusFormation?.Id;

        ChorusFormations.Clear();
        foreach (var formation in _project.Chorus.Formations)
            ChorusFormations.Add(formation);

        _selectedChorusFormation = selectedId is null
            ? null
            : ChorusFormations.FirstOrDefault(f => f.Id == selectedId);
        _selectedChorusFormation ??= ChorusFormations.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedChorusFormation));
        OnPropertyChanged(nameof(HasChorusFormations));
        (ApplyChorusFormationPresetCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>Chorus edits change the rendered audio; keep stale renders from playing.</summary>
    private void InvalidateChorusRender()
    {
        _trackOutputs.Clear();
        ClearMergedRender();
        LastRenderPath = null;
        PreviewInfo = "合唱设置已修改，需要重新渲染";
    }

    // ------------------------------------------------------------------
    // Library
    // ------------------------------------------------------------------
    /// <summary>Rebuild the voicebank choices from voicebank/, keeping an out-of-tree path visible.</summary>
    private void RefreshVoicebanks()
    {
        var current = _activeTrack.LibraryRoot;
        Voicebanks.Clear();

        if (Directory.Exists(VoicebankRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(VoicebankRoot)
                         .OrderBy(directory => Path.GetFileName(directory) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                Voicebanks.Add(new VoicebankOption(Path.GetFileName(directory), directory));
        }

        if (!string.IsNullOrWhiteSpace(current)
            && !SamePath(current, VoicebankRoot)
            && !Voicebanks.Any(option => SamePath(option.Path, current)))
        {
            var name = Path.GetFileName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            Voicebanks.Add(new VoicebankOption(string.IsNullOrEmpty(name) ? current : name, current));
        }

        OnPropertyChanged(nameof(SelectedVoicebank));
    }

    /// <summary>
    /// Install a dropped voicebank folder: validate and copy it off the UI thread, then use it on the active track.
    /// Returns null on success, otherwise the reason the install was rejected.
    /// </summary>
    public async Task<string?> InstallVoicebankAsync(string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
            return "拖入的内容不是文件夹。";

        var source = Path.GetFullPath(sourceDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(source);
        if (string.IsNullOrEmpty(name) || SamePath(source, VoicebankRoot))
            return "请拖入声库文件夹本身，而不是 voicebank 目录。";

        var target = Path.Combine(VoicebankRoot, name);
        if (Directory.Exists(target))
            return $"voicebank 中已存在同名声库：{name}";

        string? error;
        try
        {
            // Large folders would freeze the window if scanned on the UI thread.
            error = await Task.Run(() =>
            {
                if (!Directory.EnumerateFiles(source, "*.wav", SearchOption.AllDirectories).Any())
                    return "声库格式错误：没有 wav 音频文件。";

                Directory.CreateDirectory(VoicebankRoot);
                CopyDirectory(source, target);
                return null;
            });
        }
        catch (Exception ex)
        {
            if (Directory.Exists(target))
            {
                try { Directory.Delete(target, recursive: true); } catch { /* best effort rollback */ }
            }
            AddLog("error", "Voicebank install failed: " + ex.Message);
            return "安装失败：" + ex.Message;
        }

        if (error is not null)
            return error;

        var previousRoot = EffectiveLibraryRoot;
        LibraryRoot = target;
        var missing = RetargetSegments(previousRoot, target);
        InvalidateRender();
        RefreshSegments();
        RefreshLibrary();
        AddLog("info", $"Voicebank installed: {target}");
        StatusText = missing > 0
            ? $"已安装声库：{name}（{missing} 个音符无同名采样）"
            : $"已安装声库：{name}";
        return null;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    /// <summary>Compares two paths loosely: ignores a trailing separator and letter case.</summary>
    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        return string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Repoint the existing notes of the active track to the same-named samples in the new voicebank.
    /// Notes whose sample name is missing in the new voicebank keep their current audio; that count is returned.
    /// </summary>
    private int RetargetSegments(string? previousRoot, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(previousRoot)
            || string.IsNullOrWhiteSpace(newRoot)
            || SamePath(previousRoot, newRoot))
        {
            return 0;
        }

        var previousPrefix = Path.GetFullPath(previousRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var changed = 0;
        var missing = 0;
        foreach (var segment in _activeTrack.Segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Audio) || !Path.IsPathRooted(segment.Audio))
                continue;

            var audio = Path.GetFullPath(segment.Audio);
            if (!audio.StartsWith(previousPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = Path.GetFullPath(Path.Combine(newRoot, audio[previousPrefix.Length..]));
            if (!File.Exists(candidate))
            {
                missing++;
                continue;
            }

            segment.Audio = candidate;
            changed++;
        }

        if (changed > 0)
            AddLog("info", $"声库切换：{changed} 个音符改用 \"{Path.GetFileName(newRoot)}\" 的采样。");
        if (missing > 0)
            AddLog("warn", $"声库切换：{missing} 个音符在新声库中没有同名采样，保留原文件。");
        return missing;
    }

    private void RefreshLibrary()
    {
        Library.Clear();
        RefreshVoicebanks();
        foreach (var entry in LibraryScanner.Scan(EffectiveLibraryRoot))
            Library.Add(entry);
        RefreshLibraryIcon();
        StatusText = $"声库：{Library.Count} 个音频";
        OnPropertyChanged(nameof(HasVoicebank));
    }

    /// <summary>
    /// 声库图标：在声库目录中查找任意图片文件；找不到（或都无法解码）时使用 VocalB1 自带图标。
    /// </summary>
    private void RefreshLibraryIcon()
    {
        var root = EffectiveLibraryRoot;
        string? path = null;
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            path = Directory.EnumerateFiles(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                })
                .Where(IsImageFile)
                .OrderBy(p => p.Length)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        var icon = LoadLibraryIcon(path) ?? LoadAppIcon();
        if (ReferenceEquals(icon, _libraryIcon))
            return;

        _libraryIcon = icon;
        OnPropertyChanged(nameof(LibraryIcon));
        OnPropertyChanged(nameof(HasLibraryIcon));
    }

    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico"];

    private static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>声库无可用图片时回退到应用自带图标（见 csproj 中的 Assets\VocalB1.ico 资源）。</summary>
    private static ImageSource? LoadAppIcon()
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri("pack://application:,,,/Assets/VocalB1.ico", UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ImageSource? LoadLibraryIcon(string? path)
    {
        if (path is null)
            return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // 声库文件属于外部输入，损坏或无法解码的图片直接忽略，不影响声库列表。
            return null;
        }
    }

    private void AddLibraryItem(LibraryEntry? entry)
    {
        if (entry is null)
            return;

        PushUndo();
        var start = _activeTrack.Segments.Count == 0 ? 1.0 : _activeTrack.Segments.Max(s => s.BeatEnd);
        var segment = new VoiceSegment
        {
            Audio = entry.Path,
            SrcPitch = VoiceSegment.DefaultSrcPitch,
            DstPitch = "D4",
            BeatStart = start,
            BeatEnd = start + 1.0,
            Transition = DefaultTransitionFrames,
        };
        _activeTrack.Segments.Add(segment);
        _activeTrack.AddPitchNodes(segment, 5);
        AddLyricWordForSegment(segment);
        RefreshSegments();
        AddLog("info", $"Segment added: {entry.DisplayName} ({_activeTrack.Name}, beat {start:0.##} -> {start + 1:0.##})");
    }

    // ------------------------------------------------------------------
    // User dictionary
    // ------------------------------------------------------------------
    private void ImportDictionary()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入用户字典",
            Filter = "用户字典 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var dictionary = DictionaryJson.Load(dialog.FileName);
            ApplyDictionary(dictionary, dialog.FileName);

            StatusText = $"已导入字典：{dictionary.Name}";
            AddLog("info", $"User dictionary imported: \"{dictionary.Name}\" ({dictionary.Entries.Count} entries), {dialog.FileName}");

            foreach (var issue in dictionary.Validate())
                AddLog("warn", "Dictionary: " + issue);
        }
        catch (Exception ex)
        {
            AddLog("error", "Dictionary import failed: " + ex.Message);
            StatusText = "导入字典失败";
        }
    }

    /// <summary>Loads the built-in dictionary (kana + common kanji) so the panel works without importing a file.</summary>
    private void LoadDefaultDictionary()
    {
        var dictionary = DefaultDictionary.Create();
        ApplyDictionary(dictionary, null);
        AddLog("info", $"Built-in dictionary loaded: \"{dictionary.Name}\" ({dictionary.Entries.Count} entries).");
    }

    /// <summary>Points the panel at a dictionary; a null path means the built-in default one.</summary>
    private void ApplyDictionary(UserDictionary dictionary, string? path)
    {
        _dictionary = dictionary;
        _dictionaryPath = path;

        DictionaryEntries.Clear();
        foreach (var entry in dictionary.Entries)
            DictionaryEntries.Add(new DictionaryEntryRow(entry));

        OnPropertyChanged(nameof(DictionaryName));
        OnPropertyChanged(nameof(DictionaryPathText));

        SelectedDictionaryEntry = DictionaryEntries.FirstOrDefault();
        DictionaryStartBeat = _activeTrack.TotalBeats > 0 ? _activeTrack.TotalBeats : 1.0;
    }

    private void InsertDictionaryEntry()
    {
        if (_dictionary is null || SelectedDictionaryEntry is null)
            return;

        var entry = SelectedDictionaryEntry.Model;
        try
        {
            PushUndo();
            var startBeat = FreeStartAfter(DictionaryStartBeat, DictionaryBeats > 0 ? DictionaryBeats : 1.0);
            var segments = DictionaryResolver.Resolve(
                entry,
                string.IsNullOrWhiteSpace(_dictionaryPath) ? null : Path.GetDirectoryName(_dictionaryPath),
                ResolveDictionaryLibraryRoot(),
                startBeat,
                DictionaryBeats,
                DictionaryDstPitch,
                _dictionary.DefaultSrcPitch);

            _activeTrack.Segments.AddRange(segments);
            _activeTrack.Segments.Sort((a, b) => a.BeatStart.CompareTo(b.BeatStart));
            foreach (var segment in segments)
                _activeTrack.AddPitchNodes(segment, 5);
            AddLyricWordForSegments(entry.Text, segments);
            RefreshSegments();

            StatusText = $"已插入字典条目：{entry.Text}";
            AddLog("info",
                $"Dictionary entry \"{entry.Text}\" -> {_activeTrack.Name}: {segments.Count} segments (beat {segments[0].BeatStart:0.##} -> {segments[^1].BeatEnd:0.##}, target {segments[0].DstPitch})");
        }
        catch (Exception ex)
        {
            AddLog("error", "Failed to insert dictionary entry: " + ex.Message);
            StatusText = "插入字典条目失败";
        }
    }

    /// <summary>The dictionary's own LibraryRoot (may be relative to the dictionary directory) takes precedence; otherwise the current track's library is used.</summary>
    private string? ResolveDictionaryLibraryRoot()
    {
        if (_dictionary is null || string.IsNullOrWhiteSpace(_dictionary.LibraryRoot))
            return EffectiveLibraryRoot;

        var own = _dictionary.LibraryRoot!;
        if (Path.IsPathRooted(own))
            return own;

        var directory = string.IsNullOrWhiteSpace(_dictionaryPath)
            ? null
            : Path.GetDirectoryName(_dictionaryPath);
        return directory is null ? own : Path.Combine(directory, own);
    }

    // ------------------------------------------------------------------
    // BGM (backing track)
    // ------------------------------------------------------------------
    /// <summary>Opens a WAV picker and installs it as the project-wide BGM.</summary>
    private void ImportBgm()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 BGM 伴奏",
            Filter = "WAV 音频 (*.wav)|*.wav|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;

        SetBgm(dialog.FileName);
    }

    /// <summary>
    /// Sets the project-wide BGM and resets its start to the project start (第 0 小节第 0 音).
    /// The BGM keeps its original speed and is only used for preview playback.
    /// </summary>
    public void SetBgm(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var info = WavReader.ReadInfo(full);
            PushUndo();
            _project.BgmPath = full;
            _project.BgmStartBeat = 0;
            _bgmMixKey = null;
            RaiseBgmChanged();
            StatusText = $"已导入 BGM：{BgmName}";
            AddLog("info", $"BGM imported: {full} ({info.SampleRate} Hz, {info.Channels} ch, "
                + $"{info.BitsPerSample} bit, {info.DurationSeconds:F1}s). It plays at its original speed.");
        }
        catch (Exception ex)
        {
            AddLog("warn", "BGM import failed: " + ex.Message);
            StatusText = "导入 BGM 失败";
        }
    }

    public void ClearBgm()
    {
        if (!HasBgm)
            return;

        PushUndo();
        _project.BgmPath = null;
        _project.BgmStartBeat = 0;
        _bgmMixKey = null;
        RaiseBgmChanged();
        StatusText = "已移除 BGM";
        AddLog("info", "BGM removed.");
    }

    private void RaiseBgmChanged()
    {
        OnPropertyChanged(nameof(BgmPath));
        OnPropertyChanged(nameof(HasBgm));
        OnPropertyChanged(nameof(BgmName));
        OnPropertyChanged(nameof(BgmStartBeat));
        OnPropertyChanged(nameof(BgmDurationBeats));
        (ClearBgmCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Mixes the BGM under the rendered vocal into a cached WAV for playback. The cache key covers the
    /// vocal file, the BGM file and the start offset, so a re-render or a start change rebuilds it.
    /// </summary>
    private string? EnsureBgmMix(string vocalPath)
    {
        var bgmPath = _project.BgmPath;
        if (string.IsNullOrWhiteSpace(bgmPath))
            return null;
        if (!File.Exists(bgmPath))
        {
            AddLog("warn", $"BGM file not found: {bgmPath}; playing the vocal track only.");
            return null;
        }

        var startSeconds = Math.Max(0, _project.BgmStartBeat) * 60.0 / Math.Max(1, _bpm);
        var key = $"{vocalPath}|{File.GetLastWriteTimeUtc(vocalPath):O}|"
            + $"{bgmPath}|{File.GetLastWriteTimeUtc(bgmPath):O}|{startSeconds:R}";
        if (key == _bgmMixKey && _bgmMixPath is { } cached && File.Exists(cached))
            return cached;

        try
        {
            StatusText = "BGM 混音中…";
            var directory = Path.Combine(Path.GetTempPath(), "vocalb1-bgm");
            Directory.CreateDirectory(directory);
            var output = Path.Combine(directory, $"mix_{Guid.NewGuid():N}.wav");
            var started = DateTime.Now;
            BgmMixer.Mix(vocalPath, bgmPath, startSeconds, output);

            if (_bgmMixPath is { } previous && !string.Equals(previous, output, StringComparison.Ordinal))
            {
                try { File.Delete(previous); } catch { /* best effort cleanup */ }
            }

            _bgmMixPath = output;
            _bgmMixKey = key;
            AddLog("info", $"BGM mixed at beat {_project.BgmStartBeat:0.##} ({startSeconds:0.##}s), "
                + $"{(DateTime.Now - started).TotalSeconds:F1}s elapsed.");
            return output;
        }
        catch (Exception ex)
        {
            _bgmMixKey = null;
            AddLog("warn", "BGM mixing failed; playing the vocal track only: " + ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Render / Playback
    // ------------------------------------------------------------------

    /// <summary>
    /// Drop the cached render of the active track after a synthesis parameter changed.
    /// Playback replays the last rendered WAV, so without this a slider change would
    /// still play the audio produced with the old value and look like it had no effect.
    /// </summary>
    private void InvalidateRender()
    {
        if (!_trackOutputs.Remove(_activeTrack, out var output))
            return;

        // A merged WAV containing this track is stale as well.
        if (_lastMixTracks is { } sources && sources.Contains(_activeTrack))
        {
            ClearMergedRender();
            return;
        }

        if (string.Equals(LastRenderPath, output.Path, StringComparison.OrdinalIgnoreCase))
        {
            LastRenderPath = null;
            PreviewInfo = "参数已修改，需要重新渲染";
        }
    }

    /// <summary>Forget the merged render; it no longer reflects the current project.</summary>
    private void ClearMergedRender()
    {
        if (_lastMixTracks is null && _lastMixPath is null)
            return;

        if (_lastMixPath is not null && string.Equals(LastRenderPath, _lastMixPath, StringComparison.OrdinalIgnoreCase))
        {
            LastRenderPath = null;
            PreviewInfo = "参数已修改，需要重新渲染";
        }

        _lastMixPath = null;
        _lastMixTracks = null;
        _lastMixDurationSeconds = 0;
    }

    private async Task RenderAsync()
    {
        // Rendering is driven by chorus singers: every enabled singer contributes its bound track at its
        // stage position. A track is never mixed in directly, so no singer means nothing to render.
        var singers = _project.Chorus.Singers
            .Where(s => s.Enabled && _project.Tracks.Any(t => t.Id == s.TrackId))
            .ToList();
        if (singers.Count == 0)
        {
            StatusText = "没有歌手，无法渲染";
            AddLog("warn", "No enabled chorus singer; rendering is driven by singers, so nothing was rendered.");
            return;
        }

        var tracks = _project.Tracks
            .Where(t => singers.Any(s => s.TrackId == t.Id))
            .ToList();

        IsBusy = true;
        StatusText = "渲染中…";
        _renderCts = new CancellationTokenSource();
        var started = DateTime.Now;

        try
        {
            var blocked = false;
            foreach (var track in tracks)
            {
                var issues = ProjectValidator.Validate(track, _project);
                foreach (var issue in issues)
                    AddLog(issue.Severity == ProjectIssueSeverity.Error ? "error" : "warn", issue.Message);
                blocked |= ProjectValidator.HasError(issues);
            }

            if (blocked)
            {
                StatusText = "工程校验未通过";
                return;
            }

            var directory = ProjectFactory.DefaultRenderDirectory(_services.Options.RepositoryRoot);
            Directory.CreateDirectory(directory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            AddLog("info", $"Starting the Python engine to render {tracks.Count} track(s)...");
            await _services.Engine.InitializeAsync(_renderCts.Token);

            var progress = new Progress<EngineEvent>(OnEngineEvent);
            var outputs = new List<string>(tracks.Count);
            for (var i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                var stem = string.Concat(track.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var output = Path.Combine(directory, $"render_{i + 1}_{stem}_{timestamp}.wav");

                StatusText = $"渲染中（{i + 1}/{tracks.Count}）：{track.Name}";
                AddLog("info", $"Rendering track \"{track.Name}\" ({i + 1}/{tracks.Count})...");
                var result = await _services.Engine.RenderAsync(
                    new RenderRequest(_project, track, output), progress, _renderCts.Token);

                // The engine output is mono. Every singer bound to this track gets its own
                // position-dependent version (distance attenuation, HF air absorption, pan) and the
                // versions are summed; the track itself is never mixed in directly.
                var chorusSingers = singers.Where(s => s.TrackId == track.Id).ToList();
                var singerOutputs = new List<string>(chorusSingers.Count);
                for (var s = 0; s < chorusSingers.Count; s++)
                {
                    var singerPath = Path.Combine(
                        directory, $"render_{i + 1}_{stem}_chorus{s + 1}_{timestamp}.wav");
                    ChorusProcessor.Process(
                        result.OutputPath, singerPath, _project.Chorus, [chorusSingers[s]], _bpm,
                        track.Settings.BitDepth);
                    singerOutputs.Add(singerPath);
                }

                var chorusPath = Path.Combine(directory, $"render_{i + 1}_{stem}_chorus_{timestamp}.wav");
                TrackMixer.Mix(singerOutputs, chorusPath, track.Settings.BitDepth);
                _trackOutputs[track] = new RenderedTrack(chorusPath, result.DurationSeconds);
                outputs.Add(chorusPath);
                AddLog("info",
                    $"Chorus processing: {chorusSingers.Count} singer(s) on track \"{track.Name}\".");
            }

            // All selected tracks were rendered to one shared timeline; sum them into a single WAV.
            StatusText = "合并音轨…";
            var mixPath = Path.Combine(directory, $"render_mix_{timestamp}.wav");
            var mixBitDepth = tracks.Count == 0 ? 16 : tracks.Max(t => t.Settings.BitDepth);
            TrackMixer.Mix(outputs, mixPath, mixBitDepth);
            var mixInfo = WavReader.ReadInfo(mixPath);

            _lastMixPath = mixPath;
            _lastMixTracks = tracks.ToList();
            _lastMixDurationSeconds = mixInfo.DurationSeconds;
            LastRenderPath = mixPath;
            PreviewInfo = $"{Path.GetFileName(mixPath)} · {mixInfo.DurationSeconds:F2}s";
            StatusText = $"渲染完成（{(DateTime.Now - started).TotalSeconds:F1}s）";
            AddLog("info", $"{tracks.Count} track(s) merged into: {mixPath}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "渲染已取消";
            AddLog("warn", "Render canceled (the Python process was terminated).");
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            _renderCts?.Dispose();
            _renderCts = null;
            IsBusy = false;
        }
    }

    private void OnEngineEvent(EngineEvent e)
    {
        if (e.Kind == EngineEventKind.Log || e.Kind == EngineEventKind.Warning)
        {
            AddLog(e.Kind == EngineEventKind.Warning ? "warn" : "info", e.Message);
            StatusText = e.Message;
        }
        else if (e.Kind == EngineEventKind.Progress && e.Progress is not null)
        {
            StatusText = $"{e.Message} {e.Progress:P0}".Trim();
        }
    }

    private void TogglePlay()
    {
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }

        var path = LastRenderPath;
        var playbackTrack = _activeTrack;

        // Solo affects preview playback only: with a soloed track, play that track's rendered result.
        if (_project.Tracks.FirstOrDefault(t => t.Solo) is { } soloTrack)
        {
            playbackTrack = soloTrack;
            path = _trackOutputs.TryGetValue(soloTrack, out var soloOutput) && File.Exists(soloOutput.Path)
                ? soloOutput.Path
                : null;
            if (path is null)
            {
                AddLog("warn", $"Solo track \"{soloTrack.Name}\" has not been rendered yet; render it first.");
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AddLog("warn", "Nothing to play; render the track first.");
            return;
        }

        // The project-wide BGM is mixed under the rendered vocal right before playback, so a start
        // time change takes effect without re-rendering.
        var playbackPath = EnsureBgmMix(path) ?? path;

        // Start from the current playhead position; when there is no playhead yet, or playback already reached the end, restart from beat 1.
        var endBeat = PlaybackEndBeat(playbackTrack);
        if (_lastMixPath is not null && _lastMixDurationSeconds > 0.01
            && string.Equals(path, _lastMixPath, StringComparison.OrdinalIgnoreCase))
        {
            // The merged WAV may outlast the active track, so clamp against its own length.
            endBeat = 1 + _lastMixDurationSeconds * Math.Max(1, Bpm) / 60;
        }
        var startBeat = Math.Clamp(PlayheadBeat ?? 1, 1, endBeat);
        if (endBeat - startBeat < 0.05)
            startBeat = 1;

        var start = TimeSpan.FromSeconds(Math.Max(0, (startBeat - 1) * 60 / Math.Max(1, Bpm)));

        try
        {
            PlayheadBeat = startBeat;
            _services.Playback.Play(playbackPath, start);
            IsPlaying = true;
            _playheadTimer.Start();
            StatusText = $"播放中：{Path.GetFileName(playbackPath)}（第 {startBeat:0.##} 拍起）";
        }
        catch (Exception ex)
        {
            IsPlaying = false;
            ReportError(ex);
        }
    }

    /// <summary>Stop playback; the playhead stays at the current position so playback can resume from there.</summary>
    private void StopPlayback()
    {
        if (!IsPlaying)
        {
            _playheadTimer.Stop();
            return;
        }

        SyncPlayheadFromPlayback();
        _services.Playback.Stop();
        _playheadTimer.Stop();
        IsPlaying = false;
    }

    /// <summary>Called when the playhead drag starts: pause playback and keep the playhead position.</summary>
    public void PauseForPlayheadDrag()
    {
        StopPlayback();
        PlayheadBeat ??= 1;
    }

    /// <summary>Convert the player's current time to a 1-based beat position (audio second 0 = beat 1).</summary>
    private void SyncPlayheadFromPlayback()
    {
        // End playback when the end is reached (or the player stops unexpectedly); the playhead stays where it is.
        if (!_services.Playback.IsPlaying)
        {
            _playheadTimer.Stop();
            IsPlaying = false;
            return;
        }

        var position = _services.Playback.Position;

        // The position may still be 0 right after a seek takes effect; do not pull the playhead back to beat 1 in that case.
        if (position <= TimeSpan.Zero && PlayheadBeat is double current && current > 1.001)
            return;

        PlayheadBeat = Math.Max(1, 1 + position.TotalSeconds * Math.Max(1, Bpm) / 60);
    }

    /// <summary>End beat of the current track's audio (used to clamp the playhead and start time).</summary>
    private double PlaybackEndBeat(TrackDocument track)
    {
        if (_trackOutputs.TryGetValue(track, out var output) && output.DurationSeconds > 0.01)
            return 1 + output.DurationSeconds * Math.Max(1, Bpm) / 60;

        return Math.Max(1, track.TotalBeats);
    }

    private void ReportError(Exception ex)
    {
        AddLog("error", ex.Message);
        StatusText = "发生错误";
        MessageBox.Show(ex.Message, "VocalB1", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // ------------------------------------------------------------------
    // Log
    // ------------------------------------------------------------------
    private void AddLog(string kind, string message)
    {
        Logs.Add(new LogEntry(DateTime.Now, kind, message));
        while (Logs.Count > MaxLogEntries)
            Logs.RemoveAt(0);
        LogAppended?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lets the view automatically scroll to the newest log entry.</summary>
    public event EventHandler? LogAppended;

    /// <summary>Last render result of a single track.</summary>
    private sealed record RenderedTrack(string Path, double DurationSeconds);

    /// <summary>Rebuild both per-band slider collections from the current track's synthesis parameters.</summary>
    private void RefreshParameterRows()
    {
        FollowRatioRows.Clear();
        var follow = _activeTrack.Settings.FollowRatio;
        for (var i = 0; i < follow.Length; i++)
        {
            var index = i;
            FollowRatioRows.Add(new ParameterRow(LanguageService.Translate($"频段 {i + 1}"), 0, 1, 0.01, follow[i],
                value => follow[index] = value));
        }

        BandRatioRows.Clear();
        var bands = _activeTrack.Settings.BandRatios;
        for (var i = 0; i < bands.Length; i++)
        {
            var index = i;
            BandRatioRows.Add(new ParameterRow(LanguageService.Translate($"频段 {i + 1}"), 0, 3, 0.05, bands[i],
                value => bands[index] = value));
        }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            action();
        else
            dispatcher.Invoke(action);
    }
}
