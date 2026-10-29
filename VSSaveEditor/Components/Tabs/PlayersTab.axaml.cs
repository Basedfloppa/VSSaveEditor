using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VSSaveEditor.Components.Tabs;

/// <summary>
///     The "Players" tab: the playerdata list with a filter and a detail panel. The panel contents
///     are drawn by PlayersTabService - the tab gives it the controls and raises selection events.
/// </summary>
public partial class PlayersTab : UserControl
{
    public PlayersTab()
    {
        InitializeComponent();
        Scope.ScopeRequested += (_, text) => ScopeRequested?.Invoke(this, text);
        PlayerFilterBox.TextChanged += (_, e) => FilterChanged?.Invoke(this, e);
        PlayerPendingOnlyCheck.IsCheckedChanged += (_, e) => PendingOnlyChanged?.Invoke(this, e);
        PlayerList.SelectionChanged += (_, e) => PlayerPicked?.Invoke(this, e);
        ReviewBtn.Click += (_, e) => ReviewRequested?.Invoke(this, e);
    }

    public event EventHandler<TextChangedEventArgs>? FilterChanged;
    public event EventHandler<RoutedEventArgs>? PendingOnlyChanged;
    public event EventHandler<SelectionChangedEventArgs>? PlayerPicked;
    public event EventHandler<RoutedEventArgs>? ReviewRequested;

    /// <summary>The scope requested by the "Search in:" row: a column, a rectangle, an area; empty means the whole save.</summary>
    public event EventHandler<string>? ScopeRequested;
}
