using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;

namespace VSCore.Tests;

/// <summary>
/// `-wal`/`-shm` next to the save. A reader makes SQLite create them (a WAL database cannot be read without
/// the SHM index), and SQLite removes them itself only on the last close *with write access* — so a read-only
/// session used to leave a `-shm` and an empty `-wal` behind after every look at a world.
/// The editor now closes for real (no pooling) and removes the files it created, but never touches a WAL that
/// was already there or that carries frames: those belong to a writer, and the frames are committed data.
/// </summary>
public class WalCleanupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vswal_" + Guid.NewGuid().ToString("N"));

    public WalCleanupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>A save-shaped file in a folder of its own: one `chunk` table is enough for the read path.</summary>
    private string MakeSave(string name = "save.vcdbs")
    {
        var path = Path.Combine(_dir, name);
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE chunk(position INTEGER PRIMARY KEY, data BLOB);" +
                                  "INSERT INTO chunk VALUES(1, x'00'), (2, x'11');";
                cmd.ExecuteNonQuery();
            }
        }
        // a clean close checkpoints and removes the WAL: the save is back to "just a file in WAL mode"
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
        return path;
    }

    [Fact]
    public void ReaderCreatedWal_IsRemovedOnClose()
    {
        var path = MakeSave();
        var save = VSSaveFile.OpenReadOnly(path);
        Assert.Equal(2, save.ChunkRowsWithData.Count); // a real read: this is what creates the WAL index
        Assert.True(File.Exists(path + "-shm"), "the reader should create the SHM index");
        save.Dispose();
        Assert.False(File.Exists(path + "-wal"), "-wal created by the reader must not outlive the session");
        Assert.False(File.Exists(path + "-shm"), "-shm created by the reader must not outlive the session");
    }

    [Fact]
    public void ForeignWal_IsKept()
    {
        var path = MakeSave();
        File.WriteAllBytes(path + "-wal", Array.Empty<byte>()); // someone else's file, even an empty one
        File.WriteAllBytes(path + "-shm", new byte[32768]);
        using (var save = VSSaveFile.OpenReadOnly(path))
            Assert.Equal(2, save.ChunkRowsWithData.Count);
        Assert.True(File.Exists(path + "-wal"), "a WAL that was there before the session is not ours to delete");
        Assert.True(File.Exists(path + "-shm"));
    }

    [Fact]
    public void TwoSessionsOnOneSave_RemoveTheFilesOnlyWhenBothClose()
    {
        var path = MakeSave();
        var first = VSSaveFile.OpenReadOnly(path);
        Assert.Equal(2, first.ChunkRowsWithData.Count);
        var second = VSSaveFile.OpenReadOnly(path);
        Assert.Equal(2, second.ChunkRowsWithData.Count);

        first.Dispose();
        Assert.True(File.Exists(path + "-shm"), "the second session is still reading this save");

        second.Dispose();
        Assert.False(File.Exists(path + "-wal"), "the pair created by the first session goes with the last one");
        Assert.False(File.Exists(path + "-shm"));
    }

    [Fact]
    public void WalWithFrames_IsKept()
    {
        var path = MakeSave();
        // a live writer (the game) keeps the WAL open and holds frames in it
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        writer.Open();
        using (var cmd = writer.CreateCommand())
        {
            cmd.CommandText = "PRAGMA wal_autocheckpoint=0; INSERT INTO chunk VALUES(3, randomblob(4096));";
            cmd.ExecuteNonQuery();
        }
        var frames = new FileInfo(path + "-wal").Length;
        Assert.True(frames > 0, "the writer should have left frames in the WAL");

        using (var save = VSSaveFile.OpenReadOnly(path))
            Assert.Equal(3, save.ChunkRowsWithData.Count);

        Assert.True(File.Exists(path + "-wal"), "a WAL with frames is committed data: it must survive our exit");
        Assert.Equal(frames, new FileInfo(path + "-wal").Length);
    }
}
