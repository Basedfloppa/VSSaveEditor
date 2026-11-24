using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using VSCore;

namespace VSSaveEditor;

/// <summary>
///     Card primitives for the data panel. They used to be private methods of the window and
///     served only the "Chunks" tab; the block list in "Find blocks" duplicated the same
///     layout with string literals. It is now a shared layer for both places.
/// </summary>
internal static class CardUi
{
    /// <summary>
    ///     A theme brush (ThemeDictionaries Light/Dark from App.Resources). The window does not see these
    ///     resources - TryGetResource searches only its own scope - so the second step asks the application;
    ///     XAML layout resolves them the same way.
    /// </summary>
    public static bool TryThemeBrush(Control owner, string key, out IBrush brush)
    {
        var theme = owner.ActualThemeVariant;
        if (owner.TryGetResource(key, theme, out var own) && own is IBrush fromOwner)
        {
            brush = fromOwner;
            return true;
        }

        if (Application.Current is { } app
            && ((IResourceHost)app).TryGetResource(key, theme, out var global) && global is IBrush fromApp)
        {
            brush = fromApp;
            return true;
        }

        brush = Brushes.Transparent;
        return false;
    }

    /// <summary>
    ///     A theme brush bound with DynamicResource. A value taken at build time freezes:
    ///     a legend built on a light fill stayed white on the dark theme and the text on it disappeared.
    /// </summary>
    public static void BindTheme(Control owner, AvaloniaObject target, AvaloniaProperty property, string key,
        bool fill = false)
    {
        if (TryThemeBrush(owner, key, out _))
        {
            target.Bind(property, new DynamicResourceExtension(key));
            return;
        }

        // The key is missing - a resource defect, not the norm: log to debug output and take a safe
        // value (transparent for fills - otherwise the dark theme would show a white blot).
        Debug.WriteLine($"[CardUi] theme brush '{key}' not found ({owner.ActualThemeVariant})");
        target.SetValue(property, fill ? Brushes.Transparent : (owner as TemplatedControl)?.Foreground ?? Brushes.Gray);
    }

    public static void AddHeader(StackPanel sp, string text)
    {
        sp.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 4, 0, 0)
        });
    }

    public static void AddLine(Control owner, StackPanel sp, string text, bool isMono = false, bool bold = false,
        bool gray = false)
    {
        var line = new TextBlock
        {
            Text = text,
            FontFamily = isMono ? UiStyle.Mono : FontFamily.Default,
            FontSize = UiStyle.MonoFontSize,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            TextWrapping = TextWrapping.Wrap
        };
        BindTheme(owner, line, TextBlock.ForegroundProperty, gray ? "TextSecondaryBrush" : "TextPrimaryBrush");
        sp.Children.Add(line);
    }

    public static Border WrapCard(Control owner, StackPanel inner)
    {
        var card = new Border
        {
            Child = inner,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(UiStyle.CardPadding),
            Margin = new Thickness(0, UiStyle.CardMarginY)
        };
        BindTheme(owner, card, Border.BorderBrushProperty, "CardBorderBrush", true);
        return card;
    }

    /// <summary>
    ///     A field edit row: a fixed-width label plus a value box. One implementation
    ///     for entity/BlockEntity/player inventory cards - it used to be three copies with the same numbers.
    /// </summary>
    public static TextBox AddEditRow(StackPanel card, ChunkField f)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var label = new TextBlock
        {
            Text = f.DisplayPath,
            Width = UiStyle.FieldLabelWidth,
            FontFamily = UiStyle.Mono,
            FontSize = UiStyle.MonoFontSize,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(label, f.DisplayPath);
        row.Children.Add(label);
        var box = new TextBox
        {
            Text = f.EditText,
            Width = UiStyle.FieldBoxWidth,
            FontSize = UiStyle.MonoFontSize,
            FontFamily = UiStyle.Mono,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        row.Children.Add(box);
        card.Children.Add(row);
        return box;
    }

    public static string HexLines(byte[] b, int rows)
    {
        var sb = new StringBuilder();
        var take = Math.Min(rows * 16, b.Length);
        for (var i = 0; i < take; i += 16)
        {
            var n = Math.Min(16, b.Length - i);
            for (var j = 0; j < n; j++) sb.Append(b[i + j].ToString("x2")).Append(' ');
            sb.AppendLine();
        }

        if (take < b.Length) sb.Append($"… {b.Length - take} more bytes");
        return sb.ToString();
    }

    /// <summary>A wrapped explanation line of a dialog. The same three lines were written in every dialog.</summary>
    public static TextBlock Wrap(Control? owner, string text, bool bold = false)
    {
        var t = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal
        };
        if (owner is not null) BindTheme(owner, t, TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return t;
    }

    /// <summary>A wrapped warning line: bold, in the accent colour.</summary>
    public static TextBlock Warn(Control? owner, string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Bold };
        if (owner is not null) BindTheme(owner, t, TextBlock.ForegroundProperty, "TextAccentBrush");
        return t;
    }

    /// <summary>A byte count as the dialogs print it.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes / (1024.0 * 1024.0):0.0} MB";
    }

    /// <summary>What one tree row of the player-data window shows. The ECX tree (character, inventories) and the
    /// protobuf tree (mod keys) differ only in these values — the row itself is one implementation.</summary>
    public sealed class TreeRow
    {
        public required string Label { get; init; }
        public string? Tip { get; init; }

        /// <summary>Tooltip of the label alone when it says more than the action's one (the full field path).</summary>
        public string? LabelTip { get; init; }

        public int Indent { get; init; }
        public double LabelWidth { get; init; } = UiStyle.FieldLabelWidth;
        public double BoxWidth { get; init; } = UiStyle.FieldBoxWidth;
        public string ValueText { get; init; } = "";
        public string Hint { get; init; } = "";
        public bool Removed { get; init; }

        /// <summary>Editable value: the text the box starts with (null — the row shows the value read-only).</summary>
        public string? EditableText { get; init; }

        /// <summary>Flips the model's "removed" flag and returns the new state; null — the row cannot be deleted.</summary>
        public Func<bool>? ToggleRemoved { get; init; }

        /// <summary>Called when the box text changes and after a delete/undo, so the dialog can recount.</summary>
        public Action? OnChanged { get; init; }

        /// <summary>Already-built children; non-null — the row becomes an expander. The caller fills it by
        /// recursing into this method, so a tree is built from its root outwards.</summary>
        public StackPanel? Children { get; init; }

        public bool Expand { get; init; }

        /// <summary>Extra controls drawn after the value box (an item's code and count, which live in the
        /// stack header rather than in the tree).</summary>
        public Control? Extra { get; init; }

        /// <summary>Receives the hint TextBlock once the row is built, so a caller can keep the row's summary in
        /// step with a value it edits through <see cref="Extra" />.</summary>
        public Action<TextBlock>? HintSink { get; init; }
    }

    /// <summary>
    ///     One row of a data tree: the delete/undo button, the label, the value box (or the read-only value) and
    ///     a small grey hint. Returns the box, when the row has one. The two trees of the player window drew this
    ///     twice with different widths, and the chunks cards a third time.
    /// </summary>
    public static Control AddTreeRow(Control? owner, TreeRow row, out TextBox? box)
    {
        box = null;
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(row.Indent, 0, 0, 0),
            Opacity = row.Removed ? 0.45 : 1
        };

        var mark = new Button
        {
            Content = row.Removed ? "undo" : "✕",
            Padding = new Thickness(4, 0),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsVisible = row.ToggleRemoved is not null
        };
        if (row.Tip is { Length: > 0 }) ToolTip.SetTip(mark, row.Tip);
        strip.Children.Add(mark);

        var label = new TextBlock
        {
            Text = row.Label,
            Width = row.LabelWidth,
            FontFamily = UiStyle.Mono,
            FontSize = UiStyle.MonoFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextDecorations = row.Removed ? TextDecorations.Strikethrough : null
        };
        ToolTip.SetTip(label, row.LabelTip ?? row.Tip ?? row.Label);
        strip.Children.Add(label);

        mark.Click += (_, _) =>
        {
            if (row.ToggleRemoved is null) return;
            var removed = row.ToggleRemoved();
            mark.Content = removed ? "undo" : "✕";
            strip.Opacity = removed ? 0.45 : 1;
            label.TextDecorations = removed ? TextDecorations.Strikethrough : null;
            row.OnChanged?.Invoke();
        };

        if (row.EditableText is not null)
        {
            box = new TextBox
            {
                Text = row.EditableText,
                Width = row.BoxWidth,
                FontFamily = UiStyle.Mono,
                FontSize = UiStyle.MonoFontSize,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            if (row.OnChanged is not null) box.TextChanged += (_, _) => row.OnChanged();
            strip.Children.Add(box);
        }
        else if (row.Children is null)
        {
            strip.Children.Add(new TextBlock
            {
                Text = row.ValueText,
                FontFamily = UiStyle.Mono,
                FontSize = UiStyle.MonoFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray
            });
        }

        if (row.Extra is not null) strip.Children.Add(row.Extra);

        if (row.Hint.Length > 0)
        {
            var hint = new TextBlock
            {
                Text = row.Hint,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray
            };
            strip.Children.Add(hint);
            row.HintSink?.Invoke(hint);
        }

        if (row.Children is null) return strip;

        return new Expander
        {
            Header = strip,
            Content = row.Children,
            Padding = new Thickness(0),
            IsExpanded = row.Expand,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }
    /// <summary>
    ///     One field of a record: the delete/undo row with its own value box, and the children under it when the
    ///     field is a tree. The player window and the entity window draw exactly this; they differed only in the
    ///     label (a nested key, or the full path where the same key repeats inside a mod value), the delete
    ///     tooltip and where the box gets registered, so those three are the parameters.
    /// </summary>
    public static Control AddFieldRow(Control? owner, ChunkField f, int depth, string fieldTip, bool pathLabel,
        Action? onChanged, Action<ChunkField, TextBox>? onBox)
    {
        var childrenShown = f.Children.Where(c => !c.IsHeaderNoise).ToList();
        var isTree = f.Type == ChunkFieldType.Tree && childrenShown.Count > 0 && !f.IsRawTree;
        StackPanel? children = null;
        if (isTree)
        {
            children = new StackPanel { Spacing = 1, Margin = new Thickness(8, 0, 0, 0) };
            foreach (var child in childrenShown)
                children.Children.Add(AddFieldRow(owner, child, depth + 1, fieldTip, pathLabel, onChanged, onBox));
        }

        var row = new TreeRow
        {
            // A nested row already sits inside its parent, so a full path there repeats four ancestors per line;
            // the full path stays in the label tooltip.
            Label = pathLabel && f.Path.Length > 0 ? f.Path : f.Key,
            Tip = isTree ? "Delete this whole subtree" : fieldTip,
            LabelTip = $"{f.DisplayPath} ({f.Type})",
            Indent = depth * 10,
            LabelWidth = 250,
            BoxWidth = 180,
            Removed = f.Removed,
            ToggleRemoved = () => f.Removed = !f.Removed,
            OnChanged = onChanged,
            EditableText = f.IsEditable ? f.EditText : null,
            ValueText = f.ValueText,
            Hint = f.IsEditable ? f.Type.ToString() : isTree ? $"{childrenShown.Count} field(s)" : "",
            Children = children,
            Expand = depth == 0 && childrenShown.Count <= 8
        };

        var control = AddTreeRow(owner, row, out var box);
        // Every box of the subtree, not just the root's: a value two levels down is edited like any other
        if (box is not null) onBox?.Invoke(f, box);
        return control;
    }

    /// <summary>
    ///     Compact display of the card fields: the first N rows plus a lazily expanded list of the rest.
    ///     It used to print ALL fields: a panel with 36 block entities grew to ~14 700 px and built thousands of controls.
    /// </summary>
    public static void AddFieldsPreview(Control owner, StackPanel card, List<ChunkField> parsed)
    {
        const int preview = 3; // the rest is on click: the card does not inflate the panel
        foreach (var f in parsed.Take(preview))
            AddLine(owner, card, $"   {f.DisplayPath} = {f.ValueText}", true);
        if (parsed.Count <= preview) return;

        var exp = new Expander
        {
            Header = $"show fields ({parsed.Count})",
            IsExpanded = false,
            Margin = new Thickness(0, 2, 0, 0)
        };
        exp.Expanded += (_, _) =>
        {
            if (exp.Content is not null) return; // build once, and only when actually asked for
            var all = new StackPanel { Spacing = 1 };
            foreach (var f in parsed.Skip(preview))
                AddLine(owner, all, $"   {f.DisplayPath} = {f.ValueText}", true);
            exp.Content = all;
        };
        card.Children.Add(exp);
    }

    /// <summary>
    ///     Renders the card section in portions: pageSize first, the rest on a button.
    ///     This bounds both the panel height and the number of controls built when a chunk is clicked.
    /// </summary>
    public static void RenderCardsPaged(StackPanel parent, int total, int pageSize, Action<StackPanel, int> renderOne,
        string what = "cards")
    {
        var shown = Math.Min(pageSize, total);
        for (var i = 0; i < shown; i++) renderOne(parent, i);
        if (total <= shown) return;

        var rest = new StackPanel { Spacing = 2 };
        var btn = new Button
        {
            Content = $"show {total - shown} more {what}",
            Padding = new Thickness(8, 2),
            Margin = new Thickness(0, 4, 0, 0)
        };
        btn.Click += (_, _) =>
        {
            rest.Children.Clear(); // the button is replaced by the content
            for (var i = shown; i < total; i++) renderOne(rest, i);
        };
        rest.Children.Add(btn);
        parent.Children.Add(rest);
    }
}