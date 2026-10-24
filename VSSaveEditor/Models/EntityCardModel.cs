using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using VSCore;

namespace VSSaveEditor.Models;

/// <summary>
///     Card for one record of the "Entities" tab: presentation and editing. The data stays in the scan
///     model (the list and "Copy dump" read the same data), and editing takes the same path as the
///     "Chunks" tab: record payload → <see cref="EditSession" /> → a COPY of the save.
///
///     One card serves both creatures (field 6) and block entities (field 8) - the differences are only
///     the address (<see cref="EntityRow.Field" />) and the position: a creature has three float64 values
///     right in the byte stream, edited as a separate `pos.x/y/z` row, while a block entity has ordinary fields.
/// </summary>
public sealed class EntityCardModel : INotifyPropertyChanged
{
    private readonly EntityRow _row;
    private readonly Func<IEntityEditHost?> _hostProvider;
    private IEntityEditHost? _host;
    private bool _hostResolved;
    private bool _loadTried;
    private string? _loadError;
    private string? _editError;
    private List<string>? _parsedLines;
    private EntityEditService.Target? _target;
    private string _rawHexOriginal = "";

    public EntityCardModel(EntityRow row, Func<IEntityEditHost?> hostProvider)
    {
        _row = row;
        _hostProvider = hostProvider;
    }

    /// <summary>The window is resolved lazily: when the card is created (from the list template) there may be no visual tree yet.</summary>
    private IEntityEditHost? Host
    {
        get
        {
            if (_host is not null || _hostResolved) return _host;
            _host = _hostProvider();
            _hostResolved = _host is not null;
            return _host;
        }
    }

    /// <summary>The record the card shows.</summary>
    public EntityRow Row => _row;

    // ------------------------- View -------------------------

    /// <summary>Card title: the player-given name ("Moonlight") when there is one, because that is what
    /// people search the entity by. Otherwise the species code. The code is not lost: it is in <see cref="CodeText" />.</summary>
    public string Title => _row.Label;

    public bool HasTitle => Title.Length > 0;

    /// <summary>The full record code when it differs from the title: so an entity with a player name shows
    /// the species next to "Moonlight", and a modded one shows the domain (`equusdestrier:horse-…`).</summary>
    public string CodeText => _row.Code.Length > 0 && _row.Code != Title ? _row.Code : "";

    public bool HasCodeText => CodeText.Length > 0;

    public string KindBadge => _row.KindBadge;
    public bool HasKind => KindBadge.Length > 0;

    public string SexAge => _row.Sub;
    public bool HasSexAge => SexAge.Length > 0;

    /// <summary>CHUNK coordinates from the record key - not a record field (they cannot be "changed"
    /// without moving the record to another chunk). The real position is <see cref="PositionText" />.</summary>
    public string Coordinates => $"chunk X:{_row.ChunkX} Y:{_row.ChunkY} Z:{_row.ChunkZ}";

    public string Size => $"raw {_row.Size}B";

    /// <summary>Before the card loads, the heuristic dump from the scanner; afterwards the exact parsed
    /// values (after an edit the heuristic over the original bytes would already lie).</summary>
    public string Fields => _parsedLines is { Count: > 0 }
        ? string.Join("\n", _parsedLines)
        : _row.Entity?.FieldsSummary ?? "";

    public bool HasFields => _parsedLines?.Count > 0 || _row.Entity is { Fields.Count: > 0 };

    /// <summary>The entity position (float64 in the blob) when it could be located unambiguously.</summary>
    public string PositionText
    {
        get
        {
            if (_target is not { HasPosition: true } t) return "";
            var p = t.Coordinates;
            return $"pos X:{Num(p.X)} Y:{Num(p.Y)} Z:{Num(p.Z)}";
        }
    }

    public bool HasPositionText => PositionText.Length > 0;

    /// <summary>Note - a proxy to the record: an edit in the TextBox must land in the model, not in a copy
    /// ("Copy dump" and the state reset on save close read it).</summary>
    public string? Note
    {
        get => _row.Entity?.Note ?? _row.BlockEntity?.Note;
        set
        {
            if (_row.Entity is { } e) e.Note = value;
            else if (_row.BlockEntity is { } be) be.Note = value;
        }
    }

    // ------------------------- Edit -------------------------

    public bool CanEdit => Host?.Save is not null && _row.IsAddressable;

    public bool IsEditing { get; private set; }

    public ObservableCollection<EntityEditRow> Rows { get; } = new();

    /// <summary>The full record payload in hex. Escape hatch for bytes the parser did not decode
    /// (opaque/raw mod trees): when changed, it replaces the payload entirely.</summary>
    public string RawHex { get; set; } = "";

    /// <summary>An edit is already accumulated for this record (from the "Entities" or "Chunks" tab).</summary>
    public bool IsPending
    {
        get
        {
            var host = Host;
            if (host is null || !_row.IsAddressable) return false;
            return host.Edits.TryGetPayloadEdits(_row.Position, out var map)
                   && map is not null && map.ContainsKey((_row.Field, _row.Occurrence));
        }
    }

    public string LoadNote => _loadError ?? "";
    public bool HasLoadNote => LoadNote.Length > 0;

    public string EditHint
    {
        get
        {
            if (_target is null) return "";
            var opaque = _target.Fields.Count(f => f.Type == ChunkFieldType.Opaque);
            var notEditable = _target.Fields.Count(f => f.Type is ChunkFieldType.Tree or ChunkFieldType.ItemStack
                or ChunkFieldType.TreeArray);
            var parts = new List<string>();
            if (opaque > 0) parts.Add($"{opaque} opaque byte(s) — hex-edit only");
            if (notEditable > 0) parts.Add($"{notEditable} nested value(s) — shown read-only");
            if (_target.IsEntity)
            {
                if (_target.PositionCopies.Count > 0 && !_target.HasPosition)
                    parts.Add("position copies disagree — position is read-only");
                if (_target.PositionCopies.Count == 0) parts.Add("position not found in this blob");
            }

            return string.Join(" · ", parts);
        }
    }

    public bool HasEditHint => EditHint.Length > 0;

    public string EditError => _editError ?? "";
    public bool HasEditError => EditError.Length > 0;

    /// <summary>Edit window title: it differs between creatures and block entities.</summary>
    public string EditTitle => _row.IsBlockEntity ? "Edit block entity" : "Edit entity";

    /// <summary>Loads the record (payload + position) - once per card, when it is shown.</summary>
    public void EnsureLoaded()
    {
        if (_loadTried) return;
        var host = Host;
        if (host?.Save is null) return;   // the window is not ready yet - try again on the next access
        if (!_row.IsAddressable)
        {
            _loadTried = true;
            _loadError = "record is not addressable in the save (no chunk occurrence)";
            Notify(nameof(LoadNote), nameof(HasLoadNote));
            return;
        }

        _loadTried = true;
        if (!EntityEditService.TryLoad(host, _row.Field, _row.Position, _row.Occurrence, out var t, out var err))
        {
            _loadError = err;
            return;
        }

        _target = t;
        Notify(nameof(PositionText), nameof(HasPositionText), nameof(CanEdit), nameof(IsPending),
            nameof(EditHint), nameof(HasEditHint), nameof(LoadNote), nameof(HasLoadNote));
    }

    /// <summary>Discards the parsed copy and reads it again (after an edit is cancelled or re-applied).</summary>
    public void Reload()
    {
        _loadTried = false;
        _target = null;
        _parsedLines = null;
        _editError = null;
        if (IsEditing)
        {
            IsEditing = false;
            Rows.Clear();
        }

        EnsureLoaded();
        Notify(nameof(LoadNote), nameof(HasLoadNote));
        NotifyAll();
    }

    /// <summary>Prepares the edit rows for <see cref="Components.EntityEditDialog" />. false means the
    /// record cannot be edited and the reason is already in <see cref="EditError" />.</summary>
    public bool BeginEdit()
    {
        var host = Host;
        if (host?.Save is null)
        {
            SetError("no save is open");
            return false;
        }

        EnsureLoaded();
        if (_target is null)
        {
            SetError(_loadError ?? "record is not editable");
            return false;
        }

        Rows.Clear();
        RawHex = Convert.ToHexString(_target.Payload).ToLowerInvariant();
        _rawHexOriginal = RawHex;
        foreach (var f in _target.Fields.Where(f => f.IsEditable))
            Rows.Add(new EntityEditRow
            {
                Label = f.DisplayPath, Field = f, Text = FieldText(f), OriginalText = FieldText(f)
            });

        if (_target.HasPosition)
        {
            var p = _target.Coordinates;
            Rows.Add(PosRow("pos.x", 0, p.X));
            Rows.Add(PosRow("pos.y", 1, p.Y));
            Rows.Add(PosRow("pos.z", 2, p.Z));
        }

        _editError = null;
        IsEditing = true;
        NotifyAll();
        return true;
    }

    public void CancelEdit()
    {
        IsEditing = false;
        Rows.Clear();
        _editError = null;
        NotifyAll();
    }

    /// <summary>Validates and applies the edit: changed fields + position → a pending edit in the session.
    /// false means nothing to apply or the write failed, with the reason in <see cref="EditError" />.</summary>
    public async Task<bool> ApplyAsync()
    {
        if (!IsEditing) return false;
        var host = Host;
        if (host is null || _target is null)
        {
            SetError("edit target is not available — reopen the card");
            return false;
        }

        // Only a check of "is there anything to apply": which rows change is decided by TryBuildPayload -
        // it needs all three position components even when the user touched only one.
        var fieldsChanged = Rows.Any(r => r.Text != r.OriginalText);
        var rawChanged = !string.Equals(RawHex, _rawHexOriginal, StringComparison.OrdinalIgnoreCase);
        if (!fieldsChanged && !rawChanged)
        {
            SetError("nothing changed");
            return false;
        }

        if (fieldsChanged && rawChanged)
        {
            SetError("raw hex and field edits are both changed — apply one of them, not both");
            return false;
        }

        byte[]? payload;
        if (rawChanged)
        {
            var clean = new string(RawHex.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
            if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
            try
            {
                payload = Convert.FromHexString(clean);
            }
            catch (FormatException)
            {
                SetError("raw hex is not a valid even-length hex string");
                return false;
            }

            if (payload.Length == 0)
            {
                SetError("raw payload cannot be empty");
                return false;
            }
        }
        else if (!EntityEditService.TryBuildPayload(_target, Rows.ToList(), out payload, out var err) || payload is null)
        {
            SetError(err ?? "cannot build the payload");
            return false;
        }

        if (!await EntityEditService.CommitAsync(host, _target, payload, EntityEditService.Label(_target)))
            return false;

        var label = EntityEditService.Label(_target);
        _parsedLines = _target.Fields.Where(f => f.Type != ChunkFieldType.Opaque)
            .Select(f => $"{f.DisplayPath} = {f.ValueText}").ToList();
        IsEditing = false;
        Rows.Clear();
        _editError = null;

        // Re-read the record from the session now: the position offsets differ after an edit.
        _loadTried = false;
        _target = null;
        EnsureLoaded();

        host.Status = $"Pending edit: {label} ({payload.Length} B) — Review & apply…";
        NotifyAll();
        return true;
    }

    private EntityEditRow PosRow(string label, int component, double value)
    {
        var text = Num(value);
        return new EntityEditRow { Label = label, PositionComponent = component, Text = text, OriginalText = text };
    }

    private static string FieldText(ChunkField f) => f.EditText;

    /// <summary>Round-trip format: the text must parse back into the same value.</summary>
    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private void SetError(string message)
    {
        _editError = message;
        NotifyAll();
    }

    private void NotifyAll()
    {
        Notify(nameof(IsEditing), nameof(Fields), nameof(HasFields), nameof(EditError), nameof(HasEditError),
            nameof(PositionText), nameof(HasPositionText), nameof(IsPending), nameof(CanEdit),
            nameof(EditHint), nameof(HasEditHint), nameof(LoadNote), nameof(HasLoadNote));
    }

    private void Notify(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
