namespace VSCore;

public enum EntityDataKind
{
    /// <summary>The record itself: every ECX field of the entity (name, health, traits, spawn point…).</summary>
    Record,

    /// <summary>One inventory found in the record (`traderInventory`, `backpack`, `seraphinventory`, `inventory`).</summary>
    Inventory,

    /// <summary>The `tradeItems` tree of a trader: one child per offer (`stack`/`price`/`stock`/restock rules).</summary>
    TradeOffers
}

/// <summary>One editable piece of an entity's data: the record, one of its inventories or its trade offers.</summary>
public sealed class EntityDataBlock
{
    public required EntityDataKind Kind { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";

    /// <summary>The tree this block edits: the inventory tree or `tradeItems`. Null for the record block,
    /// which edits the root fields.</summary>
    public ChunkField? Node { get; init; }

    /// <summary>The slots tree of an inventory (`slots`), when the inventory has one.</summary>
    public ChunkField? Slots { get; init; }

    /// <summary>The `tradeItems` tree of the inventory — deleting a slot has to remove its offer too,
    /// otherwise the offer keeps selling an item that is no longer in the inventory.</summary>
    public ChunkField? PairedOffers { get; init; }

    /// <summary>The whole block is deleted: the inventory or the offer list disappears from the record.
    /// The flag IS the node's own flag: a removal the window knows about but the tree does not counts as a
    /// change and then writes the record back unchanged — "nothing to apply" after the block said "deleted".</summary>
    public bool Removed
    {
        get => Node?.Removed ?? false;
        set
        {
            if (Node is not null) Node.Removed = value;
        }
    }

    public List<ChunkField>? FieldRoots => Node?.Children;

    public bool HasChanges => Node is not null && ChunkDataEditor.AnyChange(Node);
}

/// <summary>One trade offer of a trader, read for the window: what it sells, for how much, how many are left.</summary>
public sealed record TradeOfferInfo(string Key, string ItemCode, int Count, double Price, long Stock, bool HasSlot)
{
    public string Text => $"{Key}: {ItemCode} ×{Count} — price {Price:0.##}, stock {Stock}";
}

/// <summary>
///     What an apply would change, counted in ONE walk over the record. The window recounts on every keystroke,
///     so the walk has to be shared: separate passes over the same tree start disagreeing about a node that is
///     both edited and deleted — the player window had to be fixed for exactly that.
/// </summary>
public readonly record struct EntityDataCounts(
    int FieldsEdited,
    int NodesRemoved,
    int OffersRemoved,
    int ItemsRemoved,
    int BlocksRemoved,
    int BytesFreed)
{
    public bool Changed => FieldsEdited > 0 || NodesRemoved > 0 || OffersRemoved > 0 || ItemsRemoved > 0
                           || BlocksRemoved > 0;

    public string Label => EditCountsLabel.Of(
        (FieldsEdited, "value(s) changed"),
        (OffersRemoved, "trade offer(s) deleted"),
        (ItemsRemoved, "item(s) deleted"),
        (NodesRemoved, "field(s) deleted"),
        (BlocksRemoved, "block(s) deleted"));
}

/// <summary>What an entity-data edit produces: the rebuilt record payload plus the counted changes.</summary>
public sealed record EntityDataBuild(byte[]? Payload, EntityDataCounts Counts)
{
    public bool Changed => Payload is not null;
    public string Summary => Counts.Label;
}

/// <summary>
///     The data of ONE entity (a creature — field 6 — or a block entity — field 8) as one editable document:
///     the record's own fields, every inventory found inside it, and a trader's trade offers. Same shape as
///     <see cref="PlayerDataDocument" />: edits are made on the parsed ECX tree and materialise only in
///     <see cref="Build" />, which serialises the record once. Nothing here touches a file — the result becomes
///     a pending edit of the session, like every other edit.
///
///     Two invariants make that safe:
///      - the payload round-trips byte for byte through the parser (checked at load; 68 753 of 68 753 entity
///        records of a real 12-GB save pass), so a record nobody edited is written back unchanged;
///      - an offer and its slot are one item: the game keeps `tradeItems.k` and `slots.k` in step (6 308 of
///        6 308 offers in that save have a matching slot with the same item), so removing one removes both.
/// </summary>
public sealed class EntityDataDocument
{
    private readonly HashSet<ChunkField> _offerNodes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ChunkField> _slotNodes = new(ReferenceEqualityComparer.Instance);

    /// <summary>Inventories and offer lists: a removed node from this set is a deleted block, not a deleted field.</summary>
    private readonly HashSet<ChunkField> _blockNodes = new(ReferenceEqualityComparer.Instance);

    private BlockRegistry? _blocks;
    private ItemRegistry? _items;

    private EntityDataDocument(byte[] payload, int field, long position, int occurrence, string label,
        EntityRecord? entity, BlockEntityRecord? blockEntity, bool lossless)
    {
        Raw = payload;
        Field = field;
        Position = position;
        Occurrence = occurrence;
        Label = label;
        Entity = entity;
        BlockEntity = blockEntity;
        Lossless = lossless;
    }

    public byte[] Raw { get; }
    public int Field { get; }
    public long Position { get; }
    public int Occurrence { get; }
    public string Label { get; }

    public EntityRecord? Entity { get; }
    public BlockEntityRecord? BlockEntity { get; }

    public bool IsEntity => Field == 6;

    /// <summary>The payload is reproduced byte for byte from the parsed tree. When false nothing may be edited:
    /// a rebuild would silently change bytes the parser only guessed at.</summary>
    public bool Lossless { get; }

    public List<EntityDataBlock> Blocks { get; } = new();

    /// <summary>Trade offers of a trader (empty for everything else) — the table the window lists first.</summary>
    public List<TradeOfferInfo> Offers { get; } = new();

    public List<ChunkField> RootFields => Entity?.RootFields ?? BlockEntity?.RootFields ?? new List<ChunkField>();

    /// <summary>
    ///     Parses one record and collects its blocks. A malformed part is skipped instead of failing the window:
    ///     a broken inventory must not hide the trade offers or the record itself.
    /// </summary>
    public static EntityDataDocument Load(byte[] payload, int field, long position, int occurrence, string label,
        BlockRegistry? blocks = null, ItemRegistry? items = null)
    {
        EntityRecord? entity = null;
        BlockEntityRecord? blockEntity = null;
        try
        {
            if (field == 6) entity = ChunkDataParser.ParseEntity(payload);
            else blockEntity = ChunkDataParser.ParseBlockEntity(payload);
        }
        catch
        {
            // both stay null: the document then reports that there is nothing editable
        }

        var rebuilt = Serialize(entity, blockEntity);

        var doc = new EntityDataDocument(payload, field, position, occurrence, label, entity, blockEntity,
            rebuilt.AsSpan().SequenceEqual(payload));
        doc.CollectBlocks(blocks, items);
        return doc;
    }

    // ------------------------- blocks -------------------------

    private void CollectBlocks(BlockRegistry? blocks, ItemRegistry? items)
    {
        _blocks = blocks;
        _items = items;
        var roots = RootFields;
        // Nothing named at all means the parser only saw opaque bytes: there is no field to edit or delete,
        // and offering a "record" block of them would be a lie.
        if (roots.Count == 0 || roots.All(f => f.IsHeaderNoise)) return;

        Blocks.Add(new EntityDataBlock
        {
            Kind = EntityDataKind.Record,
            Title = $"Record — {(IsEntity ? Entity?.TypeName : BlockEntity?.TypeName) ?? "record"}",
            Subtitle = $"{ChunkDataParser.Flatten(roots).Count} field(s) of {Raw.Length} B"
                       + (Lossless ? "" : " · NOT lossless: read-only")
        });

        var inventories = new List<EntityDataBlock>();
        Walk(roots, node =>
        {
            if (node.Type != ChunkFieldType.Tree || node.Children.Count == 0) return;
            var slots = node.Children.FirstOrDefault(c => c.Key == InventoryShape.Slots && c.Type == ChunkFieldType.Tree);
            var qslots = node.Children.FirstOrDefault(c => c.Key == InventoryShape.SlotCount && c.Type == ChunkFieldType.Int32);
            // An item map (`contents` of a bag) has no `slots` tree at all: its children are the stacks. The slot
            // map of an inventory is not an inventory of its own, hence the name check.
            var itemMap = slots is null && qslots is null && node.Key != InventoryShape.Slots
                          && node.Children.All(IsFilledStack);
            if (slots is null && qslots is null && !itemMap) return;

            var offers = node.Children.FirstOrDefault(c => c.Key == InventoryShape.TradeItems
                                                          && c.Type == ChunkFieldType.Tree);
            var itemCount = slots is not null
                ? slots.Children.Count(IsFilledStack)
                : itemMap ? node.Children.Count : 0;

            _blockNodes.Add(node);
            inventories.Add(new EntityDataBlock
            {
                Kind = EntityDataKind.Inventory,
                Title = $"Inventory — {node.Key}",
                Subtitle = $"{(qslots is null ? itemCount : qslots.IntValue)} slot(s), {itemCount} item(s)"
                           + (offers is not null ? $", {offers.Children.Count} trade offer(s)" : ""),
                Node = node,
                Slots = slots,
                PairedOffers = offers
            });

            // Every filled slot is an item for the counters, whether or not a trade offer stands behind it:
            // money and gear slots have no offer, and a chest has no offers at all.
            foreach (var stack in inventories[^1].Slots is { } sl
                         ? sl.Children.Where(IsFilledStack)
                         : itemMap ? node.Children : Enumerable.Empty<ChunkField>())
                _slotNodes.Add(stack);
        });

        Blocks.AddRange(inventories);

        foreach (var inv in inventories)
        {
            if (inv.PairedOffers is not { } offers) continue;
            _blockNodes.Add(offers);
            Blocks.Add(new EntityDataBlock
            {
                Kind = EntityDataKind.TradeOffers,
                Title = $"Trade offers — {offers.Key}",
                Subtitle = $"{offers.Children.Count} offer(s) inside {inv.Node?.Key}",
                Node = offers,
                Slots = inv.Slots
            });
            ReadOffers(offers, inv.Slots, blocks, items);
        }
    }

    /// <summary>Reads the offer rows once, so a redraw does not re-walk the tree for every row.</summary>
    private void ReadOffers(ChunkField offers, ChunkField? slots, BlockRegistry? blocks, ItemRegistry? items)
    {
        var slotByKey = slots is null
            ? new Dictionary<string, ChunkField>(StringComparer.Ordinal)
            : slots.Children.ToDictionary(c => c.Key, c => c, StringComparer.Ordinal);

        foreach (var offer in offers.Children)
        {
            if (offer.Type != ChunkFieldType.Tree) continue;
            _offerNodes.Add(offer);

            var stack = offer.Children.FirstOrDefault(c => c.Key == "stack" && c.Type == ChunkFieldType.ItemStack);
            var price = offer.Children.FirstOrDefault(c => c.Key == "price");
            var stock = offer.Children.FirstOrDefault(c => c.Key == "stock");
            var hasSlot = slotByKey.TryGetValue(offer.Key, out var slot) && slot is not null && IsFilledStack(slot);
            if (hasSlot) _slotNodes.Add(slotByKey[offer.Key]);

            // A modded item may be missing from the registry: the fallback keeps the id visible instead of
            // pretending the offer sells nothing.
            var code = stack is null
                ? "(no stack)"
                : InventoryParser.CodeOf(stack.ItemClass, stack.ItemId, blocks, items) is { Length: > 0 } c
                    ? c
                    : (stack.ItemClass == 0 ? "block:" : "item:") + stack.ItemId;
            Offers.Add(new TradeOfferInfo(offer.Key, code, stack?.StackSize ?? 0,
                price?.AsDouble ?? 0, stock is null ? 0 : (long)stock.AsDouble, hasSlot));
        }
    }

    /// <summary>True when this node has a block of its own in the window (an inventory or an offer list), so the
    /// record view must not list its children a second time.</summary>
    public bool IsBlockNode(ChunkField f) => _blockNodes.Contains(f);

    /// <summary>A stack that holds something: an empty slot may be written as a null stack, and an id of 0 is air.</summary>
    public static bool IsFilledStack(ChunkField f)
        => f.Type == ChunkFieldType.ItemStack && !f.ItemStackIsNull && f.ItemId != 0;

    /// <summary>The filled slots of an inventory, in tree order — what the window lists as items.</summary>
    public static List<ChunkField> FilledSlots(EntityDataBlock inventory)
    {
        if (inventory.Slots is { } slots) return slots.Children.Where(IsFilledStack).ToList();
        if (inventory.Node is { Children.Count: > 0 } node && node.Children.All(IsFilledStack))
            return node.Children.ToList();
        return new List<ChunkField>();
    }

    // ------------------------- deletions -------------------------

    /// <summary>
    ///     Removes one trade offer: the offer and the slot that holds its item. The game keeps the two in step,
    ///     so cutting only one leaves `tradeItems.k` selling an item the inventory no longer has (and the reverse
    ///     leaves a stack behind that no offer uses). Returns what was done, in words.
    /// </summary>
    public string RemoveOffer(string key)
    {
        var block = Blocks.FirstOrDefault(b => b.Kind == EntityDataKind.TradeOffers);
        if (block?.Node is not { } tree) return "this record has no trade offers";

        var offer = tree.Children.FirstOrDefault(c => c.Key == key);
        if (offer is null) return $"offer '{key}' not found";

        var remove = !offer.Removed;
        offer.Removed = remove;

        var slotRemoved = false;
        if (block.Slots is { } slots)
        {
            var slot = slots.Children.FirstOrDefault(c => c.Key == key);
            if (slot is not null && IsFilledStack(slot))
            {
                slot.Removed = remove;
                slotRemoved = true;
            }
        }

        return (remove ? $"offer '{key}' deleted" : $"offer '{key}' restored")
               + (slotRemoved ? " together with its inventory slot" : "");
    }

    /// <summary>
    ///     Removes one item from an inventory. In a trader's inventory a slot is one half of an offer, so the
    ///     matching `tradeItems.k` goes with it — an item is not "just deleted" while an offer still sells it.
    ///     Money and gear slots have no offer and are removed on their own.
    /// </summary>
    public string RemoveItem(EntityDataBlock inventory, ChunkField slot)
    {
        var remove = !slot.Removed;
        slot.Removed = remove;

        var offerGone = false;
        if (inventory.PairedOffers is { } offers)
        {
            var offer = offers.Children.FirstOrDefault(c => c.Key == slot.Key);
            if (offer is not null)
            {
                offer.Removed = remove;
                offerGone = true;
            }
        }

        return (remove ? $"item in slot '{slot.Key}' deleted" : $"item in slot '{slot.Key}' restored")
               + (offerGone ? " together with its trade offer" : "");
    }

    /// <summary>
    ///     Sets what a slot holds: the item code (or "block:code"/"item:code") and the stack size. These three
    ///     numbers live in the ItemStack header, not in the field tree, so an inventory could be emptied but not
    ///     changed before. A trader's offer and its slot hold the same item in two places (`tradeItems.k.stack`
    ///     and `slots.k`, equal in 6 308 of 6 308 offers of a real save): both halves are written, or the offer
    ///     would sell something the inventory does not have.
    /// </summary>
    public bool TrySetItem(ChunkField stack, string codeText, int count, out string error)
    {
        error = "";
        if (stack.Type != ChunkFieldType.ItemStack)
        {
            error = "this row holds no item";
            return false;
        }

        if (count < 1)
        {
            error = "a stack needs at least one item — use ✕ to remove it";
            return false;
        }

        if (!TryResolveItem(codeText.Trim(), out var itemClass, out var id, out error)) return false;

        var partner = PartnerOf(stack);
        foreach (var s in partner is null ? new[] { stack } : new[] { stack, partner })
        {
            s.ItemClass = itemClass;
            s.ItemId = id;
            s.StackSize = count;
            s.Dirty = true;
        }

        return true;
    }

    /// <summary>Resolves what the user typed to (class, id): 0 — block registry, 1 — item registry.
    /// A bare code is looked up in both, because the stack header carries no hint of which one it is.</summary>
    private bool TryResolveItem(string text, out int itemClass, out int id, out string error)
    {
        itemClass = 0;
        id = 0;
        error = "";

        if (text.Length == 0)
        {
            error = "enter an item code";
            return false;
        }

        if (text.StartsWith("block:", StringComparison.Ordinal))
            return FromRegistry(text["block:".Length..], blockClass: true, out itemClass, out id, out error);

        if (text.StartsWith("item:", StringComparison.Ordinal))
            return FromRegistry(text["item:".Length..], blockClass: false, out itemClass, out id, out error);

        // A bare code: items first (the common case for a trader's goods), then blocks
        if (FromRegistry(text, blockClass: false, out itemClass, out id, out error)) return true;
        if (FromRegistry(text, blockClass: true, out itemClass, out id, out _)) return true;

        error = $"no item or block with the code '{text}' in this save";
        return false;
    }

    private bool FromRegistry(string code, bool blockClass, out int itemClass, out int id, out string error)
    {
        itemClass = blockClass ? 0 : 1;
        id = 0;
        error = "";

        // An explicit number needs no registry: "item:123" means id 123, which is what a user has to type when
        // the save's gamedata section cannot be read. Codes are never all digits, so this cannot shadow one.
        if (code.Length > 0 && code.All(char.IsAsciiDigit))
        {
            id = int.Parse(code);
            return true;
        }

        var ids = blockClass ? _blocks?.IdsOf(code) ?? Array.Empty<int>() : _items?.IdsOf(code) ?? Array.Empty<int>();
        if (ids.Count == 0)
        {
            error = (blockClass ? _blocks is null : _items is null)
                ? "this save has no item registry, so only 'item:<id>' and 'block:<id>' can be typed"
                : $"no {(blockClass ? "block" : "item")} with the code '{code}' in this save";
            return false;
        }

        id = ids[0];
        return true;
    }

    /// <summary>The other half of a trader's item: the offer's own stack for a slot, the slot for an offer.</summary>
    private ChunkField? PartnerOf(ChunkField stack)
    {
        foreach (var block in Blocks)
        {
            if (block.Slots is null || block.PairedOffers is null) continue;

            if (block.Slots.Children.Contains(stack))
            {
                var offer = block.PairedOffers.Children.FirstOrDefault(o => o.Key == stack.Key);
                return offer?.Children.FirstOrDefault(c => c.Type == ChunkFieldType.ItemStack);
            }

            var owner = block.PairedOffers.Children.FirstOrDefault(o => o.Children.Contains(stack));
            if (owner is not null)
                return block.Slots.Children.FirstOrDefault(x => x.Key == owner.Key);
        }

        return null;
    }

    // ------------------------- counts and build -------------------------

    /// <summary>What an apply would change: one walk over the record, removals classified by what they are.</summary>
    public EntityDataCounts Counts()
    {
        var counts = new EntityDataCounts(0, 0, 0, 0, 0, 0);
        CountFields(RootFields, ref counts);
        return counts;
    }

    private void CountFields(IEnumerable<ChunkField> fields, ref EntityDataCounts sink)
    {
        foreach (var f in fields)
        {
            if (f.Removed)
            {
                var freed = f.Raw.Length;
                if (_blockNodes.Contains(f))
                    sink = sink with { BlocksRemoved = sink.BlocksRemoved + 1, BytesFreed = sink.BytesFreed + freed };
                else if (_offerNodes.Contains(f))
                    sink = sink with { OffersRemoved = sink.OffersRemoved + 1, BytesFreed = sink.BytesFreed + freed };
                else if (_slotNodes.Contains(f))
                    sink = sink with { ItemsRemoved = sink.ItemsRemoved + 1, BytesFreed = sink.BytesFreed + freed };
                else
                    sink = sink with { NodesRemoved = sink.NodesRemoved + 1, BytesFreed = sink.BytesFreed + freed };
                continue; // a removed node takes its subtree with it — nothing inside counts separately
            }

            if (f.Dirty) sink = sink with { FieldsEdited = sink.FieldsEdited + 1 };
            if (f.Children.Count > 0) CountFields(f.Children, ref sink);
        }
    }

    /// <summary>Serialises the record once. A null payload means "nothing differs from the saved bytes".</summary>
    public EntityDataBuild Build()
    {
        var counts = Counts();
        if (!counts.Changed) return new EntityDataBuild(null, counts);
        if (!Lossless)
            throw new InvalidOperationException(
                "this record does not round-trip through the parser — rebuilding it would change bytes the editor "
                + "does not understand; nothing was built");

        var payload = Serialize(Entity, BlockEntity);
        return new EntityDataBuild(payload.AsSpan().SequenceEqual(Raw) ? null : payload, counts);
    }

    /// <summary>The record's payload as the parser→serialiser round trip writes it. One place decides whether
    /// this record is a creature (field 6) or a block entity (field 8): the load-time round-trip check and the
    /// build must ask the same question, or a record could load as lossless and be written back on the other path.</summary>
    private static byte[] Serialize(EntityRecord? entity, BlockEntityRecord? blockEntity) => entity is not null
        ? ChunkDataEditor.SerializeEntity(entity)
        : blockEntity is not null
            ? ChunkDataEditor.SerializeBlockEntity(blockEntity)
            : Array.Empty<byte>();

    /// <summary>Depth-first walk of every tree. Inventories and offer lists are found by shape, not by name:
    /// a mod may call its inventory anything.</summary>
    private static void Walk(IEnumerable<ChunkField> fields, Action<ChunkField> visit)
    {
        foreach (var f in fields)
        {
            visit(f);
            if (f.Children.Count > 0) Walk(f.Children, visit);
        }
    }
}
