using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
using VocalB1.Core.Dictionaries;
using VocalB1.Core.Diagnostics;
using VocalB1.Core.Library;
using VocalB1.Core.Models;
using VocalB1.Core.Pitch;
using VocalB1.Core.Serialization;
using VocalB1.Engine.Audio;

namespace VocalB1.App.ViewModels;

/// <summary>
/// Main window ViewModel for the v3 object-library model. There are no tracks and no stage: the project owns
/// lyrics, parameter sets and singers; each singer references one lyric (one-to-one) and one shared parameter
/// set, and the synthesizer is invoked once per singer.
/// </summary>
public sealed class MainViewModel : ObservableObject, INodeGraphHost
{
    private const int MaxLogEntries = 500;
    private const int MaxUndoSteps = 100;
    private const double FramesPerBeatConstant = 12000.0;
    private const double DefaultLyricUnitBeats = 1.0;
    private const string LyricBlockPitch = "D4";

    private readonly AppServices _services;
    private readonly List<ProjectDocument> _undoStack = [];
    private readonly List<ProjectDocument> _redoStack = [];
    private readonly DispatcherTimer _playheadTimer;

    private ProjectDocument _project;
    private string? _projectPath;
    private string _savedSnapshot = string.Empty;
    private ImageSource? _libraryIcon;
    private CancellationTokenSource? _renderCts;
    private string? _lastMixPath;
    private string? _bgmMixPath;
    private string? _bgmMixKey;

    private LyricRow? _selectedLyric;
    private ParameterSetRow? _selectedParameterSet;
    private SingerRow? _selectedSinger;
    private SegmentRow? _selectedSegment;
    private LyricUnitRow? _selectedUnit;
    private ParameterKeyRow? _selectedParameterKey;
    private ParameterNode? _selectedNode;
    private NodeRow? _selectedNodeRow;
    private LibraryEntry? _selectedLibraryItem;
    private DictionaryEntryRow? _selectedDictionaryEntry;
    private LanguageRow? _selectedLanguage;

    private bool _editSingerGraph;
    private int _graphRevision;
    private bool _isBusy;
    private bool _isPlaying;
    private bool _isPencil;
    private bool _isLightTheme;
    private bool _languageReady;
    private bool _syncingRows;
    private PianoRollMode _rollMode = PianoRollMode.Notes;
    private int _gridDivisionIndex;
    private int? _selectedNoteIndex;
    private int? _selectedPitchIndex;
    private int _rollRevision = 1;
    private double? _playheadBeat;
    private string _statusText = "就绪";
    private string? _statusFormat;
    private object?[]? _statusArgs;
    private string _previewInfo = "尚未渲染";
    private string? _previewFormat;
    private object?[]? _previewArgs;
    private string _lyricInput = string.Empty;
    private string _lyricStatus = "输入歌词后按回车，按字典分解为最小声库单位";
    private string? _lyricStatusFormat;
    private object?[]? _lyricStatusArgs;
    private string _unitFormantText = string.Empty;

    private UserDictionary? _dictionary;
    private string? _dictionaryPath;
    private double _dictionaryStartBeat = 1.0;
    private double _dictionaryBeats = 1.0;
    private string _dictionaryDstPitch = "G4";

    public MainViewModel(AppServices services)
    {
        _services = services;
        _project = ProjectFactory.CreateEmpty(services.Options.LibraryRoot);

        LanguageService.Initialize(services.Options.RepositoryRoot);
        RebuildLanguages();

        NewProjectCommand = new RelayCommand(_ => NewProject());
        OpenProjectCommand = new RelayCommand(_ => OpenProject());
        SaveProjectCommand = new RelayCommand(_ => SaveProject());
        LoadDemoCommand = new RelayCommand(_ => LoadDemo());
        UndoCommand = new RelayCommand(_ => Undo(), _ => _undoStack.Count > 0 && CanEdit);
        RedoCommand = new RelayCommand(_ => Redo(), _ => _redoStack.Count > 0 && CanEdit);
        DeleteSelectionCommand = new RelayCommand(_ => DeleteSelection(), _ => CanEdit);
        ToggleRollModeCommand = new RelayCommand(_ => ToggleRollMode());
        CommitLyricCommand = new RelayCommand(_ => CommitLyric(), _ => CanEdit && ActiveLyric is not null);
        ApplyUnitFormantsCommand = new RelayCommand(_ => ApplyUnitFormants(), _ => CanEdit && SelectedUnit is not null);
        ClearUnitFormantsCommand = new RelayCommand(_ => ClearUnitFormants(), _ => CanEdit && SelectedUnit is not null);

        AddLyricCommand = new RelayCommand(_ => AddLyric(), _ => CanEdit);
        RemoveLyricCommand = new RelayCommand(_ => RemoveLyric(), _ => CanEdit && SelectedLyric is not null);
        DuplicateLyricCommand = new RelayCommand(_ => DuplicateLyric(), _ => CanEdit && SelectedLyric is not null);
        ToggleLyricFakeCommand = new RelayCommand(_ => ToggleLyricFake(), _ => CanEdit && SelectedLyric is not null);

        AddParameterSetCommand = new RelayCommand(_ => AddParameterSet(), _ => CanEdit);
        RemoveParameterSetCommand = new RelayCommand(_ => RemoveParameterSet(), _ => CanEdit && SelectedParameterSet is not null);
        DuplicateParameterSetCommand = new RelayCommand(_ => DuplicateParameterSet(), _ => CanEdit && SelectedParameterSet is not null);
        ToggleParameterFakeCommand = new RelayCommand(_ => ToggleParameterFake(), _ => CanEdit && SelectedParameterSet is not null);

        AddSingerCommand = new RelayCommand(_ => AddSinger(), _ => CanEdit);
        RemoveSingerCommand = new RelayCommand(_ => RemoveSinger(), _ => CanEdit && SelectedSinger is not null);
        DuplicateSingerCommand = new RelayCommand(_ => DuplicateSinger(), _ => CanEdit && SelectedSinger is not null);
        ToggleSingerFakeCommand = new RelayCommand(_ => ToggleSingerFake(), _ => CanEdit && SelectedSinger is not null);
        PurgeOrphansCommand = new RelayCommand(_ => PurgeOrphans(), _ => CanEdit);

        AddOutputNodeCommand = new RelayCommand(_ => AddOutputNode(), _ => CanEdit && ActiveGraph is not null);
        AddNodeCommand = new RelayCommand(_ => AddNode(NewNodeKind), _ => CanEdit && ActiveGraph is not null);
        RemoveNodeCommand = new RelayCommand(_ => RemoveSelectedNode(), _ => CanEdit && SelectedNode is not null);
        FrameGraphCommand = new RelayCommand(_ => FrameGraph(), _ => ActiveGraph is not null);

        RenderCommand = new AsyncRelayCommand(_ => RenderAsync(), _ => !IsBusy, ReportError);
        CancelRenderCommand = new RelayCommand(_ => _renderCts?.Cancel(), _ => IsBusy);
        PlayCommand = new RelayCommand(_ => TogglePlay());
        StopCommand = new RelayCommand(_ => StopPlayback());

        ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
        ImportLanguageCommand = new RelayCommand(_ => ImportLanguagePack());
        OpenHelpCommand = new RelayCommand(_ => OpenHelp());
        ImportBgmCommand = new RelayCommand(_ => ImportBgm(), _ => CanEdit);
        ClearBgmCommand = new RelayCommand(_ => ClearBgm(), _ => CanEdit && HasBgm);
        RefreshLibraryCommand = new RelayCommand(_ => RefreshLibrary());
        AddLibraryItemCommand = new RelayCommand(p => AddLibraryItem(p as LibraryEntry), _ => CanEdit);
        ImportDictionaryCommand = new RelayCommand(_ => ImportDictionary());
        InsertDictionaryEntryCommand = new RelayCommand(_ => InsertDictionaryEntry(),
            _ => CanEdit && _dictionary is not null && SelectedDictionaryEntry is not null);
        ExportLyricAssetCommand = new RelayCommand(_ => ExportLyricAsset(), _ => SelectedLyric is not null);
        ImportLyricAssetCommand = new RelayCommand(_ => ImportLyricAsset(), _ => CanEdit);
        ExportParameterAssetCommand = new RelayCommand(_ => ExportParameterAsset(), _ => SelectedParameterSet is not null);
        ImportParameterAssetCommand = new RelayCommand(_ => ImportParameterAsset(), _ => CanEdit);
        ExportTimbreCommand = new RelayCommand(_ => ExportTimbre(), _ => SelectedParameterSet is not null);
        ImportTimbreCommand = new RelayCommand(_ => ImportTimbre(), _ => CanEdit && SelectedParameterSet is not null);
        AnalyzeVoicebankCommand = new AsyncRelayCommand(_ => AnalyzeVoicebankAsync(),
            _ => SelectedSinger is not null && !IsBusy, ReportError);

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
        AddLog("info", "v3 对象库工程已就绪：歌词 / 参数 / 歌手三库，无音轨、无舞台。");
    }

    // ------------------------------------------------------------------
    // Collections
    // ------------------------------------------------------------------
    public ObservableCollection<LyricRow> Lyrics { get; } = [];
    public ObservableCollection<ParameterSetRow> ParameterSets { get; } = [];
    public ObservableCollection<SingerRow> Singers { get; } = [];
    public ObservableCollection<SegmentRow> Segments { get; } = [];
    public ObservableCollection<LyricUnitRow> LyricUnits { get; } = [];
    public ObservableCollection<ParameterKeyRow> ParameterKeyRows { get; } = [];
    public ObservableCollection<LibraryEntry> Library { get; } = [];
    public ObservableCollection<DictionaryEntryRow> DictionaryEntries { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ObservableCollection<VoicebankOption> Voicebanks { get; } = [];
    public ObservableCollection<LanguageRow> Languages { get; } = [];

    /// <summary>Node kinds offered by the pickers, in catalogue display order.</summary>
    public IReadOnlyList<NodeKind> NodeKinds { get; } = NodeCatalog.All.Select(d => d.Kind).ToList();

    private NodeKind _newNodeKind = NodeKind.TimeInput;

    /// <summary>Kind inserted by the toolbar's 添加节点 button.</summary>
    public NodeKind NewNodeKind
    {
        get => _newNodeKind;
        set
        {
            if (SetProperty(ref _newNodeKind, value))
                (AddNodeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public IReadOnlyList<string> MathFunctions { get; } =
        ["sin", "cos", "tan", "asin", "acos", "atan", "abs", "sqrt", "log", "log10", "exp",
         "floor", "ceil", "round", "sign", "negate", "smoothstep", "add", "subtract", "multiply",
         "divide", "pow", "min", "max", "mod", "atan2", "lerp"];

    public IReadOnlyList<string> GlobalVariables { get; } =
        ["time", "beat", "progress", "pitchHz", "pitch", "vowelIndex", "singerIndex", "singerCount", "frame"];

    public IReadOnlyList<string> BreathOutputs { get; } = ["gain", "threshold", "apScale", "apMax"];

    public IReadOnlyList<string> VowelChoices { get; } = ["", "a", "i", "u", "e", "o", "n", "-"];

    /// <summary>Fields the field-decompose node can read out of the voicebank bundle.</summary>
    public IReadOnlyList<VoicebankField> VoicebankFields { get; } = VoicebankField.All;

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------
    public ICommand NewProjectCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand SaveProjectCommand { get; }
    public ICommand LoadDemoCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand DeleteSelectionCommand { get; }
    public ICommand ToggleRollModeCommand { get; }
    public ICommand CommitLyricCommand { get; }
    public ICommand ApplyUnitFormantsCommand { get; }
    public ICommand ClearUnitFormantsCommand { get; }
    public ICommand AddLyricCommand { get; }
    public ICommand RemoveLyricCommand { get; }
    public ICommand DuplicateLyricCommand { get; }
    public ICommand ToggleLyricFakeCommand { get; }
    public ICommand AddParameterSetCommand { get; }
    public ICommand RemoveParameterSetCommand { get; }
    public ICommand DuplicateParameterSetCommand { get; }
    public ICommand ToggleParameterFakeCommand { get; }
    public ICommand AddSingerCommand { get; }
    public ICommand RemoveSingerCommand { get; }
    public ICommand DuplicateSingerCommand { get; }
    public ICommand ToggleSingerFakeCommand { get; }
    public ICommand PurgeOrphansCommand { get; }
    public ICommand AddOutputNodeCommand { get; }
    public ICommand AddNodeCommand { get; }
    public ICommand RemoveNodeCommand { get; }
    public ICommand FrameGraphCommand { get; }
    public ICommand RenderCommand { get; }
    public ICommand CancelRenderCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ImportLanguageCommand { get; }
    public ICommand OpenHelpCommand { get; }
    public ICommand ImportBgmCommand { get; }
    public ICommand ClearBgmCommand { get; }
    public ICommand RefreshLibraryCommand { get; }
    public ICommand AddLibraryItemCommand { get; }
    public ICommand ImportDictionaryCommand { get; }
    public ICommand InsertDictionaryEntryCommand { get; }
    public ICommand ExportLyricAssetCommand { get; }
    public ICommand ImportLyricAssetCommand { get; }
    public ICommand ExportParameterAssetCommand { get; }
    public ICommand ImportParameterAssetCommand { get; }
    public ICommand ExportTimbreCommand { get; }
    public ICommand ImportTimbreCommand { get; }
    public ICommand AnalyzeVoicebankCommand { get; }

    // ------------------------------------------------------------------
    // Project state
    // ------------------------------------------------------------------
    public bool CanEdit => !_project.IsLegacyReadOnly;

    public bool IsLegacyReadOnly => _project.IsLegacyReadOnly;

    public string ProjectPathText => _projectPath ?? LanguageService.Translate("（未保存）");

    public double Bpm
    {
        get => _project.Bpm;
        set
        {
            if (!CanEdit || Math.Abs(_project.Bpm - value) < 1e-9 || value <= 0)
                return;
            PushUndo();
            _project.Bpm = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BgmDurationBeats));
            _rollRevision++;
            OnPropertyChanged(nameof(RollRevision));
        }
    }

    public int BeatsPerBar
    {
        get => _project.BeatsPerBar;
        set
        {
            if (!CanEdit || _project.BeatsPerBar == value || value <= 0)
                return;
            PushUndo();
            _project.BeatsPerBar = value;
            OnPropertyChanged();
            _rollRevision++;
            OnPropertyChanged(nameof(RollRevision));
        }
    }

    public int BeatUnit
    {
        get => _project.BeatUnit;
        set
        {
            if (!CanEdit || _project.BeatUnit == value || value <= 0)
                return;
            PushUndo();
            _project.BeatUnit = value;
            OnPropertyChanged();
        }
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
            (AnalyzeVoicebankCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    public string StatusText
    {
        get => _statusFormat is null
            ? LanguageService.Translate(_statusText)
            : LanguageService.TranslateFormat(_statusFormat, _statusArgs ?? []);
        set
        {
            var hadFormat = _statusFormat is not null;
            _statusFormat = null;
            _statusArgs = null;
            if (hadFormat || !string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                _statusText = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Stores a localized status template; the getter translates it whenever the language changes.</summary>
    private void SetStatus(string format, params object?[] args)
    {
        _statusFormat = format;
        _statusArgs = args;
        OnPropertyChanged(nameof(StatusText));
    }

    public string PreviewInfo
    {
        get => _previewFormat is null
            ? LanguageService.Translate(_previewInfo)
            : LanguageService.TranslateFormat(_previewFormat, _previewArgs ?? []);
        private set
        {
            var hadFormat = _previewFormat is not null;
            _previewFormat = null;
            _previewArgs = null;
            if (hadFormat || !string.Equals(_previewInfo, value, StringComparison.Ordinal))
            {
                _previewInfo = value;
                OnPropertyChanged();
            }
        }
    }

    private void SetPreviewInfo(string format, params object?[] args)
    {
        _previewFormat = format;
        _previewArgs = args;
        OnPropertyChanged(nameof(PreviewInfo));
    }

    public string EngineInfo =>
        $"{Path.GetFileName(_services.Options.PythonPath)} · {Path.GetFileName(_services.Options.BridgeScriptPath)}";

    public string ThemeToggleText =>
        LanguageService.Translate(_isLightTheme ? "深色模式" : "浅色模式");

    // ------------------------------------------------------------------
    // Selection: lyrics / parameters / singers
    // ------------------------------------------------------------------
    public LyricRow? SelectedLyric
    {
        get => _selectedLyric;
        set
        {
            if (_syncingRows)
            {
                _selectedLyric = value;
                return;
            }
            if (!SetProperty(ref _selectedLyric, value))
                return;
            OnPropertyChanged(nameof(ActiveLyric));
            RefreshSegments();
            RefreshLyricUnits();
            (CommitLyricCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RemoveLyricCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DuplicateLyricCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ToggleLyricFakeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ExportLyricAssetCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public ParameterSetRow? SelectedParameterSet
    {
        get => _selectedParameterSet;
        set
        {
            if (_syncingRows)
            {
                _selectedParameterSet = value;
                return;
            }
            if (!SetProperty(ref _selectedParameterSet, value))
                return;
            OnPropertyChanged(nameof(ActiveParameterSet));
            OnPropertyChanged(nameof(GraphOwnerText));
            OnPropertyChanged(nameof(ActiveGraph));
            OnPropertyChanged(nameof(BaseValue));
            OnPropertyChanged(nameof(IsBaseEditable));
            RefreshParameterKeys();
            ResetNodeSelection();
            RaiseSelectionCommands();
        }
    }

    public SingerRow? SelectedSinger
    {
        get => _selectedSinger;
        set
        {
            if (_syncingRows)
            {
                _selectedSinger = value;
                return;
            }
            if (!SetProperty(ref _selectedSinger, value))
                return;
            var model = value?.Model;
            if (model is not null)
            {
                _selectedLyric = Lyrics.FirstOrDefault(r => r.Id == model.LyricId) ?? _selectedLyric;
                _selectedParameterSet = ParameterSets.FirstOrDefault(r => r.Id == model.ParameterSetId)
                    ?? _selectedParameterSet;
                OnPropertyChanged(nameof(SelectedLyric));
                OnPropertyChanged(nameof(SelectedParameterSet));
                OnPropertyChanged(nameof(ActiveLyric));
                OnPropertyChanged(nameof(ActiveParameterSet));
                OnPropertyChanged(nameof(SelectedSingerVoicebank));
                OnPropertyChanged(nameof(SelectedSingerLyric));
                OnPropertyChanged(nameof(SelectedSingerParameter));
                OnPropertyChanged(nameof(GraphOwnerText));
                OnPropertyChanged(nameof(ActiveGraph));
                RefreshSegments();
                RefreshLyricUnits();
                RefreshParameterKeys();
                ResetNodeSelection();
            }
            OnPropertyChanged(nameof(GraphOwnerText));
            OnPropertyChanged(nameof(ActiveGraph));
            OnPropertyChanged(nameof(BaseValue));
            OnPropertyChanged(nameof(IsBaseEditable));
            RaiseSelectionCommands();
        }
    }

    public LyricDocument? ActiveLyric =>
        _selectedSinger?.Model.LyricId is { } id
            ? _project.FindLyric(id)
            : _selectedLyric?.Model;

    public ParameterSet? ActiveParameterSet =>
        _selectedSinger?.Model.ParameterSetId is { } id
            ? _project.FindParameterSet(id)
            : _selectedParameterSet?.Model;

    public string SelectedSingerVoicebank
    {
        get => _selectedSinger?.Model.LibraryRoot ?? string.Empty;
        set
        {
            if (_selectedSinger is null || !CanEdit || _selectedSinger.Model.LibraryRoot == value)
                return;
            PushUndo();
            // Add the target before notifying the ComboBox: a two-way SelectedValue binding would otherwise
            // fail to find the new path in ItemsSource and write the old bank back.
            if (!Voicebanks.Any(v => string.Equals(v.Path, value, StringComparison.OrdinalIgnoreCase)))
            {
                Voicebanks.Insert(0, new VoicebankOption(
                    Path.GetFileName(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    value));
            }
            _selectedSinger.Model.LibraryRoot = value;
            _selectedSinger.Refresh();
            RefreshLibrary();
            OnPropertyChanged(nameof(SelectedSingerVoicebank));
        }
    }

    public LyricRow? SelectedSingerLyric
    {
        get => Lyrics.FirstOrDefault(r => r.Id == _selectedSinger?.Model.LyricId);
        set
        {
            if (_selectedSinger is null || !CanEdit || value is null)
                return;
            PushUndo();
            foreach (var other in _project.Singers.Where(s => s.Id != _selectedSinger.Id))
            {
                if (other.LyricId == value.Id)
                    other.LyricId = null;
            }
            _selectedSinger.Model.LyricId = value.Id;
            _selectedSinger.Refresh();
            _selectedLyric = value;
            OnPropertyChanged(nameof(SelectedSingerLyric));
            OnPropertyChanged(nameof(SelectedLyric));
            RefreshSegments();
            RefreshLyricUnits();
        }
    }

    public ParameterSetRow? SelectedSingerParameter
    {
        get => ParameterSets.FirstOrDefault(r => r.Id == _selectedSinger?.Model.ParameterSetId);
        set
        {
            if (_selectedSinger is null || !CanEdit || value is null)
                return;
            PushUndo();
            _selectedSinger.Model.ParameterSetId = value.Id;
            _selectedSinger.Refresh();
            _selectedParameterSet = value;
            OnPropertyChanged(nameof(SelectedSingerParameter));
            OnPropertyChanged(nameof(SelectedParameterSet));
            OnPropertyChanged(nameof(ActiveGraph));
            RefreshParameterKeys();
            ResetNodeSelection();
        }
    }

    public bool EditSingerGraph
    {
        get => _editSingerGraph;
        set
        {
            if (!SetProperty(ref _editSingerGraph, value))
                return;
            OnPropertyChanged(nameof(GraphOwnerText));
            OnPropertyChanged(nameof(ActiveGraph));
            OnPropertyChanged(nameof(BaseValue));
            RefreshParameterKeys();
            ResetNodeSelection();
        }
    }

    /// <summary>The node canvas currently shown: the shared parameter set or the singer overlay.</summary>
    public ParameterNodeGraph? ActiveGraph => _editSingerGraph
        ? _selectedSinger?.Model.Graph
        : ActiveParameterSet?.Graph;

    public string GraphOwnerText => _editSingerGraph
        ? LanguageService.TranslateFormat("歌手「{0}」的叠加节点图",
            _selectedSinger?.Name ?? LanguageService.Translate("未选择"))
        : LanguageService.TranslateFormat("共享参数「{0}」的节点图",
            ActiveParameterSet?.Name ?? LanguageService.Translate("未选择"));

    public int GraphRevision => _graphRevision;

    /// <summary>Bumped to ask the canvas to fit every node into the viewport.</summary>
    public int FrameRevision => _frameRevision;

    private int _frameRevision;

    // ------------------------------------------------------------------
    // Lyric editor
    // ------------------------------------------------------------------
    public string LyricInput
    {
        get => _lyricInput;
        set => SetProperty(ref _lyricInput, value);
    }

    public string LyricStatus
    {
        get => _lyricStatusFormat is null
            ? LanguageService.Translate(_lyricStatus)
            : LanguageService.TranslateFormat(_lyricStatusFormat, _lyricStatusArgs ?? []);
        set
        {
            var hadFormat = _lyricStatusFormat is not null;
            _lyricStatusFormat = null;
            _lyricStatusArgs = null;
            if (hadFormat || !string.Equals(_lyricStatus, value, StringComparison.Ordinal))
            {
                _lyricStatus = value;
                OnPropertyChanged();
            }
        }
    }

    private void SetLyricStatus(string format, params object?[] args)
    {
        _lyricStatusFormat = format;
        _lyricStatusArgs = args;
        OnPropertyChanged(nameof(LyricStatus));
    }

    public SegmentRow? SelectedSegment
    {
        get => _selectedSegment;
        set
        {
            if (_syncingRows)
            {
                _selectedSegment = value;
                return;
            }
            if (!SetProperty(ref _selectedSegment, value))
                return;
            var index = value is null ? (int?)null : Segments.IndexOf(value);
            if (_selectedNoteIndex != index)
            {
                _selectedNoteIndex = index;
                OnPropertyChanged(nameof(SelectedNoteIndex));
            }
            (DeleteSelectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public LyricUnitRow? SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (_syncingRows)
            {
                _selectedUnit = value;
                return;
            }
            if (!SetProperty(ref _selectedUnit, value))
                return;
            OnPropertyChanged(nameof(UnitVowel));
            OnPropertyChanged(nameof(UnitFormantText));
            OnPropertyChanged(nameof(UnitFormantInfo));
            OnPropertyChanged(nameof(UnitFormantValues));
            OnPropertyChanged(nameof(UnitFormantHint));
            OnPropertyChanged(nameof(UnitUsesAbsoluteAudio));
            _unitFormantText = value?.Model.FormantHz is { Length: 7 } hz
                ? string.Join(", ", hz.Select(v => v.ToString("0")))
                : string.Empty;
            OnPropertyChanged(nameof(UnitFormantText));
        }
    }

    public string UnitVowel
    {
        get => _selectedUnit?.Model.Vowel ?? "a";
        set
        {
            if (_selectedUnit is null || !CanEdit || string.IsNullOrWhiteSpace(value))
                return;
            PushUndo();
            _selectedUnit.Model.Vowel = value;
            var segment = ActiveLyric?.Segments.FirstOrDefault(s => s.Id == _selectedUnit.Model.SegmentId);
            if (segment is not null)
                segment.Vowel = value;
            _selectedUnit.Refresh();
            RefreshSegments();
            OnPropertyChanged();
            OnPropertyChanged(nameof(UnitFormantInfo));
            OnPropertyChanged(nameof(UnitFormantValues));
            OnPropertyChanged(nameof(UnitFormantHint));
        }
    }

    public string UnitFormantText
    {
        get => _unitFormantText;
        set => SetProperty(ref _unitFormantText, value);
    }

    /// <summary>Per-unit dual-mode override: use the stored absolute audio path instead of the voicebank.</summary>
    public bool UnitUsesAbsoluteAudio
    {
        get => _selectedUnit?.Model.PreferAbsoluteAudio ?? false;
        set
        {
            if (_selectedUnit is null || !CanEdit || _selectedUnit.Model.PreferAbsoluteAudio == value)
                return;
            PushUndo();
            _selectedUnit.Model.PreferAbsoluteAudio = value;
            var segment = ActiveLyric?.Segments.FirstOrDefault(s => s.Id == _selectedUnit.Model.SegmentId);
            if (segment is not null)
                segment.PreferAbsoluteAudio = value;
            _selectedUnit.Refresh();
            OnPropertyChanged();
            AddLog("info", value ? "该音节改为绝对样本路径。" : "该音节改为声库解析。");
        }
    }

    public string UnitFormantInfo    {
        get
        {
            if (_selectedUnit is null)
                return LanguageService.Translate("选择一个音节查看共振峰。");
            var profile = VoicebankFormantProfile.Load(_selectedSinger?.Model.LibraryRoot);
            var values = _selectedUnit.Model.FormantHz is { Length: 7 } manual
                ? manual
                : profile.Resolve(_selectedUnit.Model.Vowel, _selectedUnit.Model.Phoneme);
            var source = _selectedUnit.Model.FormantHz is { Length: 7 }
                ? LanguageService.Translate("手动目标")
                : LanguageService.Translate("元音管理器（自动）");
            return LanguageService.TranslateFormat("{0}：{1} Hz", source,
                string.Join(" / ", values.Select(v => v.ToString("0"))));
        }
    }

    /// <summary>Seven formant targets for the graphical editor; empty when no syllable is selected.</summary>
    public IReadOnlyList<double> UnitFormantValues
    {
        get
        {
            if (_selectedUnit is null)
                return [];
            if (_selectedUnit.Model.FormantHz is { Length: 7 } manual)
                return manual;
            var profile = VoicebankFormantProfile.Load(_selectedSinger?.Model.LibraryRoot);
            return profile.Resolve(_selectedUnit.Model.Vowel, _selectedUnit.Model.Phoneme);
        }
    }

    public string UnitFormantHint => _selectedUnit?.Model.FormantHz is { Length: 7 }
        ? LanguageService.Translate("手动目标")
        : _selectedUnit is null
            ? LanguageService.Translate("未选择音节")
            : LanguageService.Translate("自动（元音管理器）");

    public PianoRollMode RollMode
    {
        get => _rollMode;
        private set
        {
            if (!SetProperty(ref _rollMode, value))
                return;
            OnPropertyChanged(nameof(RollModeText));
        }
    }

    public string RollModeText => RollMode == PianoRollMode.Notes
        ? LanguageService.Translate("模式：音块（右键加块）")
        : LanguageService.Translate("模式：Pitch 线（右键加节点）");

    public bool IsPencil
    {
        get => _isPencil;
        set => SetProperty(ref _isPencil, value);
    }

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

    /// <summary>Grid subdivisions per bar: 0 = off, then 8 / 12 / 16 / 32.</summary>
    public int GridDivision => _gridDivisionIndex switch
    {
        1 => 8,
        2 => 12,
        3 => 16,
        4 => 32,
        _ => 0,
    };

    public int RollRevision => _rollRevision;

    public int? SelectedNoteIndex
    {
        get => _selectedNoteIndex;
        set
        {
            if (!SetProperty(ref _selectedNoteIndex, value))
                return;
            if (_syncingRows)
                return;
            if (value is int index && index >= 0 && index < Segments.Count)
                _selectedSegment = Segments[index];
            else
                _selectedSegment = null;
            OnPropertyChanged(nameof(SelectedSegment));
        }
    }

    public int? SelectedPitchIndex
    {
        get => _selectedPitchIndex;
        set => SetProperty(ref _selectedPitchIndex, value);
    }

    public double? PlayheadBeat
    {
        get => _playheadBeat;
        set => SetProperty(ref _playheadBeat, value);
    }

    public bool HasSelectedSegment => _selectedSegment is not null;

    public bool HasBgm => !string.IsNullOrWhiteSpace(_project.BgmPath);

    public string? BgmPath => _project.BgmPath;

    public string BgmName => HasBgm
        ? Path.GetFileName(_project.BgmPath!) ?? LanguageService.Translate("未导入")
        : LanguageService.Translate("未导入");

    public double BgmStartBeat
    {
        get => _project.BgmStartBeat;
        set
        {
            if (!CanEdit || Math.Abs(_project.BgmStartBeat - value) < 1e-9)
                return;
            PushUndo();
            _project.BgmStartBeat = Math.Max(0, value);
            _bgmMixKey = null;
            OnPropertyChanged();
        }
    }

    public double BgmDurationBeats
    {
        get
        {
            if (!HasBgm || !File.Exists(_project.BgmPath))
                return 0;
            try
            {
                var info = WavReader.ReadInfo(_project.BgmPath);
                return info.DurationSeconds * _project.Bpm / 60.0;
            }
            catch (Exception ex)
            {
                AddLog("warn", "读取 BGM 失败：" + ex.Message);
                return 0;
            }
        }
    }

    public void AddNoteAt(double beat, int semitone) =>
        CreateSegmentAt(beat, beat + 1.0, semitone);

    public void DrawNoteAt(double beatStart, double beatEnd, int semitone) =>
        CreateSegmentAt(beatStart, Math.Max(beatStart + 0.25, beatEnd), semitone);

    public void OnRollEdited()
    {
        if (ActiveLyric is null)
            return;
        LyricPhraseComposer.SyncWithSegments(ActiveLyric.Phrase, ActiveLyric);
        _rollRevision++;
        OnPropertyChanged(nameof(RollRevision));
        RefreshSegments();
        RefreshLyricUnits();
        AddLog("info", "Piano roll edit committed; lyric units synchronized.");
    }

    /// <summary>
    /// Moves the playback position to the given beat. This is a seek, not a play command: starting
    /// playback here would let the 33 ms position timer keep writing PlayheadBeat while the mouse is
    /// still dragging, so the playhead would snap back to the audio position and refuse to be dragged.
    /// The next Play starts from this beat.
    /// </summary>
    public void SeekToBeat(double beat)
    {
        if (IsPlaying || _services.Playback.IsPlaying)
            StopPlayback();
        PlayheadBeat = beat;
    }

    // ------------------------------------------------------------------
    // Parameter editor
    // ------------------------------------------------------------------
    public ParameterKeyRow? SelectedParameterKey
    {
        get => _selectedParameterKey;
        set
        {
            if (_syncingRows)
            {
                _selectedParameterKey = value;
                return;
            }
            if (!SetProperty(ref _selectedParameterKey, value))
                return;
            OnPropertyChanged(nameof(BaseValue));
            OnPropertyChanged(nameof(IsBaseEditable));
            SelectOutputNodeFor(value);
        }
    }

    /// <summary>The node selected on the canvas; drives the inspector below it.</summary>
    public ParameterNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (_syncingRows)
            {
                _selectedNode = value;
                return;
            }
            if (!SetProperty(ref _selectedNode, value))
                return;
            _selectedNodeRow = value is null || ActiveGraph is null
                ? null
                : new NodeRow(value, ActiveGraph, OnNodeChanged);
            OnPropertyChanged(nameof(SelectedNodeRow));
            (RemoveNodeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public NodeRow? SelectedNodeRow => _selectedNodeRow;

    public bool IsBaseEditable => _selectedParameterKey is { Definition.IsFormant: false };

    public double BaseValue
    {
        get => ActiveParameterSet is { } set && _selectedParameterKey is { } key
            ? set.GetBase(key.Key)
            : 0;
        set
        {
            if (!CanEdit || !IsBaseEditable || ActiveParameterSet is null || _selectedParameterKey is null)
                return;
            var clamped = Math.Clamp(value, _selectedParameterKey.Definition.Min,
                _selectedParameterKey.Definition.Max);
            if (Math.Abs(clamped - ActiveParameterSet.GetBase(_selectedParameterKey.Key)) < 1e-9)
                return;
            PushUndo();
            ActiveParameterSet.SetBase(_selectedParameterKey.Key, clamped);
            OnPropertyChanged();
            OnPropertyChanged(nameof(BaseValueText));
            _rollRevision++;
            OnPropertyChanged(nameof(RollRevision));
        }
    }

    public string BaseValueText => BaseValue.ToString("0.###");

    // ------------------------------------------------------------------
    // Library / dictionary / logs
    // ------------------------------------------------------------------
    public LibraryEntry? SelectedLibraryItem
    {
        get => _selectedLibraryItem;
        set => SetProperty(ref _selectedLibraryItem, value);
    }

    public DictionaryEntryRow? SelectedDictionaryEntry
    {
        get => _selectedDictionaryEntry;
        set
        {
            if (!SetProperty(ref _selectedDictionaryEntry, value))
                return;
            (InsertDictionaryEntryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public double DictionaryStartBeat
    {
        get => _dictionaryStartBeat;
        set => SetProperty(ref _dictionaryStartBeat, value);
    }

    public double DictionaryBeats
    {
        get => _dictionaryBeats;
        set => SetProperty(ref _dictionaryBeats, value);
    }

    public string DictionaryDstPitch
    {
        get => _dictionaryDstPitch;
        set => SetProperty(ref _dictionaryDstPitch, value);
    }

    public string DictionaryName => LanguageService.Translate(_dictionary?.Name ?? "内置字典");

    public string DictionaryPathText => _dictionaryPath ?? LanguageService.Translate("（内置）");

    public string LibraryRoot => _selectedSinger?.Model.LibraryRoot ?? _services.Options.LibraryRoot;

    public ImageSource? LibraryIcon => _libraryIcon;

    public bool HasLibraryIcon => _libraryIcon is not null;

    public ObservableCollection<LanguageRow> AvailableLanguages => Languages;

    public LanguageRow? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!_languageReady || value is null)
                return;
            if (!SetProperty(ref _selectedLanguage, value))
                return;
            LanguageService.Select(value.Pack.Code);
            if (Application.Current?.MainWindow is { } window)
                LanguageService.Apply(window);
            RefreshLanguageText();
        }
    }

    public void BeginLanguageSelection()
    {
        var row = Languages.FirstOrDefault(r => r.Pack.Code == LanguageService.Current.Code)
            ?? Languages.FirstOrDefault();
        _selectedLanguage = row;
        OnPropertyChanged(nameof(SelectedLanguage));
        RefreshLanguageText();
        _languageReady = true;
    }

    // ------------------------------------------------------------------
    // Project load / save
    // ------------------------------------------------------------------
    private void NewProject()
    {
        LoadProject(ProjectFactory.CreateEmpty(_services.Options.LibraryRoot), null);
        AddLog("info", "新建空对象工程。");
    }

    private void OpenProject()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开工程",
            Filter = $"VocalB1 工程 (*{ProjectJson.FileExtension})|*{ProjectJson.FileExtension}|JSON (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var project = ProjectJson.Load(dialog.FileName, Path.GetDirectoryName(dialog.FileName));
            LoadProject(project, dialog.FileName);
            AddLog(project.IsLegacyReadOnly ? "warn" : "info",
                project.IsLegacyReadOnly
                    ? "旧版 v2 工程已加载为只读：可查看和渲染，不能编辑或保存。"
                    : LanguageService.TranslateFormat("工程已打开：{0}", dialog.FileName));
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void SaveProject()
    {
        if (!CanEdit)
        {
            MessageBox.Show("旧版 v2 工程为只读，不能保存。请新建 v3 工程。", "只读工程",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var path = _projectPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存工程",
                Filter = $"VocalB1 工程 (*{ProjectJson.FileExtension})|*{ProjectJson.FileExtension}",
                FileName = _project.Name + ProjectJson.FileExtension,
            };
            if (dialog.ShowDialog() != true)
                return;
            path = dialog.FileName;
        }

        try
        {
            ProjectJson.Save(_project, path, Path.GetDirectoryName(path));
            _projectPath = path;
            _savedSnapshot = ProjectJson.Serialize(_project, Path.GetDirectoryName(path));
            OnPropertyChanged(nameof(ProjectPathText));
            SetStatus("已保存：{0}", Path.GetFileName(path));
            AddLog("info", LanguageService.TranslateFormat("工程已保存：{0}", path));
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    public bool ConfirmDiscardChanges(string action)
    {
        if (!CanEdit || string.IsNullOrWhiteSpace(_projectPath))
            return true;
        var current = ProjectJson.Serialize(_project, Path.GetDirectoryName(_projectPath));
        if (string.Equals(current, _savedSnapshot, StringComparison.Ordinal))
            return true;

        return MessageBox.Show($"工程有未保存的修改，仍要{action}吗？", "未保存的修改",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    public void OpenProjectFromPath(string path)
    {
        try
        {
            var project = ProjectJson.Load(path, Path.GetDirectoryName(path));
            LoadProject(project, path);
            AddLog(project.IsLegacyReadOnly ? "warn" : "info",
                project.IsLegacyReadOnly ? "旧版工程已加载为只读。"
                    : LanguageService.TranslateFormat("工程已打开：{0}", path));
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void LoadDemo()
    {
        var demo = ProjectFactory.CreateDemo(_services.Options.LibraryRoot);
        if (demo.Lyrics.Count == 0 || demo.Lyrics[0].Segments.Count == 0)
        {
            AddLog("warn", "示例音频缺失，无法加载示例工程。");
            StatusText = "示例音频缺失";
            return;
        }
        LoadProject(demo, null);
        AddLog("info", "示例工程已加载。");
    }

    private void LoadProject(ProjectDocument project, string? path)
    {
        _project = project;
        _projectPath = path;
        _undoStack.Clear();
        _redoStack.Clear();
        _lastMixPath = null;
        _bgmMixPath = null;
        _bgmMixKey = null;
        _selectedLyric = null;
        _selectedParameterSet = null;
        _selectedSinger = null;
        _selectedSegment = null;
        _selectedUnit = null;
        _selectedParameterKey = null;
        _selectedNode = null;
        _selectedNodeRow = null;
        _savedSnapshot = path is null ? string.Empty : ProjectJson.Serialize(project, Path.GetDirectoryName(path));

        RebuildRows();
        RefreshLibrary();
        OnPropertyChanged(nameof(Bpm));
        OnPropertyChanged(nameof(BeatsPerBar));
        OnPropertyChanged(nameof(BeatUnit));
        OnPropertyChanged(nameof(IsLegacyReadOnly));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(ProjectPathText));
        OnPropertyChanged(nameof(HasBgm));
        OnPropertyChanged(nameof(BgmName));
        OnPropertyChanged(nameof(BgmDurationBeats));
        RaiseAllCommandStates();

        if (_project.IsLegacyReadOnly)
            StatusText = "旧版工程（只读）：可渲染，不可编辑或保存";
        else
            StatusText = "就绪";
    }

    private void RebuildRows()
    {
        var lyricId = _selectedLyric?.Id;
        var parameterId = _selectedParameterSet?.Id;
        var singerId = _selectedSinger?.Id;

        Lyrics.Clear();
        foreach (var lyric in _project.Lyrics)
            Lyrics.Add(new LyricRow(lyric));

        ParameterSets.Clear();
        foreach (var parameters in _project.ParameterSets)
            ParameterSets.Add(new ParameterSetRow(parameters));

        Singers.Clear();
        foreach (var singer in _project.Singers)
            Singers.Add(new SingerRow(singer, ResolveLyricName, ResolveParameterName, RefreshAllDetails));

        _selectedSinger = Singers.FirstOrDefault(r => r.Id == singerId) ?? Singers.FirstOrDefault();
        _selectedLyric = Lyrics.FirstOrDefault(r => r.Id == lyricId)
            ?? Lyrics.FirstOrDefault(r => r.Id == _selectedSinger?.Model.LyricId)
            ?? Lyrics.FirstOrDefault();
        _selectedParameterSet = ParameterSets.FirstOrDefault(r => r.Id == parameterId)
            ?? ParameterSets.FirstOrDefault(r => r.Id == _selectedSinger?.Model.ParameterSetId)
            ?? ParameterSets.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedSinger));
        OnPropertyChanged(nameof(SelectedLyric));
        OnPropertyChanged(nameof(SelectedParameterSet));
        OnPropertyChanged(nameof(ActiveLyric));
        OnPropertyChanged(nameof(ActiveParameterSet));
        OnPropertyChanged(nameof(SelectedSingerLyric));
        OnPropertyChanged(nameof(SelectedSingerParameter));
        OnPropertyChanged(nameof(SelectedSingerVoicebank));
        OnPropertyChanged(nameof(GraphOwnerText));
        OnPropertyChanged(nameof(ActiveGraph));
        OnPropertyChanged(nameof(LibraryRoot));

        RefreshParameterKeys();
        RefreshSegments();
        RefreshLyricUnits();
        ResetNodeSelection();
    }

    private string? ResolveLyricName(string? id) => _project.FindLyric(id)?.Name;

    private string? ResolveParameterName(string? id) => _project.FindParameterSet(id)?.Name;

    private void RefreshAllDetails()
    {
        foreach (var row in Lyrics)
            row.Refresh();
        foreach (var row in ParameterSets)
            row.Refresh();
        foreach (var row in Singers)
            row.Refresh();
    }

    // ------------------------------------------------------------------
    // Undo / redo
    // ------------------------------------------------------------------
    private void PushUndo()
    {
        if (!CanEdit)
            return;
        _undoStack.Add(_project.Clone());
        if (_undoStack.Count > MaxUndoSteps)
            _undoStack.RemoveAt(0);
        _redoStack.Clear();
        (UndoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RedoCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
            return;
        _redoStack.Add(_project.Clone());
        _project = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RebuildRows();
        RefreshLibrary();
        RaiseAllCommandStates();
        AddLog("info", "撤销。");
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
            return;
        _undoStack.Add(_project.Clone());
        _project = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        RebuildRows();
        RefreshLibrary();
        RaiseAllCommandStates();
        AddLog("info", "重做。");
    }

    // ------------------------------------------------------------------
    // Lyric data-block commands
    // ------------------------------------------------------------------
    private void AddLyric()
    {
        PushUndo();
        var lyric = ProjectFactory.CreateLyric(
            ProjectDocument.UniqueName("歌词", _project.Lyrics.Select(l => l.Name)));
        _project.Lyrics.Add(lyric);
        RebuildRows();
        SelectedLyric = Lyrics.FirstOrDefault(r => r.Id == lyric.Id);
        AddLog("info", $"新建歌词「{lyric.Name}」（孤立数据块，需绑定到歌手）。");
    }

    private void RemoveLyric()
    {
        var row = SelectedLyric;
        if (row is null)
            return;
        var users = _project.LyricUsers(row.Id);
        if (users > 0)
        {
            MessageBox.Show($"歌词「{row.Name}」仍被 {users} 个歌手引用。请先解除绑定。", "无法删除",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PushUndo();
        _project.Lyrics.Remove(row.Model);
        RebuildRows();
        AddLog("info", $"删除歌词「{row.Name}」。");
    }

    private void DuplicateLyric()
    {
        var row = SelectedLyric;
        if (row is null)
            return;
        PushUndo();
        var copy = row.Model.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = ProjectDocument.UniqueName(row.Name + " 副本", _project.Lyrics.Select(l => l.Name));
        _project.Lyrics.Add(copy);
        RebuildRows();
        SelectedLyric = Lyrics.FirstOrDefault(r => r.Id == copy.Id);
    }

    private void ToggleLyricFake()
    {
        var row = SelectedLyric;
        if (row is null)
            return;
        PushUndo();
        row.Model.FakeUser = !row.Model.FakeUser;
        row.Refresh();
        AddLog("info", $"歌词「{row.Name}」伪用户 = {row.Model.FakeUser}。");
    }

    private void CommitLyric()
    {
        var lyric = ActiveLyric;
        if (lyric is null || _dictionary is null)
            return;

        try
        {
            var result = LyricPhraseParser.Parse(
                _dictionary,
                LyricInput,
                string.IsNullOrWhiteSpace(_dictionaryPath) ? null : Path.GetDirectoryName(_dictionaryPath),
                ResolveDictionaryLibraryRoot());

            PushUndo();
            var transition = Math.Round(0.25 * FramesPerBeatConstant / Math.Max(1, _project.Bpm));
            var applied = LyricPhraseComposer.Apply(lyric.Phrase, result.Words, lyric,
                DefaultLyricUnitBeats, transition, LyricBlockPitch);
            lyric.Name = string.IsNullOrWhiteSpace(lyric.Name) || lyric.Name.StartsWith("歌词", StringComparison.Ordinal)
                ? LyricInput.Trim()
                : lyric.Name;

            foreach (var missing in result.MissingAudio)
                AddLog("warn", "缺少音频：" + missing);
            foreach (var unmatched in result.Unmatched)
                AddLog("warn", "字典未收录：" + unmatched);

            if (result.MissingAudio.Count > 0)
                SetLyricStatus("已应用：复用 {0}，新增 {1}，移除 {2}，缺音频 {3}",
                    applied.Reused, applied.Added, applied.Removed, result.MissingAudio.Count);
            else
                SetLyricStatus("已应用：复用 {0}，新增 {1}，移除 {2}",
                    applied.Reused, applied.Added, applied.Removed);
            AddLog("info", LyricStatus);
            _rollRevision++;
            OnPropertyChanged(nameof(RollRevision));
            RebuildRows();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void CreateSegmentAt(double beatStart, double beatEnd, int semitone)
    {
        var lyric = ActiveLyric;
        if (lyric is null || !CanEdit)
            return;

        var entry = SelectedLibraryItem ?? Library.FirstOrDefault();
        var path = entry?.Path;
        if (path is null)
        {
            var dialog = new OpenFileDialog
            {
                Title = "为新音符选择音频",
                Filter = "WAV 音频 (*.wav)|*.wav|所有文件 (*.*)|*.*",
            };
            if (dialog.ShowDialog() != true)
                return;
            path = dialog.FileName;
        }

        PushUndo();
        // Notes may never overlap: a requested range that hits an existing note slides past it instead.
        var length = Math.Max(0.25, beatEnd - beatStart);
        var start = SegmentLayout.FindFreeStart(lyric, beatStart, length);
        var segment = new VoiceSegment
        {
            Audio = path,
            SrcPitch = VoiceSegment.DefaultSrcPitch,
            DstPitch = PitchMath.ToPitchName(semitone),
            BeatStart = start,
            BeatEnd = start + length,
            Transition = lyric.Segments.Count > 0
                ? Math.Round(0.25 * FramesPerBeatConstant / Math.Max(1, _project.Bpm))
                : 0,
            Phoneme = entry?.DisplayName ?? Path.GetFileNameWithoutExtension(path),
            Vowel = GuessVowel(entry?.DisplayName ?? Path.GetFileNameWithoutExtension(path)),
        };
        lyric.Segments.Add(segment);
        lyric.Segments.Sort((a, b) => a.BeatStart.CompareTo(b.BeatStart));
        lyric.AddPitchNodes(segment, 5);
        LyricPhraseComposer.SyncWithSegments(lyric.Phrase, lyric);

        _rollRevision++;
        OnPropertyChanged(nameof(RollRevision));
        RefreshSegments();
        RefreshLyricUnits();
        AddLog("info", $"新增发音段：{segment.Phoneme}，{segment.BeatStart:0.##} → {segment.BeatEnd:0.##} 拍"
            + (Math.Abs(segment.BeatStart - beatStart) > 1e-6 ? "（避开已有音符，已后移）" : string.Empty) + "。");
    }

    private void DeleteSelection()
    {
        if (!CanEdit)
            return;

        if (_selectedSegment is { } segment && ActiveLyric is { } lyric)
        {
            PushUndo();
            lyric.Segments.Remove(segment.Model);
            lyric.PitchLine.RemoveAll(p => p.SegmentId == segment.Model.Id);
            foreach (var word in lyric.Phrase.Words)
            {
                foreach (var unit in word.Units.Where(u => u.SegmentId == segment.Model.Id))
                {
                    unit.IsRest = true;
                    unit.SegmentId = null;
                    unit.Audio = string.Empty;
                    unit.Display = "休止";
                }
            }
            _rollRevision++;
            OnPropertyChanged(nameof(RollRevision));
            RefreshSegments();
            RefreshLyricUnits();
            return;
        }

        if (_selectedUnit is { } unitRow)
        {
            PushUndo();
            unitRow.Model.IsRest = true;
            unitRow.Model.SegmentId = null;
            unitRow.Model.Audio = string.Empty;
            unitRow.Model.Display = "休止";
            RefreshLyricUnits();
        }
    }

    private void RefreshSegments()
    {
        _syncingRows = true;
        var selectedId = _selectedSegment?.Model.Id;
        Segments.Clear();
        var lyric = ActiveLyric;
        if (lyric is not null)
        {
            for (var i = 0; i < lyric.Segments.Count; i++)
                Segments.Add(new SegmentRow(lyric.Segments[i], i));
        }
        _selectedSegment = Segments.FirstOrDefault(r => r.Model.Id == selectedId)
            ?? Segments.FirstOrDefault();
        _syncingRows = false;
        OnPropertyChanged(nameof(SelectedSegment));
        OnPropertyChanged(nameof(HasSelectedSegment));
        if (_selectedSegment is not null)
        {
            _selectedNoteIndex = Segments.IndexOf(_selectedSegment);
            OnPropertyChanged(nameof(SelectedNoteIndex));
        }
    }

    private void RefreshLyricUnits()
    {
        _syncingRows = true;
        var selectedId = _selectedUnit?.Id;
        LyricUnits.Clear();
        var lyric = ActiveLyric;
        if (lyric is not null)
        {
            foreach (var unit in lyric.Phrase.Words.SelectMany(w => w.Units))
                LyricUnits.Add(new LyricUnitRow(unit));
        }
        _selectedUnit = LyricUnits.FirstOrDefault(r => r.Id == selectedId)
            ?? LyricUnits.FirstOrDefault();
        _syncingRows = false;
        OnPropertyChanged(nameof(SelectedUnit));
        // The row list is rebuilt on load/undo, so the editor fields must resync from the new selection.
        _unitFormantText = _selectedUnit?.Model.FormantHz is { Length: 7 } hz
            ? string.Join(", ", hz.Select(v => v.ToString("0")))
            : string.Empty;
        OnPropertyChanged(nameof(UnitVowel));
        OnPropertyChanged(nameof(UnitFormantText));
        OnPropertyChanged(nameof(UnitFormantInfo));
        OnPropertyChanged(nameof(UnitFormantValues));
        OnPropertyChanged(nameof(UnitFormantHint));
        OnPropertyChanged(nameof(UnitUsesAbsoluteAudio));
    }

    private void ApplyUnitFormants()
    {
        if (_selectedUnit is null)
            return;

        var parts = UnitFormantText.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 7)
        {
            ReportError(new StageException(nameof(MainViewModel), "apply-formants",
                $"需要 7 个共振峰目标，实际 {parts.Length} 个。"));
            return;
        }

        var values = new double[7];
        for (var i = 0; i < 7; i++)
        {
            if (!double.TryParse(parts[i], out var value) || !double.IsFinite(value) || value <= 0)
            {
                ReportError(new StageException(nameof(MainViewModel), "apply-formants",
                    $"F{i + 1} 不是有效正数：{parts[i]}"));
                return;
            }
            if (i > 0 && value <= values[i - 1])
            {
                ReportError(new StageException(nameof(MainViewModel), "apply-formants",
                    $"F{i + 1} 必须大于 F{i}。"));
                return;
            }
            values[i] = value;
        }

        PushUndo();
        _selectedUnit.Model.FormantHz = values;
        var segment = ActiveLyric?.Segments.FirstOrDefault(s => s.Id == _selectedUnit.Model.SegmentId);
        if (segment is not null)
            segment.FormantHz = values.ToArray();
        _selectedUnit.Refresh();
        RefreshSegments();
        OnPropertyChanged(nameof(UnitFormantInfo));
        AddLog("info", $"音节「{_selectedUnit.Display}」共振峰目标已设置。");
    }

    private void ClearUnitFormants()
    {
        if (_selectedUnit is null)
            return;
        PushUndo();
        _selectedUnit.Model.FormantHz = null;
        var segment = ActiveLyric?.Segments.FirstOrDefault(s => s.Id == _selectedUnit.Model.SegmentId);
        if (segment is not null)
            segment.FormantHz = null;
        UnitFormantText = string.Empty;
        _selectedUnit.Refresh();
        RefreshSegments();
        OnPropertyChanged(nameof(UnitFormantInfo));
        AddLog("info", "已恢复为元音管理器自动控制。");
    }

    // ------------------------------------------------------------------
    // Parameter data-block commands
    // ------------------------------------------------------------------
    private void AddParameterSet()
    {
        PushUndo();
        var parameters = ProjectFactory.CreateParameterSet(
            ProjectDocument.UniqueName("参数", _project.ParameterSets.Select(p => p.Name)));
        _project.ParameterSets.Add(parameters);
        RebuildRows();
        SelectedParameterSet = ParameterSets.FirstOrDefault(r => r.Id == parameters.Id);
        AddLog("info", $"新建参数「{parameters.Name}」。");
    }

    private void RemoveParameterSet()
    {
        var row = SelectedParameterSet;
        if (row is null)
            return;
        var users = _project.ParameterSetUsers(row.Id);
        if (users > 0)
        {
            MessageBox.Show($"参数「{row.Name}」仍被 {users} 个歌手引用。请先解除绑定。", "无法删除",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PushUndo();
        _project.ParameterSets.Remove(row.Model);
        RebuildRows();
    }

    private void DuplicateParameterSet()
    {
        var row = SelectedParameterSet;
        if (row is null)
            return;
        PushUndo();
        var copy = row.Model.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = ProjectDocument.UniqueName(row.Name + " 副本", _project.ParameterSets.Select(p => p.Name));
        _project.ParameterSets.Add(copy);
        RebuildRows();
        SelectedParameterSet = ParameterSets.FirstOrDefault(r => r.Id == copy.Id);
    }

    private void ToggleParameterFake()
    {
        var row = SelectedParameterSet;
        if (row is null)
            return;
        PushUndo();
        row.Model.FakeUser = !row.Model.FakeUser;
        row.Refresh();
    }

    private void RefreshParameterKeys()
    {
        _syncingRows = true;
        var selectedKey = _selectedParameterKey?.Key;
        ParameterKeyRows.Clear();
        var outputs = ActiveGraph?.WrittenKeys() ?? [];
        var definitions = ParameterKeys.All.OrderBy(d => d.IsFormant ? 0 : 1)
            .ThenBy(d => d.Key.StartsWith("formant.F", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(d => d.Key);
        foreach (var definition in definitions)
        {
            var row = new ParameterKeyRow(definition);
            row.Refresh(outputs.Contains(definition.Key, StringComparer.Ordinal));
            ParameterKeyRows.Add(row);
        }
        _selectedParameterKey = ParameterKeyRows.FirstOrDefault(k => k.Key == selectedKey)
            ?? ParameterKeyRows.FirstOrDefault();
        _syncingRows = false;
        OnPropertyChanged(nameof(SelectedParameterKey));
        OnPropertyChanged(nameof(BaseValue));
        OnPropertyChanged(nameof(IsBaseEditable));
    }

    /// <summary>Selects the summary node (or the output node) that carries a parameter key.</summary>
    private void SelectOutputNodeFor(ParameterKeyRow? key)
    {
        if (key is null || ActiveGraph is not { } graph)
            return;
        var node = graph.GroupFor(key.Key) ?? graph.OutputNode();
        if (node is not null && !ReferenceEquals(node, _selectedNode))
            SelectedNode = node;
    }

    private void ResetNodeSelection()
    {
        _syncingRows = true;
        _selectedNode = null;
        _selectedNodeRow = null;
        _syncingRows = false;
        OnPropertyChanged(nameof(SelectedNode));
        OnPropertyChanged(nameof(SelectedNodeRow));
        (RemoveNodeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        BumpGraph();
        // A new graph object is about to be shown: fit its nodes instead of reusing the old pan/zoom.
        FrameGraph();
    }

    private void BumpGraph()
    {
        _graphRevision++;
        OnPropertyChanged(nameof(GraphRevision));
    }

    private void OnNodeChanged()
    {
        _rollRevision++;
        OnPropertyChanged(nameof(RollRevision));
        BumpGraph();
        RefreshParameterKeys();
    }

    /// <summary>Ensures the summary node for the selected key is wired into the four-socket output node.</summary>
    private void AddOutputNode()
    {
        if (ActiveGraph is not { } graph || _selectedParameterKey is not { } key)
            return;

        var socket = ParameterNodeGraph.SocketOfKey(key.Key);
        if (socket < 0)
        {
            AddLog("warn", $"「{key.Definition.Label}」不属于共振峰/气声汇总，继续使用基础值。");
            return;
        }

        PushUndo();
        var output = graph.OutputNode() ?? graph.Add(NodeKind.ParameterOutput, 690, 260);
        var groupKind = ParameterNodeGraph.GroupKindOfSocket(socket);
        var group = graph.Nodes.FirstOrDefault(n => n.Kind == groupKind)
            ?? graph.Add(groupKind, 420, 24 + socket * 230);
        graph.Connect(group.Id, 0, output.Id, socket);
        SelectedNode = group;
        NotifyGraphChanged();
        AddLog("info", $"节点图：{NodeCatalog.Get(groupKind).Title} 已接到参数输出，"
            + $"「{key.Definition.Label}」生效。");
    }

    private void AddNode(NodeKind kind)
    {
        if (ActiveGraph is not { } graph)
            return;

        PushUndo();
        var anchor = SelectedNode;
        var node = graph.Add(kind,
            anchor is null ? 60 : anchor.X + 40,
            anchor is null ? 60 : anchor.Y + 100);

        switch (kind)
        {
            case NodeKind.ParameterInput:
                node.Text = SuggestedOutputKey() ?? "breathGain";
                break;
            case NodeKind.FieldDecompose:
                // Convenience: a fresh field node reads from the existing voicebank data node.
                if (graph.Nodes.FirstOrDefault(n => n.Kind == NodeKind.VoicebankData) is { } data)
                    graph.Connect(data.Id, 0, node.Id, 0);
                break;
            case NodeKind.ParameterOutput:
                // Convenience: connect whichever summary nodes already exist.
                for (var socket = 0; socket <= ParameterNodeGraph.BreathSocket; socket++)
                {
                    var groupKind = ParameterNodeGraph.GroupKindOfSocket(socket);
                    if (graph.Nodes.FirstOrDefault(n => n.Kind == groupKind) is { } group)
                        graph.Connect(group.Id, 0, node.Id, socket);
                }
                break;
        }

        SelectedNode = node;
        NotifyGraphChanged();
        AddLog("info", $"节点图：添加「{NodeCatalog.Get(kind).Title}」节点。");
    }

    private void RemoveSelectedNode()
    {
        if (ActiveGraph is not { } graph || SelectedNode is not { } node)
            return;
        PushUndo();
        graph.RemoveNode(node.Id);
        ResetNodeSelection();
        RefreshParameterKeys();
    }

    private void FrameGraph()
    {
        _frameRevision++;
        OnPropertyChanged(nameof(FrameRevision));
    }

    // ------------------------------------------------------------------
    // INodeGraphHost — the canvas calls these around every structural edit
    // ------------------------------------------------------------------
    public void PushGraphUndo() => PushUndo();

    public void NotifyGraphChanged()
    {
        _rollRevision++;
        OnPropertyChanged(nameof(RollRevision));
        BumpGraph();
        RefreshParameterKeys();
        _selectedNodeRow?.Refresh();
    }

    public string? SuggestedOutputKey() => _selectedParameterKey?.Key;

    // ------------------------------------------------------------------
    // Singer data-block commands
    // ------------------------------------------------------------------
    private void AddSinger()
    {
        PushUndo();
        var singer = ProjectFactory.AddSingerBundle(
            _project,
            ProjectDocument.UniqueName("歌手", _project.Singers.Select(s => s.Name)),
            SelectedLibraryItem?.Path ?? _services.Options.LibraryRoot);
        RebuildRows();
        SelectedSinger = Singers.FirstOrDefault(r => r.Id == singer.Id);
        AddLog("info", $"新建歌手「{singer.Name}」，已自动创建并绑定独立歌词与参数。");
    }

    private void RemoveSinger()
    {
        var row = SelectedSinger;
        if (row is null)
            return;
        PushUndo();
        _project.Singers.Remove(row.Model);
        var lyric = _project.FindLyric(row.Model.LyricId);
        if (lyric is not null && _project.LyricUsers(lyric.Id) == 0 && !lyric.FakeUser)
            _project.Lyrics.Remove(lyric);
        if (_project.FindParameterSet(row.Model.ParameterSetId) is { } parameters
            && _project.ParameterSetUsers(parameters.Id) == 0 && !parameters.FakeUser)
            _project.ParameterSets.Remove(parameters);
        RebuildRows();
        AddLog("info", $"删除歌手「{row.Name}」及其孤立歌词。");
    }

    private void DuplicateSinger()
    {
        var row = SelectedSinger;
        if (row is null)
            return;
        PushUndo();
        var lyric = row.Model.LyricId is { } lyricId ? _project.FindLyric(lyricId)?.Clone() : ProjectFactory.CreateLyric("歌词");
        if (lyric is null)
            lyric = ProjectFactory.CreateLyric("歌词");
        lyric.Id = Guid.NewGuid().ToString("N");
        lyric.Name = ProjectDocument.UniqueName(row.Name + " - 歌词", _project.Lyrics.Select(l => l.Name));
        _project.Lyrics.Add(lyric);

        var copy = row.Model.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = ProjectDocument.UniqueName(row.Name + " 副本", _project.Singers.Select(s => s.Name));
        copy.LyricId = lyric.Id;
        _project.Singers.Add(copy);
        RebuildRows();
        SelectedSinger = Singers.FirstOrDefault(r => r.Id == copy.Id);
        AddLog("info", $"复制歌手「{row.Name}」：参数共享，歌词独立。");
    }

    private void ToggleSingerFake()
    {
        var row = SelectedSinger;
        if (row is null)
            return;
        PushUndo();
        row.Model.FakeUser = !row.Model.FakeUser;
        row.Refresh();
    }

    private void PurgeOrphans()
    {
        var orphans = _project.FindOrphanNames();
        if (orphans.Count == 0)
        {
            AddLog("info", "没有需要清理的孤立数据块。");
            return;
        }
        if (MessageBox.Show($"将清理 {orphans.Count} 个孤立数据块：\n{string.Join("\n", orphans)}",
                "清理孤立数据块", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        PushUndo();
        var count = _project.PurgeOrphans();
        RebuildRows();
        AddLog("info", $"已清理 {count} 个孤立数据块。");
    }

    // ------------------------------------------------------------------
    // Library / dictionary
    // ------------------------------------------------------------------
    private void RefreshLibrary()
    {
        Library.Clear();
        RefreshVoicebanks();
        foreach (var entry in LibraryScanner.Scan(LibraryRoot))
            Library.Add(entry);
        RefreshLibraryIcon();
        OnPropertyChanged(nameof(LibraryRoot));
        SetStatus("声库：{0} 个音频", Library.Count);
    }

    private void RefreshVoicebanks()
    {
        Voicebanks.Clear();
        var root = Path.Combine(_services.Options.RepositoryRoot, "voicebank");
        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(d => d))
                Voicebanks.Add(new VoicebankOption(Path.GetFileName(directory), directory));
        }

        // The active singer may point at a voicebank outside the repository (for example a temp folder
        // used by the startup test or a dragged-in bank). Keep it in the list so the two-way ComboBox
        // does not silently fall back to the first repository bank.
        var current = LibraryRoot;
        if (!string.IsNullOrWhiteSpace(current)
            && !Voicebanks.Any(v => string.Equals(v.Path, current, StringComparison.OrdinalIgnoreCase)))
        {
            Voicebanks.Insert(0, new VoicebankOption(
                Path.GetFileName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                current));
        }
    }

    private void RefreshLibraryIcon()
    {
        var root = LibraryRoot;
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

        _libraryIcon = LoadImage(path) ?? LoadAppIcon();
        OnPropertyChanged(nameof(LibraryIcon));
        OnPropertyChanged(nameof(HasLibraryIcon));
    }

    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico"];

    private static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private ImageSource? LoadImage(string? path)
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
        catch (Exception ex)
        {
            AddLog("warn", "声库图片无法解码：" + ex.Message);
            return null;
        }
    }

    private ImageSource? LoadAppIcon()
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

    private void AddLibraryItem(LibraryEntry? entry)
    {
        if (entry is null || ActiveLyric is null)
            return;
        var start = ActiveLyric.Segments.Count == 0 ? 1.0 : ActiveLyric.Segments.Max(s => s.BeatEnd);
        var semitone = 62;
        CreateSegmentAt(start, start + DefaultLyricUnitBeats, semitone);
    }

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
            AddLog("info", $"已导入字典「{dictionary.Name}」（{dictionary.Entries.Count} 条）。");
            foreach (var issue in dictionary.Validate())
                AddLog("warn", "字典：" + issue);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void LoadDefaultDictionary()
    {
        var dictionary = DefaultDictionary.Create();
        ApplyDictionary(dictionary, null);
        AddLog("info", LanguageService.TranslateFormat("内置字典已加载（{0} 条）。", dictionary.Entries.Count));
    }

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
    }

    private string? ResolveDictionaryLibraryRoot()
    {
        if (_dictionary is null || string.IsNullOrWhiteSpace(_dictionary.LibraryRoot))
            return LibraryRoot;
        var own = _dictionary.LibraryRoot!;
        if (Path.IsPathRooted(own))
            return own;
        var directory = string.IsNullOrWhiteSpace(_dictionaryPath) ? null : Path.GetDirectoryName(_dictionaryPath);
        return directory is null ? own : Path.Combine(directory, own);
    }

    private void InsertDictionaryEntry()
    {
        if (_dictionary is null || SelectedDictionaryEntry is null || ActiveLyric is null)
            return;
        try
        {
            PushUndo();
            var entry = SelectedDictionaryEntry.Model;
            var start = ActiveLyric.Segments.Count == 0 ? DictionaryStartBeat : ActiveLyric.Segments.Max(s => s.BeatEnd);
            var segments = DictionaryResolver.Resolve(
                entry,
                string.IsNullOrWhiteSpace(_dictionaryPath) ? null : Path.GetDirectoryName(_dictionaryPath),
                ResolveDictionaryLibraryRoot(),
                start,
                DictionaryBeats,
                DictionaryDstPitch,
                _dictionary.DefaultSrcPitch);

            ActiveLyric.Segments.AddRange(segments);
            ActiveLyric.Segments.Sort((a, b) => a.BeatStart.CompareTo(b.BeatStart));
            foreach (var segment in segments)
            {
                segment.Phoneme = Path.GetFileNameWithoutExtension(segment.Audio);
                segment.Vowel = GuessVowel(segment.Phoneme);
                ActiveLyric.AddPitchNodes(segment, 5);
            }
            LyricPhraseComposer.SyncWithSegments(ActiveLyric.Phrase, ActiveLyric);
            _rollRevision++;
            OnPropertyChanged(nameof(RollRevision));
            RefreshSegments();
            RefreshLyricUnits();
            AddLog("info", $"插入字典条目「{entry.Text}」：{segments.Count} 段。");
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    // ------------------------------------------------------------------
    // Asset import / export (external library)
    // ------------------------------------------------------------------
    private void ExportLyricAsset()
    {
        var row = SelectedLyric;
        if (row is null)
            return;
        var dialog = new SaveFileDialog
        {
            Title = "导出歌词数据块",
            Filter = "歌词数据块 (*.vblyric.json)|*.vblyric.json|JSON (*.json)|*.json",
            FileName = SafeFileName(row.Name) + ".vblyric.json",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            File.WriteAllText(dialog.FileName,
                JsonSerializer.Serialize(row.Model, ProjectJson.WriteOptions));
            AddLog("info", "歌词数据块已导出：" + dialog.FileName);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void ImportLyricAsset()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入歌词数据块",
            Filter = "歌词数据块 (*.vblyric.json)|*.vblyric.json|JSON (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var lyric = JsonSerializer.Deserialize<LyricDocument>(File.ReadAllText(dialog.FileName),
                ProjectJson.ReadOptions) ?? throw new InvalidDataException("歌词文件为空。");
            lyric.Id = Guid.NewGuid().ToString("N");
            lyric.Name = ProjectDocument.UniqueName(lyric.Name, _project.Lyrics.Select(l => l.Name));
            PushUndo();
            _project.Lyrics.Add(lyric);
            RebuildRows();
            AddLog("info", $"歌词数据块已导入：{lyric.Name}");
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void ExportParameterAsset()
    {
        var row = SelectedParameterSet;
        if (row is null)
            return;
        var dialog = new SaveFileDialog
        {
            Title = "导出参数数据块",
            Filter = "参数数据块 (*.vbparam.json)|*.vbparam.json|JSON (*.json)|*.json",
            FileName = SafeFileName(row.Name) + ".vbparam.json",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            File.WriteAllText(dialog.FileName,
                JsonSerializer.Serialize(row.Model, ProjectJson.WriteOptions));
            AddLog("info", "参数数据块已导出：" + dialog.FileName);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void ImportParameterAsset()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入参数数据块",
            Filter = "参数数据块 (*.vbparam.json)|*.vbparam.json|JSON (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var parameters = JsonSerializer.Deserialize<ParameterSet>(File.ReadAllText(dialog.FileName),
                ProjectJson.ReadOptions) ?? throw new InvalidDataException("参数文件为空。");
            parameters.Id = Guid.NewGuid().ToString("N");
            parameters.Name = ProjectDocument.UniqueName(parameters.Name, _project.ParameterSets.Select(p => p.Name));
            PushUndo();
            _project.ParameterSets.Add(parameters);
            RebuildRows();
            AddLog("info", $"参数数据块已导入：{parameters.Name}");
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void ExportTimbre()
    {
        if (ActiveParameterSet is null)
            return;
        var dialog = new SaveFileDialog
        {
            Title = "导出音色参数",
            Filter = $"音色参数 (*{TimbreProfile.FileExtension})|*{TimbreProfile.FileExtension}|JSON (*.json)|*.json",
            FileName = SafeFileName(ActiveParameterSet.Name) + TimbreProfile.FileExtension,
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            TimbreProfile.From(ActiveParameterSet.Base).Save(dialog.FileName);
            AddLog("info", "音色参数已导出：" + dialog.FileName);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void ImportTimbre()
    {
        if (ActiveParameterSet is null)
            return;
        var dialog = new OpenFileDialog
        {
            Title = "导入音色参数",
            Filter = $"音色参数 (*{TimbreProfile.FileExtension})|*{TimbreProfile.FileExtension}|JSON (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var profile = TimbreProfile.Load(dialog.FileName);
            PushUndo();
            foreach (var item in profile.ApplyTo(ActiveParameterSet.Base))
                AddLog("warn", "音色导入跳过：" + item);
            OnPropertyChanged(nameof(BaseValue));
            AddLog("info", "音色参数已导入。");
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private async Task AnalyzeVoicebankAsync()
    {
        var singer = SelectedSinger?.Model;
        if (singer is null || string.IsNullOrWhiteSpace(singer.LibraryRoot))
        {
            ReportError(new StageException(nameof(MainViewModel), "analyze-voicebank",
                "请先为歌手选择声库。"));
            return;
        }

        var script = Path.Combine(_services.Options.RepositoryRoot, "tools", "analyze_voicebank.py");
        if (!File.Exists(script))
        {
            ReportError(new StageException(nameof(MainViewModel), "analyze-voicebank",
                "找不到声库分析脚本：" + script));
            return;
        }

        IsBusy = true;
        StatusText = "正在分析声库共振峰…";
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = _services.Options.PythonPath,
                WorkingDirectory = _services.Options.RepositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add(script);
            info.ArgumentList.Add(singer.LibraryRoot);

            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    AddLog("info", e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    AddLog("warn", e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new StageException("VoicebankAnalyzer", "run",
                    $"声库分析失败（退出码 {process.ExitCode}），详见日志。");

            // Reload the profile so the next plan/vowel-manager read uses measured formants.
            OnPropertyChanged(nameof(UnitFormantInfo));
            OnPropertyChanged(nameof(UnitFormantValues));
            OnPropertyChanged(nameof(UnitFormantHint));
            AddLog("info", "声库共振峰分析完成：formants.json 已写入声库目录。");
            StatusText = "声库共振峰分析完成";
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }


    private async Task RenderAsync()
    {
        if (IsBusy)
            return;

        var issues = ProjectValidator.Validate(_project);
        foreach (var issue in issues)
            AddLog(issue.Severity == ProjectIssueSeverity.Error ? "error" : "warn", issue.Message);
        if (ProjectValidator.HasError(issues))
        {
            ReportError(new StageException(nameof(MainViewModel), "validate-render",
                "工程校验未通过，详见日志。"));
            return;
        }

        var singers = _project.Singers.Where(s => s.Enabled).ToList();
        if (singers.Count == 0)
        {
            ReportError(new StageException(nameof(MainViewModel), "render", "没有启用的歌手。"));
            return;
        }

        _renderCts = new CancellationTokenSource();
        IsBusy = true;
        SetStatus("渲染 {0} 个歌手…", singers.Count);

        try
        {
            // The engine is a child Python process; start it on the first render and reuse it afterwards.
            if (!_services.Engine.IsReady)
            {
                StatusText = "正在启动 Python 引擎…";
                AddLog("info", "正在启动 Python 引擎…");
                await _services.Engine.InitializeAsync(_renderCts.Token);
                AddLog("info", "Python 引擎已就绪。");
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var folder = Path.Combine(ProjectFactory.DefaultRenderDirectory(_services.Options.RepositoryRoot),
                "singers_" + stamp);
            Directory.CreateDirectory(folder);

            var outputs = new List<string>();
            for (var i = 0; i < singers.Count; i++)
            {
                var singer = singers[i];
                var plan = SingerRenderPlan.Build(_project, singer, i, singers.Count);
                var output = Path.Combine(folder, $"singer_{i + 1:00}.wav");
                var progress = new Progress<EngineEvent>(e =>
                {
                    if (e.Kind == EngineEventKind.Warning)
                        AddLog("warn", $"[{singer.Name}] {e.Message}");
                    else
                        AddLog("info", $"[{singer.Name}] {e.Message}");
                });
                var result = await _services.Engine.RenderAsync(
                    new RenderRequest(_project, plan, output), progress, _renderCts.Token);
                outputs.Add(result.OutputPath);
                AddLog("info", LanguageService.TranslateFormat("歌手「{0}」完成：{1:0.0}s。", singer.Name, result.DurationSeconds));
            }

            var mixPath = Path.Combine(ProjectFactory.DefaultRenderDirectory(_services.Options.RepositoryRoot),
                $"render_{stamp}.wav");
            TrackMixer.Mix(outputs, mixPath, _project.Singers[0].ParameterSetId is { } id
                ? _project.FindParameterSet(id)?.Base.BitDepth ?? 16
                : 16);

            _lastMixPath = EnsureBgmMix(mixPath) ?? mixPath;
            SetPreviewInfo("{0} 个歌手 · {1}", singers.Count, DateTime.Now.ToString("HH:mm:ss"));
            SetStatus("渲染完成：{0}", Path.GetFileName(_lastMixPath));
            AddLog("info", LanguageService.TranslateFormat("渲染完成：{0}", _lastMixPath));
        }
        catch (OperationCanceledException)
        {
            AddLog("warn", "渲染已取消。");
            StatusText = "渲染已取消";
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

    private void TogglePlay()
    {
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }

        if (string.IsNullOrWhiteSpace(_lastMixPath) || !File.Exists(_lastMixPath))
        {
            AddLog("warn", "尚未渲染，无法播放。");
            return;
        }

        var startBeat = PlayheadBeat ?? 1.0;
        var seconds = Math.Max(0, (startBeat - 1) * 60.0 / Math.Max(1, _project.Bpm));
        _services.Playback.Play(_lastMixPath, TimeSpan.FromSeconds(seconds));
        IsPlaying = true;
        _playheadTimer.Start();
    }

    private void StopPlayback()
    {
        _services.Playback.Stop();
        _playheadTimer.Stop();
        IsPlaying = false;
    }

    private void SyncPlayheadFromPlayback()
    {
        if (!IsPlaying)
            return;
        var seconds = _services.Playback.Position.TotalSeconds;
        PlayheadBeat = 1.0 + seconds * _project.Bpm / 60.0;
    }

    private string? EnsureBgmMix(string vocalPath)
    {
        var bgmPath = _project.BgmPath;
        if (string.IsNullOrWhiteSpace(bgmPath) || !File.Exists(bgmPath))
            return null;

        var key = $"{vocalPath}|{bgmPath}|{_project.BgmStartBeat}|{_project.Bpm}";
        if (_bgmMixPath is not null && _bgmMixKey == key && File.Exists(_bgmMixPath))
            return _bgmMixPath;

        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(vocalPath) ?? ".", "bgm");
            Directory.CreateDirectory(directory);
            var output = Path.Combine(directory, Path.GetFileNameWithoutExtension(vocalPath) + "_bgm.wav");
            var seconds = _project.BgmStartBeat * 60.0 / Math.Max(1, _project.Bpm);
            BgmMixer.Mix(vocalPath, bgmPath, seconds, output);
            _bgmMixPath = output;
            _bgmMixKey = key;
            return output;
        }
        catch (Exception ex)
        {
            AddLog("warn", "BGM 混合失败：" + ex.Message);
            return null;
        }
    }

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
            OnPropertyChanged(nameof(HasBgm));
            OnPropertyChanged(nameof(BgmName));
            OnPropertyChanged(nameof(BgmStartBeat));
            OnPropertyChanged(nameof(BgmDurationBeats));
            (ClearBgmCommand as RelayCommand)?.RaiseCanExecuteChanged();
            SetStatus("已导入 BGM：{0}", BgmName);
            AddLog("info", $"BGM 已导入：{full}（{info.SampleRate} Hz，{info.DurationSeconds:F1}s）。");
        }
        catch (Exception ex)
        {
            ReportError(ex);
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
        OnPropertyChanged(nameof(HasBgm));
        OnPropertyChanged(nameof(BgmName));
        OnPropertyChanged(nameof(BgmStartBeat));
        OnPropertyChanged(nameof(BgmDurationBeats));
        (ClearBgmCommand as RelayCommand)?.RaiseCanExecuteChanged();
        StatusText = "已移除 BGM";
    }

    // ------------------------------------------------------------------
    // Misc UI
    // ------------------------------------------------------------------
    private void ToggleRollMode() =>
        RollMode = RollMode == PianoRollMode.Notes ? PianoRollMode.PitchLine : PianoRollMode.Notes;

    private void ToggleTheme()
    {
        _isLightTheme = !_isLightTheme;
        ThemeManager.Apply(_isLightTheme);
        OnPropertyChanged(nameof(ThemeToggleText));
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
            AddLog("error", "语言包导入失败：" + error);
            return;
        }
        RebuildLanguages();
        RefreshLanguageText();
        AddLog("info", $"语言包已导入：{pack.Name}（{pack.Code}）");
    }

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
        OnPropertyChanged(nameof(GraphOwnerText));
        OnPropertyChanged(nameof(BgmName));
        OnPropertyChanged(nameof(UnitFormantInfo));
        OnPropertyChanged(nameof(UnitFormantHint));
        foreach (var row in Singers)
            row.Refresh();
        foreach (var row in Lyrics)
            row.Refresh();
        foreach (var row in ParameterSets)
            row.Refresh();
        foreach (var row in Segments)
            row.Refresh();
        foreach (var row in LyricUnits)
            row.Refresh();
        foreach (var row in ParameterKeyRows)
            row.RefreshText();
        foreach (var row in DictionaryEntries)
            row.Refresh();
    }

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
            AddLog("warn", "未找到帮助文件：" + fileName);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void AddLog(string kind, string message)
    {
        Logs.Insert(0, new LogEntry(DateTime.Now, kind, LanguageService.Translate(message)));
        while (Logs.Count > MaxLogEntries)
            Logs.RemoveAt(Logs.Count - 1);
    }

    /// <summary>
    /// Unified error surface: a <see cref="StageException"/> already carries "Class @ Stage"; anything else
    /// is wrapped with the ViewModel + stage that produced it so no failure is silently dropped.
    /// </summary>
    private void ReportError(Exception ex)
    {
        var error = ex as StageException
            ?? new StageException(nameof(MainViewModel), "command", ex.Message, ex);
        AddLog("error", error.Message);
        SetStatus("出错：{0} @ {1}", error.ClassName, error.Stage);
        MessageBox.Show(error.Message, "VocalB1 错误", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "untitled" : name;
    }

    private void Dispatch(Action action)
    {
        if (Application.Current is { Dispatcher: { } dispatcher } && !dispatcher.CheckAccess())
            dispatcher.Invoke(action);
        else
            action();
    }

    private void RaiseSelectionCommands()
    {
        (RemoveSingerCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DuplicateSingerCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleSingerFakeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveLyricCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DuplicateLyricCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleLyricFakeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveParameterSetCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DuplicateParameterSetCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleParameterFakeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportLyricAssetCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportParameterAssetCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportTimbreCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ImportTimbreCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RaiseAllCommandStates()
    {
        RaiseSelectionCommands();
        (UndoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RedoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CommitLyricCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddOutputNodeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddNodeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveNodeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearBgmCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static string GuessVowel(string? phoneme)
    {
        if (string.IsNullOrWhiteSpace(phoneme))
            return "a";
        var text = phoneme.Trim().TrimStart('_', '-').ToLowerInvariant();
        foreach (var ch in text)
        {
            var vowel = ch switch
            {
                'あ' or 'か' or 'さ' or 'た' or 'な' or 'は' or 'ま' or 'や' or 'ら' or 'わ' or 'が' or 'ざ' or 'だ' or 'ば' or 'ぱ' => "a",
                'い' or 'き' or 'し' or 'ち' or 'に' or 'ひ' or 'み' or 'り' or 'ぎ' or 'じ' or 'ぢ' or 'び' or 'ぴ' => "i",
                'う' or 'く' or 'す' or 'つ' or 'ぬ' or 'ふ' or 'む' or 'ゆ' or 'る' or 'ぐ' or 'ず' or 'づ' or 'ぶ' or 'ぷ' => "u",
                'え' or 'け' or 'せ' or 'て' or 'ね' or 'へ' or 'め' or 'れ' or 'げ' or 'ぜ' or 'で' or 'べ' or 'ぺ' => "e",
                'お' or 'こ' or 'そ' or 'と' or 'の' or 'ほ' or 'も' or 'よ' or 'ろ' or 'を' or 'ご' or 'ぞ' or 'ど' or 'ぼ' or 'ぽ' => "o",
                'ん' => "n",
                _ => string.Empty,
            };
            if (vowel.Length > 0)
                return vowel;
        }
        return text.Length > 0 && "aiueon".Contains(text[^1]) ? text[^1].ToString() : "a";
    }
}
