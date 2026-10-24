using System;
using System.Collections.Generic;
using System.IO;

namespace VSCore;

/// <summary>List of recently opened saves: MRU order, deduplication by full path, length limit.
/// Like <see cref="EditJournal"/>, it lives in VSCore — pure logic without UI, covered by unit tests.</summary>
public static class RecentFileList
{
    public const int DefaultCapacity = 10;

    /// <summary>Puts the path first and drops any previous occurrence. An empty path leaves the list unchanged.</summary>
    public static List<string> Push(IReadOnlyList<string>? current, string? path, int capacity = DefaultCapacity)
    {
        var res = new List<string>();
        if (current is null || capacity <= 0) return res;
        var key = Normalize(path);
        if (key.Length == 0) { res.AddRange(current); return res; }
        res.Add(path!);
        foreach (var p in current)
        {
            if (res.Count >= capacity) break;
            if (p is null || SamePath(p, key)) continue;
            res.Add(p);
        }
        return res;
    }

    public static List<string> Remove(IReadOnlyList<string>? current, string? path)
    {
        var res = new List<string>();
        if (current is null) return res;
        var key = Normalize(path);
        foreach (var p in current)
            if (p is not null && !SamePath(p, key)) res.Add(p);
        return res;
    }

    /// <summary>Whether the file exists on disk. The check never throws: an inaccessible path simply counts as "no file".</summary>
    public static bool Exists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path); } catch { return false; }
    }

    /// <summary>Compares paths by full name: the same save must not take up two entries.
    /// Case is ignored — on Windows the path to the same file may differ in case.</summary>
    public static bool SamePath(string? a, string? b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path!.Trim(); }
    }
}
