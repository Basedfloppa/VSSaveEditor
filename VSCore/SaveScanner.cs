using System.Diagnostics;
using System.Text;
using VSCore.Format;

namespace VSCore;

/// <summary>Per-section timings of one <see cref="SaveScanner.Scan"/> run. Filled only when the caller
/// passes an instance: the chunk loop then takes three timestamp reads per chunk, which is measurable but
/// small (~0.5 % of a 12-GB scan). A normal scan passes null and pays nothing.</summary>
public sealed class ScanTimings
{
    public long MetaTicks, RegionsTicks, ChunkReadTicks, EntityTicks, HistogramTicks, BlockCodeTicks,
        AggregationTicks, MapchunkReadTicks, MapchunkParseTicks, PlayerTicks, TotalTicks;

    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public double MetaMs => Ms(MetaTicks);
    public double RegionsMs => Ms(RegionsTicks);
    public double ChunkReadMs => Ms(ChunkReadTicks);
    public double EntityMs => Ms(EntityTicks);
    public double HistogramMs => Ms(HistogramTicks);
    public double BlockCodeMs => Ms(BlockCodeTicks);
    public double AggregationMs => Ms(AggregationTicks);
    public double MapchunkReadMs => Ms(MapchunkReadTicks);
    public double MapchunkParseMs => Ms(MapchunkParseTicks);
    public double MapchunkMs => Ms(MapchunkReadTicks) + Ms(MapchunkParseTicks);
    public double PlayerMs => Ms(PlayerTicks);
    public double TotalMs => Ms(TotalTicks);
}

// ============================= DATA MODEL =============================

/// <summary>Result of a full save scan: structured data of all tables (+ the raw blobs when requested).</summary>
public sealed class SaveScanResult
{
    public MetaScanData Meta { get; } = new();
    public List<ChunkScanData> Chunks { get; } = new();
    public List<RegionScanData> Regions { get; } = new();
    public List<MapchunkScanData> Mapchunks { get; } = new();
    public List<PlayerScanData> Players { get; } = new();

    /// <summary>Flat index of all inventories of the save (players, entities, block entities) for the Items tab.</summary>
    public List<InventoryInfo> Inventories { get; } = new();
    public int ItemStackCount { get; set; }
    public RawBlob? GamedataRaw { get; set; }

    /// <summary>World-wide counters aggregated by block code (liquids included).</summary>
    public Dictionary<string, long> WorldBlockCodeCounts { get; } = new();

    internal void ReplaceWorldBlockCodeCounts(Dictionary<string, long> counts)
    {
        WorldBlockCodeCounts.Clear();
        foreach (var kv in counts) WorldBlockCodeCounts[kv.Key] = kv.Value;
    }
    public long TotalSolid, TotalFluid, TotalAir;

    /// <summary>False while the block histogram has not been decoded yet (see <see cref="SaveScanner.ComputeBlockStats" />):
    /// the per-chunk solid/fluid/air counts and the code counts read as zero until then.</summary>
    public bool BlockStatsComputed { get; set; }
    public int EntityCount { get; set; }
    public int BlockEntityCount { get; set; }

    /// <summary>
    ///     A copy of the result holding only the chunks inside the scope — with their entities, block entities and
    ///     inventories — and every counter aggregated over that part.
    ///
    ///     It is a real copy rather than a filtered view because consumers read <see cref="WorldBlockCodeCounts" /> as
    ///     "the whole model": the block list behind the "Find blocks" overlay would otherwise offer codes that occur
    ///     only outside the scope. The players' inventories are kept on purpose — playerdata has no position, so there
    ///     is no chunk to narrow it by (see <see cref="SearchScope" />). Player rows, regions, mapchunks and gamedata
    ///     are not copied: they belong to the save, not to a chunk scope.
    /// </summary>
    public SaveScanResult WithinScope(SearchScope? scope)
    {
        scope ??= SearchScope.Everything;
        var res = new SaveScanResult();
        foreach (var cd in Chunks)
        {
            if (!scope.ContainsChunk(cd.ChunkX, cd.ChunkZ)) continue;
            res.Chunks.Add(cd);
            res.TotalSolid += cd.SolidCells;
            res.TotalFluid += cd.FluidCells;
            res.TotalAir += cd.AirCells;
            res.EntityCount += cd.Entities.Count;
            res.BlockEntityCount += cd.BlockEntities.Count;
            foreach (var e in cd.Entities) res.Inventories.AddRange(e.Inventories);
            foreach (var be in cd.BlockEntities) res.Inventories.AddRange(be.Inventories);
            foreach (var (code, cnt) in cd.BlockCodeCounts)
            {
                res.WorldBlockCodeCounts.TryGetValue(code, out var v);
                res.WorldBlockCodeCounts[code] = v + cnt;
            }
        }

        if (!scope.HasRegion) // a chunk scope contains no playerdata at all
            res.Inventories.AddRange(Inventories.Where(i => i.OwnerKind == InventoryOwnerKind.Player));
        res.ItemStackCount = res.Inventories.Sum(i => i.Items.Count);
        return res;
    }
}

/// <summary>Save metadata (gamedata/version/seed) and the overall counters.</summary>
public sealed class MetaScanData
{
    public string? Version { get; set; }
    public long? Seed { get; set; }
    public IReadOnlyList<string> TableNames { get; set; } = Array.Empty<string>();
    public long GamedataLength { get; set; }
    public int RegionCount, ChunkCount, MapchunkCount, PlayerCount;
}

/// <summary>One chunk with all extracted data.</summary>
public sealed class ChunkScanData
{
    public long Position { get; set; }
    public int ChunkX, ChunkY, ChunkZ;      // chunk coordinates (from position)

    // Derived, not stored: three ints per chunk row cost 26 MiB on the 6.3-GB save, and every consumer
    // needs only the corner of the chunk, which is ch*32 by definition.
    public int WorldX => ChunkX * 32;
    public int WorldY => ChunkY * 32;
    public int WorldZ => ChunkZ * 32;
    /// <summary>Per-chunk block histogram. Packed words in a scan, a real dictionary for hand-built models.</summary>
    public BlockCodeCounts BlockCodeCounts { get; } = new();
    public long SolidCells, FluidCells, AirCells;
    // Most chunk rows hold neither an entity nor a block entity: on the 6.3-GB server save 2.19M rows had
    // 41k entities and 1.87M block entities. An eager `List` per row costs 32 bytes even when empty, and the
    // empty lists alone were ~140 MiB. The backing lists are therefore created on the first record.
    private List<EntityInfo>? _entities;
    private List<BlockEntityInfo>? _blockEntities;

    public IReadOnlyList<EntityInfo> Entities => _entities ?? (IReadOnlyList<EntityInfo>)Array.Empty<EntityInfo>();
    public IReadOnlyList<BlockEntityInfo> BlockEntities =>
        _blockEntities ?? (IReadOnlyList<BlockEntityInfo>)Array.Empty<BlockEntityInfo>();

    internal void AddEntity(EntityInfo entity) => (_entities ??= new List<EntityInfo>()).Add(entity);
    internal void AddBlockEntity(BlockEntityInfo blockEntity) =>
        (_blockEntities ??= new List<BlockEntityInfo>()).Add(blockEntity);

    public RawBlob? Raw { get; set; }

    /// <summary>Length of the chunk blob in the table. Kept even when <see cref="Raw"/> is not captured
    /// (the app scans with captureRaw:false), so size-based filters and the dump stay meaningful.</summary>
    public int DataLength { get; set; }
}

/// <summary>Entity/player (entity) from field 6 of the chunk blob.</summary>
public sealed class EntityInfo
{
    public string? Species { get; set; }          // "equusdestrier:horse-adult-female-destrier" / "drifter-deep"
    public string? Domain { get; set; }           // "equusdestrier" / "game"; null for vanilla codes without a domain
    public string? Kind { get; set; }             // "horse", "wolf" — the first part of the code PATH, without the domain
    public string? SexAge { get; set; }           // "adult-male" (sex-age)

    /// <summary>Name given by the player (nametag): "Moonlight". Empty for most entities.
    /// This is the only human-readable name in the blob — searching for a horse by it has to work.</summary>
    public string? CustomName { get; set; }

    /// <summary>The code path without the domain: `equusdestrier:horse-adult-female-destrier` -> `horse-adult-female-destrier`.</summary>
    public string CodePath => PathOf(Species);

    /// <summary>The code path without the domain (the same operation as <see cref="CodePath" />, but for an arbitrary string).</summary>
    public static string PathOf(string? code)
    {
        if (code is null) return "";
        var i = code.IndexOf(':');
        return i < 0 ? code : code[(i + 1)..];
    }

    /// <summary>Code domain (`domain:path` -> `domain`), null if the string has no domain.</summary>
    public static string? DomainOf(string? code)
    {
        if (code is null) return null;
        var i = code.IndexOf(':');
        return i <= 0 ? null : code[..i];
    }

    public List<string> SpeciesCodes { get; set; } = new();
    public Dictionary<string, string> Attributes { get; } = new();
    public List<string> Tokens { get; set; } = new();
    public int ChunkX, ChunkY, ChunkZ;
    public int DataLength { get; set; }

    /// <summary>The record key in the `chunk` table and the ordinal of field 6 in its blob. The pair addresses
    /// the entity for an edit (EditSession) — without it the dump card does not know what exactly to change.</summary>
    public long Position { get; set; } = -1;
    public int Occurrence { get; set; } = -1;

    /// <summary>An "active"/meaningful node: it has a kind (`game:...`) or at least one recognised attribute.
    /// Empty nodes (neither a kind nor attributes) are treated as inactive junk and filtered out of the output.</summary>
    public bool HasInfo => Species is not null || Attributes.Count > 0;

    /// <summary>Attributes on one line for display on the card.</summary>
    public string AttributeSummary => string.Join(", ", Attributes.Keys);

    /// <summary>A user note on the entity (an editable field in the UI, never written to the save).</summary>
    public string? Note { get; set; }

    /// <summary>Inventories found in the ECX tree (`backpack`, `wearablesInv`, …).</summary>
    public List<InventoryInfo> Inventories { get; set; } = new();

    /// <summary>All fields from the ECX blob: "name = value" (best-effort, from the length-prefixed frame).</summary>
    public List<string> Fields { get; set; } = new();
    public string FieldsSummary => string.Join("\n", Fields);
}

/// <summary>Block entity from field 8 of the chunk blob (spawners included).</summary>
public sealed class BlockEntityInfo
{
    public string? Type { get; set; }            // "Spawner", "Chest", ...
    public int DataLength { get; set; }

    // Tokens were a list of up to 60 identifier words per block entity — 31M references / 281 MiB on the
    // 6.3-GB save. Only `Type` (the first token) and the `game:` codes are used by the list/search/cards;
    // the full record is parsed from the working copy when a card is opened, so the vocabulary is not pinned.
    private List<string>? _speciesCodes;
    private List<InventoryInfo>? _inventories;

    public IReadOnlyList<string> SpeciesCodes =>
        _speciesCodes ?? (IReadOnlyList<string>)Array.Empty<string>();

    internal void SetSpeciesCodes(List<string>? codes) => _speciesCodes = codes is { Count: > 0 } ? codes : null;

    public int ChunkX, ChunkY, ChunkZ;

    /// <summary>The record key in the `chunk` table and the ordinal of field 8 in its blob. The pair addresses
    /// the block entity for an edit (EditSession) — just like <see cref="EntityInfo.Position" /> for entities:
    /// without it the "Entities" list could not edit a chest/spawner it found.</summary>
    public long Position { get; set; } = -1;
    public int Occurrence { get; set; } = -1;

    public string CodeSummary => string.Join(", ", SpeciesCodes);

    /// <summary>A user note on the block entity (an editable field in the UI).</summary>
    public string? Note { get; set; }

    /// <summary>The container inventories (`inventory`) found in the ECX tree.</summary>
    public IReadOnlyList<InventoryInfo> Inventories =>
        _inventories ?? (IReadOnlyList<InventoryInfo>)Array.Empty<InventoryInfo>();

    internal void SetInventories(List<InventoryInfo>? inventories) =>
        _inventories = inventories is { Count: > 0 } ? inventories : null;
}

/// <summary>Region (mapregion): worldgen/geological layers.</summary>
/// <remarks>
///     The layers are parsed lazily: a big save carries ~2000 region blobs (677 MB on the 12-GB one), and the
///     scan used to read and parse every one of them just to put a few lines into the text dump. The record
///     keeps the position and the length from the table, and the blob is read the first time
///     <see cref="Layers" /> is asked for.
/// </remarks>
public sealed class RegionScanData
{
    public long Position { get; set; }
    public int DataLength { get; set; }

    /// <summary>Reads and parses the blob on first use; assigned by the scan, null for a bare record.</summary>
    public Func<List<string>>? LayerLoader { get; set; }

    private List<string>? _layers;

    public List<string> Layers => _layers ??= LoadLayers(LayerLoader, Position);

    /// <summary>True when the blob has already been read (the dump shows layers only for those).</summary>
    public bool LayersLoaded => _layers is not null;

    public RawBlob? Raw { get; set; }

    private static List<string> LoadLayers(Func<List<string>>? loader, long position)
    {
        if (loader is null) return new List<string>();
        try { return loader(); }
        catch (Exception ex)
        {
            // A closed save or a torn blob must not take the whole dump down: report it, show no layers.
            ScanDiagnostics.Warn($"mapregion at {position} could not be parsed", ex);
            return new List<string>();
        }
    }
}

/// <summary>Client map chunk (mapchunk).</summary>
public sealed class MapchunkScanData
{
    public long Position { get; set; }
    public int DataLength { get; set; }
    public List<string> Fields { get; } = new();
    public RawBlob? Raw { get; set; }
}

/// <summary>Character (playerdata).</summary>
public sealed class PlayerScanData
{
    public long PlayerId { get; set; }
    public string? PlayerUid { get; set; }
    public List<string> Tokens { get; set; } = new();
    public List<string> SpeciesCodes { get; set; } = new();
    public RawBlob? Raw { get; set; }

    /// <summary>Player name from the `EntityPlayer` embedded in playerdata (`nametag`), if present.</summary>
    public string? CustomName { get; set; }

    /// <summary>The character's inventories and the backpacks nested in them.</summary>
    public List<InventoryInfo> Inventories { get; set; } = new();
}

/// <summary>Raw blob bytes for future/unparsed formats.</summary>
public sealed class RawBlob
{
    public string Table { get; set; } = "";
    public long Key1 { get; set; }
    public string? Key2 { get; set; }
    public int Length { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string Preview { get; set; } = "";   // a short ascii/hex preview for the UI
}

// ============================= SCANNER =============================

/// <summary>
/// Walks all tables of the save and collects ALL the data: blocks/liquids per chunk,
/// entities/players (field6), block entities (field8), worldgen/geology (mapregion),
/// seed/version (gamedata), characters (playerdata). The raw bytes of every blob are opt-in
/// (<c>captureRaw</c>): the editor reads a blob from the working copy on demand, and keeping a copy
/// of every chunk blob costs hundreds of MB on a large save for a feature that is used on click.
/// </summary>
public static class SaveScanner
{
    private static readonly string[] EntityAttrKeys =
    {
        "maxhealth", "currenthealth", "health", "wasFedToAdulthood", "animalWeight",
        "birthTotalDays", "totalDaysLastBirth", "generation", "doesEat", "multiply",
        "oxygen", "entityDead", "deathReason", "emotionstates", "totalHoursDead"
    };

    /// <param name="computeBlockStats">
    ///     Decode the block histogram (solids/fluids/air plus the per-chunk code counts). It is 98 % of the
    ///     chunk loop on a 12-GB save, and nothing needs it before the model is published, so the UI passes
    ///     false and runs <see cref="ComputeBlockStats" /> afterwards while the tabs are already usable.
    /// </param>
    public static SaveScanResult Scan(VSSaveFile save, IProgress<long>? progress = null, bool captureRaw = false,
        ScanTimings? timings = null, bool computeBlockStats = true)
    {
        var res = new SaveScanResult();
        // timings is null on every normal scan: the timestamp reads below are guarded so a production
        // run measures nothing and pays nothing.
        var timed = timings is not null;
        long scanStart = timed ? Stopwatch.GetTimestamp() : 0;
        // One vocabulary per scan: the model keeps the strings, the pool itself dies with the result.
        var pool = new StringPool();
        var reg = save.BlockRegistry;
        var itemReg = save.ItemRegistry;

        // ---- Meta / gamedata ----
        long tMeta = timed ? Stopwatch.GetTimestamp() : 0;
        res.Meta.Version = save.DetectVersion();
        res.Meta.Seed = save.DetectSeed();
        res.Meta.TableNames = save.TableNames;
        res.Meta.GamedataLength = save.GamedataLength.GetValueOrDefault();
        res.Meta.RegionCount = save.MapRegions.Count;
        // The chunk count is filled from the pages below: SELECT count(*) walks the whole table b-tree
        // (12 GB, 43 s on a cold file) and the scan reads every row anyway.
        var gd = save.ReadGamedata();
        if (captureRaw && gd is { Length: > 0 })
            res.GamedataRaw = MakeRaw("gamedata", 1, null, gd);
        if (timed) timings!.MetaTicks = Stopwatch.GetTimestamp() - tMeta;

        // ---- Mapregions (worldgen/geology) ----
        // Positions and sizes come free from the table rows. The blobs (677 MB, ~2000 rows on a 12-GB save)
        // are NOT read here: the layers are decoration of the text dump, and paying 3-9 s of I/O at every
        // open for them was the wrong trade. They are parsed on first access (RegionScanData.Layers).
        long tReg = timed ? Stopwatch.GetTimestamp() : 0;
        foreach (var mr in save.MapRegions)
        {
            var rd = new RegionScanData { Position = mr.Position, DataLength = mr.DataLength };
            var position = mr.Position;
            rd.LayerLoader = () => ParseRegionLayers(save, position, captureRaw, rd);
            res.Regions.Add(rd);
        }
        if (timed) timings!.RegionsTicks = Stopwatch.GetTimestamp() - tReg;

        // ---- Chunks (blocks/liquids/entities/block entities) ----
        // Pages of blobs, never the full set at once: retaining every chunk blob (518 MiB on the large
        // save) made the scan peak depend on the save size instead of on the model it actually builds.
        // One pass, not two. The positions used to be read as a separate full-table scan, and the paged
        // reader below then walked the same b-tree again - on a cold 12-GB save that is a second 42-second
        // pass over the file. The page reader already yields every position in order, so the array is
        // filled from the pages and the extra walk disappears.
        var chunks = new List<ChunkScanData>();
        int done = 0;
        long last = long.MinValue;
        const int PageSize = 4096;
        // 8 was a guess when the cap was added; measured on a 24-core/8P+16E machine the histogram
        // keeps scaling to ~12 (69.7 s -> 61.0 s) and gains almost nothing beyond it.
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 12) };
        var timingLock = new object();
        while (true)
        {
            long tRead = timed ? Stopwatch.GetTimestamp() : 0;
            var page = save.ReadChunkRowsPage(last, PageSize);
            if (timed) timings!.ChunkReadTicks += Stopwatch.GetTimestamp() - tRead; // main thread only
            if (page.Count == 0) break;
            var pageResults = new ChunkScanData[page.Count];
            Parallel.For(0, page.Count, po,
                () => (Ent: 0L, Hist: 0L, Codes: 0L),
                (idx, _, tl) =>
                {
                    var row = page[idx];
                    var cd = new ChunkScanData();
                    long a = timed ? Stopwatch.GetTimestamp() : 0;
                    InitChunk(cd, row.Position, row.Data);
                    ParseChunkEntities(cd, row.Data, reg, itemReg, captureRaw, pool);
                    long b = timed ? Stopwatch.GetTimestamp() : 0;
                    if (computeBlockStats)
                    {
                        MeasureChunkHistogram(cd, row.Data);
                        BuildChunkBlockCodes(cd, reg);
                    }
                    long c = timed ? Stopwatch.GetTimestamp() : 0;
                    if (timed) tl.Ent += b - a;
                    if (timed) tl.Hist += c - b;
                    if (timed) tl.Codes += Stopwatch.GetTimestamp() - c;
                    pageResults[idx] = cd;
                    int d = Interlocked.Increment(ref done);
                    if (progress != null && (d & 1023) == 0) progress.Report(d);
                    return tl;
                },
                tl =>
                {
                    if (!timed) return;
                    lock (timingLock)
                    {
                        timings!.EntityTicks += tl.Ent;
                        timings.HistogramTicks += tl.Hist;
                        timings.BlockCodeTicks += tl.Codes;
                    }
                });
            chunks.AddRange(pageResults);
            last = page[^1].Position;
        }
        progress?.Report(chunks.Count);

        // ---- Aggregation ----
        long tAgg = timed ? Stopwatch.GetTimestamp() : 0;
        foreach (var cd in chunks)
        {
            res.Chunks.Add(cd);
            res.TotalSolid += cd.SolidCells;
            res.TotalFluid += cd.FluidCells;
            res.TotalAir += cd.AirCells;
            res.EntityCount += cd.Entities.Count;
            res.BlockEntityCount += cd.BlockEntities.Count;
            foreach (var e in cd.Entities) res.Inventories.AddRange(e.Inventories);
            foreach (var be in cd.BlockEntities) res.Inventories.AddRange(be.Inventories);
            foreach (var (code, cnt) in cd.BlockCodeCounts)
            {
                res.WorldBlockCodeCounts.TryGetValue(code, out var v);
                res.WorldBlockCodeCounts[code] = v + cnt;
            }
        }
        res.Meta.ChunkCount = res.Chunks.Count;
        res.BlockStatsComputed = computeBlockStats;
        if (timed) timings!.AggregationTicks = Stopwatch.GetTimestamp() - tAgg;

        // ---- Mapchunks (client map) ----
        // Only the first rows are ever displayed (the data dump prints 50), so a normal scan reads a sample
        // instead of all 254 215 blobs. captureRaw still means "retain everything".
        const int MapchunkSample = 64;
        long tMapRead = timed ? Stopwatch.GetTimestamp() : 0;
        var mapRows = captureRaw ? save.MapchunkRowsWithData : save.MapchunkRowsWithDataLimit(MapchunkSample);
        if (timed) timings!.MapchunkReadTicks = Stopwatch.GetTimestamp() - tMapRead;
        long tMap = timed ? Stopwatch.GetTimestamp() : 0;
        foreach (var mr in mapRows)
        {
            var md = new MapchunkScanData { Position = mr.Position, DataLength = mr.Data.Length };
            var b = mr.Data ?? Array.Empty<byte>();
            if (captureRaw && b.Length > 0) md.Raw = MakeRaw("mapchunk", mr.Position, null, b);
            foreach (var f in PbReader.ReadMessage(b, maxDepth: 1, maxFields: 2000))
            {
                if (f.WireType != 2 || f.Bytes is null) continue;
                var marker = FirstAscii(f.Bytes, 14);
                md.Fields.Add($"f{f.Field} len={f.Length}" + (marker.Length > 0 ? $" marker='{marker}'" : ""));
            }
            res.Mapchunks.Add(md);
        }
        res.Meta.MapchunkCount = captureRaw ? res.Mapchunks.Count : save.MapchunkRowCount;
        if (timed) timings!.MapchunkParseTicks = Stopwatch.GetTimestamp() - tMap;

        // ---- Players (playerdata) ----
        long tPlr = timed ? Stopwatch.GetTimestamp() : 0;
        foreach (var p in save.PlayerRows)
        {
            var pd = new PlayerScanData { PlayerId = p.Id, PlayerUid = p.Uid };
            var b = p.Data ?? Array.Empty<byte>();
            pd.Tokens = ExtractTokens(b, 80, pool);
            pd.SpeciesCodes = ExtractGameCodes(b, pool);
            if (captureRaw && b.Length > 0) pd.Raw = MakeRaw("playerdata", p.Id, p.Uid, b);

            if (b.Length > 0)
            {
                try
                {
                    var data = PlayerDataParser.Parse(b, p.Id, reg, itemReg);
                    pd.CustomName = data.CustomName;
                    pd.Inventories = PlayerInventories(data, pd);
                    res.Inventories.AddRange(pd.Inventories);
                }
                catch (Exception ex)
                {
                    // a damaged playerdata record must not cancel the scan of the other tables, but the
                    // reason has to reach the console: otherwise the player simply shows no inventory
                    ScanDiagnostics.Warn($"playerdata id={p.Id} could not be parsed", ex);
                }
            }

            res.Players.Add(pd);
        }
        res.Meta.PlayerCount = res.Players.Count;
        res.ItemStackCount = res.Inventories.Sum(i => i.Items.Count);
        if (timed) timings!.PlayerTicks = Stopwatch.GetTimestamp() - tPlr;
        if (timed) timings!.TotalTicks = Stopwatch.GetTimestamp() - scanStart;

        return res;
    }

    private static List<InventoryInfo> PlayerInventories(PlayerData data, PlayerScanData p)
    {
        var result = new List<InventoryInfo>();
        foreach (var inv in data.Inventories)
        {
            result.Add(MapPlayerInventory(inv.Name ?? "inventory", inv.SlotCount, inv.Items, p));
            foreach (var nested in inv.Nested)
                result.Add(MapPlayerInventory(nested.Name, nested.SlotCount, nested.Items, p));
        }

        return result;
    }

    private static InventoryInfo MapPlayerInventory(string name, int slotCount, List<ItemStackInfo> items,
        PlayerScanData p)
    {
        var inv = new InventoryInfo
        {
            OwnerKind = InventoryOwnerKind.Player,
            OwnerLabel = p.CustomName is { Length: > 0 } cn ? cn : p.PlayerUid ?? ("player #" + p.PlayerId),
            OwnerCode = p.PlayerUid ?? "",
            PlayerId = p.PlayerId,
            PlayerUid = p.PlayerUid,
            Position = -1,
            Field = -2,
            Occurrence = -1,
            HasChunk = false,
            Name = name,
            SlotCount = slotCount
        };
        inv.Items.AddRange(items);
        return inv;
    }

    /// <summary>Cheap head of the per-chunk work: position and size, before anything is parsed.
    /// Split out so the scan can time the three phases of a chunk separately (see <see cref="ScanTimings"/>).</summary>
    private static void InitChunk(ChunkScanData cd, long position, byte[] blob)
    {
        cd.Position = position;
        cd.DataLength = blob.Length;
        var (cx, cy, cz) = ChunkPosHelper.DecodeIndex((ulong)position);
        cd.ChunkX = cx; cd.ChunkY = cy; cd.ChunkZ = cz;
    }

    /// <summary>Walks the chunk payload and materialises only entities (field 6) and block entities (field 8).
    /// This is the phase that grows with how much the world contains: inventories, species, custom names.</summary>
    private static void ParseChunkEntities(ChunkScanData cd, byte[] blob, BlockRegistry reg, ItemRegistry itemReg,
        bool captureRaw, StringPool pool)
    {
        var occ6 = 0; // the order of field 6 here matches WalkTop — that is how an edit is addressed
        var occ8 = 0; // the same for field 8 (block entities): without the ordinal they cannot be opened in the editor
        // A span walker, not PbReader.ReadMessage: the latter copies every len-delimited payload and
        // recursively parses it, so it copied each block field (and every plane inside it) per chunk.
        // Only the entity/block-entity payloads are materialised now; blocks are decoded in place.
        var r = new PbSpanReader(blob, maxFields: 5000);
        while (r.MoveNext())
        {
            if (r.WireType != 2) continue;
            switch (r.Field)
            {
                case 6:
                    cd.AddEntity(ParseEntity(r.Bytes, cd.ChunkX, cd.ChunkY, cd.ChunkZ, cd.Position, occ6++, reg, itemReg, pool));
                    break;
                case 8:
                    cd.AddBlockEntity(ParseBlockEntity(r.Bytes, cd.ChunkX, cd.ChunkY, cd.ChunkZ, cd.Position, occ8++, reg, itemReg, pool));
                    break;
            }
        }
        if (captureRaw && blob.Length > 0) cd.Raw = MakeRaw("chunk", cd.Position, null, blob);
    }

    /// <summary>
    ///     Decodes the block statistics the scan skipped: per-chunk solids/fluids/air, per-chunk code counts and
    ///     the world totals. Re-reads the chunk blobs (a second 6-GB pass) but only when someone asks for the
    ///     numbers; the model itself is complete without them.
    /// </summary>
    public static void ComputeBlockStats(SaveScanResult res, VSSaveFile save, IProgress<long>? progress = null)
    {
        var reg = save.BlockRegistry;
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 12) };
        const int PageSize = 4096;
        long last = long.MinValue;
        var at = 0;
        var done = 0;
        while (true)
        {
            var page = save.ReadChunkRowsPage(last, PageSize);
            if (page.Count == 0) break;
            if (at + page.Count > res.Chunks.Count ||
                res.Chunks[at].Position != page[0].Position ||
                res.Chunks[at + page.Count - 1].Position != page[^1].Position)
                throw new InvalidOperationException(
                    $"chunk pages changed under the scan (at {at}: expected {res.Chunks[at].Position}, got {page[0].Position})");
            var baseIndex = at;
            Parallel.For(0, page.Count, po,
                idx =>
                {
                    var cd = res.Chunks[baseIndex + idx];
                    MeasureChunkHistogram(cd, page[idx].Data);
                    BuildChunkBlockCodes(cd, reg);
                    var d = Interlocked.Increment(ref done);
                    if (progress != null && (d & 1023) == 0) progress.Report(d);
                });
            at += page.Count;
            last = page[^1].Position;
        }
        progress?.Report(done);

        var counts = new Dictionary<string, long>(res.WorldBlockCodeCounts.Count);
        long solid = 0, fluid = 0, air = 0, ents = 0, bes = 0;
        foreach (var cd in res.Chunks)
        {
            solid += cd.SolidCells;
            fluid += cd.FluidCells;
            air += cd.AirCells;
            ents += cd.Entities.Count;
            bes += cd.BlockEntities.Count;
            foreach (var (code, cnt) in cd.BlockCodeCounts)
            {
                counts.TryGetValue(code, out var v);
                counts[code] = v + cnt;
            }
        }
        // Published in one replace so a reader never sees a half-filled number.
        res.ReplaceWorldBlockCodeCounts(counts);
        res.TotalSolid = solid;
        res.TotalFluid = fluid;
        res.TotalAir = air;
        res.EntityCount = (int)ents;
        res.BlockEntityCount = (int)bes;
        res.BlockStatsComputed = true;
    }

    /// <summary>Block statistics (solids field1 + liquids field16) in one fused pass: no 32 768-cell arrays,
    /// no per-cell CodeOf lookup. The per-thread histogram is reset through the touched list.</summary>
    private static void MeasureChunkHistogram(ChunkScanData cd, byte[] blob)
    {
        var hist = _idHistogram ??= new long[ushort.MaxValue + 1];
        var touched = _histTouched ??= new List<int>(256);
        touched.Clear();
        ChunkBlockDecoder.DecodeChunkHistogram(blob, hist, touched, out cd.SolidCells, out cd.FluidCells, out cd.AirCells);
    }

    /// <summary>Folds the per-thread histogram into the chunk's id counts and clears it.
    /// The ids in `touched` are distinct by construction, so the per-chunk histogram is written once,
    /// without the code-string dictionary that used to be built (and thrown away) for every chunk row.
    /// A registry where two ids share a code is still packed by id: `BlockCodeCounts` merges the
    /// duplicates when someone enumerates it.</summary>
    private static void BuildChunkBlockCodes(ChunkScanData cd, BlockRegistry reg)
    {
        var hist = _idHistogram ??= new long[ushort.MaxValue + 1];
        var touched = _histTouched ??= new List<int>(256);
        cd.BlockCodeCounts.Reset(reg, touched.Count);
        foreach (var id in touched)
        {
            cd.BlockCodeCounts.Add((ushort)id, hist[id]);
            hist[id] = 0;
        }
    }

    /// <summary>Per-thread id histogram reused across chunks (512 KiB once per worker).</summary>
    [ThreadStatic] private static long[]? _idHistogram;
    [ThreadStatic] private static List<int>? _histTouched;

    private static EntityInfo ParseEntity(ReadOnlySpan<byte> b, int cx, int cy, int cz, long position, int occurrence,
        BlockRegistry blocks, ItemRegistry items, StringPool pool)
    {
        var ent = new EntityInfo
        {
            ChunkX = cx, ChunkY = cy, ChunkZ = cz, DataLength = b.Length,
            Position = position, Occurrence = occurrence
        };
        ent.SpeciesCodes = ExtractSpeciesCodes(b, out var species, pool);
        ent.Species = species;
        ent.Domain = EntityInfo.DomainOf(species);
        if (species is string sp)
        {
            var core = EntityInfo.PathOf(sp);                          // the domain is not part of the kind
            var parts = core.Split('-');
            ent.Kind = pool.Intern(parts[0]);                          // "wolf", "fox", "chicken"
            ent.SexAge = parts.Length <= 2
                ? (parts.Length == 2 ? pool.Intern(parts[1]) : null)   // "hen", "aged"
                : pool.Intern(string.Join("-", parts.Skip(parts.Length - 2))); // "adult-male", "baby-female"
        }
        ent.CustomName = ExtractCustomName(b, pool);
        foreach (var k in EntityAttrKeys)
            if (ScanBytes(b, Encoding.ASCII.GetBytes(k))) ent.Attributes[k] = "1";
        ent.Tokens = ExtractTokens(b, 60, pool);
        ent.Fields = ExtractEntityFields(b, pool);   // all fields with their values
        // The tree parser needs a byte[] (it copies attribute payloads while parsing). Most entities have no
        // inventory, so the payload is materialised only after the cheap "slots" probe says it is worth it.
        if (InventoryParser.MightContainInventory(b))
            ent.Inventories = InventoryParser.ParseEntity(b.ToArray(), ent, blocks, items);
        return ent;
    }

    /// <summary>
    ///     All codes of the entity and the chosen "kind" — **the last code in the blob**.
    ///
    ///     VS writes the entity's own code at the end (`Entity.ToBytes`), while earlier in the blob there are
    ///     nested references and service tokens that look like a code:
    ///     <list type="bullet">
    ///         <item>the killer's code after `deathByEntity` — a duck killed by a fox was named a fox
    ///             (`game:fox-red-adult-male`, while its own `fowlmod:duck-blue-hen` came later);</item>
    ///         <item>the foetus code of a pregnant mare (`…:horse-baby-female-destrier` at 20 % of the blob
    ///             against its own code at 97 %) and the item codes in the inventory
    ///             (`…:item-creature-horse-adult-male-destrier`);</item>
    ///         <item>attribute keys with a domain (`jaunt:stamina`) and behaviour names
    ///             (`entitybehavior-debuff-essenceexhaustion-eligible` — that is what 570
    ///             armour stands out of 581 entities were called in a real save);</item>
    ///         <item>truncated inventory keys (`slot-0` → `slot-`, `baseseat-0` → `baseseat-`):
    ///             the requirement to end with a letter cuts them off.</item>
    ///     </list>
    ///     Measured on two real saves: the chosen code lies at 0.68–0.97 of the blob length (median 0.93),
    ///     "last" matched the root of the class name in 583 of 584 cases and 1774 of 1776, while
    ///     "first" matched in 12 and 12. So the rule is simple: the later candidate wins,
    ///     and a `domain:path` (with a mod domain) beats its own "bare" path.
    /// </summary>
    private static List<string> ExtractSpeciesCodes(ReadOnlySpan<byte> b, out string? species, StringPool pool)
    {
        var domains = ExtractDomainCodes(b, pool);
        var bare = ExtractBareCodes(b, domains, pool);  // we do not duplicate tokens inside an already found code
        var last = (Pos: -1, Code: (string?)null);
        foreach (var d in domains)
            if (d.Pos > last.Pos) last = (d.Pos, d.Code);
        foreach (var t in bare)
            if (t.Pos > last.Pos) last = (t.Pos, t.Code);
        species = last.Code;

        var codes = new List<string>(domains.Count + bare.Count);
        codes.AddRange(domains.Select(d => d.Code));
        codes.AddRange(bare.Select(t => t.Code));
        return codes.Distinct().ToList();
    }

    /// <summary>Codes of the form `domain:path` with any domain. <see cref="ExtractGameCodes" /> looks only for
    /// `game:`, so it does not find mod entities at all — their domain (`equusdestrier`, `fowlmod`,
    /// `crfish`) goes into the code string. The position is needed to tell an entity code from an attribute key:
    /// keys sit at the start of the blob, the kind code at the end (see <see cref="ExtractSpeciesCodes" />).
    ///
    ///     A code is a length-prefixed string, and the length must be checked: the length byte itself can be
    ///     a printable character (`0x30` = `'0'`), and without the check it stuck to the domain, yielding
    ///     `0crfish:cagedfish-…` instead of `crfish:cagedfish-…` — there were 10 of those in a real save.</summary>
    private static List<(int Pos, string Code)> ExtractDomainCodes(ReadOnlySpan<byte> b, StringPool pool)
    {
        var res = new List<(int, string)>();
        for (int i = 1; i < b.Length; i++)
        {
            int len = b[i - 1];                                          // the string length byte
            if (len < 4 || i + len > b.Length || !IsCodeWord(b[i])) continue;
            int end = i + len;
            int j = i;
            while (j < end && IsCodeWord(b[j])) j++;
            if (j == i || j >= end || b[j] != ':') continue;
            j++;
            if (j >= end || !IsCodeWord(b[j])) continue;                 // the path does not start with a separator
            int path = j;
            while (j < end && IsCodePath(b[j])) j++;
            if (j != end || j - path < 2) continue;                      // the string must be exactly the code, with no tail
            var code = pool.InternAscii(b, i, len);
            if (char.IsAsciiLetterOrDigit(code[^1])) res.Add((i, code));   // a truncated one (`foo-`) is not a code
        }
        return res;
    }

    private static bool IsCodeWord(byte c) => c is >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'_';
    private static bool IsCodePath(byte c) => IsCodeWord(c) || c == (byte)'-' || c == (byte)'.' || c == (byte)'/';

    /// <summary>
    ///     The entity nametag is the only human-readable name in the blob. In WatchedAttributes it is the group
    ///     `nametag`, inside it the key `name` with the value; both the key and the value are length-prefixed strings
    ///     (`07 nametag`, `04 name`, `09 Moonlight`). The value is decoded as UTF-8, so non-Latin characters
    ///     in the name are not lost, even though the other dump fields are read as ASCII.
    /// </summary>
    private static string? ExtractCustomName(ReadOnlySpan<byte> b, StringPool pool)
    {
        var nametag = IndexOfPrefixed(b, "nametag", 0);
        if (nametag >= 0)
        {
            var name = IndexOfPrefixed(b, "name", nametag + 1);
            if (name >= 0 && name - nametag <= 64 && ReadPrefixedText(b, name + 5, 64) is { } v) return pool.Intern(v);
        }
        for (var i = IndexOfPrefixed(b, "name", 0); i >= 0; i = IndexOfPrefixed(b, "name", i + 1))
            if (ReadPrefixedText(b, i + 5, 64) is { } v) return pool.Intern(v);
        return null;
    }

    /// <summary>Position of the length-prefixed string <paramref name="s" /> (of the length byte followed by the string itself).</summary>
    private static int IndexOfPrefixed(ReadOnlySpan<byte> b, string s, int from)
    {
        if (s.Length is 0 or > 127) return -1;
        for (int i = Math.Max(0, from); i + 1 + s.Length <= b.Length; i++)
        {
            if (b[i] != s.Length) continue;                             // the length must be exactly this string, not a substring
            int j = 0;
            while (j < s.Length && b[i + 1 + j] == s[j]) j++;
            if (j == s.Length) return i;
        }
        return -1;
    }

    /// <summary>Length-prefixed text at the position of the length byte: free of control characters, valid UTF-8.</summary>
    private static string? ReadPrefixedText(ReadOnlySpan<byte> b, int at, int max)
    {
        if (at < 0 || at >= b.Length) return null;
        int len = b[at];
        if (len == 0 || len > max || at + 1 + len > b.Length) return null;
        for (int k = 0; k < len; k++)
        {
            byte c = b[at + 1 + k];
            if (c < 32 || c == 127) return null;
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b.Slice(at + 1, len));
        }
        catch (ArgumentException)
        {
            return null;                                                // not UTF-8 — the name cannot be decoded
        }
    }

    private static BlockEntityInfo ParseBlockEntity(ReadOnlySpan<byte> b, int cx, int cy, int cz, long position, int occurrence,
        BlockRegistry blocks, ItemRegistry items, StringPool pool)
    {
        var be = new BlockEntityInfo
        {
            DataLength = b.Length, ChunkX = cx, ChunkY = cy, ChunkZ = cz,
            Position = position, Occurrence = occurrence
        };
        be.SetSpeciesCodes(ExtractGameCodes(b, pool));
        be.Type = FirstToken(b, 3, pool);
        if (InventoryParser.MightContainInventory(b))
            be.SetInventories(InventoryParser.ParseBlockEntity(b.ToArray(), be, blocks, items));
        return be;
    }

    /// <summary>The first identifier word of at least <paramref name="min" /> characters — the block entity type.
    /// The old code built the whole 60-token list (and a `Distinct` copy) per block entity just to take its
    /// first element; on the 6.3-GB save that was 31M token references and 1.87M temporary lists.</summary>
    private static string? FirstToken(ReadOnlySpan<byte> b, int min, StringPool pool)
    {
        int i = 0;
        while (i < b.Length)
        {
            if (b[i] >= 48 && b[i] < 127 && (char.IsLetter((char)b[i]) || b[i] == '_'))
            {
                int j = i;
                while (j < b.Length && b[j] >= 32 && b[j] < 127
                       && (char.IsLetterOrDigit((char)b[j]) || b[j] == '_')) j++;
                if (j - i >= min) return pool.InternAscii(b, i, j - i);
                i = j;
            }
            else i++;
        }
        return null;
    }

    /// <summary>Entity fields from the ECX blob: length-prefixed strings (the name) followed by a value
    /// (a length-prefixed string or a varint). Best-effort against the VS framing; some values may be
    /// shifted, but it outputs ALL fields with their values for display.</summary>
    private static List<string> ExtractEntityFields(ReadOnlySpan<byte> b, StringPool pool)
    {
        var res = new List<string>();
        int i = 0, n = b.Length;
        while (i < n)
        {
            int L = b[i];
            if (L > 0 && L < 60 && i + 1 + L <= n && IsIdent(b, i + 1, L))
            {
                var name = pool.InternAscii(b, i + 1, L);
                i += 1 + L;
                if (i < n)
                {
                    int L2 = b[i];
                    if (L2 > 0 && L2 < 300 && i + 1 + L2 <= n && IsPrintable(b, i + 1, L2))
                    {
                        var val = pool.InternAscii(b, i + 1, L2);
                        res.Add(pool.Intern($"{name} = {val}"));
                        i += 1 + L2;
                        continue;
                    }
                    if (TryVarint(b, i, out long v, out int ni) && v >= 0 && v < 10000000 && ni > i)
                    {
                        res.Add($"{name} = {v}");
                        i = ni;
                        continue;
                    }
                }
                res.Add(name);
            }
            else i++;
        }
        return res.Distinct().ToList();
    }

    private static bool IsIdent(ReadOnlySpan<byte> b, int s, int len)
    {
        if (len == 0) return false;
        byte c0 = b[s];
        if (!(c0 >= 'A' && c0 <= 'Z' || c0 >= 'a' && c0 <= 'z' || c0 == '_')) return false;
        for (int k = 1; k < len; k++)
        {
            byte c = b[s + k];
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.')) return false;
        }
        return true;
    }

    private static bool IsPrintable(ReadOnlySpan<byte> b, int s, int len)
    {
        for (int k = 0; k < len; k++)
        {
            byte c = b[s + k];
            if (c < 32 || c >= 127) return false;
        }
        return true;
    }

    private static bool TryVarint(ReadOnlySpan<byte> b, int i, out long val, out int next)
    {
        val = 0; next = i;
        for (int sh = 0; sh < 63 && next < b.Length; sh += 7)
        {
            byte x = b[next++];
            val |= (long)(x & 0x7F) << sh;
            if ((x & 0x80) == 0) { return true; }
        }
        val = -1; next = i;
        return false;
    }

    private static bool ScanBytes(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || needle.Length > hay.Length) return false;
        for (int i = 0; i <= hay.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    /// <summary>All `game:...` codes in the blob (domain:type-variations).</summary>
    private static List<string> ExtractGameCodes(ReadOnlySpan<byte> b, StringPool pool)
    {
        var res = new List<string>();
        for (int i = 0; i + 5 <= b.Length; i++)
        {
            if (b[i] == 'g' && b[i + 1] == 'a' && b[i + 2] == 'm' && b[i + 3] == 'e' && b[i + 4] == ':')
            {
                int j = i + 5;
                while (j < b.Length && b[j] >= 32 && b[j] < 127 && (char.IsLetterOrDigit((char)b[j]) || b[j] == '-' || b[j] == '_' || b[j] == '.' || b[j] == '/'))
                    j++;
                // j - i counts the "game:" prefix as well; a bare `game:` is not a code
                if (j - i > 6 && char.IsAsciiLetterOrDigit((char)b[j - 1])) res.Add(pool.InternAscii(b, i, j - i));   // `game:foo-` is truncated
                i = j + 1;
            }
        }
        return res.Distinct().ToList();
    }

    // Keys that look like a "kind-variant" (with a hyphen) but are not an entity code in fact.
    private static readonly HashSet<string> BareCodeStoplist = new(StringComparer.Ordinal)
    {
        "damageovertime-activeeffect", "damageovertime-activeeffects", "meta-spawner"
    };

    /// <summary>"Bare" kind codes without the game: prefix (e.g. "bowtorn-nightmare") — lowercase hyphenated
    /// tokens that are not in the stop list. A token must end with a letter: truncated inventory keys
    /// (`slot-0` → `slot-`, `baseseat-0`) are not kinds. Tokens lying inside an already found
    /// `domain:path` (<paramref name="domains" />) are skipped — otherwise the same code would be found twice,
    /// and its path without the domain would end up "last" (see <see cref="ExtractSpeciesCodes" />).</summary>
    private static List<(int Pos, string Code)> ExtractBareCodes(ReadOnlySpan<byte> b, List<(int Pos, string Code)> domains,
        StringPool pool)
    {
        var res = new List<(int, string)>();
        int i = 0;
        while (i < b.Length)
        {
            byte c = b[i];
            if (c >= 'a' && c <= 'z')
            {
                int j = i;
                while (j < b.Length && ((b[j] >= 'a' && b[j] <= 'z') || b[j] == '-')) j++;
                int n = j - i;
                if (n >= 5)
                {
                    var t = pool.InternAscii(b, i, n);
                    // only hyphenated tokens look like a "kind-variant"; without a hyphen it is a fragment of camelCase keys
                    if (t.Contains('-') && char.IsAsciiLetter(t[^1]) && !BareCodeStoplist.Contains(t)
                        && !domains.Any(d => i >= d.Pos && i < d.Pos + d.Code.Length))
                        res.Add((i, t));
                }
                i = j;
            }
            else i++;
        }
        return res;
    }

    /// <summary>Printable identifier words in the blob (for dumping into the UI).</summary>
    private static List<string> ExtractTokens(ReadOnlySpan<byte> b, int cap, StringPool pool)
    {
        var res = new List<string>();
        int i = 0;
        while (i < b.Length && res.Count < cap)
        {
            if (b[i] >= 48 && b[i] < 127 && (char.IsLetter((char)b[i]) || b[i] == '_'))
            {
                int j = i;
                while (j < b.Length && b[j] >= 32 && b[j] < 127 && (char.IsLetterOrDigit((char)b[j]) || b[j] == '_')) j++;
                if (j - i >= 3) res.Add(pool.InternAscii(b, i, j - i));
                i = j;
            }
            else i++;
        }
        return res.Distinct().ToList();
    }

    /// <summary>
    ///     Reads one mapregion blob and turns its top-level protobuf fields into one line each. Called lazily
    ///     from <see cref="RegionScanData.Layers" />, never on the open/scan path.
    /// </summary>
    private static List<string> ParseRegionLayers(VSSaveFile save, long position, bool captureRaw, RegionScanData rd)
    {
        var layers = new List<string>();
        var blob = save.ReadRegion(position) ?? Array.Empty<byte>();
        if (captureRaw && blob.Length > 0) rd.Raw = MakeRaw("mapregion", position, null, blob);
        foreach (var f in PbReader.ReadMessage(blob, maxDepth: 1, maxFields: 2000))
        {
            if (f.WireType != 2 || f.Bytes is null) continue;
            var marker = FirstAscii(f.Bytes, 14);
            layers.Add($"f{f.Field} len={f.Length}" + (marker.Length > 0 ? $" marker='{marker}'" : ""));
        }
        return layers;
    }

    private static string FirstAscii(ReadOnlySpan<byte> b, int max)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < b.Length && sb.Length < max; i++)
        {
            char c = (char)b[i];
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0) sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    private static RawBlob MakeRaw(string table, long key1, string? key2, byte[] data)
    {
        return new RawBlob
        {
            Table = table, Key1 = key1, Key2 = key2, Length = data.Length, Data = data,
            Preview = HexPreview(data)
        };
    }

    private static string HexPreview(byte[] b)
    {
        int take = Math.Min(16, b.Length);
        var sb = new StringBuilder();
        for (int i = 0; i < take; i++) sb.Append(b[i].ToString("x2")).Append(' ');
        return sb.ToString().Trim();
    }
}
