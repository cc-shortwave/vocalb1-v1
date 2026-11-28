using VocalB1.Core.Configuration;
using VocalB1.Core.Diagnostics;

namespace VocalB1.Core.Scripting;

/// <summary>
/// Installs an external node script dropped onto the window: the file is copied into
/// <c>&lt;repository&gt;/scripts/node_scripts</c> so it survives moves of the original file.
/// Installation is idempotent (identical content reuses the existing copy) and never overwrites a
/// different script with the same name.
/// </summary>
public static class NodeScriptInstaller
{
    /// <summary>Folder (below <c>scripts</c>) that holds installed node scripts.</summary>
    public const string FolderName = "node_scripts";

    /// <summary>Same limit as the sandbox so an installed script is always loadable.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>Copies <paramref name="sourcePath"/> into the script library and returns the installed path.</summary>
    public static string Install(string sourcePath, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new StageException(nameof(NodeScriptInstaller), "install", "没有提供要安装的脚本文件。");

        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
            throw new StageException(nameof(NodeScriptInstaller), "install", $"找不到脚本文件：{source}");
        if (!string.Equals(Path.GetExtension(source), ".py", StringComparison.OrdinalIgnoreCase))
            throw new StageException(nameof(NodeScriptInstaller), "install", $"只支持安装 .py 脚本：{source}");

        var bytes = File.ReadAllBytes(source);
        if (bytes.Length == 0)
            throw new StageException(nameof(NodeScriptInstaller), "install", $"脚本文件为空：{source}");
        if (bytes.Length > MaxBytes)
            throw new StageException(nameof(NodeScriptInstaller), "install",
                $"脚本超过 {MaxBytes / 1024} KB 上限：{source}");

        var root = string.IsNullOrWhiteSpace(repositoryRoot)
            ? EngineOptions.Discover().RepositoryRoot
            : repositoryRoot;
        var folder = Path.Combine(root, "scripts", FolderName);
        Directory.CreateDirectory(folder);

        var name = Path.GetFileName(source);
        var destination = Path.Combine(folder, name);
        if (File.Exists(destination))
        {
            if (HasSameContent(destination, bytes))
                return destination;

            var stem = Path.GetFileNameWithoutExtension(name);
            for (var index = 2; index < 1000; index++)
            {
                var candidate = Path.Combine(folder, $"{stem}_{index}.py");
                if (!File.Exists(candidate))
                {
                    destination = candidate;
                    break;
                }
                if (HasSameContent(candidate, bytes))
                    return candidate;
            }
        }

        File.Copy(source, destination, overwrite: false);
        return destination;
    }

    private static bool HasSameContent(string path, byte[] bytes)
    {
        try
        {
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
        }
        catch
        {
            return false;
        }
    }
}
