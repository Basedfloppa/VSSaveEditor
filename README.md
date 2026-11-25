# VS Save Editor

**Inspect and edit a Vintage Story world without launching the game.**

[![Status: pre-release](https://img.shields.io/badge/status-pre--release-orange)](#project-status)
[![Platform: Linux x64 | Windows x64](https://img.shields.io/badge/platform-Linux%20x64%20%7C%20Windows%20x64-blue)](#install)
[![License: MIT-0](https://img.shields.io/badge/license-MIT--0-blue)](#license)

VS Save Editor opens a `.vcdbs` save directly, builds a map of the world, and lets you find and edit chunks, creatures, players, inventories and blocks. Every edit goes into a working copy — the original save is never overwritten.

## Project status

> **Public alpha.** Project is finally in a usable state, but probably full of bugs and problems, so v0.0 exists to catch any problem early.

## Why this exists

A world inspection and preparation tool for server owners, modpack maintainers and world designers — for auditing, tuning and preparing a world, not as a shortcut through gameplay.

- **Build a map of a server world.** Render the world as a top-down map, switch to a Y level to see caves and ores, and export PNG, JPEG or WebP. Large saves normally open on the tile containing spawn; pan to build or reveal the other build tiles.
- **Map a road network.** Search for the block code used by your roads, highlight every matching column, and export a clean map.
- **Find a lost pet.** Filter creatures by name, species or mod, read its coordinates, and open the chunk.
- **Audit resource and spawn distribution.** Highlight ore blocks and list creatures by type and location to verify worldgen, spawn balance and modded placement.
- **Remove a mod safely.** Find the block codes left by the mod, remove or replace them, review the diff, and save a copy before uninstalling the mod.
- **Restock or clean up a trader.** Open a trader as a document (`Entities → Entity data…`), edit a price or the stock, delete an offer or a slot, or cut a mod's leftover fields out of a creature — every change is one reviewable pending edit.

- **Read the world settings the save carries.** The **World** tab parses the world tree out of `gamedata`
  (`gameMode`, `daysPerMonth`, `landformScale`, `worldWidth`, …) and lists every entry beside the mod entries
  stored in the same tree, with a filter and each value in full. Read-only for now: there is no write path for
  this tree yet.

You can also find which chest or player holds an item, edit a player's inventory, edit a creature's fields and position, and edit a trader's offers.

## Editing and safety

Opening a save creates a temporary working copy in the app's data folder (not in the system temp
folder, which is RAM-backed on many Linux systems); the original `.vcdbs` is only read. Every edit goes
into that copy. `VSSAVEEDITOR_WORK_DIR` overrides the location.

- **Review before write.** “Review & apply…” shows a before/after diff per chunk, player or entity record, with undo, redo and history.
- **Save a copy.** Writes a new `.vcdbs` next to the original, by default `<name> (edited).vcdbs`. There is no “save over the original” command.
- **Stop the game or server first.** The editor cannot guarantee a consistent view while the world is still being written.
- **Back up worlds you care about** before replacing files manually.
- Malformed input is handled defensively, but the game remains the final validator.

## Install

Download the asset for your platform from **Releases** once the first public release (0.1.0) is available. The `v0.0.x` tags are development tags, not published release assets:

| Platform | Asset |
|---|---|
| Linux x64 | `VSSaveEditor-<version>-linux-x64` + `.sha256` |
| Windows x64 | `VSSaveEditor-<version>-win-x64.exe` + `.sha256` |

Linux:

```bash
sha256sum -c VSSaveEditor-0.1.0-linux-x64.sha256
chmod +x VSSaveEditor-0.1.0-linux-x64
./VSSaveEditor-0.1.0-linux-x64
```

Windows: run the `.exe`. SmartScreen may warn because the binaries are currently unsigned.

## Known limitations

- **No block painting or 3D view.** Blocks can be removed or replaced by code, not painted individually; the map and Y slices are 2D.
- **The Items tab is read-only.** It finds an item and its owner; a stack is edited where it belongs to an owner. In `Entities → Entity data…` a slot's item can be opened and its attributes (condition, temperature…) edited, and deleting it takes a trader's offer with it. In `Players → Player data…` an item can be deleted but its attributes are not shown as fields yet.
- **The World tab edits values, not their meaning.** `gameMode`, `daysPerMonth` and the rest are editable
  (`gamedata` field 31, one pending edit each), but the editor does not know the game's list of accepted values —
  a mistyped setting is stored as typed and ignored by the game. Attributes the game reads only at world
  creation (`landformScale`, `upheavelCommonness`, `worldWidth`, …) are written too, with a warning: an existing
  world keeps its already generated terrain.
- **No whole-region wipe.** Deleting a region would regenerate everything in it, including buildings, so the feature was removed. Chunk-column deletion remains.
- **No version conversion.** Saves outside the tested 1.22.x range may parse partially.
- **The game is the final validator.** The editor does not run Vintage Story's own validation; always load a copy before using it on a server.
- **No macOS/ARM packages, code signing, auto-update or telemetry.**

## Feedback

Bug reports and reproducible save-format findings are welcome on the issue tracker. Include the Vintage Story version, the platform, and — for parser bugs — the smallest save that reproduces the problem. Do **not** attach a full world save unless you are comfortable making it public; a reduced fixture is better.

## Not affiliated with Anego Studios

VS Save Editor is an independent community project. It is not affiliated with, endorsed by,
sponsored by, or connected to Anego Studios or the Vintage Story team. Vintage Story and related
names and marks are the property of their respective owners. The editor reads `.vcdbs` saves; it
does not bundle the game client or server.
