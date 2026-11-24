namespace VSCore;

public enum PlayerDataKind
{
    /// <summary>The character itself: the `EntityPlayer` record of playerdata (health, hunger, stats, skin…).</summary>
    Character,

    /// <summary>One inventory of the character (hotbar, backpack, …) with its ECX fields.</summary>
    Inventory,

    /// <summary>A `gamedata` key that belongs to this player (`&lt;modid&gt;_&lt;key&gt;_&lt;uid&gt;`).</summary>
    ModKey,

    /// <summary>A `gamedata` key of the world that mentions this player inside its value — one entry of it is
    /// this player's, the rest belongs to others.</summary>
    SharedKey
}

/// <summary>One editable piece of a player's data: a tree (character/inventory) or a value (mod key).</summary>
public sealed class PlayerDataBlock
{
    public required PlayerDataKind Kind { get; init; }

    /// <summary>What the window lists: the inventory name, the mod key, "Character (EntityPlayer)".</summary>
    public required string Title { get; init; }

    public string Subtitle { get; init; } = "";

    /// <summary>The character record (only for <see cref="PlayerDataKind.Character" />).</summary>
    public EntityRecord? Entity { get; init; }

    /// <summary>The inventory (only for <see cref="PlayerDataKind.Inventory" />).</summary>
    public PlayerInventory? Inventory { get; init; }

    /// <summary>Index of the inventory in the repeated field of playerdata (edits address it by index).</summary>
    public int InventoryIndex { get; init; } = -1;

    /// <summary>The gamedata entry (only for <see cref="PlayerDataKind.ModKey" /> / <see cref="PlayerDataKind.SharedKey" />).</summary>
    public ModDataEntry? Entry { get; init; }

    /// <summary>Parsed value of the entry — field numbers, values and nested messages.</summary>
    public List<PbNode>? Nodes { get; init; }

    /// <summary>The whole block is deleted: the inventory disappears from playerdata, the key from gamedata.</summary>
    public bool Removed { get; set; }

    /// <summary>The ECX roots to edit: the character's fields or the inventory body fields.</summary>
    public List<ChunkField>? FieldRoots => Entity?.RootFields ?? Inventory?.RootFields;

    /// <summary>Whether anything inside this block was edited or cut.</summary>
    public bool HasChanges
    {
        get
        {
            if (Removed) return true;
            if (FieldRoots is { } roots) return ChunkDataEditor.AnyChange(roots);
            return Nodes is not null && PbTree.AnyChanged(Nodes);
        }
    }
}

/// <summary>
///     What an apply would change, counted in ONE walk: the window recounts on every keystroke, and the three
///     separate traversals it used to run (edited leaves, deleted nodes, marked blocks) walked the same trees
///     three times and disagreed about a node that was both edited and deleted.
/// </summary>
public readonly record struct PlayerDataCounts(
    int FieldsEdited,
    int NodesEdited,
    int NodesRemoved,
    int KeysRemoved,
    int InventoriesRemoved)
{
    /// <summary>Values that would be written: ECX fields and mod-value nodes together — one number for the user.</summary>
    public int ValuesEdited => FieldsEdited + NodesEdited;

    public int BlocksRemoved => KeysRemoved + InventoriesRemoved;

    public bool Changed => ValuesEdited > 0 || NodesRemoved > 0 || BlocksRemoved > 0;

    /// <summary>The pending-edit label: what will be written, in the words the window uses.</summary>
    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (ValuesEdited > 0) parts.Add($"{ValuesEdited} value(s) changed");
            if (NodesRemoved > 0) parts.Add($"{NodesRemoved} node(s) deleted");
            if (KeysRemoved > 0) parts.Add($"{KeysRemoved} mod key(s) deleted");
            if (InventoriesRemoved > 0) parts.Add($"{InventoriesRemoved} inventor(y/ies) deleted");
            return parts.Count == 0 ? "nothing to apply" : string.Join(", ", parts);
        }
    }
}

/// <summary>What a player-data edit produces: new blobs plus the counted changes for the pending-edit label.</summary>
public sealed record PlayerDataBuild(byte[]? NewPlayerdata, byte[]? NewGamedata, PlayerDataCounts Counts)
{
    public bool Changed => NewPlayerdata is not null || NewGamedata is not null;

    public string Summary => Counts.Label;
}

/// <summary>
///     The data of ONE player, all of it, as one editable document: the character record of playerdata,
///     its inventories, and every `gamedata` key that belongs to it or names it. This is the model behind the
///     "Player data" window — a per-player view, because "clear this mod's data" turned out to be a special
///     case of "edit what this player owns".
///
///     Edits are made on the parsed trees (<see cref="ChunkField" /> and <see cref="PbNode" />) and materialise
///     only in <see cref="Build" />, which rebuilds the two blobs in one pass each. Nothing here touches a file:
///     the result goes into the pending edits of the session, like every other edit of the editor.
/// </summary>
public sealed class PlayerDataDocument
{
    private PlayerDataDocument(long playerId, string? uid, string? name, byte[] raw, byte[]? gamedata)
    {
        PlayerId = playerId;
        Uid = uid;
        Name = name;
        Raw = raw;
        Gamedata = gamedata;
    }

    public long PlayerId { get; }
    public string? Uid { get; }
    public string? Name { get; }

    /// <summary>The current playerdata blob (a pending edit when there is one, otherwise the row).</summary>
    public byte[] Raw { get; }

    public byte[]? Gamedata { get; }

    public List<PlayerDataBlock> Blocks { get; } = new();

    public string Label => Name is { Length: > 0 } n ? $"{n} ({Uid})" : Uid ?? $"player {PlayerId}";

    /// <summary>
    ///     Reads everything this player owns. Malformed parts are skipped instead of failing the whole window:
    ///     a single broken mod value must not hide the character and the other keys.
    /// </summary>
    /// <param name="uids">All player uids of the save — needed to tell a per-player key from a world one.</param>
    public static PlayerDataDocument Load(long playerId, string? uid, string? name, byte[] raw, byte[]? gamedata,
        IReadOnlySet<string>? uids = null)
    {
        var doc = new PlayerDataDocument(playerId, uid, name, raw, gamedata);

        EntityRecord? entity = null;
        try
        {
            entity = PlayerDataParser.ParseEntityRecord(raw);
        }
        catch
        {
            entity = null;
        }

        if (entity is not null)
            doc.Blocks.Add(new PlayerDataBlock
            {
                Kind = PlayerDataKind.Character,
                Title = $"Character — {entity.TypeName ?? "EntityPlayer"}",
                Subtitle = $"{entity.Fields.Count} field(s) of {entity.RawBytes.Length} B",
                Entity = entity
            });

        PlayerData? parsed = null;
        try
        {
            parsed = PlayerDataParser.Parse(raw, playerId);
        }
        catch
        {
            parsed = null;
        }

        if (parsed is not null)
            for (int i = 0; i < parsed.Inventories.Count; i++)
            {
                var inv = parsed.Inventories[i];
                doc.Blocks.Add(new PlayerDataBlock
                {
                    Kind = PlayerDataKind.Inventory,
                    Title = $"Inventory — {inv.Name ?? "(no name)"}",
                    Subtitle = $"{inv.SlotCount} slot(s), {inv.Items.Count} item(s), {inv.RootFields.Count} field(s)",
                    Inventory = inv,
                    InventoryIndex = i
                });
            }

        if (uid is { Length: > 0 } && gamedata is { Length: > 0 })
        {
            List<ModDataEntry> entries;
            try
            {
                entries = ModDataStore.Read(gamedata, uids);
            }
            catch
            {
                entries = new List<ModDataEntry>();
            }

            // per-player keys first: they are exactly this player's
            foreach (var e in entries.Where(e => e.PlayerUid == uid).OrderBy(e => e.Key, StringComparer.Ordinal))
                doc.Blocks.Add(ModBlock(PlayerDataKind.ModKey, e, isMine: true));
            // A world key is listed only when THIS player really has an entry in it. `MentionsPlayer` answers
            // "some player uid is in the value", so a roster of other players would otherwise show up here as
            // this player's data with a "remove 0 entries" button.
            foreach (var e in entries.Where(e => e.PlayerUid is null && e.MentionsPlayer)
                         .OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                var block = ModBlock(PlayerDataKind.SharedKey, e, isMine: false);
                if (block.Nodes is not null && PbTree.CountEntriesMentioning(block.Nodes, uid!) == 0) continue;
                doc.Blocks.Add(block);
            }
        }

        return doc;
    }

    private static PlayerDataBlock ModBlock(PlayerDataKind kind, ModDataEntry e, bool isMine)
    {
        var nodes = PbTree.Parse(e.Value);
        return new PlayerDataBlock
        {
            Kind = kind,
            Title = e.Key,
            Subtitle = (isMine ? "this player's key" : "world key naming this player") + $", {e.Value.Length} B, {nodes.Count} entry(ies)",
            Entry = e,
            Nodes = nodes
        };
    }

    /// <summary>Whether anything in the document was changed, deleted or marked for deletion.</summary>
    public bool HasChanges => Blocks.Any(b => b.HasChanges);

    /// <summary>
    ///     Marks the value nodes that name the player in a SHARED key (a `&lt;key&gt;ByPlayerUid` dictionary keeps one
    ///     entry per player). Returns how many entries were marked; the caller reports it before applying.
    /// </summary>
    public static int MarkPlayerEntries(PlayerDataBlock block, string? uid)
    {
        if (uid is not { Length: > 0 } || block.Nodes is null) return 0;
        if (block.Kind != PlayerDataKind.SharedKey) return 0;
        return PbTree.RemoveEntriesMentioning(block.Nodes, uid);
    }

    /// <summary>How many entries of a shared key mention the player (computed without changing anything).</summary>
    public static int CountPlayerEntries(PlayerDataBlock block, string? uid)
    {
        if (uid is not { Length: > 0 } || block.Nodes is null) return 0;
        if (block.Kind != PlayerDataKind.SharedKey) return 0;
        return PbTree.CountEntriesMentioning(block.Nodes, uid);
    }

    /// <summary>
    ///     Materialises the document into new blobs. Each blob is rebuilt in ONE pass: several splices in a row
    ///     would work on stale byte ranges as soon as the first body changes length.
    /// </summary>
    public PlayerDataBuild Build()
    {
        var counts = Counts();

        byte[]? entityBody = null;
        if (Blocks.FirstOrDefault(b => b.Kind == PlayerDataKind.Character) is { Entity: { } entity }
            && ChunkDataEditor.AnyChange(entity.RootFields))
            entityBody = ChunkDataEditor.SerializeEntity(entity);

        var bodies = new Dictionary<int, byte[]>();
        var removedInventories = new HashSet<int>();
        foreach (var b in Blocks.Where(b => b.Kind == PlayerDataKind.Inventory))
        {
            if (b.Removed)
            {
                removedInventories.Add(b.InventoryIndex);
                continue;
            }

            if (b.Inventory is { } inv && ChunkDataEditor.AnyChange(inv.RootFields))
                bodies[b.InventoryIndex] = ChunkDataEditor.SerializeFields(inv.RootFields);
        }

        var newValues = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var removedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in Blocks.Where(b => b.Kind is PlayerDataKind.ModKey or PlayerDataKind.SharedKey))
        {
            if (b.Entry is null) continue;
            if (b.Removed)
            {
                removedKeys.Add(b.Entry.Key);
                continue;
            }

            if (b.Nodes is not null && PbTree.AnyChanged(b.Nodes))
                newValues[b.Entry.Key] = PbTree.Serialize(b.Nodes);
        }

        byte[]? newPlayerdata = entityBody is not null || bodies.Count > 0 || removedInventories.Count > 0
            ? PlayerDataParser.Rewrite(Raw, entityBody, bodies, removedInventories)
            : null;

        byte[]? newGamedata = null;
        if (Gamedata is { Length: > 0 } g && (newValues.Count > 0 || removedKeys.Count > 0))
            newGamedata = ModDataStore.Rewrite(g, newValues, removedKeys);

        return new PlayerDataBuild(newPlayerdata, newGamedata, counts);
    }

    /// <summary>Everything an apply would change, in ONE walk. A marked block counts as the block, not as its
    /// insides; a deleted subtree counts as one node and is not descended into (its fields are gone, not edited).</summary>
    public PlayerDataCounts Counts()
    {
        int fields = 0, nodes = 0, removed = 0, keys = 0, inventories = 0;
        foreach (var b in Blocks)
        {
            if (b.Removed && b.Kind != PlayerDataKind.Character)
            {
                if (b.Kind == PlayerDataKind.Inventory) inventories++;
                else keys++;
                continue;
            }

            if (b.FieldRoots is { } roots) CountFields(roots, ref fields, ref removed);
            if (b.Nodes is { } ns) CountNodes(ns, ref nodes, ref removed);
        }

        return new PlayerDataCounts(fields, nodes, removed, keys, inventories);
    }

    private static void CountFields(IEnumerable<ChunkField> fields, ref int edited, ref int removed)
    {
        foreach (var f in fields)
        {
            if (f.Removed)
            {
                removed++;
                continue;
            }

            if (f.Dirty) edited++;
            CountFields(f.Children, ref edited, ref removed);
        }
    }

    private static void CountNodes(IEnumerable<PbNode> nodes, ref int edited, ref int removed)
    {
        foreach (var node in nodes)
        {
            if (node.Removed)
            {
                removed++;
                continue;
            }

            if (node.Dirty) edited++;
            if (node.Children is not null) CountNodes(node.Children, ref edited, ref removed);
        }
    }
}
