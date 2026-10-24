using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VSSaveEditor.Components.Tabs;

/// <summary>The "Selection" tab: what can be done with a chunk region selected on the map.</summary>
public partial class SelectionTab : UserControl
{
    public SelectionTab()
    {
        InitializeComponent();
        DeleteChunksBtn.Click += (_, e) => DeleteRequested?.Invoke(this, e);
        ClearSelectionBtn.Click += (_, e) => ClearSelectionRequested?.Invoke(this, e);
    }

    public event EventHandler<RoutedEventArgs>? DeleteRequested;
    public event EventHandler<RoutedEventArgs>? ClearSelectionRequested;
}
