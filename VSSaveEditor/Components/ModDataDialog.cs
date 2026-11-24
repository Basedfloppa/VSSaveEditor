using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using VSCore;
using VSSaveEditor.Models;

namespace VSSaveEditor.Components;

/// <summary>What the user asked to remove: the rebuilt gamedata blob and the entries that were cut out of it.</summary>
public sealed record ModDataRemovalRequest(
    string ModId,
    IReadOnlyList<ModDataEntry> Removed,
    byte[] NewGamedata,
    bool IncludesWorldData)
{
    public long BytesFreed => Removed.Sum(e => (long)e.Value.Length);
}

/// <summary>
///     "Mod data" editor: the world message (`gamedata`) keeps a dictionary of free-form keys, and mods store
///     their per-player state there as `&lt;modid&gt;_&lt;key&gt;_&lt;playeruid&gt;`. The dialog lists those keys by mod,
///     shows which player each one belongs to, and cuts the chosen ones out to a pending edit — nothing is
///     written until Review &amp; apply, and the edit is undoable like any other.
///     It is a separate dialog rather than a card in the Players list because one action always concerns
///     every player of the save: the player card is the wrong place to say "do this for all of them".
/// </summary>
public sealed class ModDataDialog
{
    private const int MaxKeyRows = 300;
    private readonly StackPanel _detail = new() { Spacing = 3, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBox _filter = new() { Watermark = "filter: mod, key, player…" };
    private readonly ListBox _list = new() { MinWidth = 260 };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap };

    private readonly List<PlayerIdentity> _players;
    private readonly byte[] _gamedata;
    private readonly Button _removeBtn = new()
        { Content = "Remove…", Padding = new Thickness(12, 3), IsEnabled = false };

    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };

    private readonly List<ModDataEntry> _entries;
    private readonly List<ModDataGroup> _groups;
    private List<ModDataGroup> _shown = new();

    // Scope of the removal for the selected group: per-player keys only, or every key of the mod.
    private bool _includeWorldData;
    private RadioButton? _playerScope;
    private RadioButton? _allScope;
    private ModDataRemovalRequest? _result;
    private Window? _window;

    private ModDataDialog(byte[] gamedata, IReadOnlyList<PlayerIdentity> players)
    {
        _gamedata = gamedata;
        _players = players.ToList();
        var uids = new HashSet<string>(_players.Select(p => p.Uid), StringComparer.Ordinal);
        _entries = ModDataStore.Read(gamedata, uids);
        _groups = ModDataStore.Group(_entries);
    }

    /// <summary>
    ///     Shows the dialog over the CURRENT gamedata (the caller passes the pending blob when one exists,
    ///     otherwise the row from the working copy). Returns null when nothing was chosen.
    /// </summary>
    public static async Task<ModDataRemovalRequest?> ShowAsync(Window owner, byte[] gamedata,
        IReadOnlyList<PlayerIdentity> players)
    {
        var dlg = new ModDataDialog(gamedata, players);
        return await dlg.RunAsync(owner);
    }

    private async Task<ModDataRemovalRequest?> RunAsync(Window owner)
    {
        var shell = DialogShell.Create("Mod data", "Mod data saved in this world", 1020, 680, "360,5,*",
            _removeBtn, _list, _detail);
        _window = shell.Window;

        shell.Header.Children.Add(CardUi.Wrap(_window,
            "Mods keep their state in the world message (`gamedata`), per player or per world. "
            + "A per-player key ends with the player uid — usually `modid_something_<uid>`. "
            + "Removing those keys clears what the mod remembers about every player; the player characters "
            + "themselves (inventories in `playerdata`) are not touched."));
        shell.Header.Children.Add(CardUi.Wrap(_window,
            "Nothing is written here: the removal becomes a PENDING edit. Check it in Review & apply… → "
            + "Apply to working copy (undoable), and only then Save a copy….", true));
        shell.Header.Children.Add(_summary);

        var toolbar = new StackPanel
            { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        toolbar.Children.Add(new TextBlock { Text = "Filter:", VerticalAlignment = VerticalAlignment.Center });
        _filter.Width = 300;
        toolbar.Children.Add(_filter);
        shell.Header.Children.Add(toolbar);
        shell.Bottom.Children.Add(_note);

        _filter.TextChanged += (_, _) => Filter();
        _list.SelectionChanged += (_, _) => RenderDetail();
        _removeBtn.Click += (_, _) => Remove();
        shell.Window.Opened += (_, _) => _filter.Focus();

        Filter();
        await shell.Window.ShowDialog(owner);
        return _result;
    }

    /// <summary>Filtered mod list: the mod id or any of its keys (a uid typed in finds the keys of that player).</summary>
    private void Filter()
    {
        var q = (_filter.Text ?? "").Trim();
        _shown = q.Length == 0
            ? _groups.ToList()
            : _groups.Where(g => g.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || g.Entries.Any(e => e.Key.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .ToList();

        var keep = _list.SelectedIndex;
        _list.ItemsSource = _shown.Select(Describe).ToList();
        _list.SelectedIndex = _shown.Count == 0 ? -1 : Math.Clamp(keep, 0, _shown.Count - 1);

        var withPlayers = _groups.Count(g => g.PlayerEntries.Count > 0);
        _summary.Text = _entries.Count == 0
            ? "This world has no mod data at all (the gamedata row is empty or unreadable)."
            : $"{_entries.Count} key(s) in {_groups.Count} group(s); {withPlayers} group(s) hold per-player data. "
              + $"{_players.Count} player(s) known in this save.";
        RenderDetail();
    }

    /// <summary>One list row. Kept short on purpose: the ListBox draws single lines, and the full numbers are in
    /// the detail panel anyway.</summary>
    private static string Describe(ModDataGroup g)
    {
        var perPlayer = g.PlayerEntries.Count;
        var text = g.Id;
        // the player count is in the detail panel: a list row that is clipped tells nothing
        if (perPlayer > 0) text += $" · {perPlayer} keys";
        if (g.WorldEntries.Count > 0) text += $" · {g.WorldEntries.Count} world";
        return text + $" · {CardUi.FormatBytes(g.Bytes)}";
    }

    private void RenderDetail()
    {
        _detail.Children.Clear();
        _playerScope = null;
        _allScope = null;
        var g = SelectedGroup();
        if (g is null)
        {
            _removeBtn.IsEnabled = false;
            _removeBtn.Content = "Remove…";
            _detail.Children.Add(CardUi.Wrap(_window, _groups.Count == 0
                ? "This world has no mod data keys — nothing to remove."
                : "No mod data matches the filter."));
            return;
        }

        _detail.Children.Add(new TextBlock
        {
            Text = $"mod '{g.Id}' — {g.Entries.Count} key(s), {CardUi.FormatBytes(g.Bytes)}",
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        });

        var playerEntries = g.PlayerEntries;
        var worldEntries = g.WorldEntries;
        if (playerEntries.Count > 0)
            _detail.Children.Add(CardUi.Wrap(_window, 
                $"{playerEntries.Count} key(s) name a player directly ({g.PlayerCount} player(s), "
                + $"{CardUi.FormatBytes(playerEntries.Sum(e => (long)e.Value.Length))})."));
        if (worldEntries.Count > 0)
            _detail.Children.Add(CardUi.Wrap(_window, 
                $"{worldEntries.Count} key(s) belong to the world itself ({CardUi.FormatBytes(worldEntries.Sum(e => (long)e.Value.Length))}); "
                + "they are shared by everyone (religions, sites, config…)."));
        if (g.WorldKeysMentioningPlayers > 0)
            _detail.Children.Add(CardUi.Warn(_window, 
                $"{g.WorldKeysMentioningPlayers} of the world key(s) contain a player uid inside the value: "
                + "one key holds data for several players at once, and removing it erases that data for all of them."));

        // ---- scope ----
        var scopeBox = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 2) };
        scopeBox.Children.Add(new TextBlock { Text = "Remove:", FontWeight = FontWeight.Bold });
        var canPlayer = playerEntries.Count > 0;
        _playerScope = new RadioButton
        {
            GroupName = "moddata-scope",
            IsChecked = canPlayer,
            IsEnabled = canPlayer,
            Content = canPlayer
                ? $"the per-player keys only — {playerEntries.Count} key(s), {CardUi.FormatBytes(playerEntries.Sum(e => (long)e.Value.Length))}"
                : "the per-player keys only — there are none"
        };
        // The handler must read the state: unchecking a radio raises the event too, and the two radios then
        // fight over one flag in whatever order the toggle happens to uncheck its sibling.
        _playerScope.IsCheckedChanged += (_, _) =>
        {
            if (_playerScope is { IsChecked: true }) SetScope(includeWorld: false);
        };
        scopeBox.Children.Add(_playerScope);

        _allScope = new RadioButton
        {
            GroupName = "moddata-scope",
            IsChecked = !canPlayer,
            IsEnabled = worldEntries.Count > 0 || !canPlayer,
            Content = $"every key of this mod — {g.Entries.Count} key(s), {CardUi.FormatBytes(g.Bytes)}, including the world data"
        };
        _allScope.IsCheckedChanged += (_, _) =>
        {
            if (_allScope is { IsChecked: true }) SetScope(includeWorld: true);
        };
        scopeBox.Children.Add(_allScope);
        _detail.Children.Add(scopeBox);
        SetScope(includeWorld: !canPlayer);

        // ---- keys ----
        _detail.Children.Add(new TextBlock
        {
            Text = "Keys (player keys first):", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 0)
        });
        foreach (var e in g.Entries.OrderBy(e => e.PlayerUid is null).Take(MaxKeyRows))
        {
            var owner = e.PlayerUid is null
                ? e.MentionsPlayer ? "world (mentions a player)" : "world"
                : PlayerLabel(e.PlayerUid);
            _detail.Children.Add(new TextBlock
            {
                Text = $"{e.Key}   [{CardUi.FormatBytes(e.Value.Length)}]   → {owner}",
                FontFamily = UiStyle.Mono,
                FontSize = UiStyle.MonoFontSize,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (g.Entries.Count > MaxKeyRows)
            _detail.Children.Add(CardUi.Wrap(_window, $"… and {g.Entries.Count - MaxKeyRows} more key(s)."));
    }

    private void SetScope(bool includeWorld)
    {
        _includeWorldData = includeWorld;
        var g = SelectedGroup();
        if (g is null) return;
        var entries = includeWorld ? g.Entries : g.PlayerEntries;
        var can = entries.Count > 0;
        _removeBtn.IsEnabled = can;
        _removeBtn.Content = can ? $"Remove {entries.Count} key(s)…" : "Nothing to remove";
        _note.Text = can
            ? $"Creates ONE pending edit of the world message: {entries.Count} key(s) of '{g.Id}', "
              + $"{CardUi.FormatBytes(entries.Sum(e => (long)e.Value.Length))}. "
              + (includeWorld ? "The world-level keys of this mod go too. " : "")
              + "Review & apply… shows every removed key before anything is written."
            : "This mod has no key in the chosen scope.";
    }

    private void Remove()
    {
        var g = SelectedGroup();
        if (g is null) return;
        var entries = _includeWorldData ? g.Entries : g.PlayerEntries;
        var removal = ModDataStore.Remove(_gamedata, entries);
        if (!removal.Changed)
        {
            _note.Text = "Nothing matched — the blob was left unchanged.";
            return;
        }

        _result = new ModDataRemovalRequest(g.Id, removal.Removed, removal.Blob, _includeWorldData);
        _window?.Close();
    }

    private ModDataGroup? SelectedGroup()
        => _list.SelectedIndex >= 0 && _list.SelectedIndex < _shown.Count ? _shown[_list.SelectedIndex] : null;

    private string PlayerLabel(string uid)
    {
        var p = _players.FirstOrDefault(x => x.Uid == uid);
        return p?.Name is { Length: > 0 } name ? $"{name} ({uid})" : uid;
    }

}
