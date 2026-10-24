using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VSCore;

/// <summary>
/// Map of selected-block matches over the world columns — the basis of the map overlay
/// ("show only ore X, dim everything else").
/// The dimensions and MinX/MinZ match <see cref="TerrainMap"/>, so the overlay sits on the map 1:1.
/// </summary>
public sealed class BlockFilterMap
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

    public int Index(int wx, int wz) => (wz - MinZ) * Width + (wx - MinX);

    public bool Contains(int wx, int wz)
        => wx >= MinX && wz >= MinZ && wx < MinX + Width && wz < MinZ + Height;

    public bool Has(int wx, int wz)
        => Contains(wx, wz) && Count[Index(wx, wz)] > 0;

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
    public static BlockFilterMap Build(VSSaveFile save, TerrainMap grid, IReadOnlyCollection<int> blockIds,
        IProgress<double>? progress = null, CancellationToken ct = default)
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
        var wantedMask = new bool[ushort.MaxValue + 1];
        foreach (var id in blockIds)
            if (id >= 0 && id <= ushort.MaxValue) wantedMask[id] = true;

        // Only chunks of the region the grid was built for: the overlay lands on the map 1:1,
        // and blobs of other regions are not read (otherwise a one-region overlay would scan the whole save).
        var rows = save.ChunkRowsInWindow(grid.Area);
        var groups = rows
            .GroupBy(r => { var p = ChunkPosHelper.DecodeIndex((ulong)r.Position); return (p.X, p.Z); })
            .ToList();

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

                // 1) cheap palette check (blocks + liquids) — without unpacking the planes
                if (!PaletteHasWanted(ChunkBlockDecoder.DecodePalette(data), wantedMask) &&
                    !PaletteHasWanted(ChunkBlockDecoder.DecodeLiquidPalette(data), wantedMask))
                    continue;

                // 2) full decoding only for chunks that hold at least one selected block
                ushort[] blocks;
                try { blocks = ChunkBlockDecoder.DecodeChunkBlocksMergedOnePass(data); }
                catch { continue; }

                var (cx, cy, cz) = ChunkPosHelper.DecodeIndex((ulong)row.Position);
                int wxBase = cx * 32, wzBase = cz * 32, wyBase = cy * 32;
                for (int y = 31; y >= 0; y--)   // top down: the first match is the topmost one
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
        for (int i = 0; i < cells; i++) if (map.Count[i] > 0) hits++;
        map.HitColumns = hits;
        return map;
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
