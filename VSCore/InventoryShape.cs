namespace VSCore;

/// <summary>
///     The vocabulary that makes a TreeAttribute an inventory, and the bound on <c>qslots</c>.
///     <para>
///         Recognition is written twice on purpose: <see cref="InventoryParser" /> looks at the decoded, JSON-like tree
///         of playerdata, <see cref="EntityDataDocument" /> at the ECX fields of a chunk record. Two models, two walks —
///         but one vocabulary, so a name or the limit cannot drift between them.
///     </para>
///     <para>
///         The two readings are pinned to the same answer by
///         <c>InventoryParserTests.The_document_and_the_parser_see_the_same_inventory</c>.
///     </para>
/// </summary>
public static class InventoryShape
{
    /// <summary>The slot map of an inventory: a tree whose children are the item stacks.</summary>
    public const string Slots = "slots";

    /// <summary>The slot count. Absent or insane means "count the slots map instead".</summary>
    public const string SlotCount = "qslots";

    /// <summary>A trader's offers: one tree child per offer, `stack`/`price`/`stock` and the restock rules.</summary>
    public const string TradeItems = "tradeItems";

    /// <summary>Above this a `qslots` is a corrupt value, not a slot count (a real inventory has 1..200).</summary>
    public const int MaxSlotCount = 100_000;

    /// <summary>Whether a `qslots` value can be trusted as a slot count.</summary>
    public static bool IsSaneSlotCount(long count) => count is >= 0 and <= MaxSlotCount;

    /// <summary>The cheapest "does this payload mention an inventory at all" gate: a byte scan before any parse.</summary>
    public static ReadOnlySpan<byte> SlotsUtf8 => "slots"u8;
}
