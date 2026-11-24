using System.Globalization;
using VSCore;
using VSSaveEditor.Models;

namespace VSSaveEditor;

/// <summary>What the entity card in the dump needs from the window: the save, the edit session, status and confirmations.</summary>
public interface IEntityEditHost
{
    VSSaveFile? Save { get; }
    EditSession Edits { get; }

    string Status { set; }
    void PendingChanged();
    Task<bool> Confirm(string title, string message);
}

/// <summary>
///     Editing one entity from the "Entities" tab. The path is the same as in the "Chunks" tab: the record
///     is a field of the chunk blob (6 - creature, 8 - block entity), addressed by the triple
///     (chunk record key, field, occurrence), and the changed payload goes into
///     <see cref="EditSession" /> and is written only to the COPY of the save.
///
///     The difference from chunk cards is the position. A creature has none in the ECX tree: the game writes three
///     float64 values (x, y, z) straight into the byte stream (see <see cref="EntityPositionCodec" />), so such a row
///     is edited separately from the named fields. For a block entity the position is the ordinary `posx/posy/posz` fields
///     and is edited like any other field.
/// </summary>
public static class EntityEditService
{
    /// <summary>The record opened for editing: its payload, parsed structure and located position.</summary>
    public sealed record Target(long Position, int Field, int Occurrence,
        EntityRecord? Entity, BlockEntityRecord? BlockEntity, byte[] Payload,
        IReadOnlyList<EntityPositionOccurrence> PositionCopies)
    {
        public bool IsEntity => Field == 6;

        /// <summary>The named fields of the record (opaque bytes are left for the card to decode).</summary>
        public IReadOnlyList<ChunkField> Fields => Entity?.Fields ?? BlockEntity?.Fields ?? (IReadOnlyList<ChunkField>)Array.Empty<ChunkField>();

        /// <summary>The position is editable only when every copy found is the same point.
        /// A block entity has no separate "position in the stream": its fields are visible and edited as ordinary ones.</summary>
        public bool HasPosition => IsEntity && PositionCopies.Count > 0 && EntityPositionCodec.AllAgree(PositionCopies);

        /// <summary>The current position (x, y, z) - meaningful when <see cref="HasPosition" />.</summary>
        public (double X, double Y, double Z) Coordinates => HasPosition
            ? (PositionCopies[0].X, PositionCopies[0].Y, PositionCopies[0].Z)
            : (0, 0, 0);

        public string Name => Entity is { } e
            ? e.Species ?? e.TypeName ?? ""
            : BlockEntity?.TypeName ?? "";
    }

    /// <summary>Payload of the record with the given field and occurrence; null when there is no such record.</summary>
    public static byte[]? ReadPayload(byte[] chunkBlob, int field, int occurrence)
    {
        foreach (var t in ChunkDataEditor.WalkTop(chunkBlob))
        {
            if (t.Field != field || t.Occurrence != occurrence) continue;
            var len = t.PayloadEnd - t.PayloadStart;
            var payload = new byte[len];
            Buffer.BlockCopy(chunkBlob, t.PayloadStart, payload, 0, len);
            return payload;
        }

        return null;
    }

    /// <summary>
    ///     The chunk blob in its current state: with the accumulated edits (raw hex or fields), when there are any.
    ///     Otherwise the card would show original values on top of already edited ones.
    /// </summary>
    public static byte[]? ReadChunkCurrent(VSSaveFile? save, EditSession edits, long position)
    {
        if (save?.ReadChunk(position) is not { } raw) return null;
        if (edits.TryGetWholeChunk(position, out var whole) && whole is not null) return whole;
        if (edits.TryGetPayloadEdits(position, out var map) && map is { Count: > 0 })
            raw = ChunkDataEditor.SplicePayloads(raw, map.Select(kv => (kv.Key.field, kv.Key.occ, kv.Value)).ToList());
        return raw;
    }

    /// <summary>Prepares the record for editing. false plus error means it cannot be opened (no record / broken blob).</summary>
    public static bool TryLoad(IEntityEditHost host, int field, long position, int occurrence, out Target? target,
        out string? error)
    {
        target = null;
        error = null;
        if (field != 6 && field != 8)
        {
            error = $"field {field} is not editable";
            return false;
        }

        var blob = ReadChunkCurrent(host.Save, host.Edits, position);
        if (blob is null)
        {
            error = "save or chunk is not available — reopen the save";
            return false;
        }

        var payload = ReadPayload(blob, field, occurrence);
        if (payload is null)
        {
            error = $"{(field == 6 ? "entity" : "block entity")} [{occurrence}] not found in chunk {position}";
            return false;
        }

        EntityRecord? entity = null;
        BlockEntityRecord? blockEntity = null;
        try
        {
            if (field == 6) entity = ChunkDataParser.ParseEntity(payload);
            else blockEntity = ChunkDataParser.ParseBlockEntity(payload);
        }
        catch (Exception ex)
        {
            error = $"{(field == 6 ? "entity" : "block entity")} parse failed: {ex.Message}";
            return false;
        }

        // Only creatures carry the position in the byte stream; a block entity keeps it in ordinary fields.
        var copies = Array.Empty<EntityPositionOccurrence>();
        if (field == 6)
        {
            var (cx, cy, cz) = ChunkPosHelper.DecodeIndex((ulong)position);
            copies = EntityPositionCodec.Locate(payload, cx * 32, cy * 32, cz * 32).ToArray();
        }

        target = new Target(position, field, occurrence, entity, blockEntity, payload, copies);
        return true;
    }

    /// <summary>
    ///     Moves the row texts into typed fields of the record. The parsing itself lives in
    ///     <see cref="ChunkFieldText" /> — the player data window uses the same one.
    /// </summary>
    public static bool TryApplyTexts(IReadOnlyList<(ChunkField Field, string? Text)> rows, out string? error)
        => ChunkFieldText.TryApply(rows, out error);

    /// <summary>The edit label for history and preview.</summary>
    public static string Label(Target target)
    {
        var kind = target.IsEntity ? "entity" : "block entity";
        return $"{kind}[{target.Occurrence}]" + (target.Name.Length == 0 ? "" : $" {target.Name}");
    }

    /// <summary>
    ///     Puts the changed payload into the edit session. If the chunk already has an accumulated raw hex edit,
    ///     it would override the field edit - so we ask and replace it in one history step (as in the chunk card).
    /// </summary>
    public static async Task<bool> CommitAsync(IEntityEditHost host, Target target, byte[] payload, string label)
        => await CommitPayloadAsync(host, target.Position, target.Field, target.Occurrence, payload, label);

    /// <summary>
    ///     The commit itself, addressed by the triple — used both by the field editor and by the "Entity data"
    ///     window, so the conflict with a pending raw-hex edit is resolved in one place and not two.
    /// </summary>
    public static async Task<bool> CommitPayloadAsync(IEntityEditHost host, long position, int field, int occurrence,
        byte[] payload, string label)
    {
        var edits = host.Edits;
        if (edits.HasWholeChunkEdit(position))
        {
            var ok = await host.Confirm("Field edit vs raw hex",
                $"Chunk {EditSession.ChunkScope(position)} already has a pending RAW HEX edit that "
                + "replaces the whole chunk.\nApplying the edit will DISCARD that raw hex edit for this chunk.\nContinue?");
            if (!ok) return false;
            edits.ReplaceWholeChunkWithPayload(position, field, occurrence, payload, label);
        }
        else
        {
            edits.PutPayload(position, field, occurrence, payload, label);
        }

        host.PendingChanged();
        return true;
    }

    /// <summary>
    ///     Assembles the final payload: the changed fields first, then the position. Unchanged rows are not
    ///     rebuilt (otherwise double precision would be lost and null would become an empty string).
    ///     Position offsets are recomputed against the rebuilt bytes (editing a row shifts everything
    ///     behind it), and only copies with the old value are overwritten - a miss does not damage
    ///     other fields. All three position components come from the rows, even when only one was changed.
    /// </summary>
    public static bool TryBuildPayload(Target target, IReadOnlyList<EntityEditRow> rows,
        out byte[]? payload, out string? error)
    {
        payload = null;
        error = null;

        var fields = rows.Where(r => r.Field is not null && r.Text != r.OriginalText)
            .Select(r => (r.Field!, (string?)r.Text)).ToList();
        if (!TryApplyTexts(fields, out error)) return false;

        byte[] bytes;
        try
        {
            bytes = target.IsEntity
                ? ChunkDataEditor.SerializeEntity(target.Entity!)
                : ChunkDataEditor.SerializeBlockEntity(target.BlockEntity!);
        }
        catch (Exception ex)
        {
            error = $"serialization failed: {ex.Message}";
            return false;
        }

        var posRows = rows.Where(r => r.Field is null && r.PositionComponent >= 0).ToList();
        if (posRows.Count > 0 && posRows.Any(r => r.Text != r.OriginalText))
        {
            if (!target.HasPosition)
            {
                error = "position is ambiguous in this entity — field edits were not applied";
                return false;
            }

            var old = target.Coordinates;
            var values = new double[3];
            for (var i = 0; i < 3; i++)
            {
                var row = posRows.FirstOrDefault(r => r.PositionComponent == i);
                var text = row?.Text?.Trim() ?? "";
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                {
                    error = $"Bad double for pos.{i switch { 0 => "x", 1 => "y", _ => "z" }}: '{text}'";
                    return false;
                }
            }

            var (cx, cy, cz) = ChunkPosHelper.DecodeIndex((ulong)target.Position);
            var copies = EntityPositionCodec.Locate(bytes, cx * 32, cy * 32, cz * 32)
                .Where(c => EntityPositionCodec.Same(c.X, old.X)
                            && EntityPositionCodec.Same(c.Y, old.Y)
                            && EntityPositionCodec.Same(c.Z, old.Z))
                .ToList();
            if (copies.Count == 0)
            {
                error = "position bytes moved after re-serialization — reopen the entity and try again";
                return false;
            }

            if (EntityPositionCodec.Patch(bytes, copies, values[0], values[1], values[2]) == 0)
            {
                error = "position was not rewritten (stale offsets) — reopen the entity and try again";
                return false;
            }
        }

        payload = bytes;
        return true;
    }
}
