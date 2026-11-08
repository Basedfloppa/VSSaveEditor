using System.Collections.Concurrent;
using System.Text;

namespace VSCore;

/// <summary>
///     Strings of one scan, deduplicated by value. A server save repeats the same few thousand tokens
///     (block-entity types, attribute keys, item codes) tens of millions of times: on the 6.3-GB test
///     save 32M token references held 12k distinct values. Without the pool every occurrence is its own
///     string object, so the model retains hundreds of megabytes of identical text.
///
///     Deliberately not <see cref="string.Intern"/>: that table lives for the whole process and would pin
///     the vocabulary of every save ever opened until the editor exits. This pool is owned by one
///     <see cref="SaveScanner.Scan"/> call; the strings it returns stay alive through the model, the
///     dictionary itself is collected together with the scan.
/// </summary>
internal sealed class StringPool
{
    private readonly ConcurrentDictionary<string, string> _map = new(StringComparer.Ordinal);

    public string Intern(string value) => _map.GetOrAdd(value, value);

    /// <summary>Interns a char span without allocating on a hit. The scanner handles untrusted lengths,
    /// so the caller decides what may go through this path.</summary>
    public string Intern(ReadOnlySpan<char> chars)
    {
        var lookup = _map.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(chars, out var existing)) return existing;
        var value = new string(chars);
        return _map.GetOrAdd(value, value);
    }

    /// <summary>An ASCII run of a blob (tokens and codes are ASCII by construction). Short runs are
    /// decoded into a stack buffer: a hit allocates nothing, a miss only the final string.</summary>
    public string InternAscii(byte[] bytes, int start, int length) => InternAscii(bytes.AsSpan(), start, length);

    /// <summary>The span overload: the scanner reads entity/block-entity payloads straight from the chunk
    /// blob, without copying them into a byte[] first.</summary>
    public string InternAscii(ReadOnlySpan<byte> bytes, int start, int length)
    {
        if ((uint)start > (uint)bytes.Length || (uint)length > (uint)(bytes.Length - start))
            return "";
        if (length <= 256)
        {
            Span<char> chars = stackalloc char[length];
            for (var i = 0; i < length; i++) chars[i] = (char)bytes[start + i];
            return Intern(chars);
        }
        return Intern(Encoding.ASCII.GetString(bytes.Slice(start, length)));
    }
}
