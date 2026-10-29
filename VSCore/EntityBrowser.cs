namespace VSCore;

/// <summary>What to show in the "Entities" list: entities only, block entities only, or both.</summary>
public enum EntityCategory
{
    All,
    Entities,
    BlockEntities
}

/// <summary>
///     One row of the "Entities" list: an entity (field 6) or a block entity (field 8).
///     The row is a thin wrapper over the scan model, with no string copies of its own: 20,000+ block entities
///     in a real save must not turn into 20,000 duplicated "haystack" strings.
/// </summary>
public sealed class EntityRow
{
    public EntityInfo? Entity { get; init; }
    public BlockEntityInfo? BlockEntity { get; init; }

    /// <summary>Blob field number: 6 — entity, 8 — block entity. The triple (Position, Occurrence, Field) addresses an edit.</summary>
    public int Field => Entity is not null ? 6 : 8;

    public bool IsBlockEntity => Entity is null;

    public string Name => Entity is { } e
        ? e.Species is { Length: > 0 } s ? e.CodePath : e.AttributeSummary
        : BlockEntity?.Type ?? "?";

    /// <summary>Full code with the domain — `equusdestrier:horse-adult-female-destrier`, `feverstonewilds:toad`;
    /// for vanilla entities it matches <see cref="Name" />. Empty for block entities.</summary>
    public string Code => Entity?.Species ?? "";

    /// <summary>Inventories of the record (a horse's backpack, the contents of a chest). Empty ones are included too:
    /// they show that the container exists even when it is empty.</summary>
    public IReadOnlyList<InventoryInfo> Inventories
        => Entity?.Inventories ?? BlockEntity?.Inventories ?? (IReadOnlyList<InventoryInfo>)Array.Empty<InventoryInfo>();

    public int ItemCount => Inventories.Sum(i => i.Items.Count);
    public bool HasInventories => Inventories.Count > 0;
    public bool HasItems => ItemCount > 0;

    public string InventorySummary => InventoryBrowser.Summary(Inventories);

    /// <summary>Items filter over the inventories of this record; empty if there are no inventories.</summary>
    public string InventoryFilter => !HasInventories
        ? ""
        : InventoryBrowser.KeyPrefix(IsBlockEntity ? InventoryOwnerKind.BlockEntity : InventoryOwnerKind.Entity,
            Position, Occurrence);

    /// <summary>Name given by the player (`nametag`): "Moonlight". Empty for most entities.</summary>
    public string CustomName => Entity?.CustomName ?? "";

    /// <summary>Code domain (`equusdestrier`, `fowlmod`, `game`); empty if the code has no domain.</summary>
    public string Domain => Entity?.Domain is { Length: > 0 } d ? d : "";

    /// <summary>How the record is named in lists and on the card: the human name if there is one, otherwise the code.</summary>
    public string Label => CustomName.Length > 0 ? CustomName : Name;

    /// <summary>The entity "kind" (wolf, fox), or an empty string for block entities.</summary>
    public string Kind => Entity?.Kind ?? "";

    /// <summary>Sex-age of the entity, or the block entity codes.</summary>
    public string Sub => Entity is { } e ? e.SexAge ?? "" : BlockEntity?.CodeSummary ?? "";

    public int ChunkX => Entity?.ChunkX ?? BlockEntity?.ChunkX ?? 0;
    public int ChunkY => Entity?.ChunkY ?? BlockEntity?.ChunkY ?? 0;
    public int ChunkZ => Entity?.ChunkZ ?? BlockEntity?.ChunkZ ?? 0;

    public long Position => Entity?.Position ?? BlockEntity?.Position ?? -1;
    public int Occurrence => Entity?.Occurrence ?? BlockEntity?.Occurrence ?? -1;
    public int Size => Entity?.DataLength ?? BlockEntity?.DataLength ?? 0;

    /// <summary>Whether the record is addressable: only then can it be opened in the editor.</summary>
    public bool IsAddressable => Position >= 0 && Occurrence >= 0;

    public string KindBadge => IsBlockEntity ? "[block entity]" : Kind.Length > 0 ? $"[{Kind}]" : "";

    /// <summary>Mod domain badge: `equusdestrier:horse-…` makes it obvious that a mod added the kind.
    /// The `game` domain is vanilla, so it gets no badge.</summary>
    public string DomainBadge => Domain.Length > 0 && Domain != "game" ? $" [mod: {Domain}]" : "";

    public string ChunkText => $"chunk {ChunkX}:{ChunkZ} y{ChunkY}";

    public string SizeText => $"{Size} B";

    /// <summary>The list row as a single line — that is how the template draws it. The player-given name (if any) comes
    /// first: a horse is looked up by "Moonlight", and the kind has to be right there next to it.</summary>
    public string Display => (CustomName.Length > 0 ? $"{CustomName}  ·  " : "")
                             + $"{Name}  {KindBadge}{DomainBadge}  {Sub}  —  {ChunkText} · {SizeText}"
                             + (ItemCount > 0 ? $" · {ItemCount} item{(ItemCount == 1 ? "" : "s")}" : "");

    /// <summary>
    ///     Substring search over everything the record has: name/kind/sex-age, the player-given name, attributes,
    ///     parsed fields, tokens. Case-insensitive and without allocations per row.
    /// </summary>
    public bool Matches(string needle)
    {
        if (Entity is { } e)
        {
            if (Hit(e.Species) || Hit(e.Kind) || Hit(e.SexAge) || Hit(e.AttributeSummary) || Hit(e.CustomName)) return true;
            foreach (var k in e.Attributes.Keys)
                if (Hit(k))
                    return true;
            foreach (var f in e.Fields)
                if (Hit(f))
                    return true;
            foreach (var c in e.SpeciesCodes)
                if (Hit(c))
                    return true;
            foreach (var t in e.Tokens)
                if (Hit(t))
                    return true;
            return false;
        }

        if (BlockEntity is { } be)
        {
            if (Hit(be.Type) || Hit(be.CodeSummary)) return true;
            foreach (var c in be.SpeciesCodes)
                if (Hit(c))
                    return true;
            foreach (var t in be.Tokens)
                if (Hit(t))
                    return true;
        }

        return false;

        bool Hit(string? s) => s is { Length: > 0 } && s.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
///     Flat list of all entities and block entities of a save for the "Entities" tab: the old
///     "Dump" tab poured out tens of thousands of lines of text (META, histograms, regions, raw blobs)
///     in which a particular entity could not be found, and could only be edited after scrolling to it.
/// </summary>
public static class EntityBrowser
{
    /// <summary>All addressable records of the save. Inactive entities (no kind and no attributes) are skipped:
    /// they have neither a name nor fields on the card.</summary>
    public static List<EntityRow> Build(SaveScanResult r) => Build(r, null);

    /// <summary>
    ///     Records of the part of the save inside the scope. A record is placed by its chunk: the scope has no
    ///     Y borders (it is (cx,cz)), so every y column of a chunk in the scope contributes its records.
    /// </summary>
    public static List<EntityRow> Build(SaveScanResult r, SearchScope? scope)
    {
        var rows = new List<EntityRow>(r.EntityCount + r.BlockEntityCount);
        foreach (var c in r.Chunks)
        {
            if (scope is { } s && !s.ContainsChunk(c.ChunkX, c.ChunkZ)) continue;
            foreach (var e in c.Entities)
                if (e.HasInfo)
                    rows.Add(new EntityRow { Entity = e });
            foreach (var be in c.BlockEntities)
                rows.Add(new EntityRow { BlockEntity = be });
        }

        // The name is the main search key, so the list is sorted by it: "find any entity"
        // becomes scrolling an alphabetical list rather than reading the chunk walk order.
        rows.Sort((a, b) =>
        {
            var n = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (n != 0) return n;
            if (a.IsBlockEntity != b.IsBlockEntity) return a.IsBlockEntity ? 1 : -1;
            if (a.ChunkX != b.ChunkX) return a.ChunkX.CompareTo(b.ChunkX);
            if (a.ChunkZ != b.ChunkZ) return a.ChunkZ.CompareTo(b.ChunkZ);
            return a.ChunkY.CompareTo(b.ChunkY);
        });
        return rows;
    }

    /// <summary>Filter by category and substring. An empty substring filters nothing out.</summary>
    public static List<EntityRow> Filter(IEnumerable<EntityRow> rows, EntityCategory category, string? query)
    {
        var q = (query ?? "").Trim();
        var res = new List<EntityRow>();
        foreach (var row in rows)
        {
            if (category == EntityCategory.Entities && row.IsBlockEntity) continue;
            if (category == EntityCategory.BlockEntities && !row.IsBlockEntity) continue;
            if (q.Length > 0 && !row.Matches(q)) continue;
            res.Add(row);
        }

        return res;
    }

    public static int Count(IEnumerable<EntityRow> rows, EntityCategory category)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (category == EntityCategory.Entities && row.IsBlockEntity) continue;
            if (category == EntityCategory.BlockEntities && !row.IsBlockEntity) continue;
            n++;
        }

        return n;
    }

    /// <summary>
    ///     The most frequent names are what makes an empty filter useful: they show what exists in the world at all,
    ///     and one click puts that into the search.
    /// </summary>
    public static List<(string Name, int Count)> TopNames(IEnumerable<EntityRow> rows, int limit = 18)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = row.Name;
            if (key.Length == 0) continue;
            map.TryGetValue(key, out var n);
            map[key] = n + 1;
        }

        return map.OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }
}
