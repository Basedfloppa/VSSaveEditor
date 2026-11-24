using System.Globalization;

namespace VSCore;

/// <summary>
///     "Text in a box" → field value, for every editor that shows ECX fields as rows (entity cards, player
///     inventories, the player data window). One implementation on purpose: three copies of the same switch
///     is how the number parsing once started to differ between the chunk card and the entity card.
/// </summary>
public static class ChunkFieldText
{
    /// <summary>Applies the texts to the fields. Nothing is applied when any value is bad: a half-edited tree
    /// would be serialised and written, and the user would have no way to tell which rows made it.</summary>
    public static bool TryApply(IReadOnlyList<(ChunkField Field, string? Text)> rows, out string? error)
    {
        foreach (var (f, raw) in rows)
            if (!TryApply(f, raw, out error))
                return false;
        error = null;
        return true;
    }

    public static bool TryApply(ChunkField f, string? raw, out string? error)
    {
        error = null;
        var culture = CultureInfo.InvariantCulture;
        var text = raw?.Trim() ?? "";
        var name = f.DisplayPath;
        switch (f.Type)
        {
            case ChunkFieldType.Int32:
            case ChunkFieldType.Int64:
                // integers in the current culture (as the chunk card always did), floats invariantly below
                if (!long.TryParse(text, out var iv))
                {
                    error = $"Bad int for {name}: '{text}'";
                    return false;
                }

                ChunkDataEditor.SetInt(f, iv);
                return true;
            case ChunkFieldType.Float:
                if (!float.TryParse(text, NumberStyles.Float, culture, out var fv))
                {
                    error = $"Bad float for {name}: '{text}'";
                    return false;
                }

                ChunkDataEditor.SetFloat(f, fv);
                return true;
            case ChunkFieldType.Double:
                if (!double.TryParse(text, NumberStyles.Float, culture, out var dv))
                {
                    error = $"Bad double for {name}: '{text}'";
                    return false;
                }

                ChunkDataEditor.SetDouble(f, dv);
                return true;
            case ChunkFieldType.String:
                ChunkDataEditor.SetString(f, raw ?? "");
                return true;
            case ChunkFieldType.Bool:
                if (!TryParseBool(text, out var bv))
                {
                    error = $"Bad bool for {name}: '{text}' (use true/false)";
                    return false;
                }

                ChunkDataEditor.SetBool(f, bv);
                return true;
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
                return true;
            case ChunkFieldType.StringArray:
                ChunkDataEditor.SetStringArray(f, SplitList(raw));
                return true;
            case ChunkFieldType.IntArray:
            case ChunkFieldType.LongArray:
            {
                var values = new List<long>();
                foreach (var part in SplitList(raw))
                {
                    if (!long.TryParse(part, out var lv))
                    {
                        error = $"Bad int in {name}: '{part}'";
                        return false;
                    }

                    values.Add(lv);
                }

                ChunkDataEditor.SetLongArray(f, values);
                return true;
            }
            case ChunkFieldType.FloatArray:
            case ChunkFieldType.DoubleArray:
            {
                var values = new List<double>();
                foreach (var part in SplitList(raw))
                {
                    if (!double.TryParse(part, NumberStyles.Float, culture, out var dvv))
                    {
                        error = $"Bad number in {name}: '{part}'";
                        return false;
                    }

                    values.Add(dvv);
                }

                ChunkDataEditor.SetDoubleArray(f, values);
                return true;
            }
            case ChunkFieldType.BoolArray:
            {
                var values = new List<bool>();
                foreach (var part in SplitList(raw))
                {
                    if (!TryParseBool(part, out var bvv))
                    {
                        error = $"Bad bool in {name}: '{part}'";
                        return false;
                    }

                    values.Add(bvv);
                }

                ChunkDataEditor.SetBoolArray(f, values);
                return true;
            }
            case ChunkFieldType.Tree:
            case ChunkFieldType.ItemStack:
            case ChunkFieldType.TreeArray:
                // Containers: edited through their nested rows.
                return true;
            default:
                // Opaque bytes (a mod's raw tree): not a value, they survive untouched.
                return true;
        }
    }

    public static bool TryParseBool(string text, out bool value)
    {
        if (bool.TryParse(text, out value)) return true;
        if (text == "1") { value = true; return true; }
        if (text == "0") { value = false; return true; }
        value = false;
        return false;
    }

    public static List<string> SplitList(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? new List<string>()
            : raw.Split(',').Select(x => x.Trim()).ToList();

    public static bool TryParseHex(string text, out byte[] bytes, out string? error)
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
}
