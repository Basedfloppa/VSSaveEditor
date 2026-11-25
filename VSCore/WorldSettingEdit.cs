using System.Globalization;
using System.Text;
using VSCore.Format;

namespace VSCore;

/// <summary>
///     Writing one entry of the world tree back into `gamedata`. The tree is not rebuilt from the parsed list:
///     the entry's own bytes are cut and replaced, and the rest of the blob stays byte-for-byte as it was - a
///     mod's 53 KB JSON blob must not be re-serialized just because a number beside it changed.
///     Two splices are needed because the tree sits inside a length-delimited field: the entry inside the tree,
///     then the tree inside `gamedata`. Both lengths are LEB128 and are re-emitted, so a longer or shorter value
///     is the same case as an equal one.
/// </summary>
public static class WorldSettingEdit
{
    /// <summary>A mod may store a whole config file here; beyond this a text box is the wrong tool, not a save.</summary>
    public const int MaxStringBytes = 1 << 20;

    /// <summary>
    ///     Validates what was typed and returns it in the form the save uses (`true`, `512`, `1.5`, the string
    ///     exactly as typed). The dialog runs this on every keystroke: the action stays disabled and the reason
    ///     is shown before anything is written, not after.
    /// </summary>
    public static bool TryNormalize(WorldSettingKind kind, string text, out string canonical, out string? error)
    {
        canonical = text;
        error = null;
        switch (kind)
        {
            case WorldSettingKind.Integer:
            {
                if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                {
                    error = double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        ? $"'{text.Trim()}' is not a whole number - the save stores this as an int32"
                        : $"'{text.Trim()}' is not a number";
                    return false;
                }

                canonical = v.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            case WorldSettingKind.Float:
            {
                if (!float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                {
                    error = $"'{text.Trim()}' is not a number (write a dot: 0.5)";
                    return false;
                }

                if (!float.IsFinite(f))
                {
                    error = "the value must be a finite number";
                    return false;
                }

                canonical = f.ToString("0.#######", CultureInfo.InvariantCulture);
                return true;
            }
            case WorldSettingKind.Bool:
            {
                var t = text.Trim();
                if (bool.TryParse(t, out bool b))
                {
                    canonical = b ? "true" : "false";
                    return true;
                }

                var lower = t.ToLowerInvariant();
                if (lower is "1" or "yes" or "on")
                {
                    canonical = "true";
                    return true;
                }

                if (lower is "0" or "no" or "off")
                {
                    canonical = "false";
                    return true;
                }

                error = $"'{t}' is not a boolean - expected true or false";
                return false;
            }
            default:
            {
                if (text.Contains('\0'))
                {
                    error = "the value contains a zero byte, which the save cannot store";
                    return false;
                }

                int bytes = Encoding.UTF8.GetByteCount(text);
                if (bytes > MaxStringBytes)
                {
                    error = $"the value is {bytes:n0} bytes; this editor accepts up to {MaxStringBytes:n0}";
                    return false;
                }

                canonical = text;
                return true;
            }
        }
    }

    /// <summary>
    ///     Rebuilds `gamedata` with one entry set to <paramref name="text" />. <paramref name="changed" /> is
    ///     false when the stored value is already that value - the caller then skips the pending edit instead of
    ///     recording an undo step that changes nothing.
    /// </summary>
    public static bool TrySetValue(byte[] gamedata, WorldSetting setting, string text,
        out byte[] result, out bool changed, out string? error)
    {
        result = gamedata;
        changed = false;
        error = null;
        if (!TryNormalize(setting.Kind, text, out var canonical, out error)) return false;

        // The entry is located again in the blob we are about to write: the offsets on the parsed row belong to
        // the blob it was read from, and a second edit in the same session may have moved everything below it.
        var fresh = WorldSettings.Parse(gamedata);
        if (!fresh.CanEdit)
        {
            error = "this save's world tree cannot be rewritten - it was not located in gamedata";
            return false;
        }

        var target = fresh.Settings.FirstOrDefault(s => s.Index == setting.Index && s.Name == setting.Name && s.Kind == setting.Kind)
                     ?? fresh.Settings.FirstOrDefault(s => s.Name == setting.Name && s.Kind == setting.Kind && s.Value == setting.Value);
        if (target is null)
        {
            error = $"'{setting.Name}' is no longer in the world tree of this save";
            return false;
        }

        if (string.Equals(target.Value, canonical, StringComparison.Ordinal))
        {
            changed = false;
            return true;
        }

        if (!TryEncode(target.Kind, canonical, out var encoded, out error)) return false;

        var tree = fresh.Tree!;
        var newTree = ByteSplice.Apply(tree, [ByteSplice.Replace(target.ValuePrefixStart, target.ValueEnd, encoded)]);

        // The field that carries the tree is length-delimited, so a grown or shrunk tree means the length in front
        // of it changes too. Replacing only the payload would leave the old length behind and cut the last entry.
        var field = new MemoryStream(newTree.Length + 5);
        PbWriter.Varint(field, newTree.Length);
        field.Write(newTree);
        result = ByteSplice.Apply(gamedata,
            [ByteSplice.Replace(fresh.TreeHeaderStart, fresh.TreeOffset + fresh.TotalBytes, field.ToArray())]);
        changed = true;
        return true;
    }

    /// <summary>The bytes a value takes in the tree: for a string its length prefix plus UTF-8, otherwise the raw value.</summary>
    private static bool TryEncode(WorldSettingKind kind, string canonical, out byte[] encoded, out string? error)
    {
        error = null;
        switch (kind)
        {
            case WorldSettingKind.Integer:
                encoded = BitConverter.GetBytes(int.Parse(canonical, CultureInfo.InvariantCulture));
                return true;
            case WorldSettingKind.Float:
                encoded = BitConverter.GetBytes(float.Parse(canonical, NumberStyles.Float, CultureInfo.InvariantCulture));
                return true;
            case WorldSettingKind.Bool:
                encoded = [canonical == "true" ? (byte)1 : (byte)0];
                return true;
            default:
            {
                var bytes = Encoding.UTF8.GetBytes(canonical);
                var ms = new MemoryStream(bytes.Length + 5);
                PbWriter.Varint(ms, bytes.Length);
                ms.Write(bytes);
                encoded = ms.ToArray();
                return true;
            }
        }
    }
}

/// <summary>
///     Which settings the game reads only while it creates a world. The list comes from the world-config schema
///     the game itself ships (`VSSurvivalMod`, attributes marked `onlyDuringWorldCreate`) and is used for ONE
///     thing: the editor warns before such a value is changed, because the save will hold the new number while an
///     existing world keeps the terrain it already generated. It is not a constraint - the value is written either way.
/// </summary>
public static class WorldSettingNames
{
    public static readonly IReadOnlySet<string> CreationOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        "landformScale", "upheavelCommonness", "worldWidth", "worldLength", "worldClimate", "startingClimate",
        "oceanscale", "landcover", "geologicActivity", "globalTemperature", "globalPrecipitation",
        "globalForestation", "polarEquatorDistance", "storyStructuresDistScaling", "graceTimer"
    };

    public static bool IsCreationOnly(string name) => CreationOnly.Contains(name);
}
