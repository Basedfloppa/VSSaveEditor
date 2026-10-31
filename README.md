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

You can also find which chest or player holds an item, edit a player's inventory, or change a creature's health and position.

## Editing and safety

Opening a save creates a temporary working copy; the original `.vcdbs` is only read. Every edit goes into that copy.

- **Review before write.** “Review & apply…” shows a before/after diff per chunk or player, with undo, redo and history.
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
- **Item stacks are read-only.** You can find an item and its owner, but not rewrite the stack contents.
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
