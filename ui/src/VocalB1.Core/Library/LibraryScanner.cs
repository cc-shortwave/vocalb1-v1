namespace VocalB1.Core.Library;

public sealed record LibraryEntry(string Path, string DisplayName, long SizeBytes)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Voicebank directory scan: lists only wav files and their sizes; no pitch detection (that belongs to the engine side).
/// </summary>
public static class LibraryScanner
{
    private static readonly string[] Extensions = [".wav", ".wave"];

    public static IReadOnlyList<LibraryEntry> Scan(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return [];

        return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(p => Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCulture)
            .Select(p => new LibraryEntry(
                p,
                Path.GetFileNameWithoutExtension(p).TrimStart('_'),
                new FileInfo(p).Length))
            .ToList();
    }
}
