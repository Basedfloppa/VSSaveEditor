using Avalonia.Controls;
using VSCore;
using VSSaveEditor.Components;

namespace VSSaveEditor;

/// <summary>
///     The "Search in:" scope of the tabs.
///
///     Each tab used to search the whole save, and there was no way to ask the same question about one place: "which
///     chests in this chunk are full" meant walking the map and filtering by hand. The scope is per tab, not global -
///     a chunk is a natural frame for blocks and containers and a meaningless one for players - but every tab
///     derives it from the same map state, so "This chunk" means the same chunk everywhere.
///
///     Who owns what: the tab only reports the text that was typed or picked (<c>ScopeBar.ScopeRequested</c>).
///     Areas and the map selection exist only in the window, so it resolves the text into a
///     <see cref="SearchScope" />, keeps one per tab, and rebuilds exactly the tab that asked — the other tabs keep
///     the scope the user chose for them.
///
///     The area list belongs to "Find blocks" alone. There the overlay is built from the ticked block codes, so the
///     areas worth offering are exactly the ones that contain those codes; the other tabs search records, not blocks,
///     and a list of areas that holds none of them would send the user to places with nothing to find.
/// </summary>
public partial class MainWindow
{
    // one scope per tab: the search scope survives switching tabs and a rescan
    private SearchScope _chunkScope = SearchScope.Everything;
    private SearchScope _entityScope = SearchScope.Everything;
    private SearchScope _itemScope = SearchScope.Everything;
    private SearchScope _playerScope = SearchScope.Everything;
    private SearchScope _filterScope = SearchScope.Everything;

    // which areas of the save contain the ticked codes, and for which selection it was computed:
    // the list is rebuilt on every keystroke of the block search, and walking every chunk record for that
    // would be wasted work
    private IReadOnlyList<AreaMatch> _areaMatches = Array.Empty<AreaMatch>();
    private string? _areaMatchesKey;
    private SaveScanResult? _areaMatchesScan;

    private void WireScopes()
    {
        ChunkDataView.ScopeRequested += (_, text) => ApplyScope(text, ScopeTarget.Chunks);
        EntitiesView.ScopeRequested += (_, text) => ApplyScope(text, ScopeTarget.Entities);
        ItemsView.ScopeRequested += (_, text) => ApplyScope(text, ScopeTarget.Items);
        PlayersView.ScopeRequested += (_, text) => ApplyScope(text, ScopeTarget.Players);
        FindBlocksView.ScopeRequested += (_, text) => ApplyScope(text, ScopeTarget.Blocks);
    }

    private enum ScopeTarget
    {
        Chunks,
        Entities,
        Items,
        Players,
        Blocks
    }

    /// <summary>
    ///     Resolves the requested text and rebuilds only the asking tab. The reasons to refuse are named: an
    ///     unparsable rectangle would otherwise silently fall back to "whole save", which is the opposite of narrowing.
    /// </summary>
    private void ApplyScope(string text, ScopeTarget target)
    {
        // The area chips are a selection editor ("sel=3", "sel=all", "sel=none"), not a scope: they change which
        // chunks are selected on the map and leave the search scope, the picture and the camera alone.
        var requested = (text ?? "").Trim();
        if (requested.StartsWith("sel=", StringComparison.OrdinalIgnoreCase))
        {
            ToggleSelection(requested[4..]);
            return;
        }

        SearchScope scope;
        try
        {
            scope = ResolveScope(text);
        }
        catch (FormatException ex)
        {
            Status.Set(StatusChannel.Search, ex.Message, priority: AppStatus.PriorityError);
            return;
        }

        SetScope(target, scope);
        if (ScanData is null)
        {
            // the choice is kept: the scan that arrives later builds its index in this scope
            UpdateScopeBars(writeBox: true);
            Status.Set(StatusChannel.Search, $"Scope: {scope.Label} (applied when the scan finishes).");
            return;
        }

        switch (target)
        {
            case ScopeTarget.Chunks:
                RefreshChunkScope();
                break;
            case ScopeTarget.Entities:
                _entities.SetIndex(ScanData, _entityScope);
                break;
            case ScopeTarget.Items:
                _items.SetIndex(ScanData, _itemScope);
                break;
            case ScopeTarget.Players:
                _players.Refresh();
                break;
            case ScopeTarget.Blocks:
                // The picture on the map belongs to the codes and to "Show on map" — choosing another area or
                // clearing the choice must not wipe it. A build that is still running is stopped (its result would
                // belong to the old scope); the finished overlay stays, and the status says it is for another scope.
                _filterCts?.Cancel();
                PopulateFilterList();
                UpdateFilterStatus();
                break;
        }

        UpdateScopeBars(writeBox: true);
        Status.Set(StatusChannel.Search, ScopeReport(target, scope));
    }

    /// <summary>The one line the status bar shows after a scope change: the scope and what it found.</summary>
    private string ScopeReport(ScopeTarget target, SearchScope scope)
    {
        // an empty scope is named once: "chunk X, Y — nothing to search for item stacks" already says why there are none
        if (scope.IsEmpty) return $"Scope: {scope.EmptyReason}.";
        var n = target switch
        {
            ScopeTarget.Chunks => $"{_chunkView.ShownCount} chunk(s)",
            ScopeTarget.Entities => $"{_entities.ShownCount} of {_entities.TotalCount} record(s)",
            ScopeTarget.Items => $"{_items.ShownCount} of {_items.TotalCount} item stack(s)",
            ScopeTarget.Players => scope.HasRegion
                ? "no players — playerdata has no position, so a chunk does not contain it"
                : PlayerCountLabel.Text ?? "",
            _ => $"{ScopedBlockCounts().Count:N0} block code(s)",
        };
        return $"Scope: {scope.Label} — {n}.";
    }

    /// <summary>Turns "area=2", "areas=1,3", "cx,cz", "cx1,cz1,cx2,cz2" or "" into a scope against this save's areas.</summary>
    private SearchScope ResolveScope(string text)
    {
        var t = (text ?? "").Trim();
        if (t.StartsWith("area=", StringComparison.OrdinalIgnoreCase))
        {
            if (_areas is not { Areas.Count: > 0 } areas)
                throw new FormatException("The save has no map areas yet — open a save and wait for the map.");
            if (!int.TryParse(t[5..], out var number) || number < 1 || number > areas.Areas.Count)
                throw new FormatException($"There is no area {t[5..]} — this save has {areas.Areas.Count} area(s).");
            return SearchScope.ForArea(areas.Areas[number - 1], areas.Areas.Count);
        }

        if (t.StartsWith("areas=", StringComparison.OrdinalIgnoreCase))
            return ResolveAreaSet(t[6..]);

        var scope = SearchScope.Parse(t);
        // A rectangle over the void between two areas is a legitimate choice and an empty result is the honest answer;
        // saying which is which saves the user from thinking the scan broke.
        if (scope.HasRegion && ScanData is not null && _areas is { Areas.Count: > 0 } known)
        {
            var touched = known.Areas.Any(a => a.MinCx <= scope.MaxCx && a.MaxCx >= scope.MinCx &&
                                               a.MinCz <= scope.MaxCz && a.MaxCz >= scope.MinCz);
            if (!touched) scope.EmptyReason = "no chunk of the save lies in this rectangle";
        }

        return scope;
    }

    /// <summary>The tail of "areas=…": the numbers are 1-based, the same ones the list shows. An empty tail
    /// ("areas=") means no area is ticked, which is the whole save — that is what Clear sends.</summary>
    private SearchScope ResolveAreaSet(string numbers)
    {
        if (_areas is not { Areas.Count: > 0 } areas)
            throw new FormatException("The save has no map areas yet — open a save and wait for the map.");
        var picked = new List<ChunkArea>();
        foreach (var part in numbers.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var number) || number < 1 || number > areas.Areas.Count)
                throw new FormatException($"There is no area {part} — this save has {areas.Areas.Count} area(s).");
            if (!picked.Any(a => a.Index == number - 1)) picked.Add(areas.Areas[number - 1]);
        }

        return SearchScope.ForAreas(picked, areas.Areas.Count);
    }

    private void SetScope(ScopeTarget target, SearchScope scope)
    {
        switch (target)
        {
            case ScopeTarget.Chunks: _chunkScope = scope; break;
            case ScopeTarget.Entities: _entityScope = scope; break;
            case ScopeTarget.Items: _itemScope = scope; break;
            case ScopeTarget.Players: _playerScope = scope; break;
            case ScopeTarget.Blocks: _filterScope = scope; break;
        }
    }

    private SearchScope ScopeOf(ScopeTarget target) => target switch
    {
        ScopeTarget.Chunks => _chunkScope,
        ScopeTarget.Entities => _entityScope,
        ScopeTarget.Items => _itemScope,
        ScopeTarget.Players => _playerScope,
        _ => _filterScope
    };

    /// <summary>Rebuilds the chunk index for its scope and the current query (the "Find chunk" box is ANDed with it).</summary>
    private void RefreshChunkScope()
    {
        if (ScanData is { } data) _chunkView.SetChunkIndex(ChunkBrowser.Build(data, _chunkScope.ChunkScopeOf), _chunkScope);
        else _chunkView.SetChunkIndex(Array.Empty<ChunkColumnInfo>(), _chunkScope);
    }

    /// <summary>
    ///     Redraws every scope row. <paramref name="writeBox" /> is set only for scopes that came from the bar or
    ///     from code ("area=1", "cx,cz", ""): then the box is the statement of the scope, including "empty" for the
    ///     whole save. An ambient redraw (the map selection changed, the shown chunk changed) leaves a half-typed
    ///     range alone and only refreshes the proposals.
    /// </summary>
    private void UpdateScopeBars(bool writeBox = false)
    {
        var blocks = AreaListForFindBlocks();
        ChunkDataView.Scope.ShowState(DescribeScope(_chunkScope, "chunks"), BoxText(_chunkScope, writeBox), Proposals(), null, "Whole save");
        EntitiesView.Scope.ShowState(DescribeScope(_entityScope, "records"), BoxText(_entityScope, writeBox), Proposals(), null, "Whole save");
        ItemsView.Scope.ShowState(DescribeScope(_itemScope, "item stacks"), BoxText(_itemScope, writeBox), Proposals(), null, "Whole save");
        PlayersView.Scope.ShowState(DescribeScope(_playerScope, "players"), BoxText(_playerScope, writeBox), Proposals(), null, "Whole save");
        FindBlocksView.Scope.ShowState(DescribeScope(_filterScope, "block codes"), BoxText(_filterScope, writeBox), Proposals(), blocks, "All");
    }

    /// <summary>
    ///     The area list of the "Find blocks" row is an editor of the CHUNK SELECTION, not of the block-code filter:
    ///     a chip is ticked when the chunks selected on the map reach into that area, and clicking it adds the area's
    ///     chunks to the selection or drops the selected ones from it. That is why the list is there even with
    ///     nothing ticked — it is how a selection is grown by whole areas.
    ///     It is null only when the save has a single area: then there is nothing to choose between.
    /// </summary>
    private AreaListInfo? AreaListForFindBlocks()
    {
        if (_areas is not { Areas.Count: > 1 } areas) return null;
        var chosen = SelectedAreaIndices(areas);

        var chips = new List<AreaChip>(areas.Areas.Count);
        foreach (var area in areas.Areas)
        {
            var number = area.Index + 1;
            var picked = chosen.Contains(area.Index);
            // with blocks ticked a click brings the chunks that hold them, so the tooltip must not promise the whole
            // area: the overlay is built from those codes and the rest of the area would only show empty ground
            var what = _filterCodes.Count == 0
                ? "all of its chunks"
                : $"the chunks of this area that hold {_filterCodes.Count} ticked block code(s)";
            var tip = $"area {number} of {areas.Areas.Count} — {area.Columns:N0} chunks, "
                      + $"X {area.MinX}..{area.MaxX}, Z {area.MinZ}..{area.MaxZ}; "
                      + (picked
                          ? $"in the selection ({Map.SelectionCount:N0} chunk(s) selected) — click to drop {what}"
                          : $"not in the selection — click to add {what}");
            chips.Add(new AreaChip(number.ToString(), tip, $"sel={number}", picked));
        }

        return new AreaListInfo(chips, "sel=all", "sel=none");
    }

    /// <summary>Which areas the current chunk selection reaches into — the tick state of the area list.</summary>
    private HashSet<int> SelectedAreaIndices(ChunkAreas areas)
    {
        var chosen = new HashSet<int>();
        foreach (var (cx, cz) in Map.SelectedChunks)
        {
            var area = areas.AreaAt(cx * 32 + 16.0, cz * 32 + 16.0);
            if (area is not null) chosen.Add(area.Index);
        }

        return chosen;
    }

    /// <summary>
    ///     The chunk selection is edited by whole areas from here: a chip adds every chunk of its area, or drops the
    ///     selected chunks that lie inside it. The map is not moved and the block overlay is not touched — the
    ///     picture belongs to the codes and to "Show on map", not to what is selected right now.
    /// </summary>
    private void ToggleSelection(string what)
    {
        if (_areas is not { Areas.Count: > 0 } areas || ScanData is not { } scan) return;
        var next = new HashSet<(int cx, int cz)>(Map.SelectedChunks);
        var selection = what.Trim();
        if (selection.Equals("none", StringComparison.OrdinalIgnoreCase))
            next.Clear();
        else if (selection.Equals("all", StringComparison.OrdinalIgnoreCase))
            foreach (var area in areas.Areas) next.UnionWith(AreaChunks(scan, area, areas));
        else if (int.TryParse(selection, out var number) && number >= 1 && number <= areas.Areas.Count)
        {
            var mine = AreaChunks(scan, areas.Areas[number - 1], areas);
            // fully there → take it out; partly there (a drag inside the area, or a few of its chunks) → complete it.
            // A partly selected area must not vanish on the first click: the click says "this area", and the answer
            // to "is it in the selection" is the whole of it, not whichever chunks happened to be picked by hand.
            if (mine.Length > 0 && mine.All(next.Contains)) next.ExceptWith(mine);
            else next.UnionWith(mine);
        }
        else
        {
            Status.Set(StatusChannel.Search, $"There is no area {selection} in this save.", priority: AppStatus.PriorityError);
            return;
        }

        if (next.Count == 0 && _filterCodes.Count > 0 && Map.SelectionCount == 0)
        {
            // codes are ticked, yet no chunk of the save holds them: the list has nothing to add, and saying
            // "the selection is empty" would hide the reason
            var anywhere = ColumnsFor(scan).Length;
            if (anywhere == 0)
            {
                Status.Set(StatusChannel.Search,
                    "None of the ticked block codes occurs in this save — there is nothing to select. "
                    + "Untick them to select whole areas.", priority: AppStatus.PriorityError);
                return;
            }
        }

        Map.SetSelectedChunks(next);
        // the list edits the selection, so the row must say what the selection now is — without moving the camera
        var areasTouched = _areas is { } a2 ? SelectedAreaIndices(a2).Count : 0;
        Status.Set(StatusChannel.Search,
            Map.SelectionCount == 0
                ? "The chunk selection is empty."
                : $"{Map.SelectionCount:N0} chunk(s) selected"
                  + (_filterCodes.Count == 0 ? "" : $" — only chunks that hold the ticked block code(s)")
                  + (areasTouched > 0 ? $" in {areasTouched} area(s)." : "."));
    }

    // The columns a click may select, cached per scan and per code set: one pass over the chunk records answers every
    // area (filtering by a rectangle afterwards is cheap), and changing the ticks must not answer with the old set.
    private (int cx, int cz)[] _columnsCache = Array.Empty<(int cx, int cz)>();
    private SaveScanResult? _columnsCacheScan;
    private string _columnsCacheKey = "";

    /// <summary>
    ///     The chunk columns a click on an area selects: with blocks ticked — only the ones that HOLD them, because the
    ///     overlay is built from those codes and the rest of the area would show empty ground; with nothing ticked —
    ///     every column, since there is no block to be restricted to.
    /// </summary>
    private (int cx, int cz)[] ColumnsFor(SaveScanResult scan)
    {
        var codes = _filterCodes.OrderBy(c => c, StringComparer.Ordinal).ToArray();
        var key = string.Join("\n", codes) + "\n" + scan.BlockStatsComputed;
        if (ReferenceEquals(_columnsCacheScan, scan) && _columnsCacheKey == key) return _columnsCache;

        if (codes.Length == 0)
        {
            var all = new List<(int cx, int cz)>();
            foreach (var cd in scan.Chunks) all.Add((cd.ChunkX, cd.ChunkZ));
            _columnsCache = all.Distinct().ToArray();
        }
        else
        {
            _columnsCache = BlockSelectionAreas.ChunksWithCodes(scan, _save?.BlockRegistry, codes).ToArray();
        }

        _columnsCacheScan = scan;
        _columnsCacheKey = key;
        return _columnsCache;
    }

    /// <summary>The part of <see cref="ColumnsFor" /> that lies inside one area's rectangle.</summary>
    private (int cx, int cz)[] AreaChunks(SaveScanResult scan, ChunkArea area, ChunkAreas areas)
        => ColumnsFor(scan)
            .Where(c => c.cx >= area.MinCx && c.cx <= area.MaxCx && c.cz >= area.MinCz && c.cz <= area.MaxCz)
            .ToArray();

    /// <summary>"areas=1,4" for the given area indices, "" when none is chosen — which is the whole save.</summary>
    private static string AreasCommand(IEnumerable<int> areaIndices)
    {
        var parts = areaIndices.Distinct().OrderBy(i => i).Select(i => (i + 1).ToString()).ToArray();
        return parts.Length == 0 ? "" : "areas=" + string.Join(",", parts);
    }

    /// <summary>
    ///     The areas that contain the ticked codes, computed from the scan histogram. Cached per selection and per
    ///     scan: the block list is rebuilt on every keystroke, and walking every chunk record for a search box
    ///     that changed nothing about the codes would be wasted work on a save with millions of rows.
    /// </summary>
    private IReadOnlyList<AreaMatch> AreaMatches()
    {
        if (ScanData is not { } scan || _areas is null || _filterCodes.Count == 0)
            return Array.Empty<AreaMatch>();
        var codes = _filterCodes.OrderBy(c => c, StringComparer.Ordinal).ToArray();
        // the block statistics arrive after the scan: before them every histogram is empty and the answer
        // would be a cached "no area contains it" for the whole session
        var key = string.Join("\n", codes) + "\n" + scan.BlockStatsComputed;
        if (_areaMatchesScan == scan && _areaMatchesKey == key) return _areaMatches;
        _areaMatches = BlockSelectionAreas.Find(scan, _areas, _save?.BlockRegistry, codes);
        _areaMatchesKey = key;
        _areaMatchesScan = scan;
        return _areaMatches;
    }

    /// <summary>
    ///     What a scope means for one tab. The chunk scope is stated in blocks as well as chunks: a user picks a chunk
    ///     on the map by its outline, and the numbers that identify it in the search boxes are chunk coordinates.
    /// </summary>
    private static string DescribeScope(SearchScope scope, string what)
    {
        if (scope.IsEmpty) return $"{scope.EmptyReason} — nothing to search for {what}";
        if (!scope.HasRegion) return $"{scope.Label} — all {what} of the save";
        // Several areas have no single rectangle, so they are not stated as one: the label names the numbers and
        // the block range of each area is in the tooltip of its button.
        if (scope.Kind == SearchScope.ScopeKind.Areas) return $"{scope.Label} — {what} of the chosen areas";
        var size = $"{scope.Label} — {what} of X {scope.MinCx * 32}..{scope.MaxCx * 32 + 31}, Z {scope.MinCz * 32}..{scope.MaxCz * 32 + 31}";
        return scope.Kind == SearchScope.ScopeKind.Area ? size : size + "; every Y level";
    }

    /// <summary>The text the box shows for a scope — only forms that can be typed back in: a rectangle or a column
    /// (and "areas=…", which the box parses as well). An area has no text form, since its numbers belong to the save.</summary>
    private static string? BoxText(SearchScope scope, bool writeBox)
    {
        if (scope.Kind is SearchScope.ScopeKind.Column or SearchScope.ScopeKind.Rectangle)
            return $"{scope.MinCx},{scope.MinCz}" + (scope.Kind == SearchScope.ScopeKind.Column ? "" : $",{scope.MaxCx},{scope.MaxCz}");
        if (scope.Kind == SearchScope.ScopeKind.Areas)
            return AreasCommand(scope.Areas.Select(a => a.Index));
        return writeBox ? "" : null;
    }

    /// <summary>The proposals under the bar. Areas are not among them: they belong to the "Find blocks" area list,
    /// which offers only the areas that contain the ticked blocks. A list of every area in every tab is what used
    /// to push the row off the window.</summary>
    private List<(string Command, string Caption)> Proposals()
    {
        var list = new List<(string, string)>();
        if (ScanData is not null)
            list.Add(($"{_lastCx},{_lastCz}", "This chunk on the map"));
        if (Map.SelectionBounds is { } b)
            list.Add(($"{b.minCx},{b.minCz},{b.maxCx},{b.maxCz}", $"The selection ({Map.SelectionCount} chunk(s))"));
        return list;
    }

    // ---- host members: the tab services ask their tab for the scope it currently searches ----
    SearchScope IEntitiesTabHost.Scope => _entityScope;
    SearchScope IItemsTabHost.Scope => _itemScope;
    SearchScope IPlayersTabHost.Scope => _playerScope;

    /// <summary>Every scope goes back to the whole save — opening another save must not search this one's rectangle in the next.</summary>
    private void ResetScopes()
    {
        _chunkScope = _entityScope = _itemScope = _playerScope = _filterScope = SearchScope.Everything;
        _areaMatches = Array.Empty<AreaMatch>();
        _areaMatchesKey = null;
        _areaMatchesScan = null;
        UpdateScopeBars(writeBox: true);
    }
}
