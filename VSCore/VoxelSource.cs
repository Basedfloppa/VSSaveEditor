using System;
using System.Collections.Concurrent;

namespace VSCore;

/// <summary>
/// On-demand source of chunk voxels (32×32×32). Needed for Y slices: the surface map
/// (<see cref="TerrainMap"/>) only knows the top block of a column, while drawing a Y level
/// "as it is" requires the real block at exactly that level.
/// </summary>
public interface IVoxelSource
{
    /// <summary>Chunk blocks (index = x + z*32 + y*1024, see <see cref="ChunkPosHelper.Index3D"/>)
    /// or null if there is no chunk / the data is unavailable. Liquids are already merged with solid blocks.</summary>
    ushort[]? GetChunkBlocks(int cx, int cy, int cz);

    /// <summary>How many chunks are in the cache right now (diagnostics/tests).</summary>
    int CachedChunks { get; }
}

/// <summary>
/// Reads chunk voxels from the save ON DEMAND (SQLite `chunk` by position) and caches the most recently
/// decoded chunks. This is essential for "redrawing the active part of the map": in a Y slice only those
/// chunks that fall into the visible area are decoded (tens of them at a typical zoom level), not the whole
/// world (tens of thousands). The cache is reused between rebuilds, so moving the slider
/// inside one 32-block band (one cy) requires no re-decoding at all.
///
/// Locking: the save's SQLite connection is not thread-safe, so reading a blob happens under
/// <see cref="_ioLock"/>; decoding (the most expensive part) happens outside the lock.
/// </summary>
public sealed class SaveVoxelSource : IVoxelSource
{
    /// <summary>By default ~64 MB of decoded chunks: enough for a fully detailed view
    /// (1280×800 blocks ≈ 1000 chunks) without bloating memory on large worlds.</summary>
    public const int DefaultCapacity = 1024;

    private readonly Func<long, byte[]?> _load;
    private readonly int _capacity;
    private readonly ConcurrentDictionary<long, ushort[]?> _cache = new();
    private readonly ConcurrentQueue<long> _order = new();
    private readonly object _ioLock = new();
    private long _misses, _decoded;

    public SaveVoxelSource(Func<long, byte[]?> load, int capacity = DefaultCapacity)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _capacity = Math.Max(1, capacity);
    }

    public int CachedChunks => _cache.Count;

    /// <summary>How many times a chunk was decoded (after the cache) — diagnostics of cache efficiency.</summary>
    public long DecodedChunks => System.Threading.Interlocked.Read(ref _decoded);

    /// <summary>How many lookups missed the cache.</summary>
    public long CacheMisses => System.Threading.Interlocked.Read(ref _misses);

    public ushort[]? GetChunkBlocks(int cx, int cy, int cz)
    {
        // the chunk position in the `chunk` table is exactly the packed (cx,cy,cz) (ChunkPosHelper).
        long position = (long)ChunkPosHelper.ToIndex(cx, cy, cz);
        if (_cache.TryGetValue(position, out var cached)) return cached;

        System.Threading.Interlocked.Increment(ref _misses);
        byte[]? blob;
        try { lock (_ioLock) blob = _load(position); }
        catch { blob = null; }   // the save is closed/damaged — just "no data" rather than a render crash

        ushort[]? blocks = null;
        if (blob is { Length: > 0 })
        {
            try { blocks = ChunkBlockDecoder.DecodeChunkBlocksMergedOnePass(blob); }
            catch { blocks = null; }
        }
        System.Threading.Interlocked.Increment(ref _decoded);

        if (_cache.TryAdd(position, blocks))
        {
            _order.Enqueue(position);
            // FIFO eviction: we keep the cache within a memory budget (a chunk = 32×32×32 ushort = 64 KB)
            while (_order.Count > _capacity && _order.TryDequeue(out long old))
                _cache.TryRemove(old, out _);
        }
        return blocks;
    }
}
