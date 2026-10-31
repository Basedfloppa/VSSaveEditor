using System.Text;
using VSCore.Format;

namespace VSCore;

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
    public long TotalSolid, TotalFluid, TotalAir;
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
    public int WorldX, WorldY, WorldZ;      // world corner of the chunk in blocks (ch*32)
    public Dictionary<string, long> BlockCodeCounts { get; } = new();
    public long SolidCells, FluidCells, AirCells;
    public List<EntityInfo> Entities { get; } = new();
    public List<BlockEntityInfo> BlockEntities { get; } = new();
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
    public List<string> Tokens { get; set; } = new();
    public List<string> SpeciesCodes { get; set; } = new();
    public int DataLength { get; set; }

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
    public List<InventoryInfo> Inventories { get; set; } = new();
}

/// <summary>Region (mapregion): worldgen/geological layers.</summary>
public sealed class RegionScanData
{
    public long Position { get; set; }
    public int DataLength { get; set; }
    public List<string> Layers { get; } = new();
    public RawBlob? Raw { get; set; }
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

    public static SaveScanResult Scan(VSSaveFile save, IProgress<double>? progress = null, bool captureRaw = false)
    {
        var res = new SaveScanResult();
        var reg = save.BlockRegistry;
        var itemReg = save.ItemRegistry;

        // ---- Meta / gamedata ----
        res.Meta.Version = save.DetectVersion();
        res.Meta.Seed = save.DetectSeed();
        res.Meta.TableNames = save.TableNames;
        res.Meta.GamedataLength = save.GamedataLength.GetValueOrDefault();
        res.Meta.RegionCount = save.MapRegions.Count;
        res.Meta.ChunkCount = save.Chunks.Count;
        var gd = save.ReadGamedata();
        if (captureRaw && gd is { Length: > 0 })
            res.GamedataRaw = MakeRaw("gamedata", 1, null, gd);

        // ---- Mapregions (worldgen/geology) ----
        foreach (var mr in save.MapRegions)
        {
            var rd = new RegionScanData { Position = mr.Position, DataLength = mr.DataLength };
            var b = save.ReadRegion(mr.Position) ?? Array.Empty<byte>();
            if (captureRaw && b.Length > 0) rd.Raw = MakeRaw("mapregion", mr.Position, null, b);
            foreach (var f in PbReader.ReadMessage(b, maxDepth: 1, maxFields: 2000))
            {
                if (f.WireType != 2 || f.Bytes is null) continue;
                var marker = FirstAscii(f.Bytes, 14);
                rd.Layers.Add($"f{f.Field} len={f.Length}" + (marker.Length > 0 ? $" marker='{marker}'" : ""));
            }
            res.Regions.Add(rd);
        }

        // ---- Chunks (blocks/liquids/entities/block entities) ----
        // Positions first, then pages of blobs: retaining every chunk blob (518 MiB on the large save)
        // made the scan peak depend on the save size instead of on the model it actually builds.
        var positions = save.ChunkPositions;
        int n = positions.Count;
        var chunks = new ChunkScanData[n];
        int total = Math.Max(1, n), done = 0, start = 0;
        long last = long.MinValue;
        const int PageSize = 4096;
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) };
        while (start < n)
        {
            var page = save.ReadChunkRowsPage(last, PageSize);
            if (page.Count == 0) break;
            // capture the page index range; the lambda must not see the loop's moving `start`
            int pageStart = start;
            Parallel.For(0, page.Count, po, idx =>
            {
                var row = page[idx];
                var cd = new ChunkScanData();
                ProcessChunk(row.Position, row.Data, reg, itemReg, cd, captureRaw);
                chunks[pageStart + idx] = cd;
                int d = Interlocked.Increment(ref done);
                if (progress != null && (d % 32 == 0 || d == n)) progress.Report((double)d / total);
            });
            start += page.Count;
            last = page[^1].Position;
        }
        progress?.Report(1.0);

        // ---- Aggregation ----
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

        // ---- Mapchunks (client map) ----
        foreach (var mr in save.MapchunkRowsWithData)
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
        res.Meta.MapchunkCount = res.Mapchunks.Count;

        // ---- Players (playerdata) ----
        foreach (var p in save.PlayerRows)
        {
            var pd = new PlayerScanData { PlayerId = p.Id, PlayerUid = p.Uid };
            var b = p.Data ?? Array.Empty<byte>();
            pd.Tokens = ExtractTokens(b, 80);
            pd.SpeciesCodes = ExtractGameCodes(b);
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
                catch
                {
                    // a damaged playerdata record must not cancel the scan of the other tables
                }
            }

            res.Players.Add(pd);
        }
        res.Meta.PlayerCount = res.Players.Count;
        res.ItemStackCount = res.Inventories.Sum(i => i.Items.Count);

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

    private static void ProcessChunk(long position, byte[] blob, BlockRegistry reg, ItemRegistry itemReg,
        ChunkScanData cd, bool captureRaw)
    {
        cd.Position = position;
        cd.DataLength = blob.Length;
        var (cx, cy, cz) = ChunkPosHelper.DecodeIndex((ulong)position);
        cd.ChunkX = cx; cd.ChunkY = cy; cd.ChunkZ = cz;
        cd.WorldX = cx * 32; cd.WorldY = cy * 32; cd.WorldZ = cz * 32;

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
                    cd.Entities.Add(ParseEntity(r.Bytes.ToArray(), cx, cy, cz, position, occ6++, reg, itemReg));
                    break;
                case 8:
                    cd.BlockEntities.Add(ParseBlockEntity(r.Bytes.ToArray(), cx, cy, cz, position, occ8++, reg, itemReg));
                    break;
            }
        }
        if (captureRaw && blob.Length > 0) cd.Raw = MakeRaw("chunk", position, null, blob);

        // Block statistics (solids field1 + liquids field16) in one fused pass: no 32 768-cell arrays,
        // no per-cell CodeOf/Dictionary<string,long>. The string dictionary is touched once per distinct
        // id in the chunk; the per-thread histogram is reset through the touched list.
        var hist = _idHistogram ??= new long[ushort.MaxValue + 1];
        var touched = _histTouched ??= new List<int>(256);
        touched.Clear();
        ChunkBlockDecoder.DecodeChunkHistogram(blob, hist, touched, out cd.SolidCells, out cd.FluidCells, out cd.AirCells);
        foreach (var id in touched)
        {
            var code = reg.CodeOf(id) ?? "id:" + id;
            cd.BlockCodeCounts.TryGetValue(code, out var v);
            cd.BlockCodeCounts[code] = v + hist[id];
            hist[id] = 0;
        }
    }

    /// <summary>Per-thread id histogram reused across chunks (512 KiB once per worker).</summary>
    [ThreadStatic] private static long[]? _idHistogram;
    [ThreadStatic] private static List<int>? _histTouched;

    private static EntityInfo ParseEntity(byte[] b, int cx, int cy, int cz, long position, int occurrence,
        BlockRegistry blocks, ItemRegistry items)
    {
        var ent = new EntityInfo
        {
            ChunkX = cx, ChunkY = cy, ChunkZ = cz, DataLength = b.Length,
            Position = position, Occurrence = occurrence
        };
        ent.SpeciesCodes = ExtractSpeciesCodes(b, out var species);
        ent.Species = species;
        ent.Domain = EntityInfo.DomainOf(species);
        if (species is string sp)
        {
            var core = EntityInfo.PathOf(sp);                          // the domain is not part of the kind
            var parts = core.Split('-');
            ent.Kind = parts[0];                                       // "wolf", "fox", "chicken"
            ent.SexAge = parts.Length <= 2
                ? (parts.Length == 2 ? parts[1] : null)                // "hen", "aged"
                : string.Join("-", parts.Skip(parts.Length - 2));     // "adult-male", "baby-female"
        }
        ent.CustomName = ExtractCustomName(b);
        foreach (var k in EntityAttrKeys)
            if (ScanBytes(b, Encoding.ASCII.GetBytes(k))) ent.Attributes[k] = "1";
        ent.Tokens = ExtractTokens(b, 60);
        ent.Fields = ExtractEntityFields(b);   // all fields with their values
        if (InventoryParser.MightContainInventory(b))
            ent.Inventories = InventoryParser.ParseEntity(b, ent, blocks, items);
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
    private static List<string> ExtractSpeciesCodes(byte[] b, out string? species)
    {
        var domains = ExtractDomainCodes(b);
        var bare = ExtractBareCodes(b, domains);        // we do not duplicate tokens inside an already found code
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
    private static List<(int Pos, string Code)> ExtractDomainCodes(byte[] b)
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
            var code = Encoding.ASCII.GetString(b, i, len);
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
    private static string? ExtractCustomName(byte[] b)
    {
        var nametag = IndexOfPrefixed(b, "nametag", 0);
        if (nametag >= 0)
        {
            var name = IndexOfPrefixed(b, "name", nametag + 1);
            if (name >= 0 && name - nametag <= 64 && ReadPrefixedText(b, name + 5, 64) is { } v) return v;
        }
        for (var i = IndexOfPrefixed(b, "name", 0); i >= 0; i = IndexOfPrefixed(b, "name", i + 1))
            if (ReadPrefixedText(b, i + 5, 64) is { } v) return v;
        return null;
    }

    /// <summary>Position of the length-prefixed string <paramref name="s" /> (of the length byte followed by the string itself).</summary>
    private static int IndexOfPrefixed(byte[] b, string s, int from)
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
    private static string? ReadPrefixedText(byte[] b, int at, int max)
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
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b, at + 1, len);
        }
        catch (ArgumentException)
        {
            return null;                                                // not UTF-8 — the name cannot be decoded
        }
    }

    private static BlockEntityInfo ParseBlockEntity(byte[] b, int cx, int cy, int cz, long position, int occurrence,
        BlockRegistry blocks, ItemRegistry items)
    {
        var be = new BlockEntityInfo
        {
            DataLength = b.Length, ChunkX = cx, ChunkY = cy, ChunkZ = cz,
            Position = position, Occurrence = occurrence
        };
        be.Tokens = ExtractTokens(b, 60);
        be.SpeciesCodes = ExtractGameCodes(b);
        be.Type = be.Tokens.FirstOrDefault(t => t.Length >= 3);
        if (InventoryParser.MightContainInventory(b))
            be.Inventories = InventoryParser.ParseBlockEntity(b, be, blocks, items);
        return be;
    }

    /// <summary>Entity fields from the ECX blob: length-prefixed strings (the name) followed by a value
    /// (a length-prefixed string or a varint). Best-effort against the VS framing; some values may be
    /// shifted, but it outputs ALL fields with their values for display.</summary>
    private static List<string> ExtractEntityFields(byte[] b)
    {
        var res = new List<string>();
        int i = 0, n = b.Length;
        while (i < n)
        {
            int L = b[i];
            if (L > 0 && L < 60 && i + 1 + L <= n && IsIdent(b, i + 1, L))
            {
                var name = Encoding.ASCII.GetString(b, i + 1, L);
                i += 1 + L;
                if (i < n)
                {
                    int L2 = b[i];
                    if (L2 > 0 && L2 < 300 && i + 1 + L2 <= n && IsPrintable(b, i + 1, L2))
                    {
                        var val = Encoding.ASCII.GetString(b, i + 1, L2);
                        res.Add($"{name} = {val}");
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

    private static bool IsIdent(byte[] b, int s, int len)
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

    private static bool IsPrintable(byte[] b, int s, int len)
    {
        for (int k = 0; k < len; k++)
        {
            byte c = b[s + k];
            if (c < 32 || c >= 127) return false;
        }
        return true;
    }

    private static bool TryVarint(byte[] b, int i, out long val, out int next)
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

    private static bool ScanBytes(byte[] hay, byte[] needle)
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
    private static List<string> ExtractGameCodes(byte[] b)
    {
        var res = new List<string>();
        for (int i = 0; i + 5 <= b.Length; i++)
        {
            if (b[i] == 'g' && b[i + 1] == 'a' && b[i + 2] == 'm' && b[i + 3] == 'e' && b[i + 4] == ':')
            {
                int j = i + 5;
                var sb = new StringBuilder("game:");
                while (j < b.Length && b[j] >= 32 && b[j] < 127 && (char.IsLetterOrDigit((char)b[j]) || b[j] == '-' || b[j] == '_' || b[j] == '.' || b[j] == '/'))
                    sb.Append((char)b[j++]);
                if (sb.Length > 6 && char.IsAsciiLetterOrDigit(sb[^1])) res.Add(sb.ToString());   // `game:foo-` is truncated
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
    private static List<(int Pos, string Code)> ExtractBareCodes(byte[] b, List<(int Pos, string Code)> domains)
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
                    var t = Encoding.ASCII.GetString(b, i, n);
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
    private static List<string> ExtractTokens(byte[] b, int cap)
    {
        var res = new List<string>();
        int i = 0;
        while (i < b.Length && res.Count < cap)
        {
            if (b[i] >= 48 && b[i] < 127 && (char.IsLetter((char)b[i]) || b[i] == '_'))
            {
                int j = i;
                while (j < b.Length && b[j] >= 32 && b[j] < 127 && (char.IsLetterOrDigit((char)b[j]) || b[j] == '_')) j++;
                if (j - i >= 3) res.Add(Encoding.ASCII.GetString(b, i, j - i));
                i = j;
            }
            else i++;
        }
        return res.Distinct().ToList();
    }

    private static string FirstAscii(byte[] b, int max)
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
