using VSCore;

namespace VSSaveEditor;

/// <summary>
///     Accumulated data edits (fields of entity/BlockEntity/inventories and raw hex of a whole chunk).
///     They are applied only to the COPY of the save. Chunk positions and player ids belong to a specific
///     file, so the session is reset when another save is opened.
///     Every edit goes into <see cref="Journal" />: an undo restores exactly the state that
///     existed before it (including displaced field edits when raw hex overrode them).
/// </summary>
public sealed class EditSession
{
    private readonly Dictionary<long, Dictionary<(int field, int occ), byte[]>> _payload = new();

    // Edit labels for history and preview: the key is the same as the edit itself.
    private readonly Dictionary<(long pos, int field, int occ), string> _payloadLabels = new();
    private readonly Dictionary<long, string> _playerLabels = new();
    private readonly Dictionary<long, byte[]> _players = new();
    private readonly Dictionary<long, byte[]> _wholeChunk = new();
    private readonly Dictionary<long, string> _wholeLabels = new();

    // Journal of edits already applied to the working copy. Separate from EditJournal: Undo/Redo cover
    // only not-yet-applied edits, while the history must survive the apply.
    private readonly List<AppliedEdit> _applied = new();

    /// <summary>The undo/redo journal of pending edits.</summary>
    public EditJournal Journal { get; } = new();

    /// <summary>Which chunk (position) the Raw hex field applies to.</summary>
    public long HexTargetPosition { get; set; } = -1;

    public string HexTargetLabel { get; set; } = "";

    public int ChunkCount => _payload.Count + _wholeChunk.Count;
    public int PlayerCount => _players.Count;
    public bool HasPending => ChunkCount + PlayerCount > 0;

    /// <summary>Edits already applied to the working copy (in application order).</summary>
    public IReadOnlyList<AppliedEdit> Applied => _applied;

    public int AppliedCount => _applied.Count;

    /// <summary>How many applied edits have not yet gone into any file saved as a copy.</summary>
    public int UnsavedAppliedCount => _applied.Count(a => a.SavedTo is null);

    public bool CanUndo => Journal.CanUndo;
    public bool CanRedo => Journal.CanRedo;
    public int UndoCount => Journal.UndoCount;
    public int RedoCount => Journal.RedoCount;

    /// <summary>How many positions are waiting to be written (field edits and whole-chunk hex edits separately).</summary>
    public int PayloadEditCount => _payload.Count;

    public int WholeChunkEditCount => _wholeChunk.Count;

    /// <summary>Positions with accumulated edits - for diagnostics and step-by-step removal.</summary>
    public IReadOnlyCollection<long> PayloadPositions => _payload.Keys;

    public IReadOnlyCollection<long> WholeChunkPositions => _wholeChunk.Keys;
    public IReadOnlyCollection<long> PlayerIds => _players.Keys;

    public bool HasPayloadEdits(long position)
    {
        return _payload.TryGetValue(position, out var map) && map.Count > 0;
    }

    public bool HasWholeChunkEdit(long position)
    {
        return _wholeChunk.ContainsKey(position);
    }

    public bool TryGetWholeChunk(long position, out byte[]? blob)
    {
        return _wholeChunk.TryGetValue(position, out blob);
    }

    public bool TryGetPayloadEdits(long position, out Dictionary<(int field, int occ), byte[]>? map)
    {
        return _payload.TryGetValue(position, out map);
    }

    public bool TryGetPlayerBlob(long playerId, out byte[]? blob)
    {
        return _players.TryGetValue(playerId, out blob);
    }

    /// <summary>Label of a field edit (for history and diff): what exactly this payload changes.</summary>
    public string LabelForPayload(long position, int field, int occ)
    {
        return _payloadLabels.TryGetValue((position, field, occ), out var l) ? l : DefaultPayloadLabel(field, occ);
    }

    public string LabelForWholeChunk(long position)
    {
        return _wholeLabels.TryGetValue(position, out var l) ? l : "raw hex (whole chunk)";
    }

    public string LabelForPlayer(long playerId)
    {
        return _playerLabels.TryGetValue(playerId, out var l) ? l : "player blob";
    }

    /// <summary>The edit scope for a chunk position: "chunk X,Z yN". The history groups by it.</summary>
    public static string ChunkScope(long position)
    {
        var (cx, cy, cz) = ChunkPosHelper.DecodeIndex((ulong)position);
        return $"chunk {cx},{cz} y{cy}";
    }

    private static string DefaultPayloadLabel(int field, int occ)
    {
        return field switch
        {
            6 => $"entity[{occ}]",
            8 => $"block entity[{occ}]",
            _ => $"field {field}[{occ}]"
        };
    }

    // ------------------------- Recording edits (with history) -------------------------

    /// <summary>Accumulates a payload edit of a field. label says what object it is (for history/diff).</summary>
    public void PutPayload(long position, int field, int occ, byte[] payload, string? label = null)
    {
        var before = _payload.TryGetValue(position, out var map) && map.TryGetValue((field, occ), out var b) ? b : null;
        var lbl = label ?? DefaultPayloadLabel(field, occ);
        ApplyPayload(position, field, occ, payload, lbl);
        Journal.Push(new EditRecord(position, false, ChunkScope(position), lbl,
            () =>
            {
                if (before is null) DropPayload(position, field, occ);
                else ApplyPayload(position, field, occ, before, lbl);
            },
            () => ApplyPayload(position, field, occ, payload, lbl),
            $"payload:{position}:{field}:{occ}"));
    }

    /// <summary>
    ///     Raw hex replaces the whole blob, so field edits of the same position are removed -
    ///     but not silently: the caller asks the user (B4). Undo brings them back too.
    /// </summary>
    public void PutWholeChunk(long position, byte[] blob, string? label = null)
    {
        var beforeWhole = _wholeChunk.TryGetValue(position, out var bw) ? bw : null;
        var beforeLabel = _wholeLabels.TryGetValue(position, out var bl) ? bl : null;
        var beforePayload = CapturePayload(position);
        var lbl = label ?? "raw hex (whole chunk)";
        ApplyWhole(position, blob, lbl);
        DropAllPayload(position); // hex replaces the whole blob: field edits of this position will never be applied
        Journal.Push(new EditRecord(position, false, ChunkScope(position), lbl,
            () =>
            {
                if (beforeWhole is null) DropWhole(position);
                else ApplyWhole(position, beforeWhole, beforeLabel ?? lbl);
                RestorePayload(position, beforePayload);
            },
            () =>
            {
                ApplyWhole(position, blob, lbl);
                DropAllPayload(position);
            },
            $"whole:{position}"));
    }

    /// <summary>
    ///     The chunk hex edit is replaced by a field edit - in one history step: the user pressed
    ///     one button, so one Undo must revert it.
    /// </summary>
    public void ReplaceWholeChunkWithPayload(long position, int field, int occ, byte[] payload, string? label = null)
    {
        var beforeWhole = _wholeChunk.TryGetValue(position, out var bw) ? bw : null;
        var beforeLabel = _wholeLabels.TryGetValue(position, out var bl) ? bl : null;
        var beforePayload = CapturePayload(position);
        var lbl = label ?? DefaultPayloadLabel(field, occ);
        DropWhole(position);
        ApplyPayload(position, field, occ, payload, lbl);
        Journal.Push(new EditRecord(position, false, ChunkScope(position), lbl,
            () =>
            {
                DropPayload(position, field, occ);
                if (beforeWhole is not null) ApplyWhole(position, beforeWhole, beforeLabel ?? "raw hex (whole chunk)");
                RestorePayload(position, beforePayload);
            },
            () =>
            {
                DropWhole(position);
                ApplyPayload(position, field, occ, payload, lbl);
            }));
    }

    /// <summary>Edit of a player blob (a whole inventory).</summary>
    public void PutPlayer(long playerId, byte[] blob, string? label = null)
    {
        var before = _players.TryGetValue(playerId, out var b) ? b : null;
        var lbl = label ?? "player blob";
        ApplyPlayer(playerId, blob, lbl);
        Journal.Push(new EditRecord(playerId, true, PlayerScope(playerId), lbl,
            () =>
            {
                if (before is null) DropPlayer(playerId);
                else ApplyPlayer(playerId, before, lbl);
            },
            () => ApplyPlayer(playerId, blob, lbl),
            $"player:{playerId}:{lbl}"));
    }

    private static string PlayerScope(long playerId)
    {
        return $"player #{playerId}";
    }

    /// <summary>Removes the raw hex edit of a position (one history step). false means there was no edit.</summary>
    public bool RemoveWholeChunk(long position)
    {
        if (!_wholeChunk.TryGetValue(position, out var before)) return false;
        var beforeLabel = LabelForWholeChunk(position);
        DropWhole(position);
        Journal.Push(new EditRecord(position, false, ChunkScope(position), beforeLabel,
            () => ApplyWhole(position, before, beforeLabel),
            () => DropWhole(position)));
        return true;
    }

    /// <summary>Removes the field edits of a position (one history step). false means there were none.</summary>
    public bool RemovePayloadEdits(long position)
    {
        var before = CapturePayload(position);
        if (before is null) return false;
        DropAllPayload(position);
        Journal.Push(new EditRecord(position, false, ChunkScope(position), $"drop {before.Data.Count} field edit(s)",
            () => RestorePayload(position, before),
            () => DropAllPayload(position)));
        return true;
    }

    // ------------------------- Undo / redo -------------------------

    /// <summary>Reverts the last edit. Returns it (null means there is nothing to revert).</summary>
    public EditRecord? Undo()
    {
        return Journal.Undo();
    }

    /// <summary>Reapplies the last reverted edit. Returns it (null means there is nothing to redo).</summary>
    public EditRecord? Redo()
    {
        return Journal.Redo();
    }

    private PayloadSnapshot? CapturePayload(long position)
    {
        if (!_payload.TryGetValue(position, out var map) || map.Count == 0) return null;
        var labels = new Dictionary<(int field, int occ), string>();
        foreach (var key in map.Keys)
            if (_payloadLabels.TryGetValue((position, key.field, key.occ), out var l))
                labels[key] = l;
        return new PayloadSnapshot { Data = new Dictionary<(int field, int occ), byte[]>(map), Labels = labels };
    }

    private void RestorePayload(long position, PayloadSnapshot? snap)
    {
        DropAllPayload(position);
        if (snap is null) return;
        var map = new Dictionary<(int field, int occ), byte[]>(snap.Data);
        _payload[position] = map;
        foreach (var kv in snap.Labels) _payloadLabels[(position, kv.Key.field, kv.Key.occ)] = kv.Value;
    }

    private void ApplyPayload(long position, int field, int occ, byte[] payload, string label)
    {
        if (!_payload.TryGetValue(position, out var map))
            _payload[position] = map = new Dictionary<(int field, int occ), byte[]>();
        map[(field, occ)] = payload;
        _payloadLabels[(position, field, occ)] = label;
    }

    private void DropPayload(long position, int field, int occ)
    {
        if (!_payload.TryGetValue(position, out var map)) return;
        map.Remove((field, occ));
        _payloadLabels.Remove((position, field, occ));
        if (map.Count == 0) _payload.Remove(position);
    }

    private void DropAllPayload(long position)
    {
        if (!_payload.Remove(position)) return;
        foreach (var key in _payloadLabels.Keys.Where(k => k.pos == position).ToList())
            _payloadLabels.Remove(key);
    }

    private void ApplyWhole(long position, byte[] blob, string label)
    {
        _wholeChunk[position] = blob;
        _wholeLabels[position] = label;
    }

    private void DropWhole(long position)
    {
        _wholeChunk.Remove(position);
        _wholeLabels.Remove(position);
    }

    private void ApplyPlayer(long playerId, byte[] blob, string label)
    {
        _players[playerId] = blob;
        _playerLabels[playerId] = label;
    }

    private void DropPlayer(long playerId)
    {
        _players.Remove(playerId);
        _playerLabels.Remove(playerId);
    }

    // ------------------------- Reset and snapshot -------------------------

    /// <summary>
    ///     Drops the accumulated edits without touching the chunk selected for hex editing
    ///     (after a successful apply you can keep editing without picking the chunk again).
    ///     The Undo/Redo journal is cleared, but <see cref="Applied" /> is untouched: that is the history of
    ///     already applied edits, and it must stay visible.
    /// </summary>
    public void ClearPending()
    {
        _payload.Clear();
        _wholeChunk.Clear();
        _players.Clear();
        _payloadLabels.Clear();
        _wholeLabels.Clear();
        _playerLabels.Clear();
        Journal.Clear();
    }

    /// <summary>
    ///     Records that the accumulated edits were applied to the working copy: moves them into the history
    ///     and clears pending. The edits are not lost - the History flyout shows exactly those.
    /// </summary>
    public int CommitPending()
    {
        var records = Journal.Past.ToList();
        var at = DateTime.Now;
        foreach (var r in records)
            _applied.Add(new AppliedEdit(at, r.Scope, r.Label, r.IsPlayer));
        ClearPending();
        return records.Count;
    }

    /// <summary>
    ///     Records in the history an operation that already changes the working copy directly (deleting chunks,
    ///     a region, blocks). It has no pending state and no Undo step: the record exists so the operation
    ///     is visible in History and falls under the "do not close an unsaved copy" protection.
    /// </summary>
    public AppliedEdit RecordApplied(string scope, string label)
    {
        var edit = new AppliedEdit(DateTime.Now, scope, label, false);
        _applied.Add(edit);
        return edit;
    }

    /// <summary>Marks the applied edits as saved to a file (its path goes into the history).</summary>
    public int MarkSaved(string path)
    {
        var n = 0;
        foreach (var a in _applied.Where(a => a.SavedTo is null))
        {
            a.SavedTo = path;
            n++;
        }
        return n;
    }

    /// <summary>Resets everything (including the hex target and the history of applied edits).</summary>
    public (int chunks, int players) Reset()
    {
        var dropped = (ChunkCount, PlayerCount);
        ClearPending();
        _applied.Clear();
        HexTargetPosition = -1;
        HexTargetLabel = "";
        return dropped;
    }

    /// <summary>
    ///     A snapshot for the background write. It copies only references to the blobs (once written into the session they
    ///     never change), so the background thread can read the DB while the UI lives its life.
    /// </summary>
    public ChangeSet Snapshot()
    {
        var positions = _payload.Keys.Union(_wholeChunk.Keys).ToList();
        var chunks = new (long Position, byte[]? Whole, (int Field, int Occ, byte[] Data)[] Fields)[positions.Count];
        for (var i = 0; i < positions.Count; i++)
        {
            var pos = positions[i];
            _wholeChunk.TryGetValue(pos, out var whole);
            (int Field, int Occ, byte[] Data)[] fields = _payload.TryGetValue(pos, out var map) && map.Count > 0
                ? map.Select(kv => (kv.Key.field, kv.Key.occ, kv.Value)).ToArray()
                : Array.Empty<(int, int, byte[])>();
            chunks[i] = (pos, whole, fields);
        }

        var players = _players.Select(kv => (kv.Key, kv.Value)).ToArray();
        return new ChangeSet(chunks, players);
    }

    /// <summary>
    ///     Materializes the snapshot into CopyAndApplyChanges arguments: reads blobs from the save and splices in
    ///     the payload edits. The work is heavy (DB + chunk decompression), so call it in the background.
    /// </summary>
    public static List<(string table, long position, byte[] data)> Materialize(VSSaveFile save, ChangeSet set)
    {
        var changes = new List<(string table, long position, byte[] data)>(set.Chunks.Length + set.Players.Length);
        foreach (var (position, whole, fields) in set.Chunks)
        {
            byte[] blob;
            if (whole is not null)
            {
                blob = whole; // hex edit of the whole chunk
            }
            else
            {
                blob = save.ReadChunk(position) ?? Array.Empty<byte>();
                if (fields.Length > 0)
                    blob = ChunkDataEditor.SplicePayloads(blob, fields.Select(f => (f.Field, f.Occ, f.Data)).ToList());
            }

            changes.Add(("chunk", position, blob));
        }

        foreach (var (playerId, blob) in set.Players) changes.Add(("playerdata", playerId, blob));
        return changes;
    }

    // ------------------------- Low-level mutators (no history) -------------------------

    private sealed class PayloadSnapshot
    {
        public required Dictionary<(int field, int occ), byte[]> Data { get; init; }
        public required Dictionary<(int field, int occ), string> Labels { get; init; }
    }
}

/// <summary>One edit already applied to the working copy: scope, label and where it was saved.</summary>
public sealed class AppliedEdit
{
    public AppliedEdit(DateTime at, string scope, string label, bool isPlayer)
    {
        At = at;
        Scope = scope;
        Label = label;
        IsPlayer = isPlayer;
    }

    public DateTime At { get; }
    public string Scope { get; }
    public string Label { get; }
    public bool IsPlayer { get; }

    /// <summary>null means the edit exists only in the working copy; otherwise the path of the file saved as a copy.</summary>
    public string? SavedTo { get; set; }
}

/// <summary>An immutable snapshot of edits passed to the background copy write.</summary>
public sealed class ChangeSet
{
    public ChangeSet(
        (long Position, byte[]? Whole, (int Field, int Occ, byte[] Data)[] Fields)[] chunks,
        (long PlayerId, byte[] Blob)[] players)
    {
        Chunks = chunks;
        Players = players;
    }

    public (long Position, byte[]? Whole, (int Field, int Occ, byte[] Data)[] Fields)[] Chunks { get; }
    public (long PlayerId, byte[] Blob)[] Players { get; }

    public int Count => Chunks.Length + Players.Length;
}