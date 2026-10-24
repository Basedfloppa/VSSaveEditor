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
    ///     Moves the row texts into typed fields of the record. One implementation for the record cards
    ///     and the "Chunks" tab: value parsing used to be duplicated.
    /// </summary>
    public static bool TryApplyTexts(IReadOnlyList<(ChunkField Field, string? Text)> rows, out string? error)
    {
        error = null;
        var culture = CultureInfo.InvariantCulture;
        foreach (var (f, raw) in rows)
        {
            var text = raw?.Trim() ?? "";
            var name = f.DisplayPath;
            switch (f.Type)
            {
                case ChunkFieldType.Int32:
                case ChunkFieldType.Int64:
                    // as in the chunk card: integers in the current culture, floats invariantly
                    if (!long.TryParse(text, out var iv))
                    {
                        error = $"Bad int for {name}: '{text}'";
                        return false;
                    }

                    ChunkDataEditor.SetInt(f, iv);
                    break;
                case ChunkFieldType.Float:
                    if (!float.TryParse(text, NumberStyles.Float, culture, out var fv))
                    {
                        error = $"Bad float for {name}: '{text}'";
                        return false;
                    }

                    ChunkDataEditor.SetFloat(f, fv);
                    break;
                case ChunkFieldType.Double:
                    if (!double.TryParse(text, NumberStyles.Float, culture, out var dv))
                    {
                        error = $"Bad double for {name}: '{text}'";
                        return false;
                    }

                    ChunkDataEditor.SetDouble(f, dv);
                    break;
                case ChunkFieldType.String:
                    ChunkDataEditor.SetString(f, raw ?? "");
                    break;
                case ChunkFieldType.Bool:
                    if (!TryParseBool(text, out var bv))
                    {
                        error = $"Bad bool for {name}: '{text}' (use true/false)";
                        return false;
                    }

                    ChunkDataEditor.SetBool(f, bv);
                    break;
                case ChunkFieldType.ByteArray:
                    if (!TryParseHex(text, out var bytes, out var hexError))
                    {
                        error = $"Bad hex for {name}: {hexError}";
                        return false;
                    }

                    if (bytes.Length > ushort.MaxValue)
                    {
                        error = $"Too many bytes for {name}: {bytes.Length} (max 65535)";
                        return false;
                    }

                    ChunkDataEditor.SetBytes(f, bytes);
                    break;
                case ChunkFieldType.StringArray:
                    ChunkDataEditor.SetStringArray(f, SplitList(raw));
                    break;
                case ChunkFieldType.IntArray:
                case ChunkFieldType.LongArray:
                {
                    var values = new List<long>();
                    foreach (var part in SplitList(raw))
                    {
                        if (!long.TryParse(part, NumberStyles.Integer, culture, out var lv))
                        {
                            error = $"Bad int in {name}: '{part}'";
                            return false;
                        }

                        values.Add(lv);
                    }

                    ChunkDataEditor.SetLongArray(f, values);
                    break;
                }
                case ChunkFieldType.FloatArray:
                case ChunkFieldType.DoubleArray:
                {
                    var values = new List<double>();
                    foreach (var part in SplitList(raw))
                    {
                        if (!double.TryParse(part, NumberStyles.Float, culture, out var dv2))
                        {
                            error = $"Bad number in {name}: '{part}'";
                            return false;
                        }

                        values.Add(dv2);
                    }

                    ChunkDataEditor.SetDoubleArray(f, values);
                    break;
                }
                case ChunkFieldType.BoolArray:
                {
                    var values = new List<bool>();
                    foreach (var part in SplitList(raw))
                    {
                        if (!TryParseBool(part, out var bv2))
                        {
                            error = $"Bad bool in {name}: '{part}'";
                            return false;
                        }

                        values.Add(bv2);
                    }

                    ChunkDataEditor.SetBoolArray(f, values);
                    break;
                }
                case ChunkFieldType.Tree:
                case ChunkFieldType.ItemStack:
                case ChunkFieldType.TreeArray:
                case ChunkFieldType.Opaque:
                    // Not a leaf value: edit it through the nested fields or the hex fallback.
                    break;
            }
        }

        return true;
    }

    private static bool TryParseBool(string text, out bool value)
    {
        if (bool.TryParse(text, out value)) return true;
        if (text == "1") { value = true; return true; }
        if (text == "0") { value = false; return true; }
        value = false;
        return false;
    }

    private static List<string> SplitList(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? new List<string>()
            : raw.Split(',').Select(x => x.Trim()).ToList();

    private static bool TryParseHex(string text, out byte[] bytes, out string? error)
    {
        error = null;
        var clean = new string(text.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
        if (clean.Length == 0)
        {
            bytes = Array.Empty<byte>();
            return true;
        }

        if (clean.Length % 2 != 0)
        {
            bytes = Array.Empty<byte>();
            error = "odd number of hex digits";
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(clean);
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            error = "not a hex string";
            return false;
        }
    }

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
    {
        var edits = host.Edits;
        if (edits.HasWholeChunkEdit(target.Position))
        {
            var ok = await host.Confirm("Field edit vs raw hex",
                $"Chunk {EditSession.ChunkScope(target.Position)} already has a pending RAW HEX edit that "
                + "replaces the whole chunk.\nApplying the edit will DISCARD that raw hex edit for this chunk.\nContinue?");
            if (!ok) return false;
            edits.ReplaceWholeChunkWithPayload(target.Position, target.Field, target.Occurrence, payload, label);
        }
        else
        {
            edits.PutPayload(target.Position, target.Field, target.Occurrence, payload, label);
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
