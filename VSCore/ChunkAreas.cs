using System;
using System.Collections.Generic;
using System.Linq;

namespace VSCore;

/// <summary>
/// The rectangle of a connected group of (cx,cz) columns — a world "area" that the map shows
/// in its own window. A save may contain several such groups: a typical case is a pregen
/// started away from spawn, which adds a patch of chunks at the edge of the map.
/// One map for all groups cannot be built: the bounding box of two patches at opposite edges
/// of a 512000×512000 world is ~6.6e10 columns, i.e. hundreds of GiB (see <see cref="TerrainMap.MaxCells"/>),
/// so the map is built for one area and the others are loaded when the user scrolls further.
/// </summary>
public sealed class ChunkArea
{
    /// <summary>Position in <see cref="ChunkAreas.Areas"/>; 0 — the area that opens first.</summary>
    public int Index { get; set; }

    public int MinCx { get; init; }
    public int MinCz { get; init; }
    public int MaxCx { get; init; }
    public int MaxCz { get; init; }

    /// <summary>(cx,cz) columns in the area — the area's "size" for sorting and labels.</summary>
    public int Columns { get; init; }

    /// <summary>Chunk records (cx,cy,cz) — how much data the area holds in total.</summary>
    public int Rows { get; init; }

    /// <summary>The area containing the world centre (where spawn is) — that is the one that opens first.
    /// Set by <see cref="ChunkAreas.Build(IReadOnlyCollection{long}, (int, int)?)"/> after sorting.</summary>
    public bool IsMain { get; set; }

    public int MinX => MinCx * 32;
    public int MinZ => MinCz * 32;
    public int MaxX => MaxCx * 32 + 31;
    public int MaxZ => MaxCz * 32 + 31;
    public int WidthBlocks => (MaxCx - MinCx + 1) * 32;
    public int HeightBlocks => (MaxCz - MinCz + 1) * 32;

    /// <summary>Bounding box area in blocks (not the "population"): that is what sizes the map arrays.</summary>
    public long BlockCells => (long)WidthBlocks * HeightBlocks;

    public double CentreX => MinX + WidthBlocks / 2.0;
    public double CentreZ => MinZ + HeightBlocks / 2.0;

    public bool ContainsChunk(int cx, int cz)
        => cx >= MinCx && cx <= MaxCx && cz >= MinCz && cz <= MaxCz;

    public bool ContainsBlock(double wx, double wz)
        => wx >= MinX && wx <= MaxX && wz >= MinZ && wz <= MaxZ;

    /// <summary>Distance from a point to the area rectangle (0 inside) — for picking the nearest area.</summary>
    public double DistanceTo(double wx, double wz)
    {
        double dx = Math.Max(0, Math.Max(MinX - wx, wx - MaxX));
        double dz = Math.Max(0, Math.Max(MinZ - wz, wz - MaxZ));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Short label for the status/metadata: "area 2 of 3 — 8 715 chunks, X … Z …".</summary>
    public string Label(int total) => $"area {Index + 1} of {total} — {Columns:N0} chunks, X {MinX}..{MaxX}, Z {MinZ}..{MaxZ}";
}

/// <summary>
/// All areas (connected groups of chunks) of a save plus the map panning domain.
/// Built from chunk positions (no data is read) — a fraction of a second even for 155,000 rows.
/// </summary>
public sealed class ChunkAreas
{
    private readonly ChunkArea[] _areas;

    private ChunkAreas(ChunkArea[] areas, (int MinX, int MinZ, int MaxX, int MaxZ) worldBounds,
        int? worldWidth, int? worldLength, int minWorldY, int maxWorldY, int totalColumns, int totalRows)
    {
        _areas = areas;
        WorldBounds = worldBounds;
        WorldWidth = worldWidth;
        WorldLength = worldLength;
        MinWorldY = minWorldY;
        MaxWorldY = maxWorldY;
        TotalColumns = totalColumns;
        TotalRows = totalRows;
    }

    public IReadOnlyList<ChunkArea> Areas => _areas;

    /// <summary>The area that opens first (the world centre, otherwise the largest one);
    /// null — the save has no chunks at all.</summary>
    public ChunkArea? Main => _areas.Length > 0 ? _areas[0] : null;

    /// <summary>Pan/zoom domain: the union of the areas plus a margin. It must not be smaller —
    /// otherwise a distant area could not be scrolled to, and there is nothing to draw outside the areas.</summary>
    public (int MinX, int MinZ, int MaxX, int MaxZ) WorldBounds { get; }

    public int? WorldWidth { get; }
    public int? WorldLength { get; }

    /// <summary>Y range over ALL areas — the bounds of the layer slider, so that it does not change when
    /// switching areas (a single area's window already has a narrower range).</summary>
    public int MinWorldY { get; }
    public int MaxWorldY { get; }

    public int TotalColumns { get; }
    public int TotalRows { get; }

    public static ChunkAreas Build(VSSaveFile save)
        => Build(save.ChunkPositions, save.DetectWorldSize());

    /// <summary>Clusters chunk columns into connected groups (8 neighbours) and splits the groups
    /// whose bounding box does not fit into <see cref="TerrainMap.MaxCells"/>.</summary>
    public static ChunkAreas Build(IReadOnlyCollection<long> chunkPositions, (int Width, int Length)? worldSize)
    {
        var rowCount = new Dictionary<long, int>(Math.Max(16, chunkPositions.Count / 2));
        int minY = int.MaxValue, maxY = int.MinValue;
        foreach (var p in chunkPositions)
        {
            var (cx, cy, cz) = ChunkPosHelper.DecodeIndex((ulong)p);
            long key = PackColumn(cx, cz);
            rowCount[key] = rowCount.TryGetValue(key, out var n) ? n + 1 : 1;
            int y0 = cy * 32, y1 = y0 + 31;
            if (y0 < minY) minY = y0;
            if (y1 > maxY) maxY = y1;
        }

        if (rowCount.Count == 0)
            return new ChunkAreas(Array.Empty<ChunkArea>(), (0, 0, 0, 0), worldSize?.Width, worldSize?.Length, 0, 0, 0, 0);

        var columns = rowCount.Keys.ToArray();
        var index = new Dictionary<long, int>(columns.Length);
        for (int i = 0; i < columns.Length; i++) index[columns[i]] = i;
        var parent = new int[columns.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]]; // path compression: without it the depth grows on a "snake" of chunks
                x = parent[x];
            }

            return x;
        }

        void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b) parent[b] = a;
        }

        for (int i = 0; i < columns.Length; i++)
        {
            var (cx, cz) = UnpackColumn(columns[i]);
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                if (index.TryGetValue(PackColumn(cx + dx, cz + dz), out var j)) Union(i, j);
            }
        }

        var groups = new Dictionary<int, List<(int Cx, int Cz, int Rows)>>();
        for (int i = 0; i < columns.Length; i++)
        {
            var (cx, cz) = UnpackColumn(columns[i]);
            int root = Find(i);
            if (!groups.TryGetValue(root, out var list)) groups[root] = list = new List<(int, int, int)>();
            list.Add((cx, cz, rowCount[columns[i]]));
        }

        var windows = new List<ChunkArea>();
        foreach (var g in groups.Values)
        foreach (var w in SplitToFit(g))
            windows.Add(w);

        // The start area is the one holding the world centre (the server spawn); if the world size is unknown
        // or there are no chunks at the centre — the largest one. Without this rule the map would open on
        // a random pregen patch if it happened to be larger than the settled area.
        ChunkArea main = null!;
        if (worldSize is { } ws)
        {
            double cx = ws.Width / 2.0, cz = ws.Length / 2.0;
            main = windows.Where(w => w.ContainsBlock(cx, cz)).OrderBy(w => w.BlockCells).FirstOrDefault()!;
        }

        main ??= windows.OrderByDescending(w => w.Columns).ThenBy(w => w.BlockCells).First();
        main.IsMain = true;

        var ordered = new List<ChunkArea> { main };
        ordered.AddRange(windows.Where(w => !ReferenceEquals(w, main))
            .OrderByDescending(w => w.Columns).ThenBy(w => w.MinX).ThenBy(w => w.MinZ));
        for (int i = 0; i < ordered.Count; i++) ordered[i].Index = i;

        int minAreaX = ordered.Min(a => a.MinX), minAreaZ = ordered.Min(a => a.MinZ);
        int maxAreaX = ordered.Max(a => a.MaxX), maxAreaZ = ordered.Max(a => a.MaxZ);
        // A 10% margin (minimum 512 blocks): otherwise the viewport hits the area border right at the edge
        int margin = (int)Math.Max(512, Math.Max(maxAreaX - minAreaX, maxAreaZ - minAreaZ) * 0.1);

        return new ChunkAreas(ordered.ToArray(),
            (minAreaX - margin, minAreaZ - margin, maxAreaX + margin, maxAreaZ + margin),
            worldSize?.Width, worldSize?.Length,
            minY == int.MaxValue ? 0 : minY, maxY == int.MinValue ? 0 : maxY,
            ordered.Sum(a => a.Columns), ordered.Sum(a => a.Rows));
    }

    /// <summary>The area containing the point (in blocks); when rectangles overlap — the one nearest by centre.</summary>
    public ChunkArea? AreaAt(double wx, double wz)
        => _areas.Where(a => a.ContainsBlock(wx, wz))
            .OrderBy(a => Dist2(a.CentreX - wx, a.CentreZ - wz))
            .FirstOrDefault();

    /// <summary>The area nearest to the point (for the hint when the screen sits in the void between areas).</summary>
    public ChunkArea AreaNearest(double wx, double wz)
    {
        ChunkArea best = _areas[0];
        double bestD = double.MaxValue;
        foreach (var a in _areas)
        {
            double d = a.DistanceTo(wx, wz);
            if (d < bestD)
            {
                bestD = d;
                best = a;
            }
        }

        return best;
    }

    /// <summary>
    ///     Which area to show when the viewport looks at such a rectangle (in blocks).
    ///     We take the one nearest to the viewport centre among those intersecting it: the "centre is inside
    ///     the area" rule would flicker at the border, and "any intersecting one" would pick by order.
    ///     The current area is kept as long as it intersects the viewport itself and is no farther than the chosen one.
    /// </summary>
    public ChunkArea? AreaForViewport(double minX, double minZ, double maxX, double maxZ, ChunkArea? current)
    {
        double cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
        ChunkArea? best = null;
        double bestD = double.MaxValue;
        foreach (var a in _areas)
        {
            if (a.MaxX < minX || a.MinX > maxX || a.MaxZ < minZ || a.MinZ > maxZ) continue; // no intersection
            double d = a.DistanceTo(cx, cz);
            bool better = d < bestD - 1e-9 ||
                          (Math.Abs(d - bestD) <= 1e-9 && best is not null && a.Columns > best.Columns);
            if (better)
            {
                bestD = d;
                best = a;
            }
        }

        if (best is null) return null;
        if (current is not null && !ReferenceEquals(best, current))
        {
            // do not switch if the current area is exactly as near: otherwise at the junction
            // of two large bboxes it would switch back and forth on every pixel of panning
            double dc = current.DistanceTo(cx, cz);
            if (current.MaxX >= minX && current.MinX <= maxX && current.MaxZ >= minZ && current.MinZ <= maxZ &&
                dc <= bestD + 1e-9)
                return current;
        }

        return best;
    }

    private static double Dist2(double dx, double dz) => dx * dx + dz * dz;

    /// <summary>(cx,cz) column -> key. The format matches <see cref="TerrainMap.PackColumn"/>.</summary>
    public static long PackColumn(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

    public static (int Cx, int Cz) UnpackColumn(long key) => ((int)(key >> 32), (int)(uint)key);

    /// <summary>
    ///     Splits a group of columns so that the bounding box of every part fits into
    ///     <see cref="TerrainMap.MaxCells"/>. We cut at the widest hole in the occupied coordinates
    ///     (that separates "genuine" patches), and in a solid stretch — at the weighted median,
    ///     so that a degenerate part does not appear.
    /// </summary>
    private static List<ChunkArea> SplitToFit(List<(int Cx, int Cz, int Rows)> group)
    {
        if (group.Count == 0) return new List<ChunkArea>();
        int minCx = group.Min(e => e.Cx), maxCx = group.Max(e => e.Cx);
        int minCz = group.Min(e => e.Cz), maxCz = group.Max(e => e.Cz);
        long cells = (long)(maxCx - minCx + 1) * 32 * ((maxCz - minCz + 1) * 32);
        if (cells <= TerrainMap.MaxCells || group.Count == 1)
            return new List<ChunkArea>
            {
                new()
                {
                    MinCx = minCx, MaxCx = maxCx, MinCz = minCz, MaxCz = maxCz,
                    Columns = group.Count, Rows = group.Sum(e => e.Rows)
                }
            };

        bool alongX = maxCx - minCx >= maxCz - minCz;
        var counts = new SortedDictionary<int, int>();
        foreach (var e in group)
        {
            int c = alongX ? e.Cx : e.Cz;
            counts[c] = counts.TryGetValue(c, out var n) ? n + e.Rows : e.Rows;
        }

        var coords = counts.Keys.ToArray();
        int cut;
        int gapAt = -1, bestGap = -1;
        for (int i = 1; i < coords.Length; i++)
        {
            int g = coords[i] - coords[i - 1];
            if (g > bestGap)
            {
                bestGap = g;
                gapAt = i;
            }
        }

        if (bestGap > 1)
        {
            cut = coords[gapAt - 1]; // everything left of the hole stays in the left part
        }
        else
        {
            int half = Math.Max(1, group.Sum(e => e.Rows) / 2), acc = 0, i = 0;
            for (; i < coords.Length; i++)
            {
                acc += counts[coords[i]];
                if (acc >= half) break;
            }

            if (i >= coords.Length - 1) i = coords.Length - 2;
            cut = coords[i];
        }

        var left = group.Where(e => (alongX ? e.Cx : e.Cz) <= cut).ToList();
        var right = group.Where(e => (alongX ? e.Cx : e.Cz) > cut).ToList();
        if (left.Count == 0 || right.Count == 0) // a safeguard against a degenerate cut
            return new List<ChunkArea>
            {
                new()
                {
                    MinCx = minCx, MaxCx = maxCx, MinCz = minCz, MaxCz = maxCz,
                    Columns = group.Count, Rows = group.Sum(e => e.Rows)
                }
            };

        var result = SplitToFit(left);
        result.AddRange(SplitToFit(right));
        return result;
    }
}
