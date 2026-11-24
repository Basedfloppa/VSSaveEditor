using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace VSCore.Tests;

/// <summary>
///     World-level mod data: the `gamedata` dictionary where mods keep per-player state
///     (`divineascension_playerprogressiondata_&lt;uid&gt;`) next to world-scoped keys (`divineascension_religions`).
///     The fixtures are synthetic saves in temp — same shape as a real one, no dependence on files on this machine.
/// </summary>
public class ModDataTests : IDisposable
{
    private const string UidA = "zCoQ0h2n1QqKA6d5zrbsbnVk";
    private const string UidB = "p+BcZDwmc9EddZ3BJBMhKCot";

    private readonly string _dir;

    public ModDataTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsmoddata_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ------------------------- blob builders (FastSerializer) -------------------------

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

    private static byte[] LenField(int field, byte[] payload) => Cat(Varint(((long)field << 3) | 2), Varint(payload.Length), payload);

    private static byte[] VarintField(int field, long value) => Cat(Varint((long)field << 3), Varint(value));

    private static byte[] Entry(string key, byte[] value, int field = ModDataStore.ModDataField)
        => LenField(field, Cat(LenField(1, Encoding.UTF8.GetBytes(key)), LenField(2, value)));

    /// <summary>A gamedata message: world fields + a big "BlockIDs" field + the mod data entries.</summary>
    private static byte[] Gamedata(params byte[][] extras) => Cat(extras);

    private static byte[] RegistryField() => LenField(27, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02 });

    // ------------------------- reading -------------------------

    [Fact(DisplayName = "gamedata: mod keys are read with their values, other fields are ignored")]
    public void Read_FindsModDataEntries()
    {
        var gd = Gamedata(VarintField(1, 512000), RegistryField(),
            Entry("divineascension_religions", new byte[] { 1, 2, 3 }),
            Entry("oreMapMarkers-" + UidA, new byte[] { 4, 5 }));

        var entries = ModDataStore.Read(gd);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { "divineascension_religions", "oreMapMarkers-" + UidA }, entries.Select(e => e.Key));
        Assert.Equal(new byte[] { 1, 2, 3 }, entries[0].Value);
        Assert.Null(entries[0].PlayerUid); // without the uid set nothing is player-scoped
    }

    [Fact(DisplayName = "gamedata: a key that ends with a known uid belongs to that player, also after '-' or ':'")]
    public void Read_MarksPlayerScopedKeys()
    {
        var uids = new HashSet<string>(StringComparer.Ordinal) { UidA, UidB };
        var gd = Gamedata(
            Entry("divineascension_playerprogressiondata_" + UidA, new byte[] { 1 }),
            Entry("oreMapMarkers-" + UidB, new byte[] { 2 }),
            Entry("terratag:labels_v1", new byte[] { 3 }),
            Entry("loreDiscoveriesByPlayerUid", Encoding.ASCII.GetBytes("..." + UidA + "...")));

        var entries = ModDataStore.Read(gd, uids);

        Assert.Equal(UidA, entries[0].PlayerUid);
        Assert.Equal(UidB, entries[1].PlayerUid);
        Assert.Null(entries[2].PlayerUid);
        // the key does not name a player — one key for the whole roster — but its value mentions one
        Assert.Null(entries[3].PlayerUid);
        Assert.True(entries[3].MentionsPlayer);
        Assert.False(entries[2].MentionsPlayer);
    }

    [Theory(DisplayName = "uid at the end of a key: exact match after a separator, nothing else")]
    [InlineData("mod_data_" + UidA, UidA)]
    [InlineData("oreMapMarkers-" + UidA, UidA)]
    [InlineData("terratag:" + UidA, UidA)]
    [InlineData("mod_data_" + UidA + "x", null)]   // the key continues past the uid
    [InlineData("mod_data" + UidA, null)]          // no separator: not a per-player key
    [InlineData("playerdata_" + UidA, UidA)]
    public void PlayerUidIn_RequiresAnExactSuffixAfterSeparator(string key, string? expected)
    {
        var uids = new HashSet<string>(StringComparer.Ordinal) { UidA, "other" };
        Assert.Equal(expected, ModDataStore.PlayerUidIn(key, uids));
    }

    [Fact(DisplayName = "mod id: text before the first separator, other separators included")]
    public void ModIdOf_SplitsOnTheFirstSeparator()
    {
        Assert.Equal("divineascension", ModDataStore.ModIdOf("divineascension_playerprogressiondata_" + UidA));
        Assert.Equal("Wiltoga", ModDataStore.ModIdOf("Wiltoga.OfflineFoodNoSpoil.PlayerData"));
        Assert.Equal("terratag", ModDataStore.ModIdOf("terratag:labels_v1"));
        Assert.Equal("oreMapMarkers", ModDataStore.ModIdOf("oreMapMarkers"));
    }

    [Fact(DisplayName = "grouping: per-player and world keys of one mod land together, the spelling's case does not split it")]
    public void Group_CollectsBothScopesOfOneMod()
    {
        var uids = new HashSet<string>(StringComparer.Ordinal) { UidA, UidB };
        var gd = Gamedata(
            Entry("DivineAscension.ModConfig", new byte[] { 1 }),
            Entry("divineascension_playerprogressiondata_" + UidA, new byte[] { 2 }),
            Entry("divineascension_playerprogressiondata_" + UidB, new byte[] { 3 }),
            Entry("divineascension_religions", new byte[] { 4 }));

        var groups = ModDataStore.Group(ModDataStore.Read(gd, uids));
        var da = Assert.Single(groups, g => g.Id.Equals("divineascension", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(4, da.Entries.Count);
        Assert.Equal(2, da.PlayerEntries.Count);
        Assert.Equal(2, da.PlayerCount);
        Assert.Equal(2, da.WorldEntries.Count);
    }

    // ------------------------- removal -------------------------

    [Fact(DisplayName = "remove: only the chosen entries disappear, every other field stays byte-identical")]
    public void Remove_CutsOnlyTheChosenEntries()
    {
        var uids = new HashSet<string>(StringComparer.Ordinal) { UidA, UidB };
        var registry = RegistryField();
        var gd = Gamedata(
            VarintField(1, 512000),
            registry,
            Entry("divineascension_playerprogressiondata_" + UidA, new byte[] { 7, 7, 7 }),
            Entry("divineascension_playerprogressiondata_" + UidB, new byte[] { 8, 8 }),
            Entry("divineascension_religions", new byte[] { 9 }));

        var entries = ModDataStore.Read(gd, uids);
        var da = ModDataStore.Group(entries).Single(g => g.Id == "divineascension");
        var removal = ModDataStore.Remove(gd, da.PlayerEntries);

        Assert.True(removal.Changed);
        Assert.Equal(2, removal.Removed.Count);
        Assert.Equal(gd.Length - removal.Removed.Sum(e => e.Length), removal.Blob.Length);
        // the registry field and the world key survive byte for byte
        Assert.True(ContainsSlice(removal.Blob, registry));
        var left = ModDataStore.Read(removal.Blob, uids);
        Assert.Equal(new[] { "divineascension_religions" }, left.Select(e => e.Key));
        Assert.Equal(new byte[] { 9 }, left[0].Value);
        // the removed uids are gone from the blob entirely: no half-cut entry is left behind
        Assert.DoesNotContain(UidA, Encoding.ASCII.GetString(removal.Blob));
        Assert.DoesNotContain(UidB, Encoding.ASCII.GetString(removal.Blob));
    }

    [Fact(DisplayName = "remove: an unknown key changes nothing (same blob, no rewrite)")]
    public void RemoveKeys_UnknownKey_IsANoOp()
    {
        var gd = Gamedata(RegistryField(), Entry("divineascension_religions", new byte[] { 9 }));
        var removal = ModDataStore.RemoveKeys(gd, new[] { "no-such-key" });

        Assert.False(removal.Changed);
        Assert.Same(gd, removal.Blob);
    }

    [Fact(DisplayName = "remove: removing every key leaves a readable world message with the other fields")]
    public void Remove_AllEntries_KeepsTheRestOfTheMessage()
    {
        var gd = Gamedata(VarintField(1, 512000), RegistryField(),
            Entry("mod_a_x", new byte[] { 1 }), Entry("mod_a_y", new byte[] { 2 }));
        var entries = ModDataStore.Read(gd);

        var removal = ModDataStore.Remove(gd, entries);

        Assert.Empty(ModDataStore.Read(removal.Blob));
        Assert.Equal(gd.Length - removal.Removed.Sum(e => e.Length), removal.Blob.Length);
        Assert.True(ContainsSlice(removal.Blob, RegistryField()));
    }

    // ------------------------- diff for the preview -------------------------

    [Fact(DisplayName = "preview: a removed key is one row '(removed)' with its size")]
    public void BlobDiff_ModData_ListsRemovedKeys()
    {
        var before = Gamedata(RegistryField(),
            Entry("divineascension_playerprogressiondata_" + UidA, new byte[] { 1, 2, 3 }),
            Entry("divineascension_religions", new byte[] { 4 }));
        var after = ModDataStore.RemoveKeys(before,
            new[] { "divineascension_playerprogressiondata_" + UidA }).Blob;

        var rows = BlobDiff.ModData("gamedata", before, after);

        var row = Assert.Single(rows);
        Assert.Equal($"mod data 'divineascension_playerprogressiondata_{UidA}'", row.Field);
        Assert.Equal("(removed)", row.NewValue);
    }

    // ------------------------- writing the row -------------------------

    [Fact(DisplayName = "write: gamedata is updated by savegameid in the COPY, and nothing else in the save changes")]
    public void CopyAndApplyChanges_WritesGamedataBySavegameId()
    {
        var src = Path.Combine(_dir, "world.vcdbs");
        var chunkBlob = new byte[] { 1, 2, 3, 4 };
        var oldGamedata = Gamedata(RegistryField(),
            Entry("divineascension_playerprogressiondata_" + UidA, new byte[] { 1, 2, 3 }),
            Entry("divineascension_religions", new byte[] { 4 }));
        var playerBlob = new byte[] { 0x0A, 0x03, 0x75, 0x69, 0x64 };

        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = src, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            c.Open();
            foreach (var sql in new[]
                     {
                         "CREATE TABLE chunk(position INTEGER PRIMARY KEY, data BLOB);",
                         "CREATE TABLE mapchunk(position INTEGER PRIMARY KEY, data BLOB);",
                         "CREATE TABLE mapregion(position INTEGER PRIMARY KEY, data BLOB);",
                         "CREATE TABLE gamedata(savegameid INTEGER PRIMARY KEY, data BLOB);",
                         "CREATE TABLE playerdata(playerid INTEGER PRIMARY KEY, playeruid TEXT, data BLOB);"
                     })
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            using var ins = c.CreateCommand();
            ins.CommandText =
                "INSERT INTO chunk(position,data) VALUES(@p,@chunk);" +
                "INSERT INTO gamedata(savegameid,data) VALUES(1,@gd);" +
                "INSERT INTO playerdata(playerid,playeruid,data) VALUES(1,@uid,@player);";
            ins.Parameters.AddWithValue("@p", (long)ChunkPosHelper.ToIndex(1, 0, 1));
            ins.Parameters.AddWithValue("@chunk", chunkBlob);
            ins.Parameters.AddWithValue("@gd", oldGamedata);
            ins.Parameters.AddWithValue("@uid", UidA);
            ins.Parameters.AddWithValue("@player", playerBlob);
            ins.ExecuteNonQuery();
        }

        var newGamedata = ModDataStore.RemoveKeys(oldGamedata,
            new[] { "divineascension_playerprogressiondata_" + UidA }).Blob;
        var (copy, backup) = VSSaveFile.CopyAndApplyChanges(src, new[] { ("gamedata", 1L, newGamedata) });

        Assert.True(File.Exists(copy));
        Assert.True(File.Exists(backup));
        using var save = VSSaveFile.OpenReadOnly(copy);
        Assert.Equal(newGamedata, save.ReadGamedata());
        var entries = ModDataStore.Read(save.ReadGamedata());
        Assert.Equal(new[] { "divineascension_religions" }.AsEnumerable(), entries.Select(e => e.Key));
        // the rest of the save is untouched
        Assert.Equal(chunkBlob, save.ReadChunk((long)ChunkPosHelper.ToIndex(1, 0, 1)));
        Assert.Single(save.PlayerRows);
        Assert.Equal(playerBlob, save.PlayerRows[0].Data);
        // the original still holds the data: only the copy was written
        using var original = VSSaveFile.OpenReadOnly(src);
        Assert.Equal(oldGamedata, original.ReadGamedata());
    }

    private static bool ContainsSlice(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }
}
