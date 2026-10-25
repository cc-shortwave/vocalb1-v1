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
    private int? _selectedNoteIndex;
    private int? _selectedPitchIndex;
    private int _rollRevision = 1;
    private string _previewInfo = "尚未渲染";
    private bool _isBusy;
    private bool _isPlaying;
    private string _statusText = "就绪";
    private string? _lastRenderPath;
    private double? _playheadBeat;
    private bool _isLightTheme;
    private LanguageRow? _selectedLanguage;
    private bool _languageReady;

    private UserDictionary? _dictionary;
    private string? _dictionaryPath;
    private DictionaryEntryRow? _selectedDictionaryEntry;
    private double _dictionaryStartBeat = 1.0;
    private double _dictionaryBeats = 1.0;
    private string _dictionaryDstPitch = "G4";

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
        RemoveSegmentCommand = new RelayCommand(p => RemoveSegment(p as SegmentRow));
        AddTrackCommand = new RelayCommand(_ => AddTrack());
        RemoveTrackCommand = new RelayCommand(_ => RemoveTrack(), _ => _project.Tracks.Count > 1);
        ToggleRollModeCommand = new RelayCommand(_ => ToggleRollMode());
        ImportDictionaryCommand = new RelayCommand(_ => ImportDictionary());
        InsertDictionaryEntryCommand = new RelayCommand(
            _ => InsertDictionaryEntry(),
            _ => _dictionary is not null && SelectedDictionaryEntry is not null);
        RenderCommand = new AsyncRelayCommand(_ => RenderAsync(), _ => !IsBusy, ReportError);
        CancelRenderCommand = new RelayCommand(_ => _renderCts?.Cancel(), _ => IsBusy);
        PlayCommand = new RelayCommand(_ => TogglePlay());
        StopCommand = new RelayCommand(_ => StopPlayback());
        ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
        ImportLanguageCommand = new RelayCommand(_ => ImportLanguagePack());

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

    /// <summary>声库目录下与音频同级的 icon.jpg，显示在顶栏「新建」旁。</summary>
    public ImageSource? LibraryIcon => _libraryIcon;

    public bool HasLibraryIcon => _libraryIcon is not null;

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
            LibraryRoot = value.Path;
            OnPropertyChanged();
            RefreshLibrary();
            StatusText = $"声库：{value.Name}";
        }
    }

    /// <summary>True when the project differs from the last loaded/saved snapshot (prompted on load and on close).</summary>
    public bool HasUnsavedChanges =>
        !string.Equals(_savedSnapshot, ProjectJson.Serialize(_project), StringComparison.Ordinal);

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

    /// <summary>Vocal balance (0-100): larger values put more energy into the right channel; 50 is centered.</summary>
    public double StereoBalance
    {
        get => _activeTrack.Settings.StereoBalance;
        set
        {
            if (_activeTrack.Settings.StereoBalance == value)
                return;
            _activeTrack.Settings.StereoBalance = value;
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
            OnPropertyChanged(nameof(SelectedLyric));
            OnPropertyChanged(nameof(SelectedTransitionBeats));
        }
    }

    /// <summary>Whether a segment is selected (the lyric input box is enabled based on this).</summary>
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

    /// <summary>Lyric of the selected segment: typed input is written back to the model and the piano roll redraws immediately.</summary>
    public string SelectedLyric
    {
        get => _selectedSegment?.Lyric ?? string.Empty;
        set
        {
            var row = _selectedSegment;
            if (row is null || row.Lyric == value)
                return;

            // Consecutive edits within the same segment's lyric are coalesced into one undo step, so each keystroke does not take a slot.
            PushUndo("lyric:" + row.Model.Id);
            row.Lyric = value ?? string.Empty;
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

    // ------------------------------------------------------------------
    // User dictionary
    // ------------------------------------------------------------------
    public string DictionaryName => _dictionary?.Name ?? LanguageService.Translate("（未导入）");

    public string DictionaryPathText => LanguageService.Translate(
        _dictionaryPath ?? "导入 .json 字典，按文本组合声库音频");

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
    // Commands
    // ------------------------------------------------------------------
    public ICommand NewProjectCommand { get; }
    public ICommand LoadDemoCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand SaveProjectCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand DeleteSelectionCommand { get; }
    public ICommand RefreshLibraryCommand { get; }
    public ICommand AddLibraryItemCommand { get; }
    public ICommand RemoveSegmentCommand { get; }
    public ICommand AddTrackCommand { get; }
    public ICommand RemoveTrackCommand { get; }
    public ICommand ToggleRollModeCommand { get; }
    public ICommand ImportDictionaryCommand { get; }
    public ICommand InsertDictionaryEntryCommand { get; }
    public ICommand RenderCommand { get; }
    public ICommand CancelRenderCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ImportLanguageCommand { get; }

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
        ProjectDocument project;
        try
        {
            project = ProjectJson.Load(path);
        }
        catch (Exception ex)
        {
            AddLog("error", "Failed to open project: " + ex.Message);
            StatusText = "打开失败";
            MessageBox.Show("无法打开工程文件：" + ex.Message, "载入工程", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!ConfirmDiscardChanges("载入工程"))
            return;

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
            ProjectJson.Save(_project, path);
            _projectPath = path;
            _savedSnapshot = ProjectJson.Serialize(_project);
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
        _selectedTrack = Tracks[0];
        OnPropertyChanged(nameof(SelectedTrack));

        ActivateTrack(_project.Tracks[0]);
        _savedSnapshot = ProjectJson.Serialize(_project);
        (RemoveTrackCommand as RelayCommand)?.RaiseCanExecuteChanged();
        StatusText = "就绪";
    }

    private void RefreshSegments()
    {
        var selectedIndex = _selectedNoteIndex;

        // Rebuild transition points after overlap changes so that regular control points never fall inside overlapping segments.
        SegmentLayout.SyncTransitionPoints(_activeTrack);

        Segments.Clear();
        for (var i = 0; i < _activeTrack.Segments.Count; i++)
            Segments.Add(new SegmentRow(_activeTrack.Segments[i], i));

        RollRevision++;

        _selectedSegment = selectedIndex is int index && index >= 0 && index < Segments.Count
            ? Segments[index]
            : null;
        OnPropertyChanged(nameof(SelectedSegment));
        OnPropertyChanged(nameof(HasSelectedSegment));
        OnPropertyChanged(nameof(SelectedLyric));
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
        trackIndex = Math.Clamp(trackIndex, 0, Math.Max(0, Tracks.Count - 1));
        _selectedTrack = Tracks.Count > 0 ? Tracks[trackIndex] : null;
        OnPropertyChanged(nameof(SelectedTrack));

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
            SrcPitch = "D4",
            DstPitch = PitchMath.ToPitchName(semitone),
            BeatStart = start,
            BeatEnd = start + 1.0,
        };
        _activeTrack.Segments.Add(segment);
        _activeTrack.Segments.Sort((a, b) => a.BeatStart.CompareTo(b.BeatStart));
        _activeTrack.AddPitchNodes(segment, 5);
        RefreshSegments();

        AddLog("info",
            $"Note added: {entry.DisplayName} | beat {start:0.##} -> {start + 1:0.##} | {PitchMath.ToPitchName(semitone)}");
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
        Tracks.Remove(row);

        (RemoveTrackCommand as RelayCommand)?.RaiseCanExecuteChanged();
        SelectedTrack = Tracks[Math.Min(index, Tracks.Count - 1)];
        AddLog("info", $"Track removed: {row.Name}");
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
        OnPropertyChanged(nameof(StereoBalance));
        OnPropertyChanged(nameof(Jitter));
        OnPropertyChanged(nameof(Shimmer));
        RefreshParameterRows();

        SelectedSegment = null;
        SelectedPitchIndex = null;
        RefreshLibrary();
        RefreshSegments();
        DictionaryStartBeat = track.TotalBeats > 0 ? track.TotalBeats : 1.0;

        if (_trackOutputs.TryGetValue(track, out var output) && File.Exists(output.Path))
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
    /// Install a dropped voicebank folder: validate the format, copy it under voicebank/, then use it on the active track.
    /// Returns null on success, otherwise the reason the install was rejected.
    /// </summary>
    public string? InstallVoicebank(string sourceDirectory)
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

        try
        {
            if (!Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                    .Any(file => string.Equals(Path.GetFileName(file), "oto.ini", StringComparison.OrdinalIgnoreCase)))
                return "声库格式错误：缺少 oto.ini。";

            if (!Directory.EnumerateFiles(source, "*.wav", SearchOption.AllDirectories).Any())
                return "声库格式错误：没有 wav 音频文件。";

            Directory.CreateDirectory(VoicebankRoot);
            CopyDirectory(source, target);
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

        LibraryRoot = target;
        RefreshLibrary();
        AddLog("info", $"Voicebank installed: {target}");
        StatusText = $"已安装声库：{name}";
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

    private void RefreshLibrary()
    {
        Library.Clear();
        RefreshVoicebanks();
        foreach (var entry in LibraryScanner.Scan(EffectiveLibraryRoot))
            Library.Add(entry);
        RefreshLibraryIcon();
        StatusText = $"声库：{Library.Count} 个音频";
    }

    /// <summary>声库图标与音频文件同级：重新扫描声库时同步查找 icon.jpg。</summary>
    private void RefreshLibraryIcon()
    {
        var root = EffectiveLibraryRoot;
        var path = string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)
            ? null
            : Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(p => string.Equals(Path.GetFileName(p), "icon.jpg", StringComparison.OrdinalIgnoreCase));

        var icon = LoadLibraryIcon(path);
        if (ReferenceEquals(icon, _libraryIcon))
            return;

        _libraryIcon = icon;
        OnPropertyChanged(nameof(LibraryIcon));
        OnPropertyChanged(nameof(HasLibraryIcon));
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
            SrcPitch = "D4",
            DstPitch = "D4",
            BeatStart = start,
            BeatEnd = start + 1.0,
        };
        _activeTrack.Segments.Add(segment);
        _activeTrack.AddPitchNodes(segment, 5);
        RefreshSegments();
        AddLog("info", $"Segment added: {entry.DisplayName} ({_activeTrack.Name}, beat {start:0.##} -> {start + 1:0.##})");
    }

    private void RemoveSegment(SegmentRow? row)
    {
        if (row is null)
            return;

        PushUndo();
        _activeTrack.Segments.Remove(row.Model);
        _activeTrack.PitchLine.RemoveAll(p => p.SegmentId == row.Model.Id);
        SegmentLayout.SyncTransitionPoints(_activeTrack);
        RefreshSegments();
        AddLog("info", $"Segment removed: {row.Lyric} ({_activeTrack.Name})");
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

            _dictionary = dictionary;
            _dictionaryPath = dialog.FileName;

            DictionaryEntries.Clear();
            foreach (var entry in dictionary.Entries)
                DictionaryEntries.Add(new DictionaryEntryRow(entry));

            OnPropertyChanged(nameof(DictionaryName));
            OnPropertyChanged(nameof(DictionaryPathText));

            SelectedDictionaryEntry = DictionaryEntries.FirstOrDefault();
            DictionaryStartBeat = _activeTrack.TotalBeats > 0 ? _activeTrack.TotalBeats : 1.0;

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
    // Render / Playback
    // ------------------------------------------------------------------
    private async Task RenderAsync()
    {
        IsBusy = true;
        StatusText = "渲染中…";
        _renderCts = new CancellationTokenSource();

        try
        {
            var issues = ProjectValidator.Validate(_activeTrack, _project);
            foreach (var issue in issues)
                AddLog(issue.Severity == ProjectIssueSeverity.Error ? "error" : "warn", issue.Message);

            if (ProjectValidator.HasError(issues))
            {
                StatusText = "工程校验未通过";
                return;
            }

            var directory = ProjectFactory.DefaultRenderDirectory(_services.Options.RepositoryRoot);
            Directory.CreateDirectory(directory);
            var stem = string.Concat(_activeTrack.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var output = Path.Combine(directory, $"render_{stem}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

            AddLog("info", $"Starting the Python engine to render track \"{_activeTrack.Name}\"...");
            await _services.Engine.InitializeAsync(_renderCts.Token);

            var progress = new Progress<EngineEvent>(OnEngineEvent);
            var result = await _services.Engine.RenderAsync(
                new RenderRequest(_project, _activeTrack, output), progress, _renderCts.Token);

            // The engine output is mono; expand it to stereo here according to the track's vocal balance and distribute left/right energy.
            try
            {
                if (StereoBalanceProcessor.Apply(result.OutputPath, _activeTrack.Settings.StereoBalance))
                    AddLog("info", $"Stereo balance {_activeTrack.Settings.StereoBalance:0}: left/right channel energy adjusted.");
            }
            catch (Exception ex)
            {
                AddLog("warn", "Stereo balance post-processing failed: " + ex.Message);
            }

            _trackOutputs[_activeTrack] = new RenderedTrack(result.OutputPath, result.DurationSeconds);
            LastRenderPath = result.OutputPath;
            PreviewInfo = $"{Path.GetFileName(result.OutputPath)} · {result.DurationSeconds:F2}s";
            StatusText = $"渲染完成（{result.Elapsed.TotalSeconds:F1}s）";
            AddLog("info", $"Output: {result.OutputPath}");
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
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AddLog("warn", "Nothing to play; render the track first.");
            return;
        }

        // Start from the current playhead position; when there is no playhead yet, or playback already reached the end, restart from beat 1.
        var endBeat = PlaybackEndBeat();
        var startBeat = Math.Clamp(PlayheadBeat ?? 1, 1, endBeat);
        if (endBeat - startBeat < 0.05)
            startBeat = 1;

        var start = TimeSpan.FromSeconds(Math.Max(0, (startBeat - 1) * 60 / Math.Max(1, Bpm)));

        try
        {
            PlayheadBeat = startBeat;
            _services.Playback.Play(path, start);
            IsPlaying = true;
            _playheadTimer.Start();
            StatusText = $"播放中：{Path.GetFileName(path)}（第 {startBeat:0.##} 拍起）";
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
    private double PlaybackEndBeat()
    {
        if (_trackOutputs.TryGetValue(_activeTrack, out var output) && output.DurationSeconds > 0.01)
            return 1 + output.DurationSeconds * Math.Max(1, Bpm) / 60;

        return Math.Max(1, _activeTrack.TotalBeats);
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
