using System.Text;
using VSCore.Format;
using Xunit;

namespace VSCore.Tests;

/// <summary>
///     Rewriting the playerdata row: the character (field 3) and the inventories (repeated field 2) of ONE
///     player, in one pass. The row also carries fields this editor does not model, and those have to survive
///     byte for byte — the row is what the game loads to spawn the character.
/// </summary>
public class PlayerDataRewriteTests
{
    private const string Uid = "zCoQ0h2n1QqKA6d5zrbsbnVk";

    private static byte[] Varint(long v)
    {
        var ms = new MemoryStream();
        var u = (ulong)v;
        while (u >= 0x80)
        {
            ms.WriteByte((byte)((u & 0x7F) | 0x80));
            u >>= 7;
        }

        ms.WriteByte((byte)u);
        return ms.ToArray();
    }

    private static byte[] Cat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }

    private static byte[] LenField(int field, byte[] payload)
        => Cat(Varint(((long)field << 3) | 2), Varint(payload.Length), payload);

    private static byte[] VarintField(int field, long value) => Cat(Varint((long)field << 3), Varint(value));

    private static byte[] Lp(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return Cat(Varint(b.Length), b);
    }

    private static ChunkField Float(string key, float v)
        => new() { Type = ChunkFieldType.Float, Key = key, FloatValue = v, Dirty = true };

    private static ChunkField Str(string key, string v)
        => new() { Type = ChunkFieldType.String, Key = key, StringValue = v, Dirty = true };

    private static ChunkField Tree(string key, params ChunkField[] children)
        => new() { Type = ChunkFieldType.Tree, Key = key, Children = children.ToList(), Dirty = true };

    private static byte[] Body(params ChunkField[] fields) => ChunkDataEditor.SerializeFields(fields);

    private static byte[] EntityBody() => Cat(Lp("EntityPlayer"), Lp("1.22.7"),
        Body(Tree("health", Float("currenthealth", 15f), Float("maxhealth", 15f)),
            Str("playerUID", Uid)));

    private static byte[] Inventory(string name, params ChunkField[] fields) => LenField(2, Cat(LenField(1, Encoding.UTF8.GetBytes(name)), LenField(2, Body(fields))));

    /// <summary>A playerdata row: uid, two inventories, the character, and two fields the editor does not model.</summary>
    private static byte[] Row() => Cat(
        LenField(1, Encoding.UTF8.GetBytes(Uid)),
        Inventory("hotbar-1", Tree("qslots", Str("slot-0", "game:axe-stone"))),
        Inventory("backpack", Tree("qslots", Str("slot-0", "game:ore-quartz"))),
        LenField(3, EntityBody()),
        VarintField(4, 12345),
        LenField(14, new byte[] { 1, 2, 3, 4 }));

    [Fact(DisplayName = "playerdata: a new inventory body lands in that inventory only")]
    public void Rewrite_ReplacesOneInventoryBody()
    {
        var row = Row();
        var newBody = Body(Tree("qslots", Str("slot-0", "game:pickaxe-copper")));
        var rebuilt = PlayerDataParser.Rewrite(row, inventoryBodies: new Dictionary<int, byte[]> { [1] = newBody });

        var parsed = PlayerDataParser.Parse(rebuilt, 7);
        Assert.Equal(Uid, parsed.Uid);
        Assert.Equal(2, parsed.Inventories.Count);
        Assert.Equal("hotbar-1", parsed.Inventories[0].Name);
        
        Assert.Contains("game:pickaxe-copper", Encoding.UTF8.GetString(parsed.Inventories[1].RawBytes));
        Assert.Contains("game:axe-stone", Encoding.UTF8.GetString(parsed.Inventories[0].RawBytes));
        // the character and the unmodelled fields are copied as they were
        Assert.NotNull(PlayerDataParser.ParseEntityRecord(rebuilt));
        Assert.Equal(12345, EmbeddedVarint(rebuilt, 4));
    }

    [Fact(DisplayName = "playerdata: an inventory can be removed without touching the others")]
    public void Rewrite_RemovesInventory()
    {
        var rebuilt = PlayerDataParser.Rewrite(Row(), removedInventories: new HashSet<int> { 0 });
        var parsed = PlayerDataParser.Parse(rebuilt, 7);
        Assert.Single(parsed.Inventories);
        Assert.Equal("backpack", parsed.Inventories[0].Name);
        Assert.Null(PlayerDataParser.ParseEntityRecord(rebuilt) is null ? "missing character" : null);
    }

    [Fact(DisplayName = "playerdata: a new character body replaces field 3 and keeps the inventories")]
    public void Rewrite_ReplacesEntityBody()
    {
        var newEntity = Cat(Lp("EntityPlayer"), Lp("1.22.7"),
            Body(Tree("health", Float("currenthealth", 99f), Float("maxhealth", 99f)), Str("playerUID", Uid)));
        var rebuilt = PlayerDataParser.Rewrite(Row(), entityBody: newEntity);
        var entity = PlayerDataParser.ParseEntityRecord(rebuilt);
        Assert.NotNull(entity);
        Assert.Equal(2, PlayerDataParser.Parse(rebuilt, 7).Inventories.Count);
        var health = entity!.Fields.First(f => f.DisplayPath == "health.currenthealth");
        Assert.Equal(99f, health.FloatValue);
    }

    [Fact(DisplayName = "playerdata: removing a field of the character keeps the rest of the tree")]
    public void Rewrite_CharacterFieldCanBeDeleted()
    {
        var entity = PlayerDataParser.ParseEntityRecord(Row())!;
        var health = entity.Fields.First(f => f.DisplayPath == "health.currenthealth");
        var healthTree = entity.RootFields.First(f => f.Key == "health");
        healthTree.Children.Remove(health);
        healthTree.Dirty = true;
        Assert.DoesNotContain(healthTree.Children, c => c.Key == "currenthealth");

        var rebuilt = PlayerDataParser.Rewrite(Row(), entityBody: ChunkDataEditor.SerializeEntity(entity));
        var again = PlayerDataParser.ParseEntityRecord(rebuilt)!;
        Assert.DoesNotContain(again.Fields, f => f.DisplayPath == "health.currenthealth");
        Assert.Contains(again.Fields, f => f.DisplayPath == "health.maxhealth");
        Assert.Contains(again.Fields, f => f.DisplayPath == "playerUID");
    }

    [Fact(DisplayName = "playerdata: no changes means the same bytes")]
    public void Rewrite_NothingToDo_IsNoOp()
    {
        var row = Row();
        Assert.Equal(row, PlayerDataParser.Rewrite(row));
    }

    /// <summary>The value of a top-level varint field, read back with the same walk the editor uses.</summary>
    private static long EmbeddedVarint(byte[] blob, int field)
    {
        foreach (var f in ChunkDataEditor.WalkTop(blob))
            if (f.Field == field)
                return PbReader.ReadVarint(blob, f.PayloadStart).value;
        return -1;
    }

    [Fact(DisplayName = "playerdata: the index of an edit is the index the reader showed (two bodies in one inventory)")]
    public void Rewrite_And_Parse_AgreeOnInventoryIndex()
    {
        // An inventory message with two field-2 entries. The reader takes the FIRST as the ECX body, and the
        // writer may only replace that one: two rules here would edit a body the window never showed.
        var dup = LenField(2, Cat(
            LenField(1, Encoding.UTF8.GetBytes("dup")),
            LenField(2, Body(Tree("qslots", Str("slot-0", "game:first")))),
            LenField(2, Body(Tree("qslots", Str("slot-0", "game:second"))))));
        var row = Cat(LenField(1, Encoding.UTF8.GetBytes(Uid)), dup);

        var parsed = PlayerDataParser.Parse(row, 7);
        Assert.Single(parsed.Inventories);
        Assert.Contains("game:first", Encoding.UTF8.GetString(parsed.Inventories[0].RawBytes));

        var rebuilt = PlayerDataParser.Rewrite(row,
            inventoryBodies: new Dictionary<int, byte[]> { [0] = Body(Tree("qslots", Str("slot-0", "game:new"))) });
        var text = Encoding.UTF8.GetString(rebuilt);
        Assert.Contains("game:new", text);
        Assert.DoesNotContain("game:first", text);
        Assert.Contains("game:second", text); // the second field 2 is not the body and keeps its bytes
    }

    [Fact(DisplayName = "playerdata: a varint field 2 is not an inventory for either side of an edit")]
    public void Parse_And_Rewrite_SkipNonMessageFieldTwo()
    {
        // WalkTop reports the varint as field 2 too. Counting it as an inventory would shift every index after
        // it, so the edit would land in the next inventory while the window showed the first one.
        var row = Cat(
            LenField(1, Encoding.UTF8.GetBytes(Uid)),
            VarintField(2, 5),
            Inventory("hotbar-1", Tree("qslots", Str("slot-0", "game:axe-stone"))));

        Assert.Single(PlayerDataParser.Parse(row, 7).Inventories);
        var rebuilt = PlayerDataParser.Rewrite(row,
            inventoryBodies: new Dictionary<int, byte[]> { [0] = Body(Tree("qslots", Str("slot-0", "game:new"))) });
        var text = Encoding.UTF8.GetString(rebuilt);
        Assert.Contains("game:new", text);
        Assert.DoesNotContain("game:axe-stone", text);
        Assert.Equal(5, EmbeddedVarint(rebuilt, 2));
    }
}
