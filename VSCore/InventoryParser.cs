using System.Globalization;
using System.Text;
using VSCore.Format;

namespace VSCore;

/// <summary>
///     Parsing of inventories from the ECX trees the game writes
///     (<c>InventoryBase.SlotsToTreeAttributes</c> → <c>TreeAttribute.ToBytes</c>).
///
///     This is a separate layer from <see cref="ChunkDataParser" />: that one parses flat fields
///     `[type][key][value]`, while an inventory is a nested tree (`tree 6`) where `slots` is a tree
///     of itemstack attributes (`item 7`). Previously such bytes landed in opaque and the contents of
///     chests/backpacks were invisible. Here inventory trees are parsed in full, but read-only:
///     item editing is not implemented yet, so the record bytes are not rebuilt.
///
///     Tree attributes: 1 int, 2 long, 3 double, 4 float, 5 string, 6 tree, 7 itemstack,
///     8 byte[], 9 bool, 10 string[], 11 int[], 12 float[], 13 double[], 14 tree[],
///     15 long[], 16 bool[] (ids from the decompiled VintagestoryAPI).
/// </summary>
public static class InventoryParser
{
    /// <summary>Quick check: without the `slots` string there can be no inventory in the blob.
    /// Saves parsing the tree for most block entities (lights, flowers, mechanisms).</summary>
    public static bool MightContainInventory(ReadOnlySpan<byte> payload)
        => payload.IndexOf("slots"u8) >= 0;

    /// <summary>Inventories of an entity (field 6): `backpack`, `wearablesInv`, `harvestableInv`, …</summary>
    public static List<InventoryInfo> ParseEntity(byte[] payload, EntityInfo owner, BlockRegistry? blocks, ItemRegistry? items)
    {
        var parsed = new List<InventoryContents>();
        CollectFromPayload(payload, parsed);

        var label = owner.CustomName is { Length: > 0 } name
            ? name
            : owner.Species is { Length: > 0 } species ? species : owner.CodePath;
        ResolveCodes(parsed, blocks, items);
        return Attach(parsed, new OwnerContext(InventoryOwnerKind.Entity, label, owner.Species ?? "",
            owner.Position, 6, owner.Occurrence, -1, null, owner.ChunkX, owner.ChunkY, owner.ChunkZ, null, null, null, ""));
    }

    /// <summary>Inventories of a block entity (field 8): chests, barrels, crates, firepits, shelves.</summary>
    public static List<InventoryInfo> ParseBlockEntity(byte[] payload, BlockEntityInfo owner, BlockRegistry? blocks,
        ItemRegistry? items)
    {
        var parsed = new List<InventoryContents>();
        CollectFromPayload(payload, parsed);

        // posx/posy/posz and blockCode live in the root tree: without them the item card would know
        // only the chunk, not the exact position of the chest. If the tree was not parsed in full,
        // the inventories are still kept — the key walk found them.
        int? x = null, y = null, z = null;
        var blockCode = "";
        try
        {
            int i = 0;
            _ = ReadString(payload, ref i); // header: the block entity type
            var root = ReadTree(payload, ref i, 0);
            x = AsInt(root, "posx");
            y = AsInt(root, "posy");
            z = AsInt(root, "posz");
            blockCode = AsString(root, "blockCode") ?? "";
        }
        catch
        {
            // the coordinates are optional
        }

        var label = owner.Type is { Length: > 0 } type ? type : "block entity";
        ResolveCodes(parsed, blocks, items);
        return Attach(parsed, new OwnerContext(InventoryOwnerKind.BlockEntity, label, owner.CodeSummary,
            owner.Position, 8, owner.Occurrence, -1, null, owner.ChunkX, owner.ChunkY, owner.ChunkZ, x, y, z, blockCode));
    }

    /// <summary>Inventories of one playerdata message. Returns nested ones too (a backpack inside a backpack).</summary>
    public static List<InventoryContents> ParsePlayerInventory(byte[] body, string name, BlockRegistry? blocks,
        ItemRegistry? items)
    {
        var parsed = new List<InventoryContents>();
        try
        {
            // The root of the playerdata body is the inventory itself (`qslots` + `slots`), with no key wrapper.
            int i = 0;
            var root = ReadTree(body, ref i, 0);
            AddInventory(root, name, parsed);
        }
        catch
        {
            // the body may be empty (`00`) — that is normal for creative/ground
        }

        // Nested contents/backpack inside items are found by the same key walk.
        CollectFromPayload(body, parsed);
        ResolveCodes(parsed, blocks, items);
        return parsed;
    }

    /// <summary>
    ///     Finds inventory trees by key without parsing the root in full. This matters: entities have
    ///     behaviour fields in WatchedAttributes with their own binary format, and a full parse
    ///     failed on them before it reached `backpack` (a horse with a backpack stayed empty).
    ///     Every inventory in the tree is a `tree 6` attribute with a name and a subtree, so walking
    ///     the `06 &lt;LP key&gt;` sequence finds them regardless of their neighbours.
    /// </summary>
    private static void CollectFromPayload(byte[] payload, List<InventoryContents> sink)
    {
        for (int i = 0; i + 2 < payload.Length; i++)
        {
            if (payload[i] != 6) continue; // TreeAttribute.GetAttributeId()
            int p = i + 1;
            if (!TryReadString(payload, ref p, out var key)) continue;
            if (key.Length is < 2 or > 48 || !IsPrintableKey(key)) continue;
            if (key is "slots" or "qslots") continue; // parts of an inventory, not separate containers

            try
            {
                var tree = ReadTree(payload, ref p, 0);
                AddInventory(tree, key, sink);
            }
            catch
            {
                // the key may belong to a non-inventory tree; skip it and keep looking
            }
        }
    }

    private static bool IsPrintableKey(string key)
    {
        foreach (var c in key)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')) return false;
        return true;
    }

    private static void AddInventory(Dictionary<string, object?> tree, string name, List<InventoryContents> sink)
    {
        if (!IsInventory(tree, out var slots, out var slotCount))
        {
            // A backpack's `contents` is just a tree of itemstack attributes without qslots.
            if (IsItemMap(tree, out var direct))
            {
                var directInv = new InventoryContents { Name = name, SlotCount = direct };
                foreach (var (slot, value) in tree)
                {
                    if (value is not ItemStackInfo stack) continue;
                    stack.Slot = slot;
                    directInv.Items.Add(stack);
                }
                if (directInv.Items.Count > 0) sink.Add(directInv);
            }

            return;
        }

        var contents = new InventoryContents { Name = name, SlotCount = slotCount };
        foreach (var (slot, value) in slots)
        {
            if (value is not ItemStackInfo stack) continue;
            stack.Slot = slot;
            contents.Items.Add(stack);
        }

        sink.Add(contents);
    }

    private static bool IsInventory(Dictionary<string, object?> tree, out Dictionary<string, object?> slots, out int slotCount)
    {
        slots = null!;
        slotCount = 0;
        if (!tree.TryGetValue("slots", out var raw) || raw is not Dictionary<string, object?> map) return false;
        if (tree.TryGetValue("qslots", out var q) && q is long ql)
            slotCount = ql is < 0 or > 100_000 ? map.Count : (int)ql;
        else slotCount = map.Count;
        slots = map;
        return true;
    }

    private static bool IsItemMap(Dictionary<string, object?> tree, out int count)
    {
        count = 0;
        foreach (var value in tree.Values)
        {
            if (value is ItemStackInfo) count++;
            else if (value is not null) return false;
        }

        return count > 0;
    }

    /// <summary>Player name from the `EntityPlayer` embedded in playerdata (nametag).
    /// We do not parse this blob's full tree: after WatchedAttributes it holds behaviour
    /// data in its own format (not tree attributes), and parsing "to the end" would fail
    /// before the name is found. We look for exactly the length-prefixed pair `nametag` → `name`.</summary>
    public static string? ParsePlayerEntityName(byte[] payload)
    {
        for (int at = IndexOfPrefixed(payload, "nametag", 0); at >= 0; at = IndexOfPrefixed(payload, "nametag", at + 1))
        {
            var name = IndexOfPrefixed(payload, "name", at + 1);
            if (name >= 0 && name - at <= 64 && ReadPrefixedText(payload, name + 5, 64) is { } value)
                return value;
        }

        for (int at = IndexOfPrefixed(payload, "name", 0); at >= 0; at = IndexOfPrefixed(payload, "name", at + 1))
            if (ReadPrefixedText(payload, at + 5, 64) is { } value)
                return value;
        return null;
    }

    private static int IndexOfPrefixed(byte[] b, string s, int from)
    {
        if (s.Length is 0 or > 127) return -1;
        for (int i = Math.Max(0, from); i + 1 + s.Length <= b.Length; i++)
        {
            if (b[i] != s.Length) continue;
            int j = 0;
            while (j < s.Length && b[i + 1 + j] == s[j]) j++;
            if (j == s.Length) return i;
        }

        return -1;
    }

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
            return new UTF8Encoding(false, true).GetString(b, at + 1, len);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void ResolveCodes(List<InventoryContents> inventories, BlockRegistry? blocks, ItemRegistry? items)
    {
        foreach (var inv in inventories)
            foreach (var stack in inv.Items)
                // Old/server saves may have no ItemIDs section at all. Then the only
                // available registry is BlockIDs: for block items (and some
                // modded ones) the code is found there, and that beats `item:<id>`.
                stack.Code = stack.IsBlock
                    ? blocks?.CodeOf(stack.Id) ?? items?.CodeOf(stack.Id) ?? ""
                    : items?.CodeOf(stack.Id) ?? blocks?.CodeOf(stack.Id) ?? "";
    }

    private sealed record OwnerContext(InventoryOwnerKind Kind, string Label, string Code, long Position, int Field,
        int Occurrence, long PlayerId, string? PlayerUid, int ChunkX, int ChunkY, int ChunkZ, int? X, int? Y, int? Z,
        string ContainerCode);

    private static List<InventoryInfo> Attach(List<InventoryContents> parsed, OwnerContext owner)
    {
        var result = new List<InventoryInfo>(parsed.Count);
        foreach (var p in parsed)
        {
            var inv = new InventoryInfo
            {
                OwnerKind = owner.Kind,
                OwnerLabel = owner.Label,
                OwnerCode = owner.Code,
                Position = owner.Position,
                Field = owner.Field,
                Occurrence = owner.Occurrence,
                PlayerId = owner.PlayerId,
                PlayerUid = owner.PlayerUid,
                ChunkX = owner.ChunkX,
                ChunkY = owner.ChunkY,
                ChunkZ = owner.ChunkZ,
                X = owner.X,
                Y = owner.Y,
                Z = owner.Z,
                ContainerCode = owner.ContainerCode,
                Name = p.Name,
                SlotCount = p.SlotCount
            };
            inv.Items.AddRange(p.Items);
            result.Add(inv);
        }

        return result;
    }

    // ------------------------- Attribute tree -------------------------

    private static Dictionary<string, object?> ReadTree(byte[] b, ref int i, int depth)
    {
        if (depth > 30) throw new FormatException("attribute tree is too deep");
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (true)
        {
            if (i >= b.Length) throw new FormatException("truncated attribute tree");
            byte id = b[i++];
            if (id == 0) return result;
            var key = ReadString(b, ref i);
            result[key] = ReadAttribute(b, ref i, id, depth);
        }
    }

    private static object? ReadAttribute(byte[] b, ref int i, byte id, int depth)
    {
        switch (id)
        {
            case 1: return (long)ReadInt32(b, ref i);
            case 2: return ReadInt64(b, ref i);
            case 3: return ReadDouble(b, ref i);
            case 4: return ReadSingle(b, ref i);
            case 5: return ReadString(b, ref i);
            case 6: return ReadTree(b, ref i, depth + 1);
            case 7:
            {
                bool isNull = ReadByte(b, ref i) != 0;
                if (isNull) return null;
                var stack = new ItemStackInfo
                {
                    Class = ReadInt32(b, ref i),
                    Id = ReadInt32(b, ref i),
                    Count = ReadInt32(b, ref i)
                };
                var attributes = ReadTree(b, ref i, depth + 1);
                FlattenAttributes(stack, "", attributes);
                return stack;
            }
            case 8:
            {
                int len = ReadInt32(b, ref i);
                if (len < 0 || i + len > b.Length) throw new FormatException("bad byte array");
                i += len;
                return null;
            }
            case 9: return ReadByte(b, ref i) != 0;
            case 10:
            {
                int count = ReadInt32(b, ref i);
                if (count < 0 || count > 100_000) throw new FormatException("bad string array");
                var arr = new string[count];
                for (int k = 0; k < count; k++) arr[k] = ReadString(b, ref i);
                return arr;
            }
            case 11:
            {
                int count = ReadInt32(b, ref i);
                Skip(b, ref i, (long)count * 4);
                return null;
            }
            case 12:
            {
                int count = ReadInt32(b, ref i);
                Skip(b, ref i, (long)count * 4);
                return null;
            }
            case 13:
            {
                int count = ReadInt32(b, ref i);
                Skip(b, ref i, (long)count * 8);
                return null;
            }
            case 14:
            {
                int count = ReadInt32(b, ref i);
                var arr = new Dictionary<string, object?>[count];
                for (int k = 0; k < count; k++) arr[k] = ReadTree(b, ref i, depth + 1);
                return arr;
            }
            case 15:
            {
                int count = ReadInt32(b, ref i);
                Skip(b, ref i, (long)count * 8);
                return null;
            }
            case 16:
            {
                int count = ReadInt32(b, ref i);
                Skip(b, ref i, count);
                return null;
            }
            default:
                throw new FormatException($"unknown attribute id {id}");
        }
    }

    /// <summary>Scalar attributes of the stack — what is visible on the card (`condition`, `type`, …).
    /// Nested trees are flattened with a dotted path; itemstack values are skipped —
    /// they are either parts of a tool or a separate container collected by its own pass.</summary>
    private static void FlattenAttributes(ItemStackInfo stack, string prefix, Dictionary<string, object?>? tree)
    {
        if (tree is null) return;
        foreach (var (key, value) in tree)
        {
            var name = prefix.Length == 0 ? key : prefix + "." + key;
            switch (value)
            {
                case null:
                case ItemStackInfo:
                    continue;
                case Dictionary<string, object?> child:
                    FlattenAttributes(stack, name, child);
                    continue;
                case string s:
                    stack.Attributes.Add(new KeyValuePair<string, string>(name, s));
                    continue;
                case long l:
                    stack.Attributes.Add(new KeyValuePair<string, string>(name, l.ToString(CultureInfo.InvariantCulture)));
                    continue;
                case double d:
                    stack.Attributes.Add(new KeyValuePair<string, string>(name, d.ToString("0.###", CultureInfo.InvariantCulture)));
                    continue;
                case float f:
                    stack.Attributes.Add(new KeyValuePair<string, string>(name, f.ToString("0.###", CultureInfo.InvariantCulture)));
                    continue;
                case bool b:
                    stack.Attributes.Add(new KeyValuePair<string, string>(name, b ? "true" : "false"));
                    continue;
                case string[] sa:
                    stack.Attributes.Add(new KeyValuePair<string, string>(name, string.Join(",", sa)));
                    continue;
            }
        }
    }

    private static void Skip(byte[] b, ref int i, long count)
    {
        if (count < 0 || i + count > b.Length) throw new FormatException("truncated attribute");
        i += (int)count;
    }

    private static byte ReadByte(byte[] b, ref int i)
    {
        if (i >= b.Length) throw new FormatException("truncated attribute");
        return b[i++];
    }

    private static int ReadInt32(byte[] b, ref int i)
    {
        if (i + 4 > b.Length) throw new FormatException("truncated int32");
        int v = BitConverter.ToInt32(b, i);
        i += 4;
        return v;
    }

    private static long ReadInt64(byte[] b, ref int i)
    {
        if (i + 8 > b.Length) throw new FormatException("truncated int64");
        long v = BitConverter.ToInt64(b, i);
        i += 8;
        return v;
    }

    private static double ReadDouble(byte[] b, ref int i)
    {
        if (i + 8 > b.Length) throw new FormatException("truncated double");
        double v = BitConverter.ToDouble(b, i);
        i += 8;
        return v;
    }

    private static float ReadSingle(byte[] b, ref int i)
    {
        if (i + 4 > b.Length) throw new FormatException("truncated float");
        float v = BitConverter.ToSingle(b, i);
        i += 4;
        return v;
    }

    private static string ReadString(byte[] b, ref int i)
    {
        var (len, next) = PbReader.ReadVarint(b, i);
        i = next;
        if (!PbReader.LengthFits(len, i, b.Length)) throw new FormatException("bad string length");
        var s = Encoding.UTF8.GetString(b, i, (int)len);
        i += (int)len;
        return s;
    }

    private static bool TryReadString(byte[] b, ref int i, out string value)
    {
        value = "";
        int save = i;
        try
        {
            value = ReadString(b, ref i);
            return true;
        }
        catch
        {
            i = save;
            return false;
        }
    }

    private static bool LooksLikeVersion(string s)
        => s.Length is > 0 and < 24 && char.IsAsciiDigit(s[0]) && s.All(c => char.IsAsciiDigit(c) || c == '.');

    private static int? AsInt(Dictionary<string, object?> tree, string key)
        => tree.TryGetValue(key, out var v) && v is long l && l is >= int.MinValue and <= int.MaxValue ? (int)l : null;

    private static string? AsString(Dictionary<string, object?> tree, string key)
        => tree.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;
}
