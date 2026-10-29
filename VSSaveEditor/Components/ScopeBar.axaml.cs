using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace VSSaveEditor.Components;

/// <summary>
///     The "Search in:" row of a tab: the current scope, a box for a chunk column or a rectangle, the proposals
///     (Every area, This chunk on the map, The selection) and the way back to the whole save.
///     The bar knows neither the save nor the map: it raises <see cref="ScopeRequested" /> with the text that was
///     typed or picked, and the window resolves it into a <see cref="VSCore.SearchScope" /> — the areas and
///     the map selection only exist there.
/// </summary>
public partial class ScopeBar : UserControl
{
    public ScopeBar()
    {
        InitializeComponent();
        ScopeBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var text = (ScopeBox.Text ?? "").Trim();
            if (text.Length > 0) ScopeRequested?.Invoke(this, text);
        };
        ScopeClearBtn.Click += (_, _) => ScopeRequested?.Invoke(this, "");
    }

    /// <summary>The requested scope: "cx,cz", "cx1,cz1,cx2,cz2", "area=N", "unplaced"; empty means "the whole save".</summary>
    public event EventHandler<string>? ScopeRequested;

    /// <summary>
    ///     Shows the active scope. <paramref name="boxText" /> writes the box back — the scope that was picked from the
    ///     map or a selection ends up there, so the tab tells which rectangle is being searched — while null leaves
    ///     whatever the user is typing alone (a background refresh must not overwrite a half-typed range).
    /// </summary>
    public void ShowState(string label, string? boxText, IReadOnlyList<(string Command, string Caption)> proposals)
    {
        ScopeLabel.Text = label;
        if (boxText is not null && ScopeBox.Text != boxText) ScopeBox.Text = boxText;

        ScopeButtons.Children.Clear();
        foreach (var (command, caption) in proposals)
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
            ScopeButtons.Children.Add(button);
        }
    }

    /// <summary>The line as the status bar words it: what the tab searches now.</summary>
    public string Describe() => ScopeLabel.Text ?? "";
}
