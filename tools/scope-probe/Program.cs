using System.Diagnostics;
using VSCore;

// Headless probe of the "Search in" scope: scans a real save and compares the scoped indexes with the
// whole-save ones. Everything it prints is what the tabs would show in the same scope.

var path = args.Length > 0 ? args[0] : throw new ArgumentException("usage: scope-probe <save.vcdbs> [cx,cz]");
string? columnText = args.Length > 1 ? args[1] : null;
var failures = 0;

var sw = Stopwatch.StartNew();
using var save = VSSaveFile.OpenReadOnly(path);
Console.WriteLine($"opened {Path.GetFileName(path)} in {sw.ElapsedMilliseconds} ms");

sw.Restart();
var data = SaveScanner.Scan(save, null, captureRaw: false);
Console.WriteLine($"scanned in {sw.ElapsedMilliseconds} ms — {data.Chunks.Count} chunk rows, "
                  + $"{data.EntityCount} entities, {data.BlockEntityCount} block entities, {data.ItemStackCount} stacks");

var allChunks = ChunkBrowser.Build(data);
var allEntities = EntityBrowser.Build(data);
var allItems = InventoryBrowser.Build(data);
var areas = ChunkAreas.Build(save);
Console.WriteLine($"columns {allChunks.Count}, areas {areas.Areas.Count}, players {data.Players.Count}");

// The column of the densest chunk when no column was given: it is where a user is most likely to start.
var column = columnText is not null
    ? SearchScope.Parse(columnText)
    : SearchScope.ForColumn(allChunks.OrderByDescending(c => c.Entities + c.BlockEntities).First().Cx,
        allChunks.OrderByDescending(c => c.Entities + c.BlockEntities).First().Cz);
Console.WriteLine($"\n== scope: {column.Label}");

var scopedChunks = ChunkBrowser.Build(data, column);
var scopedEntities = EntityBrowser.Build(data, column);
var scopedItems = InventoryBrowser.Build(data, column);

Console.WriteLine($"chunks   : {scopedChunks.Count} of {allChunks.Count}");
Console.WriteLine($"entities : {scopedEntities.Count} of {allEntities.Count} records");
Console.WriteLine($"items    : {scopedItems.Count} of {allItems.Count} stacks, "
                  + $"{InventoryBrowser.CountInventories(scopedItems)} container(s)");

// every scoped row must lie in the column, and the union over all rows must be the whole save again
Check("chunks", scopedChunks.All(c => column.ContainsChunk(c.Cx, c.Cz)), "a chunk outside the column");
Check("entities", scopedEntities.All(e => column.ContainsChunk(e.ChunkX, e.ChunkZ)), "an entity outside the column");
Check("items", scopedItems.All(i => i.IsPlayer == false), "playerdata inside a chunk scope");
Check("items", scopedItems.All(i => column.ContainsChunk(i.Inventory.ChunkX, i.Inventory.ChunkZ)),
    "a container outside the column");

var scopedSum = 0;
foreach (var c in data.Chunks)
    scopedSum += ChunkBrowser.Build(data, SearchScope.ForColumn(c.ChunkX, c.ChunkZ)).Count;
Console.WriteLine($"per-column chunk counts add up to {scopedSum} (whole save: {allChunks.Count})");

var everything = data.WithinScope(null).WorldBlockCodeCounts;
var narrowed = data.WithinScope(column).WorldBlockCodeCounts;
var onlyOutside = everything.Keys.Except(narrowed.Keys).Count();
Console.WriteLine($"block codes: {narrowed.Count} in the scope of {everything.Count} — {onlyOutside} occur only outside it");

foreach (var area in areas.Areas.Take(4))
{
    var areaScope = SearchScope.ForArea(area, areas.Areas.Count);
    var chunks = ChunkBrowser.Build(data, areaScope);
    var ents = EntityBrowser.Build(data, areaScope);
    var items = InventoryBrowser.Build(data, areaScope);
    Console.WriteLine($"{areaScope.Label}: {chunks.Count} columns, {ents.Count} records, {items.Count} stacks");
}

// searching inside the scope: the content query must not see records of the neighbours
var sample = scopedEntities.FirstOrDefault();
if (sample is not null)
{
    var q = sample.Name;
    var inScope = EntityBrowser.Filter(scopedEntities, EntityCategory.All, q).Count;
    var whole = EntityBrowser.Filter(allEntities, EntityCategory.All, q).Count;
    Console.WriteLine($"query \"{q}\": {inScope} of {whole} record(s)");
    Check("entities", inScope <= whole, "more matches in the scope than in the save");
    Check("entities", scopedEntities.All(e => EntityBrowser.Filter(scopedEntities, EntityCategory.All, q).Contains(e)
                                             || !e.Matches(q)), "a matching record missing from the scoped result");
}

var outside = SearchScope.ForRectangle(int.MaxValue - 10, int.MaxValue - 10, int.MaxValue - 9, int.MaxValue - 9);
Console.WriteLine($"\n== outside every area: {ChunkBrowser.Build(data, outside).Count} columns, "
                  + $"{EntityBrowser.Build(data, outside).Count} records, {InventoryBrowser.Build(data, outside).Count} stacks");

var unplaced = InventoryBrowser.Build(data, SearchScope.UnplacedOnly);
Console.WriteLine($"== unplaced (playerdata): {unplaced.Count} stacks in {InventoryBrowser.CountInventories(unplaced)} container(s), "
                  + $"{ChunkBrowser.Build(data, SearchScope.UnplacedOnly.ChunkScopeOf).Count} columns, "
                  + $"{EntityBrowser.Build(data, SearchScope.UnplacedOnly.ChunkScopeOf).Count} records");

Console.WriteLine("\nRESULT: " + (failures == 0 ? "OK" : $"{failures} FAILURE(S)"));
return failures == 0 ? 0 : 1;

void Check(string what, bool ok, string message)
{
    if (ok) return;
    failures++;
    Console.WriteLine($"FAIL {what}: {message}");
}
