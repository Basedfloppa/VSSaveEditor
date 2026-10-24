using Microsoft.Data.Sqlite;
using VSCore.Format;

namespace VSCore;

public sealed record MapRegionInfo(long Position, int DataLength);
public sealed record ChunkInfo(long Position, int DataLength);

/// <summary>
/// Access to VS save tables (.vcdbs) and metadata.
/// <see cref="OpenReadOnly"/> never modifies the file; for edits <see cref="OpenForEditing"/>
/// works on a throwaway working copy, while <see cref="SaveCopyTo"/> writes a separate file next to it.
/// </summary>
public sealed class VSSaveFile : IDisposable
{
    private readonly SqliteConnection _conn;
    private BlockRegistry? _blockRegistry;
    private ItemRegistry? _itemRegistry;

    /// <summary>The file that is actually open. For an editing session this is the working copy, not the user's save.</summary>
    public string Path { get; }

    /// <summary>The source file the user opened. null — the object was opened directly (read-only).</summary>
    public string? SourcePath { get; }

    /// <summary>Directory of the throwaway working copy: deleted together with the object.</summary>
    private string? _workingDir;

    /// <summary>Block registry (block id -> code) from gamedata, parsed lazily.</summary>
    public BlockRegistry BlockRegistry
    {
        get
        {
            if (_blockRegistry is null)
                _blockRegistry = BlockRegistry.FromGamedata(ReadGamedata() ?? Array.Empty<byte>());
            return _blockRegistry;
        }
    }

    /// <summary>Item registry (item id -> code) from the same gamedata section as BlockIDs.
    /// Needed by the inventories: a stack carries no item code, only class+id.</summary>
    public ItemRegistry ItemRegistry
    {
        get
        {
            if (_itemRegistry is null)
                _itemRegistry = ItemRegistry.FromGamedata(ReadGamedata() ?? Array.Empty<byte>());
            return _itemRegistry;
        }
    }

    private VSSaveFile(string path, SqliteConnection conn, string? sourcePath = null, string? workingDir = null)
    {
        Path = path;
        _conn = conn;
        SourcePath = sourcePath;
        _workingDir = workingDir;
    }

    /// <summary>
    ///     Opens the save for editing: the original is read only once, and all work happens on a throwaway
    ///     working copy. Otherwise every edit would create a new copy while the save loaded in the editor
    ///     stayed the old one — that is, the edit history and the view would diverge from what the
    ///     user sees.
    /// </summary>
    public static VSSaveFile OpenForEditing(string path)
    {
        // No .bak is needed for the working copy: the original does not change anyway, while an extra full copy
        // of a 500-MB save is noticeable both on disk and in opening time.
        var (dir, copy, _) = CreateWorkingCopy(path, withBackup: false);
        try
        {
            return new VSSaveFile(copy, OpenConnection(copy, SqliteOpenMode.ReadWrite), path, dir);
        }
        catch
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
            throw;
        }
    }

    /// <summary>
    ///     Applies changes to the WORKING COPY in one transaction. A separate connection is opened:
    ///     the background scan is reading the file through the main one, and one SQLite connection cannot
    ///     be used from two threads at once.
    /// </summary>
    public int ApplyChanges(IEnumerable<(string table, long position, byte[] data)> changes)
    {
        if (SourcePath is null) throw new InvalidOperationException("this save is not editable (open it with OpenForEditing)");
        var list = changes.ToList();
        if (list.Count == 0) return 0;
        foreach (var (table, _, _) in list) EnsureSafeTableName(table);

        using var conn = OpenWriteConnection();
        using var txn = conn.BeginTransaction();
        using var up = conn.CreateCommand();
        up.Transaction = txn;
        var dp = up.Parameters.Add("@d", SqliteType.Blob);
        var pp = up.Parameters.Add("@p", SqliteType.Integer);
        var applied = 0;
        foreach (var (table, pos, data) in list)
        {
            var key = table == "playerdata" ? "playerid" : "position";
            up.CommandText = $"UPDATE \"{table}\" SET data=@d WHERE {key}=@p;";
            dp.Value = data;
            pp.Value = pos;
            applied += up.ExecuteNonQuery();
        }
        txn.Commit();
        return applied;
    }

    /// <summary>
    ///     Connection for a targeted edit of the working copy. Separate from the main one: the background scan
    ///     reads the file in parallel, and one SQLite connection cannot be used from two threads.
    /// </summary>
    private SqliteConnection OpenWriteConnection()
    {
        if (SourcePath is null) throw new InvalidOperationException("this save is not editable (open it with OpenForEditing)");
        var conn = OpenConnection(Path, SqliteOpenMode.ReadWrite);
        try
        {
            using var pragma = conn.CreateCommand();
            // the background scan may hold short read locks; we wait instead of failing with "database is locked"
            pragma.CommandText = "PRAGMA busy_timeout=30000;";
            pragma.ExecuteNonQuery();
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Saves the working copy into a separate self-contained file (without `-wal`/`-shm`) by atomic
    ///     publication through `<dest>.tmp` + `Move`. The original must not be touched: writing to it is an error.
    /// </summary>
    public string SaveCopyTo(string destination)
    {
        if (SourcePath is null) throw new InvalidOperationException("this save is not a working copy");
        var dest = System.IO.Path.GetFullPath(destination);
        if (SamePath(dest, SourcePath))
            throw new InvalidOperationException("refusing to overwrite the original save; choose another file name");
        var dir = System.IO.Path.GetDirectoryName(dest);
        if (string.IsNullOrEmpty(dir)) throw new ArgumentException("destination has no directory", nameof(destination));
        System.IO.Directory.CreateDirectory(dir);
        var tmp = dest + ".tmp";
        try
        {
            foreach (var stale in new[] { tmp, dest + "-wal", dest + "-shm" })
                if (System.IO.File.Exists(stale)) { try { System.IO.File.Delete(stale); } catch { } }
            using (var src = OpenConnection(Path, SqliteOpenMode.ReadOnly))
            using (var dst = OpenConnection(tmp, SqliteOpenMode.ReadWriteCreate))
                src.BackupDatabase(dst);
            using (var dst = OpenConnection(tmp, SqliteOpenMode.ReadWrite))
            {
                using (var j = dst.CreateCommand()) { j.CommandText = "PRAGMA journal_mode=DELETE;"; j.ExecuteScalar(); }
                try { using var ck = dst.CreateCommand(); ck.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);"; ck.ExecuteScalar(); } catch { }
            }
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var s = tmp + suffix;
                if (System.IO.File.Exists(s)) { try { System.IO.File.Delete(s); } catch { } }
            }
            System.IO.File.Move(tmp, dest, true);
            // an old destination file may still have its own -wal/-shm; they are incompatible with the new
            // main part and would apply foreign frames when opened
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var s = dest + suffix;
                if (System.IO.File.Exists(s)) { try { System.IO.File.Delete(s); } catch { } }
            }
            return dest;
        }
        catch (Exception ex)
        {
            try { System.IO.File.Delete(tmp); } catch { }
            if (ex is InvalidOperationException or ArgumentException) throw;
            // on a foreign/unavailable folder SQLite simply reports "unable to open database file" —
            // the UI needs the path, otherwise it is unclear which copy failed to be written
            throw new InvalidOperationException($"cannot save the working copy to '{dest}': {ex.Message}", ex);
        }
    }

    private static bool SamePath(string a, string b)
    {
        return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    ///     Removes the selected chunk columns (all Y) from the WORKING COPY together with their map data.
    ///     The original is not touched: the object was opened through <see cref="OpenForEditing"/>.
    /// </summary>
    public (int ChunkRows, int MapChunkRows) DeleteChunks(IReadOnlyCollection<(int Cx, int Cz)> columns)
    {
        if (columns.Count == 0) throw new ArgumentException("no chunks selected", nameof(columns));
        var wanted = new HashSet<(int, int)>(columns);
        using var conn = OpenWriteConnection();
        using var txn = conn.BeginTransaction();
        int chunkRows = DeleteChunkColumns(conn, txn, "chunk", wanted);
        int mapRows = TableExists(conn, txn, "mapchunk") ? DeleteChunkColumns(conn, txn, "mapchunk", wanted) : 0;
        txn.Commit();
        return (chunkRows, mapRows);
    }

    /// <summary>
    ///     Clears the selected block ids in the WORKING COPY (the cells become air). The original
    ///     does not change; the result is a <see cref="BlockRemovalOutcome"/> with the working copy and original paths.
    /// </summary>
    public BlockRemovalOutcome RemoveBlocks(IReadOnlyCollection<int> blockIds,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (blockIds.Count == 0) throw new ArgumentException("no block ids", nameof(blockIds));
        using var conn = OpenWriteConnection();
        var stats = RewriteBlocksCore(conn, blockIds, 0, progress, ct);
        return new BlockRemovalOutcome(Path, SourcePath ?? "", stats.Scanned, stats.Changed,
            stats.Fields, stats.Cleared, stats.BytesBefore, stats.BytesAfter);
    }

    /// <summary>
    ///     Replaces the selected block ids with <paramref name="targetId" /> in the WORKING COPY (0 — air).
    ///     The original does not change. Only the palette changes, so the remaining chunk fields stay
    ///     untouched byte for byte (see <see cref="ChunkBlockRewriter.ReplaceBlockIds" />).
    /// </summary>
    public BlockReplaceOutcome ReplaceBlocks(IReadOnlyCollection<int> blockIds, int targetId,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (blockIds.Count == 0) throw new ArgumentException("no block ids", nameof(blockIds));
        using var conn = OpenWriteConnection();
        var stats = RewriteBlocksCore(conn, blockIds, targetId, progress, ct);
        return new BlockReplaceOutcome(Path, SourcePath ?? "", stats.Scanned, stats.Changed,
            stats.Fields, stats.Cleared, stats.BytesBefore, stats.BytesAfter);
    }

    public static VSSaveFile OpenReadOnly(string path)
    {
        try
        {
            return new VSSaveFile(path, OpenConnection(path, SqliteOpenMode.ReadOnly));
        }
        catch (SqliteException)
        {
            // A read-only WAL may fail to open if there is no readable `-shm` next to it (the world is not stopped,
            // foreign permissions). We then work on a consistent copy of the file set — the original is not touched.
            if (!System.IO.File.Exists(path + "-wal") && !System.IO.File.Exists(path + "-shm")) throw;
            var (dir, copy, _) = CreateWorkingCopy(path, withBackup: false);
            return new VSSaveFile(path, OpenConnection(copy, SqliteOpenMode.ReadWrite), workingDir: dir);
        }
    }

    private static SqliteConnection OpenConnection(string path, SqliteOpenMode mode)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = mode == SqliteOpenMode.ReadOnly ? SqliteCacheMode.Shared : SqliteCacheMode.Default
        };
        // DataSource is not a parameter but connection-string text. If the path contains `;` or a quote,
        // we check that the path stayed exactly the same after assembly: otherwise there is a risk of connecting
        // to the wrong file — the one slipped in by the name's "tail".
        var connStr = cs.ToString();
        if (new SqliteConnectionStringBuilder(connStr).DataSource != path)
            throw new InvalidOperationException($"unsafe save path: {path}");
        var conn = new SqliteConnection(connStr);
        conn.Open();
        return conn;
    }

    /// <summary>The table name is inserted into the SQL as text (an identifier cannot be a parameter),
    /// so only a safe identifier is allowed through. Also needed for the public
    /// <see cref="CopyAndApplyChanges"/>, where the caller supplies the table name.</summary>
    public static void EnsureSafeTableName(string table)
    {
        if (string.IsNullOrEmpty(table) || char.IsAsciiDigit(table[0]))
            throw new ArgumentException($"unsafe table name: '{table}'", nameof(table));
        foreach (var ch in table)
            if (ch != '_' && !char.IsAsciiLetterOrDigit(ch))
                throw new ArgumentException($"unsafe table name: '{table}'", nameof(table));
    }

    /// <summary>File name of the working copy: the basename of the source save, but only a safe one.
    /// An empty name and `..`/separators would take the copy outside the temp directory, hence the fallback.</summary>
    public static string SafeCopyFileName(string srcPath)
    {
        var name = System.IO.Path.GetFileName(srcPath);
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(new[] { '/', '\\' }) >= 0)
            return "save.vcdbs";
        return name;
    }

    /// <summary>
    /// A consistent working snapshot of the save in a new temp folder + a backup BEFORE any edits.
    /// The snapshot is taken through the SQLite Online Backup API, so it also includes uncheckpointed data
    /// from `-wal`; if a read-only WAL is unavailable — by copying the file set and then
    /// folding the WAL into the main file. The result is one self-contained file (without `-wal`/`-shm`),
    /// and the `.bak` is published atomically (`tmp` + `Move`): a crash of the editor leaves no
    /// "half-written" backup, and the original was not modified in any case.
    /// </summary>
    private static (string dir, string copy, string backup) CreateWorkingCopy(string srcPath, bool withBackup = true)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vseditor_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var copy = System.IO.Path.Combine(dir, SafeCopyFileName(srcPath));
        var backup = copy + ".bak";
        try
        {
            var snapshotted = false;
            try
            {
                using (var src = OpenConnection(srcPath, SqliteOpenMode.ReadOnly))
                using (var dst = OpenConnection(copy, SqliteOpenMode.ReadWriteCreate))
                    src.BackupDatabase(dst);
                snapshotted = true;
            }
            catch (SqliteException) { /* fallback below: copy the files as they are */ }

            if (!snapshotted)
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var s = srcPath + suffix;
                    if (System.IO.File.Exists(s))
                        System.IO.File.Copy(s, System.IO.Path.Combine(dir, System.IO.Path.GetFileName(s)), true);
                }
                if (!System.IO.File.Exists(copy))
                    throw new InvalidOperationException($"cannot snapshot save: {srcPath}");
            }

            // fold the WAL into the main file: the copy and the .bak must be self-contained
            using (var dst = OpenConnection(copy, SqliteOpenMode.ReadWrite))
            {
                using (var j = dst.CreateCommand()) { j.CommandText = "PRAGMA journal_mode=DELETE;"; j.ExecuteScalar(); }
                try { using var ck = dst.CreateCommand(); ck.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);"; ck.ExecuteScalar(); } catch { }
            }
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var s = copy + suffix;
                if (System.IO.File.Exists(s)) { try { System.IO.File.Delete(s); } catch { } }
            }

            // the backup is taken BEFORE the edits — atomic publication.
            // Without a WAL we copy the original byte for byte (the usual .bak); with a WAL — a consistent snapshot,
            // otherwise a raw copy of the main file would lose the uncheckpointed data.
            if (withBackup)
            {
                bool hasWal = System.IO.File.Exists(srcPath + "-wal") || System.IO.File.Exists(srcPath + "-shm");
                var tmpBackup = backup + ".tmp";
                System.IO.File.Copy(hasWal ? copy : srcPath, tmpBackup, true);
                System.IO.File.Move(tmpBackup, backup, true);
            }
            return (dir, copy, backup);
        }
        catch
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
            throw;
        }
    }

    /// <summary>Removes temporary working copies left behind by a crashed editor
    /// (older than `olderThan`, 3 days by default). Fresh directories are left alone — another running
    /// instance may be working in them.</summary>
    public static void CleanupStaleWorkingCopies(TimeSpan? olderThan = null)
    {
        try
        {
            var cutoff = DateTime.UtcNow - (olderThan ?? TimeSpan.FromDays(3));
            foreach (var d in System.IO.Directory.GetDirectories(System.IO.Path.GetTempPath(), "vseditor_*"))
            {
                try { if (System.IO.Directory.GetCreationTimeUtc(d) < cutoff) System.IO.Directory.Delete(d, true); }
                catch { }
            }
        }
        catch { /* cleanup must not get in the way of startup */ }
    }

    /// <summary>Resolution (schema_name.table_name) of the save tables.</summary>
    public IReadOnlyList<string> TableNames
    {
        get
        {
            var list = new List<string>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name!='sqlite_sequence' ORDER BY name;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    private List<T> ReadRows<T>(string table, Func<long, int, T> map)
    {
        EnsureSafeTableName(table);
        var list = new List<T>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT position, length(data) FROM \"{table}\" ORDER BY position;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(map(r.GetInt64(0), (int)(r.IsDBNull(1) ? 0 : r.GetInt64(1))));
        return list;
    }

    public List<MapRegionInfo> MapRegions => ReadRows("mapregion", (p, l) => new MapRegionInfo(p, l));
    public List<ChunkInfo> Chunks => ReadRows("chunk", (p, l) => new ChunkInfo(p, l));

    public int CountRows(string table)
    {
        EnsureSafeTableName(table);
        using var c = _conn.CreateCommand();
        c.CommandText = $"SELECT count(*) FROM \"{table}\";";
        return Convert.ToInt32(c.ExecuteScalar());
    }

    public struct PlayerRow { public long Id; public string? Uid; public byte[] Data; }
    /// <summary>Rows of the playerdata table (characters) with raw data.</summary>
    public List<PlayerRow> PlayerRows
    {
        get
        {
            var list = new List<PlayerRow>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT playerid, playeruid, data FROM playerdata;";
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new PlayerRow
                {
                    Id = r.GetInt64(0),
                    Uid = r.IsDBNull(1) ? null : r.GetString(1),
                    Data = r.IsDBNull(2) ? Array.Empty<byte>() : (byte[])r.GetValue(2)
                });
            return list;
        }
    }

    public long? GamedataLength
    {
        get { using var c = _conn.CreateCommand(); c.CommandText = "SELECT length(data) FROM gamedata WHERE savegameid=1;"; return (long?)c.ExecuteScalar(); }
    }

    public struct ChunkRow { public long Position; public byte[] Data; }
    /// <summary>Chunks with data (for building the terrain map).</summary>
    public List<ChunkRow> ChunkRowsWithData => ChunkRowsInWindow(null);

    /// <summary>
    ///     Chunk rows filtered by the columns of an area (<paramref name="area" /> = null — all).
    ///     Chunk blobs outside the window are not read from the DB at all: without the filter, a window for one
    ///     area would pull the data of the whole save into memory (155,584 rows against 16,144 in a two-area save).
    /// </summary>
    public List<ChunkRow> ChunkRowsInWindow(ChunkArea? area)
    {
        var list = new List<ChunkRow>();
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT position, data FROM chunk ORDER BY position;";
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            long position = r.GetInt64(0);
            if (area is not null)
            {
                var (cx, _, cz) = ChunkPosHelper.DecodeIndex((ulong)position);
                if (!area.ContainsChunk(cx, cz)) continue;
            }

            list.Add(new ChunkRow
            {
                Position = position,
                Data = r.IsDBNull(1) ? Array.Empty<byte>() : (byte[])r.GetValue(1)
            });
        }

        return list;
    }

    /// <summary>Positions of all `chunk` rows (no data is read) — the map areas are computed from them.</summary>
    public List<long> ChunkPositions
    {
        get
        {
            var list = new List<long>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT position FROM chunk ORDER BY position;";
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(r.GetInt64(0));
            return list;
        }
    }

    /// <summary>Rows of the mapchunk table (the client map) with data.</summary>
    public List<ChunkRow> MapchunkRowsWithData
    {
        get
        {
            var list = new List<ChunkRow>();
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT position, data FROM mapchunk ORDER BY position;";
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new ChunkRow { Position = r.GetInt64(0), Data = r.IsDBNull(1) ? Array.Empty<byte>() : (byte[])r.GetValue(1) });
            return list;
        }
    }

    public byte[]? ReadRegion(long position)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT data FROM mapregion WHERE position=@p;";
        c.Parameters.AddWithValue("@p", position);
        var o = c.ExecuteScalar();
        return o is byte[] b ? b : (o is null ? null : System.Text.Encoding.Default.GetBytes(o.ToString()!));
    }

    /// <summary>Raw bytes of a particular chunk (for the structural view/editing).</summary>
    public byte[]? ReadChunk(long position)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT data FROM chunk WHERE position=@p;";
        c.Parameters.AddWithValue("@p", position);
        var o = c.ExecuteScalar();
        return o as byte[];
    }

    public byte[]? ReadGamedata()
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT data FROM gamedata WHERE savegameid=1;";
        var o = c.ExecuteScalar();
        return o as byte[];
    }

    /// <summary>
    ///     World size in blocks (worldWidth/worldLength from gamedata). Needed not for drawing but for
    ///     choosing the starting map area: the world spawn is its centre, and that is where the map should open,
    ///     not on the largest patch of chunks (a pregen at the world edge can be larger than the settled area). Best-effort.
    /// </summary>
    public (int Width, int Length)? DetectWorldSize()
    {
        var g = ReadGamedata();
        if (g is null) return null;
        var w = FindSizeAfterKey(g, "worldWidth");
        var l = FindSizeAfterKey(g, "worldLength");
        return w is int vw && l is int vl ? (vw, vl) : null;
    }

    /// <summary>
    ///     Fraction of the year (0…1) from the save calendar — needed by the seasonal map tinting.
    ///     The game computes `TotalDays = TotalGameSeconds / 3600 / HoursPerDay` (gamedata fields 12 and 25),
    ///     and the days in a year as `daysPerMonth * 12` from WorldConfiguration (30·12=360 in most worlds).
    ///     null — the save has no calendar (nothing to tint).
    /// </summary>
    public double? DetectYearRel()
    {
        var g = ReadGamedata();
        return g is { Length: > 0 } ? YearRelFromGamedata(g) : null;
    }

    /// <summary>Parsing of the calendar from gamedata (extracted from <see cref="DetectYearRel"/> for the test).</summary>
    public static double? YearRelFromGamedata(byte[] g)
    {
        if (g.Length == 0) return null;
        long totalSeconds = 0;
        float hoursPerDay = 24f;
        foreach (var f in VSCore.Format.PbReader.ReadMessage(g, maxDepth: 1, maxFields: 200))
        {
            if (f.Field == 12 && f.WireType == 0 && f.IntValue > 0) totalSeconds = f.IntValue;
            else if (f.Field == 25 && f.WireType == 5 && f.FloatValue > 0 && f.FloatValue <= 48) hoursPerDay = f.FloatValue;
        }
        if (totalSeconds <= 0) return null;
        int daysPerMonth = FindIntAfterKey(g, "daysPerMonth", 1, 365) ?? 30;
        int daysPerYear = daysPerMonth * 12;
        double totalDays = totalSeconds / 3600.0 / hoursPerDay;
        double rel = totalDays % daysPerYear / daysPerYear;
        return rel < 0 ? rel + 1 : rel;
    }

    /// <summary>
    ///     Y of the world spawn point (gamedata field 40, nested field 2). Spawn is placed at the surface
    ///     near sea level, so this is a second (and more reliable than the water mode) reference point for
    ///     sea level: in a world without an ocean the water mode is a lake, while spawn is still at the sea.
    /// </summary>
    public int? DetectSpawnY()
        => ReadGamedata() is { Length: > 0 } g ? SpawnYFromGamedata(g) : null;

    /// <summary>Parsing of the spawn Y from gamedata (extracted for the test).</summary>
    public static int? SpawnYFromGamedata(byte[] g)
    {
        if (g.Length == 0) return null;
        foreach (var f in VSCore.Format.PbReader.ReadMessage(g, maxDepth: 1, maxFields: 200))
        {
            if (f.Field != 40 || f.WireType != 2 || f.Bytes is null) continue;
            foreach (var inner in VSCore.Format.PbReader.ReadMessage(f.Bytes, maxDepth: 0, maxFields: 16))
                if (inner.Field == 2 && inner.WireType == 0 && inner.IntValue is > 0 and < 16384)
                    return (int)inner.IntValue;
        }
        return null;
    }

    /// <summary>An integer right after an ASCII key in gamedata (TreeAttribute writes `key + length + value`).
    /// We allow up to three bytes between the key and the digits — as in <see cref="FindSizeAfterKey"/>.</summary>
    private static int? FindIntAfterKey(byte[] blob, string key, int min, int max)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(key);
        for (int i = 0; i + needle.Length + 1 < blob.Length; i++)
        {
            if (!blob.AsSpan(i).StartsWith(needle)) continue;
            if (i > 0 && char.IsAsciiLetter((char)blob[i - 1])) continue;
            if (char.IsAsciiLetter((char)blob[i + needle.Length])) continue;
            int p = i + needle.Length;
            for (int skip = 0; skip < 3 && p < blob.Length && !char.IsAsciiDigit((char)blob[p]); skip++) p++;
            long v = 0;
            int digits = 0;
            while (p < blob.Length && digits < 4 && char.IsAsciiDigit((char)blob[p]))
            {
                v = v * 10 + (blob[p] - '0');
                digits++;
                p++;
            }
            if (digits > 0 && v >= min && v <= max) return (int)v;
        }
        return null;
    }

    /// <summary>
    ///     A number right after an ASCII key in gamedata. A VS world writes attributes as "key + length + string"
    ///     (`worldWidth\x06512000`), but we also accept relaxed JSON (`"worldWidth": 512000`), so we skip
    ///     up to four bytes between the key and the digits. The value is sanity-checked:
    ///     otherwise a `worldWidth` from some mod's settings would produce a made-up "world centre".
    /// </summary>
    private static int? FindSizeAfterKey(byte[] blob, string key)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(key);
        for (int i = 0; i + needle.Length + 1 < blob.Length; i++)
        {
            if (!blob.AsSpan(i).StartsWith(needle)) continue;
            if (i > 0 && char.IsAsciiLetter((char)blob[i - 1])) continue;              // the tail of a longer key
            if (char.IsAsciiLetter((char)blob[i + needle.Length])) continue;           // the key continues with a letter
            int p = i + needle.Length;
            for (int skip = 0; skip < 4 && p < blob.Length && !char.IsAsciiDigit((char)blob[p]); skip++) p++;
            long v = 0;
            int digits = 0;
            while (p < blob.Length && digits < 9 && char.IsAsciiDigit((char)blob[p]))
            {
                v = v * 10 + (blob[p] - '0');
                digits++;
                p++;
            }

            if (digits > 0 && v is >= 1024 and <= 33_554_432) return (int)v;
        }

        return null;
    }

    /// <summary>World seed (best-effort): varint field 7 in gamedata (confirmed structurally, the semantics are still being clarified).</summary>
    public long? DetectSeed()
    {
        var g = ReadGamedata();
        if (g is null) return null;
        foreach (var f in PbReader.ReadMessage(g, maxDepth: 1))
            if (f.Field == 7 && f.WireType == 0 && f.IntValue > 0 && f.IntValue < 1_000_000_000L)
                return f.IntValue;
        return null;
    }

    /// <summary>VS version: we look for the string in field 12 of the first chunk (confirmed in practice — "1.22.6").</summary>
    public string? DetectVersion()
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT data FROM chunk LIMIT 1;";
        var o = c.ExecuteScalar();
        if (o is not byte[] b) return null;
        foreach (var f in PbReader.ReadMessage(b, maxDepth: 1))
        {
            if (f.Field == 12 && f.Bytes is { } seg)
            {
                // raw UTF-8 (may contain garbage) — trim to the printable part
                var txt = System.Text.Encoding.UTF8.GetString(seg);
                var clean = new string(txt.Where(ch => ch >= 32 && ch < 127).ToArray());
                if (clean.StartsWith("1.") || clean.StartsWith("0.")) return clean;
            }
        }
        return null;
    }

    /// <summary>
    /// Copies the save and deletes from the COPY ALL rows of the selected chunks (all Y) in the `chunk`
    /// and `mapchunk` tables. The game performs the regeneration on the next load. The original is not modified.
    /// Returns the copy/backup paths and the number of deleted rows.
    /// </summary>
    public static (string copyPath, string backupPath, int chunkRows, int mapChunkRows)
        CopyAndDeleteChunks(string srcPath, IReadOnlyCollection<(int Cx, int Cz)> columns)
    {
        if (columns.Count == 0) throw new ArgumentException("no chunks selected");
        var wanted = new HashSet<(int, int)>(columns);
        var (dir, copy, backup) = CreateWorkingCopy(srcPath);
        try
        {
            using var conn = OpenConnection(copy, SqliteOpenMode.ReadWrite);
            using var txn = conn.BeginTransaction();
            int chunkRows = DeleteChunkColumns(conn, txn, "chunk", wanted);
            int mapRows = TableExists(conn, txn, "mapchunk") ? DeleteChunkColumns(conn, txn, "mapchunk", wanted) : 0;
            txn.Commit();
            return (copy, backup, chunkRows, mapRows);
        }
        catch
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
            throw;
        }
    }

    private static bool TableExists(SqliteConnection conn, SqliteTransaction txn, string table)
    {
        using var c = conn.CreateCommand();
        c.Transaction = txn;
        c.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n LIMIT 1;";
        c.Parameters.AddWithValue("@n", table);
        return c.ExecuteScalar() is not null;
    }

    /// <summary>Deletes table rows whose `position` decodes into one of the selected columns (cx,cz).
    /// Y is ignored — the whole chunk column is deleted.</summary>
    private static int DeleteChunkColumns(SqliteConnection conn, SqliteTransaction txn, string table, HashSet<(int, int)> columns)
    {
        EnsureSafeTableName(table);
        var toDelete = new List<long>();
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = txn;
            sel.CommandText = $"SELECT position FROM \"{table}\";";
            using var r = sel.ExecuteReader();
            while (r.Read())
            {
                long p = r.GetInt64(0);
                var (x, _, z) = ChunkPosHelper.DecodeIndex((ulong)p);
                if (columns.Contains((x, z))) toDelete.Add(p);
            }
        }
        if (toDelete.Count == 0) return 0;
        using var del = conn.CreateCommand();
        del.Transaction = txn;
        del.CommandText = $"DELETE FROM \"{table}\" WHERE position=@p;";
        var pp = del.Parameters.Add("@p", SqliteType.Integer);
        int n = 0;
        foreach (var p in toDelete) { pp.Value = p; n += del.ExecuteNonQuery(); }
        return n;
    }

    /// <summary>
    /// Copies the save and applies the chunk data changes in the COPY (safe for the original).
    /// Returns (copyPath, backupPath).
    /// </summary>
    public static (string copyPath, string backupPath) CopyAndApplyChunkChanges(string srcPath, IEnumerable<(long position, byte[] data)> changes)
        => CopyAndApplyChanges(srcPath, changes.Select(c => ("chunk", c.position, c.data)));

    /// <summary>
    /// Copies the save and applies the changes (table, position, data) in the COPY (the original is not touched).
    /// Returns (copyPath, backupPath).
    /// </summary>
    public static (string copyPath, string backupPath) CopyAndApplyChanges(string srcPath, IEnumerable<(string table, long position, byte[] data)> changes)
    {
        var list = changes.ToList();
        if (list.Count == 0) throw new ArgumentException("no changes");
        // we validate the table names BEFORE creating the copy — bad input does not touch the disk
        foreach (var (table, _, _) in list) EnsureSafeTableName(table);
        var (dir, copy, backup) = CreateWorkingCopy(srcPath);
        try
        {
            using var conn = OpenConnection(copy, SqliteOpenMode.ReadWrite);
            using var txn = conn.BeginTransaction();
            using var up = conn.CreateCommand();
            up.Transaction = txn;
            var dp = up.Parameters.Add("@d", SqliteType.Blob);
            var pp = up.Parameters.Add("@p", SqliteType.Integer);
            foreach (var (table, pos, data) in list)
            {
                var key = table == "playerdata" ? "playerid" : "position";
                up.CommandText = $"UPDATE \"{table}\" SET data=@d WHERE {key}=@p;";
                dp.Value = data;
                pp.Value = pos;
                up.ExecuteNonQuery();
            }
            txn.Commit();
            return (copy, backup);
        }
        catch
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Copies the save and clears the selected block ids in the COPY: the cells of those blocks (solid and liquid)
    /// become air (see <see cref="ChunkBlockRewriter"/>). The original is not modified, the `.bak`
    /// is created before the edits. Chunks are read in pages by `position`, so a 250-MB save is not loaded
    /// into memory as a whole, and the edits run in one transaction — the copy is either fully updated or not at all.
    /// </summary>
    public static BlockRemovalOutcome CopyAndRemoveBlocks(string srcPath, IReadOnlyCollection<int> blockIds,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (blockIds.Count == 0) throw new ArgumentException("no block ids", nameof(blockIds));
        var (dir, copy, backup) = CreateWorkingCopy(srcPath);
        try
        {
            using var conn = OpenConnection(copy, SqliteOpenMode.ReadWrite);
            var stats = RewriteBlocksCore(conn, blockIds, 0, progress, ct);
            return new BlockRemovalOutcome(copy, backup, stats.Scanned, stats.Changed,
                stats.Fields, stats.Cleared, stats.BytesBefore, stats.BytesAfter);
        }
        catch
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
            throw;
        }
    }

    /// <summary>
    ///     Copies the save and replaces the selected block ids with another block in the COPY. The original is not
    ///     modified; the `.bak` is created before the edits. Chunks are read in pages, the edits run in one transaction.
    /// </summary>
    public static BlockReplaceOutcome CopyAndReplaceBlocks(string srcPath, IReadOnlyCollection<int> blockIds,
        int targetId, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (blockIds.Count == 0) throw new ArgumentException("no block ids", nameof(blockIds));
        var (dir, copy, backup) = CreateWorkingCopy(srcPath);
        try
        {
            using var conn = OpenConnection(copy, SqliteOpenMode.ReadWrite);
            var stats = RewriteBlocksCore(conn, blockIds, targetId, progress, ct);
            return new BlockReplaceOutcome(copy, backup, stats.Scanned, stats.Changed,
                stats.Fields, stats.Cleared, stats.BytesBefore, stats.BytesAfter);
        }
        catch
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
            throw;
        }
    }

    private readonly record struct BlockRewriteStats(
        int Scanned, int Changed, int Fields, long Cleared, long BytesBefore, long BytesAfter);

    /// <summary>Shared chunk pass for block removal/replacement: used both for a copy and for the working copy.</summary>
    private static BlockRewriteStats RewriteBlocksCore(SqliteConnection conn, IReadOnlyCollection<int> blockIds,
        int targetId, IProgress<double>? progress, CancellationToken ct)
    {
        var wanted = new HashSet<int>(blockIds);
        int total;
        using (var cnt = conn.CreateCommand())
        {
            cnt.CommandText = "SELECT count(*) FROM \"chunk\";";
            total = Convert.ToInt32(cnt.ExecuteScalar());
        }

        const int pageSize = 256;
        int scanned = 0, changed = 0, fields = 0;
        long cleared = 0, bytesBefore = 0, bytesAfter = 0, lastPos = long.MinValue;

        using var txn = conn.BeginTransaction();
        using var up = conn.CreateCommand();
        up.Transaction = txn;
        up.CommandText = "UPDATE \"chunk\" SET data=@d WHERE position=@p;";
        var dp = up.Parameters.Add("@d", SqliteType.Blob);
        var pp = up.Parameters.Add("@p", SqliteType.Integer);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var rows = new List<ChunkRow>(pageSize);
            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = txn;
                sel.CommandText = "SELECT position, data FROM \"chunk\" WHERE position > @last ORDER BY position LIMIT " + pageSize + ";";
                sel.Parameters.AddWithValue("@last", lastPos);
                using var r = sel.ExecuteReader();
                while (r.Read())
                    rows.Add(new ChunkRow
                    {
                        Position = r.GetInt64(0),
                        Data = r.IsDBNull(1) ? Array.Empty<byte>() : (byte[])r.GetValue(1)
                    });
            }
            if (rows.Count == 0) break;

            foreach (var row in rows)
            {
                scanned++;
                bytesBefore += row.Data.Length;
                var rewritten = targetId == 0
                    ? ChunkBlockRewriter.ClearBlockIds(row.Data, wanted, out int entries, out int f)
                    : ChunkBlockRewriter.ReplaceBlockIds(row.Data, wanted, targetId, out entries, out f);
                if (rewritten is null) { bytesAfter += row.Data.Length; continue; }
                changed++;
                fields += f;
                cleared += entries;
                bytesAfter += rewritten.Length;
                dp.Value = rewritten;
                pp.Value = row.Position;
                up.ExecuteNonQuery();
            }
            lastPos = rows[^1].Position;
            if (progress is not null && total > 0) progress.Report((double)scanned / total);
        }
        txn.Commit();
        progress?.Report(1.0);
        return new BlockRewriteStats(scanned, changed, fields, cleared, bytesBefore, bytesAfter);
    }

    public void Dispose()
    {
        _conn.Dispose();
        if (_workingDir is { Length: > 0 } dir)
        {
            _workingDir = null;
            try { System.IO.Directory.Delete(dir, true); } catch { /* the copy is throwaway; cleanup must not break the exit */ }
        }
    }
}
