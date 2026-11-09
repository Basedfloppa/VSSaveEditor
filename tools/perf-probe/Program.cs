using System.Diagnostics;
using VSCore;

// Headless profile of the open path on a real save. Each phase is timed on its own so the wall-clock of
// "opened a save" can be split into read / decode / aggregation instead of guessed at.
// Run: dotnet run -c Release --project tools/perf-probe -- /path/to/save.vcdbs [--skip-scan]

var path = args.Length > 0 ? args[0] : throw new ArgumentException("usage: perf-probe <save.vcdbs> [--skip-scan]");
var skipScan = args.Contains("--skip-scan");
var phases = new List<(string Name, double Ms, string Note)>();
var total = Stopwatch.StartNew();

void Phase(string name, double ms, string note = "")
{
    phases.Add((name, ms, note));
    Console.WriteLine($"{name,-46} {ms,10:F0} ms  {note}");
    Console.Out.Flush();
}

double Timed(Action a, string name, string note = "")
{
    var sw = Stopwatch.StartNew();
    a();
    sw.Stop();
    Phase(name, sw.Elapsed.TotalMilliseconds, note);
    return sw.Elapsed.TotalMilliseconds;
}

void Dump(string title, IEnumerable<(string Key, object Value)> rows)
{
    Console.WriteLine($"\n== {title}");
    foreach (var (k, v) in rows) Console.WriteLine($"  {k,-32} {v}");
    Console.Out.Flush();
}

var fileBytes = new FileInfo(path).Length;
Dump("env", new (string, object)[]
{
    ("file", path), ("bytes", fileBytes), ("cores", Environment.ProcessorCount),
    ("gc_server", System.Runtime.GCSettings.IsServerGC),
});

// The UI opens the save through OpenForEditing, which is lazy now: opening reads the original and the
// snapshot is taken at the first write. Both halves are measured separately - the point of the change is
// that the 24-second copy disappears from the open path, not that it becomes free.
if (args.Contains("--copy-only"))
{
    var swLazy = Stopwatch.StartNew();
    using var editing = VSSaveFile.OpenForEditing(path);
    swLazy.Stop();
    Phase("OpenForEditing (lazy: the original stays open)", swLazy.Elapsed.TotalMilliseconds, "no snapshot yet");

    var pos = editing.ChunkPositions.FirstOrDefault();
    var blob = editing.ReadChunk(pos);
    var swFirst = Stopwatch.StartNew();
    var rows = editing.ApplyChanges(new[] { ("chunk", pos, blob ?? Array.Empty<byte>()) });
    swFirst.Stop();
    Phase("first write (snapshot + 1 update)", swFirst.Elapsed.TotalMilliseconds,
        $"{rows} row, snapshot {fileBytes / 1e9:F2} GB into {VSSaveFile.WorkingCopyRoot}");
    Dump("copy", new (string, object)[]
    {
        ("lazy_open_ms", Math.Round(swLazy.Elapsed.TotalMilliseconds)),
        ("first_write_ms", Math.Round(swFirst.Elapsed.TotalMilliseconds)),
        ("snapshot_MB_per_s", Math.Round(fileBytes / 1e6 / Math.Max(1, swFirst.Elapsed.TotalMilliseconds) * 1000)),
    });
    return 0;
}

// ---- 1. open the SQLite file ----
var swOpen = Stopwatch.StartNew();
var save = VSSaveFile.OpenReadOnly(path);
swOpen.Stop();
Phase("1. OpenReadOnly", swOpen.Elapsed.TotalMilliseconds, $"{fileBytes / 1e9:F2} GB");

// The map is built on the open path too, and it decodes every chunk of the main area a second time.
if (args.Contains("--map-only"))
{
    ChunkAreas? areas0 = null;
    Timed(() => { areas0 = ChunkAreas.Build(save); }, "ChunkAreas.Build", $"{areas0?.Areas.Count} areas");
    TerrainMap? tm0 = null;
    Timed(() => { tm0 = TerrainMap.BuildInArea(save, areas0!.Main, null); },
        "TerrainMap.BuildInArea(main)", $"{tm0?.Width}x{tm0?.Height} columns");
    return 0;
}

// Number and size of the build tiles the UI derives from the chunk positions.
if (args.Contains("--areas-only"))
{
    ChunkAreas? ar;
    var swAr = Stopwatch.StartNew();
    ar = ChunkAreas.Build(save);
    swAr.Stop();
    Phase("ChunkAreas.Build", swAr.Elapsed.TotalMilliseconds, $"{ar.Areas.Count} areas");
    foreach (var a in ar.Areas)
        Console.WriteLine($"  area {a.Index,2}{(a.IsMain ? " *" : "  ")} cx {a.MinCx}..{a.MaxCx} cz {a.MinCz}..{a.MaxCz}  columns {a.Columns}  rows {a.Rows}  cells {a.BlockCells / 1e6:F1}M");
    return 0;
}

// The background warm pass with the on-disk tile cache: build every tile once (store after each build),
// then read them all back. Reports what a first run costs and what a later switch costs.
if (args.Contains("--warm"))
{
    var cacheIdx = Array.IndexOf(args, "--cache");
    var cacheRoot = cacheIdx >= 0 ? args[cacheIdx + 1] : "/tmp/vsmcache";
    if (args.Contains("--fresh")) { try { System.IO.Directory.Delete(cacheRoot, true); } catch { } }
    var cache = new TerrainMapCache(cacheRoot);
    var swAreas = Stopwatch.StartNew();
    var ar = ChunkAreas.Build(save);
    swAreas.Stop();
    double warmCells = ar.Areas.Sum(a => (double)a.BlockCells);
    Phase("ChunkAreas.Build", swAreas.Elapsed.TotalMilliseconds,
        $"{ar.Areas.Count} areas, {warmCells / 1e6:F0}M columns total");
    var needs = TerrainMap.NeedsForMode(TerrainColorizer.ModeBlocks);
    var swFirst = Stopwatch.StartNew();
    int built = 0, fromDisk = 0, failed = 0;
    double buildMs = 0, storeMs = 0;
    foreach (var area in ar.Areas)
    {
        var swA = Stopwatch.StartNew();
        var hit = cache.TryLoad(save, area, needs, TerrainColorizer.ModeBlocks, out var tm);
        if (hit && tm is not null)
        {
            fromDisk++;
            Console.WriteLine($"  area {area.Index,2}: from disk {swA.Elapsed.TotalMilliseconds,7:F0} ms");
            continue;
        }

        try
        {
            var swB = Stopwatch.StartNew();
            tm = TerrainMap.BuildInArea(save, area, null, needs, TerrainColorizer.ModeBlocks);
            swB.Stop();
            buildMs += swB.Elapsed.TotalMilliseconds;
            var swS = Stopwatch.StartNew();
            cache.Save(save, area, needs, TerrainColorizer.ModeBlocks, tm);
            swS.Stop();
            storeMs += swS.Elapsed.TotalMilliseconds;
            built++;
            Console.WriteLine($"  area {area.Index,2}: built {swB.Elapsed.TotalMilliseconds,7:F0} ms + stored {swS.Elapsed.TotalMilliseconds,5:F0} ms" +
                              $" (cells {area.BlockCells / 1e6:F1}M)");
        }
        catch (Exception ex) { failed++; Console.WriteLine($"  area {area.Index,2}: FAILED {ex.Message}"); }
    }

    swFirst.Stop();
    Phase("warm pass (all tiles)", swFirst.Elapsed.TotalMilliseconds, $"built {built}, from disk {fromDisk}, failed {failed}");
    Console.WriteLine($"  cache: {cache.Count} tile(s), {cache.BytesOnDisk() / (1024 * 1024)} MB on disk");
    Console.WriteLine($"  built: {buildMs:F0} ms total, stored: {storeMs:F0} ms total");

    // Second pass: the same tiles, now all on disk — this is what a switch to any region costs afterwards.
    var swSecond = Stopwatch.StartNew();
    var reads = new List<double>();
    foreach (var area in ar.Areas)
    {
        var swR = Stopwatch.StartNew();
        var ok = cache.TryLoad(save, area, needs, TerrainColorizer.ModeBlocks, out _);
        swR.Stop();
        if (ok) reads.Add(swR.Elapsed.TotalMilliseconds);
    }

    swSecond.Stop();
    Phase("read all tiles back", swSecond.Elapsed.TotalMilliseconds,
        $"{reads.Count} hit(s), mean {reads.DefaultIfEmpty(0).Average():F0} ms, max {reads.DefaultIfEmpty(0).Max():F0} ms");
    return 0;
}

// Replays what the window does on open: build the main area, then start the full scan and the
// background preload of the remaining areas at the same time. They share one SQLite connection,
// so this is where a UI-only slowdown (that a plain scan does not show) comes from.
if (args.Contains("--ui-path"))
{
    var noPreload = args.Contains("--no-preload");
    var limitIdx = Array.IndexOf(args, "--preload");
    var preloadLimit = limitIdx >= 0 ? int.Parse(args[limitIdx + 1]) : int.MaxValue;

    if (args.Contains("--prefetch")) save.StartPrefetch();
    var swAr = Stopwatch.StartNew();
    var areas = ChunkAreas.Build(save);
    swAr.Stop();
    Phase("ChunkAreas.Build", swAr.Elapsed.TotalMilliseconds, $"{areas.Areas.Count} areas");
    var swMain = Stopwatch.StartNew();
    var mainMap = TerrainMap.BuildInArea(save, areas.Main);
    swMain.Stop();
    Phase("TerrainMap.BuildInArea(main)", swMain.Elapsed.TotalMilliseconds,
        mainMap is null ? "null" : $"{mainMap.Width}x{mainMap.Height}");

    // The scan reports a row count, not a fraction: the exact total would cost a full table pass before
    // the first chunk (43 s cold). Marks are stored as counts and converted to percentages once the total
    // is known, so "time to 20 %" still means the same thing.
    var marks = new List<(long Count, double Ms)>();
    var gate = new object();
    var lastMark = -1L;
    var scanSw = Stopwatch.StartNew();
    var progress = new ProbeProgress(n =>
    {
        lock (gate)
        {
            if (n - lastMark < 65_536 && n != 0) return;
            lastMark = n;
            marks.Add((n, scanSw.Elapsed.TotalMilliseconds));
        }
    });

    var gcBefore = GC.GetTotalPauseDuration();
    var allocBefore = GC.GetTotalAllocatedBytes();
    var scanTask = Task.Run(() => SaveScanner.Scan(save, progress, captureRaw: false));

    // --fixed replays the shipped order: the scan owns the connection first, the neighbours are warmed after
    // it, and a tile that would not fit the cache budget is not built at all.
    var fixedOrder = args.Contains("--fixed");
    const long budgetCells = 160_000_000;
    long usedCells = mainMap is null ? 0 : (long)mainMap.Width * mainMap.Height;

    var free = new List<(ChunkArea Area, double Ms)>();
    var preload = () =>
    {
        if (noPreload) return;
        foreach (var area in areas!.Areas.Where(a => !ReferenceEquals(a, areas.Main)).Take(preloadLimit))
        {
            if (usedCells + area.BlockCells > budgetCells) continue;
            var swA = Stopwatch.StartNew();
            try { TerrainMap.BuildInArea(save, area); }
            catch (Exception ex) { Console.WriteLine($"  preload area {area.Index} failed: {ex.Message}"); }
            swA.Stop();
            usedCells += area.BlockCells;
            free.Add((area, swA.Elapsed.TotalMilliseconds));
            Console.WriteLine($"  preload area {area.Index,2}: {swA.Elapsed.TotalMilliseconds,8:F0} ms (columns {area.Columns}, cells {area.BlockCells / 1e6:F1}M)");
            Console.Out.Flush();
        }
    };

    if (fixedOrder)
    {
        scanTask.GetAwaiter().GetResult();
        scanSw.Stop(); // the preload below is not part of the scan
        preload();
    }
    else
    {
        preload();
    }

    SaveScanResult? scanResult = null;
    try { scanResult ??= scanTask.GetAwaiter().GetResult(); } catch (Exception ex) { Console.WriteLine($"  scan failed: {ex.Message}"); }
    scanSw.Stop();
    var gcAfter = GC.GetTotalPauseDuration();
    var allocAfter = GC.GetTotalAllocatedBytes();
    Dump("gc", new (string, object)[]
    {
        ("gc_pause_ms", Math.Round((gcAfter - gcBefore).TotalMilliseconds)),
        ("alloc_GB", Math.Round((allocAfter - allocBefore) / 1e9, 2)),
        ("gen2", GC.CollectionCount(2)),
    });
    Dump("ui-path", new (string, object)[]
    {
        ("areas", areas.Areas.Count),
        ("preload_areas", free.Count),
        ("fixed_order", fixedOrder),
        ("preload_total_ms", Math.Round(free.Sum(f => f.Ms))),
        ("preload_max_ms", Math.Round(free.Count == 0 ? 0 : free.Max(f => f.Ms))),
        ("scan_total_ms", Math.Round(scanSw.Elapsed.TotalMilliseconds)),
        ("chunks", scanResult?.Chunks.Count ?? 0),
    });
    Console.WriteLine("  scan progress marks (pct @ ms):");
    long totalRows = Math.Max(1, scanResult?.Chunks.Count ?? 1);
    foreach (var (count, ms) in marks)
        Console.WriteLine($"    {count * 100.0 / totalRows,5:F0}% @ {ms,10:F0}  ({count:N0} chunks)");
    return 0;
}

// Raw cost of the positions pass with different SQLite pragmas. A fresh reflink copy makes the file
// cold, so this measures what the first open actually pays, not what a warm cache hides.
if (args.Contains("--raw-positions"))
{
    var mmapIdx = Array.IndexOf(args, "--mmap");
    var mmap = mmapIdx >= 0 ? long.Parse(args[mmapIdx + 1]) : 0;
    var cacheIdx = Array.IndexOf(args, "--cache");
    var cacheKb = cacheIdx >= 0 ? int.Parse(args[cacheIdx + 1]) : 0;

    var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly };
    using var raw = new Microsoft.Data.Sqlite.SqliteConnection(cs.ToString());
    raw.Open();
    if (mmap > 0) { using var pc = raw.CreateCommand(); pc.CommandText = $"PRAGMA mmap_size={mmap};"; pc.ExecuteNonQuery(); }
    if (cacheKb > 0) { using var pc = raw.CreateCommand(); pc.CommandText = $"PRAGMA cache_size=-{cacheKb};"; pc.ExecuteNonQuery(); }

    var swR = Stopwatch.StartNew();
    long n = 0;
    using (var cmd = raw.CreateCommand())
    {
        cmd.CommandText = "SELECT position FROM chunk ORDER BY position;";
        using var rr = cmd.ExecuteReader();
        while (rr.Read()) n++;
    }
    swR.Stop();
    Phase("raw ChunkPositions", swR.Elapsed.TotalMilliseconds, $"{n} rows, mmap={mmap / (1024 * 1024)}MB cache={cacheKb}KB");
    Dump("raw-positions", new (string, object)[]
    {
        ("rows", n), ("ms", Math.Round(swR.Elapsed.TotalMilliseconds)),
        ("mmap_MB", mmap / (1024 * 1024)), ("cache_KB", cacheKb),
        ("MB_per_s", Math.Round(fileBytes / 1e6 / Math.Max(1, swR.Elapsed.TotalMilliseconds) * 1000)),
    });
    return 0;
}

// The positions pass reads one 4-KB page at a time on a single connection. On NVMe that leaves the
// queue depth at 1; sharding the rowid range across connections tests whether parallelism helps.
if (args.Contains("--raw-positions-parallel"))
{
    var nIdx = Array.IndexOf(args, "--shards");
    int shards = nIdx >= 0 ? int.Parse(args[nIdx + 1]) : 8;
    var mmapIdx2 = Array.IndexOf(args, "--mmap");
    var mmap2 = mmapIdx2 >= 0 ? long.Parse(args[mmapIdx2 + 1]) : 0;

    var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly };
    long lo, hi;
    using (var probe = new Microsoft.Data.Sqlite.SqliteConnection(csb.ToString()))
    {
        probe.Open();
        using var c = probe.CreateCommand();
        c.CommandText = "SELECT min(position), max(position) FROM chunk;";
        using var r = c.ExecuteReader();
        r.Read();
        lo = r.GetInt64(0); hi = r.GetInt64(1);
    }
    long span = (hi - lo) / shards + 1;
    var counts = new long[shards];
    var swP = Stopwatch.StartNew();
    Parallel.For(0, shards, i =>
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(csb.ToString());
        conn.Open();
        if (mmap2 > 0) { using var pc = conn.CreateCommand(); pc.CommandText = $"PRAGMA mmap_size={mmap2};"; pc.ExecuteNonQuery(); }
        long from = lo + span * i, to = i == shards - 1 ? hi + 1 : lo + span * (i + 1);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT position FROM chunk WHERE position >= @a AND position < @b ORDER BY position;";
        cmd.Parameters.AddWithValue("@a", from);
        cmd.Parameters.AddWithValue("@b", to);
        long n = 0;
        using var rr = cmd.ExecuteReader();
        while (rr.Read()) n++;
        counts[i] = n;
    });
    swP.Stop();
    Phase($"raw ChunkPositions ({shards} shards)", swP.Elapsed.TotalMilliseconds, $"{counts.Sum()} rows, mmap={mmap2 / (1024 * 1024)}MB");
    Dump("raw-positions-parallel", new (string, object)[]
    {
        ("rows", counts.Sum()), ("shards", shards), ("ms", Math.Round(swP.Elapsed.TotalMilliseconds)),
        ("mmap_MB", mmap2 / (1024 * 1024)),
        ("MB_per_s", Math.Round(fileBytes / 1e6 / Math.Max(1, swP.Elapsed.TotalMilliseconds) * 1000)),
        ("per_shard", string.Join(",", counts.Select(c => c / 1000 + "k"))),
    });
    return 0;
}

// Cold-vs-warm cost of the positions pass. On the real open path this runs before anything else has
// pulled the file into page cache, so its first-pass number is the one the user feels.
if (args.Contains("--positions-only"))
{
    var swP1 = Stopwatch.StartNew();
    var p1 = save.ChunkPositions;
    swP1.Stop();
    var swP2 = Stopwatch.StartNew();
    var p2 = save.ChunkPositions;
    swP2.Stop();
    Phase("2a. ChunkPositions (first pass this run)", swP1.Elapsed.TotalMilliseconds, $"{p1.Count} rows");
    Phase("2b. ChunkPositions (second pass, warm)", swP2.Elapsed.TotalMilliseconds, $"{p2.Count} rows");
    Dump("positions", new (string, object)[]
    {
        ("first_pass_ms", Math.Round(swP1.Elapsed.TotalMilliseconds)),
        ("second_pass_ms", Math.Round(swP2.Elapsed.TotalMilliseconds)),
        ("speedup", Math.Round(swP1.Elapsed.TotalMilliseconds / Math.Max(1, swP2.Elapsed.TotalMilliseconds), 1)),
        ("rows", p1.Count),
    });
    return 0;
}

// Thread scaling of the histogram decode: the scan caps Parallel at 8, the machine may have more.
if (args.Contains("--hist-only"))
{
    var idx = Array.IndexOf(args, "--threads");
    var threads = idx >= 0 ? int.Parse(args[idx + 1]) : 8;
    const int Ps = 4096;
    var swH0 = Stopwatch.StartNew();
    var po0 = new ParallelOptions { MaxDegreeOfParallelism = threads };
    long cells0 = 0, solid0 = 0, fluid0 = 0, air0 = 0;
    long last0 = long.MinValue;
    while (true)
    {
        var page = save.ReadChunkRowsPage(last0, Ps);
        if (page.Count == 0) break;
        long lCells = 0, lSolid = 0, lFluid = 0, lAir = 0;
        Parallel.For(0, page.Count, po0,
            () => (Hist: new long[65536], Touched: new List<int>(256), C: 0L, S: 0L, F: 0L, A: 0L),
            (i, _, st) =>
            {
                var r = page[i];
                long s2, f2, a2;
                // The zero-word-skip prototype lived here as a temporary working-tree change; it is reverted,
                // its measurements are in tmp/perf/histfast*.log. Keep the probe on the shipped decoder.
                ChunkBlockDecoder.DecodeChunkHistogram(r.Data, st.Hist, st.Touched, out s2, out f2, out a2);
                st.C += s2 + f2 + a2; st.S += s2; st.F += f2; st.A += a2;
                foreach (var id in st.Touched) st.Hist[id] = 0;
                return st;
            },
            st =>
            {
                Interlocked.Add(ref lCells, st.C); Interlocked.Add(ref lSolid, st.S);
                Interlocked.Add(ref lFluid, st.F); Interlocked.Add(ref lAir, st.A);
            });
        cells0 += lCells; solid0 += lSolid; fluid0 += lFluid; air0 += lAir;
        last0 = page[^1].Position;
    }
    swH0.Stop();
    Dump("hist", new (string, object)[]
    {
        ("mode", "baseline"),
        ("threads", threads), ("ms", Math.Round(swH0.Elapsed.TotalMilliseconds)),
        ("cells_M", Math.Round(cells0 / 1e6, 1)),
        ("Mcells_per_s", Math.Round(cells0 / 1e6 / Math.Max(1, swH0.Elapsed.TotalMilliseconds) * 1000, 1)),
        ("solid_M", Math.Round(solid0 / 1e6, 1)),
        ("fluid_M", Math.Round(fluid0 / 1e6, 1)),
        ("air_M", Math.Round(air0 / 1e6, 1)),
    });
    return 0;
}

// Load-independent A/B of the histogram decoder: the product path (zero-word skip) and the per-cell
// reference run over the same pages of the same file in one process, so a busy host moves both numbers.
// The order alternates per page: whichever runs second would otherwise profit from the other's warm
// cache, and that would show up as a fake speed-up.
if (args.Contains("--hist-ab"))
{
    var idx = Array.IndexOf(args, "--threads");
    var threads = idx >= 0 ? int.Parse(args[idx + 1]) : 8;
    const int Ps = 4096;
    var po = new ParallelOptions { MaxDegreeOfParallelism = threads };
    var fastParts = new System.Collections.Concurrent.ConcurrentDictionary<int, long[]>();
    var slowParts = new System.Collections.Concurrent.ConcurrentDictionary<int, long[]>();

    (long S, long F, long A) RunPage(List<VSSaveFile.ChunkRow> page, bool fast)
    {
        long ls = 0, lf = 0, la = 0;
        var parts = fast ? fastParts : slowParts;
        Parallel.For(0, page.Count, po, i =>
        {
            // per-thread array, never cleared: DecodeChunkHistogram adds counts, `touched` is only the
            // caller's list of slots to reset, and this mode wants one running total per thread
            var hist = parts.GetOrAdd(Environment.CurrentManagedThreadId, _ => new long[65536]);
            var touched = new List<int>(64);
            long s2, f2, a2;
            if (fast)
                ChunkBlockDecoder.DecodeChunkHistogram(page[i].Data, hist, touched, out s2, out f2, out a2);
            else
                ChunkBlockDecoder.DecodeChunkHistogramPerCell(page[i].Data, hist, touched, out s2, out f2, out a2);
            Interlocked.Add(ref ls, s2); Interlocked.Add(ref lf, f2); Interlocked.Add(ref la, a2);
        });
        return (ls, lf, la);
    }

    double fastMs = 0, slowMs = 0;
    long cellsFast = 0, cellsSlow = 0;
    long last = long.MinValue;
    int pageNo = 0;
    while (true)
    {
        var page = save.ReadChunkRowsPage(last, Ps);
        if (page.Count == 0) break;
        bool fastFirst = (pageNo++ & 1) == 0;
        var sw = Stopwatch.StartNew();
        var a = RunPage(page, fastFirst);
        var ms1 = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var b = RunPage(page, !fastFirst);
        var ms2 = sw.Elapsed.TotalMilliseconds;
        if (fastFirst) { fastMs += ms1; slowMs += ms2; cellsFast += a.S + a.F + a.A; cellsSlow += b.S + b.F + b.A; }
        else { slowMs += ms1; fastMs += ms2; cellsSlow += a.S + a.F + a.A; cellsFast += b.S + b.F + b.A; }
        last = page[^1].Position;
    }

    long[] Reduce(System.Collections.Concurrent.ConcurrentDictionary<int, long[]> parts)
    {
        var total = new long[65536];
        foreach (var arr in parts.Values) for (int i = 0; i < 65536; i++) total[i] += arr[i];
        return total;
    }
    var tail = (fast: Reduce(fastParts), slow: Reduce(slowParts));
    long diffIds = 0, diffCells = 0;
    for (int id = 0; id < 65536; id++) if (tail.fast[id] != tail.slow[id]) { diffIds++; diffCells += tail.fast[id] - tail.slow[id]; }
    Dump("hist-ab", new (string, object)[]
    {
        ("threads", threads), ("fast_ms", Math.Round(fastMs)), ("slow_ms", Math.Round(slowMs)),
        ("speedup", Math.Round(slowMs / Math.Max(1, fastMs), 3)),
        ("cells_fast_M", Math.Round(cellsFast / 1e6, 1)), ("cells_slow_M", Math.Round(cellsSlow / 1e6, 1)),
        ("ids_that_differ", diffIds), ("cells_delta", diffCells),
    });
    return 0;
}

// Per-section breakdown of the whole "scanning save data" pass, not just its read phase.
if (args.Contains("--scan-breakdown"))
{
    var timings = new ScanTimings();
    var allocBefore = GC.GetTotalAllocatedBytes(false);
    var sw = Stopwatch.StartNew();
    var res = SaveScanner.Scan(save, null, captureRaw: false, timings: timings);
    sw.Stop();
    var scanTotal = sw.Elapsed.TotalMilliseconds;
    var allocGb = (GC.GetTotalAllocatedBytes(false) - allocBefore) / 1e9;

    double Wall(params double[] xs) => xs.Sum();
    var readWall = timings.ChunkReadMs;
    var otherWall = timings.MetaMs + timings.RegionsMs + timings.AggregationMs + timings.MapchunkMs + timings.PlayerMs;
    var parallelWall = scanTotal - readWall - otherWall;
    var cpuSum = timings.EntityMs + timings.HistogramMs + timings.BlockCodeMs;
    double Pct(double x) => cpuSum <= 0 ? 0 : x / cpuSum * 100;

    Console.WriteLine("\n== scan sections (wall clock)");
    void Row(string name, double ms) => Console.WriteLine($"  {name,-34} {ms,9:F0} ms  {ms / scanTotal * 100,5:F1} %");
    Row("meta / gamedata", timings.MetaMs);
    Row("regions", timings.RegionsMs);
    Row("chunk read (sequential, I/O)", readWall);
    Row("chunk loop (parallel wall)", parallelWall);
    Row("aggregation", timings.AggregationMs);
    Row("mapchunks: read", timings.MapchunkReadMs);
    Row("mapchunks: field parse", timings.MapchunkParseMs);
    Row("players", timings.PlayerMs);
    Row("whole scan", scanTotal);

    Console.WriteLine("\n== inside the chunk loop (CPU time across threads)");
    void Cpu(string name, double ms) => Console.WriteLine($"  {name,-34} {ms,9:F0} ms  {Pct(ms),5:F1} %");
    Cpu("entities + block entities", timings.EntityMs);
    Cpu("block histogram", timings.HistogramMs);
    Cpu("block-code fold", timings.BlockCodeMs);
    Console.WriteLine($"  {"sum of CPU time",-34} {cpuSum,9:F0} ms  (parallelism {cpuSum / Math.Max(1, parallelWall):F1}x)");

    Dump("scan result", new (string, object)[]
    {
        ("chunks", res.Chunks.Count), ("entities", res.EntityCount), ("block_entities", res.BlockEntityCount),
        ("inventories", res.Inventories.Count), ("item_stacks", res.ItemStackCount),
        ("block_codes", res.WorldBlockCodeCounts.Count), ("players", res.Players.Count),
        ("mapchunks", res.Meta.MapchunkCount), ("regions", res.Regions.Count),
        ("alloc_GB", Math.Round(allocGb, 2)),
        ("GC2", GC.CollectionCount(2)),
        ("managed_heap_GB", Math.Round(GC.GetTotalMemory(false) / 1e9, 2)),
    });

    // What the UI does with the result before the data tabs are usable.
    Console.WriteLine("\n== after the scan (UI side)");
    Timed(() => { _ = ChunkBrowser.Build(res); }, "   ChunkBrowser.Build");
    Timed(() => { _ = EntityBrowser.Build(res); }, "   EntityBrowser.Build");
    Timed(() => { _ = InventoryBrowser.Build(res); }, "   InventoryBrowser.Build");
    Timed(() => { _ = res.WithinScope(null); }, "   WithinScope(null)");
    return 0;
}

// ---- 2. metadata: everything the meta panel needs before any chunk is touched ----
var positions = new List<long>();
Timed(() => positions = save.ChunkPositions, "2. ChunkPositions (SELECT position)", $"{positions.Count} rows");
Timed(() => { _ = save.TableNames; }, "   TableNames");
Timed(() => { _ = save.MapRegions; }, "   MapRegions");
Timed(() => { _ = save.Chunks; }, "   Chunks (count)");
Timed(() => { _ = save.DetectVersion(); }, "   DetectVersion");
Timed(() => { _ = save.DetectSeed(); }, "   DetectSeed");
Timed(() => { _ = save.DetectWorldSize(); }, "   DetectWorldSize");
Timed(() => { _ = save.DetectYearRel(); }, "   DetectYearRel");
Timed(() => { _ = save.DetectSpawnY(); }, "   DetectSpawnY");

// ---- 3. pure read of the chunk table: pages of blobs, no decoding ----
const int PageSize = 4096;
(long Ms, long Bytes, int Rows) ReadAllPass(int connections)
{
    var sw = Stopwatch.StartNew();
    long bytes = 0;
    int rows = 0;
    if (connections <= 1)
    {
        long last = long.MinValue;
        while (true)
        {
            var page = save.ReadChunkRowsPage(last, PageSize);
            if (page.Count == 0) break;
            foreach (var r in page) bytes += r.Data.Length;
            rows += page.Count;
            last = page[^1].Position;
        }
    }
    else
    {
        // Independent connections: SQLite allows concurrent readers, so this is the ceiling for IO once
        // the scan stops funnelling every page through one connection behind one lock.
        var bounds = new (long Lo, long Hi)[connections];
        var step = Math.Max(1, positions.Count / connections);
        for (int i = 0; i < connections; i++)
        {
            var lo = i == 0 ? long.MinValue : positions[Math.Min(positions.Count - 1, i * step)];
            var hi = i == connections - 1 ? long.MaxValue : positions[Math.Min(positions.Count - 1, (i + 1) * step)];
            bounds[i] = (lo, hi);
        }
        long b = 0;
        int rc = 0;
        Parallel.For(0, connections, i =>
        {
            using var s2 = VSSaveFile.OpenReadOnly(path);
            long last = bounds[i].Lo;
            long localBytes = 0;
            int localRows = 0;
            while (true)
            {
                var page = s2.ReadChunkRowsPage(last, PageSize);
                if (page.Count == 0) break;
                foreach (var r in page) localBytes += r.Data.Length;
                localRows += page.Count;
                last = page[^1].Position;
                if (last >= bounds[i].Hi) break;
            }
            Interlocked.Add(ref b, localBytes);
            Interlocked.Add(ref rc, localRows);
        });
        bytes = b;
        rows = rc;
    }
    sw.Stop();
    return ((long)sw.Elapsed.TotalMilliseconds, bytes, rows);
}

static double MbPerSec(long bytes, long ms) => bytes / 1e6 / Math.Max(1, ms) * 1000;

var read1 = ReadAllPass(1);
Phase("3a. read all chunk blobs (1 conn)", read1.Ms, $"{read1.Rows} rows, {read1.Bytes / 1e9:F2} GB, {MbPerSec(read1.Bytes, read1.Ms):F0} MB/s");
var read2 = ReadAllPass(1);
Phase("3b. read all chunk blobs again (warm)", read2.Ms, $"{MbPerSec(read2.Bytes, read2.Ms):F0} MB/s");
var read4 = ReadAllPass(4);
Phase("3c. read all chunk blobs (4 conns)", read4.Ms, $"{read4.Rows} rows, {MbPerSec(read4.Bytes, read4.Ms):F0} MB/s");
var read8 = ReadAllPass(8);
Phase("3d. read all chunk blobs (8 conns)", read8.Ms, $"{MbPerSec(read8.Bytes, read8.Ms):F0} MB/s");

// ---- 4. read + only the block histogram (no entity/block-entity parsing) ----
double histMs = 0;
long totalCells = 0;
if (!skipScan)
{
    var swH = Stopwatch.StartNew();
    var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) };
    long cells = 0;
    long last = long.MinValue;
    while (true)
    {
        var page = save.ReadChunkRowsPage(last, PageSize);
        if (page.Count == 0) break;
        Parallel.For(0, page.Count, po,
            () => (Hist: new long[65536], Touched: new List<int>(256), Cells: 0L),
            (idx, _, st) =>
            {
                var r = page[idx];
                ChunkBlockDecoder.DecodeChunkHistogram(r.Data, st.Hist, st.Touched, out var solid, out var fluid, out var air);
                st.Cells += solid + fluid + air;
                foreach (var id in st.Touched) st.Hist[id] = 0;
                return st;
            },
            st => Interlocked.Add(ref cells, st.Cells));
        last = page[^1].Position;
    }
    swH.Stop();
    histMs = swH.Elapsed.TotalMilliseconds;
    totalCells = cells;
    Phase("4. read + block histogram only", histMs, $"{cells / 1e6:F1}M cells decoded");
}

// ---- 5. the real scan ----
double scanMs = 0;
SaveScanResult? data = null;
if (!skipScan)
{
    var allocBefore = GC.GetTotalAllocatedBytes(false);
    var swS = Stopwatch.StartNew();
    data = SaveScanner.Scan(save, null, captureRaw: false);
    swS.Stop();
    scanMs = swS.Elapsed.TotalMilliseconds;
    var allocAfter = GC.GetTotalAllocatedBytes(false);
    Phase("5. SaveScanner.Scan (full)", scanMs,
        $"{data.Chunks.Count} chunks, {data.EntityCount} ents, {data.BlockEntityCount} blockents, "
        + $"{(allocAfter - allocBefore) / 1e9:F2} GB alloc, GC0/1/2={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
}

// ---- 6. what the UI builds on top of the scan ----
if (data is not null)
{
    ChunkAreas? areas = null;
    Timed(() => { areas = ChunkAreas.Build(save); }, "6a. ChunkAreas.Build", $"{areas?.Areas.Count} areas");
    var cols = ChunkBrowser.Build(data);
    Timed(() => { _ = ChunkBrowser.Build(data); }, "6b. ChunkBrowser.Build", $"{cols.Count} columns");
    var ents = EntityBrowser.Build(data);
    Timed(() => { _ = EntityBrowser.Build(data); }, "6c. EntityBrowser.Build", $"{ents.Count} rows");
    var items = InventoryBrowser.Build(data);
    Timed(() => { _ = InventoryBrowser.Build(data); }, "6d. InventoryBrowser.Build", $"{items.Count} stacks");
    Timed(() => { _ = data.WithinScope(null); }, "6e. WithinScope(null)");
    _ = areas; _ = cols; _ = ents; _ = items;
}

// ---- 7. memory held by the model ----
if (data is not null)
{
    Dump("model", new (string, object)[]
    {
        ("managed_heap_GB", Math.Round(GC.GetTotalMemory(true) / 1e9, 3)),
        ("chunk_rows_in_model", data.Chunks.Count),
        ("entities", data.EntityCount),
        ("block_entities", data.BlockEntityCount),
        ("inventories", data.Inventories.Count),
        ("item_stacks", data.ItemStackCount),
        ("distinct_block_codes", data.WorldBlockCodeCounts.Count),
    });
}

total.Stop();
Console.WriteLine();
Dump("phases", phases.Select(p => (p.Name, (object)p.Ms)).ToArray());
Dump("totals", new (string, object)[]
{
    ("total_wall_clock_ms", Math.Round(total.Elapsed.TotalMilliseconds)),
    ("sum_of_phases_ms", Math.Round(phases.Sum(p => p.Ms))),
});

if (!skipScan)
{
    var readShare = read1.Ms / Math.Max(1, scanMs) * 100;
    var histOnly = histMs - read1.Ms;
    var entityMs = scanMs - histMs;
    Dump("scan split (ablation)", new (string, object)[]
    {
        ("scan_ms", Math.Round(scanMs)),
        ("read_ms_1conn", read1.Ms),
        ("read_pct_of_scan", Math.Round(readShare, 1)),
        ("hist_only_ms", Math.Round(histOnly)),
        ("hist_pct_of_scan", Math.Round(histOnly / Math.Max(1, scanMs) * 100, 1)),
        ("entity_parse_ms", Math.Round(entityMs)),
        ("entity_parse_pct_of_scan", Math.Round(entityMs / Math.Max(1, scanMs) * 100, 1)),
        ("read_speedup_4conn", Math.Round((double)read1.Ms / Math.Max(1, read4.Ms), 2)),
        ("read_speedup_8conn", Math.Round((double)read1.Ms / Math.Max(1, read8.Ms), 2)),
    });
}

return 0;

/// <summary>IProgress without a SynchronizationContext hop — the console has none, and the UI one would
/// reorder the marks through the Dispatcher.</summary>
internal sealed class ProbeProgress(Action<long> apply) : IProgress<long>
{
    public void Report(long value) => apply(value);
}
