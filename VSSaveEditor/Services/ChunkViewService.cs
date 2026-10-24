using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using VSCore;

namespace VSSaveEditor;

/// <summary>
///     Everything the "Chunks" tab presenter needs from the window: the model, the list of found chunks, the
///     summary line, the card panel and the feedback (status, confirmation, availability of the
///     save-edits button).
/// </summary>
public interface IChunkViewHost
{
    /// <summary>The owner used to resolve theme-dependent brushes.</summary>
    Control Owner { get; }

    VSSaveFile? Save { get; }
    ScanService Scan { get; }
    EditSession Edits { get; }
    StackPanel Panel { get; }
    TextBox HexBox { get; }

    /// <summary>The list of found chunks: its Tag carries the row coordinates (the window navigates by them).</summary>
    ComboBox ChunkPick { get; }

    /// <summary>The current chunk list query text (read by the presenter, written by the window/Clear button).</summary>
    string ChunkQueryText { get; set; }

    /// <summary>The line that explains the query next to the list.</summary>
    string ChunkListSummary { set; }

    string ChunkStatus { set; }
    void SetSaveEnabled(bool enabled);

    /// <summary>The edit count and history changed - the window refreshes the Undo/Redo strip in the drawer header.</summary>
    void PendingChanged();

    Task<bool> Confirm(string title, string message);

    /// <summary>Open Items with a filter on the inventories of the selected record.</summary>
    void ShowItems(string query);
}

/// <summary>
///     Viewing and editing chunk data: cards for creatures/BlockEntity/player inventories, the hex fallback
///     and accumulation of edits in EditSession. The window keeps only the panel, the status and confirmations.
/// </summary>
public sealed class ChunkViewService
{
    private const int
        MaxCardsPerSection = 12; // the rest is on the "show more" button (a chunk with 47 BEs must not build 47 cards at once)

    private readonly Dictionary<long, ColumnSection> _columnSections = new();
    private readonly List<(ChunkField field, TextBox box)> _editBoxes = new();

    private readonly IChunkViewHost _host;
    private BlockEntityRecord? _editBe;
    private EntityRecord? _editEntity;

    /// <summary>The chunk (column) index for the search list: built once per scan.</summary>
    private IReadOnlyList<ChunkColumnInfo> _columns = Array.Empty<ChunkColumnInfo>();

    // edit mode state: filled in when a card renders, read by the matching Apply
    private (long pos, int field, int occ)? _editingCard;

    private string
        _statusBase = ""; // base status message - UpdateDirtyStatus builds from it, without "+=" accumulation

    public ChunkViewService(IChunkViewHost host)
    {
        _host = host;
    }

    public int CurrentCx { get; private set; }
    public int CurrentCz { get; private set; }

    /// <summary>Whether a card edit is running, and for which position (null means no edit).</summary>
    public bool IsEditing => _editingCard is not null;

    public long? EditingPosition => _editingCard?.pos;

    /// <summary>The scroll container of the data panel: needed so the position is not lost on a rebuild.</summary>
    private ScrollViewer? ChunkScroll => _host.Panel.FindAncestorOfType<ScrollViewer>();

    /// <summary>The chunk index for the search list (called after the scan is published). Empty means the save is closed.</summary>
    public void SetChunkIndex(IReadOnlyList<ChunkColumnInfo> columns)
    {
        _columns = columns;
        RefreshChunkList();
    }

    /// <summary>
    ///     Rebuilds the found chunk list for the current query. A list row shows the record
    ///     counters - that is how a chunk is recognised without opening it; the selection is reset because
    ///     the rows differ after filtering.
    /// </summary>
    public void RefreshChunkList()
    {
        var query = ChunkQuery.Parse(_host.ChunkQueryText);
        var cols = ChunkBrowser.Filter(_columns, query);
        var total = ChunkBrowser.Count(_columns, query);

        // The row format must not change: the "entities N, be M" counters in the list depend on it.
        _host.ChunkPick.SelectedIndex = -1; // the selection would point at another row after filtering
        _host.ChunkPick.ItemsSource = cols
            .Select(c => $"{c.Cx}, {c.Cz}  (entities {c.Entities}, be {c.BlockEntities})")
            .ToList();
        _host.ChunkPick.Tag = cols.Select(c => (c.Cx, c.Cz, c.Entities, c.BlockEntities)).ToList();

        // The parse error goes at the start of the line: otherwise it would be lost in the tail of a long summary.
        var note = query.Error is { Length: > 0 } err ? $"! {err} — " : "";
        if (_columns.Count == 0)
            _host.ChunkListSummary = "no data — open a save and let the scan finish";
        else if (query.IsEmpty)
            _host.ChunkListSummary = note + (total == 0
                ? "no chunk has entities or block entities"
                : $"{total} chunk(s) with entities/block entities (showing {cols.Count}) — type a query to narrow");
        else
            _host.ChunkListSummary = note + $"{total} match(es) for {query.Describe()}"
                                     + (cols.Count < total ? $" (showing {cols.Count})" : "");
    }

    /// <summary>Clears the edit mode (opening another save makes the card data stale).</summary>
    public void EndEditing()
    {
        _editingCard = null;
        _editBoxes.Clear();
        _editEntity = null;
        _editBe = null;
    }

    /// <summary>Parses and renders the structured data of chunk (cx, cz) across all y columns.</summary>
    public void Show(int cx, int cz)
    {
        CurrentCx = cx;
        CurrentCz = cz;
        var save = _host.Save;
        var scan = _host.Scan;
        var edits = _host.Edits;
        if (scan.Data is null || save is null)
        {
            ShowEmptyState("Save not scanned yet.");
            return;
        }

        if (scan.ColumnsFor(cx, cz).Count == 0)
        {
            ShowEmptyState($"Chunk {cx},{cz} not found in the save.");
            return;
        }

        var panel = _host.Panel;
        panel.Children.Clear();
        _columnSections.Clear();
        var cols = scan.ColumnsFor(cx, cz);

        edits.HexTargetPosition = -1;
        edits.HexTargetLabel = "";
        foreach (var cd in cols)
        {
            var sp = new StackPanel { Spacing = 3 };
            var pc = ParseColumn(cd, out var occ6, out var occ8, out var mismatch, out var error);
            if (pc is null)
            {
                if (error is not null) CardUi.AddHeader(sp, $"Chunk {cx},{cz} y{cd.ChunkY}: parse error — {error}");
                else continue; // empty blob - no section, as before
            }
            else
            {
                RenderParsedChunk(sp, pc, occ6, occ8, cd);
                _columnSections[cd.Position] =
                    new ColumnSection(sp, pc.Entities.Count, pc.BlockEntities.Count, mismatch);
                if (edits.HexTargetPosition < 0)
                {
                    edits.HexTargetPosition = cd.Position;
                    edits.HexTargetLabel = $"{cx}:{cz} y{cd.ChunkY}";
                }
            }

            panel.Children.Add(sp);
        }

        RebuildStatus();
        UpdateHexBox();
        UpdateDirtyStatus();
        // New data is shown from the top; only Rebuild() preserves the scroll position.
        if (ChunkScroll is { } sv) sv.Offset = default;
    }

    /// <summary>Redraws one y column (after an external edit - for example an undo in the history).</summary>
    public void RefreshPosition(long position)
    {
        RefreshSection(position);
    }

    /// <summary>Redraws the whole panel while preserving the scroll position.</summary>
    public void RefreshAll()
    {
        Rebuild();
    }

    /// <summary>
    ///     Parses one y column including the accumulated edits (they are visible in the card immediately).
    ///     error != null means the blob is broken; mismatch means the walk order did not match the parser, so field editing is disabled.
    /// </summary>
    private ParsedChunk? ParseColumn(ChunkScanData cd, out Dictionary<int, int> occ6, out Dictionary<int, int> occ8,
        out bool mismatch, out string? error)
    {
        occ6 = new Dictionary<int, int>(); // entity index -> occurrence
        occ8 = new Dictionary<int, int>();
        mismatch = false;
        error = null;
        var save = _host.Save!;
        var edits = _host.Edits;

        var raw = cd.Raw?.Data ?? save.ReadChunk(cd.Position);
        if (raw is null || raw.Length == 0) return null;
        // show the current state: apply the accumulated edits first (payload/hex)
        if (edits.TryGetWholeChunk(cd.Position, out var whole)) raw = whole!;
        else if (edits.TryGetPayloadEdits(cd.Position, out var map))
            raw = ChunkDataEditor.SplicePayloads(raw, map!.Select(kv => (kv.Key.field, kv.Key.occ, kv.Value)).ToList());

        ParsedChunk pc;
        try
        {
            pc = ChunkDataParser.Parse(raw, save.BlockRegistry, cd.Position);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }

        // occurrence numbers of field6/field8 in the top-level blob (for a targeted edit)
        int i6 = 0, i8 = 0;
        foreach (var t in ChunkDataEditor.WalkTop(raw))
            if (t.Field == 6) occ6[i6++] = t.Occurrence;
            else if (t.Field == 8) occ8[i8++] = t.Occurrence;
        // Safety net: if the walk order did not match the parser order, an edit by index would go
        // to the wrong object - in that case field editing is disabled (occurrence = -1) instead of risking it.
        if (occ6.Count != pc.Entities.Count || occ8.Count != pc.BlockEntities.Count)
        {
            occ6.Clear();
            occ8.Clear();
            mismatch = true;
        }

        return pc;
    }

    /// <summary>
    ///     A full rebuild of the panel for the same chunk - preserving the scroll position (P1-3).
    ///     A selection change goes through Show() and resets the scroll on purpose: the content is different.
    /// </summary>
    private void Rebuild()
    {
        var scroll = ChunkScroll;
        var offset = scroll?.Offset ?? default;
        Show(CurrentCx, CurrentCz);
        if (scroll is not null && offset != default) scroll.Offset = offset;
    }

    /// <summary>
    ///     A targeted redraw of one y column: the other sections are not recreated, so
    ///     expanded field lists and the scroll position are preserved (P1-3).
    /// </summary>
    private void RefreshSection(long position)
    {
        if (!_columnSections.TryGetValue(position, out var section))
        {
            Rebuild();
            return;
        }

        var cd = _host.Scan.ColumnsFor(CurrentCx, CurrentCz).FirstOrDefault(c => c.Position == position);
        if (cd is null)
        {
            Rebuild();
            return;
        }

        section.Container.Children.Clear();
        var pc = ParseColumn(cd, out var occ6, out var occ8, out var mismatch, out var error);
        if (pc is null)
        {
            if (error is not null)
                CardUi.AddHeader(section.Container,
                    $"Chunk {CurrentCx},{CurrentCz} y{cd.ChunkY}: parse error — {error}");
            _columnSections.Remove(position);
        }
        else
        {
            RenderParsedChunk(section.Container, pc, occ6, occ8, cd);
            _columnSections[position] = section with
            {
                Entities = pc.Entities.Count, Bes = pc.BlockEntities.Count, OccMismatch = mismatch
            };
        }

        RebuildStatus();
        UpdateHexBox();
        UpdateDirtyStatus();
    }

    /// <summary>Rebuilds the base status from the saved sections (without re-parsing blobs).</summary>
    private void RebuildStatus()
    {
        var hexOverrides = false; // a whole-chunk hex edit and field edits of one position can coexist
        foreach (var cd in _host.Scan.ColumnsFor(CurrentCx, CurrentCz))
            if (_host.Edits.HasWholeChunkEdit(cd.Position) && _host.Edits.HasPayloadEdits(cd.Position))
                hexOverrides = true;
        var entities = _columnSections.Values.Sum(x => x.Entities);
        var bes = _columnSections.Values.Sum(x => x.Bes);
        var occMismatch = _columnSections.Values.Any(x => x.OccMismatch);
        _statusBase =
            $"Chunk {CurrentCx},{CurrentCz}: {_host.Scan.ColumnsFor(CurrentCx, CurrentCz).Count} y-col(s), {entities} entit(y/ies), {bes} block entit(y/ies)."
            + (hexOverrides ? "  ! raw hex edit overrides field edits for this chunk" : "")
            + (occMismatch ? "  ! blob field order mismatch - field editing disabled" : "");
    }

    private void UpdateHexBox()
    {
        var edits = _host.Edits;
        _host.HexBox.Text = edits.HexTargetPosition >= 0
            ? ToHex((edits.TryGetWholeChunk(edits.HexTargetPosition, out var hx)
                ? hx
                : _host.Save!.ReadChunk(edits.HexTargetPosition)) ?? Array.Empty<byte>())
            : "";
    }

    private void RenderParsedChunk(StackPanel sp, ParsedChunk pc, Dictionary<int, int> occ6, Dictionary<int, int> occ8,
        ChunkScanData cd)
    {
        CardUi.AddHeader(sp,
            $"Chunk {pc.ChunkX}:{pc.ChunkZ} — y{pc.ChunkY}  (world X{pc.WorldX} Y{pc.WorldY} Z{pc.WorldZ})");
        CardUi.AddLine(_host.Owner, sp,
            $"raw {pc.RawBytes.Length} B | version {pc.Version ?? "?"} | solid {pc.SolidCells} fluid {pc.FluidCells} air {pc.AirCells}",
            true);

        // Blocks are statistics only (read-only): "blocks and their placement are not editable"
        CardUi.AddHeader(sp, $"Blocks (read-only, top {Math.Min(12, pc.BlockCounts.Count)})");
        foreach (var kv in pc.BlockCounts.OrderByDescending(x => x.Value).Take(12))
            CardUi.AddLine(_host.Owner, sp, $"   {kv.Key}: {kv.Value}", true);

        if (pc.Entities.Count > 0)
        {
            CardUi.AddHeader(sp, $"Entities ({pc.Entities.Count}) — editable fields");
            CardUi.RenderCardsPaged(sp, pc.Entities.Count, MaxCardsPerSection, (host, i) =>
                RenderEntityCard(host, pc.Entities[i], pc.Position, occ6.TryGetValue(i, out var o) ? o : -1,
                    i < cd.Entities.Count ? cd.Entities[i].Inventories : null));
        }

        if (pc.BlockEntities.Count > 0)
        {
            CardUi.AddHeader(sp, $"Block entities ({pc.BlockEntities.Count}) — editable fields");
            CardUi.RenderCardsPaged(sp, pc.BlockEntities.Count, MaxCardsPerSection, (host, i) =>
                RenderBlockEntityCard(host, pc.BlockEntities[i], pc.Position, occ8.TryGetValue(i, out var o) ? o : -1,
                    i < cd.BlockEntities.Count ? cd.BlockEntities[i].Inventories : null));
        }

        CardUi.AddHeader(sp, "Raw (hex preview)");
        CardUi.AddLine(_host.Owner, sp, CardUi.HexLines(pc.RawBytes, 8), true);
    }

    private void RenderEntityCard(StackPanel parent, EntityRecord e, long position, int occ,
        IReadOnlyList<InventoryInfo>? inventories)
    {
        var card = new StackPanel { Spacing = 1 };
        var parsed = e.Fields.Where(f => f.IsEditable).ToList();
        var opaque = e.Fields.Count(f => f.Type == ChunkFieldType.Opaque);

        CardUi.AddLine(_host.Owner, card,
            $"{e.Species ?? e.TypeName ?? "?"}" + (e.Kind is { } k ? $" [{k}]" : "") +
            (e.SexAge is { } sa ? $" {sa}" : ""), bold: true);
        if (e.TypeName is { } tn && tn != e.Species)
            CardUi.AddLine(_host.Owner, card, $"   type: {tn} v{e.Version}", true);
        if (inventories is { Count: > 0 } invs && invs.Any(i => i.Items.Count > 0))
        {
            CardUi.AddLine(_host.Owner, card, "   inventories: " + InventoryBrowser.Summary(inventories), true, gray: true);
            var show = new Button { Content = "Find items", Padding = new Thickness(8, 2), Margin = new Thickness(0, 2, 0, 0) };
            var query = InventoryBrowser.KeyPrefix(InventoryOwnerKind.Entity, position, occ);
            show.Click += (_, _) => _host.ShowItems(query);
            card.Children.Add(show);
        }

        if (_editingCard == (position, 6, occ))
        {
            // edit mode: a TextBox for every parsed field
            _editBoxes.Clear();
            foreach (var f in parsed) _editBoxes.Add((f, CardUi.AddEditRow(card, f)));
            var rowBtns = new StackPanel
                { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
            var apply = new Button { Content = "Apply", Padding = new Thickness(8, 2) };
            apply.Click += async (_, _) => await ApplyCardEditAsync(6);
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(8, 2) };
            cancel.Click += (_, _) =>
            {
                _editingCard = null;
                RefreshSection(position);
            };
            rowBtns.Children.Add(apply);
            rowBtns.Children.Add(cancel);
            card.Children.Add(rowBtns);
            _editEntity = e;
            _editBe = null;
        }
        else
        {
            CardUi.AddFieldsPreview(_host.Owner, card, parsed);
            if (opaque > 0)
                CardUi.AddLine(_host.Owner, card, $"   … {opaque} opaque bytes (hex-edit only)", true, gray: true);
            if (occ >= 0)
            {
                var editBtn = new Button
                    { Content = "Edit fields", Padding = new Thickness(8, 2), Margin = new Thickness(0, 3, 0, 0) };
                editBtn.Click += (_, _) =>
                {
                    _editingCard = (position, 6, occ);
                    _editEntity = e;
                    _editBe = null;
                    RefreshSection(position);
                };
                card.Children.Add(editBtn);
            }
        }

        parent.Children.Add(CardUi.WrapCard(_host.Owner, card));
    }

    private void RenderBlockEntityCard(StackPanel parent, BlockEntityRecord be, long position, int occ,
        IReadOnlyList<InventoryInfo>? inventories)
    {
        var card = new StackPanel { Spacing = 1 };
        var parsed = be.Fields.Where(f => f.IsEditable).ToList();
        var opaque = be.Fields.Count(f => f.Type == ChunkFieldType.Opaque);

        CardUi.AddLine(_host.Owner, card, $"{be.TypeName ?? "?"}", bold: true);
        if (inventories is { Count: > 0 } invs && invs.Any(i => i.Items.Count > 0))
        {
            CardUi.AddLine(_host.Owner, card, "   inventories: " + InventoryBrowser.Summary(inventories), true, gray: true);
            var show = new Button { Content = "Find items", Padding = new Thickness(8, 2), Margin = new Thickness(0, 2, 0, 0) };
            var query = InventoryBrowser.KeyPrefix(InventoryOwnerKind.BlockEntity, position, occ);
            show.Click += (_, _) => _host.ShowItems(query);
            card.Children.Add(show);
        }

        if (_editingCard == (position, 8, occ))
        {
            _editBoxes.Clear();
            foreach (var f in parsed) _editBoxes.Add((f, CardUi.AddEditRow(card, f)));
            var rowBtns = new StackPanel
                { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
            var apply = new Button { Content = "Apply", Padding = new Thickness(8, 2) };
            apply.Click += async (_, _) => await ApplyCardEditAsync(8);
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(8, 2) };
            cancel.Click += (_, _) =>
            {
                _editingCard = null;
                RefreshSection(position);
            };
            rowBtns.Children.Add(apply);
            rowBtns.Children.Add(cancel);
            card.Children.Add(rowBtns);
            _editBe = be;
            _editEntity = null;
        }
        else
        {
            CardUi.AddFieldsPreview(_host.Owner, card, parsed);
            if (opaque > 0)
                CardUi.AddLine(_host.Owner, card, $"   … {opaque} opaque bytes (hex-edit only)", true, gray: true);
            if (occ >= 0)
            {
                var editBtn = new Button
                    { Content = "Edit fields", Padding = new Thickness(8, 2), Margin = new Thickness(0, 3, 0, 0) };
                editBtn.Click += (_, _) =>
                {
                    _editingCard = (position, 8, occ);
                    _editBe = be;
                    _editEntity = null;
                    RefreshSection(position);
                };
                card.Children.Add(editBtn);
            }
        }

        parent.Children.Add(CardUi.WrapCard(_host.Owner, card));
    }

    /// <summary>Applies the TextBox values to the card fields (6=entity, 8=block entity) and accumulates the edit.</summary>
    private async Task ApplyCardEditAsync(int field)
    {
        if (_editingCard is not (long pos, int f, int occ) || (_editEntity is null && _editBe is null)) return;
        var fields = _editEntity?.Fields ?? _editBe!.Fields;
        var parsed = fields.Where(x => x.IsEditable).ToList();
        // Value parsing is shared with the dump cards (Services/EntityEditService.cs) so the type rules
        // do not drift apart between the two edit entry points.
        var rows = new List<(ChunkField Field, string? Text)>();
        for (var i = 0; i < _editBoxes.Count && i < parsed.Count; i++)
            rows.Add((_editBoxes[i].field, _editBoxes[i].box.Text));
        try
        {
            if (!EntityEditService.TryApplyTexts(rows, out var badValue))
            {
                _host.ChunkStatus = badValue ?? "Bad value";
                return;
            }
        }
        catch (Exception ex)
        {
            _host.ChunkStatus = $"Apply failed: {ex.Message}";
            return;
        }

        // The source of truth is the field from _editingCard (f), not the field argument: otherwise the serializer
        // and the edit key could refer to different structures (B9).
        byte[] payload;
        if (f == 6 && _editEntity is { } en)
        {
            payload = ChunkDataEditor.SerializeEntity(en);
        }
        else if (f == 8 && _editBe is { } be2)
        {
            payload = ChunkDataEditor.SerializeBlockEntity(be2);
        }
        else
        {
            _host.ChunkStatus = "Edit target is not available — reopen the chunk.";
            _editingCard = null;
            return;
        }

        var edits = _host.Edits;
        var label = TargetLabel(f, occ);
        // If this chunk already has a raw hex edit of the whole blob, it would override the field edit:
        // so we ask explicitly and drop the hex instead of losing the edit silently (B4). Both operations are one
        // history step: the user pressed one button, so Undo must be one step too.
        if (edits.HasWholeChunkEdit(pos))
        {
            var okHex = await _host.Confirm("Field edit vs raw hex",
                $"Chunk {CurrentCx},{CurrentCz} already has a pending RAW HEX edit that replaces the whole chunk.\n" +
                "Applying the field edit will DISCARD that raw hex edit for this chunk.\nContinue?");
            if (!okHex) return;
            edits.ReplaceWholeChunkWithPayload(pos, f, occ, payload, label);
        }
        else
        {
            edits.PutPayload(pos, f, occ, payload, label);
        }

        _editingCard = null;
        UpdateDirtyStatus();
        RefreshSection(pos);
    }

    /// <summary>The edit label for history and preview: which object is edited in this column.</summary>
    private string TargetLabel(int field, int occ)
    {
        var kind = field == 6 ? "entity" : field == 8 ? "block entity" : $"field {field}";
        var name = _editEntity is { } en ? en.Species ?? en.TypeName
            : _editBe is { } be ? be.TypeName
            : null;
        return $"{kind}[{occ}]" + (string.IsNullOrEmpty(name) ? "" : $" {name}");
    }

    /// <summary>Hex fallback: applies raw bytes to the current chunk (named in EditSession.HexTargetPosition).</summary>
    public async Task ApplyHexAsync()
    {
        try
        {
            var edits = _host.Edits;
            if (edits.HexTargetPosition < 0 || _host.Save is null)
            {
                _host.ChunkStatus = "No chunk loaded for hex edit.";
                return;
            }

            var bytes = ParseHex(_host.HexBox.Text ?? "");
            if (bytes is null)
            {
                _host.ChunkStatus = "Hex invalid: need pairs of hex digits (e.g. 0a 0b ff).";
                return;
            }

            if (bytes.Length < 2)
            {
                _host.ChunkStatus = "Hex too short.";
                return;
            }

            var pos = edits.HexTargetPosition;
            // raw hex replaces the whole blob - field edits of this position would be lost silently (B4).
            // PutWholeChunk removes them itself and records them in the history, so this is a single undo step.
            if (edits.TryGetPayloadEdits(pos, out var fields) && fields is { Count: > 0 })
            {
                var ok = await _host.Confirm("Raw hex replaces the whole chunk",
                    $"Chunk {edits.HexTargetLabel} already has {fields.Count} pending field edit(s).\n" +
                    "The raw blob replaces the whole chunk and those field edits will be DISCARDED.\nContinue?");
                if (!ok)
                {
                    _host.ChunkStatus = $"Hex apply cancelled — {fields.Count} field edit(s) kept.";
                    return;
                }
            }

            edits.PutWholeChunk(pos, bytes, $"raw hex ({bytes.Length} B)");
            // Redraw the panel: the override warning, the hex preview and the counters all refresh
            Show(CurrentCx, CurrentCz);
        }
        catch (Exception ex)
        {
            _host.ChunkStatus = $"Hex apply failed: {ex.Message}";
        }
    }

    private static byte[]? ParseHex(string s)
    {
        // ToHex appends a service tail "… N more bytes" to the same field we read from:
        // without trimming it, the prefilled hex would always count as invalid and Apply hex would never work.
        var tail = s.IndexOf('…');
        if (tail >= 0) s = s[..tail];
        var clean = new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        if (clean.Length % 2 != 0) return null;
        var res = new byte[clean.Length / 2];
        for (var i = 0; i < res.Length; i++)
            if (!byte.TryParse(clean.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out res[i]))
                return null;
        return res;
    }

    private static string ToHex(byte[] b)
    {
        var sb = new StringBuilder();
        var take = Math.Min(64, b.Length);
        for (var i = 0; i < take; i++) sb.Append(b[i].ToString("x2")).Append(' ');
        if (take < b.Length) sb.Append($"… {b.Length - take} more bytes");
        return sb.ToString();
    }

    /// <summary>The empty panel explains itself: it used to be simply empty without a selection (P1-6).</summary>
    public void ShowEmptyState(string? note = null)
    {
        var panel = _host.Panel;
        panel.Children.Clear();
        _columnSections.Clear();
        var sp = new StackPanel { Spacing = 3 };
        CardUi.AddHeader(sp, "Chunks");
        CardUi.AddLine(_host.Owner, sp,
            "Select a chunk on the map to inspect its entities, block entities and raw blob.");
        CardUi.AddLine(_host.Owner, sp, "Shift+drag selects a region — the Selection tab then works on those chunks.",
            gray: true);
        CardUi.AddLine(_host.Owner, sp,
            "Chunk edits are applied to the loaded working copy (Review & apply…); players live in the Players tab.",
            gray: true);
        if (note is not null) CardUi.AddLine(_host.Owner, sp, note, gray: true);
        panel.Children.Add(sp);
        // The panel explains the empty state itself, so the hint does not go to the shared line:
        // otherwise "No chunk selected." would override the selection message.
        _statusBase = "";
        UpdateDirtyStatus();
    }

    /// <summary>Updates the status and the availability of the save button from the number of accumulated edits.</summary>
    public void UpdateDirtyStatus()
    {
        var chunks = _host.Edits.ChunkCount;
        var players = _host.Edits.PlayerCount;
        _host.SetSaveEnabled(chunks + players > 0);
        _host.PendingChanged();
        // build from _statusBase rather than "+=", so the text does not accumulate across repeated calls
        _host.ChunkStatus = chunks + players > 0
            ? $"{_statusBase}  |  {chunks} chunk(s), {players} player(s) with pending edits — Review & apply…"
            : _statusBase;
    }

    /// <summary>
    ///     A section of one y column: the container and its contribution to the status. It exists so that
    ///     only the affected column is redrawn instead of the whole panel (P1-3), and the status is not recomputed by parsing.
    /// </summary>
    private sealed record ColumnSection(StackPanel Container, int Entities, int Bes, bool OccMismatch);
}