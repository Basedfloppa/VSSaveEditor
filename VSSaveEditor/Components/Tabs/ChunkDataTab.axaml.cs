using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VSSaveEditor.Components.Tabs;

/// <summary>
///     The "Chunks" tab of the bottom drawer: chunk search, picking from the results, the hex fallback
///     and the card panel of the selected chunk. The tab owns its own layout and control wiring and only
///     raises action events: the window decides what to do with the selected chunk - it holds the save,
///     the scan and the map.
/// </summary>
public partial class ChunkDataTab : UserControl
{
    public ChunkDataTab()
    {
        InitializeComponent();
        ChunkSearchBtn.Click += (_, e) => SearchRequested?.Invoke(this, e);
        ChunkClearBtn.Click += (_, e) => ClearRequested?.Invoke(this, e);
        // Enter in the search box is the same command as the button: that is the field's own convention, not window logic
        ChunkSearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) SearchRequested?.Invoke(this, new RoutedEventArgs());
        };
        GoChunkBtn.Click += (_, e) => GoChunkRequested?.Invoke(this, e);
        ChunkPick.SelectionChanged += (_, e) => ChunkPicked?.Invoke(this, e);
        ApplyHexBtn.Click += (_, e) => ApplyHexRequested?.Invoke(this, e);
        SaveCopyBtn.Click += (_, e) => SaveRequested?.Invoke(this, e);
        // the scope row only reports, the window decides what a chunk/rectangle/area means for the save
        Scope.ScopeRequested += (_, text) => ScopeRequested?.Invoke(this, text);
    }

    public event EventHandler<RoutedEventArgs>? SearchRequested;
    public event EventHandler<RoutedEventArgs>? ClearRequested;
    public event EventHandler<RoutedEventArgs>? GoChunkRequested;
    public event EventHandler<SelectionChangedEventArgs>? ChunkPicked;
    public event EventHandler<RoutedEventArgs>? ApplyHexRequested;
    public event EventHandler<RoutedEventArgs>? SaveRequested;

    /// <summary>The scope requested by the "Search in:" row: a column, a rectangle, an area; empty means the whole save.</summary>
    public event EventHandler<string>? ScopeRequested;
}
