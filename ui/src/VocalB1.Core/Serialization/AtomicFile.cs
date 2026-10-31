using System.IO;

namespace VocalB1.Core.Serialization;

/// <summary>
/// Writes a text file through a sibling temp file and a replace move, so an interrupted
/// save never truncates the previous content.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }
}
