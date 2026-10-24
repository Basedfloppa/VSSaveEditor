namespace VSCore;

/// <summary>Where the item was found: in playerdata, in an entity (field 6) or in a block entity (field 8).</summary>
public enum InventoryOwnerKind
{
    Player,
    Entity,
    BlockEntity
}

/// <summary>
///     One item stack from an inventory tree. `Class` and `Id` are exactly what the game writes
///     (<c>ItemStack.ToBytes</c>); the code appears after resolution through the save's Block/Item registry.
///     Attributes are the scalar fields of the stack (`condition`, `temperature`, `type`, …), without nested
///     containers: those are parsed as separate inventories so a backpack is not passed off as an item.
/// </summary>
public sealed class ItemStackInfo
{
    /// <summary>0 — block, 1 — item (EnumItemClass in the game).</summary>
    public int Class;

    public int Id;
    public int Count;

    /// <summary>Code from the registry; empty if the registry does not know the id (then `item:<id>` is shown).</summary>
    public string Code = "";

    /// <summary>Slot key inside the `slots` tree (`0`, `1`, `slot-4`).</summary>
    public string Slot = "";

    public List<KeyValuePair<string, string>> Attributes { get; } = new();

    public bool IsBlock => Class == 0;

    public string FallbackCode => (IsBlock ? "block:" : "item:") + Id;

    /// <summary>The code that is displayed and searched; the fallback keeps items that have no registry.</summary>
    public string DisplayCode => Code.Length > 0 ? Code : FallbackCode;

    /// <summary>Attributes on one line — for the search and for the preview in the card.</summary>
    public string AttributeText => string.Join(" ", Attributes.Select(a => a.Key + "=" + a.Value));

    public string AttributeSummary => string.Join(" · ", Attributes.Take(8).Select(a => a.Key + "=" + a.Value));
}

/// <summary>
///     One inventory inside a record: `inventory` on a chest, `backpack` on a horse, `character-…`
///     in playerdata. It contains the empty slots too: `SlotCount` is `qslots`, while `Items` holds only
///     the occupied slots.
/// </summary>
public sealed class InventoryInfo
{
    public InventoryOwnerKind OwnerKind;
    public string OwnerLabel = "";
    public string OwnerCode = "";
    public long Position = -1;
    public int Field;
    public int Occurrence = -1;
    public long PlayerId = -1;
    public string? PlayerUid;
    public int ChunkX, ChunkY, ChunkZ;

    /// <summary>Whether the owner has a chunk: playerdata has no position, and `chunk 0:0` would be a lie.</summary>
    public bool HasChunk = true;

    /// <summary>Exact block entity position (`posx/posy/posz` fields) if it is present in the tree.</summary>
    public int? X, Y, Z;

    /// <summary>Container block code (`chest`, `barrel`, `groundstorage`) if it is recorded in the tree.</summary>
    public string ContainerCode = "";

    /// <summary>Inventory name / path to it: `inventory`, `backpack`, `character-…`.</summary>
    public string Name = "";

    public int SlotCount;
    public List<ItemStackInfo> Items { get; } = new();

    /// <summary>Stable inventory address for the `key:` filter and for navigation from the cards.</summary>
    public string Key => OwnerKind switch
    {
        InventoryOwnerKind.Player => $"p:{PlayerId}:{Name}",
        InventoryOwnerKind.Entity => $"e:{Position}:{Occurrence}:{Name}",
        _ => $"b:{Position}:{Occurrence}:{Name}"
    };

    public string OwnerBadge => OwnerKind switch
    {
        InventoryOwnerKind.Player => "[player]",
        InventoryOwnerKind.Entity => "[entity]",
        _ => "[block]"
    };

    public string ContainerLabel => ContainerCode.Length > 0 ? $"{Name} ({ContainerCode})" : Name;

    public string ChunkText => HasChunk ? $"chunk {ChunkX}:{ChunkZ} y{ChunkY}" : "";

    public string PositionText => X is null || Y is null || Z is null ? "" : $"X:{X} Y:{Y} Z:{Z}";
}

/// <summary>Parsed contents of one inventory tree before it is bound to an owner.</summary>
public sealed class InventoryContents
{
    public string Name = "";
    public int SlotCount;
    public List<ItemStackInfo> Items { get; } = new();
}
