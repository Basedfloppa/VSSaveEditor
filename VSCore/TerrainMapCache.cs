using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VSCore;

/// <summary>
///     On-disk store of built terrain tiles. A tile costs 1.5–3.5 s to build (it probes and decodes the chunks
///     of its window) and roughly 130 MB of arrays to keep — on a large save the whole map is 4–5 GB, so it
///     cannot all stay in RAM. Written out compressed it fits a bounded cache directory, and reading it back
///     is far cheaper than probing the save again. That is what lets the background warm pass prepare every
///     tile, including the ones the user is not looking at.
///
///     The cache is a pure optimisation: a missing, unreadable or foreign directory disables it and the tile
///     is built as before. Staleness is excluded by the key: the source file identity (path, length, mtime),
///     the content version of the open save, and the format version of the store.
/// </summary>
public sealed class TerrainMapCache
{
    /// <summary>Cached tiles for one large save are ~40–60 MB each (44 of them on the biggest checked save).</summary>
    public const long DefaultMaxBytes = 3L * 1024 * 1024 * 1024;

    /// <summary>Prune down to this fraction of the limit so that pruning does not run on every tile.</summary>
    private const double PruneTarget = 0.9;

    private const string Extension = ".tile";

    private readonly string? _root;
    private readonly long _maxBytes;
    private readonly object _gate = new();

    public TerrainMapCache(string? root = null, long maxBytes = DefaultMaxBytes)
    {
        _root = root ?? DefaultRoot();
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
        if (_root is not null) Prune();
    }

    /// <summary>True when the cache has a usable directory. With it false every call is a cheap no-op.</summary>
    public bool IsEnabled => _root is not null;

    public string? Root => _root;

    /// <summary><c>VSSAVEEDITOR_MAP_CACHE</c> overrides the folder (tests, or putting the cache on another disk);
    /// otherwise the local application data folder — the same reasoning as the working-copy root: the temp
    /// folder may be a tmpfs, and a multi-GB cache in RAM defeats the point.</summary>
    private static string? DefaultRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("VSSAVEEDITOR_MAP_CACHE");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            try
            {
                Directory.CreateDirectory(overridden);
                return overridden;
            }
            catch { /* unwritable override: fall through to the default */ }
        }

        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
            {
                var root = Path.Combine(local, "VSSaveEditor", "mapcache");
                Directory.CreateDirectory(root);
                return root;
            }
        }
        catch { /* no usable profile: the cache is simply off */ }

        return null;
    }

    /// <summary>Loads a tile if a valid one is stored for this save/area. False means "build it".</summary>
    public bool TryLoad(VSSaveFile save, ChunkArea area, TerrainNeeds needs, int mode, out TerrainMap? map)
    {
        map = null;
        var path = FileFor(save, area, needs, mode);
        if (path is null) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            map = TerrainMapStore.Read(stream, save, area);
            // A tile built for another render mode has the wrong arrays for this one: treat it as a miss.
            if (map is not null && map.Needs != needs) map = null;
            return map is not null;
        }
        catch
        {
            // Unreadable or half-written: treat as a miss. A cache must never be the reason a save fails.
            map = null;
            return false;
        }
    }

    /// <summary>Stores a tile. Silently does nothing when the cache is disabled or the disk refuses the write:
    /// the warm pass must not fail because of the cache.</summary>
    public void Save(VSSaveFile save, ChunkArea area, TerrainNeeds needs, int mode, TerrainMap map)
    {
        var path = FileFor(save, area, needs, mode);
        if (path is null) return;
        // A unique temp name: the same tile can be produced twice at once — the background warm pass and a
        // scroll into that very area — and a shared ".tmp" made both writers fail on the locked file.
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            }

            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                TerrainMapStore.Write(stream, map);
            lock (_gate)
            {
                File.Move(tmp, path, true);   // a reader never sees a partially written tile
            }
        }
        catch (Exception ex)
        {
            // Silent by design (the warm pass must not fail because of the cache), but never invisible:
            // a cache that always fails to save looks exactly like a cache that always misses.
            ScanDiagnostics.Warn($"map tile not cached (area {area.Index + 1}, cx {area.MinCx}..{area.MaxCx})", ex);
            try { File.Delete(tmp); } catch { }
            return;
        }

        Prune();
    }

    /// <summary>Drops every tile built from this save (used after the content changed in place).</summary>
    public void Invalidate(VSSaveFile save)
    {
        if (_root is null) return;
        var dir = Path.Combine(_root, KeyFor(save));
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    /// <summary>Total size of the cached tiles, in bytes.</summary>
    public long BytesOnDisk()
    {
        if (_root is null) return 0;
        try
        {
            long sum = 0;
            foreach (var f in EnumerateTiles(_root))
            {
                try { sum += new FileInfo(f).Length; } catch { }
            }

            return sum;
        }
        catch { return 0; }
    }

    /// <summary>Number of cached tiles.</summary>
    public int Count
    {
        get
        {
            if (_root is null) return 0;
            try { return EnumerateTiles(_root).Count(); } catch { return 0; }
        }
    }

    /// <summary>Deletes the oldest tiles until the cache is back under its limit. Returns the bytes removed.</summary>
    public long Prune()
    {
        if (_root is null) return 0;
        lock (_gate)
        {
            long removed = 0;
            try
            {
                var files = EnumerateTiles(_root)
                    .Select(f => { try { return new FileInfo(f); } catch { return null; } })
                    .Where(f => f is { Exists: true })
                    .Select(f => f!)
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();
                long total = files.Sum(f => f.Length);
                if (total <= _maxBytes) return 0;
                long target = (long)(_maxBytes * PruneTarget);
                foreach (var f in files)
                {
                    if (total <= target) break;
                    try
                    {
                        long len = f.Length;
                        f.Delete();
                        total -= len;
                        removed += len;
                    }
                    catch { }
                }

                // Empty per-save folders left behind by the deletions.
                foreach (var dir in Directory.EnumerateDirectories(_root))
                {
                    try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
                }
            }
            catch { }

            return removed;
        }
    }

    private static IEnumerable<string> EnumerateTiles(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*" + Extension); } catch { continue; }
            foreach (var f in files) yield return f;
        }
    }

    /// <summary>Where the tile of this area lives, or null when the cache is off.
    /// The area BOUNDS are part of the name, not its index: if the save grew and the tiles were cut
    /// differently, an index would point at another part of the world and serve the wrong map.</summary>
    private string? FileFor(VSSaveFile save, ChunkArea area, TerrainNeeds needs, int mode)
    {
        if (_root is null) return null;
        var name = FormattableString.Invariant(
            $"{area.MinCx}_{area.MinCz}_{area.MaxCx}_{area.MaxCz}_n{(int)needs}_m{mode}{Extension}");
        return Path.Combine(_root, KeyFor(save), name);
    }

    /// <summary>Folder of the save being read: the original file identity, so a working copy of the same save
    /// shares its cache, and an edit (which bumps the content version) rotates it.</summary>
    private static string KeyFor(VSSaveFile save)
    {
        var identity = save.SourcePath ?? save.Path;
        long len = 0, mtime = 0;
        try
        {
            var fi = new FileInfo(identity);
            if (fi.Exists)
            {
                len = fi.Length;
                mtime = fi.LastWriteTimeUtc.Ticks;
            }
        }
        catch { /* an unreadable identity only means a weaker key, not a failure */ }

        var raw = $"{identity}|{len}|{mtime}|{save.ContentVersion}|{TerrainMapStore.FormatVersion}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
