using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using VSCore;
using VSSaveEditor.Models;

namespace VSSaveEditor.Components;

/// <summary>The pending edit the window asks for: the rebuilt blobs of one player's data.</summary>
public sealed record PlayerDataEditRequest(
    long PlayerId,
    string? PlayerUid,
    byte[]? NewPlayerdata,
    byte[]? NewGamedata,
    string Label,
    string Summary);

/// <summary>
///     "Player data": everything the save knows about ONE player, in one tree — the character record of
///     playerdata (health, hunger, stats, skin), its inventories, and every `gamedata` key that belongs to it
///     or names it. Values are edited in place and any node can be cut out, including a whole key.
///
///     This window replaced "clear the data of mod X for everybody" as the primary action: that was one case of
///     "edit what this player owns", and the general view is the honest one — most of a player's data is not in
///     the mod keys at all, and a mod key is not the only thing one wants to delete.
///
///     Nothing is written here: the result becomes ONE pending edit (undoable, reviewable) like every other.
/// </summary>
public sealed class PlayerDataDialog
{
    private const int MaxFieldsPerSection = 400;

    private readonly List<PlayerIdentity> _players;

    /// <summary>All uids of the save: a mod key is recognised as "this player's" only against this set, and a
    /// world key as "names a player" only against it too.</summary>
    private readonly HashSet<string> _uids;
    private readonly byte[]? _gamedata;
    private readonly Func<long, byte[]?> _readPlayer;
    private readonly Func<Task>? _openBulk;

    private readonly TextBox _filter = new() { Watermark = "filter: name, uid, key, field…" };
    private readonly ListBox _playerList = new() { MinWidth = 240 };
    private readonly StackPanel _detail = new() { Spacing = 4, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap };

    private readonly Button _applyBtn = new()
        { Content = "Add to pending edits", Padding = new Thickness(14, 3), IsEnabled = false };

    private readonly List<TextEditRow> _rows = new();
    private PlayerDataDocument? _doc;
    private List<PlayerIdentity> _shown = new();
    private Window? _window;
    private PlayerDataEditRequest? _result;

    private PlayerDataDialog(IReadOnlyList<PlayerIdentity> players, byte[]? gamedata,
        Func<long, byte[]?> readPlayer, Func<Task>? openBulk)
    {
        _players = players.ToList();
        _uids = new HashSet<string>(_players.Select(p => p.Uid).Where(u => u.Length > 0), StringComparer.Ordinal);
        _gamedata = gamedata;
        _readPlayer = readPlayer;
        _openBulk = openBulk;
    }

    public static async Task<PlayerDataEditRequest?> ShowAsync(Window owner, IReadOnlyList<PlayerIdentity> players,
        byte[]? gamedata, Func<long, byte[]?> readPlayer, Func<Task>? openBulk = null)
    {
        var dlg = new PlayerDataDialog(players, gamedata, readPlayer, openBulk);
        return await dlg.RunAsync(owner);
    }

    private async Task<PlayerDataEditRequest?> RunAsync(Window owner)
    {
        _applyBtn.Tag = "apply"; // the UI probe finds the action button by this tag
        var shell = DialogShell.Create("Player data", "Everything saved for one player", 1120, 720, "320,5,*",
            _applyBtn, _playerList, _detail);
        _window = shell.Window;

        shell.Header.Children.Add(CardUi.Wrap(_window,
            "The character lives in playerdata (an EntityPlayer tree: health, hunger, temperature, stats, skin), "
            + "its inventories next to it, and the mods keep their state in the world message (`gamedata`) as keys "
            + "that either end with the player uid or mention it inside the value."));
        shell.Header.Children.Add(CardUi.Wrap(_window,
            "Edit any value and press ✕ to delete a field, a whole subtree or a key. Nothing is written from here: "
            + "the result becomes ONE pending edit — review it in Review & apply, then apply it to the working copy "
            + "(undoable) and save a copy.", true));

        var filterRow = new StackPanel
            { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        filterRow.Children.Add(new TextBlock { Text = "Player:", VerticalAlignment = VerticalAlignment.Center });
        _filter.Width = 260;
        filterRow.Children.Add(_filter);
        if (_openBulk is not null)
        {
            var bulk = new Button
            {
                Content = "All players by mod…",
                Padding = new Thickness(10, 2),
                Margin = new Thickness(12, 0, 0, 0)
            };
            ToolTip.SetTip(bulk, "The old view: one mod, every player at once (clear a mod's per-player keys wholesale)");
            bulk.Click += async (_, _) => await _openBulk();
            filterRow.Children.Add(bulk);
        }

        shell.Header.Children.Add(filterRow);
        shell.Bottom.Children.Add(_note);
        shell.Bottom.Children.Add(_status);

        _filter.TextChanged += (_, _) => FilterPlayers();
        _playerList.SelectionChanged += (_, _) => RenderDetail();
        _applyBtn.Click += (_, _) => Apply();
        FilterPlayers(); // the list has to be filled before the window is shown, not by the first keystroke
        await shell.Window.ShowDialog(owner);
        return _result;
    }

    // ------------------------- player list -------------------------

    private void FilterPlayers()
    {
        var q = (_filter.Text ?? "").Trim();
        _shown = q.Length == 0
            ? _players.ToList()
            : _players.Where(p => (p.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                                  || p.Uid.Contains(q, StringComparison.OrdinalIgnoreCase)
                                  || p.Id.ToString().Contains(q, StringComparison.Ordinal)).ToList();
        var keep = _playerList.SelectedIndex;
        _playerList.ItemsSource = _shown.Select(DialogShell.DescribePlayer).ToList();
        _playerList.SelectedIndex = _shown.Count == 0 ? -1 : Math.Clamp(keep, 0, _shown.Count - 1);
    }

    /// <summary>Loads the selected player's document and draws it. Selection changes go through here.</summary>
    private void RenderDetail()
    {
        _detail.Children.Clear();
        _rows.Clear();
        _applyBtn.IsEnabled = false;
        _note.Text = "";
        _status.Text = "";
        _doc = null;

        if (_playerList.SelectedIndex < 0 || _playerList.SelectedIndex >= _shown.Count)
        {
            _detail.Children.Add(CardUi.Wrap(_window, _players.Count == 0
                ? "This save has no playerdata rows."
                : "Select a player in the list."));
            return;
        }

        var player = _shown[_playerList.SelectedIndex];
        var raw = _readPlayer(player.Id);
        if (raw is not { Length: > 0 })
        {
            _detail.Children.Add(CardUi.Wrap(_window, $"playerdata row #{player.Id} has no data."));
            return;
        }

        try
        {
            _doc = PlayerDataDocument.Load(player.Id, player.Uid, player.Name, raw, _gamedata, _uids);
        }
        catch (Exception ex)
        {
            _detail.Children.Add(CardUi.Wrap(_window, $"playerdata #{player.Id} parse error: {ex.Message}"));
            return;
        }

        DrawDocument(raw.Length);
    }

    /// <summary>Draws the loaded document as it is now — marks and edits included. Redrawing must NOT reload
    /// from the saved bytes: that is how a mark made one click ago would silently disappear.</summary>
    private void DrawDocument(int playerBytes)
    {
        _detail.Children.Clear();
        _rows.Clear();
        if (_doc is null) return;

        _detail.Children.Add(new TextBlock
        {
            Text = _doc.Label, FontWeight = FontWeight.Bold, FontSize = 14, TextWrapping = TextWrapping.Wrap
        });
        _detail.Children.Add(CardUi.Wrap(_window, _doc.Gamedata is { Length: > 0 }
            ? $"playerdata {CardUi.FormatBytes(playerBytes)} · gamedata {CardUi.FormatBytes(_doc.Gamedata.Length)} "
              + $"· {_doc.Blocks.Count} data block(s)"
            : $"playerdata {CardUi.FormatBytes(playerBytes)} · the save has no gamedata row, so there is no mod "
              + "data to show"));

        foreach (var block in _doc.Blocks) _detail.Children.Add(RenderBlock(block));
        UpdateSummary();
    }

    // ------------------------- blocks -------------------------

    private Control RenderBlock(PlayerDataBlock block)
    {
        var card = new StackPanel { Spacing = 2 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        head.Children.Add(new TextBlock
        {
            Text = block.Kind switch
            {
                PlayerDataKind.Character => "Character",
                PlayerDataKind.Inventory => "Inventory",
                PlayerDataKind.ModKey => "Mod key (this player)",
                _ => "World key naming this player"
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
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        // ---- actions ----
        if (block.Kind is PlayerDataKind.Inventory or PlayerDataKind.ModKey)
        {
            var del = new Button { Content = "Delete this block", Padding = new Thickness(10, 2) };
            del.Tag = "block:" + block.Title; // the UI probe finds the action of a block by its key
            ToolTip.SetTip(del, block.Kind == PlayerDataKind.Inventory
                ? "The inventory disappears from playerdata — its items are gone"
                : "The key disappears from the world's mod data (this player only)");
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
        }

        if (block.Kind == PlayerDataKind.SharedKey && _doc?.Uid is { Length: > 0 } uid)
        {
            var count = PlayerDataDocument.CountPlayerEntries(block, uid);
            var mark = new Button
            {
                Tag = "mark:" + block.Title,
                Content = $"Remove entries naming this player ({count})",
                Padding = new Thickness(10, 2),
                IsEnabled = count > 0
            };
            ToolTip.SetTip(mark,
                "One dictionary entry per player: only the entries whose value contains this player's uid are cut. "
                + "The rest of the key belongs to other players and stays.");
            mark.Click += (_, _) =>
            {
                var n = PlayerDataDocument.MarkPlayerEntries(block, uid);
                ReRender();
                _status.Text = $"{n} entry(ies) of '{block.Title}' marked for deletion.";
            };
            toolbar.Children.Add(mark);
        }

        if (toolbar.Children.Count > 0) card.Children.Add(toolbar);

        // ---- tree ----
        if (block.FieldRoots is { } roots) RenderFields(body, roots);
        else if (block.Nodes is { } nodes) RenderNodes(body, nodes);

        card.Children.Add(body);
        return CardUi.WrapCard(_window!, card);
    }

    /// <summary>
    ///     Redraws after an action that changes the model (marking the player's entries in a shared key).
    ///     The typed values are pushed into the model FIRST: they live in text boxes, and a redraw would throw
    ///     away everything the user has typed but not yet applied.
    /// </summary>
    private void ReRender()
    {
        if (_doc is null) return;
        if (!PushTexts(out var error))
        {
            _status.Text = "Not redrawn: " + error;
            return;
        }

        DrawDocument(_readPlayer(_doc.PlayerId)?.Length ?? 0);
    }

    // ------------------------- ECX tree (character, inventories) -------------------------

    private void RenderFields(StackPanel parent, List<ChunkField> fields)
    {
        var shown = 0;
        var hidden = 0;
        foreach (var f in fields)
        {
            if (IsHeaderNoise(f))
            {
                hidden++;
                continue;
            }

            if (++shown > MaxFieldsPerSection)
            {
                parent.Children.Add(CardUi.Wrap(_window, $"… {fields.Count - shown} more field(s)"));
                break;
            }

            parent.Children.Add(RenderField(f, 0));
        }

        if (hidden > 0)
            parent.Children.Add(CardUi.Wrap(_window,
                $"{hidden} unparsed byte field(s) not shown — the game wrote them without a "
                + "name this editor understands, so they cannot be edited or deleted here"));
    }

    /// <summary>
    ///     The bytes in front of the ECX tree of an entity: the parser keeps them byte for byte, but they carry
    ///     no key and cannot be edited — showing them as a dozen "&lt;1 raw byte&gt;" rows only hides the real data.
    /// </summary>
    private static bool IsHeaderNoise(ChunkField f)
        => f.Type == ChunkFieldType.Opaque && (f.Key.Length == 0 || f.Key.Any(c => char.IsControl(c)));

    private Control RenderField(ChunkField f, int depth)
    {
        var childrenShown = f.Children.Where(c => !IsHeaderNoise(c)).ToList();
        var isTree = f.Type == ChunkFieldType.Tree && childrenShown.Count > 0 && !f.IsRawTree;
        StackPanel? children = null;
        if (isTree)
        {
            children = new StackPanel { Spacing = 1, Margin = new Thickness(8, 0, 0, 0) };
            foreach (var child in childrenShown) children.Children.Add(RenderField(child, depth + 1));
        }

        var row = new CardUi.TreeRow
        {
            Label = f.Path.Length > 0 ? f.Path : f.Key,
            Tip = isTree
                ? "Delete this whole subtree"
                : "Delete this field from the saved character/inventory",
            LabelTip = $"{f.DisplayPath} ({f.Type})",
            Indent = depth * 10,
            LabelWidth = 250,
            BoxWidth = 180,
            Removed = f.Removed,
            ToggleRemoved = () => f.Removed = !f.Removed,
            OnChanged = UpdateSummary,
            EditableText = f.IsEditable ? f.EditText : null,
            ValueText = f.ValueText,
            Hint = f.IsEditable ? f.Type.ToString() : isTree ? $"{childrenShown.Count} field(s)" : "",
            Children = children,
            Expand = depth == 0 && childrenShown.Count <= 8
        };

        var control = CardUi.AddTreeRow(_window, row, out var box);
        if (box is not null) _rows.Add(new TextEditRow(f, box, f.EditText));
        return control;
    }

    // ------------------------- protobuf tree (mod keys) -------------------------

    private void RenderNodes(StackPanel parent, List<PbNode> nodes)
    {
        if (nodes.Count == 0)
        {
            parent.Children.Add(CardUi.Wrap(_window, "(empty value)"));
            return;
        }

        foreach (var n in nodes) parent.Children.Add(RenderNode(n, 0));
    }

    private Control RenderNode(PbNode node, int depth)
    {
        StackPanel? children = null;
        if (node.IsMessage)
        {
            children = new StackPanel { Spacing = 1, Margin = new Thickness(8, 0, 0, 0) };
            foreach (var c in node.Children!) children.Children.Add(RenderNode(c, depth + 1));
        }

        // A value this editor cannot show as a small editable field is shown as text only: a 7-KB opaque blob
        // in a text box is not an editor, and a mistyped hex digit would silently replace the mod's whole state.
        var editable = !node.Raw && !node.IsMessage
                       && (node.WireType == 0 || (node.WireType == 2 && node.Payload.Length <= 256));
        var text = node.ValueText;
        var row = new CardUi.TreeRow
        {
            Label = node.Label,
            Tip = "Delete this entry (the whole subtree) from this player's data",
            Indent = depth * 14,
            LabelWidth = 110,
            BoxWidth = 220,
            Removed = node.Removed,
            ToggleRemoved = () => node.Removed = !node.Removed,
            OnChanged = UpdateSummary,
            EditableText = editable ? text : null,
            ValueText = text,
            Hint = node.IsMessage
                ? $"{node.Children!.Count} field(s), {node.Payload.Length} B"
                : editable
                    ? node.WireType == 0 ? "number" : node.Text is null ? "hex" : "text"
                    : "",
            Children = children,
            Expand = depth == 0 && node.Children is { Count: <= 6 }
        };

        var control = CardUi.AddTreeRow(_window, row, out var box);
        if (box is not null) _rows.Add(new TextEditRow(node, box, text));
        return control;
    }

    // ------------------------- apply -------------------------

    private void UpdateSummary()
    {
        if (_doc is null)
        {
            _applyBtn.IsEnabled = false;
            return;
        }

        // Counted on the model, not on the boxes: a redraw recreates the boxes with the already-edited text,
        // and the "what will be written" line must not fall back to zero because of that.
        var counts = _doc.Counts();
        _applyBtn.IsEnabled = counts.Changed;
        _note.Text = counts.Changed
            ? $"{counts.ValuesEdited} value(s) to write, {counts.NodesRemoved} node(s) to delete, "
              + $"{counts.BlocksRemoved} block(s) to delete"
            : "Nothing changed yet.";
    }

    private bool PushTexts(out string? error)
    {
        error = null;
        foreach (var row in _rows)
        {
            if (!row.IsChanged) continue; // an untouched box must not mark the value as edited
            if (row.Field is { } field)
            {
                if (!ChunkFieldText.TryApply(field, row.Box.Text, out error))
                {
                    error = $"{field.DisplayPath}: {error}";
                    return false;
                }

                continue;
            }

            var node = row.Node!;
            var text = (row.Box.Text ?? "").Trim();
            if (node.WireType == 0)
            {
                if (!long.TryParse(text, out var value))
                {
                    error = $"{node.Label}: '{text}' is not a whole number";
                    return false;
                }

                PbTree.SetInt(node, value);
                continue;
            }

            if (node.Text is not null)
            {
                PbTree.SetText(node, row.Box.Text ?? "");
                continue;
            }

            if (!ChunkFieldText.TryParseHex(text, out var bytes, out var hexError))
            {
                error = $"{node.Label}: {hexError}";
                return false;
            }

            PbTree.SetBytes(node, bytes);
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

        PlayerDataBuild build;
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

        var label = $"player data {_doc.Label}: {build.Summary}";
        _result = new PlayerDataEditRequest(_doc.PlayerId, _doc.Uid, build.NewPlayerdata, build.NewGamedata,
            label, build.Summary);
        _window?.Close();
    }

    // ------------------------- helpers -------------------------

    /// <summary>One editable row: a box, and the text it had when the row was drawn. Only a box whose text
    /// differs is pushed into the model — otherwise every Apply would "edit" every field it could show, and the
    /// pending edit would rewrite blobs nobody touched.</summary>
    private sealed class TextEditRow
    {
        public TextEditRow(ChunkField? field, TextBox box, string original)
        {
            Field = field;
            Box = box;
            Original = original;
        }

        public TextEditRow(PbNode node, TextBox box, string original)
        {
            Node = node;
            Box = box;
            Original = original;
        }

        public ChunkField? Field { get; }
        public PbNode? Node { get; }
        public TextBox Box { get; }
        public string Original { get; }

        public bool IsChanged => (Box.Text ?? "") != Original;
    }
}
