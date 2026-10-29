using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using VSCore;

namespace VSSaveEditor;

/// <summary>What the Items tab needs from the window: controls, map and owner navigation, clipboard.</summary>
public interface IItemsTabHost
{
    Control Owner { get; }
    ListBox List { get; }
    StackPanel DetailPanel { get; }

    /// <summary>The empty state centred in the detail panel: no save, no matches or no selection.</summary>
    TextBlock EmptyHint { get; }

    string FilterText { get; }
    InventorySourceFilter SourceFilter { get; }

    /// <summary>The part of the save this tab searches — the "Search in:" row of the tab.</summary>
    SearchScope Scope { get; }
    string Summary { set; }
    string Status { set; }

    void ShowChunk(int cx, int cz);
    void OpenOwner(ItemStackRow row);
    void CopyText(string text, string note);
}

/// <summary>
///     Search across all inventories of the save: players (playerdata), creatures (field 6) and block entities
///     (field 8). It answers "who has this" and "where it lies", unlike the Entities tab,
///     which searches the records themselves, not their contents.
/// </summary>
public sealed class ItemsTabService
{
    private readonly IItemsTabHost _host;
    private List<ItemStackRow> _all = new();
    private List<ItemStackRow> _shown = new();
    private ItemStackRow? _selected;
    private int _filterSeq;

    public ItemsTabService(IItemsTabHost host)
    {
        _host = host;
    }

    public int TotalCount => _all.Count;
    public int ShownCount => _shown.Count;

    /// <summary>The index is built once per scan and cleared when the save is closed.</summary>
    public void SetIndex(SaveScanResult? data) => SetIndex(data, _host.Scope);

    /// <summary>
    ///     The scoped index. Unlike the chunks and entities it takes the scope itself, not its chunk restriction:
    ///     the player inventory index treats "unplaced" and a rectangle differently (see <see cref="InventoryBrowser.Build" />).
    /// </summary>
    public void SetIndex(SaveScanResult? data, SearchScope scope)
    {
        _all = InventoryBrowser.Build(data, scope);
        _selected = null;
        _host.DetailPanel.Children.Clear();
        Refresh();
    }

    /// <summary>
    ///     Rebuilds the list for the current filter. 35 000 stacks and ~100 ms on a big save -
    ///     so the filter goes to the background and the request number drops stale results (as in Entities).
    /// </summary>
    public void Refresh()
    {
        var query = InventoryQuery.Parse(_host.FilterText);
        var source = _host.SourceFilter;
        var all = _all;
        var seq = ++_filterSeq;
        if (all.Count == 0)
        {
            Apply(new List<ItemStackRow>(), query, source);
            return;
        }

        _ = FilterAsync(seq, all, source, query);
    }

    private async Task FilterAsync(int seq, List<ItemStackRow> all, InventorySourceFilter source, InventoryQuery query)
    {
        List<ItemStackRow> shown;
        try
        {
            shown = await Task.Run(() => InventoryBrowser.Filter(all, source, query));
        }
        catch (Exception ex)
        {
            if (seq == _filterSeq)
            {
                _host.Summary = $"filter failed: {ex.Message}";
                _host.Status = $"Items filter failed: {ex.Message}";
            }
            return;
        }

        if (seq != _filterSeq) return;
        Apply(shown, query, source);
    }

    private void Apply(List<ItemStackRow> shown, InventoryQuery query, InventorySourceFilter source)
    {
        _shown = shown;
        _host.List.ItemsSource = _shown;

        var note = query.Error.Length > 0 ? $"! {query.Error} — " : "";
        var sourceText = source switch
        {
            InventorySourceFilter.Players => " in player inventories",
            InventorySourceFilter.Entities => " in creature inventories",
            InventorySourceFilter.BlockEntities => " in containers",
            _ => ""
        };
        var filterText = query.IsEmpty ? "" : $" matching {query.Describe()}";
        _host.Summary = _all.Count == 0
            ? "no data — open a save and let the scan finish"
            : note + $"{_shown.Count} of {_all.Count} stack(s) in {InventoryBrowser.CountInventories(_shown)} container(s)"
              + sourceText + filterText;

        var keep = _selected is not null && _shown.Contains(_selected) ? _selected : null;
        if (!ReferenceEquals(keep, _selected))
        {
            _selected = null;
            _host.DetailPanel.Children.Clear();
        }

        if (_host.List.SelectedItem != keep) _host.List.SelectedItem = keep;
        if (keep is null) ShowEmptyHint();
    }

    /// <summary>Card of the selected stack; null shows the hint instead.</summary>
    public void Select(ItemStackRow? row)
    {
        _selected = row;
        var panel = _host.DetailPanel;
        panel.Children.Clear();
        if (row is null)
        {
            ShowEmptyHint();
            return;
        }

        _host.EmptyHint.IsVisible = false;

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 2) };
        var where = new Button { Content = "Show chunk on map", Padding = new Thickness(8, 2) };
        where.IsEnabled = row.Inventory.HasChunk;
        ToolTip.SetTip(where, row.Inventory.HasChunk
            ? $"Jump to chunk {row.Inventory.ChunkX},{row.Inventory.ChunkZ}"
            : "playerdata has no position; the player's entity may not be in the scanned chunks");
        where.Click += (_, _) => _host.ShowChunk(row.Inventory.ChunkX, row.Inventory.ChunkZ);
        head.Children.Add(where);

        var owner = new Button { Content = "Open owner", Padding = new Thickness(8, 2) };
        ToolTip.SetTip(owner, row.IsPlayer
            ? "Open Players with this uid in the filter"
            : "Open this creature/block entity in the Entities tab");
        owner.Click += (_, _) => _host.OpenOwner(row);
        head.Children.Add(owner);

        var copy = new Button { Content = "Copy code", Padding = new Thickness(8, 2) };
        copy.Click += (_, _) => _host.CopyText(row.Code, $"Copied '{row.Code}'.");
        head.Children.Add(copy);
        panel.Children.Add(head);

        CardUi.AddHeader(panel, $"{row.Count} × {row.Code}");
        CardUi.AddLine(_host.Owner, panel,
            $"class: {(row.Stack.IsBlock ? "Block" : "Item")} · id {row.Stack.Id} · slot {row.Slot}", true);
        CardUi.AddLine(_host.Owner, panel,
            $"owner: {row.OwnerLabel} {row.OwnerBadge}" + (row.OwnerCode.Length > 0 ? $" · {row.OwnerCode}" : ""));
        CardUi.AddLine(_host.Owner, panel,
            $"container: {row.Container} · {row.Inventory.SlotCount} slot(s), {row.Inventory.Items.Count} item(s)");
        if (row.ChunkText.Length > 0)
            CardUi.AddLine(_host.Owner, panel, $"where: {row.ChunkText}" + (row.PositionText.Length > 0 ? $" · {row.PositionText}" : ""));
        else
            CardUi.AddLine(_host.Owner, panel, "where: playerdata (no in-world position recorded)", true, gray: true);

        if (row.Stack.Attributes.Count > 0)
        {
            CardUi.AddHeader(panel, $"Item attributes ({row.Stack.Attributes.Count})");
            foreach (var a in row.Stack.Attributes.Take(16))
                CardUi.AddLine(_host.Owner, panel, $"   {a.Key} = {a.Value}", true);
            if (row.Stack.Attributes.Count > 16)
                CardUi.AddLine(_host.Owner, panel, $"   … +{row.Stack.Attributes.Count - 16} more", true, gray: true);
        }
        else
        {
            CardUi.AddLine(_host.Owner, panel, "no item attributes", true, gray: true);
        }

        if (row.Inventory.OwnerKind == InventoryOwnerKind.BlockEntity)
            CardUi.AddLine(_host.Owner, panel,
                "The container itself is a block entity; item edits are not written to the save yet.", true, gray: true);
    }

    /// <summary>Shows the hint in the detail panel: why there is no card and what to do.</summary>
    private void ShowEmptyHint()
    {
        _host.DetailPanel.Children.Clear();
        _host.EmptyHint.Text = _all.Count == 0
            ? "Open a save and wait for the scan to finish.\n\nItem stacks from players, creatures and containers will appear here."
            : _shown.Count == 0
                ? "No item stack matches the filter.\n\nClear the filter or change the source."
                : "Select an item stack in the list.\n\nThe card shows its container, owner and location.";
        _host.EmptyHint.IsVisible = true;
    }

    /// <summary>The "Copy list" text - the same rows as in the list, plus attributes.</summary>
    public string ListText()
    {
        var lines = new List<string> { $"# {_shown.Count} of {_all.Count} item stack(s)", "" };
        foreach (var r in _shown)
            lines.Add($"{r.Count}\t{r.Code}\t{r.OwnerLabel}\t{r.OwnerBadge}\t{r.Container}\t{r.Slot}\t{r.ChunkText}\t{r.PositionText}\t{r.Attributes}");
        return string.Join("\n", lines);
    }
}
