using Xunit;
using static VSCore.Tests.TestBytes;

namespace VSCore.Tests;

/// <summary>
///     The per-entity document behind the "Entity data" window: the record, its inventories and a trader's
///     trade offers, with deletion of an item and of an offer as one operation on two halves of the same item.
/// </summary>
public class EntityDataDocumentTests
{
    /// <summary>A trader shaped like the real records of a 1.22 save: `traderInventory` holds `qslots`, `slots`
    /// and `tradeItems`, where offer k and slot k are the same item.</summary>
    private static byte[] TraderPayload()
        => Cat(Text("entitytrader"), Text("1.0"),
            EcxTree("nametag", EcxString("name", "Aram")),
            EcxTree("health", EcxFloat("currenthealth", 25f), EcxFloat("maxhealth", 25f)),
            EcxTree("traderInventory",
                EcxInt("qslots", 41),
                EcxTree("slots",
                    EcxStack("0", 1, 2228, 1, EcxFloat("condition", 0.82f)),
                    EcxStack("2", 1, 2394, 1),
                    EcxStack("9", 1, 2866, 31)),
                EcxTree("tradeItems",
                    EcxTree("0", EcxStack("stack", 1, 2228, 1), EcxInt("price", 6), EcxInt("stock", 1)),
                    EcxTree("2", EcxStack("stack", 1, 2394, 1), EcxInt("price", 4), EcxInt("stock", 1)))));

    private static EntityDataDocument LoadTrader(byte[]? payload = null)
        => EntityDataDocument.Load(payload ?? TraderPayload(), 6, 7912, 1, "trader Aram");

    private static ChunkField Node(EntityDataDocument doc, string path)
    {
        var list = doc.RootFields;
        ChunkField? cur = null;
        foreach (var part in path.Split('.'))
        {
            cur = list.FirstOrDefault(f => f.Key == part);
            Assert.NotNull(cur);
            list = cur!.Children;
        }

        return cur!;
    }

    private static EntityDataBlock Block(EntityDataDocument doc, EntityDataKind kind)
    {
        var block = doc.Blocks.FirstOrDefault(b => b.Kind == kind);
        Assert.NotNull(block);
        return block!;
    }

    [Fact]
    public void Setting_an_item_writes_the_header_and_the_paired_offer_together()
    {
        var doc = LoadTrader();
        var slot = Block(doc, EntityDataKind.Inventory).Slots!.Children.First(c => c.Key == "2");

        Assert.True(doc.TrySetItem(slot, "item:2394", 5, out var error), error);
        Assert.Equal(2394, slot.ItemId);
        Assert.Equal(5, slot.StackSize);

        // the same item lives in the offer — a trader whose offer says 1 while the slot says 5 sells what it does not have
        var offer = Node(doc, "traderInventory.tradeItems.2.stack");
        Assert.Equal(2394, offer.ItemId);
        Assert.Equal(5, offer.StackSize);

        var build = doc.Build();
        Assert.True(build.Changed);
        var again = LoadTrader(build.Payload!);
        var slot2 = Block(again, EntityDataKind.Inventory).Slots!.Children.First(c => c.Key == "2");
        Assert.Equal(2394, slot2.ItemId);
        Assert.Equal(5, slot2.StackSize);
        Assert.Equal(5, Node(again, "traderInventory.tradeItems.2.stack").StackSize);
    }

    [Fact]
    public void Setting_an_item_resolves_a_code_and_keeps_the_old_item_when_it_cannot()
    {
        var gamedata = Gamedata(("ItemIDs", new[] { (2228, "clothes-head-hat"), (2394, "gem-quartz") }),
            ("BlockIDs", new[] { (7, "soil-medium-normal") }));
        var doc = EntityDataDocument.Load(TraderPayload(), 6, 1, 1, "trader",
            BlockRegistry.FromGamedata(gamedata), ItemRegistry.FromGamedata(gamedata));
        var slot = Block(doc, EntityDataKind.Inventory).Slots!.Children.First(c => c.Key == "0");

        Assert.True(doc.TrySetItem(slot, "gem-quartz", 2, out var error), error);
        Assert.Equal(2394, slot.ItemId);
        Assert.Equal(1, slot.ItemClass);

        Assert.False(doc.TrySetItem(slot, "no-such-item", 2, out var badCode));
        Assert.Contains("no item or block", badCode);
        Assert.Equal(2394, slot.ItemId); // a rejected code must not half-apply
        Assert.Equal(2, slot.StackSize);

        // a block code goes to the block registry and class 0, not to the item with the same id
        Assert.True(doc.TrySetItem(slot, "block:soil-medium-normal", 3, out var blockError), blockError);
        Assert.Equal(0, slot.ItemClass);
        Assert.Equal(7, slot.ItemId);
    }

    [Fact]
    public void A_stack_needs_at_least_one_item()
    {
        var doc = LoadTrader();
        var slot = Block(doc, EntityDataKind.Inventory).Slots!.Children.First(c => c.Key == "0");

        Assert.False(doc.TrySetItem(slot, "item:2228", 0, out var error));
        Assert.Contains("at least one item", error);
        Assert.Equal(1, slot.StackSize);
    }

    [Fact]
    public void Item_registry_resolves_a_code_back_to_its_ids()
    {
        var items = ItemRegistry.FromGamedata(Gamedata(("ItemIDs", new[] { (5, "ore-quartz"), (6, "ore-quartz") })));

        Assert.Equal(new[] { 5, 6 }, items.IdsOf("ore-quartz"));
        Assert.True(items.HasCode("ore-quartz"));
        Assert.Empty(items.IdsOf("no-such-code"));
    }

    [Theory]
    [InlineData("11", 11L, null)]      // a varint field: a whole number
    [InlineData(" 7 ", 7L, null)]      // surrounding spaces are the box's, not the value's
    [InlineData("11.5", 0L, "whole number")] // the message the window shows under the field
    public void TrySetText_takes_a_number_for_a_varint_field(string typed, long expected, string? errorPart)
    {
        var node = new PbNode { WireType = 0, IntValue = 3 };
        var ok = PbTree.TrySetText(node, typed, out var error);

        Assert.Equal(errorPart is null, ok);
        if (errorPart is not null) Assert.Contains(errorPart, error);
        else Assert.Equal(expected, node.IntValue);
    }

    [Fact]
    public void TrySetText_takes_text_for_a_len_field_and_hex_when_the_text_is_not_utf8()
    {
        var text = new PbNode { WireType = 2, Payload = "old"u8.ToArray() };
        Assert.True(PbTree.TrySetText(text, "new", out _));
        Assert.Equal("new", System.Text.Encoding.UTF8.GetString(text.Payload));

        var hex = new PbNode { WireType = 2, Payload = new byte[] { 1 } }; // not valid UTF-8: shown as hex
        Assert.True(PbTree.TrySetText(hex, "ff00", out _));
        Assert.Equal(new byte[] { 0xff, 0x00 }, hex.Payload);
        Assert.False(PbTree.TrySetText(hex, "zz", out var bad));
        Assert.NotNull(bad);
    }

    [Fact]
    public void Editing_a_price_lands_in_the_payload_and_leaves_the_other_offer_alone()
    {
        // The box path, not a setter: this is what the window does when a price is typed
        var doc = LoadTrader();
        var price = Node(doc, "traderInventory.tradeItems.0.price");
        Assert.Equal(6, price.IntValue);
        Assert.True(ChunkFieldText.TryApply(price, "11", out var error), error);

        var build = doc.Build();

        Assert.True(build.Changed);
        Assert.Equal(1, build.Counts.FieldsEdited);
        var reloaded = EntityDataDocument.Load(build.Payload!, 6, 7912, 1, "trader Aram");
        Assert.Equal(11, Node(reloaded, "traderInventory.tradeItems.0.price").IntValue);
        Assert.Equal(4, Node(reloaded, "traderInventory.tradeItems.2.price").IntValue);
    }

    [Fact]
    public void Deleting_the_offer_list_really_deletes_it()
    {
        var doc = LoadTrader();
        var offers = doc.Blocks.Single(b => b.Kind == EntityDataKind.TradeOffers);

        offers.Removed = true;
        var build = doc.Build();

        Assert.True(build.Changed, "deleting the offer list must change the record, not just the window");
        var reloaded = LoadTrader(build.Payload);
        Assert.Empty(reloaded.Offers);
        Assert.DoesNotContain(reloaded.Blocks, b => b.Kind == EntityDataKind.TradeOffers);
    }

    [Fact]
    public void Deleting_an_inventory_takes_its_offers_with_it()
    {
        var doc = LoadTrader();
        var inventory = Block(doc, EntityDataKind.Inventory);

        inventory.Removed = true;
        var build = doc.Build();

        Assert.True(build.Changed, "deleting the inventory must change the record");
        var reloaded = LoadTrader(build.Payload);
        Assert.DoesNotContain(reloaded.Blocks,
            b => b.Kind is EntityDataKind.Inventory or EntityDataKind.TradeOffers);
        Assert.Empty(reloaded.Offers);
    }

    [Fact]
    public void Load_ListsRecordInventoryAndOffers()
    {
        var doc = LoadTrader();

        Assert.True(doc.Lossless);
        Assert.Equal(new[] { EntityDataKind.Record, EntityDataKind.Inventory, EntityDataKind.TradeOffers },
            doc.Blocks.Select(b => b.Kind).ToArray());
        Assert.Equal("traderInventory", Block(doc, EntityDataKind.Inventory).Title.Split("— ")[1]);
        Assert.Equal(2, doc.Offers.Count);
        Assert.Equal(new[] { "0", "2" }, doc.Offers.Select(o => o.Key).ToArray());
        Assert.All(doc.Offers, o => Assert.True(o.HasSlot));
        Assert.Equal(new[] { 6d, 4d }, doc.Offers.Select(o => o.Price).ToArray());
        Assert.Equal(new[] { 2228, 2394 }, doc.Offers.Select(o => o.ItemCode).Select(c => int.Parse(c.Split(':')[1])));
    }

    [Fact]
    public void Build_WithoutEdits_ProducesNoPayload()
    {
        var doc = LoadTrader();

        Assert.False(doc.Counts().Changed);
        var build = doc.Build();
        Assert.Null(build.Payload);
        Assert.False(build.Changed);
        Assert.Equal("nothing to apply", build.Summary);
    }

    [Fact]
    public void RemoveOffer_CutsTheOfferAndItsSlot()
    {
        var payload = TraderPayload();
        var doc = LoadTrader(payload);
        var offerBytes = Node(doc, "traderInventory.tradeItems.0").Raw.Length;
        var slotBytes = Node(doc, "traderInventory.slots.0").Raw.Length;

        var text = doc.RemoveOffer("0");
        Assert.Contains("deleted", text);
        Assert.Contains("inventory slot", text);

        var counts = doc.Counts();
        Assert.Equal(1, counts.OffersRemoved);
        Assert.Equal(1, counts.ItemsRemoved);
        Assert.Equal(offerBytes + slotBytes, counts.BytesFreed);
        Assert.Contains("1 trade offer(s) deleted", counts.Label);

        var build = doc.Build();
        Assert.NotNull(build.Payload);
        Assert.Equal(payload.Length - offerBytes - slotBytes, build.Payload!.Length);

        // the rebuilt record parses back to the same tree and holds no offer '0' and no slot '0'
        var back = EntityDataDocument.Load(build.Payload, 6, 7912, 1, "trader Aram");
        Assert.Equal(new[] { "2" }, back.Offers.Select(o => o.Key).ToArray());
        Assert.Equal(new[] { "2", "9" }, Node(back, "traderInventory.slots").Children.Select(c => c.Key).ToArray());
        Assert.Equal("Aram", Node(back, "nametag.name").StringValue);
        Assert.Equal(25f, Node(back, "health.currenthealth").FloatValue);
        Assert.Empty(back.Counts() is var c && c.Changed ? new[] { 1 } : Array.Empty<int>()); // nothing pending after reload
    }

    [Fact]
    public void RemoveOffer_Twice_RestoresIt()
    {
        var doc = LoadTrader();
        doc.RemoveOffer("0");
        var back = doc.RemoveOffer("0");

        Assert.Contains("restored", back);
        Assert.False(doc.Counts().Changed);
        Assert.Null(doc.Build().Payload);
    }

    [Fact]
    public void RemoveOffer_UnknownKey_ChangesNothing()
    {
        var doc = LoadTrader();
        Assert.Contains("not found", doc.RemoveOffer("7"));
        Assert.False(doc.Counts().Changed);
    }

    [Fact]
    public void RemoveItem_OnASlotWithoutOffer_LeavesTheOffersAlone()
    {
        var doc = LoadTrader();
        var inventory = Block(doc, EntityDataKind.Inventory);
        var money = inventory.Slots!.Children.First(c => c.Key == "9");

        var text = doc.RemoveItem(inventory, money);
        Assert.Contains("item in slot '9' deleted", text);
        Assert.DoesNotContain("trade offer", text);

        var counts = doc.Counts();
        Assert.Equal(1, counts.ItemsRemoved);
        Assert.Equal(0, counts.OffersRemoved);

        var build = doc.Build();
        var back = EntityDataDocument.Load(build.Payload!, 6, 7912, 1, "trader");
        Assert.Equal(2, back.Offers.Count);
        Assert.Equal(new[] { "0", "2" }, Node(back, "traderInventory.slots").Children.Select(c => c.Key).ToArray());
    }

    [Fact]
    public void RemoveItem_OnAPairedSlot_RemovesTheOfferToo()
    {
        var doc = LoadTrader();
        var inventory = Block(doc, EntityDataKind.Inventory);
        var slot = inventory.Slots!.Children.First(c => c.Key == "2");

        var text = doc.RemoveItem(inventory, slot);
        Assert.Contains("together with its trade offer", text);

        var counts = doc.Counts();
        Assert.Equal(1, counts.ItemsRemoved);
        Assert.Equal(1, counts.OffersRemoved);

        var back = EntityDataDocument.Load(doc.Build().Payload!, 6, 7912, 1, "trader");
        Assert.Equal(new[] { "0" }, back.Offers.Select(o => o.Key).ToArray());
        Assert.Equal(new[] { "0", "9" }, Node(back, "traderInventory.slots").Children.Select(c => c.Key).ToArray());
    }

    [Fact]
    public void DeletingARecordField_IsCountedAsOneField()
    {
        var doc = LoadTrader();
        var name = Node(doc, "nametag.name");
        name.Removed = true;

        var counts = doc.Counts();
        Assert.Equal(1, counts.NodesRemoved);
        Assert.Equal(0, counts.ItemsRemoved);
        Assert.Equal(1, counts.FieldsEdited + counts.NodesRemoved - 0); // no edit, only the deletion
        Assert.Equal(0, counts.FieldsEdited);

        var payload = doc.Build().Payload!;
        Assert.Equal(TraderPayload().Length - name.Raw.Length, payload.Length);
        var back = EntityDataDocument.Load(payload, 6, 7912, 1, "trader");
        Assert.Empty(Node(back, "nametag").Children);
    }

    [Fact]
    public void DeletingAWholeInventory_CountsAsBlock()
    {
        var doc = LoadTrader();
        var inventory = Block(doc, EntityDataKind.Inventory);
        inventory.Removed = true;

        var counts = doc.Counts();
        Assert.Equal(1, counts.BlocksRemoved);
        Assert.Equal(inventory.Node!.Raw.Length, counts.BytesFreed);

        var back = EntityDataDocument.Load(doc.Build().Payload!, 6, 7912, 1, "trader");
        Assert.Empty(back.Blocks.Where(b => b.Kind != EntityDataKind.Record));
        Assert.Empty(back.Offers);
    }

    [Fact]
    public void BlockEntity_InventoryIsEditableToo()
    {
        var payload = Cat(Text("chest"),
            EcxInt("posx", 100),
            EcxTree("inventory",
                EcxInt("qslots", 8),
                EcxTree("slots",
                    EcxStack("0", 1, 5, 3, EcxFloat("condition", 0.5f)),
                    EcxStack("1", 0, 12, 1))));

        var doc = EntityDataDocument.Load(payload, 8, 1234, 0, "chest");
        Assert.True(doc.Lossless);
        var inventory = Block(doc, EntityDataKind.Inventory);
        Assert.Equal(2, EntityDataDocument.FilledSlots(inventory).Count);

        doc.RemoveItem(inventory, inventory.Slots!.Children.First(c => c.Key == "0"));
        var back = EntityDataDocument.Load(doc.Build().Payload!, 8, 1234, 0, "chest");
        var backInv = Block(back, EntityDataKind.Inventory);
        Assert.Equal(new[] { "1" }, backInv.Slots!.Children.Select(c => c.Key).ToArray());
        Assert.Equal(100, Node(back, "posx").IntValue);
    }

    /// <summary>
    ///     The invariant the "Delete this block" bug broke: whatever the summary counts as removed must reach the
    ///     payload. Before the fix `Counts()` saw the block flag while `Build()` serialised a tree that never saw it,
    ///     so the window announced a deletion and Apply answered "nothing to apply".
    /// </summary>
    [Theory]
    [InlineData(EntityDataKind.Inventory)]
    [InlineData(EntityDataKind.TradeOffers)]
    public void Counts_and_build_agree_on_a_removed_block(EntityDataKind kind)
    {
        var doc = LoadTrader();
        Block(doc, kind).Removed = true;
        var counts = doc.Counts();
        var build = doc.Build();

        Assert.True(counts.Changed, $"{kind}: the summary announces the deletion");
        Assert.NotNull(build.Payload); // ... and the payload must follow it
        Assert.True(build.Payload!.Length < doc.Raw.Length, $"{kind}: the payload got shorter");
        var back = EntityDataDocument.Load(build.Payload, 6, 7912, 1, "trader");
        Assert.DoesNotContain(back.Blocks, b => b.Kind == kind);
    }

    [Fact]
    public void An_untouched_record_says_nothing_and_builds_nothing()
    {
        var doc = LoadTrader();
        Assert.False(doc.Counts().Changed);
        Assert.Null(doc.Build().Payload);
    }

    /// <summary>
    ///     Two readers, one format: <see cref="InventoryParser" /> on the decoded tree and this document on the ECX
    ///     fields must agree on what the same record holds — otherwise a name or a limit changed in one place only.
    /// </summary>
    [Fact]
    public void The_document_and_the_parser_see_the_same_inventory()
    {
        var payload = TraderPayload();
        var owner = new EntityInfo
        {
            Species = "game:trader-male-clothing-temperate", ChunkX = 7912, ChunkY = 1, ChunkZ = 7390, Position = 0,
            Occurrence = 0
        };

        var parsed = Assert.Single(InventoryParser.ParseEntity(payload, owner, null, null));
        var doc = EntityDataDocument.Load(payload, 6, 7912, 1, "trader Armen");
        var block = Block(doc, EntityDataKind.Inventory);
        var filled = EntityDataDocument.FilledSlots(block);

        Assert.Equal("traderInventory", parsed.Name);
        Assert.Equal(41, parsed.SlotCount);
        Assert.Equal(filled.Count, parsed.Items.Count);
        Assert.Equal(filled.Select(f => f.Key).OrderBy(k => k), parsed.Items.Select(i => i.Slot).OrderBy(k => k));
        // Slot 9 is money/gear: it is an item, but nothing sells it, so it has no offer (2 offers for 3 items)
        Assert.Equal(2, doc.Offers.Count);
    }

    [Fact]
    public void BrokenPayload_ReportsNothingEditable()
    {
        var doc = EntityDataDocument.Load(new byte[] { 0xFF, 0xFF, 0x01 }, 6, 0, 0, "broken");
        Assert.Empty(doc.Blocks);
        Assert.False(doc.Counts().Changed);
        Assert.Null(doc.Build().Payload);
    }
}
