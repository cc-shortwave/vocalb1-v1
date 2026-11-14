namespace VocalB1.Core.Models;

/// <summary>
/// Blender-style data-block contract: every object that can be referenced by others has a stable id,
/// a user-editable name, and a "fake user" flag that keeps it alive when no other object references it.
/// </summary>
public interface IDatablock
{
    string Id { get; set; }

    string Name { get; set; }

    /// <summary>When true the data-block is never treated as an orphan, even with zero users.</summary>
    bool FakeUser { get; set; }
}

/// <summary>User-count information for one data-block, computed from the current project graph.</summary>
public sealed record DatablockUsage(int Users, bool IsOrphan)
{
    /// <summary>Display text used by the outliner, Blender style: "3 用户" / "0 用户（孤立）".</summary>
    public string Text => Users == 0 ? "0 用户（孤立）" : $"{Users} 用户";
}
