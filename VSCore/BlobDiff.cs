using System;
using System.Collections.Generic;
using System.Linq;

namespace VSCore;

/// <summary>A single "before -> after" difference inside a blob. ByteLevel — bytes were compared, not fields
/// of the ECX blob (its structure could not be parsed), so the values are sizes, not field values.</summary>
public sealed class FieldChange
{
    public string Field { get; init; } = "";
    public string OldValue { get; init; } = "";
    public string NewValue { get; init; } = "";
    public bool ByteLevel { get; init; }
}

/// <summary>
/// Compares save blobs for the edit preview: ECX fields of entities/block entities/inventories
/// and raw bytes. Pure logic without UI — lives in VSCore for the sake of unit tests (F3).
/// </summary>
public static class BlobDiff
{
    /// <summary>
    /// Differences in the payload of one top-level chunk field. For field 6/8 the payload is an ECX blob, and
    /// the rows come from its fields; for the rest (and for unparsed ones) it is a byte-by-byte comparison.
    /// </summary>
    public static List<FieldChange> Payload(int field, string label, byte[]? oldB, byte[] newB)
    {
        if (oldB is null)
            return new List<FieldChange> { new() { Field = label, OldValue = "(no value)", NewValue = $"{newB.Length} B", ByteLevel = true } };

        var oldFields = field switch
        {
            6 => SafeFields(() => ChunkDataParser.ParseEntity(oldB).Fields),
            8 => SafeFields(() => ChunkDataParser.ParseBlockEntity(oldB).Fields),
            _ => null
        };
        var newFields = field switch
        {
            6 => SafeFields(() => ChunkDataParser.ParseEntity(newB).Fields),
            8 => SafeFields(() => ChunkDataParser.ParseBlockEntity(newB).Fields),
            _ => null
        };
        if (oldFields is null || newFields is null) return new List<FieldChange> { Bytes(label, oldB, newB) };

        var rows = Fields(oldFields, newFields);
        // the fields matched but the blob is still different (e.g. the header changed) — we do not hide that
        return rows.Count > 0 ? rows : new List<FieldChange> { Bytes(label, oldB, newB) };
    }

    /// <summary>Compares two sets of ECX fields by key; identical keys are distinguished by an #n suffix.</summary>
    public static List<FieldChange> Fields(IEnumerable<ChunkField> oldFields, IEnumerable<ChunkField> newFields)
    {
        var oldF = Describe(oldFields);
        var newF = Describe(newFields);
        var newMap = new Dictionary<string, string>();
        foreach (var kv in newF) newMap[kv.Key] = kv.Value;
        var res = new List<FieldChange>();
        foreach (var kv in oldF)
        {
            if (!newMap.TryGetValue(kv.Key, out var nv)) res.Add(new FieldChange { Field = kv.Key, OldValue = kv.Value, NewValue = "(removed)" });
            else if (!string.Equals(kv.Value, nv, StringComparison.Ordinal)) res.Add(new FieldChange { Field = kv.Key, OldValue = kv.Value, NewValue = nv });
        }
        var oldKeys = oldF.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var kv in newF)
            if (!oldKeys.Contains(kv.Key)) res.Add(new FieldChange { Field = kv.Key, OldValue = "(added)", NewValue = kv.Value });
        return res;
    }

    /// <summary>Differences of the player blob: inventories are matched by index (names may repeat, B3).</summary>
    public static List<FieldChange> Player(string label, byte[]? oldB, byte[] newB)
    {
        PlayerData? oldP = ParsePlayer(oldB);
        PlayerData? newP = ParsePlayer(newB);
        if (oldP is null || newP is null) return new List<FieldChange> { Bytes(label, oldB, newB) };

        var res = new List<FieldChange>();
        int count = Math.Max(oldP.Inventories.Count, newP.Inventories.Count);
        for (int i = 0; i < count; i++)
        {
            var name = i < newP.Inventories.Count ? newP.Inventories[i].Name
                     : i < oldP.Inventories.Count ? oldP.Inventories[i].Name : null;
            var oldFields = i < oldP.Inventories.Count ? oldP.Inventories[i].Fields : new List<ChunkField>();
            var newFields = i < newP.Inventories.Count ? newP.Inventories[i].Fields : new List<ChunkField>();
            foreach (var c in Fields(oldFields, newFields))
                res.Add(new FieldChange { Field = $"inventory[{i}] '{name ?? "?"}' · {c.Field}", OldValue = c.OldValue, NewValue = c.NewValue });
        }
        if (!string.Equals(oldP.Uid, newP.Uid, StringComparison.Ordinal))
            res.Add(new FieldChange { Field = "uid", OldValue = oldP.Uid ?? "(none)", NewValue = newP.Uid ?? "(none)" });
        return res;
    }

    /// <summary>Byte-level difference: for the preview what matters is how many bytes, and what size they became.</summary>
    public static FieldChange Bytes(string label, byte[]? oldB, byte[] newB)
    {
        if (oldB is null)
            return new() { Field = label, OldValue = "(no value)", NewValue = $"{newB.Length} B", ByteLevel = true };
        int differ = CountDiffering(oldB, newB);
        return new()
        {
            Field = label,
            OldValue = $"{oldB.Length} B",
            NewValue = oldB.Length == newB.Length
                ? $"{newB.Length} B, {differ} byte(s) differ"
                : $"{newB.Length} B, {differ} byte(s) differ (was {oldB.Length} B)",
            ByteLevel = true
        };
    }

    public static int CountDiffering(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length), diff = Math.Abs(a.Length - b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) diff++;
        return diff;
    }

    /// <summary>Payload of a top-level chunk field by field number and repetition (field 6/8 and others).</summary>
    public static byte[]? TopPayload(byte[] blob, int field, int occurrence)
    {
        foreach (var t in ChunkDataEditor.WalkTop(blob))
            if (t.Field == field && t.Occurrence == occurrence)
                return blob[t.PayloadStart..t.PayloadEnd];
        return null;
    }

    private static List<KeyValuePair<string, string>> Describe(IEnumerable<ChunkField> fields)
    {
        var res = new List<KeyValuePair<string, string>>();
        var seen = new Dictionary<string, int>();
        foreach (var f in fields)
        {
            var key = f.DisplayPath;
            seen.TryGetValue(key, out int n);
            seen[key] = n + 1;
            var id = n == 0 ? key : $"{key}#{n + 1}";
            res.Add(new KeyValuePair<string, string>(id, f.ValueText));
        }
        return res;
    }

    private static List<ChunkField>? SafeFields(Func<List<ChunkField>> parse)
    {
        try { return parse(); } catch { return null; }
    }

    private static PlayerData? ParsePlayer(byte[]? b)
    {
        if (b is null || b.Length == 0) return null;
        try { return PlayerDataParser.Parse(b, 0); } catch { return null; }
    }
}
