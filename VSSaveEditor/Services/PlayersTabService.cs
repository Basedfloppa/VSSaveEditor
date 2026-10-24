using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using VSCore;

namespace VSSaveEditor;

/// <summary>
///     Everything the "Players" tab presenter needs from the window: the model, the tab controls
///     and map navigation.
/// </summary>
public interface IPlayersTabHost
{
    Control Owner { get; }
    VSSaveFile? Save { get; }
    ScanService Scan { get; }
    EditSession Edits { get; }
    ListBox List { get; }
    StackPanel Detail { get; }
    TextBlock EmptyHint { get; }
    TextBox Filter { get; }
    CheckBox PendingOnly { get; }
    TextBlock Count { get; }
    string Status { set; }

    /// <summary>The edit count and history changed - the window refreshes the Undo/Redo strip in the drawer header.</summary>
    void PendingChanged();

    void FocusChunk(int cx, int cz);

    /// <summary>Open Items with a filter on the player inventory.</summary>
    void ShowItems(string query);
}

/// <summary>
///     The "Players" tab: the list of characters (playerdata) with a filter, plus a separate detail panel.
///     Players used to be drawn inside every chunk card, although playerdata does not belong to a chunk,
///     and there was no place where a player would be visible as a list entry (P1-4).
/// </summary>
public sealed class PlayersTabService
{
    private const int InventoriesPerPage = 8;
    private readonly List<(ChunkField field, TextBox box)> _boxes = new();

    private readonly IPlayersTabHost _host;
    private readonly Dictionary<string, (int cx, int cz)> _positionCache = new();
    private int _editInvIndex = -1;

    // single-inventory edit mode: filled in when the card renders, read by Apply
    private long _editPlayerId = -1;
    private PlayerInventory? _editingInv;
    private List<VSSaveFile.PlayerRow> _rows = new();
    private VSSaveFile? _rowsOwner; // PlayerRows is a DB query: re-read only when the save changes
    private List<PlayerScanData> _shown = new();
    private bool _suppressSelection;

    public PlayersTabService(IPlayersTabHost host)
    {
        _host = host;
    }

    /// <summary>The player whose card is currently in edit mode (null means no edit).</summary>
    public long? EditingPlayerId => _editPlayerId >= 0 ? _editPlayerId : null;

    public void EndEditing()
    {
        _editPlayerId = -1;
        _editInvIndex = -1;
        _editingInv = null;
        _boxes.Clear();
    }

    /// <summary>Re-reads the list and the details. Called after a scan, a filter change or an edit.</summary>
    public void Refresh()
    {
        var list = _host.List;
        var scan = _host.Scan;
        if (scan.Data is null)
        {
            _shown = new List<PlayerScanData>();
            _rows = new List<VSSaveFile.PlayerRow>();
            _rowsOwner = null;
            SetItems(list, null, -1);
            _host.Count.Text = "";
            ShowDetailNote("Open a save and wait for the scan to finish.");
            return;
        }

        if (!ReferenceEquals(_rowsOwner, _host.Save))
        {
            _rows = _host.Save?.PlayerRows ?? new List<VSSaveFile.PlayerRow>();
            _rowsOwner = _host.Save;
        }

        var q = (_host.Filter.Text ?? "").Trim();
        var pendingOnly = _host.PendingOnly.IsChecked == true;
        var all = scan.Data.Players;
        _shown = all.Where(p => Matches(p, q) && (!pendingOnly || _host.Edits.TryGetPlayerBlob(p.PlayerId, out _)))
            .ToList();

        var keep = list.SelectedIndex;
        SetItems(list, _shown.Select(Describe).ToList(),
            _shown.Count == 0 ? -1 : Math.Clamp(keep, 0, _shown.Count - 1));

        _host.Count.Text = $"{_shown.Count} of {all.Count} player(s)"
                           + (pendingOnly ? " with pending edits" : "")
                           + (q.Length > 0 ? $" matching '{q}'" : "");
        if (_shown.Count == 0)
        {
            ShowDetailNote(all.Count == 0
                ? "This save has no playerdata rows."
                : "No player matches the filter.");
            return;
        }

        RenderDetail(list.SelectedIndex);
    }

    private void SetItems(ListBox list, List<string>? items, int selected)
    {
        // SelectionChanged fires synchronously on an ItemsSource change: without the flag the details would render twice
        _suppressSelection = true;
        list.ItemsSource = items;
        list.SelectedIndex = selected;
        _suppressSelection = false;
    }

    public void OnSelectionChanged()
    {
        if (_suppressSelection) return;
        EndEditing();
        RenderDetail(_host.List.SelectedIndex);
    }

    public void OnFilterChanged()
    {
        Refresh();
    }

    private bool Matches(PlayerScanData p, string query)
    {
        if (query.Length == 0) return true;
        var hay = string.Join(" ",
            new[]
            {
                p.CustomName ?? "", p.PlayerUid ?? "", p.PlayerId.ToString(),
                string.Join(" ", p.Tokens), string.Join(" ", p.SpeciesCodes)
            });
        if (hay.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        // inventory names and field values are searched too ("who has a backpack")
        var pd = ParseOf(p);
        if (pd is null) return false;
        foreach (var inv in pd.Inventories)
        {
            if ((inv.Name ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var f in inv.Fields)
                if (f.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (f.StringValue ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
                    return true;
            // Items are the main reason to search a player: "who has ore-quartz".
            foreach (var item in inv.Items)
                if (item.DisplayCode.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || item.AttributeText.Contains(query, StringComparison.OrdinalIgnoreCase))
                    return true;
            foreach (var nested in inv.Nested)
                foreach (var item in nested.Items)
                    if (item.DisplayCode.Contains(query, StringComparison.OrdinalIgnoreCase))
                        return true;
        }

        return false;
    }

    private string Describe(PlayerScanData p)
    {
        var pending = _host.Edits.TryGetPlayerBlob(p.PlayerId, out _);
        var pd = ParseOf(p);
        var name = p.CustomName is { Length: > 0 } cn ? $"{cn} ({p.PlayerUid ?? "no uid"})" : p.PlayerUid ?? "(no uid)";
        return (pending ? "• " : "")
               + $"{name} — id {p.PlayerId}"
               + (pd is null ? "" : $", {pd.Inventories.Count} inventor(y/ies), {pd.Inventories.Sum(i => i.Items.Count)} item(s)")
               + (pending ? "  — pending edits" : "");
    }

    /// <summary>Current player blob: this session's edit, otherwise the raw scan bytes, otherwise the DB row.</summary>
    private byte[]? RawOf(PlayerScanData p)
    {
        if (_host.Edits.TryGetPlayerBlob(p.PlayerId, out var pending) && pending is { Length: > 0 }) return pending;
        if (p.Raw?.Data is { Length: > 0 } raw) return raw;
        var row = _rows.FirstOrDefault(r => r.Id == p.PlayerId);
        return row.Data is { Length: > 0 } ? row.Data : null;
    }

    private PlayerData? ParseOf(PlayerScanData p)
    {
        var raw = RawOf(p);
        if (raw is null) return null;
        try
        {
            return PlayerDataParser.Parse(raw, p.PlayerId);
        }
        catch
        {
            return null;
        }
    }

    private void ShowDetailNote(string note)
    {
        var panel = _host.Detail;
        panel.Children.Clear();
        _host.EmptyHint.Text = note + "\n\n" +
            "Playerdata holds the character's inventories; edits are applied to the loaded working copy.";
        _host.EmptyHint.IsVisible = true;
    }

    private void RenderDetail(int index)
    {
        var panel = _host.Detail;
        panel.Children.Clear();
        _host.EmptyHint.IsVisible = false;
        if (index < 0 || index >= _shown.Count)
        {
            ShowDetailNote("Select a player in the list.");
            return;
        }

        var p = _shown[index];
        var raw = RawOf(p);
        if (raw is null || raw.Length == 0)
        {
            ShowDetailNote($"playerdata row #{p.PlayerId} has no data.");
            return;
        }

        PlayerData pd;
        try
        {
            pd = PlayerDataParser.Parse(raw, p.PlayerId);
        }
        catch (Exception ex)
        {
            ShowDetailNote($"playerdata #{p.PlayerId} parse error: {ex.Message}");
            return;
        }

        var pending = _host.Edits.TryGetPlayerBlob(p.PlayerId, out _);
        CardUi.AddHeader(panel,
            $"Player {pd.CustomName ?? pd.Uid ?? "(no uid)"}" + (pd.CustomName is { Length: > 0 } ? $" ({pd.Uid})" : "")
            + $" — playerdata id {p.PlayerId}");
        CardUi.AddLine(_host.Owner, panel,
            $"raw {raw.Length} B | {pd.Inventories.Count} inventor(y/ies) | source: {(pending ? "pending edit in this session" : "playerdata row")}",
            true, gray: pending);
        if (pending)
            CardUi.AddLine(_host.Owner, panel, "This player has pending edits — see Review & apply.", bold: true);

        var (cx, cz) = FindPlayerChunk(p);
        var jump = new Button
        {
            Content = "Show on map",
            Padding = new Thickness(8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = cx != int.MinValue
        };
        ToolTip.SetTip(jump, cx == int.MinValue
            ? "The playerdata row has no position and no matching player entity was found in the chunk data, so there is nowhere to jump."
            : $"Jump to chunk {cx},{cz}");
        jump.Click += (_, _) => _host.FocusChunk(cx, cz);
        panel.Children.Add(jump);

        CardUi.RenderCardsPaged(panel, pd.Inventories.Count, InventoriesPerPage,
            (parent, i) => RenderInventory(parent, p, pd, i), "inventories");
    }

    private void RenderInventory(StackPanel parent, PlayerScanData p, PlayerData pd, int index)
    {
        var inv = pd.Inventories[index];
        var card = new StackPanel { Spacing = 1 };
        CardUi.AddLine(_host.Owner, card,
            $"{inv.Name ?? "?"} — {inv.SlotCount} slot(s), {inv.Items.Count} item(s)", bold: true);
        RenderInventoryItems(card, inv);
        var parsed = inv.Fields.Where(f => f.IsEditable).ToList();

        if (_editPlayerId == p.PlayerId && _editInvIndex == index)
        {
            _boxes.Clear();
            foreach (var f in parsed) _boxes.Add((f, CardUi.AddEditRow(card, f)));
            var btns = new StackPanel
                { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
            var apply = new Button { Content = "Apply", Padding = new Thickness(8, 2) };
            apply.Click += (_, _) => Apply(p, pd, index);
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(8, 2) };
            cancel.Click += (_, _) =>
            {
                EndEditing();
                Refresh();
            };
            btns.Children.Add(apply);
            btns.Children.Add(cancel);
            card.Children.Add(btns);
        }
        else
        {
            CardUi.AddFieldsPreview(_host.Owner, card, parsed);
            var opaque = inv.Fields.Count(f => f.Type == ChunkFieldType.Opaque);
            if (opaque > 0)
                CardUi.AddLine(_host.Owner, card, $"   … {opaque} opaque bytes (hex-edit only)", true, gray: true);
            if (parsed.Count > 0)
            {
                var editBtn = new Button
                {
                    Content = "Edit inventory fields", Padding = new Thickness(8, 2), Margin = new Thickness(0, 3, 0, 0)
                };
                editBtn.Click += (_, _) =>
                {
                    _editPlayerId = p.PlayerId;
                    _editInvIndex = index;
                    _editingInv = inv;
                    Refresh();
                };
                card.Children.Add(editBtn);
            }
        }

        if (inv.Items.Count > 0 || inv.Nested.Count > 0)
        {
            var find = new Button
            {
                Content = "Find items", Padding = new Thickness(8, 2), Margin = new Thickness(0, 3, 0, 0)
            };
            ToolTip.SetTip(find, "Open the Items tab filtered to this inventory");
            find.Click += (_, _) => _host.ShowItems($"key:p:{p.PlayerId}:{inv.Name}");
            card.Children.Add(find);
        }

        parent.Children.Add(CardUi.WrapCard(_host.Owner, card));
    }

    /// <summary>Inventory items: the first few rows, the rest as a count.</summary>
    private void RenderInventoryItems(StackPanel card, PlayerInventory inv)
    {
        const int preview = 6;
        foreach (var item in inv.Items.Take(preview))
            CardUi.AddLine(_host.Owner, card,
                $"   {item.Count} × {item.DisplayCode}" + (item.AttributeSummary.Length > 0 ? $"  ({item.AttributeSummary})" : ""),
                true);
        if (inv.Items.Count > preview)
            CardUi.AddLine(_host.Owner, card, $"   … +{inv.Items.Count - preview} more item(s)", true, gray: true);
        foreach (var nested in inv.Nested)
            CardUi.AddLine(_host.Owner, card,
                $"   nested {nested.Name}: {nested.Items.Count} item(s)", true, gray: true);
    }

    /// <summary>Applies the TextBox values to the inventory fields and accumulates the edit in EditSession.</summary>
    private void Apply(PlayerScanData p, PlayerData pd, int index)
    {
        if (_editingInv is null || _editPlayerId != p.PlayerId || _editInvIndex != index)
        {
            _host.Status = "Edit target is not available — reopen the player.";
            EndEditing();
            return;
        }

        var rows = _boxes.Select(b => (b.field, (string?)b.box.Text)).ToList();
        if (!EntityEditService.TryApplyTexts(rows, out var badValue))
        {
            _host.Status = badValue ?? "Bad value";
            return;
        }

        var newBody = ChunkDataEditor.SerializeFields(_editingInv.RootFields);
        var raw = RawOf(p);
        if (raw is null) return;
        // edit the inventory by INDEX: inventory names can repeat (B3)
        byte[] newBlob;
        try
        {
            newBlob = PlayerDataParser.ReplaceInventoryBody(raw, index, newBody);
        }
        catch (Exception ex)
        {
            _host.Status = $"Apply failed: {ex.Message}";
            return;
        }

        _host.Edits.PutPlayer(p.PlayerId, newBlob, $"inventory[{index}] '{_editingInv.Name ?? "?"}'");
        _host.Status =
            $"Pending edit: player {pd.Uid ?? p.PlayerId.ToString()}, inventory[{index}] '{_editingInv.Name ?? "?"}'";
        EndEditing();
        Refresh();
        _host.PendingChanged();
    }

    /// <summary>
    ///     The chunk where this player's creature stands. playerdata has no position, so we look for the player
    ///     entity in the already collected scan data; if not found, "Show on map" stays disabled instead of lying with coordinates.
    /// </summary>
    private (int cx, int cz) FindPlayerChunk(PlayerScanData p)
    {
        var uid = p.PlayerUid;
        if (string.IsNullOrEmpty(uid)) return (int.MinValue, int.MinValue);
        if (_positionCache.TryGetValue(uid, out var cached)) return cached;
        (int cx, int cz) result = (int.MinValue, int.MinValue);
        var data = _host.Scan.Data;
        if (data is not null)
            foreach (var c in data.Chunks)
            {
                foreach (var e in c.Entities)
                {
                    if (!e.HasInfo) continue;
                    var playerLike = (e.Species ?? "").Contains("player", StringComparison.OrdinalIgnoreCase)
                                     || (e.Kind ?? "").Contains("player", StringComparison.OrdinalIgnoreCase);
                    if (!playerLike) continue;
                    var matches = e.FieldsSummary.Contains(uid, StringComparison.OrdinalIgnoreCase)
                                  || e.Fields.Any(f => f.Contains(uid, StringComparison.OrdinalIgnoreCase));
                    if (!matches) continue;
                    result = (c.ChunkX, c.ChunkZ);
                    break;
                }

                if (result.cx != int.MinValue) break;
            }

        _positionCache[uid] = result;
        return result;
    }
}