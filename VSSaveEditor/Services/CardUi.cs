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