namespace VSCore.Format;

/// <summary>
///     Rebuilds a blob by cutting and replacing byte ranges in ONE pass. Every editor of a shared blob (the chunk
///     blob, a playerdata row, the world message) needs the same three rules, and they used to be written four
///     times with small differences:
///     ranges are applied in a single pass, because after the first range that changes length every later offset
///     would be stale; a range that starts inside the previous one is skipped as already covered; bytes outside
///     every range are copied exactly as they were.
/// </summary>
public static class ByteSplice
{
    /// <summary>An edit that puts <paramref name="Replacement" /> in the place of [start, end). A null
    /// replacement only cuts, and start == end inserts at that offset (how a missing field is appended).</summary>
    public readonly record struct Edit(int Start, int End, byte[]? Replacement);

    /// <summary>Range to cut out.</summary>
    public static Edit Cut(int start, int end) => new(start, end, null);

    /// <summary>Range to replace.</summary>
    public static Edit Replace(int start, int end, byte[] replacement) => new(start, end, replacement);

    /// <summary>
    ///     Applies the edits. Ranges outside the blob are ignored (an entry read from another blob must never
    ///     cut into this one) and overlapping ranges are tolerated instead of throwing: the caller has already
    ///     validated what it could, and a partially applied splice is still a correct blob.
    /// </summary>
    public static byte[] Apply(byte[] blob, IEnumerable<Edit> edits)
    {
        var list = new List<Edit>();
        foreach (var e in edits)
            if (e.Start >= 0 && e.End >= e.Start && e.End <= blob.Length) list.Add(e);
        if (list.Count == 0) return blob;
        list.Sort((a, b) => a.Start.CompareTo(b.Start));

        var ms = new MemoryStream(blob.Length + 64);
        int pos = 0;
        foreach (var e in list)
        {
            if (e.Start < pos) continue; // overlaps the previous edit: already covered by it
            ms.Write(blob, pos, e.Start - pos);
            if (e.Replacement is { Length: > 0 } replacement) ms.Write(replacement, 0, replacement.Length);
            pos = e.End;
        }

        ms.Write(blob, pos, blob.Length - pos);
        return ms.ToArray();
    }
}
