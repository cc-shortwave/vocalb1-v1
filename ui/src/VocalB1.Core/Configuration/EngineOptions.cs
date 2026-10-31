using System.Text.Json;
using VocalB1.Core.Serialization;

namespace VocalB1.Core.Configuration;

/// <summary>
/// Engine runtime configuration: Python interpreter, repository root, and bridge script location.
/// Defaults are auto-detected by <see cref="Discover"/> and can be overridden in ui/engine.settings.json.
/// </summary>
public sealed class EngineOptions
{
    /// <summary>Root directory of the repository containing the Python synthesis engine (with VocalB1Synthesizer.py).</summary>
    public string RepositoryRoot { get; set; } = string.Empty;

    /// <summary>Path to the Python interpreter.</summary>
    public string PythonPath { get; set; } = string.Empty;

    /// <summary>Path to the bridge script (a fixed file in the repository; no existing .py is modified).</summary>
    public string BridgeScriptPath { get; set; } = string.Empty;

    /// <summary>Voicebank root directory (source of the wav list).</summary>
    public string LibraryRoot { get; set; } = string.Empty;

    /// <summary>Engine startup (handshake) timeout.</summary>
    public int StartupTimeoutSeconds { get; set; } = 30;

    /// <summary>Timeout for a single render.</summary>
    public int RenderTimeoutSeconds { get; set; } = 900;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// Probes upward from the current executable location for the repository root;
    /// the interpreter is taken from the VOCALB1_PYTHON environment variable first, then a
    /// Python distribution bundled at &lt;root&gt;/python/python.exe, then C:\Python312\python.exe,
    /// and finally python on PATH.
    /// </summary>
    public static EngineOptions Discover()
    {
        var root = FindRepositoryRoot(AppContext.BaseDirectory) ?? Environment.CurrentDirectory;
        var options = new EngineOptions
        {
            RepositoryRoot = root,
            BridgeScriptPath = Path.Combine(root, "ui", "bridge", "vocal_bridge.py"),
            LibraryRoot = DiscoverLibraryRoot(root),
        };

        var env = Environment.GetEnvironmentVariable("VOCALB1_PYTHON");
        var bundled = Path.Combine(root, "python", "python.exe");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            options.PythonPath = env;
        else if (File.Exists(bundled))
            options.PythonPath = bundled;
        else if (File.Exists(@"C:\Python312\python.exe"))
            options.PythonPath = @"C:\Python312\python.exe";
        else
            options.PythonPath = "python";

        return options;
    }

    public static EngineOptions Load(string settingsPath)
    {
        var discovered = Discover();
        if (!File.Exists(settingsPath))
            return discovered;

        EngineOptions loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<EngineOptions>(
                File.ReadAllText(settingsPath), JsonOptions) ?? discovered;
        }
        catch (Exception)
        {
            // A damaged or partially written settings file must never prevent startup:
            // fall back to the discovered defaults.
            return discovered;
        }

        // Fall back to discovered values for missing fields
        if (string.IsNullOrWhiteSpace(loaded.RepositoryRoot))
            loaded.RepositoryRoot = discovered.RepositoryRoot;
        if (string.IsNullOrWhiteSpace(loaded.PythonPath))
            loaded.PythonPath = discovered.PythonPath;
        if (string.IsNullOrWhiteSpace(loaded.BridgeScriptPath))
            loaded.BridgeScriptPath = Path.Combine(loaded.RepositoryRoot, "ui", "bridge", "vocal_bridge.py");
        if (string.IsNullOrWhiteSpace(loaded.LibraryRoot))
            loaded.LibraryRoot = discovered.LibraryRoot;

        return loaded;
    }

    public void Save(string settingsPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(settingsPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        AtomicFile.WriteAllText(settingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>
    /// Voicebank root: the first subdirectory of &lt;repository&gt;/voicebank, if present.
    /// Keeps the repository portable — no absolute path is baked in.
    /// </summary>
    private static string DiscoverLibraryRoot(string repositoryRoot)
    {
        var voicebank = Path.Combine(repositoryRoot, "voicebank");
        if (Directory.Exists(voicebank))
        {
            var dirs = Directory.GetDirectories(voicebank);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            if (dirs.Length > 0)
                return dirs[0];
        }
        return voicebank;
    }

    private static string? FindRepositoryRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        for (var depth = 0; depth < 10 && dir is not null; depth++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VocalB1Synthesizer.py")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
