using Avalonia.Controls;
using Avalonia.Interactivity;
using VSCore;

namespace VSSaveEditor.Components.Tabs;

/// <summary>
///     The "Entities" tab of the bottom drawer: filter, category, record list, detail panel and
///     copying. Like the other tabs, it owns its layout and control wiring,
///     while behaviour (scan, map, edit session) stays with the window - here there are only action events.
/// </summary>
public partial class EntitiesTab : UserControl
{
    public EntitiesTab()
    {
        InitializeComponent();
        Scope.ScopeRequested += (_, text) => ScopeRequested?.Invoke(this, text);
        EntityFilterBox.TextChanged += (_, _) => FilterChanged?.Invoke(this, EventArgs.Empty);
        EntityCategoryPick.SelectionChanged += (_, _) => FilterChanged?.Invoke(this, EventArgs.Empty);
        EntityClearBtn.Click += (_, e) => ClearRequested?.Invoke(this, e);
        CopyListBtn.Click += (_, e) => CopyListRequested?.Invoke(this, e);
        CopyDumpBtn.Click += (_, e) => CopyDumpRequested?.Invoke(this, e);
        EntityList.SelectionChanged += (_, e) => EntityPicked?.Invoke(this, e);
    }

    /// <summary>The filter text or the category changed - the list has to be rebuilt.</summary>
    public event EventHandler? FilterChanged;

    public event EventHandler<RoutedEventArgs>? ClearRequested;
    public event EventHandler<RoutedEventArgs>? CopyListRequested;
    public event EventHandler<RoutedEventArgs>? CopyDumpRequested;
    public event EventHandler<SelectionChangedEventArgs>? EntityPicked;

    public string FilterText => EntityFilterBox.Text ?? "";

    /// <summary>The combo box items are in the same order as <see cref="EntityCategory" />.</summary>
    public EntityCategory Category => (EntityCategory)Math.Clamp(EntityCategoryPick.SelectedIndex, 0, 2);

    public void SetCategory(EntityCategory category) => EntityCategoryPick.SelectedIndex = (int)category;

    /// <summary>The scope requested by the "Search in:" row: a column, a rectangle, an area; empty means the whole save.</summary>
    public event EventHandler<string>? ScopeRequested;
}
