# VS Save Editor — user guide

This guide covers the normal workflow: open a world, inspect it, make an edit, review the result, and save a copy.

VS Save Editor is an independent community tool. It is not affiliated with, endorsed by, sponsored
by, or connected to Anego Studios or the Vintage Story team.

## Before you start

- **Back up the world.** The editor never writes to the file you open, but a backup is still the simplest protection against mistakes elsewhere.
- **Close the game or server.** The editor can read a WAL snapshot, but it cannot guarantee a consistent view while the world is being written.
- Use a world saved by **Vintage Story 1.22.6 or 1.22.7**. Other versions may open partially, but are not tested.

## Open a world

1. Start VS Save Editor.
2. Choose **Open…** (`Ctrl+O`), pick a `.vcdbs` file, or choose a recent world from the start panel.
3. Wait for the background scan. The map appears first; chunk, entity and item indexes fill in as the scan finishes.

Opening a world creates a disposable **working copy**. The original `.vcdbs` is opened read-only and is never the target of an edit.

## The map

| Action | Result |
|---|---|
| Drag | Pan the map. |
| Mouse wheel | Zoom in or out. |
| **Fit** | Frame the currently selected area or the whole world. |
| Render mode | Switch between the game-like **Blocks** view and other available modes. |
| **Y slice** | Show the real block at one Y level. Air stays in the background, so caves and ores become visible. |
| **Block overlay** | Search the save's own block list, tick codes, and highlight matching columns. |
| **Chunk grid** | Show chunk boundaries. |

Click a chunk to select it. `Shift+LMB` selects a rectangular region; `Shift+RMB` removes chunks from the selection.

The **Export** controls write PNG, JPEG or lossless WebP at 1–8 pixels per block. You can export the current view, the whole area, or only selected chunks, with the current render mode, Y slice and overlay applied.

## Tabs

| Tab | Use it for |
|---|---|
| **Chunks** | Find a chunk by coordinates, counters or content; inspect its blocks and records. |
| **Entities** | Find creatures and block entities, inspect their fields, and jump to their chunk. |
| **Items** | Find who holds an item, in which container and slot, and jump to the owner or chunk. |
| **Players** | Browse `playerdata`, edit player inventory fields, and show a player on the map when their creature exists. |
| **Find blocks** | Remove or replace block codes across the world. |

The **Chunks**, **Entities**, **Items**, **Players** and **Find blocks** tabs have a **Search in:** row that limits the search to one chunk, a rectangle or one map area — see [Search scope](#search-scope).
| **Selection** | Delete the selected chunk columns from the working copy. |
| **World** | Read version, seed, world size, areas and save diagnostics. |
| **Settings** | Change map, export and display options. |

On the **Entities**, **Items** and **Players** tabs, the list and detail card can be resized with the divider between them. The detail card starts wider than the list.

## Finding data

### Search scope

Each search tab has a **Search in:** row under its toolbar. It limits that tab — and only that tab — to part of the save:

| What | How |
|---|---|
| The whole save | **Whole save** (the default). |
| One chunk | Type `15991, 15983` (chunk X and Z) and press Enter, or click **This chunk on the map**. |
| A rectangle of chunks | Type `15991, 15983, 16010, 16000` — `cx1, cz1, cx2, cz2`. |
| One map area | Click **Area N of M**; a save with distant pregen patches has several. |

The label next to the box names the active scope, and the status line reports how much it found. A rectangle that lies outside every area says so instead of quietly searching the whole save.

A scope is two-dimensional: every Y level of the chosen chunks is inside it. Typing a scope into one tab leaves the other tabs' scopes alone, so the Chunks tab can show one chunk while Items shows the whole save.

How each tab reads the scope:

| Tab | Inside the scope |
|---|---|
| **Chunks** | Chunk columns whose own coordinates are in the rectangle. |
| **Entities** | Creatures and block entities standing in the rectangle. |
| **Items** | Containers and creature inventories in the rectangle. **Player inventories are only listed when the scope is the whole save** — `playerdata` has no position, and a player's backpack travels with the player, not with the chunk. |
| **Players** | Lists nobody in a chunk scope: only the player's creature is placed in the world, and it is in the Entities tab. |
| **Find blocks** | The block list and the "Show on map" overlay count only the blocks inside the scope; the overlay itself stays aligned with the whole map. |

Every scope goes back to the whole save when another save is opened.

### Chunks

The **Chunks** search accepts several terms at once. All terms must match; a space and a comma are equivalent.

| Term | Meaning |
|---|---|
| `15991 15983`, `x15991`, `z15983` | Exact column coordinates. |
| `15991..16000`, `cx15991..16000`, `cz15983..15990` | Coordinate range. Without an axis, either axis may match. |
| `ent>3`, `entities>=2`, `be>0`, `blocks>100`, `yc>1`, `raw>4096` | Record counters. |
| `y110` | A Y column exists at this level. |
| `empty` | No solid or fluid cells. |
| any text | Search species, entity fields, block entities and block codes. |

An unparsable counter is ignored and the reason is shown in the summary instead of breaking the search. The list shows up to 400 matches at a time; the summary reports the total.

Selecting a row opens the chunk. The card shows coordinates, version, block statistics, entity and block-entity cards, and a hex preview. Use **Edit fields** for recognized values. For data the parser cannot show as fields, **Raw hex** can replace the whole chunk blob; use it only with a known-good payload.

### Entities

The **Entities** list contains creatures and block entities. Filter by name, species, code, field value or mod domain, or use **All / Entities / Block entities** to narrow the category.

Selecting a row opens a card with the real position and recognized fields. **Show chunk on map** opens the chunk, and **Edit fields** opens the field editor.

Nested values such as `health.currenthealth`, `hunger.saturation`, arrays, booleans and `pos.x/y/z` are shown as separate fields. Bytes the editor cannot interpret are preserved and can be changed through **Raw payload (hex)**.

### Items

The **Items** tab searches player inventories, entity inventories and block-entity containers such as chests, barrels and crates.

The free-text field searches code, owner, container, slot and attributes. Prefixes narrow the search:

```text
item:ore-quartz
owner:player-name
container:chest
slot:3
attr:condition
chunk:16012,16001
count>=10
```

Terms are combined with AND. A malformed `chunk:` term is reported in the summary without clearing the list. **This chunk** limits the result to the chunk currently shown on the map.

**Show chunk on map** jumps to the stack's chunk. **Open owner** opens the player or container record.

Item stacks are currently **read-only**: you can locate an item, but not rewrite its contents.

### Players

The **Players** tab lists `playerdata` records. It supports filtering, inventory editing by index and pending-edit markers. When the player's creature exists in the world, **Show on map** jumps to it.

### Find blocks

Use **Find blocks** to clean up blocks from a removed mod or replace one block code with another:

1. Search for a block code. A colon (`:`) keeps only modded codes.
2. Tick the codes you want to change.
3. Choose **Remove selected…** to turn them into air, or **Replace selected…** and pick a target block from the save.
4. Confirm the operation.

Only the palette changes; the rest of the chunk data stays byte-for-byte intact. The operation edits the working copy and is recorded in History. Removing a block does not remove its block entity, and the in-game map may keep the old colours until that chunk is saved again.

### Selection

The **Selection** tab deletes every Y level of the selected chunk columns from the working copy. The game regenerates those chunks on the next load. Use this for broken or unwanted chunks, not as a general terrain editor.

## Editing and saving

1. **Edit a record.** Use **Edit fields** on a chunk, entity or block-entity card, or edit a player inventory in the **Players** tab.
2. **Review.** Open **Review & apply…**. The dialog shows a before → after diff for each chunk or player. You can filter the list, copy it as text, or export a text report.
3. **Apply.** Applying moves the edits into the working copy. The cards and tabs re-read the changed data.
4. **Save a copy.** Choose **Save a copy…**. The editor writes a new `.vcdbs` next to the original, by default `<name> (edited).vcdbs`.

There is intentionally no command that overwrites the original file.

A chunk scope is a display and search filter only. A block removal or a chunk-column deletion still works on **every ticked code in the world** and on the **selected chunks on the map**, whatever scope the tab shows.

- **Undo / redo** works on pending edits. The History panel also keeps applied edits after they are applied.
- **Save a copy…** writes a self-contained file atomically. No `-wal` or `-shm` sidecar is left behind.
- Closing the window asks whether to save the working copy or discard it.

Always open the **saved copy**, not the original, when checking the result in Vintage Story.

## Safety

- The original `.vcdbs` is opened read-only.
- All changes go into a temporary working copy.
- **Save a copy…** writes a separate file; the original is never overwritten.
- Block and chunk actions require confirmation and only affect the copy.
- Field edits are reviewable as a before → after diff before they are applied.

The game remains the final validator. The editor does not run Vintage Story's own validation; load a copy in the game before using it on a server.

## Known limitations

- **No block placement or layout editing.** Blocks can be removed or replaced by code, not painted individually.
- **Item stacks are read-only.**
- **No 3D view.** The map and Y slices are 2D.
- **Region wipe is not available.** Deleting a `mapregion` regenerates the whole region, including buildings, so the feature was removed as unsafe. Chunk-column deletion remains.
- **Mod block colours are heuristic.** Vanilla colours come from the game assets; mod blocks use stable fallback colours.
- **No version conversion.** Saves outside the tested 1.22.x range may parse partially.
- **No macOS or ARM packages yet.**
- **No code signing, auto-update or telemetry.**

## Troubleshooting

| Problem | What to try |
|---|---|
| The map is empty after opening | Wait for the scan to finish, then use **Fit**. A world with only unloaded chunks may have little to draw. |
| The save will not open | Close the game or server that is using it. Check that the file is a `.vcdbs` world, not a backup or an unrelated SQLite database. |
| Edits are missing in the game | Open the file written by **Save a copy…**, not the original. Deleted chunks are regenerated by the game. |
| A field cannot be edited | It may be part of an unrecognized payload. Use **Raw payload (hex)** on the record, or leave it unchanged. |
| A block or item is missing from a list | The scan indexes what the save contains. A code that exists only in an unused palette slot, or a record with no recognizable fields, may not appear. |

## More information

- [CHANGELOG.md](../CHANGELOG.md) — changes in each release.
- [README.md](../README.md) — feature overview, supported platforms and installation.

## Repository scripts

Not part of the app download; they are for working on the source tree.

| Script | Purpose |
|---|---|
| `tools/gen_block_colors.py <assets> VSCore/BlockColors.txt` | Regenerates the block colour table from an installed Vintage Story. Colours are averaged from the real textures, so mod blocks keep heuristic colours until the table is rebuilt; the script needs the game assets. |
| `tools/check-deps.sh` | CI check: every lock file matches the resolved graph and no package has a known vulnerability. Covers the solution plus the projects outside it (`.uisnap/`, `tools/scope-probe/`). |
| `tools/publish-release.sh`, `tools/publish-release.ps1` | Builds the same single-file linux-x64/win-x64 assets the release workflow does, into `artifacts/release/`, so a release build can be checked locally. |
| `tools/scope-probe` | Headless check of the **Search in** scope on a real save: prints scoped versus whole-save counts and fails when a scoped row lies outside the scope. `dotnet run --project tools/scope-probe -c Release <save.vcdbs> [cx,cz]` — no window or display needed. |

## License

VS Save Editor is released under the MIT No Attribution (MIT-0) license. See [LICENSE](../LICENSE).
