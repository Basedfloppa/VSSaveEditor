using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using VSCore;
using VSSaveEditor.Models;

namespace VSSaveEditor.Components;

/// <summary>The pending edit the window asks for: the rebuilt payload of one record.</summary>
public sealed record EntityDataEditRequest(
    long Position,
    int Field,
    int Occurrence,
    byte[] Payload,
    string Label,
    string Summary);

/// <summary>
///     "Entity data": everything ONE record holds — its own fields, every inventory inside it and, for a trader,
///     its trade offers — in one tree, where values are edited in place and any node can be cut out.
///
///     The shape is the same as the "Player data" window, for the same reason: a creature's data is not "one
///     inventory" or "one mod key", it is a record with nested trees, and deleting an item from a trader means
///     deleting the offer AND the slot that holds it (the game keeps the two in step).
///
///     Nothing is written here: the result becomes ONE pending edit (undoable, reviewable) like every other.
/// </summary>
public sealed class EntityDataDialog
{
    private const int MaxFieldsPerSection = 400;
    private const int MaxListed = 1500;

    private readonly List<EntityRow> _rows;
    private readonly Func<EntityRow, byte[]?> _read;
    private readonly BlockRegistry? _blocks;
    private readonly ItemRegistry? _items;

    private readonly TextBox _filter = new() { Watermark = "filter: name, code, field…" };
    private readonly ListBox _list = new() { MinWidth = 300 };
    private readonly StackPanel _detail = new() { Spacing = 4, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    // Named so the UI probe reads THIS line, not the static hint in the header (both mention "to delete")
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Name = "EditNote" };

    private readonly Button _apply = new()
        { Content = "Add to pending edits", Padding = new Thickness(14, 3), IsEnabled = false };

    private readonly List<EditRow> _edits = new();
    private List<EntityRow> _shown = new();
    private EntityDataDocument? _doc;
    private Window? _window;
    private EntityDataEditRequest? _result;

    private EntityDataDialog(IReadOnlyList<EntityRow> rows, Func<EntityRow, byte[]?> read, BlockRegistry? blocks,
        ItemRegistry? items)
    {
        _rows = rows.ToList();
        _read = read;
        _blocks = blocks;
        _items = items;
    }

    public static async Task<EntityDataEditRequest?> ShowAsync(Window owner, IReadOnlyList<EntityRow> rows,
        EntityRow? selected, Func<EntityRow, byte[]?> read, BlockRegistry? blocks = null, ItemRegistry? items = null)
    {
        var dlg = new EntityDataDialog(rows, read, blocks, items);
        return await dlg.RunAsync(owner, selected);
    }

    private async Task<EntityDataEditRequest?> RunAsync(Window owner, EntityRow? selected)
    {
        _apply.Tag = "apply"; // the UI probe finds the action button by this tag
        var shell = DialogShell.Create("Entity data", "Everything saved for one record", 1160, 740, "380,5,*",
            _apply, _list, _detail);
        _window = shell.Window;

        shell.Header.Children.Add(CardUi.Wrap(_window,
            "A record is a tree: its own fields (name, health, traits, spawn point), the inventories inside it "
            + "(`traderInventory`, `backpack`, `inventory`) with their slots, and a trader's `tradeItems` list."));
        shell.Header.Children.Add(CardUi.Wrap(_window,
            "Edit any value and press ✕ to delete a field, a subtree, an item or a whole offer. Nothing is written "
            + "from here: the result becomes ONE pending edit — review it in Review & apply, then apply it to the "
            + "working copy (undoable) and save a copy.", true));

        // The rows carry a full Display line (name, code, chunk, size): without wrapping the tail is clipped in
        // a 380-px list, and "which record is this" is exactly the tail.
        _list.ItemTemplate = new FuncDataTemplate<string>((text, _) => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap
        });

        var filterRow = new StackPanel
            { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        filterRow.Children.Add(new TextBlock { Text = "Record:", VerticalAlignment = VerticalAlignment.Center });
        _filter.Width = 300;
        filterRow.Children.Add(_filter);
        _filter.TextChanged += (_, _) => FilterRows(selected);
        shell.Header.Children.Add(filterRow);

        shell.Bottom.Children.Add(_note);
        shell.Bottom.Children.Add(_status);

        _list.SelectionChanged += (_, _) => LoadSelected();
        _apply.Click += (_, _) => Apply();
        FilterRows(selected);
        await shell.Window.ShowDialog(owner);
        return _result;
    }

    // ------------------------- record list -------------------------

    private void FilterRows(EntityRow? select)
    {
        var q = (_filter.Text ?? "").Trim();
        var matched = q.Length == 0
            ? _rows
            : _rows.Where(r => r.Display.Contains(q, StringComparison.OrdinalIgnoreCase)
                               || r.Code.Contains(q, StringComparison.OrdinalIgnoreCase)
                               || r.CustomName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        var capped = matched.Count > MaxListed;
        _shown = capped ? matched.Take(MaxListed).ToList() : matched;
        _list.ItemsSource = _shown.Select(r => r.ItemCount > 0 ? $"{r.Display}  ·  {r.InventorySummary}" : r.Display)
            .ToList();
        _status.Text = capped
            ? $"{matched.Count} record(s) match — the list shows the first {MaxListed}; narrow the filter to see the rest."
            : "";

        var index = select is null ? -1 : _shown.FindIndex(r => r.Position == select.Position && r.Occurrence == select.Occurrence && r.Field == select.Field);
        _list.SelectedIndex = _shown.Count == 0 ? -1 : index >= 0 ? index : 0;
    }

    /// <summary>Loads the selected record and draws it. Selection changes go through here.</summary>
    private void LoadSelected()
    {
        _detail.Children.Clear();
        _edits.Clear();
        _apply.IsEnabled = false;
        _note.Text = "";
        _doc = null;

        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _shown.Count)
        {
            _detail.Children.Add(CardUi.Wrap(_window, _rows.Count == 0
                ? "No records to show — open a save and pick something in the Entities tab."
                : "Select a record in the list."));
            return;
        }

        var row = _shown[_list.SelectedIndex];
        var payload = _read(row);
        if (payload is not { Length: > 0 })
        {
            _detail.Children.Add(CardUi.Wrap(_window, $"record {row.Position}:{row.Occurrence} could not be read."));
            return;
        }

        try
        {
            _doc = EntityDataDocument.Load(payload, row.Field, row.Position, row.Occurrence, row.Label, _blocks, _items);
        }
        catch (Exception ex)
        {
            _detail.Children.Add(CardUi.Wrap(_window, $"record {row.Position}:{row.Occurrence} parse error: {ex.Message}"));
            return;
        }

        DrawDocument(payload.Length);
    }

    /// <summary>Draws the loaded document as it is now — deletions and edits included. Redrawing must NOT reload
    /// from the saved bytes: that is how a mark made one click ago would silently disappear.</summary>
    private void DrawDocument(int payloadBytes)
    {
        _detail.Children.Clear();
        _edits.Clear();
        if (_doc is null) return;

        _detail.Children.Add(new TextBlock
        {
            Text = _doc.Label, FontWeight = FontWeight.Bold, FontSize = 14, TextWrapping = TextWrapping.Wrap
        });
        _detail.Children.Add(CardUi.Wrap(_window,
            $"field {(int)_doc.Field} · occurrence {_doc.Occurrence} · {CardUi.FormatBytes(payloadBytes)} · "
            + $"{_doc.Blocks.Count} block(s)" + (_doc.Lossless ? "" : " · NOT lossless: read-only")));

        // The blocks are collapsed, and a collapsed block is just a title: say that it opens, or the inventory
        // looks like a header with nothing behind it.
        if (_doc.Blocks.Count > 0)
            _detail.Children.Add(CardUi.Wrap(_window,
                "Click a block title to open it — the record's fields, the inventory's items (code and count "
                + "are editable) or the trader's offers."));

        if (_doc.Blocks.Count == 0)
        {
            _detail.Children.Add(CardUi.Wrap(_window,
                "This payload has no named field — it is not an ECX tree the editor understands."));
            return;
        }

        if (!_doc.Lossless)
            _detail.Children.Add(CardUi.Warn(_window,
                "This record does not round-trip through the parser: rebuilding it would change bytes the editor "
                + "does not understand, so applying is disabled for it."));

        foreach (var block in _doc.Blocks) _detail.Children.Add(RenderBlock(block));
        UpdateSummary();
    }

    // ------------------------- blocks -------------------------

    private Control RenderBlock(EntityDataBlock block)
    {
        var card = new StackPanel { Spacing = 2 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        head.Children.Add(new TextBlock
        {
            Text = block.Kind switch
            {
                EntityDataKind.Record => "Record",
                EntityDataKind.Inventory => "Inventory",
                _ => "Trade offers"
            },
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        head.Children.Add(new TextBlock
        {
            Text = block.Title,
            FontFamily = UiStyle.Mono,
            FontSize = UiStyle.MonoFontSize,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        card.Children.Add(head);
        card.Children.Add(CardUi.Wrap(_window, block.Subtitle));

        var body = new StackPanel { Spacing = 1, Margin = new Thickness(0, 2, 0, 0) };

        if (block.Kind != EntityDataKind.Record)
        {
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var del = new Button { Content = "Delete this block", Padding = new Thickness(10, 2) };
            del.Tag = "block:" + block.Title;
            ToolTip.SetTip(del, block.Kind == EntityDataKind.Inventory
                ? "The inventory disappears from the record — its items are gone"
                : "The whole `tradeItems` list disappears: the trader sells nothing");
            var marked = false;
            del.Click += (_, _) =>
            {
                marked = !marked;
                block.Removed = marked;
                del.Content = marked ? "Deleted — click to restore" : "Delete this block";
                body.Opacity = marked ? 0.4 : 1;
                UpdateSummary();
            };
            toolbar.Children.Add(del);
            // Inside the block body, not on the card: the body is what the Expander shows, and a control added to
            // the card itself was dropped when the card was replaced by the collapse header.
            body.Children.Add(toolbar);
        }

        if (block.Kind == EntityDataKind.Record) RenderRecordTree(body);
        else if (block.Kind == EntityDataKind.Inventory) RenderInventory(body, block);
        else RenderOffers(body, block);

        // Collapsed by default: an entity's record holds hundreds of fields, and while it was expanded the
        // inventory and the offer list — the reason this window exists — sat below the fold and looked absent.
        var expander = new Expander
        {
            Header = new TextBlock
            {
                Text = $"{block.Title} · {block.Subtitle}",
                FontFamily = UiStyle.Mono,
                FontSize = UiStyle.MonoFontSize,
                TextWrapping = TextWrapping.Wrap
            },
            Content = body,
            IsExpanded = false,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        card.Children.Clear();
        card.Children.Add(expander);
        return CardUi.WrapCard(_window!, card);
    }

    /// <summary>The record's own fields. Trees that have a block of their own (inventories, offer lists) are
    /// left out here — otherwise the same slots would be listed twice with two different delete buttons.</summary>
    private void RenderRecordTree(StackPanel parent)
    {
        if (_doc is null) return;
        var owned = _doc.Blocks.Where(b => b.Node is not null).Select(b => b.Node!).ToHashSet(ReferenceEqualityComparer.Instance);

        var shown = 0;
        var hidden = 0;
        foreach (var f in _doc.RootFields)
        {
            if (f.IsHeaderNoise)
            {
                hidden++;
                continue;
            }

            if (owned.Contains(f)) continue;
            if (++shown > MaxFieldsPerSection)
            {
                parent.Children.Add(CardUi.Wrap(_window, $"… {_doc.RootFields.Count - shown} more field(s) not shown"));
                break;
            }

            parent.Children.Add(RenderField(f, 0));
        }

        if (hidden > 0)
            parent.Children.Add(CardUi.Wrap(_window,
                $"{hidden} unparsed byte(s) not shown — the game wrote them without a name this editor "
                + "understands (the position and behaviour blobs), so they cannot be edited or deleted here"));
    }

    private Control RenderField(ChunkField f, int depth)
        => CardUi.AddFieldRow(_window, f, depth, "Delete this field from the saved record",
            pathLabel: false, UpdateSummary, (field, box) => _edits.Add(new EditRow(field, box, field.EditText)));

    /// <summary>An inventory: one row per filled slot (the slot key stays what it is — the slot index is the
    /// position in the grid), the item's attributes inside the row, then the inventory's own fields.</summary>
    private void RenderInventory(StackPanel parent, EntityDataBlock block)
    {
        if (_doc is null || block.Node is null) return;
        var stacks = EntityDataDocument.FilledSlots(block);

        if (stacks.Count == 0)
            parent.Children.Add(CardUi.Wrap(_window, "(this inventory is empty)"));
        else
            parent.Children.Add(ColumnCaptions("slot", "item code", "count"));

        foreach (var stack in stacks)
        {
            var attrs = new StackPanel { Spacing = 1, Margin = new Thickness(8, 0, 0, 0) };
            foreach (var a in stack.Children.Where(c => !c.IsHeaderNoise)) attrs.Children.Add(RenderField(a, 1));

            var row = new CardUi.TreeRow
            {
                Label = $"slot {stack.Key}",
                Tip = block.PairedOffers is null
                    ? "Delete this item from the inventory"
                    : "Delete this item AND its trade offer — the offer would otherwise sell an item that is not in the inventory",
                LabelTip = $"{block.Node.Key}.slots.{stack.Key}",
                LabelWidth = ItemLabelWidth,
                BoxWidth = ItemCodeWidth,
                Removed = stack.Removed,
                ToggleRemoved = () =>
                {
                    _status.Text = _doc.RemoveItem(block, stack);
                    return stack.Removed;
                },
                OnChanged = UpdateSummary,
                Children = attrs.Children.Count > 0 ? attrs : null,
                Expand = false,
                Extra = BuildItemEditor(stack)
            };
            parent.Children.Add(CardUi.AddTreeRow(_window, row, out _));
        }

        // What the block already shows is decided by the document (it knows its own nodes), not by key names here
        var rest = block.Node.Children.Where(c => !_doc!.IsBlockNode(c) && !c.IsHeaderNoise).ToList();
        if (rest.Count > 0)
        {
            var other = new StackPanel { Spacing = 1, Margin = new Thickness(0, 3, 0, 0) };
            other.Children.Add(new TextBlock
            {
                Text = $"{block.Node.Key} fields", FontSize = 11, Foreground = Brushes.Gray
            });
            foreach (var f in rest) other.Children.Add(RenderField(f, 1));
            parent.Children.Add(other);
        }
    }

    /// <summary>Row geometry in one place: a caption that is 2 px off its column reads as a different field.</summary>
    private const double ItemLabelWidth = 110;
    private const double ItemCodeWidth = 200;
    private const double ItemCountWidth = 52;

    /// <summary>Without these captions the row's two boxes are unlabelled — which one is the code and which the
    /// amount is not visible, and the ✕ that removes the item looks like part of the label.</summary>
    private Control ColumnCaptions(string label, string code, string count, string trailing = null)
    {
        // 24 px is the ✕ button of a row (18) plus the 6 px gap the strip puts before the label
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(24, 0, 0, 2)
        };
        strip.Children.Add(Caption(label, ItemLabelWidth));
        strip.Children.Add(Caption(code, ItemCodeWidth));
        strip.Children.Add(Caption(count, ItemCountWidth));
        if (trailing is { Length: > 0 }) strip.Children.Add(Caption(trailing, double.NaN));
        return strip;
    }

    private static Control Caption(string text, double width)
    {
        var tb = new TextBlock
        {
            Text = text, FontSize = 11, Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!double.IsNaN(width)) tb.Width = width;
        return tb;
    }

    /// <summary>
    ///     What a slot holds — the item code and how many. Both live in the ItemStack header (class/id/size),
    ///     not in the tree, so before this the window could only delete an item, never change it. The same editor
    ///     serves the inventory rows and the offer rows: both call the document, which keeps the paired halves in step.
    /// </summary>
    private Control BuildItemEditor(ChunkField stack)
    {
        var code = InventoryParser.CodeOf(stack.ItemClass, stack.ItemId, _blocks, _items);
        if (code.Length == 0) code = (stack.ItemClass == 0 ? "block:" : "item:") + stack.ItemId;

        var codeBox = new TextBox
        {
            Text = code, Width = ItemCodeWidth, FontFamily = UiStyle.Mono, FontSize = UiStyle.MonoFontSize,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = "item-code"
        };
        ToolTip.SetTip(codeBox,
            "Item code this slot holds ('clothes-head-minstrel-hat', or 'item:123' / 'block:123' when the code "
            + "is unknown). The offer that sells it follows: the game keeps offer and slot in step.");

        var countBox = new TextBox
        {
            Text = stack.StackSize.ToString(), Width = ItemCountWidth, FontFamily = UiStyle.Mono,
            FontSize = UiStyle.MonoFontSize,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = "item-count"
        };
        ToolTip.SetTip(countBox, "How many are in this stack (1 or more — an empty slot is the ✕ button)");

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        panel.Children.Add(codeBox);
        panel.Children.Add(countBox);

        void Sync()
        {
            if (!int.TryParse(countBox.Text, out var count))
            {
                _status.Text = "Not changed: the count must be a whole number.";
                return;
            }

            var before = stack.StackSize;
            var beforeCode = InventoryParser.CodeOf(stack.ItemClass, stack.ItemId, _blocks, _items);
            if (!_doc!.TrySetItem(stack, codeBox.Text ?? "", count, out var error))
            {
                _status.Text = "Not changed: " + error;
                return;
            }

            var code2 = InventoryParser.CodeOf(stack.ItemClass, stack.ItemId, _blocks, _items);
            if (code2.Length == 0) code2 = (stack.ItemClass == 0 ? "block:" : "item:") + stack.ItemId;
            _status.Text = $"slot '{stack.Key}': {code2} ×{stack.StackSize}"
                           + (beforeCode != code2 ? $" (was {beforeCode})" : "")
                           + (before != stack.StackSize ? $" (was ×{before})" : "");
            UpdateSummary();
        }

        // On Enter or on leaving the box, not on every keystroke: a half-typed code would report "no such item"
        void OnKey(object? _, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Sync();
        }

        codeBox.KeyDown += OnKey;
        countBox.KeyDown += OnKey;
        codeBox.LostFocus += (_, _) => Sync();
        countBox.LostFocus += (_, _) => Sync();
        return panel;
    }

    /// <summary>A trader: one row per offer with its price/stock, the offer's fields inside the row (that is
    /// where a price is edited), and the paired slot going with it on deletion.</summary>
    private void RenderOffers(StackPanel parent, EntityDataBlock block)
    {
        if (_doc is null || block.Node is null) return;

        if (_doc.Offers.Count > 0)
            parent.Children.Add(ColumnCaptions("offer", "item code", "count", "price · stock"));

        foreach (var offer in _doc.Offers)
        {
            var node = block.Node.Children.FirstOrDefault(c => c.Key == offer.Key);
            if (node is null) continue;

            var fields = new StackPanel { Spacing = 1, Margin = new Thickness(8, 0, 0, 0) };
            foreach (var child in node.Children.Where(c => !c.IsHeaderNoise)) fields.Children.Add(RenderField(child, 1));

            var stack = node.Children.FirstOrDefault(c => c.Type == ChunkFieldType.ItemStack);
            var editor = stack is null ? null : BuildItemEditor(stack);

            var row = new CardUi.TreeRow
            {
                Label = $"offer {offer.Key}",
                Tip = "Delete this offer together with the inventory slot that holds its item",
                LabelTip = $"{block.Node.Key}.{offer.Key}",
                LabelWidth = ItemLabelWidth,
                BoxWidth = ItemCodeWidth,
                Removed = node.Removed,
                ToggleRemoved = () =>
                {
                    _status.Text = _doc.RemoveOffer(offer.Key);
                    return block.Node.Children.FirstOrDefault(c => c.Key == offer.Key)?.Removed ?? false;
                },
                OnChanged = UpdateSummary,
                Hint = $"price {offer.Price:0.##} · stock {offer.Stock}"
                       + (offer.HasSlot ? "" : " · no slot"),
                Children = fields,
                Expand = false,
                Extra = editor
            };
            parent.Children.Add(CardUi.AddTreeRow(_window, row, out _));
        }
    }

    // ------------------------- apply -------------------------

    private void UpdateSummary()
    {
        if (_doc is null)
        {
            _apply.IsEnabled = false;
            return;
        }

        // The model counts deletions; a value still sitting in a box is not in the model until apply (or a redraw),
        // so a typed value alone would leave the button dead. Both sources, one "to write" per typed box.
        var counts = _doc.Counts();
        var typed = _edits.Count(r => r.IsChanged);
        var (text, any) = EditCountsLabel.Live(typed,
            (counts.FieldsEdited, "value(s) changed"),
            (counts.OffersRemoved, "trade offer(s) deleted"),
            (counts.ItemsRemoved, "item(s) deleted"),
            (counts.NodesRemoved, "field(s) deleted"),
            (counts.BlocksRemoved, "block(s) deleted"));
        _apply.IsEnabled = any && _doc.Lossless;
        _note.Text = !any
            ? "Nothing changed yet."
            : text + (counts.BytesFreed > 0 ? $" ({counts.BytesFreed} B freed)" : "");
    }

    private bool PushTexts(out string? error)
    {
        error = null;
        foreach (var row in _edits)
        {
            if (!row.IsChanged) continue; // an untouched box must not mark the value as edited
            if (row.Field is not { } field) continue; // an ECX row always has one; a PbNode row (player window) does not
            if (!ChunkFieldText.TryApply(field, row.Box.Text, out error))
            {
                error = $"{field.DisplayPath}: {error}";
                return false;
            }
        }

        return true;
    }

    private void Apply()
    {
        if (_doc is null) return;
        if (!PushTexts(out var error))
        {
            _status.Text = "Not applied: " + error;
            return;
        }

        EntityDataBuild build;
        try
        {
            build = _doc.Build();
        }
        catch (Exception ex)
        {
            _status.Text = "Rebuild failed: " + ex.Message;
            return;
        }

        if (!build.Changed)
        {
            _status.Text = "Nothing to apply — no value differs from the saved one.";
            return;
        }

        _result = new EntityDataEditRequest(_doc.Position, _doc.Field, _doc.Occurrence, build.Payload!,
            $"entity data {_doc.Label}: {build.Summary}", build.Summary);
        _window?.Close();
    }
}
