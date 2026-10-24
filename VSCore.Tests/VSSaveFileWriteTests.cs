using Microsoft.Data.Sqlite;
using Xunit;

namespace VSCore.Tests;

/// <summary>Writes into a COPY: chunk removal (task 2) and WAL/crash safety of the snapshot (task 6).
/// A synthetic save in temp is used, so the tests do not depend on real files on the machine.</summary>
public class VSSaveFileWriteTests : IDisposable
{
    private readonly string _dir;
    private readonly string _src;

    public VSSaveFileWriteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vstest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _src = Path.Combine(_dir, "world.vcdbs");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Cs(string p)
        => new SqliteConnectionStringBuilder { DataSource = p, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Creates a save in WAL mode and leaves the connection OPEN: the fresh rows live only
    /// in `-wal` (imitating a live or abruptly closed world). Returns the connection — close it in the test.</summary>
    private SqliteConnection CreateLiveWalSave(long regionPosition, params (int cx, int cy, int cz)[] chunks)
    {
        var c = new SqliteConnection(Cs(_src));
        c.Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, "PRAGMA wal_autocheckpoint=0;");
        Exec(c, "CREATE TABLE chunk(position INTEGER PRIMARY KEY, data BLOB);");
        Exec(c, "CREATE TABLE mapchunk(position INTEGER PRIMARY KEY, data BLOB);");
        Exec(c, "CREATE TABLE mapregion(position INTEGER PRIMARY KEY, data BLOB);");
        Exec(c, "CREATE TABLE gamedata(savegameid INTEGER PRIMARY KEY, data BLOB);");
        Exec(c, "CREATE TABLE playerdata(playerid INTEGER PRIMARY KEY, playeruid TEXT, data BLOB);");

        foreach (var (cx, cy, cz) in chunks)
        {
            long p = (long)ChunkPosHelper.ToIndex(cx, cy, cz);
            long mp = (long)ChunkPosHelper.ToIndex(cx, 0, cz);   // mapchunk has one row per column (Y=0)
            using var ins = c.CreateCommand();
            ins.CommandText = "INSERT INTO chunk(position,data) VALUES(@p,@d);" +
                              "INSERT OR IGNORE INTO mapchunk(position,data) VALUES(@mp,@d);";
            ins.Parameters.AddWithValue("@p", p);
            ins.Parameters.AddWithValue("@mp", mp);
            ins.Parameters.AddWithValue("@d", new byte[] { 1, 2, 3, 4 });
            ins.ExecuteNonQuery();
        }
        using (var reg = c.CreateCommand())
        {
            reg.CommandText = "INSERT INTO mapregion(position,data) VALUES(@p,@d);";
            reg.Parameters.AddWithValue("@p", regionPosition);
            reg.Parameters.AddWithValue("@d", new byte[] { 9, 9 });
            reg.ExecuteNonQuery();
        }
        return c;
    }

    [Fact(DisplayName = "chunk removal: only the selected column (all Y) disappears from the COPY, the original stays intact")]
    public void DeleteChunks_RemovesOnlySelectedColumns_FromCopy()
    {
        const long regionPos = 4242;
        using var live = CreateLiveWalSave(regionPos, (10, 0, 20), (10, 1, 20), (11, 0, 20));
        Assert.True(File.Exists(_src + "-wal"), "the save must be in the WAL state (data in -wal)");

        var (copy, backup, chunkRows, mapChunkRows) = VSSaveFile.CopyAndDeleteChunks(_src, new[] { (10, 20) });

        Assert.Equal(2, chunkRows);        // the two Y levels of the selected column
        Assert.Equal(1, mapChunkRows);     // mapchunk holds one row per column
        Assert.True(File.Exists(backup));
        Assert.False(File.Exists(copy + "-wal"), "the copy snapshot must be self-contained (no -wal)");
        Assert.False(File.Exists(backup + "-wal"), "the backup must be self-contained (no -wal)");

        using (var w = VSSaveFile.OpenReadOnly(copy))
        {
            var cols = w.Chunks.Select(x => ChunkPosHelper.DecodeIndex((ulong)x.Position)).ToList();
            Assert.DoesNotContain(cols, p => p.X == 10 && p.Z == 20);
            Assert.Contains(cols, p => p.X == 11 && p.Z == 20);
        }

        // the backup is the state BEFORE the removal, WAL data included
        using (var b = VSSaveFile.OpenReadOnly(backup))
            Assert.Equal(3, b.Chunks.Count);

        // the original is unchanged
        using (var orig = VSSaveFile.OpenReadOnly(_src))
        {
            Assert.Equal(3, orig.Chunks.Count);
            Assert.Contains(orig.MapRegions, r => r.Position == regionPos);
        }
    }

    [Fact(DisplayName = "chunk removal: an empty selection throws")]
    public void DeleteChunks_EmptySelection_Throws()
    {
        using var live = CreateLiveWalSave(1, (1, 0, 1));
        Assert.Throws<ArgumentException>(() => VSSaveFile.CopyAndDeleteChunks(_src, Array.Empty<(int, int)>()));
    }
}
