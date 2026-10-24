using Avalonia.Controls;
using Avalonia.Layout;
using VSCore;
using VSSaveEditor.Components;

namespace VSSaveEditor;

/// <summary>
///     Everything the "Entities" tab presenter needs from the window: the list, the detail panel, the summary line,
///     the current filter (also written by the "common names" chips) and navigation to the record's chunk.
/// </summary>
public interface IEntitiesTabHost
{
    /// <summary>The owner used to resolve theme-dependent brushes.</summary>
    Control Owner { get; }

    ListBox List { get; }
    StackPanel DetailPanel { get; }

    /// <summary>The empty state centred in the detail panel: no save, no matches or no selection.</summary>
    TextBlock EmptyHint { get; }

    WrapPanel ChipsPanel { get; }

    /// <summary>The summary line above the list: "N of M record(s) · filter …".</summary>
    string Summary { set; }

    /// <summary>The filter text - the tab reads it on rebuild, the "common names" chips write it.</summary>
    string FilterText { get; set; }

    EntityCategory Category { get; }

    /// <summary>Navigation to the record's chunk: the map focuses it and the "Chunks" tab opens it.</summary>
    void ShowChunk(int cx, int cz);

    /// <summary>Open Items with a filter on the inventories of this record.</summary>
    void ShowItems(string query);

    /// <summary>Reset the category before an exact selection (navigation from the Items tab).</summary>
    void SetCategory(EntityCategory category);
}

/// <summary>
///     The "Entities" tab presenter: a flat list of all creatures and block entities in the save, a filter,
///     a category, the "common names" hint and the card of the selected record with the field editor.
///
///     This place used to hold a text dump: tens of thousands of lines (META, histograms, regions,
///     raw blobs) with rare creature cards inside; finding a specific record in them was impossible.
///     The dump lines did not disappear - they remain as the export ("Copy raw dump"), while the page became
///     a list you can search and edit.
/// </summary>
public sealed class EntitiesTabService
{
    private readonly IEntitiesTabHost _host;
    private List<EntityRow> _all = new();
    private List<EntityRow> _shown = new();
    private EntityCard? _card;
    private EntityRow? _selected;
    private EntityRow? _pendingSelect; // exact selection from Items: applied after the background filter
    private int _filterSeq; // filter request number: a stale result is not published

    public EntitiesTabService(IEntitiesTabHost host)
    {
        _host = host;
    }

    public int ShownCount => _shown.Count;
    public int TotalCount => _all.Count;

    /// <summary>The list index is rebuilt once per scan (and reset when the save is closed).</summary>
    public void SetIndex(SaveScanResult? data)
    {
        _all = data is null ? new List<EntityRow>() : EntityBrowser.Build(data);
        _selected = null;
        _card = null;
        _host.DetailPanel.Children.Clear();
        Refresh();
    }

    /// <summary>
    ///     Rebuilds the list for the current filter and category; the selection survives if the record remains.
    ///
    ///     Filtering goes to the background: on a 252 MB save that is 132 000 records and ~170 ms per keystroke
    ///     (measured with `.uisnap scan`), so typing on the UI thread would visibly stutter. The race
    ///     is closed by the request number: a stale filter result is not published - the same technique as
    ///     in the background dump build (<c>_dumpSeq</c> in the window).
    /// </summary>
    public void Refresh()
    {
        var category = _host.Category;
        var text = _host.FilterText;
        var all = _all;
        var seq = ++_filterSeq;

        if (all.Count == 0)
        {
            Apply(new List<EntityRow>(), category, text);
            return;
        }

        _ = FilterAsync(seq, all, category, text);
    }

    private async Task FilterAsync(int seq, List<EntityRow> all, EntityCategory category, string text)
    {
        List<EntityRow> shown;
        try
        {
            shown = await Task.Run(() => EntityBrowser.Filter(all, category, text));
        }
        catch (Exception ex)
        {
            if (seq == _filterSeq) _host.Summary = $"filter failed: {ex.Message}";
            return;
        }

        if (seq != _filterSeq) return; // a newer filter arrived - this result is already stale
        Apply(shown, category, text);
    }

    private void Apply(List<EntityRow> shown, EntityCategory category, string text)
    {
        _shown = shown;
        _host.List.ItemsSource = _shown;
        var label = text.Trim();
        // The summary is short on purpose: there is one toolbar row, and a long hint pushed the buttons out of the window.
        _host.Summary = _all.Count == 0
            ? "no data — open a save and let the scan finish"
            : $"{_shown.Count} of {_all.Count} record(s)"
              + (label.Length > 0 ? $" matching \"{label}\"" : "");

        RenderChips(ChipsFor(category));

        // The detail survives a rebuild only when the same record is still in the list: otherwise the selection
        // would point at a row that no longer exists.
        var keep = _selected is not null && _shown.Contains(_selected) ? _selected : null;
        if (!ReferenceEquals(keep, _selected))
        {
            _selected = null;
            _card = null;
            _host.DetailPanel.Children.Clear();
        }

        if (_host.List.SelectedItem != keep) _host.List.SelectedItem = keep;
        if (keep is null) ShowEmptyHint();

        // An exact selection from Items arrives while the filter is still running: the rows appear only
        // in Apply, so we search and select here rather than in the calling code.
        if (_pendingSelect is { } wanted)
        {
            var found = _shown.FirstOrDefault(r => r.Position == wanted.Position && r.Field == wanted.Field
                                                    && r.Occurrence == wanted.Occurrence);
            // A row that is not found is not consumed: while a newer filter runs, the old result
            // may arrive first and lack it; otherwise the jump from Items would be lost in the race.
            if (found is not null)
            {
                _pendingSelect = null;
                _host.List.SelectedItem = found;
                Select(found);
            }
        }
    }

    /// <summary>Opens a record by its edit address (position, field, occurrence) - the jump from Items.</summary>
    public void SelectAddress(long position, int field, int occurrence)
    {
        var row = _all.FirstOrDefault(r => r.Position == position && r.Field == field && r.Occurrence == occurrence);
        if (row is null) return;
        _host.FilterText = "";
        _host.SetCategory(EntityCategory.All);
        _pendingSelect = row;
        Refresh();
    }

    /// <summary>Shows the record card; null clears the detail panel.</summary>
    public void Select(EntityRow? row)
    {
        _selected = row;
        _card = null;
        var panel = _host.DetailPanel;
        panel.Children.Clear();
        if (row is null)
        {
            ShowEmptyHint();
            return;
        }

        _host.EmptyHint.IsVisible = false;

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 2, 0, 2) };
        var jump = new Button { Content = "Show chunk on map", Padding = new Avalonia.Thickness(8, 2) };
        jump.Click += (_, _) => _host.ShowChunk(row.ChunkX, row.ChunkZ);
        head.Children.Add(jump);

        if (row.HasItems)
        {
            var items = new Button { Content = "Find items", Padding = new Avalonia.Thickness(8, 2) };
            ToolTip.SetTip(items, "Open the Items tab filtered to this record's inventories");
            items.Click += (_, _) => _host.ShowItems(row.InventoryFilter);
            head.Children.Add(items);
        }
        var kind = new TextBlock
        {
            Text = row.IsBlockEntity ? "block entity (field 8)" : "creature (field 6)",
            VerticalAlignment = VerticalAlignment.Center
        };
        CardUi.BindTheme(_host.Owner, kind, TextBlock.ForegroundProperty, "TextSecondaryBrush");
        head.Children.Add(kind);
        panel.Children.Add(head);

        if (row.HasInventories)
            CardUi.AddLine(_host.Owner, panel, "inventories: " + row.InventorySummary, true, gray: true);

        var card = new EntityCard { DataContext = row };
        _card = card;
        panel.Children.Add(card);
    }

    /// <summary>Re-reads the detail card (after an undo/redo of an edit in the window).</summary>
    public void ReloadDetail() => _card?.Reload();

    /// <summary>Shows the hint in the detail panel: why there is no card and what to do.</summary>
    private void ShowEmptyHint()
    {
        _host.DetailPanel.Children.Clear();
        _host.EmptyHint.Text = _all.Count == 0
            ? "Open a save and wait for the scan to finish.\n\nEntities and block entities will appear here."
            : _shown.Count == 0
                ? "No entity matches the filter.\n\nClear the filter or switch the category."
                : "Select an entity in the list.\n\nThe card shows its fields, position and inventory links.";
        _host.EmptyHint.IsVisible = true;
    }

    /// <summary>The text list of the shown records - what "Copy list" puts on the clipboard.
    /// The second column is the full code with the domain when it differs from the name (a mod entity
    /// or a player name): so the export loses neither "Moonlight" nor `equusdestrier:horse-…`.</summary>
    public string ListText()
    {
        var lines = new List<string> { $"# {_shown.Count} of {_all.Count} record(s)", "" };
        foreach (var r in _shown)
        {
            var code = r.Code.Length > 0 && r.Code != r.Label ? r.Code : "";
            lines.Add($"{r.Label}\t{code}\t{(r.IsBlockEntity ? "block entity" : r.Kind)}\t{r.Sub}\t{r.ChunkText}\t{r.Size}B");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    ///     What to show in "common names". In "All" mode some block entity types would drown out creatures
    ///     by count (the save has an order of magnitude more of them), so the hint takes the top of each category
    ///     separately - otherwise it would be impossible to tell who lives in the world at all.
    /// </summary>
    private List<(string Name, int Count)> ChipsFor(EntityCategory category)
    {
        if (category != EntityCategory.All)
            return EntityBrowser.TopNames(EntityBrowser.Filter(_all, category, null), 12);
        var creatures = EntityBrowser.TopNames(EntityBrowser.Filter(_all, EntityCategory.Entities, null), 6);
        var blocks = EntityBrowser.TopNames(EntityBrowser.Filter(_all, EntityCategory.BlockEntities, null), 6);
        return creatures.Concat(blocks).ToList();
    }

    /// <summary>Common names as buttons: a click puts the name into the filter.</summary>
    private void RenderChips(List<(string Name, int Count)> top)
    {
        var panel = _host.ChipsPanel;
        panel.Children.Clear();
        if (top.Count == 0) return;
        foreach (var (name, count) in top)
        {
            // Names can be long ("entitybehavior-debuff-…"): they are trimmed in the hint,
            // while the click puts the full name into the filter.
            var label = name.Length > 26 ? name[..25] + "…" : name;
            var btn = new Button
            {
                Content = $"{label} ×{count}",
                Padding = new Avalonia.Thickness(6, 1),
                Margin = new Avalonia.Thickness(0, 0, 4, 2),
                FontSize = UiStyle.MonoFontSize
            };
            ToolTip.SetTip(btn, name);
            btn.Click += (_, _) =>
            {
                _host.FilterText = name;
                Refresh();
            };
            panel.Children.Add(btn);
        }
    }
}
