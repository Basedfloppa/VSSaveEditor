using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace VSSaveEditor.Components.Tabs;

/// <summary>
///     The "Settings" tab: render mode, LOD, grid, Y layer and map export options.
///     The tab owns the radio groups itself (<see cref="SelectRenderMode" /> / <see cref="SelectLod" />),
///     so the window never has to reach into its panels; persisting the values still belongs to the window.
/// </summary>
public partial class SettingsTab : UserControl
{
    public SettingsTab()
    {
        InitializeComponent();
        foreach (var child in SettingsRenderPanel.Children)
            if (child is RadioButton rb)
                rb.IsCheckedChanged += (_, _) =>
                {
                    // The tab parses the Tag itself: the window need not reach into its panels or know about RadioButton
                    if (rb.IsChecked == true && TagOf(rb) is { } mode) RenderModeSelected?.Invoke(mode);
                };
        foreach (var child in SettingsLodPanel.Children)
            if (child is RadioButton rb)
                rb.IsCheckedChanged += (_, _) =>
                {
                    if (rb.IsChecked == true && TagOf(rb) is { } lod) LodSelected?.Invoke(lod);
                };
        ChunkGridCheck.IsCheckedChanged += (_, e) => ChunkGridChanged?.Invoke(this, e);
        HoverTooltipCheck.IsCheckedChanged += (_, e) => HoverTooltipChanged?.Invoke(this, e);
        SurfaceBtn.Click += (_, e) => ResetLayerRequested?.Invoke(this, e);
        LayerSlider.ValueChanged += (_, e) => LayerChanged?.Invoke(this, e);
        ClearSelectionBtn.Click += (_, e) => ClearSelectionRequested?.Invoke(this, e);
        // Everything below only affects the persisted export settings - one "settings changed" event covers it
        ExportScaleBox.ValueChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        ExportFormatBox.SelectionChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        JpegQualityBox.ValueChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        ExportFilterBox.SelectionChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        ExportTransparentBgCheck.IsCheckedChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        // "Export area" does more than change a setting: the label next to it explains what will go into the file
        ExportRegionBox.SelectionChanged += (_, e) => ExportRegionChanged?.Invoke(this, e);
    }

    /// <summary>A render mode was selected - the radio <c>Tag</c> value (0 heightmap, 1 blocks, 2 ocean depth).</summary>
    public event Action<int>? RenderModeSelected;

    /// <summary>An LOD was selected - the radio <c>Tag</c> value (0 auto, 1…5 fixed).</summary>
    public event Action<int>? LodSelected;
    public event EventHandler<RoutedEventArgs>? ChunkGridChanged;
    public event EventHandler<RoutedEventArgs>? HoverTooltipChanged;
    public event EventHandler<RoutedEventArgs>? ResetLayerRequested;
    public event EventHandler<RangeBaseValueChangedEventArgs>? LayerChanged;
    public event EventHandler<SelectionChangedEventArgs>? ExportRegionChanged;
    public event EventHandler? SettingsChanged;
    public event EventHandler<RoutedEventArgs>? ClearSelectionRequested;

    /// <summary>Checks the radio with the given <c>Tag</c> in the "Render mode" group.</summary>
    public void SelectRenderMode(int mode)
    {
        SelectRadio(SettingsRenderPanel, mode);
    }

    /// <summary>Checks the radio with the given <c>Tag</c> in the "LOD" group.</summary>
    public void SelectLod(int index)
    {
        SelectRadio(SettingsLodPanel, index);
    }

    private static int? TagOf(RadioButton rb) => int.TryParse(rb.Tag as string, out var v) ? v : null;

    private static void SelectRadio(StackPanel panel, int value)
    {
        foreach (var child in panel.Children)
            if (child is RadioButton rb && int.TryParse(rb.Tag as string, out var v) && v == value)
            {
                rb.IsChecked = true;
                return;
            }
    }
}
