using System.Text;
using VSCore.Format;

namespace VSCore;

/// <summary>Character (playerdata): uid + named inventories with ECX fields.</summary>
public sealed class PlayerData
{
    public string? Uid;
    public List<PlayerInventory> Inventories = new();
    public byte[] RawBytes = Array.Empty<byte>();
    public long PlayerId;

    /// <summary>Name from the embedded `EntityPlayer` (field 3 of playerdata): this is how the player is recognised
    /// in the item search, while `playeruid` is the technical key.</summary>
    public string? CustomName;

    public bool HasParsedFields => Inventories.Any(i => i.Fields.Count > 0);
}

/// <summary>One player inventory/tree: name + ECX fields (type/key/value, the same format as for entities).</summary>
public sealed class PlayerInventory
{
    public string? Name;                      // "hotbar-...", "backpack", ...
    public List<ChunkField> RootFields = new(); // root fields of the ECX body (for serialisation)
    public List<ChunkField> Fields = new();   // leaf fields for search/display
    public byte[] RawBytes = Array.Empty<byte>();   // the ECX body (the inner field2)

    public int SlotCount;
    public List<ItemStackInfo> Items = new();

    /// <summary>Nested containers (a backpack inside a backpack): the names are the path from the root.</summary>
    public List<InventoryContents> Nested = new();
}

/// <summary>
/// playerdata parser: the top level is protobuf (field1=uid, field2=message),
/// inside the message field1=inventory name, field2=ECX body (the same field format as for entities).
/// Editing: rebuild the ECX body -> splice it into the message -> splice it into the blob.
/// </summary>
public static class PlayerDataParser
{
    public static PlayerData Parse(byte[] blob, long playerId, BlockRegistry? blocks = null, ItemRegistry? items = null)
    {
        var pd = new PlayerData { RawBytes = blob, PlayerId = playerId };
        foreach (var f in PbReader.ReadMessage(blob, maxDepth: 1, maxFields: 2000))
        {
            if (f.WireType != 2 || f.Bytes is null) continue;
            if (f.Field == 1 && pd.Uid is null)
            {
                pd.Uid = CleanAscii(f.Bytes);
                continue;
            }
            if (f.Field == 2)
                pd.Inventories.Add(ParseInventory(f.Bytes, blocks, items));
            else if (f.Field == 3 && pd.CustomName is null)
            {
                // playerdata keeps the EntityPlayer itself next to the inventories. All we need from it
                // is the name: the position in chunks is looked up by playerUID (see PlayersTabService).
                try
                {
                    var entity = InventoryParser.ParsePlayerEntityName(f.Bytes);
                    if (entity is { Length: > 0 }) pd.CustomName = entity;
                }
                catch
                {
                    // not critical: the player will simply stay named by uid
                }
            }
        }
        return pd;
    }

    private static PlayerInventory ParseInventory(byte[] msg, BlockRegistry? blocks, ItemRegistry? items)
    {
        var inv = new PlayerInventory();
        byte[]? body = null;
        foreach (var f in PbReader.ReadMessage(msg, maxDepth: 1, maxFields: 100))
        {
            if (f.WireType != 2 || f.Bytes is null) continue;
            if (f.Field == 1 && inv.Name is null) inv.Name = CleanAscii(f.Bytes);
            else if (f.Field == 2) body = f.Bytes;
        }
        if (body is { Length: > 0 })
        {
            inv.RawBytes = body;
            // Fields — the previous lossless parse (qslots + opaque slots) for editing qslots.
            // Items are a separate tree: they cannot be derived from the flat fields.
            inv.RootFields = ChunkDataParser.ParseFields(body, 0, body.Length);
            // For the Players tab we keep only the top level: qslots is editable, while the slots tree
            // (the contents of the items) is a separate itemstack editing task, not dozens of rows per card.
            inv.Fields = inv.RootFields;
            var parsed = InventoryParser.ParsePlayerInventory(body, inv.Name ?? "inventory", blocks, items);
            var top = parsed.FirstOrDefault(p => p.Name == (inv.Name ?? "inventory")) ?? parsed.FirstOrDefault();
            if (top is not null)
            {
                inv.SlotCount = top.SlotCount;
                inv.Items = top.Items;
                inv.Nested = parsed.Where(p => !ReferenceEquals(p, top)).ToList();
            }
        }
        return inv;
    }

    /// <summary>
    /// Replaces the ECX body of an inventory by its INDEX in <see cref="PlayerData.Inventories"/> (nested splice).
    /// The index is safer than the name: a player may have inventories with identical names, and a "by name"
    /// edit then lands in the wrong inventory (B3).
    /// </summary>
    public static byte[] ReplaceInventoryBody(byte[] blob, int inventoryIndex, byte[] newBody)
    {
        if (inventoryIndex < 0) throw new ArgumentOutOfRangeException(nameof(inventoryIndex), "inventory index must be >= 0");
        var msgs = ChunkDataEditor.WalkTop(blob).Where(t => t.Field == 2).ToList();
        if (inventoryIndex >= msgs.Count)
            throw new ArgumentException($"inventory index {inventoryIndex} is out of range (found {msgs.Count} inventories)");
        var m = msgs[inventoryIndex];
        var msg = blob[m.PayloadStart..m.PayloadEnd];
        var bodyIdx = ChunkDataEditor.WalkTop(msg).Where(t => t.Field == 2).ToList();
        if (bodyIdx.Count == 0) throw new ArgumentException($"inventory #{inventoryIndex} has no ECX body");
        var newMsg = ChunkDataEditor.SplicePayloads(msg, new[] { (2, bodyIdx[0].Occurrence, newBody) });
        return ChunkDataEditor.SplicePayloads(blob, new[] { (2, m.Occurrence, newMsg) });
    }

    /// <summary>
    /// Replaces the ECX body of an inventory by name — only if that name occurs EXACTLY ONCE.
    /// With duplicate names it throws (rather than editing "the first one it finds");
    /// use the overload that takes an index for ambiguous cases.
    /// </summary>
    public static byte[] ReplaceInventoryBody(byte[] blob, string name, byte[] newBody)
    {
        var msgs = ChunkDataEditor.WalkTop(blob).Where(t => t.Field == 2).ToList();
        int match = -1, matches = 0;
        for (int i = 0; i < msgs.Count; i++)
        {
            var msg = blob[msgs[i].PayloadStart..msgs[i].PayloadEnd];
            var invName = ChunkDataEditor.WalkTop(msg)
                .Where(t => t.Field == 1 && t.PayloadEnd > t.PayloadStart)
                .Select(t => CleanAscii(msg[t.PayloadStart..t.PayloadEnd]))
                .FirstOrDefault();
            if (invName != name) continue;
            matches++;
            if (match < 0) match = i;
        }
        if (match < 0) throw new ArgumentException($"inventory '{name}' not found");
        if (matches > 1) throw new ArgumentException($"inventory name '{name}' is ambiguous ({matches} matches) — use the index overload");
        return ReplaceInventoryBody(blob, match, newBody);
    }

    private static string CleanAscii(byte[] b)
    {
        var sb = new StringBuilder();
        foreach (byte c in b) if (c >= 32 && c < 127) sb.Append((char)c);
        return sb.ToString();
    }
}
