using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

namespace VSSaveEditor.Components;

/// <summary>One entry of the area list: a button of the strip.</summary>
public sealed record AreaChip(string Caption, string Tip, string Command, bool IsSelected);

/// <summary>
///     The area list of the scope row: the chips to offer, and the commands of the two list-wide buttons.
///     Everything is a command string the window resolves ("areas=1,4"), so the bar stays as dumb as it was
///     with the proposals: it knows neither the save nor the map.
/// </summary>
public sealed record AreaListInfo(IReadOnlyList<AreaChip> Chips, string AllCommand, string ClearCommand);

/// <summary>
///     The "Search in:" row of a tab: the current scope, a box for a chunk column or a rectangle, the proposals
///     (This chunk on the map, The selection), the area list of "Find blocks" and the way back to the whole save.
///     The bar knows neither the save nor the map: it raises <see cref="ScopeRequested" /> with the text that was
///     typed or picked, and the window resolves it into a <see cref="VSCore.SearchScope" /> — the areas and
///     the map selection only exist there.
/// </summary>
public partial class ScopeBar : UserControl
{
    /// <summary>How far one click of ‹ › moves the strip: a viewport minus one chip, so no chip is skipped
    /// over. With a fixed step of one chip a save with twelve areas would need eleven clicks.</summary>
    private const double PageOverlap = 30;

    private bool _layoutPending;

    public ScopeBar()
    {
        InitializeComponent();
        ScopeBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var text = (ScopeBox.Text ?? "").Trim();
            if (text.Length > 0) ScopeRequested?.Invoke(this, text);
        };
        ScopeClearBtn.Click += (_, _) => ScopeRequested?.Invoke(this, _resetCommand);
        AreaPrevBtn.Click += (_, _) => Page(-1);
        AreaNextBtn.Click += (_, _) => Page(1);
        AreaClearBtn.Click += (_, _) => ScopeRequested?.Invoke(this, _clearCommand);
        // the strip can be scrolled by wheel/keys as well; the arrows have to follow it
        AreaScroll.ScrollChanged += (_, _) => UpdateArrows();
    }

    private string _resetCommand = "";
    private string _clearCommand = "";

    /// <summary>The requested scope: "cx,cz", "cx1,cz1,cx2,cz2", "area=N", "areas=1,3", "unplaced";
    /// empty means "the whole save".</summary>
    public event EventHandler<string>? ScopeRequested;

    /// <summary>
    ///     Shows the active scope. <paramref name="boxText" /> writes the box back — the scope that was picked from the
    ///     map or a selection ends up there, so the tab tells which rectangle is being searched — while null leaves
    ///     whatever the user is typing alone (a background refresh must not overwrite a half-typed range).
    ///     <paramref name="areas" /> is null on every tab that has no area list; the reset button is worded
    ///     by the caller because "All" means "all available areas" where the areas are listed and
    ///     "Whole save" everywhere else.
    /// </summary>
    public void ShowState(string label, string? boxText, IReadOnlyList<(string Command, string Caption)> proposals,
        AreaListInfo? areas, string resetCaption)
    {
        ScopeLabel.Text = label;
        if (boxText is not null && ScopeBox.Text != boxText) ScopeBox.Text = boxText;
        ScopeClearBtn.Content = resetCaption;
        _resetCommand = areas is { Chips.Count: > 0 } ? areas.AllCommand : "";
        _clearCommand = areas?.ClearCommand ?? "";

        ScopeButtons.Children.Clear();
        foreach (var (command, caption) in proposals)
            ScopeButtons.Children.Add(MakeButton(caption, command));

        ShowAreas(areas);
    }

    private Button MakeButton(string caption, string command)
    {
        var button = new Button
        {
            Content = caption,
            Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = UiStyle.MonoFontSize
        };
        ToolTip.SetTip(button, command);
        button.Click += (_, _) => ScopeRequested?.Invoke(this, command);
        return button;
    }

    private void ShowAreas(AreaListInfo? areas)
    {
        var chips = areas?.Chips ?? Array.Empty<AreaChip>();
        AreaStrip.Children.Clear();
        foreach (var chip in chips)
        {
            var button = MakeButton(chip.Caption, chip.Command);
            // A ticked area must be visible without reading the tooltip: it is the only state of the list that
            // changes the scope. The "accent" class is the theme's own accent fill, so the chip matches the
            // window in both light and dark themes instead of hard-coding a colour here.
            if (chip.IsSelected) button.Classes.Add("accent");
            ToolTip.SetTip(button, chip.Tip);
            AreaStrip.Children.Add(button);
        }

        AreaGroup.IsVisible = chips.Count > 0;
        if (chips.Count == 0) return;
        // the offsets of the previous list (another save, another selection) mean nothing in this one
        AreaScroll.Offset = new Vector(0, 0);
        if (_layoutPending) return;
        _layoutPending = true;
        // the extent is only known after the new chips are measured
        Dispatcher.UIThread.Post(() =>
        {
            _layoutPending = false;
            UpdateArrows();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Moves the strip by one page and keeps the arrows in step with its ends.</summary>
    private void Page(int direction)
    {
        var viewport = AreaScroll.Viewport.Width;
        var extent = AreaScroll.Extent.Width;
        var step = Math.Max(PageOverlap, viewport - PageOverlap);
        var offset = Math.Clamp(AreaScroll.Offset.X + direction * step, 0, Math.Max(0, extent - viewport));
        AreaScroll.Offset = new Vector(offset, 0);
        UpdateArrows();
    }

    private void UpdateArrows()
    {
        var viewport = AreaScroll.Viewport.Width;
        var extent = AreaScroll.Extent.Width;
        var offset = AreaScroll.Offset.X;
        AreaPrevBtn.IsEnabled = offset > 0.5 && extent > viewport + 0.5;
        AreaNextBtn.IsEnabled = extent > viewport + 0.5 && offset < extent - viewport - 0.5;
    }

    /// <summary>The line as the status bar words it: what the tab searches now.</summary>
    public string Describe() => ScopeLabel.Text ?? "";
}
