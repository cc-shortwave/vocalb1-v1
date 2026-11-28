using System.IO.Compression;
using System.Text.Json;
using VocalB1.Core.Configuration;
using VocalB1.Core.Serialization;

namespace VocalB1.Core.Scripting;

/// <summary>
/// Sealed storage for external node scripts that violated the sandbox policy.
/// A violating script is copied into a timestamped staging folder together with a manifest, the folder is
/// compressed into a read-only archive and its SHA-256 is blocked permanently. The original user file is
/// never modified; the hash block makes the same content impossible to execute again.
/// </summary>
public static class NodeScriptQuarantine
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Blocked = new(StringComparer.OrdinalIgnoreCase);

    private static string _root = string.Empty;
    private static bool _loaded;

    /// <summary>
    /// Default location: a <c>quarantine</c> folder next to <c>vocal_lib</c> (the repository root in a
    /// development tree, the portable package root after release), so archives travel with the installation.
    /// </summary>
    public static string DefaultRoot => Path.Combine(EngineOptions.Discover().RepositoryRoot, "quarantine");

    public static string Root
    {
        get
        {
            lock (Sync)
            {
                EnsureLoaded();
                return _root;
            }
        }
    }

    /// <summary>Switches the archive root (used by tests) and reloads the block index from it.</summary>
    public static void Configure(string root)
    {
        lock (Sync)
        {
            _root = string.IsNullOrWhiteSpace(root) ? DefaultRoot : root;
            Blocked.Clear();
            _loaded = false;
            EnsureLoaded();
        }
    }

    /// <summary>Whether this exact script content is sealed; returns the archive path and reason.</summary>
    public static bool IsBlocked(string sha256, out string archive, out string reason)
    {
        lock (Sync)
        {
            EnsureLoaded();
            if (Blocked.TryGetValue(sha256, out var entry))
            {
                archive = entry.Archive;
                reason = entry.Reason;
                return true;
            }
            archive = string.Empty;
            reason = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Compresses the offending script plus a manifest into a sealed archive and blocks its hash.
    /// Returns the archive path (empty when compression itself failed, the hash is blocked regardless).
    /// </summary>
    public static string Seal(string scriptPath, string sha256, string reason, string? detail)
    {
        lock (Sync)
        {
            EnsureLoaded();
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var name = $"{stamp}_{sha256[..Math.Min(12, sha256.Length)]}";
            var stage = Path.Combine(_root, "_stage_" + name);
            var archive = Path.Combine(_root, name + ".zip");

            try
            {
                Directory.CreateDirectory(_root);
                if (Directory.Exists(stage))
                    Directory.Delete(stage, true);
                Directory.CreateDirectory(stage);

                if (File.Exists(scriptPath))
                    File.Copy(scriptPath, Path.Combine(stage, "script.py"), true);

                var manifest = JsonSerializer.Serialize(new
                {
                    original = scriptPath,
                    sha256,
                    reason,
                    detail,
                    sealedAt = DateTime.Now.ToString("O"),
                }, ProjectJson.WriteOptions);
                AtomicFile.WriteAllText(Path.Combine(stage, "manifest.json"), manifest);

                if (File.Exists(archive))
                    File.Delete(archive);
                ZipFile.CreateFromDirectory(stage, archive, CompressionLevel.Optimal, false);
                Directory.Delete(stage, true);

                // "Sealed": the archive cannot be changed or deleted by an accidental overwrite.
                File.SetAttributes(archive, FileAttributes.ReadOnly);
            }
            catch
            {
                // Archiving is best effort: the hash block below is what actually isolates the script.
                try
                {
                    if (Directory.Exists(stage))
                        Directory.Delete(stage, true);
                }
                catch
                {
                    // staging cleanup is not critical
                }
                archive = string.Empty;
            }

            Blocked[sha256] = new Entry(scriptPath, reason, archive);
            SaveIndex();
            return archive;
        }
    }

    private static void EnsureLoaded()
    {
        if (string.IsNullOrEmpty(_root))
            _root = DefaultRoot;
        if (_loaded)
            return;
        _loaded = true;

        try
        {
            var path = Path.Combine(_root, "index.json");
            if (!File.Exists(path))
                return;

            var entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(
                File.ReadAllText(path), ProjectJson.ReadOptions);
            if (entries is null)
                return;
            foreach (var (sha, entry) in entries)
            {
                if (entry is not null && !string.IsNullOrWhiteSpace(sha))
                    Blocked[sha] = entry;
            }
        }
        catch
        {
            // A damaged index must not prevent the app from starting; unknown scripts stay in quarantine
            // only until the index can be read again.
        }
    }

    private static void SaveIndex()
    {
        try
        {
            Directory.CreateDirectory(_root);
            AtomicFile.WriteAllText(Path.Combine(_root, "index.json"),
                JsonSerializer.Serialize(Blocked, ProjectJson.WriteOptions));
        }
        catch
        {
            // The in-memory block still applies for the current session.
        }
    }

    private sealed class Entry
    {
        public Entry()
        {
        }

        public Entry(string script, string reason, string archive)
        {
            Script = script;
            Reason = reason;
            Archive = archive;
        }

        public string Script { get; set; } = string.Empty;

        public string Reason { get; set; } = string.Empty;

        public string Archive { get; set; } = string.Empty;
    }
}
