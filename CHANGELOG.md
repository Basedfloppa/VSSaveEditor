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

### Fixed

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
