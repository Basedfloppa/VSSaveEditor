using System.Text;
using Xunit;

namespace VSCore.Tests;

/// <summary>
///     The per-player document behind the "Player data" window: character + inventories + every `gamedata`
///     key that belongs to the player or names it, and the rebuild of both blobs after edits.
/// </summary>
public class PlayerDataDocumentTests
{
    private const string UidA = "zCoQ0h2n1QqKA6d5zrbsbnVk";
    private const string UidB = "p+BcZDwmc9EddZ3BJBMhKCot";

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

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] Lp(string s) => Cat(Varint(Text(s).Length), Text(s));

    private static ChunkField Float(string key, float v)
        => new() { Type = ChunkFieldType.Float, Key = key, FloatValue = v, Dirty = true };

    private static ChunkField Str(string key, string v)
        => new() { Type = ChunkFieldType.String, Key = key, StringValue = v, Dirty = true };

    private static ChunkField Tree(string key, params ChunkField[] children)
        => new() { Type = ChunkFieldType.Tree, Key = key, Children = children.ToList(), Dirty = true };

    private static byte[] Body(params ChunkField[] fields) => ChunkDataEditor.SerializeFields(fields);

    private static byte[] Inventory(string name, params ChunkField[] fields)
        => LenField(2, Cat(LenField(1, Text(name)), LenField(2, Body(fields))));

    private static byte[] EntityBody() => Cat(Lp("EntityPlayer"), Lp("1.22.7"),
        Body(Tree("health", Float("currenthealth", 15f), Float("maxhealth", 15f)),
            Tree("hunger", Float("currentsaturation", 1200f)),
            Str("playerUID", UidA)));

    private static byte[] PlayerRow() => Cat(
        LenField(1, Text(UidA)),
        Inventory("hotbar-1", Tree("qslots", Str("slot-0", "game:axe-stone"))),
        Inventory("backpack", Tree("qslots", Str("slot-0", "game:ore-quartz"))),
        LenField(3, EntityBody()));

    private static byte[] ModEntry(string key, byte[] value)
        => LenField(ModDataStore.ModDataField, Cat(LenField(1, Text(key)), LenField(2, value)));

    /// <summary>The shape of Divine Ascension: a nested message with the uid and a couple of counters.</summary>
    private static byte[] Progression(string uid)
        => Cat(LenField(100, Cat(LenField(1, Text(uid)), VarintField(2, 5), VarintField(3, 29))),
            LenField(101, Cat(VarintField(1, 7), VarintField(2, 130))));

    /// <summary>`loreDiscoveriesByPlayerUid`: one dictionary entry per player, keyed and valued by uid.</summary>
    private static byte[] Roster(params string[] uids)
        => Cat(uids.Select(u => LenField(1, Cat(LenField(1, Text(u)), LenField(2, new byte[] { 9, 9 })))).ToArray());

    private static byte[] Gamedata()
        => Cat(
            LenField(27, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }), // a registry field nobody edits
            ModEntry("divineascension_playerprogressiondata_" + UidA, Progression(UidA)),
            ModEntry("divineascension_playerprogressiondata_" + UidB, Progression(UidB)),
            ModEntry("divineascension_religions", Text("Stone Order")),
            ModEntry("loreDiscoveriesByPlayerUid", Roster(UidA, UidB)));

    private static PlayerDataDocument Load(byte[] gamedata = null!)
        => PlayerDataDocument.Load(3, UidA, "zorolin", PlayerRow(), gamedata ?? Gamedata(),
            new HashSet<string>(new[] { UidA, UidB }, StringComparer.Ordinal));

    [Fact(DisplayName = "document: the window sees the character, the inventories and the player's keys")]
    public void Load_ListsAllSources()
    {
        var doc = Load();
        Assert.Equal(PlayerDataKind.Character, doc.Blocks[0].Kind);
        Assert.Equal(2, doc.Blocks.Count(b => b.Kind == PlayerDataKind.Inventory));
        var mine = doc.Blocks.Where(b => b.Kind == PlayerDataKind.ModKey).Select(b => b.Title).ToList();
        Assert.Equal(new[] { "divineascension_playerprogressiondata_" + UidA }, mine);
        var shared = doc.Blocks.Where(b => b.Kind == PlayerDataKind.SharedKey).Select(b => b.Title).ToList();
        Assert.Equal(new[] { "loreDiscoveriesByPlayerUid" }, shared);
        Assert.False(doc.HasChanges);
        Assert.False(doc.Build().Changed);
    }

    [Fact(DisplayName = "document: deleting the modifier's key removes only this player's entry")]
    public void Build_RemovesPlayerKey()
    {
        var doc = Load();
        var key = doc.Blocks.First(b => b.Kind == PlayerDataKind.ModKey);
        key.Removed = true;
        var build = doc.Build();

        Assert.NotNull(build.NewGamedata);
        Assert.Null(build.NewPlayerdata); // the character itself was not touched
        var entries = ModDataStore.Read(build.NewGamedata);
        Assert.DoesNotContain(entries, e => e.Key == key.Title);
        Assert.Contains(entries, e => e.Key == "divineascension_playerprogressiondata_" + UidB);
        Assert.Contains(entries, e => e.Key == "divineascension_religions");
        Assert.Equal(1, build.Counts.KeysRemoved);
        // the untouched keys are byte-identical
        var before = ModDataStore.Read(Gamedata());
        var after = entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        foreach (var e in before.Where(e => e.Key != key.Title))
            Assert.Equal(e.Value, after[e.Key]);
    }

    [Fact(DisplayName = "document: an edited value in a mod key is written back without touching its neighbours")]
    public void Build_RewritesValue()
    {
        var doc = Load();
        var block = doc.Blocks.First(b => b.Kind == PlayerDataKind.ModKey);
        var counter = block.Nodes!.First(n => n.Field == 101).Children![1];
        PbTree.SetInt(counter, 999);
        Assert.Equal(1, doc.Counts().NodesEdited);
        var build = doc.Build();

        var entry = ModDataStore.Read(build.NewGamedata!).First(e => e.Key == block.Title);
        var nodes = PbTree.Parse(entry.Value);
        Assert.Equal(999, nodes.First(n => n.Field == 101).Children![1].IntValue);
        Assert.Equal(UidA, nodes.First().Children![0].Text);
        Assert.Equal(Progression(UidB), ModDataStore.Read(build.NewGamedata!).First(e => e.Key.EndsWith(UidB)).Value);
    }

    [Fact(DisplayName = "document: in a shared key only the entry naming the player is cut")]
    public void Build_SharedKeyEntryRemoved()
    {
        var doc = Load();
        var shared = doc.Blocks.First(b => b.Kind == PlayerDataKind.SharedKey);
        Assert.Equal(1, PlayerDataDocument.CountPlayerEntries(shared, UidA));
        Assert.Equal(1, PlayerDataDocument.MarkPlayerEntries(shared, UidA));
        var build = doc.Build();

        var entry = ModDataStore.Read(build.NewGamedata!).First(e => e.Key == "loreDiscoveriesByPlayerUid");
        var text = Encoding.UTF8.GetString(entry.Value);
        Assert.DoesNotContain(UidA, text);
        Assert.Contains(UidB, text);
    }

    [Fact(DisplayName = "document: a deleted character field and a deleted inventory land in the same new row")]
    public void Build_PlayerdataEditsTogether()
    {
        var doc = Load();
        var character = doc.Blocks.First(b => b.Kind == PlayerDataKind.Character);
        var hunger = character.Entity!.RootFields.First(f => f.Key == "hunger");
        hunger.Removed = true;
        var current = character.Entity!.Fields.First(f => f.DisplayPath == "health.currenthealth");
        Assert.True(ChunkFieldText.TryApply(current, "7.5", out var error), error);

        var backpack = doc.Blocks.First(b => b.Kind == PlayerDataKind.Inventory && b.Inventory!.Name == "backpack");
        backpack.Removed = true;

        var build = doc.Build();
        Assert.NotNull(build.NewPlayerdata);
        var parsed = PlayerDataParser.Parse(build.NewPlayerdata!, 3);
        Assert.Single(parsed.Inventories);
        Assert.Equal("hotbar-1", parsed.Inventories[0].Name);

        var entity = PlayerDataParser.ParseEntityRecord(build.NewPlayerdata!)!;
        Assert.DoesNotContain(entity.Fields, f => f.DisplayPath.StartsWith("hunger."));
        Assert.Equal(7.5f, entity.Fields.First(f => f.DisplayPath == "health.currenthealth").FloatValue);
        Assert.Equal(1, build.Counts.InventoriesRemoved);
        Assert.Equal(1, build.Counts.FieldsEdited);
        Assert.Equal(1, build.Counts.NodesRemoved);
    }

    [Fact(DisplayName = "document: a player without uid still shows the character and the inventories")]
    public void Load_WithoutUid_ShowsCharacterOnly()
    {
        var doc = PlayerDataDocument.Load(3, null, null, PlayerRow(), Gamedata());
        Assert.Equal(3, doc.Blocks.Count);
        Assert.DoesNotContain(doc.Blocks, b => b.Kind is PlayerDataKind.ModKey or PlayerDataKind.SharedKey);
    }

    [Fact(DisplayName = "document: a world key naming only OTHER players is not this player's data")]
    public void Load_IgnoresSharedKeysOfOtherPlayers()
    {
        // `loreDiscoveriesByPlayerUid` keeps one entry per player. A roster without this player in it must not
        // show up in his window with a "remove 0 entries" button.
        var gamedata = Cat(
            ModEntry("loreDiscoveriesByPlayerUid", Roster(UidB)),
            ModEntry("divineascension_playerprogressiondata_" + UidA, Progression(UidA)));
        var doc = Load(gamedata);
        Assert.DoesNotContain(doc.Blocks, b => b.Kind == PlayerDataKind.SharedKey);
        Assert.Contains(doc.Blocks, b => b.Kind == PlayerDataKind.ModKey);
    }

    [Fact(DisplayName = "document: a world key is listed when it really holds an entry of this player")]
    public void Load_ListsSharedKeyOfThisPlayer()
    {
        var doc = Load(Cat(ModEntry("loreDiscoveriesByPlayerUid", Roster(UidA, UidB))));
        var shared = Assert.Single(doc.Blocks.Where(b => b.Kind == PlayerDataKind.SharedKey));
        Assert.Equal(1, PlayerDataDocument.CountPlayerEntries(shared, UidA));
    }
}
