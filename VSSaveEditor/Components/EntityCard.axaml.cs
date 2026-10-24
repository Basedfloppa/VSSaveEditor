using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VSCore;
using VSSaveEditor.Models;

namespace VSSaveEditor.Components;

/// <summary>One entity card; presentation and editing live in <see cref="EntityCardModel"/>.</summary>
public partial class EntityCard : UserControl
{
    public EntityCard() => InitializeComponent();

    /// <summary>DataContext is <see cref="EntityRow"/> (set by the record list). The view model is
    /// assembled here because XAML cannot bind to the record: its coordinates are fields, not properties.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Root.DataContext = DataContext is EntityRow row
            ? new EntityCardModel(row, () => TopLevel.GetTopLevel(this) as IEntityEditHost)
            : null;
        EnsureLoaded();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The window (edit host) is only reachable from the tree - load the record here so the card
        // shows the real position right away, not just the chunk coordinates.
        EnsureLoaded();
    }

    /// <summary>Re-reads the entity from the session - after an edit is cancelled or re-applied.</summary>
    public void Reload() => Model?.Reload();

    private EntityCardModel? Model => Root.DataContext as EntityCardModel;

    private void EnsureLoaded() => Model?.EnsureLoaded();

    private async void OnEdit(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!model.BeginEdit()) return; // nothing to edit - the card itself shows why
        if (!await EntityEditDialog.ShowAsync(owner, model)) model.CancelEdit();
    }
}
