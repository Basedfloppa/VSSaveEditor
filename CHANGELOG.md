# Changelog

All notable changes to VS Save Editor are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow semantic versioning.

## [Unreleased] — planned first public release `0.1.0`

### Added

- 2D terrain map rendered in the in-game map's colours, with render modes, LOD, chunk grid and
  hover readout.
- Connected-area map building for worlds with distant pregen patches: the map opens on the tile
  containing spawn, neighbouring tiles are preloaded in the background, and a tile that is not
  cached is built when panned to.
- Real Y-layer slice with on-demand voxel decoding.
- Chunk search by coordinates, ranges, counters, layer, empty state and content.
- Entity and block-entity index with field editing, nested paths, real positions and raw hex
  fallback.
- Item-stack search across players, creatures and containers, with navigation to the owner and
  the chunk.
- Players tab for `playerdata`, including inventory editing by index.
- “Search in” scope on the Chunks, Entities, Items, Players and Find-blocks tabs: search the whole
  save, one chunk, a rectangle of chunks, the map selection or one map area.
- Block overlay and block removal/replacement by code, editing the palette only.
- Chunk-column deletion from the working copy.
- Non-destructive working-copy editing, before/after review, undo/redo and edit history.
- “Save a copy…” to a new `.vcdbs`; the original is never overwritten.
- Map export to PNG, JPEG and lossless WebP: **whole save** (all build tiles, automatically downscaled when their union is too large), active area only, or selected chunks.
- Recent-saves list, start panel, dark/light theme and English UI.
- MIT No Attribution (MIT-0) license.

### Performance

- Working copies are created on the first edit instead of on open. Opening the 12 GB server save
  copied 12 GB (24 GB of disk traffic) before the first chunk was decoded, and most sessions never
  edit. The session now reads the original and takes the snapshot at the first write — measured
  **24 s → 30 ms** to open, with the deferred snapshot at 9.4 s on a warm source. A read-only `-wal`
  that cannot be opened without its `-shm` still falls back to the eager copy.
- The scan no longer reads `chunk.position` in a separate pass before paging the blobs: the page
  reader already yields every position in order, so a cold 12 GB save no longer pays a second
  full-table walk (42 s measured cold). `BlockFilterScanner` counts rows with `COUNT(*)` instead of
  materialising 4 M positions only to size a progress bar.
- The block histogram decodes 32-cell words instead of single cells: an all-zero word is 32 copies
  of `palette[0]` and is counted in one step, and a cell whose mask bit is clear skips the per-cell
  decode. Both decoders were run over the same pages in one process on the 12 GB server save:
  **61.4 → 49.5 s (1.24×)**, histograms identical on all 65 536 ids. The scan cap went from 8 to 12
  workers (measured 69.7 → 61.0 s at 12; going to 24 gained almost nothing on this 8P+16E machine).
  Full scan on the 12 GB save: **99.8 → 65.2 s**.
- The whole open path was replayed headlessly (`tools/perf-probe --ui-path`), not just the scan: the
  window starts the scan and the background preload of the neighbouring map tiles at the same time, and
  both read through one SQLite connection under one lock. On the 12 GB save (**45 build tiles**) the
  preload built every tile (149.8 s) while the scan crawled — **5 % of the scan took 44.6 s, 20 % took
  85.7 s**, the whole scan 163.5 s instead of 65 s alone. Each tile also re-ran `SELECT position FROM
  chunk` over the entire file, which is 42 s on a cold one, so a barely-warm save reached 20 % in the
  five minutes the user measured. Three changes: the scan finishes before the preload starts, a tile
  that does not fit the cache budget is not built at all (the old code built it and then discarded the
  map), and chunk positions are cached per save so a tile filters a list instead of walking the table.
  Replay after the fixes: **20 % at 26.9 s**, scan **66.6 s**, 14 tiles preloaded in 22.0 s, a tile
  build **3.5 → 1.5 s**.
- Read-only connections are opened with `PRAGMA query_only=1`, so an accidental write fails instead
  of touching the user's save. Two other pragmas were measured and rejected: a 128 MiB `cache_size`
  made a full positions pass 26 % slower (2.57 → 3.25 s), and `mmap_size` moved it by ~2 % — inside
  the run-to-run noise — while mmap of a file the game may rewrite risks a SIGBUS.
- Working copies are no longer created in the system temp folder. On Linux `/tmp` is often a tmpfs:
  opening the 6.3 GB server save copied 6.3 GB into RAM before the first edit and kept it there for the
  whole session (measured on the 1.7 GB save: the tmpfs copy raised `used` by ~1.7 GB and `shared` by
  ~1.6 GB, while the disk-backed copy left `used` unchanged and only grew the page cache). Copies now
  go to the local application data folder, with `VSSAVEEDITOR_WORK_DIR` as an override and stale copies
  cleaned from both the new root and the old temp location.
- The scan model holds one string instance per value instead of one per occurrence: a 6.3 GB server
  save (2.19M chunk rows, 1.87M block entities) repeats only ~12k distinct tokens across ~32M
  references. The tokens are pooled per scan (not `string.Intern`, whose table would outlive every
  opened save), and the per-chunk block histogram is packed into `(id << 16) | count` words instead
  of a `Dictionary<string,long>`. On the 1.7 GB server save the retained scan model fell from 845 to
  392 MiB; on the 518 MB test save the scan went from 6.4 to 5.5 s, allocation from 3379 to 3117 MiB
  and peak working set from 860 to 510 MiB, with identical block/entity/aggregate hashes.
- Terrain-map surface and bottom Y are 16-bit (a chunk Y is 10 bits), which removes 4 bytes per map
  column: the 156M-column preload budget of the 6.3 GB save dropped from 2629 to 1999 MiB with the
  same rendered world-coordinate and colour hashes.
- The scan no longer keeps the full block-entity vocabulary or empty per-row containers: block-entity
  tokens were 31M references (281 MiB) on the 6.3 GB save, while only the type (first token) and the
  `game:` codes are read by the UI. Chunk rows and block entities allocate their entity/inventory lists
  on the first record instead of per row (~260 MiB of empty `List` objects), block-entity/entity
  payloads are parsed straight from the chunk span instead of a `byte[]` copy per record, and world
  coordinates are derived from the chunk coordinates. Retained scan model on the 6.3 GB server save:
  **1501 → 875 MiB**, peak working set **3562 → 2910 MiB**, scan **58 → 50 s**. Side effect: free-text
  search over the Entities tab no longer matches arbitrary block-entity token words — its type and
  `game:` codes are still searchable, and the full record is parsed from the working copy when the
  card is opened.
- A registry where two block ids share a code no longer turns every per-chunk histogram into a
  `Dictionary<string,long>`: the histogram stays packed by id and duplicate codes are summed only when
  the histogram is read. On the modded 518 MB capture this cut the scan model from 235 to 108 MiB.
- Chunk decoding for the map and the scan is fused and allocation-light: block planes are read in
  place, top/bottom/water/rain come from per-column summaries, and block statistics use a numeric
  histogram instead of a string dictionary per cell.
- Chunk blobs are read in pages (map: one Y layer, scan: 4096 rows) instead of being retained for
  the whole save; the map no longer needs the `flat` staging array.
- The detailed map view memoises per-column colours, so panning and zooming do not repeat block
  lookups and tinting.
- Blocks-mode maps no longer build or fill the water-depth data they never read; the in-game
  blocks colour comes from `MapY`/`MapBlock`, so the water column is skipped per chunk. Rain-surface
  data is still built because `MapY`/`MapBlock` use it.
- A coarse, pre-coloured pyramid is built for large areas: zoomed out, the whole map is a bitmap blit
  instead of a per-column LOD pass, and the exact per-block pass returns as soon as detail is needed.
- Built area maps are cached and preloaded in the background. At detail zoom every visible tile with
  a cached full map is sampled at the same density as the active one, so a viewport that straddles a
  tile boundary shows full resolution on both sides; at overview zoom the tiles are separate overview
  layers instead of replacing the current one.
- Sprawling chunk groups are cut into 16M-column build tiles instead of one map per connected
  group: on the 3.9 GB test save this reduced the first map from 10.7 s to 7.1 s and the total
  bounding-box area from 203M to 131M columns, with all tiles fitting the preload/cache budget.

- Built map tiles are stored on disk (`TerrainMapCache`) and every tile of the world is prepared in
  the background, not only the ones that fit the in-memory budget. On the 12 GB server save
  (45 tiles, 389 M columns) the warm pass takes **117 s once** — 110 s of builds plus 6.6 s of
  stores — and afterwards any region, including one never opened, loads in **110 ms (mean) / 309 ms
  (max)**: a disk read instead of a 1.5–3.5 s rebuild. The 45 tiles occupy **450 MB** on disk
  (~1.2 bytes per column, 9.4× smaller than the raw arrays), and the cache is bounded and pruned by
  age. Only 120 M columns stay in RAM (~1.3 GB): the rest is evicted farthest-first and read back
  when needed.
- Map tiles are stored zstd-compressed with the relief bounds, overview pyramid, present-chunk set
  and chisel materials; the block registry and the on-demand voxel source are re-attached on load.
  The key covers the source file identity (path, length, mtime) and the content version, so a tile
  built from an older or edited save is a miss, never stale rendering.

### Changed
- Map-region layers are parsed on demand instead of during the scan. A big save carries ~2000
  `mapregion` blobs (677 MB on the 12-GB reference save) and every one of them was read and
  parsed at open just to put a few lines into the text dump: on that save the regions section went
  from 3.3 s warm / 9.3 s cold to **3 ms**, and 677 MB of I/O left the open path. The scan keeps
  the position and the size from the table rows; the blob is read the first time the layers are
  asked for, and a failing read (closed save, torn blob) reports and shows nothing rather than
  taking the dump down. The dump prints layers for the first 8 regions and says how many were not
  read.
- Opening a save now warms the file in the background (`VSSaveFile.StartPrefetch`). The passes that follow read the same bytes, but SQLite pulls 4-KB pages one at a time: on a cold 12-GB save the positions pass took 43.3 s alone and 31.7 s right after a bulk sequential read of the same file (the map phase in the full open path went 15.2 s -> 7.5 s). Best effort: failures are ignored and it stops when the save is disposed.

### Fixed

- Looking at a save no longer leaves `-wal`/`-shm` next to it. Opening a WAL world makes SQLite create
  both files, and SQLite deletes them itself only when the last connection closes **with write access** —
  a read-only reader never could, and connection pooling handed the connection back instead of closing
  it, so every session left a 32-KB `-shm` and an empty `-wal` behind. Connections are now opened without
  pooling, and on close the editor removes the pair it created, with two guards: files that already existed
  before the session are left alone, and a WAL that still carries frames is never deleted (those frames are
  committed transactions — e.g. the game running next to the editor). The working copy is checkpointed and
  goes away with its folder. Verified on a 7.9-MB save: a WAL world opened and exited leaves just the
  `.vcdbs`; with a live writer holding 16 KB of frames, the editor read 2801 chunks and left the WAL intact.
- The map no longer leaves empty pieces of the world on screen after the background warm pass. A tile
  evicted from the RAM budget used to vanish completely: looking at the whole map on a 12-GB save showed
  **29 of 45 areas as black**, and nothing brought them back until the user scrolled into each one. An
  evicted tile now leaves a shrunk copy of its pyramid (≤512 px, under a megabyte), eviction drops the
  tiles off screen before the ones on it, and a visible area whose overview is too coarse for the current
  zoom is read back from the on-disk tile cache (~0.1-0.5 s) instead of staying blank. Probe `cover` on the
  12-GB save: 29 blank visible areas before, 0 at whole-map zoom and 0 at medium zoom (after 0.5 s).
- A missing desktop D-Bus service is reported once, as a warning, and never touches the status line. On a machine without `xdg-desktop-portal`/IBus, Avalonia asks for a name that is not activatable from a task nobody awaits, so the finalizer rethrows `org.freedesktop.DBus.Error.ServiceUnknown` in the middle of a scan: the console showed a full stack under `[app][ERROR] background task failed`, and the window showed `Unexpected error`. Neither is the editor's failure — input and file dialogs simply fall back to the built-in ones. `AppLog.IsPlatformNoise` recognises that case (a D-Bus/portal exception whose message says the name is missing), `AppLog.PlatformNoise` writes one `[app][WARN] platform: no desktop D-Bus service …` line per run with the cause and no stack, and the handlers return before the UI is touched. Everything else keeps the full error path. Probe: `dotnet run --project .uisnap -- applog`.
- The scan no longer crashes the window when it reports progress. `SaveScanner` reports rows from the worker threads of `Parallel.For`, the handler wrote the status line, and that line is a control only the UI thread may touch — a 12-GB save died 2 s in with `InvalidOperationException: Call from invalid thread` wrapped in an `AggregateException`. `AppStatus` now keeps its channels under a lock and raises `Changed` through `Status.Dispatch`, which the window points at the UI dispatcher; `OnStatusChanged` also re-posts itself if it is ever reached off-thread. The lock is not decoration: 4000 writes from 8 threads threw 2755 times and left the visible line disagreeing with `Status.Text`. Regression probe: `dotnet run --project .uisnap -- statusrace`.
- A scan failure now names its real cause on the console: `ScanDiagnostics` unwraps the `AggregateException` that `Parallel.For` adds, so the header line reads `InvalidOperationException: Call from invalid thread` instead of `One or more errors occurred.`
- Nothing that escapes a handler is silent any more. The three global nets (the domain, the UI thread,
  and a background task nobody awaited) wrote to `Trace`, which a GUI launch never shows, so a failure
  surfaced only as `Unexpected error: …` on the status line with no reason anywhere. They now log
  through `AppLog` (`[app][ERROR] …` on stderr, stack included, identical consecutive lines collapsed
  into a count) and name the innermost cause. A late failure also stops hijacking the window: an
  unobserved task is rethrown by the finalizer thread during an unrelated action — usually a scan,
  because it allocates heavily — and the old handler then replaced the progress line and cleared
  `_busy` while the scan was still running. Reported case: `org.freedesktop.DBus.Error.ServiceUnknown`
  (a D-Bus portal absent on the machine) arriving 2 s into a scan. `ReportUnhandled` now takes
  `resetBusy`: a live UI-handler failure still clears the flags, a dead background task only reaches
  the line when nothing else owns it. Regression probe: `dotnet run --project .uisnap -- applog`.

- A pan or zoom-out that exposes a second build tile while the primary bitmap still covers the
  viewport now builds that tile's exact layer instead of leaving the newly visible tile blank.
- Map export’s **Whole area** was only the active build tile, so a save split into many tiles
  produced a small image of one area. The export now offers **Whole save**, builds/reuses every
  tile, renders them into one union canvas, and samples the union at an integer block step when
  its full-resolution RGBA would exceed the image budget (the 18-tile, 4.7 GB save exports as
  7659×14774 px at one pixel per 3 blocks instead of failing at 4 GiB). Empty gaps between distant
  tiles remain background; they are absent chunks, not missing render.
- Wheel zoom is coalesced: the ready bitmap is soft-scaled between notches and one background
  rebuild runs after the gesture, instead of starting and cancelling a full four-tile pass on every
  notch.
- All visible build tiles now use one quality decision. Exact is used for the whole view when any
  visible tile has no suitable overview (or a filter/Y slice needs exact pixels); otherwise every
  tile uses its overview. The active tile is no longer the only sharp one.
- Extra tile layers survive a zoom/rebuild until their replacement is ready, so neighbouring
  regions no longer blink while the wheel is moving.
- A coarse pyramid of every visible cached tile is painted underneath the exact frames. It is hidden
  while those frames cover the viewport and fills only otherwise-black strips during a pan/zoom
  rebuild, at a pyramid-only tile at detail zoom, or outside a kept frame while the next bitmap is
  built. Genuinely empty chunk columns still show the map background, because there is no terrain
  there to draw.
- A cached tile without a suitable overview pyramid is no longer dropped by the old 4-megapixel
  guard: only its visible intersection is rendered, at the current density.
- A pan now marks the view as user-managed, so resizing the window no longer re-fits the map and
  jumps back to the active tile. The “no map data here” hint is shown only when no cached area
  intersects the viewport, not when the active tile is merely off screen.
- **Find blocks overlay now covers the whole map.** It used to be built against the active build
  tile, so after the map was split into 16M-column tiles the highlights covered one small tile.
  The overlay is now a sparse whole-save result (one part per matching chunk, so memory scales with
  matches, not with the world bounds), stays active across area switches, and is painted over
  overview-only areas with a coarse preview. At detail zoom every visible tile with a cached full map
  gets the exact per-column layer. An area that has only an overview pyramid is still drawn (with
  the coarse preview) when the overlay forces exact rendering for the other tiles; previously it
  was skipped entirely, so panning with an overlay made those regions vanish. The pan-coverage check
  also requires that coarse layer now, so a newly revealed pyramid-only area triggers a rebuild instead
  of staying missing until an unrelated redraw.
- `BottomY` was one block too high in chunks above Y=255: the old packed staging word put the high
  bit of `cy` into the low bit of `bottom`. The map now keeps them as separate ints.
- Scrolling to another map area while the data scan was running could leave the view with only the
  first area until the scan finished: both threads used the same SQLite connection. Reads through
  the main connection are now serialised, so an area builds between scan pages (a page is short).

### Changed

- The default map mode is now **Blocks** (the in-game map colours); Heightmap and Ocean depth
  remain available in Settings.
- The map legend overlay was removed: it duplicated the Settings tab and could extend past the
  map border.
- Release builds are trimmed: each platform is now about 24 MB instead of about 50 MB, so both
  fit the 40 MB VSModDB upload limit.
- Region wipe and the Regions tab were removed: the game regenerates a whole region, including
  buildings, so the result was not predictable. Chunk-column deletion remains.
- The original “read-only MVP” was replaced by a working-copy model with explicit review and
  saving.

### Security

- Hardened parsing of malformed saves, with length guards, output limits and negative tests.

### Known limitations

- No per-cell block painting, fill or copy/paste; blocks are removed/replaced by code only.
- Item-stack contents are searchable but read-only.
- No 3D view.
- No structure import/export or VCDBS version conversion.
- macOS, Linux ARM, Windows ARM and 32-bit builds are not published.
- Binaries are unsigned; there is no auto-update or telemetry.
- Mod block colours are heuristic; vanilla colours come from generated game-asset tables.
- The game remains the final validator for any edited save.

### First release status

The first public build is not tagged yet. The project is released under the MIT No Attribution
(MIT-0) license, and the binaries are unsigned; there is no auto-update or telemetry.

## Scan diagnostics and deferred block statistics

- Scan failures and per-record warnings now go to the console (`[scan][ERROR] …` / `[scan][WARN] …`,
  with the stack trace) instead of only the status line; a damaged playerdata blob reports its id.
- `SaveScanner.Scan(..., computeBlockStats: false)` skips the block histogram — 98 % of the chunk loop on
  a 12-GB save — and `SaveScanner.ComputeBlockStats` fills it afterwards, so the map and the tabs are
  usable while the statistics are still being decoded. The editor runs that pass in the background and
  refreshes the dump, the filter list and the overlay when it lands.
