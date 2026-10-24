# Changelog

All notable changes to VS Save Editor are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow semantic versioning.

## [Unreleased] — planned first public release `0.1.0`

### Added

- 2D terrain map rendered in the in-game map's colours, with render modes, LOD, chunk grid and
  hover readout.
- Connected-area map building for worlds with distant pregen patches; distant areas load when
  panned to.
- Real Y-layer slice with on-demand voxel decoding.
- Chunk search by coordinates, ranges, counters, layer, empty state and content.
- Entity and block-entity index with field editing, nested paths, real positions and raw hex
  fallback.
- Item-stack search across players, creatures and containers, with navigation to the owner and
  the chunk.
- Players tab for `playerdata`, including inventory editing by index.
- Block overlay and block removal/replacement by code, editing the palette only.
- Chunk-column deletion from the working copy.
- Non-destructive working-copy editing, before/after review, undo/redo and edit history.
- “Save a copy…” to a new `.vcdbs`; the original is never overwritten.
- Map export to PNG, JPEG and lossless WebP, whole area or selected chunks.
- Recent-saves list, start panel, dark/light theme and English UI.
- MIT No Attribution (MIT-0) license.

### Changed

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
