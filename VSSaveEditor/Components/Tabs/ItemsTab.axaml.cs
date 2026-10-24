using Avalonia.Controls;
using Avalonia.Interactivity;
using VSCore;

namespace VSSaveEditor.Components.Tabs;

/// <summary>
///     The "Items" tab: layout and wiring of its own controls. Behaviour (scan, map,
///     jump to the owner) stays with the window through <see cref="IItemsTabHost"/>.
/// </summary>
public partial class ItemsTab : UserControl
{
    public ItemsTab()
    {
        InitializeComponent();
        ItemFilterBox.TextChanged += (_, _) => FilterChanged?.Invoke(this, EventArgs.Empty);
        ItemSourcePick.SelectionChanged += (_, _) => FilterChanged?.Invoke(this, EventArgs.Empty);
        ItemClearBtn.Click += (_, e) => ClearRequested?.Invoke(this, e);
        ItemChunkBtn.Click += (_, e) => CurrentChunkRequested?.Invoke(this, e);
        CopyItemsBtn.Click += (_, e) => CopyListRequested?.Invoke(this, e);
        ItemList.SelectionChanged += (_, e) => ItemPicked?.Invoke(this, e);
    }

    public event EventHandler? FilterChanged;
    public event EventHandler<RoutedEventArgs>? ClearRequested;
    public event EventHandler<RoutedEventArgs>? CurrentChunkRequested;
    public event EventHandler<RoutedEventArgs>? CopyListRequested;
    public event EventHandler<SelectionChangedEventArgs>? ItemPicked;

    public string FilterText => ItemFilterBox.Text ?? "";

    /// <summary>The item order matches <see cref="InventorySourceFilter"/>.</summary>
    public InventorySourceFilter SourceFilter => (InventorySourceFilter)Math.Clamp(ItemSourcePick.SelectedIndex, 0, 3);
}
