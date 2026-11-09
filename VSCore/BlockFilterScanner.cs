using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VSCore;

/// <summary>Read-only lookup over a block-match map. Both the single-grid map built for one
/// area and the sparse whole-save overlay implement it, so the map view and the image export
/// do not need to know how many grids the result consists of.</summary>
public interface IBlockFilter
{
    int HitColumns { get; }
    long MatchCells { get; }
    IReadOnlyDictionary<int, long> IdCells { get; }

    bool TryGet(int wx, int wz, out ushort id, out short y, out byte count);
    bool TryFindNext(int fromX, int fromZ, bool forward, out int x, out int z);
}

/// <summary>
/// Map of selected-block matches over the world columns — the basis of the map overlay
/// ("show only ore X, dim everything else").
/// The dimensions and MinX/MinZ match <see cref="TerrainMap"/>, so the overlay sits on the map 1:1.
/// </summary>
public sealed class BlockFilterMap : IBlockFilter
{
    public int MinX, MinZ, Width, Height;

    /// <summary>id of the found block in the column (0 = no match).</summary>
    public ushort[] TopBlock = Array.Empty<ushort>();

    /// <summary>Absolute Y of the TOPMOST match in the column (0 = none).</summary>
    public short[] TopY = Array.Empty<short>();

    /// <summary>How many selected-block cells the column has (saturates at 255).</summary>
    public byte[] Count = Array.Empty<byte>();

    public long MatchCells;                 // total matched cells in the world
    public int HitColumns;                  // columns with at least one match
    public Dictionary<int, long> IdCells = new();   // id -> cells (for the legend)

    int IBlockFilter.HitColumns => HitColumns;
    long IBlockFilter.MatchCells => MatchCells;
    IReadOnlyDictionary<int, long> IBlockFilter.IdCells => IdCells;

    public int Index(int wx, int wz) => (wz - MinZ) * Width + (wx - MinX);

    public bool Contains(int wx, int wz)
        => wx >= MinX && wz >= MinZ && wx < MinX + Width && wz < MinZ + Height;

    public bool Has(int wx, int wz)
        => Contains(wx, wz) && Count[Index(wx, wz)] > 0;

    public bool TryGet(int wx, int wz, out ushort id, out short y, out byte count)
    {
        id = 0; y = 0; count = 0;
        if (!Contains(wx, wz)) return false;
        int i = Index(wx, wz);
        count = Count[i];
        if (count == 0) return false;
        id = TopBlock[i];
        y = TopY[i];
        return true;
    }

    /// <summary>
    /// Finds the nearest matching column after/before (fromX,fromZ) in row order (Z first, then X),
    /// wrapping around. The list of matches is not materialised (there may be millions) — we walk the array.
    /// </summary>
    public bool TryFindNext(int fromX, int fromZ, bool forward, out int x, out int z)
    {
        x = 0; z = 0;
        int cells = Width * Height;
        if (cells <= 0 || Count.Length < cells) return false;
        int sx = Math.Clamp(fromX, MinX, MinX + Width - 1);
        int sz = Math.Clamp(fromZ, MinZ, MinZ + Height - 1);
        int start = (sz - MinZ) * Width + (sx - MinX);
        for (int step = 1; step <= cells; step++)
        {
            int idx = forward ? (start + step) % cells : (start - step + 2 * cells) % cells;
            if (Count[idx] == 0) continue;
            z = MinZ + idx / Width;
            x = MinX + idx % Width;
            return true;
        }
        return false;
    }

    /// <summary>First match in row order (no wrap).</summary>
    internal bool TryFirstHit(out int x, out int z)
    {
        for (int i = 0; i < Count.Length; i++)
        {
            if (Count[i] == 0) continue;
            z = MinZ + i / Width;
            x = MinX + i % Width;
            return true;
        }
        x = z = 0;
        return false;
    }

    /// <summary>Last match in row order (no wrap).</summary>
    internal bool TryLastHit(out int x, out int z)
    {
        for (int i = Count.Length - 1; i >= 0; i--)
        {
            if (Count[i] == 0) continue;
            z = MinZ + i / Width;
            x = MinX + i % Width;
            return true;
        }
        x = z = 0;
        return false;
    }

    /// <summary>First match strictly after/before (fromX,fromZ) inside this grid (no wrap).</summary>
    internal bool TryFindAfter(int fromX, int fromZ, bool forward, out int x, out int z)
    {
        x = z = 0;
        if (!Contains(fromX, fromZ)) return false;
        int start = Index(fromX, fromZ);
        if (forward)
        {
            for (int i = start + 1; i < Count.Length; i++)
            {
                if (Count[i] == 0) continue;
                z = MinZ + i / Width;
                x = MinX + i % Width;
                return true;
            }
        }
        else
        {
            for (int i = start - 1; i >= 0; i--)
            {
                if (Count[i] == 0) continue;
                z = MinZ + i / Width;
                x = MinX + i % Width;
                return true;
            }
        }
        return false;
    }
}

/// <summary>A small pre-coloured overlay image over the whole save, used where a full map is not
/// cached (a pyramid overview). It is built once from the sparse parts, never per frame.</summary>
public sealed class BlockFilterPreview
{
    public int MinX, MinZ, Width, Height; // world bounds
    public int CellSize;                  // blocks per pixel
    public int PixelWidth, PixelHeight;
    /// <summary>Top block id per pixel (0 = no match). The view colours it with the same palette
    /// as the exact layer, so a coarse area and the active tile never disagree.</summary>
    public ushort[] Ids = Array.Empty<ushort>();

    public double PpbX => PixelWidth / (double)Width;
    public double PpbY => PixelHeight / (double)Height;
}

/// <summary>
/// Sparse whole-save overlay: one tiny <see cref="BlockFilterMap"/> per chunk that actually contains
/// a selected block. A save can be 500 000 blocks wide, so a dense grid over the world bounds is not
/// an option (it would be hundreds of MiB even when the search finds a handful of ore columns).
/// The per-chunk parts keep the overlay proportional to the matches, not to the world size.
/// </summary>
public sealed class BlockFilterOverlay : IBlockFilter
{
    private readonly Dictionary<long, BlockFilterMap> _parts;
    private readonly long[] _order; // chunk keys sorted by (cz, cx) — the same row order as the map

    internal BlockFilterOverlay(Dictionary<long, BlockFilterMap> parts, BlockFilterPreview? preview = null)
    {
        _parts = parts;
        Preview = preview;
        _order = parts.Keys.OrderBy(OrderKey).ToArray();

        int hits = 0;
        long cells = 0;
        var ids = new Dictionary<int, long>();
        foreach (var part in parts.Values)
        {
            hits += part.HitColumns;
            cells += part.MatchCells;
            foreach (var kv in part.IdCells)
                ids[kv.Key] = ids.TryGetValue(kv.Key, out var n) ? n + kv.Value : kv.Value;
        }
        HitColumns = hits;
        MatchCells = cells;
        IdCells = ids;
    }

    public int HitColumns { get; }
    public long MatchCells { get; }
    public IReadOnlyDictionary<int, long> IdCells { get; }

    /// <summary>Rasterises the sparse parts into at most maxDim×maxDim pixels (transparent where no match).</summary>
    internal static BlockFilterPreview? BuildPreview(IReadOnlyDictionary<long, BlockFilterMap> parts, int maxDim = 2048)
    {
        if (parts.Count == 0) return null;
        int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
        foreach (var part in parts.Values)
        {
            minX = Math.Min(minX, part.MinX);
            minZ = Math.Min(minZ, part.MinZ);
            maxX = Math.Max(maxX, part.MinX + part.Width - 1);
            maxZ = Math.Max(maxZ, part.MinZ + part.Height - 1);
        }
        int width = maxX - minX + 1, height = maxZ - minZ + 1;
        int cell = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)Math.Max(1, maxDim)));
        int pw = Math.Max(1, (int)Math.Ceiling(width / (double)cell));
        int ph = Math.Max(1, (int)Math.Ceiling(height / (double)cell));
        var ids = new ushort[pw * ph];
        foreach (var part in parts.Values)
        {
            for (int i = 0; i < part.Count.Length; i++)
            {
                if (part.Count[i] == 0) continue;
                int wx = part.MinX + i % part.Width;
                int wz = part.MinZ + i / part.Width;
                int x = Math.Clamp((wx - minX) / cell, 0, pw - 1);
                int z = Math.Clamp((wz - minZ) / cell, 0, ph - 1);
                ids[z * pw + x] = part.TopBlock[i];
            }
        }
        return new BlockFilterPreview
        {
            MinX = minX, MinZ = minZ, Width = width, Height = height,
            CellSize = cell, PixelWidth = pw, PixelHeight = ph, Ids = ids
        };
    }

    /// <summary>Number of chunks that own at least one highlighted column.</summary>
    public int PartCount => _parts.Count;

    /// <summary>Coarse whole-save image for the areas that have only a cached pyramid.</summary>
    public BlockFilterPreview? Preview { get; }

    private static long OrderKey(long packed)
    {
        var (cx, cz) = ChunkAreas.UnpackColumn(packed);
        return ((long)cz << 32) ^ (uint)cx;
    }

    public bool TryGet(int wx, int wz, out ushort id, out short y, out byte count)
    {
        id = 0; y = 0; count = 0;
        long key = ChunkAreas.PackColumn(wx >> 5, wz >> 5);
        return _parts.TryGetValue(key, out var part) && part.TryGet(wx, wz, out id, out y, out count);
    }

    /// <summary>Next/previous match in global (Z, X) row order, wrapping around.</summary>
    public bool TryFindNext(int fromX, int fromZ, bool forward, out int x, out int z)
    {
        x = z = 0;
        if (_order.Length == 0) return false;
        int fromCx = fromX >> 5, fromCz = fromZ >> 5;
        long startKey = ((long)fromCz << 32) ^ (uint)fromCx;
        int at = Array.BinarySearch(_order, startKey);
        int insertion = at >= 0 ? at : ~at;

        if (forward)
        {
            // Current chunk: only columns strictly after the cursor.
            if (at >= 0 && _parts[_order[at]].TryFindAfter(fromX, fromZ, true, out x, out z)) return true;
            for (int j = insertion + (at >= 0 ? 1 : 0); j < _order.Length; j++)
                if (_parts[_order[j]].TryFirstHit(out x, out z)) return true;
            for (int j = 0; j < insertion; j++)
                if (_parts[_order[j]].TryFirstHit(out x, out z)) return true;
            return false;
        }

        // Current chunk: only columns strictly before the cursor.
        if (at >= 0 && _parts[_order[at]].TryFindAfter(fromX, fromZ, false, out x, out z)) return true;
        for (int j = insertion - 1; j >= 0; j--)
            if (_parts[_order[j]].TryLastHit(out x, out z)) return true;
        for (int j = _order.Length - 1; j >= insertion; j--)
            if (_parts[_order[j]].TryLastHit(out x, out z)) return true;
        return false;
    }
}

/// <summary>
/// Search for selected blocks (ores/stones/resources) across all world columns.
/// 
/// Key optimisation: the chunk PALETTE is read first — it yields every id present in the chunk
/// and does NOT require unpacking the bit planes (the heaviest part). If the wanted ids are not in the palette,
/// the chunk is not decoded at all. Chunks are grouped by (cx,cz): a single worker owns a whole world column,
/// so writes to the shared arrays never overlap and need no locking.
/// </summary>
public static class BlockFilterScanner
{
    /// <summary>
    ///     Builds the overlay for the grid. <paramref name="scope" /> narrows the scan to a rectangle of chunks
    ///     (the "Find blocks" tab's "Search in"): the arrays still cover the whole grid — they are aligned with the map
    ///     1:1, and a smaller map would draw the highlights at the wrong place — but the chunks outside it are never
    ///     read, so the match counters and the legend describe the scope.
    /// </summary>
    public static BlockFilterMap Build(VSSaveFile save, TerrainMap grid, IReadOnlyCollection<int> blockIds,
        IProgress<double>? progress = null, CancellationToken ct = default, SearchScope? scope = null)
    {
        var map = new BlockFilterMap
        {
            MinX = grid.MinX,
            MinZ = grid.MinZ,
            Width = Math.Max(1, grid.Width),
            Height = Math.Max(1, grid.Height)
        };
        int cells = map.Width * map.Height;
        map.TopBlock = new ushort[cells];
        map.TopY = new short[cells];
        map.Count = new byte[cells];
        if (blockIds.Count == 0 || cells <= 0) return map;

        // an id mask instead of a HashSet: an array lookup per each of the 32768 chunk cells is noticeably cheaper
        var wantedMask = MakeMask(blockIds);

        // Only chunks of the region the grid was built for: the overlay lands on the map 1:1,
        // and blobs of other regions are not read (otherwise a one-region overlay would scan the whole save).
        var rows = save.ChunkRowsInWindow(grid.Area);
        var groups = rows
            .GroupBy(r => { var p = ChunkPosHelper.DecodeIndex((ulong)r.Position); return (p.X, p.Z); })
            // the scope check is before the palette read: a chunk outside the scope is not even touched
            .Where(g => scope is null || scope.ContainsChunk(g.Key.X, g.Key.Z))
            .ToList();

        Scan(save, groups, wantedMask, map, progress, ct);
        return map;
    }

    /// <summary>
    ///     Builds a sparse overlay across the WHOLE save (or the scope), not just the active build
    ///     tile. This is what makes "Show on map" cover every area of the map: the old code built
    ///     the filter against the active tile, so after the map was split into 16 Mi tiles the overlay
    ///     covered only one small tile. The scan is paged, so it does not retain every blob at once.
    /// </summary>
    public static BlockFilterOverlay BuildAll(VSSaveFile save, IReadOnlyCollection<int> blockIds,
        IProgress<double>? progress = null, CancellationToken ct = default, SearchScope? scope = null)
    {
        var parts = new ConcurrentDictionary<long, BlockFilterMap>();
        if (blockIds.Count == 0) return new BlockFilterOverlay(new Dictionary<long, BlockFilterMap>());

        var wantedMask = MakeMask(blockIds);
        // Count only: this used to materialise all 4 M positions (and read every chunk page) just to
        // size the progress bar.
        long totalRows = save.CountRows("chunk");
        long done = 0;
        long after = long.MinValue;
        var opts = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct
        };

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var rows = save.ReadChunkRowsPage(after, 2048);
            if (rows.Count == 0) break;
            after = rows[^1].Position;

            var groups = rows
                .GroupBy(r => { var p = ChunkPosHelper.DecodeIndex((ulong)r.Position); return (p.X, p.Z); })
                .Where(g => scope is null || scope.ContainsChunk(g.Key.X, g.Key.Z))
                .ToList();

            Parallel.For(0, groups.Count, opts, gi =>
            {
                var g = groups[gi];
                var hits = new List<(int X, int Z, int Id, int Y)>();
                long localCells = 0;
                Dictionary<int, long>? localIds = null;
                foreach (var row in g)
                {
                    ct.ThrowIfCancellationRequested();
                    var data = row.Data;
                    if (data is null || data.Length == 0) continue;
                    if (!PaletteHasWanted(ChunkBlockDecoder.DecodePalette(data), wantedMask) &&
                        !PaletteHasWanted(ChunkBlockDecoder.DecodeLiquidPalette(data), wantedMask))
                        continue;

                    ushort[] blocks;
                    try { blocks = ChunkBlockDecoder.DecodeChunkBlocksMergedOnePass(data); }
                    catch { continue; }

                    var (cx, cy, cz) = ChunkPosHelper.DecodeIndex((ulong)row.Position);
                    int wxBase = cx * 32, wzBase = cz * 32, wyBase = cy * 32;
                    for (int y = 31; y >= 0; y--)
                    {
                        int plane = 1024 * y;
                        for (int col = 0; col < 1024; col++)
                        {
                            int id = blocks[plane + col];
                            if (id == 0 || !wantedMask[id]) continue;
                            int wx = wxBase + (col & 31), wz = wzBase + (col >> 5);
                            localCells++;
                            (localIds ??= new Dictionary<int, long>())[id] =
                                localIds.TryGetValue(id, out var idCount) ? idCount + 1 : 1;
                            hits.Add((wx, wz, id, wyBase + y));
                        }
                    }
                }

                if (hits.Count > 0)
                {
                    long key = ChunkAreas.PackColumn(g.Key.X, g.Key.Z);
                    var part = parts.GetOrAdd(key, _ => new BlockFilterMap
                    {
                        MinX = g.Key.X * 32,
                        MinZ = g.Key.Z * 32,
                        Width = 32,
                        Height = 32,
                        TopBlock = new ushort[1024],
                        TopY = new short[1024],
                        Count = new byte[1024]
                    });
                    lock (part)
                    {
                        foreach (var hit in hits)
                        {
                            int li = (hit.Z - part.MinZ) * 32 + (hit.X - part.MinX);
                            if (part.Count[li] < 255) part.Count[li]++;
                            if (part.TopBlock[li] == 0 || hit.Y > part.TopY[li])
                            {
                                part.TopY[li] = (short)hit.Y;
                                part.TopBlock[li] = (ushort)hit.Id;
                            }
                        }
                        part.MatchCells += localCells;
                        if (localIds is not null)
                            foreach (var kv in localIds)
                                part.IdCells[kv.Key] = part.IdCells.TryGetValue(kv.Key, out var c) ? c + kv.Value : kv.Value;
                    }
                }

            });

            done += rows.Count;
            progress?.Report(totalRows <= 0 ? 1 : Math.Min(1.0, (double)done / totalRows));
        }

        // The parts carry Count/TopBlock/TopY; their MatchCells/HitColumns are cheap to finalise here.
        foreach (var part in parts.Values)
        {
            int hits = 0;
            for (int i = 0; i < part.Count.Length; i++)
                if (part.Count[i] > 0) hits++;
            part.HitColumns = hits;
        }

        var snapshot = new Dictionary<long, BlockFilterMap>(parts);
        return new BlockFilterOverlay(snapshot, BlockFilterOverlay.BuildPreview(snapshot));
    }

    private static bool[] MakeMask(IReadOnlyCollection<int> blockIds)
    {
        var mask = new bool[ushort.MaxValue + 1];
        foreach (var id in blockIds)
            if (id >= 0 && id <= ushort.MaxValue) mask[id] = true;
        return mask;
    }

    private static void Scan(VSSaveFile save, List<IGrouping<(int X, int Z), VSSaveFile.ChunkRow>> groups,
        bool[] wantedMask, BlockFilterMap map, IProgress<double>? progress, CancellationToken ct)
    {
        long matchCells = 0;
        int done = 0;
        var idCells = new Dictionary<int, long>();
        var opts = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct
        };

        Parallel.For(0, groups.Count, opts, gi =>
        {
            long localCells = 0;
            Dictionary<int, long>? localIds = null;
            foreach (var row in groups[gi])
            {
                opts.CancellationToken.ThrowIfCancellationRequested();
                var data = row.Data;
                if (data is null || data.Length == 0) continue;

                if (!PaletteHasWanted(ChunkBlockDecoder.DecodePalette(data), wantedMask) &&
                    !PaletteHasWanted(ChunkBlockDecoder.DecodeLiquidPalette(data), wantedMask))
                    continue;

                ushort[] blocks;
                try { blocks = ChunkBlockDecoder.DecodeChunkBlocksMergedOnePass(data); }
                catch { continue; }

                var (cx, cy, cz) = ChunkPosHelper.DecodeIndex((ulong)row.Position);
                int wxBase = cx * 32, wzBase = cz * 32, wyBase = cy * 32;
                for (int y = 31; y >= 0; y--)
                {
                    int plane = 1024 * y;
                    for (int col = 0; col < 1024; col++)
                    {
                        int id = blocks[plane + col];
                        if (id == 0 || !wantedMask[id]) continue;
                        int wx = wxBase + (col & 31), wz = wzBase + (col >> 5);
                        if (wx < map.MinX || wz < map.MinZ ||
                            wx >= map.MinX + map.Width || wz >= map.MinZ + map.Height) continue;
                        int gi2 = (wz - map.MinZ) * map.Width + (wx - map.MinX);
                        localCells++;
                        (localIds ??= new Dictionary<int, long>())[id] = localIds.TryGetValue(id, out var c) ? c + 1 : 1;
                        if (map.Count[gi2] < 255) map.Count[gi2]++;
                        int wy = wyBase + y;
                        if (map.TopBlock[gi2] == 0 || wy > map.TopY[gi2])
                        {
                            map.TopY[gi2] = (short)wy;
                            map.TopBlock[gi2] = (ushort)id;
                        }
                    }
                }
            }
            Interlocked.Add(ref matchCells, localCells);
            if (localIds is not null)
                lock (idCells)
                    foreach (var kv in localIds)
                        idCells[kv.Key] = idCells.TryGetValue(kv.Key, out var c) ? c + kv.Value : kv.Value;
            int d = Interlocked.Increment(ref done);
            if (progress is not null && (d % 32 == 0 || d == groups.Count))
                progress.Report((double)d / Math.Max(1, groups.Count));
        });

        map.MatchCells = matchCells;
        map.IdCells = idCells;
        int hits = 0;
        for (int i = 0; i < map.Count.Length; i++) if (map.Count[i] > 0) hits++;
        map.HitColumns = hits;
    }

    private static bool PaletteHasWanted(int[] palette, bool[] wantedMask)
    {
        for (int i = 0; i < palette.Length; i++)
        {
            int id = palette[i];
            if (id > 0 && id <= ushort.MaxValue && wantedMask[id]) return true;
        }
        return false;
    }
}
