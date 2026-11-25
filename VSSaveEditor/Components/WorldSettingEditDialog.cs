using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VSCore;

namespace VSSaveEditor.Components;

/// <summary>
///     Editor of ONE world-tree entry (`gameMode`, `daysPerMonth`, a mod's config blob …). Separate window, like the
///     entity editor: the row lives in a virtualized list, and growing the card into a form would push the list
///     around under the pointer.
///     What it decides on its own: whether the typed text is a value of this type. The window above it decides what
///     that means for the save (pending edit, one undo step), so this dialog knows nothing about files.
/// </summary>
public sealed class WorldSettingEditDialog
{
    /// <summary>Values longer than this get a multi-line box: a JSON blob answers to a different editing gesture.</summary>
    private const int MultilineChars = 200;

    private readonly Button _action = new() { Content = "Add to pending edits", Padding = new Thickness(10, 3) };
    private readonly WorldSetting _setting;
    private readonly bool _creationOnly;
    private TextBox? _box;
    private ComboBox? _boolPick;
    private readonly TextBlock _error = new()
    {
        Name = "WorldEditError", TextWrapping = TextWrapping.Wrap, IsVisible = false, Foreground = Brushes.IndianRed
    };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };

    private WorldSettingEditDialog(WorldSetting setting, bool creationOnly)
    {
        _setting = setting;
        _creationOnly = creationOnly;
    }

    /// <summary>The new value in the form the save stores it, or null when the dialog was cancelled or closed.</summary>
    public static async Task<string?> ShowAsync(Window owner, WorldSetting setting)
    {
        return await new WorldSettingEditDialog(setting, WorldSettingNames.IsCreationOnly(setting.Name)).RunAsync(owner);
    }

    private async Task<string?> RunAsync(Window owner)
    {
        var multiline = _setting.Kind == WorldSettingKind.String && _setting.Value.Length > MultilineChars;
        var win = new Window
        {
            Title = $"Edit '{_setting.Name}'",
            Width = 560,
            // A short value must not open an empty field of grey: the window takes the height it needs, while a
            // multi-line blob gets a fixed box with its own scroll.
            SizeToContent = multiline ? SizeToContent.Manual : SizeToContent.Height,
            Height = multiline ? 520 : double.NaN,
            MinWidth = 420,
            CanResize = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(10, 3) };
        cancel.Click += (_, _) => win.Close();
        win.Content = BuildContent(cancel);

        string? result = null;
        _action.Click += (_, _) =>
        {
            var text = CurrentText();
            if (!WorldSettingEdit.TryNormalize(_setting.Kind, text, out var canonical, out _)) return; // the label says why
            result = canonical;
            win.Close();
        };
        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) win.Close();
            else if (e.Key == Key.Enter && !multiline && _action.IsEnabled && _box is not null)
            {
                e.Handled = true;
                _action.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }
        };

        Validate(); // the box starts with the stored value: the action starts disabled, and says nothing yet
        win.Opened += (_, _) =>
        {
            ((Control?)_box ?? _boolPick)?.Focus();
            if (_box is not null) _box.CaretIndex = _box.Text?.Length ?? 0;
        };
        await win.ShowDialog(owner);
        return result;
    }

    private Control BuildContent(Button cancel)
    {
        var root = new DockPanel();

        var header = new StackPanel { Spacing = 2, Margin = new Thickness(12, 12, 12, 8) };
        header.Children.Add(new TextBlock { Text = _setting.Name, FontWeight = FontWeight.Bold, FontSize = 15 });
        header.Children.Add(new TextBlock
        {
            Text = $"{_setting.KindName} · {(long)_setting.Value.Length:n0} char(s), {_setting.ValueBytes:n0} B in the save"
                   + (_setting.IsEdited ? " · already has a pending edit" : ""),
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var body = new StackPanel { Spacing = 6, Margin = new Thickness(12, 0, 12, 0) };

        if (_setting.Kind == WorldSettingKind.Bool)
        {
            _boolPick = new ComboBox { Name = "WorldEditBoolPick", Width = 160, SelectedIndex = _setting.Value == "true" ? 0 : 1 };
            _boolPick.Items.Add("true");
            _boolPick.Items.Add("false");
            _boolPick.SelectionChanged += (_, _) => Validate();
            body.Children.Add(_boolPick);
        }
        else
        {
            var multiline = _setting.Kind == WorldSettingKind.String && _setting.Value.Length > MultilineChars;
            _box = new TextBox
            {
                Name = "WorldEditBox",
                Text = _setting.Value,
                FontFamily = UiStyle.Mono,
                FontSize = UiStyle.MonoFontSize,
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                Height = multiline ? 300 : double.NaN,
                VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center
            };
            if (multiline) ScrollViewer.SetVerticalScrollBarVisibility(_box, ScrollBarVisibility.Auto);
            _box.TextChanged += (_, _) => Validate();
            body.Children.Add(_box);
        }

        // The one warning that changes what the user expects: the game reads this attribute at world creation.
        if (_creationOnly)
            body.Children.Add(new TextBlock
            {
                Text = "The game reads this value only while a world is created. The new number goes into the save, "
                       + "but an existing world keeps its already generated terrain — the change is for a new world.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DarkOrange
            });

        _hint.Text = Hint();
        body.Children.Add(_hint);
        body.Children.Add(_error);

        // Footer last: it is the DockPanel's filling child only if nothing else follows, so the form body is the
        // child that fills and the buttons keep their height.
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 8, 12, 12)
        };
        _action.Classes.Add("accent");
        footer.Children.Add(_action);
        footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body); // last child of the DockPanel: the form takes the space above the footer
        return root;
    }

    private string Hint()
    {
        var hint = _setting.Kind switch
        {
            WorldSettingKind.Integer => "a whole number, -2 147 483 648 … 2 147 483 647 (stored as int32)",
            WorldSettingKind.Float => "a number with a dot, e.g. 0.5 (stored as float32)",
            WorldSettingKind.Bool => "true or false (stored as one byte)",
            _ => "the text exactly as the save stores it — no quotes are added or removed"
        };
        return hint + " · nothing is written until Review & apply…";
    }

    private string CurrentText() => _boolPick is not null
        ? _boolPick.SelectedItem as string ?? _setting.Value
        : _box?.Text ?? "";

    /// <summary>Live check: the action is enabled only for a valid value that differs from the stored one.</summary>
    private void Validate()
    {
        var text = CurrentText();
        var ok = WorldSettingEdit.TryNormalize(_setting.Kind, text, out var canonical, out var error);
        _error.Text = error ?? "";
        _error.IsVisible = error is not null;
        var unchanged = ok && string.Equals(canonical, _setting.Value, StringComparison.Ordinal);
        _action.IsEnabled = ok && !unchanged;
        ToolTip.SetTip(_action, !ok ? error : unchanged ? "The value is already stored as typed." : null);
    }

    /// <summary>For the probe: what the dialog currently holds and whether its action is available.</summary>
    public TextBox? Box => _box;
    public ComboBox? BoolPick => _boolPick;
    public Button Action => _action;
}
