using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace VocalB1.App.Services;

/// <summary>A language pack: Chinese source text → target-language translations.</summary>
public sealed class LanguagePack
{
    public required string Code { get; init; }

    public required string Name { get; init; }

    /// <summary>Chinese source text (whitespace normalized) → translation.</summary>
    public Dictionary<string, string> Entries { get; init; } = new(StringComparer.Ordinal);

    public bool IsSourceLanguage => string.Equals(Code, "zh", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Loading, selecting and UI text replacement for language packs.
/// Chinese is the source key; other languages are substituted via the mapping table. The selected language
/// and imported external packs are recorded in the user configuration directory.
/// </summary>
public static class LanguageService
{
    private const string SettingsFolderName = "VocalB1";
    private const string SettingsFileName = "settings.json";
    private const string LanguagesFolderName = "languages";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Dictionary<string, LanguagePack> Packs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> ExternalPaths = [];
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, string>> Originals = new();

    private static string? _languagesDirectory;
    private static bool _initialized;

    public static LanguagePack Current { get; private set; } = new() { Code = "zh", Name = "中文" };

    public static event EventHandler? Changed;

    public static string LanguagesDirectory => _languagesDirectory ?? string.Empty;

    /// <summary>Chinese first, the rest sorted by name.</summary>
    public static IReadOnlyList<LanguagePack> Available
    {
        get
        {
            var list = Packs.Values.ToList();
            list.Sort((a, b) =>
            {
                if (a.IsSourceLanguage != b.IsSourceLanguage)
                    return a.IsSourceLanguage ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            });
            return list;
        }
    }

    /// <summary>Loads the built-in language pack directory, then restores the previous selection (including imported external packs).</summary>
    public static void Initialize(string repositoryRoot)
    {
        if (_initialized)
            return;
        _initialized = true;

        _languagesDirectory = Path.Combine(repositoryRoot, LanguagesFolderName);
        LoadDirectory();

        var settings = ReadSettings();
        foreach (var path in settings.ExternalPacks ?? [])
            LoadFile(path, remember: false);

        var code = settings.Language ?? "zh";
        if (Packs.TryGetValue(code, out var saved))
            Current = saved;
        else if (Packs.TryGetValue("zh", out var chinese))
            Current = chinese;
    }

    /// <summary>Switches to the specified language and refreshes all windows immediately.</summary>
    public static bool Select(string code)
    {
        if (!Packs.TryGetValue(code, out var pack) || pack.Code == Current.Code && pack == Current)
            return false;

        Current = pack;
        SaveSettings();
        ApplyToWindows();
        Changed?.Invoke(null, EventArgs.Empty);
        return true;
    }

    /// <summary>Imports an external language pack (an existing pack with the same code is replaced) and selects it on success.</summary>
    public static LanguagePack? Import(string path, out string? error)
    {
        error = null;
        try
        {
            var pack = LoadFile(path, remember: true);
            if (pack is null)
            {
                error = "语言包缺少 code 字段。";
                return null;
            }
            Select(pack.Code);
            return pack;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Translates Chinese source text using the current language; returns it unchanged when no entry exists.</summary>
    public static string Translate(string chinese)
    {
        if (string.IsNullOrEmpty(chinese) || Current.IsSourceLanguage)
            return chinese;

        var key = Normalize(chinese);
        return Current.Entries.TryGetValue(key, out var translated) ? translated : chinese;
    }

    public static void ApplyToWindows()
    {
        if (Application.Current is null)
            return;

        foreach (Window window in Application.Current.Windows)
            Apply(window);
    }

    /// <summary>Replaces all unbound static text inside the window with the current language.</summary>
    public static void Apply(DependencyObject root) => Walk(root);

    private static void Walk(DependencyObject node)
    {
        if (node is Window window)
            Localize(window, Window.TitleProperty);

        if (node is TextBlock textBlock)
            Localize(textBlock, TextBlock.TextProperty);

        if (node is HeaderedContentControl headered)
            Localize(headered, HeaderedContentControl.HeaderProperty);

        if (node is ContentControl content)
            Localize(content, ContentControl.ContentProperty);

        if (node is FrameworkElement element)
            LocalizeToolTip(element);

        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject childNode)
                Walk(childNode);
        }
    }

    /// <summary>Translates static text only: data bindings (including list templates) are left untouched.</summary>
    private static void Localize(DependencyObject owner, DependencyProperty property)
    {
        if (BindingOperations.IsDataBound(owner, property))
            return;

        if (owner.GetValue(property) is not string current || string.IsNullOrWhiteSpace(current))
            return;

        var originals = Originals.GetOrCreateValue(owner);
        if (!originals.TryGetValue(property, out var original))
        {
            original = current;
            originals[property] = original;
        }

        var translated = Translate(original);
        if (!string.Equals(current, translated, StringComparison.Ordinal))
            owner.SetValue(property, translated);
    }

    private static void LocalizeToolTip(FrameworkElement element)
    {
        if (BindingOperations.IsDataBound(element, FrameworkElement.ToolTipProperty))
            return;

        var current = element.ToolTip switch
        {
            string text => text,
            ToolTip { Content: string text } => text,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(current))
            return;

        var originals = Originals.GetOrCreateValue(element);
        if (!originals.TryGetValue(FrameworkElement.ToolTipProperty, out var original))
        {
            original = current;
            originals[FrameworkElement.ToolTipProperty] = original;
        }

        var translated = Translate(original);
        if (element.ToolTip is ToolTip tip)
            tip.Content = translated;
        else if (!string.Equals(current, translated, StringComparison.Ordinal))
            element.ToolTip = translated;
    }

    private static void LoadDirectory()
    {
        if (_languagesDirectory is null || !Directory.Exists(_languagesDirectory))
            return;

        foreach (var file in Directory.EnumerateFiles(_languagesDirectory, "*.json"))
        {
            try
            {
                LoadFile(file, remember: false);
            }
            catch
            {
                // A corrupted single language pack does not prevent other packs from loading.
            }
        }
    }

    private static LanguagePack? LoadFile(string path, bool remember)
    {
        if (!File.Exists(path))
            return null;

        var fullPath = Path.GetFullPath(path);
        var json = File.ReadAllText(fullPath);
        var dto = JsonSerializer.Deserialize<PackFile>(json, JsonOptions);
        if (string.IsNullOrWhiteSpace(dto?.Code))
            return null;

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in dto.Entries ?? [])
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrEmpty(value))
                entries[Normalize(key)] = value;
        }

        var pack = new LanguagePack
        {
            Code = dto.Code.Trim(),
            Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Code.Trim() : dto.Name.Trim(),
            Entries = entries,
        };
        Packs[pack.Code] = pack;

        if (remember && !ExternalPaths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            ExternalPaths.Add(fullPath);

        return pack;
    }

    private static SettingsFile ReadSettings()
    {
        var path = SettingsPath;
        if (path is null || !File.Exists(path))
            return new SettingsFile();

        try
        {
            return JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(path), JsonOptions) ?? new SettingsFile();
        }
        catch
        {
            return new SettingsFile();
        }
    }

    private static void SaveSettings()
    {
        var path = SettingsPath;
        if (path is null)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var settings = new SettingsFile
            {
                Language = Current.Code,
                ExternalPacks = [.. ExternalPaths],
            };
            File.WriteAllText(path, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A settings write failure does not affect the current switch.
        }
    }

    private static string? SettingsPath
    {
        get
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(folder)
                ? null
                : Path.Combine(folder, SettingsFolderName, SettingsFileName);
        }
    }

    private static string Normalize(string value) => Regex.Replace(value, "\\s+", " ").Trim();

    private sealed class PackFile
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
        public Dictionary<string, string>? Entries { get; set; }
    }

    private sealed class SettingsFile
    {
        public string? Language { get; set; }
        public List<string>? ExternalPacks { get; set; }
    }
}
