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
    private SqliteConnection _conn;
    private List<long>? _positionsCache;
    private CancellationTokenSource? _prefetch;

    /// <summary>Guards the single read connection. Microsoft.Data.Sqlite does not allow one connection to be
    /// used by two threads at once; the background scan and an area build (or a chunk card) overlap all the
    /// time, and before this lock the second reader either failed or waited until the scan finished — which
    /// is exactly the "one area and then nothing" behaviour on a large save. Reads are short (a page of
    /// chunk rows or one blob), so a plain lock is enough and keeps memory flat.</summary>
    private readonly object _ioLock = new();
    private BlockRegistry? _blockRegistry;
    private ItemRegistry? _itemRegistry;

    /// <summary>The file that is actually open. An editing session starts on the user's save and switches to
    /// the working copy at the first write (see <see cref="EnsureEditable"/>).</summary>
    public string Path => _path;
    private string _path;

    /// <summary>The source file the user opened. null — the object was opened directly (read-only).</summary>
    public string? SourcePath { get; }

    /// <summary>True once an editing session has its own copy on disk. False right after opening (the
    /// session still reads the original) and for a read-only session.</summary>
    public bool HasWorkingCopy => _workingDir is not null;

    /// <summary>Directory of the throwaway working copy: deleted together with the object.</summary>
    private string? _workingDir;

    /// <summary>True for an editing session that has not written yet: reads go to the original, and the
    /// 12-GB snapshot is skipped until something is actually edited.</summary>
    private bool _promotionPending;

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
        _path = path;
        _conn = conn;
        SourcePath = sourcePath;
        _workingDir = workingDir;
        // A reader of a WAL database makes SQLite create `-wal`/`-shm` next to the save. Remembering whether
        // they were already there is what separates "ours to remove" from "someone else's, hands off".
        _readerPath = workingDir is null ? path : null;
        if (_readerPath is not null)
            RegisterReader(_readerPath, System.IO.File.Exists(path + "-wal") || System.IO.File.Exists(path + "-shm"));
    }

    /// <summary>Read-only sessions per file. Two sessions on one save share the same `-shm`, so the cleanup runs
    /// only when the last of them closes; and the files may be removed only when the FIRST of them opened a
    /// save that had neither — otherwise a writer put them there and they are not ours.</summary>
    private sealed class ReaderSessions
    {
        public int Count;
        public bool FilesWereOurs;
    }

    private static readonly Dictionary<string, ReaderSessions> Readers = new(StringComparer.Ordinal);

    private static void RegisterReader(string path, bool filesExisted)
    {
        lock (Readers)
        {
            if (Readers.TryGetValue(path, out var existing)) { existing.Count++; return; }
            Readers[path] = new ReaderSessions { Count = 1, FilesWereOurs = !filesExisted };
        }
    }

    /// <summary>Drops this session from the open readers; <paramref name="filesWereOurs" /> says whether the pair
    /// next to the save may be removed by whoever closes last.</summary>
    private static bool UnregisterReader(string path, out bool filesWereOurs)
    {
        lock (Readers)
        {
            filesWereOurs = false;
            if (!Readers.TryGetValue(path, out var state)) return true;
            if (state.Count > 1) { state.Count--; return false; }
            Readers.Remove(path);
            filesWereOurs = state.FilesWereOurs;
            return true;
        }
    }

    /// <summary>The file whose `-wal`/`-shm` this session may have created (the user's save, never the
    /// working copy — that one leaves together with its folder). Cleared once the cleanup has run.</summary>
    private string? _readerPath;

    /// <summary>
    ///     Removes the `-wal`/`-shm` a read-only session left next to the save. SQLite deletes those files
    ///     itself only when the last connection closes **with write access**; a reader cannot unlink them, so a
    ///     clean world used to be left with a 32-KB `-shm` and an empty `-wal` after every look at it.
    ///
    ///     Two conditions guard the deletion, both about not touching foreign data: the files must not have
    ///     existed before we opened (otherwise a writer put them there), and the WAL must still be empty — a WAL
    ///     with frames carries committed transactions and deleting it would throw them away. A third guard is
    ///     the other read-only session on the same file: the files may only go when the last one closes.
    /// </summary>
    private void FoldAwayReaderWal()
    {
        var path = _readerPath;
        _readerPath = null;
        if (path is null) return;
        if (!UnregisterReader(path, out var filesWereOurs) || !filesWereOurs) return;
        var wal = path + "-wal";
        var shm = path + "-shm";
        try
        {
            if (System.IO.File.Exists(wal) && new System.IO.FileInfo(wal).Length > 0) return;
        }
        catch
        {
            return;
        }
        foreach (var file in new[] { wal, shm })
        {
            try { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); }
            catch { /* the file is locked (another process joined in): leaving it is the safe outcome */ }
        }
    }

    /// <summary>
    ///     Opens the save for editing: the original is read only once, and all work happens on a throwaway
    ///     working copy. Otherwise every edit would create a new copy while the save loaded in the editor
    ///     stayed the old one — that is, the edit history and the view would diverge from what the
    ///     user sees.
    /// </summary>
    public static VSSaveFile OpenForEditing(string path)
    {
        // The snapshot is deferred to the first write. On a 12-GB save it cost 24 s (24 GB of disk traffic)
        // before anything was decoded, and most sessions never edit at all. Until then the session reads
        // the original — the same pages the map and the scan need — and only a write pays for the copy.
        try
        {
            return new VSSaveFile(path, OpenConnection(path, SqliteOpenMode.ReadOnly), path)
            {
                _promotionPending = true
            };
        }
        catch (SqliteException)
        {
            // A read-only WAL that cannot be opened without its `-shm` (foreign permissions, the world is
            // not stopped) cannot be read consistently at all, so this falls back to the eager snapshot.
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
    }

    /// <summary>
    /// Switches an editing session to its working copy: creates the snapshot now and replaces the read
    /// connection. Called from every write path, so nothing above this class has to know that opening
    /// was lazy. A read-only session (opened through <see cref="OpenReadOnly"/>) has no source and still
    /// throws, exactly as before.
    /// </summary>
    private void EnsureEditable()
    {
        if (SourcePath is null) throw new InvalidOperationException("this save is not editable (open it with OpenForEditing)");
        if (!_promotionPending) return;
        lock (_ioLock)
        {
            if (!_promotionPending) return;
            var (dir, copy, _) = CreateWorkingCopy(SourcePath, withBackup: false);
            SqliteConnection conn;
            try
            {
                conn = OpenConnection(copy, SqliteOpenMode.ReadWrite);
            }
            catch
            {
                try { System.IO.Directory.Delete(dir, true); } catch { }
                throw;
            }
            _conn.Dispose();
            FoldAwayReaderWal(); // the read-only phase is over: the original must be left as clean as we found it
            _conn = conn;
            _path = copy;
            _workingDir = dir;
            _promotionPending = false;
        }
    }

    /// <summary>
    ///     Applies changes to the WORKING COPY in one transaction. A separate connection is opened:
    ///     the background scan is reading the file through the main one, and one SQLite connection cannot
    ///     be used from two threads at once.
    /// </summary>
    public int ApplyChanges(IEnumerable<(string table, long position, byte[] data)> changes)
    {
        EnsureEditable();
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
        InvalidatePositions(); // an update may add or drop chunk rows
        txn.Commit();
        return applied;
    }

    /// <summary>
    ///     Connection for a targeted edit of the working copy. Separate from the main one: the background scan
    ///     reads the file in parallel, and one SQLite connection cannot be used from two threads.
    /// </summary>
    private SqliteConnection OpenWriteConnection()
    {
        EnsureEditable();
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
        EnsureEditable();
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
        InvalidatePositions(); // the deleted columns are gone from the chunk table
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
            Cache = mode == SqliteOpenMode.ReadOnly ? SqliteCacheMode.Shared : SqliteCacheMode.Default,
            // No pooling: a pooled connection is handed back instead of closed, so SQLite never runs its
            // "last connection" path and the `-wal`/`-shm` of the save outlive the session. Closing for real
            // also keeps the exit deterministic: what the editor opened, the editor closes.
            Pooling = false
        };
        // DataSource is not a parameter but connection-string text. If the path contains `;` or a quote,
        // we check that the path stayed exactly the same after assembly: otherwise there is a risk of connecting
        // to the wrong file — the one slipped in by the name's "tail".
        var connStr = cs.ToString();
        if (new SqliteConnectionStringBuilder(connStr).DataSource != path)
            throw new InvalidOperationException($"unsafe save path: {path}");
        var conn = new SqliteConnection(connStr);
        conn.Open();
        ApplyReadPragmas(conn, mode);
        return conn;
    }

    /// <summary>
    /// Read-side tuning, read-only connections only. `query_only` makes an accidental write fail instead
    /// of touching the user's save, at no measurable cost. Two other pragmas were measured on a 12-GB save
    /// and deliberately NOT set: a 128-MiB `cache_size` made a full positions pass 26 % slower
    /// (2.57 s -> 3.25 s) — the scan streams pages once, so a large cache only adds bookkeeping — and
    /// `mmap_size` moved the same pass by ~2 %, inside the run-to-run noise, while mmap of a file the game
    /// may rewrite risks a SIGBUS. The defaults are better here.
    /// </summary>
    private static void ApplyReadPragmas(SqliteConnection conn, SqliteOpenMode mode)
    {
        if (mode != SqliteOpenMode.ReadOnly) return;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA query_only=1;";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // tuning must never prevent opening a save: a build without this pragma still works
        }
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
    /// An empty name and `..`/separators would take the copy outside the working-copy directory, hence the fallback.</summary>
    public static string SafeCopyFileName(string srcPath)
    {
        var name = System.IO.Path.GetFileName(srcPath);
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(new[] { '/', '\\' }) >= 0)
            return "save.vcdbs";
        return name;
    }

    /// <summary>
    /// A consistent working snapshot of the save in the working-copy folder + a backup BEFORE any edits.
    /// The snapshot is taken through the SQLite Online Backup API, so it also includes uncheckpointed data
    /// from `-wal`; if a read-only WAL is unavailable — by copying the file set and then
    /// folding the WAL into the main file. The result is one self-contained file (without `-wal`/`-shm`),
    /// and the `.bak` is published atomically (`tmp` + `Move`): a crash of the editor leaves no
    /// "half-written" backup, and the original was not modified in any case.
    /// </summary>
    private static (string dir, string copy, string backup) CreateWorkingCopy(string srcPath, bool withBackup = true)
    {
        var dir = System.IO.Path.Combine(WorkingCopyRoot, "vseditor_" + Guid.NewGuid().ToString("N"));
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

    /// <summary>
    ///     Directory for throwaway working copies.
    ///
    ///     Deliberately NOT <see cref="System.IO.Path.GetTempPath" />: `/tmp` is a tmpfs on many Linux
    ///     installs, so a 6-GB server save copied there would occupy 6 GB of RAM for the whole editing
    ///     session (and would not show up as process memory, only as "used" in the system monitor). The
    ///     local application data folder is disk-backed on Windows, Linux and macOS; the temp folder stays
    ///     the fallback for a sandboxed or portable install where that folder cannot be created.
    ///     VSSAVEEDITOR_WORK_DIR overrides it (useful for tests and for putting the copy on another disk).
    /// </summary>
    public static string WorkingCopyRoot { get; } = ResolveWorkingCopyRoot();

    private static string ResolveWorkingCopyRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("VSSAVEEDITOR_WORK_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            try
            {
                System.IO.Directory.CreateDirectory(overridden);
                return overridden;
            }
            catch { /* unwritable override: fall through to the defaults */ }
        }

        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
            {
                var root = System.IO.Path.Combine(local, "VSSaveEditor", "working");
                System.IO.Directory.CreateDirectory(root);
                return root;
            }
        }
        catch { /* no usable profile: the temp folder is still better than failing to open a save */ }

        return System.IO.Path.GetTempPath();
    }

    /// <summary>Removes working copies left behind by a crashed editor (older than `olderThan`, 3 days by
    /// default). Fresh directories are left alone — another running instance may be working in them.
    /// The temp folder is still scanned: copies created by older versions live there.</summary>
    public static void CleanupStaleWorkingCopies(TimeSpan? olderThan = null)
    {
        try
        {
            var cutoff = DateTime.UtcNow - (olderThan ?? TimeSpan.FromDays(3));
            foreach (var root in new[] { WorkingCopyRoot, System.IO.Path.GetTempPath() }.Distinct(StringComparer.Ordinal))
            {
                if (!System.IO.Directory.Exists(root)) continue;
                foreach (var d in System.IO.Directory.GetDirectories(root, "vseditor_*"))
                {
                    try { if (System.IO.Directory.GetCreationTimeUtc(d) < cutoff) System.IO.Directory.Delete(d, true); }
                    catch { }
                }
            }
        }
        catch { /* cleanup must not get in the way of startup */ }
    }

    /// <summary>Runs one short read on the shared connection under the connection lock.</summary>
    private T ReadDb<T>(Func<SqliteConnection, T> read)
    {
        lock (_ioLock) return read(_conn);
    }

    /// <summary>Resolution (schema_name.table_name) of the save tables.</summary>
    public IReadOnlyList<string> TableNames => ReadDb(conn =>
    {
        var list = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name!='sqlite_sequence' ORDER BY name;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    });

    private List<T> ReadRows<T>(string table, Func<long, int, T> map)
    {
        EnsureSafeTableName(table);
        return ReadDb(conn =>
        {
            var list = new List<T>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT position, length(data) FROM \"{table}\" ORDER BY position;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(map(r.GetInt64(0), (int)(r.IsDBNull(1) ? 0 : r.GetInt64(1))));
            return list;
        });
    }

    public List<MapRegionInfo> MapRegions => ReadRows("mapregion", (p, l) => new MapRegionInfo(p, l));
    public List<ChunkInfo> Chunks => ReadRows("chunk", (p, l) => new ChunkInfo(p, l));

    public int CountRows(string table)
    {
        EnsureSafeTableName(table);
        return ReadDb(conn =>
        {
            using var c = conn.CreateCommand();
            c.CommandText = $"SELECT count(*) FROM \"{table}\";";
            return Convert.ToInt32(c.ExecuteScalar());
        });
    }

    public struct PlayerRow { public long Id; public string? Uid; public byte[] Data; }
    /// <summary>Rows of the playerdata table (characters) with raw data.</summary>
    public List<PlayerRow> PlayerRows => ReadDb(conn =>
    {
        var list = new List<PlayerRow>();
        using var c = conn.CreateCommand();
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
    });

    public long? GamedataLength => ReadDb(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT length(data) FROM gamedata WHERE savegameid=1;";
        return (long?)c.ExecuteScalar();
    });

    public struct ChunkRow { public long Position; public byte[] Data; }
    /// <summary>Chunks with data (for building the terrain map).</summary>
    public List<ChunkRow> ChunkRowsWithData => ChunkRowsInWindow(null);

    /// <summary>
    ///     Chunk rows filtered by the columns of an area (<paramref name="area" /> = null — all).
    ///     Chunk blobs outside the window are not read from the DB at all: without the filter, a window for one
    ///     area would pull the data of the whole save into memory (155,584 rows against 16,144 in a two-area save).
    /// </summary>
    public List<ChunkRow> ChunkRowsInWindow(ChunkArea? area) => ReadDb(conn =>
    {
        var list = new List<ChunkRow>();
        using var c = conn.CreateCommand();
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
    });

    /// <summary>Positions of the chunk rows of an area (no blobs are read). Used by the paged map build:
    /// it needs the bounds and the groups first, then fetches the blobs a page at a time.</summary>
    public List<long> ChunkPositionsInWindow(ChunkArea? area) => ReadDb(_ =>
    {
        var all = _positionsCache ??= ReadPositions(_conn);
        if (area is null) return all;
        var list = new List<long>(all.Count / 8);
        foreach (var position in all)
        {
            var (cx, _, cz) = ChunkPosHelper.DecodeIndex((ulong)position);
            if (area.ContainsChunk(cx, cz)) list.Add(position);
        }
        return list;
    });

    /// <summary>Chunk rows in an inclusive position range. Positions are primary-key ordered, so a page of
    /// complete (cx,cz) groups is one indexed range scan; blobs outside the requested page are never materialised.</summary>
    public List<ChunkRow> ReadChunkRowsRange(long fromPosition, long toPosition, ChunkArea? area = null) => ReadDb(conn =>
    {
        var list = new List<ChunkRow>();
        using var c = conn.CreateCommand();
        if (area is null)
        {
            c.CommandText = "SELECT position, data FROM chunk WHERE position >= @lo AND position <= @hi ORDER BY position;";
        }
        else
        {
            // Filter the area in SQL, before SQLite materialises the blob column. A page is one Y layer,
            // but that layer may contain several areas; without this condition every area build read the
            // blobs of all the others (and held the shared read lock while doing it).
            c.CommandText = "SELECT position, data FROM chunk WHERE position >= @lo AND position <= @hi "
                            + "AND ((position >> 27) & @mask) BETWEEN @minCz AND @maxCz "
                            + "AND (position & @mask) BETWEEN @minCx AND @maxCx ORDER BY position;";
            c.Parameters.AddWithValue("@mask", (long)ChunkPosHelper.ChunkMask);
            c.Parameters.AddWithValue("@minCx", area.MinCx);
            c.Parameters.AddWithValue("@maxCx", area.MaxCx);
            c.Parameters.AddWithValue("@minCz", area.MinCz);
            c.Parameters.AddWithValue("@maxCz", area.MaxCz);
        }
        c.Parameters.AddWithValue("@lo", fromPosition);
        c.Parameters.AddWithValue("@hi", toPosition);
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new ChunkRow
            {
                Position = r.GetInt64(0),
                Data = r.IsDBNull(1) ? Array.Empty<byte>() : (byte[])r.GetValue(1)
            });
        return list;
    });

    /// <summary>A page of chunk rows after <paramref name="afterPosition"/> (exclusive), ordered by position.
    /// Used by the scan so it does not retain every blob of the save at once.</summary>
    public List<ChunkRow> ReadChunkRowsPage(long afterPosition, int limit) => ReadDb(conn =>
    {
        var list = new List<ChunkRow>(Math.Max(1, limit));
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT position, data FROM chunk WHERE position > @after ORDER BY position LIMIT @n;";
        c.Parameters.AddWithValue("@after", afterPosition);
        c.Parameters.AddWithValue("@n", Math.Max(1, limit));
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new ChunkRow
            {
                Position = r.GetInt64(0),
                Data = r.IsDBNull(1) ? Array.Empty<byte>() : (byte[])r.GetValue(1)
            });
        return list;
    });

    /// <summary>Positions of all `chunk` rows (no data is read) — the map areas are computed from them.</summary>
    /// <remarks>
    ///     Cached for the lifetime of the save object: the query reads the whole table (the blob column lives in
    ///     the same b-tree, so selecting one column still touches every page). The map builds one tile per area
    ///     and asked for the same list again for each of them — on a 12-GB save with 45 tiles that was 45 full
    ///     passes over the file. The writers below invalidate the cache, because they remove rows.
    /// </remarks>
    public List<long> ChunkPositions => ReadDb(_ => _positionsCache ??= ReadPositions(_conn));

    /// <summary>How many times the chunk tables of this file were changed in this session. The on-disk tile
    /// cache mixes it into its key, so a tile built from older content is never served after an edit.</summary>
    public int ContentVersion { get; private set; }

    private static List<long> ReadPositions(SqliteConnection conn)
    {
        var list = new List<long>();
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT position FROM chunk ORDER BY position;";
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    /// <summary>Drops the cached positions — a write may have added or removed chunk rows.</summary>
    private void InvalidatePositions()
    {
        _positionsCache = null;
        // Chunk data changed: every tile cached from the previous content is stale, so the cache key
        // (which includes this counter) rotates. Within a session the file mtime is not enough — an edit
        // and a re-read can land in the same second.
        ContentVersion++;
    }

    /// <summary>Rows of the mapchunk table (the client map) with data.</summary>
    public List<ChunkRow> MapchunkRowsWithData => MapchunkRowsWithDataLimit(int.MaxValue);

    /// <summary>First <paramref name="limit"/> mapchunk rows with data, ordered by position.</summary>
    /// <remarks>The mapchunk blobs are 3.7 GB on the 12-GB server save (31 % of the file) while the only
    /// consumer of the scanned rows — the data dump — prints fifty of them. The scan therefore reads a
    /// sample and asks for <see cref="MapchunkRowCount"/> separately, which walks the leaf pages only.</remarks>
    public List<ChunkRow> MapchunkRowsWithDataLimit(int limit) => ReadDb(conn =>
    {
        var list = new List<ChunkRow>(Math.Min(limit, 4096));
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT position, data FROM mapchunk ORDER BY position LIMIT @n;";
        c.Parameters.AddWithValue("@n", limit);
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(new ChunkRow { Position = r.GetInt64(0), Data = r.IsDBNull(1) ? Array.Empty<byte>() : (byte[])r.GetValue(1) });
        return list;
    });

    /// <summary>Number of mapchunk rows. COUNT(*) over this table reads its leaves (~12 MB), not the blobs.</summary>
    public int MapchunkRowCount => ReadDb(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT count(*) FROM mapchunk;";
        return Convert.ToInt32(c.ExecuteScalar());
    });

    public byte[]? ReadRegion(long position) => ReadDb(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT data FROM mapregion WHERE position=@p;";
        c.Parameters.AddWithValue("@p", position);
        var o = c.ExecuteScalar();
        return o is byte[] b ? b : (o is null ? null : System.Text.Encoding.Default.GetBytes(o.ToString()!));
    });

    /// <summary>Raw bytes of a particular chunk (for the structural view/editing).</summary>
    public byte[]? ReadChunk(long position) => ReadDb(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT data FROM chunk WHERE position=@p;";
        c.Parameters.AddWithValue("@p", position);
        var o = c.ExecuteScalar();
        return o as byte[];
    });

    public byte[]? ReadGamedata() => ReadDb(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT data FROM gamedata WHERE savegameid=1;";
        var o = c.ExecuteScalar();
        return o as byte[];
    });

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
    public string? DetectVersion() => ReadDb(conn =>
    {
        using var c = conn.CreateCommand();
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
    });

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

    /// <summary>
    /// Reads the file front to back once, in the background, so the passes that follow meet warm pages.
    /// Measured on a cold 12-GB save: the positions pass took 43.3 s on its own and 31.7 s right after a
    /// sequential read of the same file, because SQLite pulls 4-KB pages one at a time (~280 MB/s) while a
    /// bulk read of the same file runs at ~3.7 GB/s. Best effort: a failure or a busy disk changes nothing,
    /// and it stops as soon as the save is disposed.
    /// </summary>
    public void StartPrefetch()
    {
        if (SourcePath is not { Length: > 0 } path) return;
        _prefetch?.Cancel();
        var cts = new CancellationTokenSource();
        _prefetch = cts;
        var token = cts.Token;
        new Thread(() =>
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    1 << 20, FileOptions.SequentialScan);
                var buf = new byte[1 << 20];
                while (!token.IsCancellationRequested && fs.Read(buf, 0, buf.Length) > 0) { }
            }
            catch
            {
                // prefetch is an optimisation only: a locked or unreadable file must not affect opening
            }
        })
        { IsBackground = true, Name = "save-prefetch" }.Start();
    }

    public void Dispose()
    {
        try { _prefetch?.Cancel(); } catch { }
        _prefetch?.Dispose();
        _prefetch = null;
        lock (_ioLock)
        {
            // On exit the WAL of a copy we wrote must be folded into the main file, not left next to it.
            // The working copies are opened in DELETE mode, so this is a no-op today — it states the contract
            // for any future writable session, where a leftover `-wal` would carry the user's edits.
            if (_workingDir is not null) CheckpointQuietly(_conn);
            _conn.Dispose();
        }
        FoldAwayReaderWal();
        if (_workingDir is { Length: > 0 } dir)
        {
            _workingDir = null;
            try { System.IO.Directory.Delete(dir, true); } catch { /* the copy is throwaway; cleanup must not break the exit */ }
        }
    }

    /// <summary>`PRAGMA wal_checkpoint(TRUNCATE)`: every frame goes into the main file and the WAL is emptied.
    /// Never throws: a failed checkpoint must not break the exit, and the caller deletes the file set anyway.</summary>
    private static void CheckpointQuietly(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // not a WAL database, or the file is busy — the caller's cleanup is the fallback
        }
    }
}
