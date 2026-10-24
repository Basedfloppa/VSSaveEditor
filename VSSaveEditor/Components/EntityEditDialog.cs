using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using VSSaveEditor.Models;

namespace VSSaveEditor.Components;

/// <summary>
///     Entity field editor over the dump - in a separate window. It cannot live inside the card:
///     the card sits in a virtualized ListBox, and expanding three dozen rows inside a list
///     item threw the scrolling off - the ListBox recomputed its heights and the card fell out of view.
/// </summary>
public sealed class EntityEditDialog
{
    private readonly Button _applyBtn = new() { Content = "Apply", Padding = new Thickness(10, 3) };
    private readonly Button _cancelBtn = new() { Content = "Cancel", Padding = new Thickness(10, 3) };
    private readonly List<(EntityEditRow Row, TextBox Box)> _boxes = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly EntityCardModel _model;
    private TextBox? _firstBox;
    private TextBox? _rawBox;

    private EntityEditDialog(EntityCardModel model)
    {
        _model = model;
    }

    /// <summary>true when the edit went into pending, false when it was cancelled or the window was closed.</summary>
    public static async Task<bool> ShowAsync(Window owner, EntityCardModel model)
    {
        return await new EntityEditDialog(model).RunAsync(owner);
    }

    private async Task<bool> RunAsync(Window owner)
    {
        var win = new Window
        {
            Title = _model.EditTitle,
            Width = 620,
            Height = 640,
            MinWidth = 460,
            MinHeight = 320,
            CanResize = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        win.Content = BuildContent();

        var applied = false;
        _cancelBtn.Click += (_, _) => win.Close();
        _applyBtn.Click += async (_, _) =>
        {
            SyncTexts();
            if (!await _model.ApplyAsync())
            {
                Render(); // the reason is already on the model - show it right here
                return;
            }

            applied = true;
            win.Close();
        };

        win.Opened += (_, _) => _firstBox?.Focus();
        await win.ShowDialog(owner);
        return applied;
    }

    private Control BuildContent()
    {
        var root = new DockPanel();

        var header = new StackPanel { Spacing = 2, Margin = new Thickness(12, 12, 12, 6) };
        header.Children.Add(new TextBlock { Text = _model.EditTitle, FontWeight = FontWeight.Bold, FontSize = 15 });
        header.Children.Add(new TextBlock
        {
            Text = $"{_model.Title} · {_model.Coordinates} · {_model.Size}", TextWrapping = TextWrapping.Wrap
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new StackPanel { Spacing = 4, Margin = new Thickness(12, 4, 12, 12) };
        footer.Children.Add(_hint);
        footer.Children.Add(_error);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0)
        };
        buttons.Children.Add(_applyBtn);
        buttons.Children.Add(_cancelBtn);
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var rows = new StackPanel { Spacing = 3, Margin = new Thickness(12, 0) };
        foreach (var row in _model.Rows) rows.Children.Add(BuildRow(row));
        if (rows.Children.Count == 0)
            rows.Children.Add(new TextBlock { Text = "No editable fields in this entity." });

        // Escape hatch: fields the parser did not decode (opaque/raw mod trees) can only be
        // fixed at the payload level. Deliberately kept apart from the normal rows and does not clash with them.
        rows.Children.Add(new TextBlock
        {
            Text = "Raw payload (hex) — advanced, use only for bytes that are not listed above",
            Margin = new Thickness(0, 10, 0, 2),
            Foreground = Brushes.Gray
        });
        _rawBox = new TextBox
        {
            Text = _model.RawHex,
            AcceptsReturn = true,
            Height = 82,
            FontFamily = UiStyle.Mono,
            FontSize = UiStyle.MonoFontSize,
            TextWrapping = TextWrapping.Wrap
        };
        rows.Children.Add(_rawBox);
        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        Render();
        return root;
    }

    private Control BuildRow(EntityEditRow row)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var label = new TextBlock
        {
            Text = row.Label,
            Width = UiStyle.FieldLabelWidth,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = UiStyle.Mono,
            FontSize = UiStyle.MonoFontSize,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTip.SetTip(label, row.Label);
        line.Children.Add(label);

        var box = new TextBox
        {
            Text = row.Text,
            Width = UiStyle.FieldBoxWidth,
            MinHeight = 0,
            Padding = new Thickness(2),
            FontFamily = UiStyle.Mono,
            FontSize = UiStyle.MonoFontSize
        };
        // No binding and no TextChanged: TextBox does not raise the event for a programmatic Text set,
        // so the dialog keeps the row-to-box pairs itself and copies the texts before Apply.
        _boxes.Add((row, box));
        _firstBox ??= box;

        line.Children.Add(box);
        return line;
    }

    /// <summary>Copies what is in the boxes now into the model rows - Apply reads those.</summary>
    private void SyncTexts()
    {
        foreach (var (row, box) in _boxes) row.Text = box.Text ?? "";
        if (_rawBox is not null) _model.RawHex = _rawBox.Text ?? "";
    }

    private void Render()
    {
        _hint.Text = _model.EditHint;
        _hint.IsVisible = _model.HasEditHint;
        _error.Text = _model.EditError;
        _error.IsVisible = _model.HasEditError;
    }
}
