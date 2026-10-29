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
/// </summary>
public partial class MainWindow
{
    // one scope per tab: the search scope survives switching tabs and a rescan
    private SearchScope _chunkScope = SearchScope.Everything;
    private SearchScope _entityScope = SearchScope.Everything;
    private SearchScope _itemScope = SearchScope.Everything;
    private SearchScope _playerScope = SearchScope.Everything;
    private SearchScope _filterScope = SearchScope.Everything;

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
                PopulateFilterList();
                // the overlay itself is rebuilt on "Show on map": a scope change must not start a world-wide block scan
                // while the user is only choosing where to look
                if (_filterMap is not null) _filterMap = null;
                UpdateFilterStatus();
                break;
        }

        UpdateScopeBars();
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

    /// <summary>Turns "area=2", "cx,cz", "cx1,cz1,cx2,cz2" or "" into a scope against this save's areas.</summary>
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
    ///     Redraws every scope row. The box is written only for scopes that came from the bar or code ("area=1",
    ///     "cx,cz") — never over a half-typed range — and never for the derived ones: the rectangle read from the map
    ///     selection has no text the user typed, but showing it lets them copy the numbers into another tab.
    /// </summary>
    private void UpdateScopeBars()
    {
        ChunkDataView.Scope.ShowState(DescribeScope(_chunkScope, "chunks"), BoxText(_chunkScope), Proposals());
        EntitiesView.Scope.ShowState(DescribeScope(_entityScope, "records"), BoxText(_entityScope), Proposals());
        ItemsView.Scope.ShowState(DescribeScope(_itemScope, "item stacks"), BoxText(_itemScope), Proposals());
        PlayersView.Scope.ShowState(DescribeScope(_playerScope, "players"), BoxText(_playerScope), Proposals());
        FindBlocksView.Scope.ShowState(DescribeScope(_filterScope, "block codes"), BoxText(_filterScope), Proposals());
    }

    /// <summary>
    ///     What a scope means for one tab. The chunk scope is stated in blocks as well as chunks: a user picks a chunk
    ///     on the map by its outline, and the numbers that identify it in the search boxes are chunk coordinates.
    /// </summary>
    private static string DescribeScope(SearchScope scope, string what)
    {
        if (scope.IsEmpty) return $"{scope.EmptyReason} — nothing to search for {what}";
        if (!scope.HasRegion) return $"{scope.Label} — every {what} of the save";
        var size = $"{scope.Label} — {what} of X {scope.MinCx * 32}..{scope.MaxCx * 32 + 31}, Z {scope.MinCz * 32}..{scope.MaxCz * 32 + 31}";
        return scope.Kind == SearchScope.ScopeKind.Area ? size : size + "; every Y level";
    }

    /// <summary>The text the box shows for a scope — only for a rectangle/column, since an area has no text form to type.</summary>
    private static string? BoxText(SearchScope scope)
        => scope.Kind is SearchScope.ScopeKind.Column or SearchScope.ScopeKind.Rectangle
            ? $"{scope.MinCx},{scope.MinCz}" + (scope.Kind == SearchScope.ScopeKind.Column ? "" : $",{scope.MaxCx},{scope.MaxCz}")
            : null;

    /// <summary>
    ///     The proposals under the bar. "Every area" is offered only when the save has more than one: with a single
    ///     area it is the same rectangle as "Whole save", and a button that changes nothing is noise in every tab.
    /// </summary>
    private List<(string Command, string Caption)> Proposals()
    {
        var list = new List<(string, string)>();
        if (_areas is { Areas.Count: > 1 })
            for (var i = 0; i < _areas.Areas.Count; i++)
                list.Add(($"area={i + 1}", $"Area {i + 1} of {_areas.Areas.Count}"));

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
        UpdateScopeBars();
    }
}
