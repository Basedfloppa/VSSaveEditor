using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace VSSaveEditor.Components;

/// <summary>
/// Replacement block picker for the "Find blocks" tab. The list comes from the BlockIDs of the open save,
/// so only an id the game knows can be written; air (id 0) is available too - it is
/// the equivalent of removal, but with an explicit label.
/// </summary>
public sealed class BlockReplaceDialog
{
    public sealed record Choice(string Code, int Id)
    {
        public override string ToString() => Id == 0 ? "air (id 0)" : $"{Code}   [id {Id}]";
    }

    private readonly List<Choice> _all;
    private readonly ListBox _list = new() { Height = 320, MinHeight = 120 };
    private readonly TextBox _search = new() { Watermark = "filter: stone, ore, mod:domain…" };
    private readonly TextBlock _count = new() { Foreground = Brushes.Gray };
    private readonly Button _ok = new() { Content = "Replace", Padding = new Thickness(12, 3), IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Cancel", Padding = new Thickness(12, 3) };
    private Choice? _result;

    private BlockReplaceDialog(List<Choice> all) => _all = all;

    public static async Task<Choice?> ShowAsync(Window owner, IReadOnlyList<Choice> choices,
        string sourceSummary)
    {
        var dlg = new BlockReplaceDialog(choices.ToList());
        return await dlg.RunAsync(owner, sourceSummary);
    }

    private async Task<Choice?> RunAsync(Window owner, string sourceSummary)
    {
        var win = new Window
        {
            Title = "Replace blocks",
            Width = 560,
            Height = 560,
            MinWidth = 420,
            MinHeight = 360,
            CanResize = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        win.Content = Build(sourceSummary);
        _search.TextChanged += (_, _) => Filter();
        _list.SelectionChanged += (_, _) => _ok.IsEnabled = _list.SelectedItem is Choice;
        _ok.Click += (_, _) =>
        {
            _result = _list.SelectedItem as Choice;
            win.Close();
        };
        _cancel.Click += (_, _) => win.Close();
        win.Opened += (_, _) => _search.Focus();

        Filter();
        await win.ShowDialog(owner);
        return _result;
    }

    private Control Build(string sourceSummary)
    {
        var root = new DockPanel();

        var header = new StackPanel { Spacing = 4, Margin = new Thickness(12, 12, 12, 6) };
        header.Children.Add(new TextBlock
        {
            Text = "Replace the ticked block codes with another block",
            FontWeight = FontWeight.Bold,
            FontSize = 15
        });
        header.Children.Add(new TextBlock { Text = sourceSummary, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new TextBlock
        {
            Text = "Only palette entries change; the rest of every chunk stays byte-identical.",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap
        });
        header.Children.Add(_search);
        header.Children.Add(_count);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 6, 12, 12)
        };
        footer.Children.Add(_ok);
        footer.Children.Add(_cancel);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        root.Children.Add(new ScrollViewer
        {
            Margin = new Thickness(12, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _list
        });
        return root;
    }

    private void Filter()
    {
        var q = (_search.Text ?? "").Trim();
        var matched = q.Length == 0
            ? _all
            : _all.Where(c => c.Code.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        const int max = 600;
        _list.ItemsSource = matched.Take(max).ToList();
        _count.Text = matched.Count == 0
            ? "No block matches that filter."
            : matched.Count > max
                ? $"{matched.Count:N0} matches — showing first {max}; refine the filter"
                : $"{matched.Count:N0} block(s)";
        _ok.IsEnabled = _list.SelectedItem is Choice;

        // If the filter leaves exactly one option, select it - a common case of typing the exact code.
        if (matched.Count == 1) _list.SelectedIndex = 0;
    }
}
