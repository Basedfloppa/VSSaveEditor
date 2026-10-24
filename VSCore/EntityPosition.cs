using System.Buffers.Binary;

namespace VSCore;

/// <summary>A float64 triple (x, y, z) found in an entity blob: its offset in the blob and the values.</summary>
public readonly record struct EntityPositionOccurrence(int Offset, double X, double Y, double Z);

/// <summary>
///     Entity position inside the field-6 blob. It is NOT a named ECX tree field: the game writes three
///     float64 values (x, y, z) straight into the byte stream, so we address them by offset. The offset is not
///     fixed (a variable-length attribute tree sits in front of it), so we find it by validation: the triple
///     must fall inside the AABB of the chunk whose record holds it. In real 1.22.7 saves such
///     a triple is usually not alone (two copies with the same value), so an edit writes all copies.
///
///     Why this is safe: the components are fixed at 8 bytes each, the blob length does not change and the
///     structure does not shift. The value is verified before writing — if the bytes do not match the expected
///     ones the edit is not applied (better to do nothing than to corrupt a foreign field).
/// </summary>
public static class EntityPositionCodec
{
    public const int TripleSize = 24;          // 3 × float64
    public const double Epsilon = 1e-6;        // tolerance for coordinate comparison

    /// <summary>
    ///     Finds in the blob all float64 triples that fall inside the chunk AABB (with a margin of
    ///     <paramref name="margin" /> blocks: an entity at a chunk border may sit slightly outside it).
    /// </summary>
    public static List<EntityPositionOccurrence> Locate(byte[] entityBlob, int chunkWorldX, int chunkWorldY,
        int chunkWorldZ, int margin = 96)
    {
        var res = new List<EntityPositionOccurrence>();
        if (entityBlob.Length < TripleSize) return res;
        for (var i = 0; i + TripleSize <= entityBlob.Length; i++)
        {
            var x = BitConverter.ToDouble(entityBlob, i);
            var y = BitConverter.ToDouble(entityBlob, i + 8);
            var z = BitConverter.ToDouble(entityBlob, i + 16);
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z)) continue;
            if (!InRange(x, chunkWorldX, margin)) continue;
            if (!InRange(z, chunkWorldZ, margin)) continue;
            if (!InYRange(y, chunkWorldY)) continue;
            res.Add(new EntityPositionOccurrence(i, x, y, z));
        }

        return res;
    }

    private static bool InRange(double v, int origin, int margin) => v >= origin - margin && v <= origin + 32 + margin;

    private static bool InYRange(double y, int chunkWorldY)
    {
        // In VS, Y spans up to 511 chunks (9 bits in the chunk position), that is up to ~16k blocks; a separate
        // per-chunk check does not help here (an entity may hang above its own chunk).
        _ = chunkWorldY;
        return y is >= -1024 and <= 16384;
    }

    /// <summary>All found triples describe the same point (copies of one value).</summary>
    public static bool AllAgree(IReadOnlyList<EntityPositionOccurrence> found)
    {
        if (found.Count == 0) return false;
        var first = found[0];
        return found.All(f => Same(f.X, first.X) && Same(f.Y, first.Y) && Same(f.Z, first.Z));
    }

    public static bool Same(double a, double b) => Math.Abs(a - b) <= Epsilon;

    /// <summary>Reads the position at an offset.</summary>
    public static (double X, double Y, double Z) Read(byte[] blob, int offset)
    {
        if (offset < 0 || offset + TripleSize > blob.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return (BinaryPrimitives.ReadDoubleLittleEndian(blob.AsSpan(offset)),
            BinaryPrimitives.ReadDoubleLittleEndian(blob.AsSpan(offset + 8)),
            BinaryPrimitives.ReadDoubleLittleEndian(blob.AsSpan(offset + 16)));
    }

    /// <summary>
    ///     Writes the new position into every supplied copy. Before writing it checks that the blob holds
    ///     exactly the expected old value (otherwise the offset is stale — the blob is already different). Returns
    ///     the number of overwritten copies; 0 — none matched, the blob is untouched.
    /// </summary>
    public static int Patch(byte[] blob, IReadOnlyList<EntityPositionOccurrence> copies,
        double x, double y, double z, bool requireOldValue = true)
    {
        var written = 0;
        foreach (var c in copies)
        {
            if (c.Offset < 0 || c.Offset + TripleSize > blob.Length) continue;
            var (ox, oy, oz) = Read(blob, c.Offset);
            if (requireOldValue && !(Same(ox, c.X) && Same(oy, c.Y) && Same(oz, c.Z))) continue;
            Write(blob, c.Offset, x, y, z);
            written++;
        }

        return written;
    }

    /// <summary>Writes a float64 triple in LE at an offset (the blob length does not change).</summary>
    public static void Write(byte[] blob, int offset, double x, double y, double z)
    {
        if (offset < 0 || offset + TripleSize > blob.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(offset), x);
        BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(offset + 8), y);
        BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(offset + 16), z);
    }
}
