using System;
using System.Collections.Generic;
using System.Linq;

namespace VSCore;

/// <summary>One map area that contains the block selection, with how much of it was found there.</summary>
public readonly record struct AreaMatch(ChunkArea Area, int MatchChunks, long MatchCells);

/// <summary>
///     Which areas of a save contain the blocks ticked in the "Find blocks" tab.
///
///     This is what makes the area list of the scope row honest: the overlay is built from the ticked codes, so an
///     area that contains none of them cannot show anything and is not offered. The answer comes from the scan
///     histogram (<see cref="ChunkScanData.BlockCodeCounts" />), which already holds every block id of every chunk:
///     no chunk is decoded and no overlay is built, so the list is ready as soon as a code is ticked.
///
///     Counts are per CHUNK RECORD (a save row, as the "Chunks" tab counts them). A record that carries two ticked
///     codes is counted once: the whole selection is one pass, not one pass per code — otherwise the same row would
///     be counted twice and the number would exceed the save.
/// </summary>
public static class BlockSelectionAreas
{
    /// <summary>Areas that contain at least one cell of any of <paramref name="codes" />, in area order.
    /// An empty selection, or one whose codes are unknown to this save, gives an empty list.</summary>
    public static IReadOnlyList<AreaMatch> Find(SaveScanResult? scan, ChunkAreas? areas, BlockRegistry? registry,
        IReadOnlyCollection<string> codes)
    {
        var (mask, unknown) = BuildMask(registry, codes);
        if (scan is null || areas is null || mask is null || areas.Areas.Count == 0)
            return Array.Empty<AreaMatch>();

        var positions = new Dictionary<ChunkArea, int>();
        for (var i = 0; i < areas.Areas.Count; i++) positions[areas.Areas[i]] = i;
        var chunkCounts = new int[areas.Areas.Count];
        var cellCounts = new long[areas.Areas.Count];

        foreach (var cd in scan.Chunks)
        {
            var hit = cd.BlockCodeCounts.SumSelected(mask, unknown);
            if (hit <= 0) continue;
            // the centre of the column, so a chunk is attributed to the area that really holds it
            var area = areas.AreaAt(cd.WorldX + 16.0, cd.WorldZ + 16.0);
            if (area is null || !positions.TryGetValue(area, out var at)) continue;
            chunkCounts[at]++;
            cellCounts[at] += hit;
        }

        var result = new List<AreaMatch>();
        for (var i = 0; i < chunkCounts.Length; i++)
            if (chunkCounts[i] > 0)
                result.Add(new AreaMatch(areas.Areas[i], chunkCounts[i], cellCounts[i]));
        return result;
    }

    /// <summary>
    ///     The chunk COLUMNS that carry at least one cell of any of <paramref name="codes" />, in save order.
    ///     <paramref name="area" /> keeps only the columns inside that area's rectangle.
    ///
    ///     This is what a click on an area of the scope row turns into a chunk selection: the chunks of the area where
    ///     the ticked block actually is, not every chunk of the area. The answer comes from the same scan histogram as
    ///     <see cref="Find" /> — no chunk is decoded, and one pass over the records answers the whole save.
    /// </summary>
    public static IReadOnlyList<(int Cx, int Cz)> ChunksWithCodes(SaveScanResult? scan, BlockRegistry? registry,
        IReadOnlyCollection<string> codes, ChunkArea? area = null)
    {
        var (mask, unknown) = BuildMask(registry, codes);
        if (scan is null || mask is null) return Array.Empty<(int, int)>();
        var list = new List<(int Cx, int Cz)>();
        var seen = new HashSet<(int Cx, int Cz)>();
        foreach (var cd in scan.Chunks)
        {
            if (cd.BlockCodeCounts.SumSelected(mask, unknown) <= 0) continue;
            if (area is not null &&
                (cd.ChunkX < area.MinCx || cd.ChunkX > area.MaxCx || cd.ChunkZ < area.MinCz || cd.ChunkZ > area.MaxCz))
                continue;
            // one column may be several Y records: the selection is a set of columns, so it is listed once
            if (seen.Add((cd.ChunkX, cd.ChunkZ))) list.Add((cd.ChunkX, cd.ChunkZ));
        }

        return list;
    }

    /// <summary>Chunk records of the whole save that carry any of the codes, and their cells. Kept next to
    /// <see cref="Find" /> so a caller can state the same number without an area split.</summary>
    public static (int Chunks, long Cells) CountAll(SaveScanResult? scan, BlockRegistry? registry,
        IReadOnlyCollection<string> codes)
    {
        var (mask, unknown) = BuildMask(registry, codes);
        if (scan is null || mask is null) return (0, 0);
        var chunks = 0;
        long cells = 0;
        foreach (var cd in scan.Chunks)
        {
            var hit = cd.BlockCodeCounts.SumSelected(mask, unknown);
            if (hit <= 0) continue;
            chunks++;
            cells += hit;
        }

        return (chunks, cells);
    }

    /// <summary>
    ///     Turns the ticked codes into an id mask (what a packed histogram needs) plus the codes the registry does
    ///     not know (what a hand-built histogram needs). The "id:NNN" form is the tab's own fallback for a code that
    ///     is not in BlockIDs, so it has to work here as well.
    /// </summary>
    private static (bool[]? Mask, HashSet<string>? Codes) BuildMask(BlockRegistry? registry, IReadOnlyCollection<string> codes)
    {
        if (codes.Count == 0) return (null, null);
        var mask = new bool[ushort.MaxValue + 1];
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        var anyId = false;
        foreach (var code in codes)
        {
            if (registry is not null)
            {
                var ids = registry.IdsOf(code);
                if (ids.Count > 0)
                {
                    foreach (var id in ids)
                        if (id is > 0 and <= ushort.MaxValue)
                        {
                            mask[id] = true;
                            anyId = true;
                        }

                    continue;
                }
            }

            if (code.Length > 3 && code.StartsWith("id:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(code.AsSpan(3), out var raw) && raw is > 0 and <= ushort.MaxValue)
            {
                mask[raw] = true;
                anyId = true;
                continue;
            }

            unknown.Add(code);
        }

        return anyId || unknown.Count > 0 ? (mask, unknown) : (null, null);
    }
}
