using Microsoft.Data.Sqlite;
using Xunit;

namespace VSCore.Tests;

/// <summary>
///     An editing session: a working copy is opened, edits are applied to it, the original is not
///     modified, and SaveCopyTo writes a separate .vcdbs next to it. Previously every write created a new
///     copy from the original, so a loaded save did not show the edits and the history was wiped immediately.
/// </summary>
public class WorkingCopyTests : IDisposable
{
    private readonly string _dir;
    private readonly string _src;
    private readonly long _chunkPos = (long)ChunkPosHelper.ToIndex(3, 4, 5);

    public WorkingCopyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vswc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _src = Path.Combine(_dir, "world.vcdbs");
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _src, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        c.Open();
        Exec(c, "CREATE TABLE chunk(position INTEGER PRIMARY KEY, data BLOB);");
        Exec(c, "CREATE TABLE mapchunk(position INTEGER PRIMARY KEY, data BLOB);");
        Exec(c, "CREATE TABLE gamedata(savegameid INTEGER PRIMARY KEY, data BLOB);");
        Exec(c, "CREATE TABLE playerdata(playerid INTEGER PRIMARY KEY, playeruid TEXT, data BLOB);");
        using (var ins = c.CreateCommand())
        {
            ins.CommandText =
                "INSERT INTO chunk(position,data) VALUES(@p,@d);" +
                "INSERT INTO mapchunk(position,data) VALUES(@mp,@d);" +
                "INSERT INTO playerdata(playerid,playeruid,data) VALUES(7,'uid-7',@pd);" +
                "INSERT INTO gamedata(savegameid,data) VALUES(1,@g);";
            ins.Parameters.AddWithValue("@p", _chunkPos);
            ins.Parameters.AddWithValue("@mp", (long)ChunkPosHelper.ToIndex(3, 0, 5));
            ins.Parameters.AddWithValue("@d", new byte[] { 1, 2, 3 });
            ins.Parameters.AddWithValue("@pd", new byte[] { 7, 7 });
            ins.Parameters.AddWithValue("@g", new byte[] { 9 });
            ins.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact(DisplayName = "OpenForEditing: edits go to the working copy, the original does not change")]
    public void OpenForEditing_AppliesOnlyToWorkingCopy()
    {
        using var save = VSSaveFile.OpenForEditing(_src);
        Assert.Equal(_src, save.SourcePath);
        Assert.NotEqual(Path.GetFullPath(_src), Path.GetFullPath(save.Path));
        Assert.Equal(3, save.ReadChunk(_chunkPos)!.Length);

        var rows = save.ApplyChanges(new[] { ("chunk", _chunkPos, new byte[] { 4, 5, 6, 7 }) });
        Assert.Equal(1, rows);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, save.ReadChunk(_chunkPos));

        // the working copy is a separate file and the original stays byte-for-byte as it was
        using var original = VSSaveFile.OpenReadOnly(_src);
        Assert.Equal(new byte[] { 1, 2, 3 }, original.ReadChunk(_chunkPos));
    }

    [Fact(DisplayName = "DeleteChunks: deletes the column only in the working copy, the original stays intact")]
    public void DeleteChunks_AppliesToWorkingCopyOnly()
    {
        using var save = VSSaveFile.OpenForEditing(_src);
        var (chunkRows, mapRows) = save.DeleteChunks(new[] { (3, 5) });

        Assert.Equal(1, chunkRows);
        Assert.Equal(1, mapRows);
        Assert.Empty(save.Chunks);
        Assert.Empty(save.MapchunkRowsWithData);

        using var original = VSSaveFile.OpenReadOnly(_src);
        Assert.Single(original.Chunks);
        Assert.Single(original.MapchunkRowsWithData);
    }

    [Fact(DisplayName = "DeleteChunks + SaveCopyTo: the deletion lands in the saved copy, the original stays intact")]
    public void DeleteChunks_ThenSaveCopy_KeepsDeletionInSavedFile()
    {
        var dest = Path.Combine(_dir, "deleted (edited).vcdbs");
        using (var save = VSSaveFile.OpenForEditing(_src))
        {
            save.DeleteChunks(new[] { (3, 5) });
            save.SaveCopyTo(dest);
        }

        using (var copy = VSSaveFile.OpenReadOnly(dest))
        {
            Assert.Empty(copy.Chunks);
            Assert.Empty(copy.MapchunkRowsWithData);
        }

        using var original = VSSaveFile.OpenReadOnly(_src);
        Assert.Single(original.Chunks);
        Assert.Single(original.MapchunkRowsWithData);
    }

    [Fact(DisplayName = "RemoveBlocks: a read-only save cannot be edited")]
    public void RemoveBlocks_OnReadOnlyOpen_Throws()
    {
        using var save = VSSaveFile.OpenReadOnly(_src);
        Assert.Throws<InvalidOperationException>(() => save.RemoveBlocks(new[] { 1 }));
        Assert.Throws<InvalidOperationException>(() => save.ReplaceBlocks(new[] { 1 }, 2));
        Assert.Throws<InvalidOperationException>(() => save.DeleteChunks(new[] { (3, 5) }));
    }

    [Fact(DisplayName = "ApplyChanges: playerdata is updated by playerid")]
    public void ApplyChanges_UpdatesPlayerData()
    {
        using var save = VSSaveFile.OpenForEditing(_src);
        save.ApplyChanges(new[] { ("playerdata", 7L, new byte[] { 8, 9, 10 }) });
        var row = save.PlayerRows.Single(r => r.Id == 7);
        Assert.Equal(new byte[] { 8, 9, 10 }, row.Data);

        using var original = VSSaveFile.OpenReadOnly(_src);
        Assert.Equal(new byte[] { 7, 7 }, original.PlayerRows.Single(r => r.Id == 7).Data);
    }

    [Fact(DisplayName = "SaveCopyTo: writes a self-contained file; the original cannot be overwritten")]
    public void SaveCopyTo_WritesNextToOriginal_AndRefusesOriginal()
    {
        using var save = VSSaveFile.OpenForEditing(_src);
        save.ApplyChanges(new[] { ("chunk", _chunkPos, new byte[] { 11, 12 }) });
        var dest = Path.Combine(_dir, "world (edited).vcdbs");

        var saved = save.SaveCopyTo(dest);

        Assert.Equal(Path.GetFullPath(dest), saved);
        Assert.True(File.Exists(dest));
        Assert.False(File.Exists(dest + "-wal"));
        Assert.False(File.Exists(dest + "-shm"));
        using (var copy = VSSaveFile.OpenReadOnly(dest))
            Assert.Equal(new byte[] { 11, 12 }, copy.ReadChunk(_chunkPos));
        using (var original = VSSaveFile.OpenReadOnly(_src))
            Assert.Equal(new byte[] { 1, 2, 3 }, original.ReadChunk(_chunkPos));

        Assert.Throws<InvalidOperationException>(() => save.SaveCopyTo(_src));
        Assert.Equal(new byte[] { 1, 2, 3 }, new OpenReadOnlyProbe(_src).Chunk(_chunkPos));
    }

    [Fact(DisplayName = "ApplyChanges: a read-only open cannot be edited")]
    public void ApplyChanges_OnReadOnlyOpen_Throws()
    {
        using var save = VSSaveFile.OpenReadOnly(_src);
        Assert.Throws<InvalidOperationException>(() =>
            save.ApplyChanges(new[] { ("chunk", _chunkPos, new byte[] { 1 }) }));
    }

    [Fact(DisplayName = "SaveCopyTo: stale -wal/-shm files are not left next to the destination")]
    public void SaveCopyTo_RemovesStaleWalNextToDestination()
    {
        var dest = Path.Combine(_dir, "copy.vcdbs");
        File.WriteAllBytes(dest + "-wal", new byte[] { 1, 2, 3 });
        File.WriteAllBytes(dest + "-shm", new byte[] { 4, 5 });
        using var save = VSSaveFile.OpenForEditing(_src);
        save.SaveCopyTo(dest);
        Assert.False(File.Exists(dest + "-wal"));
        Assert.False(File.Exists(dest + "-shm"));
        using var copy = VSSaveFile.OpenReadOnly(dest);
        Assert.Equal(new byte[] { 1, 2, 3 }, copy.ReadChunk(_chunkPos));
    }

    [Fact(DisplayName = "OpenForEditing: Dispose removes the temporary working copy directory")]
    public void Dispose_RemovesWorkingDirectory()
    {
        var save = VSSaveFile.OpenForEditing(_src);
        var workDir = Path.GetDirectoryName(save.Path)!;
        Assert.True(Directory.Exists(workDir));
        save.Dispose();
        Assert.False(Directory.Exists(workDir));
        Assert.True(File.Exists(_src));
    }

    /// <summary>A small helper so that checking the original after Dispose does not keep the object alive.</summary>
    private sealed class OpenReadOnlyProbe
    {
        private readonly VSSaveFile _save;
        public OpenReadOnlyProbe(string path) => _save = VSSaveFile.OpenReadOnly(path);
        public byte[]? Chunk(long p) { using (_save) return _save.ReadChunk(p); }
    }
}
