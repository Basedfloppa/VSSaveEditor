using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VSCore;

namespace VSSaveEditor;

/// <summary>Result of applying: the working copy that received the edits, and the untouched original.</summary>
public sealed record ApplyResult(string WorkingCopy, string Original);

/// <summary>
///     Preview of the pending edits before writing (F3): what exactly changes, in which chunk or
///     inventory, with filters and a text report. The confirmation used to show only the counts of
///     chunks and players, so one had to agree blindly (P1-2).
/// </summary>
public sealed class PendingChangesDialog
{
    /// <summary>
    ///     The window performs the apply (it holds the save and the busy flags); the dialog only
    ///     shows the stages and the result.
    /// </summary>
    public delegate Task<ApplyResult> WriteHandler(IProgress<string> stage);

    private const int MaxRowsShown = 400; // a report of thousands of rows must not be built in full
    private readonly StackPanel _body = new() { Spacing = 4, Margin = new Thickness(10) };
    private readonly Button _closeBtn = MakeButton("Close");
    private readonly Button _copyTextBtn = MakeButton("Copy as text");
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _exportBtn = MakeButton("Export report…");
    private readonly TextBox _filter = new() { Width = 260, Watermark = "filter: field, chunk or value…" };
    private readonly ComboBox _kind = new() { Width = 190 };

    private readonly ProgressBar _progress = new()
        { Height = 6, Width = 200, IsVisible = false, IsIndeterminate = true };

    private readonly DiffReport _report;
    private readonly StackPanel _resultPanel = new() { Spacing = 3, IsVisible = false };
    private readonly TextBlock _stage = new() { IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly WriteHandler? _write;

    private readonly Button _writeBtn = MakeButton("Apply to working copy");
    private bool _busy;

    private Window? _window;

    private PendingChangesDialog(DiffReport report, WriteHandler? write)
    {
        _report = report;
        _write = write;
    }

    /// <summary>Shows the dialog. Returns the write result, or null when the user declined.</summary>
    public static async Task<ApplyResult?> ShowAsync(Window owner, DiffReport report, WriteHandler? write)
    {
        var dlg = new PendingChangesDialog(report, write);
        return await dlg.RunAsync(owner);
    }

    private async Task<ApplyResult?> RunAsync(Window owner)
    {
        var win = new Window
        {
            Title = "Review pending changes",
            Width = 980,
            Height = 640,
            MinWidth = 720,
            MinHeight = 420,
            CanResize = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        _window = win;
        win.Content = BuildContent();
        Render();

        ApplyResult? result = null;
        _closeBtn.Click += (_, _) => win.Close();
        _writeBtn.Click += async (_, _) => result = await WriteAsync();
        _copyTextBtn.Click += async (_, _) => await CopyTextAsync();
        _exportBtn.Click += async (_, _) => await ExportAsync();

        await win.ShowDialog(owner);
        return result;
    }

    private Control BuildContent()
    {
        var root = new DockPanel();

        var header = new StackPanel { Spacing = 6, Margin = new Thickness(12, 12, 12, 6) };
        var title = new TextBlock { Text = "Pending changes", FontWeight = FontWeight.Bold, FontSize = 15 };
        var summary = new TextBlock { Text = _report.Summary, TextWrapping = TextWrapping.Wrap };
        header.Children.Add(title);
        header.Children.Add(summary);

        var filters = new StackPanel
            { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        filters.Children.Add(new TextBlock { Text = "Show:", VerticalAlignment = VerticalAlignment.Center });
        _kind.Items.Add("All changes");
        _kind.Items.Add("Chunk fields");
        _kind.Items.Add("Raw chunk blobs");
        _kind.Items.Add("Players");
        _kind.SelectedIndex = 0; // after filling: before that there is no selected item
        _kind.SelectionChanged += (_, _) => Render();
        filters.Children.Add(_kind);
        filters.Children.Add(new TextBlock { Text = "Filter:", VerticalAlignment = VerticalAlignment.Center });
        _filter.TextChanged += (_, _) => Render();
        filters.Children.Add(_filter);
        header.Children.Add(filters);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new StackPanel { Spacing = 6, Margin = new Thickness(12, 6, 12, 12) };
        var buttons = new StackPanel
            { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_copyTextBtn);
        buttons.Children.Add(_exportBtn);
        buttons.Children.Add(_closeBtn);
        if (_write != null)
        {
            _writeBtn.Classes.Add("accent");
            _writeBtn.IsEnabled = !_report.IsEmpty;
            if (_report.IsEmpty) _writeBtn.Content = "Nothing to apply";
            buttons.Children.Add(_writeBtn);
        }

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        statusRow.Children.Add(_count);
        statusRow.Children.Add(_progress);
        footer.Children.Add(statusRow);
        footer.Children.Add(_stage);
        footer.Children.Add(_resultPanel);
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        root.Children.Add(new ScrollViewer
        {
            Content = _body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        return root;
    }

    private static Button MakeButton(string text)
    {
        return new Button { Content = text, Padding = new Thickness(10, 3) };
    }

    /// <summary>Rebuilds the list for the current filters; with more rows than the limit it shows a slice.</summary>
    private void Render()
    {
        _body.Children.Clear();
        int rows = 0, groups = 0;
        var text = (_filter.Text ?? "").Trim();
        foreach (var g in _report.Groups)
        {
            var matching = g.Rows.Where(r => KindMatches(g, r) && TextMatches(g, r, text)).ToList();
            var titleHit = text.Length > 0 && g.Title.Contains(text, StringComparison.OrdinalIgnoreCase);
            if (matching.Count == 0 && !titleHit) continue;
            groups++;
            _body.Children.Add(BuildGroupCard(g, matching));
            rows += matching.Count;
            if (rows > MaxRowsShown) break;
        }

        if (groups == 0)
            _body.Children.Add(new TextBlock { Text = "No change matches the filter.", Margin = new Thickness(4) });
        _count.Text = _report.Groups.Count == 0
            ? "Nothing pending."
            : $"{groups} of {_report.Groups.Count} location(s), {rows} difference(s) shown. Applied to the working copy — the original save is not modified.";
    }

    private bool KindMatches(DiffGroup g, FieldChange r)
    {
        return _kind.SelectedIndex switch
        {
            1 => !g.IsPlayer && !r.ByteLevel,
            2 => !g.IsPlayer && r.ByteLevel,
            3 => g.IsPlayer,
            _ => true
        };
    }

    private static bool TextMatches(DiffGroup g, FieldChange r, string text)
    {
        if (text.Length == 0) return true;
        return g.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
               || r.Field.Contains(text, StringComparison.OrdinalIgnoreCase)
               || r.OldValue.Contains(text, StringComparison.OrdinalIgnoreCase)
               || r.NewValue.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private Control BuildGroupCard(DiffGroup g, List<FieldChange> rows)
    {
        var inner = new StackPanel { Spacing = 2 };
        var title = new TextBlock
        {
            Text = g.Title
                   + (g.ByteDelta != 0 ? $"   ({g.ByteDelta:+#;-#;0} B)" : "")
                   + (g.Note is { Length: > 0 } ? $"   — {g.Note}" : ""),
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        };
        inner.Children.Add(title);

        var shown = rows.Count > 0 ? rows : g.Rows;
        if (shown.Count == 0) inner.Children.Add(Gray("(no readable differences)"));
        foreach (var r in shown.Take(80))
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*,24,*") };
            grid.Children.Add(Cell(r.Field, 0, true));
            grid.Children.Add(Cell(r.OldValue, 1, true, true));
            grid.Children.Add(Cell("→", 2, false, true));
            grid.Children.Add(Cell(r.NewValue, 3, true));
            inner.Children.Add(grid);
        }

        if (shown.Count > 80) inner.Children.Add(Gray($"… {shown.Count - 80} more row(s) in this location"));

        var card = new Border
        {
            Child = inner,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 2)
        };
        if (_window is not null) CardUi.BindTheme(_window, card, Border.BorderBrushProperty, "CardBorderBrush", true);
        return card;
    }

    private TextBlock Gray(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11 };
        if (_window is not null) CardUi.BindTheme(_window, t, TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return t;
    }

    private TextBlock Cell(string text, int column, bool mono, bool secondary = false)
    {
        var t = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 8, 0),
            FontFamily = mono ? UiStyle.Mono : FontFamily.Default,
            FontSize = UiStyle.MonoFontSize
        };
        Grid.SetColumn(t, column);
        if (_window is not null && secondary)
            CardUi.BindTheme(_window, t, TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return t;
    }

    private async Task<ApplyResult?> WriteAsync()
    {
        if (_write is null || _busy || _window is null) return null;
        _busy = true;
        SetButtonsEnabled(false);
        _progress.IsVisible = true;
        _stage.IsVisible = true;
        _stage.Text = "Materialising changes…";
        var progress = new Progress<string>(s => _stage.Text = s);
        try
        {
            var result = await _write(progress);
            _progress.IsVisible = false;
            _stage.Text = "Done.";
            ShowResult(result);
            SetCloseOnly();
            return result;
        }
        catch (Exception ex)
        {
            _progress.IsVisible = false;
            _stage.Text = "Failed: " + ex.Message + " — nothing was applied to the working copy.";
            SetButtonsEnabled(true);
            return null;
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowResult(ApplyResult result)
    {
        _resultPanel.Children.Clear();
        _resultPanel.Children.Add(new TextBlock
            { Text = "Applied to the working copy.", FontWeight = FontWeight.Bold });
        _resultPanel.Children.Add(new TextBlock
        {
            Text = "Original (never modified): " + result.Original, TextWrapping = TextWrapping.Wrap,
            FontFamily = UiStyle.Mono, FontSize = UiStyle.MonoFontSize
        });
        _resultPanel.Children.Add(Gray(
            "The loaded save now shows these edits. Use File → Save a copy… to write a .vcdbs next to the original "
            + "(the working copy itself is temporary)."));
        // the working copy path - for diagnostics, not as a "save result"
        _resultPanel.Children.Add(Gray("Temporary working copy: " + result.WorkingCopy));
        _resultPanel.IsVisible = true;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _writeBtn.IsEnabled = enabled && !_report.IsEmpty;
        _copyTextBtn.IsEnabled = enabled;
        _exportBtn.IsEnabled = enabled;
    }

    private void SetCloseOnly()
    {
        _writeBtn.IsVisible = false;
        _copyTextBtn.IsVisible = false;
        _exportBtn.IsVisible = false;
    }

    private async Task CopyTextAsync()
    {
        try
        {
            if (_window?.Clipboard is { } cb) await cb.SetTextAsync(_report.ToText());
        }
        catch
        {
            /* the clipboard may be unavailable - the report can still be exported */
        }
    }

    private async Task ExportAsync()
    {
        if (_window is null) return;
        try
        {
            var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export the change report",
                SuggestedFileName = "pending-changes.txt",
                DefaultExtension = "txt",
                FileTypeChoices = new[] { new FilePickerFileType("Text report") { Patterns = new[] { "*.txt" } } }
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;
            await File.WriteAllTextAsync(path, _report.ToText());
            if (_stage is not null)
            {
                _stage.IsVisible = true;
                _stage.Text = "Report saved: " + path;
            }
        }
        catch (Exception ex)
        {
            if (_stage is not null)
            {
                _stage.IsVisible = true;
                _stage.Text = "Export failed: " + ex.Message;
            }
        }
    }
}