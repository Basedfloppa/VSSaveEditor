using Avalonia.Controls;
using VSSaveEditor.Components.Tabs;

namespace VSSaveEditor;

/// <summary>
///     The bottom drawer: the tabs live in <c>Components/Tabs</c> - their layout and the wiring of their own
///     controls stay there, while the window subscribes to the tabs' action events.
///     The tab parts (fields, lists, labels) whose state the window reads and writes are addressed
///     by the aliases below: the coupling to the tabs is gathered in one place, and the window logic reads as
///     it did before the extraction - without paths like <c>ChunkDataView.ChunkXBox</c> in every handler.
/// </summary>
public partial class MainWindow
{
    // ---- Chunks ----
    private StackPanel ChunkDataPanel => ChunkDataView.ChunkDataPanel;
    private ComboBox ChunkPick => ChunkDataView.ChunkPick;
    private TextBox ChunkSearchBox => ChunkDataView.ChunkSearchBox;
    private TextBlock ChunkSummaryLabel => ChunkDataView.ChunkSummaryLabel;
    private TextBox ChunkXBox => ChunkDataView.ChunkXBox;
    private TextBox ChunkZBox => ChunkDataView.ChunkZBox;
    private TextBox HexEditBox => ChunkDataView.HexEditBox;
    private Button SaveCopyBtn => ChunkDataView.SaveCopyBtn;

    // ---- Entities ----
    private ListBox EntityList => EntitiesView.EntityList;
    private StackPanel EntityDetailPanel => EntitiesView.EntityDetailPanel;
    private WrapPanel EntityChipsPanel => EntitiesView.EntityChipsPanel;
    private TextBox EntityFilterBox => EntitiesView.EntityFilterBox;
    private ComboBox EntityCategoryPick => EntitiesView.EntityCategoryPick;
    private TextBlock EntitySummaryLabel => EntitiesView.EntitySummaryLabel;

    // ---- Items ----
    private ListBox ItemList => ItemsView.ItemList;
    private StackPanel ItemDetailPanel => ItemsView.ItemDetailPanel;
    private TextBox ItemFilterBox => ItemsView.ItemFilterBox;
    private ComboBox ItemSourcePick => ItemsView.ItemSourcePick;
    private TextBlock ItemSummaryLabel => ItemsView.ItemSummaryLabel;

    // ---- Selection ----
    private Button DeleteChunksBtn => SelectionView.DeleteChunksBtn;

    // ---- Players ----
    private TextBlock PlayerCountLabel => PlayersView.PlayerCountLabel;
    private StackPanel PlayerDetailPanel => PlayersView.PlayerDetailPanel;
    private TextBlock PlayerEmptyHint => PlayersView.PlayerEmptyHint;
    private TextBox PlayerFilterBox => PlayersView.PlayerFilterBox;
    private ListBox PlayerList => PlayersView.PlayerList;
    private CheckBox PlayerPendingOnlyCheck => PlayersView.PlayerPendingOnlyCheck;

    // ---- Find blocks ----
    private Button FilterBuildBtn => FindBlocksView.FilterBuildBtn;
    private CheckBox FilterDimCheck => FindBlocksView.FilterDimCheck;
    private StackPanel FilterListPanel => FindBlocksView.FilterListPanel;
    private TextBox FilterSearchBox => FindBlocksView.FilterSearchBox;
    private Button RemoveBlocksBtn => FindBlocksView.RemoveBlocksBtn;
    private Button ReplaceBlocksBtn => FindBlocksView.ReplaceBlocksBtn;

    // ---- Settings ----
    private CheckBox ChunkGridCheck => SettingsView.ChunkGridCheck;
    private ComboBox ExportFilterBox => SettingsView.ExportFilterBox;
    private ComboBox ExportFormatBox => SettingsView.ExportFormatBox;
    private ComboBox ExportRegionBox => SettingsView.ExportRegionBox;
    private TextBlock ExportRegionLabel => SettingsView.ExportRegionLabel;
    private NumericUpDown ExportScaleBox => SettingsView.ExportScaleBox;
    private CheckBox ExportTransparentBgCheck => SettingsView.ExportTransparentBgCheck;
    private CheckBox HoverTooltipCheck => SettingsView.HoverTooltipCheck;
    private NumericUpDown JpegQualityBox => SettingsView.JpegQualityBox;
    private TextBlock LayerLabel => SettingsView.LayerLabel;
    private Slider LayerSlider => SettingsView.LayerSlider;
    private TextBlock LodStatusText => SettingsView.LodStatusText;

    // ---- World ----
    private TextBlock MetaText => WorldView.MetaText;

    /// <summary>
    ///     The window's subscription to the tab actions. The tabs are created by the XAML loader and know nothing
    ///     about the save, the scan or the map: they raise an event, and the behaviour stays in the window.
    /// </summary>
    private void WireTabComponents()
    {
        ChunkDataView.SearchRequested += OnChunkSearch;
        ChunkDataView.ClearRequested += OnChunkSearchClear;
        ChunkDataView.GoChunkRequested += OnGoChunk;
        ChunkDataView.ChunkPicked += OnChunkPicked;
        ChunkDataView.ApplyHexRequested += OnApplyHex;
        ChunkDataView.SaveRequested += OnSaveEditsToCopy;

        EntitiesView.FilterChanged += OnEntityFilterChanged;
        EntitiesView.EntityPicked += OnEntityPicked;
        EntitiesView.ClearRequested += OnEntityClearFilter;
        EntitiesView.CopyListRequested += OnCopyEntityList;
        EntitiesView.CopyDumpRequested += OnCopyScanDump;

        ItemsView.FilterChanged += OnItemFilterChanged;
        ItemsView.ItemPicked += OnItemPicked;
        ItemsView.ClearRequested += OnItemClearFilter;
        ItemsView.CopyListRequested += OnCopyItemList;
        ItemsView.CurrentChunkRequested += OnItemsInCurrentChunk;

        SelectionView.DeleteRequested += OnDeleteChunks;
        SelectionView.ClearSelectionRequested += OnClearSelection;

        PlayersView.FilterChanged += OnPlayerFilterChanged;
        PlayersView.PendingOnlyChanged += OnPlayerFilterChanged;
        PlayersView.PlayerPicked += OnPlayerPicked;
        PlayersView.ReviewRequested += OnReviewAndCommit;

        FindBlocksView.SearchChanged += OnFilterSearchChanged;
        FindBlocksView.ClearRequested += OnFilterClear;
        FindBlocksView.RemoveRequested += OnRemoveBlocks;
        FindBlocksView.ReplaceRequested += OnReplaceBlocks;
        FindBlocksView.BuildRequested += OnFilterBuild;
        FindBlocksView.PrevRequested += OnFilterPrevMatch;
        FindBlocksView.NextRequested += OnFilterNextMatch;
        FindBlocksView.ExportImageRequested += OnExportMapImage;
        FindBlocksView.DimChanged += OnFilterDimChanged;

        SettingsView.RenderModeSelected += OnRenderModeSelected;
        SettingsView.LodSelected += OnLodSelected;
        SettingsView.ChunkGridChanged += OnChunkGridChanged;
        SettingsView.HoverTooltipChanged += OnHoverTooltipChanged;
        SettingsView.ResetLayerRequested += OnResetLayer;
        SettingsView.LayerChanged += OnLayer;
        SettingsView.ExportRegionChanged += OnExportRegionChanged;
        SettingsView.SettingsChanged += (_, _) => SaveSettings();
        SettingsView.ClearSelectionRequested += OnClearSelection;

        // Initial map state: before the tabs were extracted the XAML loader applied it itself - the events
        // of RadioButton/CheckBox with IsChecked="True" arrived when the controls were created. Now the event
        // arrives only on a change (applying the saved settings raises it only when the radio
        // actually switches, and writing the settings is suppressed at that moment by the _settingsLoading flag).
        // Without these calls the layer label would stay empty until the first mode switch, and the state
        // would depend on TerrainView defaults.
        ApplyRenderMode();
        Map.SetLodMode(_lodIndex);
        Map.SetShowChunkGrid(ChunkGridCheck.IsChecked == true);
        Map.SetFilterDim(FilterDimCheck.IsChecked == true);
        UpdateLayerLabel();
    }
}
