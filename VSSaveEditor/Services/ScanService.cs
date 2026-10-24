using VSCore;

namespace VSSaveEditor;

/// <summary>
///     State of the background "all data" scan of a save: the model, the (cx,cz) → y columns index and
///     the scan epoch. The epoch guards against races: a stale scan result is not published
///     if another save was opened or a new scan was started in the meantime. The instance is owned by
///     the UI thread; background code only computes the result, and the caller publishes it on the UI thread.
/// </summary>
public sealed class ScanService
{
    private int _epoch;

    public SaveScanResult? Data { get; private set; }
    public Dictionary<(int X, int Z), List<ChunkScanData>>? ByColumn { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>Marks the start of a scan: everything older (both results and running scans) becomes stale.</summary>
    public int Begin()
    {
        var epoch = ++_epoch;
        Data = null;
        ByColumn = null;
        IsRunning = true;
        return epoch;
    }

    public bool IsCurrent(int epoch)
    {
        return epoch == _epoch;
    }

    /// <summary>Publishes the result when its epoch is current; otherwise false and the UI is left alone.</summary>
    public bool TryPublish(int epoch, SaveScanResult data)
    {
        if (!IsCurrent(epoch)) return false;
        Data = data;
        ByColumn = BuildIndex(data);
        return true;
    }

    public void Finish(int epoch)
    {
        if (IsCurrent(epoch)) IsRunning = false;
    }

    /// <summary>A full reset: the previous scan is superseded (another save was opened).</summary>
    public void Reset()
    {
        ++_epoch;
        Data = null;
        ByColumn = null;
        IsRunning = false;
    }

    /// <summary>y columns of chunk (cx,cz) from the index; the fallback is a linear search until the index is built.</summary>
    public IReadOnlyList<ChunkScanData> ColumnsFor(int cx, int cz)
    {
        if (ByColumn is not null && ByColumn.TryGetValue((cx, cz), out var cols)) return cols;
        if (Data is null) return Array.Empty<ChunkScanData>();
        return Data.Chunks.Where(c => c.ChunkX == cx && c.ChunkZ == cz).OrderBy(c => c.ChunkY).ToList();
    }

    /// <summary>The (cx,cz) → y columns index: built once per scan instead of a Where over all chunks for each lookup.</summary>
    public static Dictionary<(int X, int Z), List<ChunkScanData>> BuildIndex(SaveScanResult data)
    {
        return data.Chunks
            .GroupBy(c => (c.ChunkX, c.ChunkZ))
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.ChunkY).ToList());
    }
}

/// <summary>
///     Builds the text dump of a scan. Pure functions over the model: on a big save this is tens of
///     thousands of lines, so they run in the background rather than on the UI thread.
/// </summary>
public static class ScanDumpBuilder
{
    /// <summary>Dump of the whole save (without a chunk selection) - the "Copy raw dump" export.</summary>
    public static List<object> Build(SaveScanResult r)
    {
        var items = new List<object>
        {
            $"META version={r.Meta.Version ?? "?"} seed={r.Meta.Seed?.ToString() ?? "?"} | tables={string.Join(", ", r.Meta.TableNames)}",
            $"regions={r.Meta.RegionCount} chunks={r.Meta.ChunkCount} mapchunks={r.Meta.MapchunkCount} players={r.Meta.PlayerCount}",
            $"blocks solid={r.TotalSolid:N0} fluid={r.TotalFluid:N0} air={r.TotalAir:N0} | entities={r.EntityCount} blockEntities={r.BlockEntityCount}",
            "-- Block histogram (world, top 40) --"
        };
        foreach (var kv in r.WorldBlockCodeCounts.OrderByDescending(x => x.Value).Take(40))
            items.Add($"   {kv.Key}: {kv.Value:N0}");

        items.Add("-- Players --");
        foreach (var p in r.Players)
            items.Add($"   id={p.PlayerId} uid={p.PlayerUid} codes=[{string.Join(",", p.SpeciesCodes)}]");

        var allEnt = r.Chunks.SelectMany(c => c.Entities).ToList();
        var ents = allEnt.Where(e => e.HasInfo).ToList(); // filter out "inactive" nodes (no kind/attributes)
        items.Add($"-- Entities (active {ents.Count}/{allEnt.Count}) --");
        foreach (var e in ents.Take(2000)) items.Add(e);
        if (ents.Count > 2000) items.Add($"   ... and {ents.Count - 2000} more (kept in model)");

        var bes = r.Chunks.SelectMany(c => c.BlockEntities).ToList();
        items.Add($"-- Block entities ({bes.Count}) --");

        items.Add("-- Map regions (layers) --");
        foreach (var rg in r.Regions)
        {
            items.Add($"   region {rg.Position} len={rg.DataLength}");
            foreach (var l in rg.Layers.Take(20)) items.Add($"      {l}");
        }

        items.Add($"-- Mapchunks (client map, {r.Mapchunks.Count}) --");
        items.Add($"   raw retained: {r.Mapchunks.Count(m => m.Raw != null)}");
        foreach (var m in r.Mapchunks.Take(50))
            items.Add($"   {m.Position} len={m.DataLength} fields=[{string.Join("; ", m.Fields.Take(6))}]");
        if (r.Mapchunks.Count > 50) items.Add($"   ... and {r.Mapchunks.Count - 50} more");

        items.Add("-- Raw blobs --");
        items.Add($"   chunk raws retained: {r.Chunks.Count(c => c.Raw != null)} (in memory)");
        if (r.GamedataRaw is { } g) items.Add($"   gamedata raw len={g.Length} preview=[{g.Preview}]");
        foreach (var p in r.Players)
            if (p.Raw is { } pr)
                items.Add($"   playerdata{pr.Key1} raw len={pr.Length} preview=[{pr.Preview}]");

        return items;
    }

    /// <summary>Data of the selected chunk region (creatures, BlockEntity, blocks) instead of the global dump.</summary>
    public static List<object> BuildForSelection(
        SaveScanResult r,
        Dictionary<(int X, int Z), List<ChunkScanData>>? byColumn,
        (int minCx, int minCz, int maxCx, int maxCz) bounds)
    {
        var (miX, miZ, maX, maZ) = bounds;
        var items = new List<object>();
        var emitted = 0;
        for (var cx = miX; cx <= maX && emitted < 4000; cx++)
        for (var cz = miZ; cz <= maZ && emitted < 4000; cz++)
        {
            var cols = byColumn is not null && byColumn.TryGetValue((cx, cz), out var byCol)
                ? byCol
                : r.Chunks.Where(c => c.ChunkX == cx && c.ChunkZ == cz).OrderBy(c => c.ChunkY).ToList();
            if (cols.Count == 0) continue;
            items.Add($"== Chunk {cx}:{cz} ({cols.Count} y-col) ==");
            foreach (var cd in cols)
            {
                items.Add(
                    $"  [y{cd.ChunkY}] world X{cd.WorldX} Y{cd.WorldY} Z{cd.WorldZ} | s={cd.SolidCells} f={cd.FluidCells} a={cd.AirCells} | raw={cd.Raw?.Length ?? 0}B");
                var act = cd.Entities.Where(e => e.HasInfo).ToList();
                items.Add($"  entities (active)={act.Count}/{cd.Entities.Count}");
                foreach (var e in act) items.Add(e);
                items.Add(
                    $"  top blocks: {string.Join(", ", cd.BlockCodeCounts.OrderByDescending(x => x.Value).Take(8).Select(k => $"{k.Key}:{k.Value}"))}");
            }

            emitted += cols.Count;
        }

        if (emitted == 0) items.Add("(no data for the selected chunk)");
        return items;
    }
}