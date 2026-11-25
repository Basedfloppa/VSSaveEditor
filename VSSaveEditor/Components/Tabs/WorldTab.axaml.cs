using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VSCore;

namespace VSSaveEditor.Components.Tabs;

/// <summary>
///     The "World" tab: metadata of the open save (version, seed, size, world time) and the world tree it
///     carries. Layout and the wiring of its own controls live here; the window decides what to fill in.
///     The value card is shown only while a setting is selected — the list keeps the width otherwise.
/// </summary>
public partial class WorldTab : UserControl
{
    /// <summary>Width of the value card when it opens; the splitter lets the user change it.</summary>
    private const double CardWidth = 420;

    /// <summary>The summary line of the save metadata, without the disclosure marker.</summary>
    private string _infoLine = "World info";

    /// <summary>The count line ("177 setting(s) in 63 225 B") and what to do while nothing is selected.</summary>
    private string _head = "";
    private string _hint = "";
    private bool _detailOpen;

    public WorldTab()
    {
        InitializeComponent();
        WorldEditBtn.Click += (_, _) => EditRequested?.Invoke(this, EventArgs.Empty);
        // Double-click is the second way into the same dialog: a row is the thing a user points at, and the
        // card's button is only reachable after the row was selected anyway.
        WorldList.DoubleTapped += (_, e) =>
        {
            if (WorldList.SelectedItem is not WorldSetting) return;
            e.Handled = true;
            EditRequested?.Invoke(this, EventArgs.Empty);
        };
        WorldFilterBox.TextChanged += (_, _) =>
        {
            UpdateClearButton();
            FilterChanged?.Invoke(this, EventArgs.Empty);
        };
        WorldFilterBox.KeyDown += OnFilterKeyDown;
        WorldClearBtn.Click += (_, e) => ClearRequested?.Invoke(this, e);
        CopyWorldBtn.Click += (_, e) => CopyListRequested?.Invoke(this, e);
        WorldCopyValueBtn.Click += (_, e) => CopyValueRequested?.Invoke(this, e);
        WorldInfoToggle.IsCheckedChanged += (_, _) => UpdateInfoToggle();
        WorldList.SelectionChanged += (_, e) => SettingPicked?.Invoke(this, e);
        UpdateClearButton();
        UpdateInfoToggle();
    }

    public event EventHandler? FilterChanged;
    public event EventHandler<RoutedEventArgs>? ClearRequested;
    public event EventHandler<RoutedEventArgs>? CopyListRequested;
    public event EventHandler<RoutedEventArgs>? CopyValueRequested;

    /// <summary>The value is to be edited (the card's "Edit…" or a double-click on a row).</summary>
    public event EventHandler? EditRequested;
    public event EventHandler<SelectionChangedEventArgs>? SettingPicked;

    public string FilterText => WorldFilterBox.Text ?? "";

    /// <summary>Clears the filter box (the "✕" button and Esc); the window refreshes the list on the TextChanged it raises.</summary>
    public void ClearFilter() => WorldFilterBox.Text = "";

    /// <summary>
    ///     The one status line above the list: the count (or the reason there is none) plus, while nothing is
    ///     selected, what to do next. Both go into the same line so that picking a row changes text, not height.
    /// </summary>
    public void SetStatus(string head, string hint)
    {
        _head = head;
        _hint = hint;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var text = _detailOpen || _hint.Length == 0 ? _head : _head + " · " + _hint;
        WorldStatusLabel.Text = text;
        ToolTip.SetTip(WorldStatusLabel, text);
    }

    /// <summary>The save metadata: one line while closed, the full block (tables, relief, gamedata) when opened.</summary>
    public void SetInfo(string header, string details)
    {
        _infoLine = header;
        MetaText.Text = details;
        UpdateInfoToggle();
    }

    /// <summary>The marker carries the state as well as the chevron: &#9656; closed, &#9662; open.</summary>
    private void UpdateInfoToggle()
    {
        var open = WorldInfoToggle.IsChecked == true;
        MetaText.IsVisible = open;
        WorldInfoHeader.Text = (open ? "\u25BE " : "\u25B8 ") + _infoLine;
    }

    /// <summary>
    ///     Opens or closes the value card. Closed means zero width and no splitter: the list uses the whole row,
    ///     which is what the drawer height is short of.
    /// </summary>
    public void ShowDetail(bool show)
    {
        _detailOpen = show;
        WorldDetailCard.IsVisible = show;
        WorldSplitter.IsVisible = show;
        if (WorldBody.ColumnDefinitions.Count >= 3)
            WorldBody.ColumnDefinitions[2].Width = show ? new GridLength(CardWidth) : new GridLength(0);
        // The status line also says what the next click does: once the card is open that is not needed.
        UpdateStatus();
    }

    /// <summary>What the card shows: the setting name, its type/size line and the value in full.</summary>
    public void ShowSetting(WorldSetting setting)
    {
        WorldDetailName.Text = setting.Name;
        var pending = setting.IsEdited ? " · pending edit" : "";
        WorldDetailMeta.Text = (setting.IsLong
            ? $"{setting.KindName} · {setting.ValueBytes:n0} B · cut in the list, the box has all {setting.Value.Length:n0} chars"
            : $"{setting.KindName} · {setting.ValueBytes:n0} B") + pending;
        WorldValueBox.Text = setting.Value;
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || FilterText.Length == 0) return;
        e.Handled = true;
        ClearFilter();
    }

    /// <summary>A "✕" that exists only while there is something to clear — an always-on Clear button is noise.</summary>
    private void UpdateClearButton() => WorldClearBtn.IsVisible = FilterText.Length > 0;

    public ListBox List => WorldList;
    public TextBox ValueBox => WorldValueBox;
    public TextBlock StatusLabel => WorldStatusLabel;
    public TextBlock InfoHeader => WorldInfoHeader;
    public Border DetailCard => WorldDetailCard;
    public Button EditButton => WorldEditBtn;

}
