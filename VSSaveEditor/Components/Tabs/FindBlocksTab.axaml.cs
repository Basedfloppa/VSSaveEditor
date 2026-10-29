using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VSSaveEditor.Components.Tabs;

/// <summary>
///     The "Find blocks" tab: the blocks of the open save with checkboxes, plus actions on the selected ones.
///     The tab hands the row list (panel) to the window, which fills it in PopulateFilterList.
/// </summary>
public partial class FindBlocksTab : UserControl
{
    public FindBlocksTab()
    {
        InitializeComponent();
        Scope.ScopeRequested += (_, text) => ScopeRequested?.Invoke(this, text);
        FilterSearchBox.TextChanged += (_, e) => SearchChanged?.Invoke(this, e);
        FilterClearBtn.Click += (_, e) => ClearRequested?.Invoke(this, e);
        RemoveBlocksBtn.Click += (_, e) => RemoveRequested?.Invoke(this, e);
        ReplaceBlocksBtn.Click += (_, e) => ReplaceRequested?.Invoke(this, e);
        FilterBuildBtn.Click += (_, e) => BuildRequested?.Invoke(this, e);
        FilterPrevBtn.Click += (_, e) => PrevRequested?.Invoke(this, e);
        FilterNextBtn.Click += (_, e) => NextRequested?.Invoke(this, e);
        FilterExportBtn.Click += (_, e) => ExportImageRequested?.Invoke(this, e);
        FilterDimCheck.IsCheckedChanged += (_, e) => DimChanged?.Invoke(this, e);
    }

    public event EventHandler<TextChangedEventArgs>? SearchChanged;
    public event EventHandler<RoutedEventArgs>? ClearRequested;
    public event EventHandler<RoutedEventArgs>? RemoveRequested;
    public event EventHandler<RoutedEventArgs>? ReplaceRequested;
    public event EventHandler<RoutedEventArgs>? BuildRequested;
    public event EventHandler<RoutedEventArgs>? PrevRequested;
    public event EventHandler<RoutedEventArgs>? NextRequested;
    public event EventHandler<RoutedEventArgs>? ExportImageRequested;
    public event EventHandler<RoutedEventArgs>? DimChanged;

    /// <summary>The scope requested by the "Search in:" row: a column, a rectangle, an area; empty means the whole save.</summary>
    public event EventHandler<string>? ScopeRequested;
}
