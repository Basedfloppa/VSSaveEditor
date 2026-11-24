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
    // Top-level fields of a playerdata row. Reader and writer share these constants on purpose: an edit
    // addresses an inventory by its INDEX, that index is "how many field 2 came before it", and two different
    // rules for counting it (a message parser cap here, a walk there) would let an edit land in another one.
    private const int UidField = 1;
    private const int InventoryField = 2;
    private const int EntityField = 3;

    public static PlayerData Parse(byte[] blob, long playerId, BlockRegistry? blocks = null, ItemRegistry? items = null)
    {
        var pd = new PlayerData { RawBytes = blob, PlayerId = playerId };
        foreach (var f in ChunkDataEditor.WalkTop(blob))
        {
            if (f.WireType != 2) continue;
            var payload = blob[f.PayloadStart..f.PayloadEnd];
            if (f.Field == UidField)
            {
                pd.Uid ??= CleanAscii(payload);
                continue;
            }

            if (f.Field == InventoryField) pd.Inventories.Add(ParseInventory(payload, blocks, items));
            else if (f.Field == EntityField && pd.CustomName is null)
            {
                // playerdata keeps the EntityPlayer itself next to the inventories. All we need from it
                // is the name: the position in chunks is looked up by playerUID (see PlayersTabService).
                try
                {
                    var entity = InventoryParser.ParsePlayerEntityName(payload);
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

    /// <summary>
    ///     What an inventory message consists of: field 1 is its name, field 2 its ECX body. ONE definition for the
    ///     reader and the writer — with two, an edit could replace a different field than the one that was shown.
    ///     The body is the FIRST field 2 of the message, which is where the writer replaces it.
    /// </summary>
    private static (string? Name, ChunkDataEditor.ChunkTopField? Body) InventoryParts(byte[] msg)
    {
        string? name = null;
        ChunkDataEditor.ChunkTopField? body = null;
        foreach (var f in ChunkDataEditor.WalkTop(msg))
        {
            if (f.WireType != 2) continue;
            if (f.Field == 1) name ??= CleanAscii(msg[f.PayloadStart..f.PayloadEnd]);
            else if (f.Field == 2) body ??= f;
        }

        return (name, body);
    }

    private static PlayerInventory ParseInventory(byte[] msg, BlockRegistry? blocks, ItemRegistry? items)
    {
        var (name, bodyField) = InventoryParts(msg);
        var inv = new PlayerInventory { Name = name };
        if (bodyField is null || bodyField.PayloadEnd <= bodyField.PayloadStart) return inv;
        var body = msg[bodyField.PayloadStart..bodyField.PayloadEnd];
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

        return inv;
    }

    /// <summary>The top-level messages of a row that are inventories — the index space of every edit of one.</summary>
    public static List<ChunkDataEditor.ChunkTopField> InventoryFields(byte[] blob)
        => ChunkDataEditor.WalkTop(blob).Where(f => f.Field == InventoryField && f.WireType == 2).ToList();

    /// <summary>
    /// Replaces the ECX body of an inventory by its INDEX in <see cref="PlayerData.Inventories"/> (nested splice).
    /// The index is safer than the name: a player may have inventories with identical names, and a "by name"
    /// edit then lands in the wrong inventory (B3).
    /// </summary>
    public static byte[] ReplaceInventoryBody(byte[] blob, int inventoryIndex, byte[] newBody)
    {
        if (inventoryIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(inventoryIndex), "inventory index must be >= 0");
        var found = InventoryFields(blob).Count;
        if (inventoryIndex >= found)
            throw new ArgumentException($"inventory index {inventoryIndex} is out of range (found {found} inventories)");
        // one splice path for everything: Rewrite reads the index in the same walk and replaces the same body
        return Rewrite(blob, null, new Dictionary<int, byte[]> { [inventoryIndex] = newBody });
    }

    /// <summary>
    /// Replaces the ECX body of an inventory by name — only if that name occurs EXACTLY ONCE.
    /// With duplicate names it throws (rather than editing "the first one it finds");
    /// use the overload that takes an index for ambiguous cases.
    /// </summary>
    public static byte[] ReplaceInventoryBody(byte[] blob, string name, byte[] newBody)
    {
        var msgs = InventoryFields(blob);
        int match = -1, matches = 0;
        for (int i = 0; i < msgs.Count; i++)
        {
            if (InventoryParts(blob[msgs[i].PayloadStart..msgs[i].PayloadEnd]).Name != name) continue;
            matches++;
            if (match < 0) match = i;
        }
        if (match < 0) throw new ArgumentException($"inventory '{name}' not found");
        if (matches > 1) throw new ArgumentException($"inventory name '{name}' is ambiguous ({matches} matches) — use the index overload");
        return ReplaceInventoryBody(blob, match, newBody);
    }

    /// <summary>
    ///     The `EntityPlayer` record kept in playerdata (field 3): the character itself — health, hunger,
    ///     satiety, temperature, stats, skin — as an editable ECX tree. null when the row has no such field.
    /// </summary>
    public static EntityRecord? ParseEntityRecord(byte[] blob)
    {
        foreach (var f in ChunkDataEditor.WalkTop(blob))
        {
            if (f.Field != EntityField || f.WireType != 2 || f.PayloadEnd <= f.PayloadStart) continue;
            try
            {
                return ChunkDataParser.ParseEntity(blob[f.PayloadStart..f.PayloadEnd]);
            }
            catch
            {
                return null; // a broken character blob must not hide the inventories
            }
        }

        return null;
    }

    /// <summary>
    ///     Rebuilds the playerdata blob with a new character body and/or new inventory bodies, in ONE pass:
    ///     a sequence of <see cref="ReplaceInventoryBody" /> calls would invalidate the byte ranges of the
    ///     following ones as soon as a body changes length. Removed inventories disappear from the repeated
    ///     field; every other top-level field is copied byte for byte.
    /// </summary>
    /// <param name="entityBody">New payload of field 3 (the serialised <see cref="EntityRecord" />), or null to keep the old one.</param>
    /// <param name="inventoryBodies">Inventory INDEX → new ECX body (the body, not the whole inventory message).</param>
    /// <param name="removedInventories">Inventory indexes to drop (an inventory counts as a whole repeated field 2).</param>
    public static byte[] Rewrite(byte[] blob, byte[]? entityBody = null,
        IReadOnlyDictionary<int, byte[]>? inventoryBodies = null, IReadOnlySet<int>? removedInventories = null)
    {
        var edits = new List<ByteSplice.Edit>();
        int invIndex = -1;
        bool entityWritten = false;
        foreach (var f in ChunkDataEditor.WalkTop(blob))
        {
            if (f.WireType != 2) continue; // the same rule the reader applies, so the indexes cannot drift
            if (f.Field == InventoryField)
            {
                invIndex++;
                if (removedInventories is not null && removedInventories.Contains(invIndex))
                {
                    edits.Add(ByteSplice.Cut(f.Start, f.PayloadEnd));
                    continue;
                }

                if (inventoryBodies is not null && inventoryBodies.TryGetValue(invIndex, out var body))
                {
                    var msg = blob[f.PayloadStart..f.PayloadEnd];
                    var (_, bodyField) = InventoryParts(msg);
                    if (bodyField is null) throw new ArgumentException($"inventory #{invIndex} has no ECX body");
                    // the outer repeated field has to be written again: the message changed length
                    var newMsg = ReplacePayload(msg, bodyField, body);
                    edits.Add(ByteSplice.Replace(f.Start, f.PayloadEnd, PbWriter.MessageBytes(f.Field, newMsg)));
                }

                continue;
            }

            if (f.Field == EntityField && entityBody is not null && !entityWritten)
            {
                entityWritten = true;
                edits.Add(ByteSplice.Replace(f.Start, f.PayloadEnd, PbWriter.MessageBytes(EntityField, entityBody)));
            }
        }

        // a row without the character: the field is appended, as the game would have written it
        if (entityBody is not null && !entityWritten)
            edits.Add(new ByteSplice.Edit(blob.Length, blob.Length, PbWriter.MessageBytes(EntityField, entityBody)));
        return ByteSplice.Apply(blob, edits);
    }

    /// <summary>Replaces one payload INSIDE an inventory message: header fields stay untouched.</summary>
    private static byte[] ReplacePayload(byte[] msg, ChunkDataEditor.ChunkTopField target, byte[] payload)
        => ByteSplice.Apply(msg, new[]
        {
            ByteSplice.Replace(target.Start, target.PayloadEnd, PbWriter.MessageBytes(target.Field, payload))
        });

    private static string CleanAscii(byte[] b)
    {
        var sb = new StringBuilder();
        foreach (byte c in b) if (c >= 32 && c < 127) sb.Append((char)c);
        return sb.ToString();
    }
}
