using System;
using System.Collections.Generic;
using System.Text;

namespace VSCore;

/// <summary>
/// Materials of "chiselled" blocks (BlockEntityMicroBlock / BlockEntityChisel).
///
/// In the world such a block is stored under one shared block id (`chiseledblock` / `microblock`), while what
/// it was cut from lives in the block entity: the <c>posx/posy/posz</c> fields (Int32) and <c>materials</c>.
/// The materials array comes in two forms (depending on version/mod):
///   type-byte 0x0B — Int32 array of block ids;
///   type-byte 0x0A — array of LP strings with block codes ("rock-peridotite").
/// The tail of the blob holds types we do not parse (`emitSideAo`, `sideSolid`, …), so the fields we need
/// are located by key name, checking the preceding <c>[type-byte][key length]</c>.
/// </summary>
public static class ChiselMaterials
{
    /// <summary>Type-byte of an Int32 array (count: int32, then count int32 values).</summary>
    public const byte IntArrayType = 0x0B;

    /// <summary>Type-byte of an LP-string array (count: int32, then count strings).</summary>
    public const byte StringArrayType = 0x0A;

    private const byte Int32Type = 0x01;

    /// <summary>Reads the position and materials of a chiselled block. false — if this is not a chiselled block
    /// or the required fields were not found (the caller then uses a neutral colour).</summary>
    public static bool TryRead(byte[]? beBlob, BlockRegistry? reg, out int wx, out int wy, out int wz, out int[] materials)
    {
        wx = wy = wz = 0;
        materials = Array.Empty<int>();
        if (beBlob is null || beBlob.Length < 8) return false;

        // Block entity header: an LP string with the type name ("Chisel" / "MicroBlock")
        int nameLen = beBlob[0];
        if (nameLen <= 0 || 1 + nameLen > beBlob.Length) return false;
        var name = Encoding.ASCII.GetString(beBlob, 1, nameLen);
        if (!IsChiselLike(name)) return false;
        int body = 1 + nameLen;

        bool hasX = false, hasY = false, hasZ = false;
        var ids = Array.Empty<int>();
        for (int i = body + 2; i + 1 < beBlob.Length; i++)
        {
            byte first = beBlob[i];
            if (first != (byte)'p' && first != (byte)'m') continue;   // posx/posy/posz/materials
            if (MatchKey(beBlob, i, "posx", Int32Type)) { wx = BitConverter.ToInt32(beBlob, i + 4); hasX = true; }
            else if (MatchKey(beBlob, i, "posy", Int32Type)) { wy = BitConverter.ToInt32(beBlob, i + 4); hasY = true; }
            else if (MatchKey(beBlob, i, "posz", Int32Type)) { wz = BitConverter.ToInt32(beBlob, i + 4); hasZ = true; }
            else if (MatchKey(beBlob, i, "materials", IntArrayType)) ids = ReadIntArray(beBlob, i + 9);
            else if (MatchKey(beBlob, i, "materials", StringArrayType)) ids = ResolveCodes(ReadStringArray(beBlob, i + 9), reg);
            if (hasX && hasY && hasZ && ids.Length > 0) break;
        }

        if (!hasX || !hasZ || ids.Length == 0) return false;
        materials = ids;
        return true;
    }

    private static bool IsChiselLike(string name)
        => name.Contains("chisel", StringComparison.OrdinalIgnoreCase)
        || name.Contains("microblock", StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks that key sits at offset i and is preceded by [type-byte][key length].</summary>
    private static bool MatchKey(byte[] b, int i, string key, byte type)
    {
        if (i < 2 || i + key.Length > b.Length) return false;
        if (b[i - 1] != (byte)key.Length || b[i - 2] != type) return false;
        for (int j = 0; j < key.Length; j++) if (b[i + j] != (byte)key[j]) return false;
        return true;
    }

    private static int[] ReadIntArray(byte[] b, int p)
    {
        if (p + 4 > b.Length) return Array.Empty<int>();
        int n = BitConverter.ToInt32(b, p);
        if (n <= 0 || (long)n * 4 > b.Length - p - 4) return Array.Empty<int>();
        var ids = new int[n];
        for (int k = 0; k < n; k++) ids[k] = BitConverter.ToInt32(b, p + 4 + k * 4);
        return ids;
    }

    private static string[] ReadStringArray(byte[] b, int p)
    {
        if (p + 4 > b.Length) return Array.Empty<string>();
        int n = BitConverter.ToInt32(b, p);
        if (n <= 0 || n > 4096) return Array.Empty<string>();
        p += 4;
        var res = new string[n];
        for (int k = 0; k < n; k++)
        {
            if (p >= b.Length) return Array.Empty<string>();
            int len = b[p++];
            if (p + len > b.Length) return Array.Empty<string>();
            res[k] = Encoding.UTF8.GetString(b, p, len);
            p += len;
        }
        return res;
    }

    /// <summary>Material codes -> block id (the first match; a domain such as "game:" is dropped).</summary>
    private static int[] ResolveCodes(string[] codes, BlockRegistry? reg)
    {
        if (reg is null || codes.Length == 0) return Array.Empty<int>();
        var ids = new List<int>(codes.Length);
        foreach (var c in codes)
        {
            if (string.IsNullOrEmpty(c)) continue;
            var list = reg.IdsOf(c);
            if (list.Count == 0)
            {
                int colon = c.IndexOf(':');
                if (colon >= 0 && colon + 1 < c.Length) list = reg.IdsOf(c.Substring(colon + 1));
            }
            if (list.Count > 0) ids.Add(list[0]);
        }
        return ids.ToArray();
    }
}

