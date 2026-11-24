using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using VSSaveEditor.Models;

namespace VSSaveEditor;

/// <summary>
///     The frame of the data dialogs: the title line, the list/detail split with a splitter, the note lines
///     above the footer and the footer with the primary action. It used to be copied into every dialog
///     (~70 lines each) with small differences in widths and margins; the dialogs now only fill in their own
///     content and the frame stays the same in all of them.
/// </summary>
internal sealed class DialogShell
{
    private DialogShell(Button action) => Action = action;

    public Window Window { get; private set; } = null!;

    /// <summary>Rows of the header. The first line is the bold title added by the shell.</summary>
    public StackPanel Header { get; } = new() { Spacing = 4, Margin = new Thickness(12, 12, 12, 6) };

    /// <summary>Note/status lines between the split view and the footer.</summary>
    public StackPanel Bottom { get; } = new() { Spacing = 2, Margin = new Thickness(12, 0, 12, 0) };

    /// <summary>The primary action: the shell places it in the footer, before "Close".</summary>
    public Button Action { get; }

    /// <summary>
    ///     Builds the frame. The list and the detail panel belong to the caller (they hold its state); the shell
    ///     only places them, so the splitter and the scrolling behave the same everywhere.
    /// </summary>
    /// <param name="listColumns">Column widths of the split: the list, the splitter, the detail panel.</param>
    public static DialogShell Create(string title, string headerLine, double width, double height,
        string listColumns, Button action, ListBox list, StackPanel detail)
    {
        var shell = new DialogShell(action);
        var win = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            MinWidth = 760,
            MinHeight = 440,
            CanResize = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        shell.Window = win;

        var root = new DockPanel();

        shell.Header.Children.Add(new TextBlock
        {
            Text = headerLine,
            FontWeight = FontWeight.Bold,
            FontSize = 15
        });
        DockPanel.SetDock(shell.Header, Dock.Top);
        root.Children.Add(shell.Header);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 6, 12, 12)
        };
        action.Classes.Add("accent");
        var close = new Button { Content = "Close", Padding = new Thickness(12, 3) };
        close.Click += (_, _) => win.Close();
        footer.Children.Add(action);
        footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        DockPanel.SetDock(shell.Bottom, Dock.Bottom);
        root.Children.Add(shell.Bottom);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(listColumns), Margin = new Thickness(12, 0) };
        Grid.SetColumn(list, 0);
        grid.Children.Add(list);
        var splitter = new GridSplitter
        {
            Width = 5,
            Background = Brushes.Transparent,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = detail
        };
        Grid.SetColumn(scroll, 2);
        grid.Children.Add(scroll);
        root.Children.Add(grid);

        win.Content = root;
        return shell;
    }

    /// <summary>The players of the save as list rows: the name (or the uid) and the id the row is addressed by.</summary>
    public static string DescribePlayer(PlayerIdentity p)
        => (p.Name is { Length: > 0 } n ? n : p.Uid) + $" — id {p.Id}";
}
