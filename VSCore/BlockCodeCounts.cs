using System.Collections;

namespace VSCore;

/// <summary>
///     Per-chunk block histogram: block id -> number of cells. A server save has millions of chunk rows
///     with only a handful of distinct blocks in each, and a <see cref="Dictionary{TKey,TValue}" /> per row
///     costs several times the payload (buckets, entries, the dictionary object itself). The scan writes
///     the histogram as packed <c>(id &lt;&lt; 16) | count</c> words — a chunk has at most 32 768 cells, so a
///     count always fits 16 bits — and only materialises code strings when someone enumerates it.
///
///     The string indexer and the manual path exist for the UI/tests, which build a model without a scan:
///     a loaded count still behaves like the old dictionary. A scan always goes through
///     <see cref="Reset" /> + <see cref="Add" /> and never allocates strings per chunk.
/// </summary>
public sealed class BlockCodeCounts : IReadOnlyDictionary<string, long>
{
    private BlockRegistry? _registry;
    private uint[] _entries = Array.Empty<uint>();
    private int _count;
    private int _mergedCount = -1;

    // Only for models assembled by hand (tests, fixtures): these keys were never block ids.
    private Dictionary<string, long>? _manual;

    public int Count
    {
        get
        {
            if (_manual is not null) return _manual.Count;
            if (!MergesByCode) return _count;
            if (_mergedCount < 0) _mergedCount = Merged().Count;
            return _mergedCount;
        }
    }

    /// <summary>Two block ids can carry the same code in a modded save. The scan still stores ids (packed),
    /// and this is where the duplicate codes are summed — only when someone reads the histogram, never
    /// as a dictionary per chunk.</summary>
    private bool MergesByCode => _registry?.HasDuplicateCodes == true;

    private Dictionary<string, long> Merged()
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < _count; i++)
        {
            var code = CodeOf(_entries[i]);
            map.TryGetValue(code, out var value);
            map[code] = value + CountOf(_entries[i]);
        }
        return map;
    }

    public IEnumerable<string> Keys
    {
        get
        {
            if (_manual is not null)
                foreach (var k in _manual.Keys) yield return k;
            else if (MergesByCode)
                foreach (var k in Merged().Keys) yield return k;
            else
                for (var i = 0; i < _count; i++) yield return CodeOf(_entries[i]);
        }
    }

    public IEnumerable<long> Values
    {
        get
        {
            if (_manual is not null)
                foreach (var v in _manual.Values) yield return v;
            else if (MergesByCode)
                foreach (var v in Merged().Values) yield return v;
            else
                for (var i = 0; i < _count; i++) yield return CountOf(_entries[i]);
        }
    }

    public long this[string key]
    {
        get => TryGetValue(key, out var value)
            ? value
            : throw new KeyNotFoundException($"block code not in this chunk: '{key}'");
        set
        {
            if (_manual is null)
            {
                _manual = new Dictionary<string, long>(StringComparer.Ordinal);
                _count = 0;
                _registry = null;
            }
            _manual[key] = value;
        }
    }

    /// <summary>Binds the histogram to the save's block registry and reserves room for <paramref name="capacity" />
    /// distinct ids. Called once per chunk by the scanner before the ids are appended.</summary>
    internal void Reset(BlockRegistry? registry, int capacity)
    {
        _registry = registry;
        _manual = null;
        if (_entries.Length < capacity) _entries = new uint[capacity];
        _count = 0;
        _mergedCount = -1;
    }

    /// <summary>Appends one distinct block id. The scanner feeds a per-thread histogram whose ids are
    /// already unique, so no merge is needed here.</summary>
    internal void Add(ushort id, long count)
    {
        if (_manual is not null)
            throw new InvalidOperationException("a hand-built histogram cannot be mixed with packed ids");
        if (count is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(count), count, "a chunk has at most 32768 cells");
        if (_count == _entries.Length)
            Array.Resize(ref _entries, Math.Max(4, _count * 2));
        _entries[_count++] = ((uint)id << 16) | (uint)count;
        _mergedCount = -1;
    }

    public bool ContainsKey(string key) => TryGetValue(key, out _);

    public bool TryGetValue(string key, out long value)
    {
        if (_manual is not null) return _manual.TryGetValue(key, out value);
        if (MergesByCode)
        {
            value = 0;
            var found = false;
            for (var i = 0; i < _count; i++)
            {
                if (!string.Equals(CodeOf(_entries[i]), key, StringComparison.Ordinal)) continue;
                value += CountOf(_entries[i]);
                found = true;
            }
            return found;
        }
        for (var i = 0; i < _count; i++)
        {
            if (!string.Equals(CodeOf(_entries[i]), key, StringComparison.Ordinal)) continue;
            value = CountOf(_entries[i]);
            return true;
        }
        value = 0;
        return false;
    }

    public IEnumerator<KeyValuePair<string, long>> GetEnumerator()
    {
        if (_manual is not null)
        {
            foreach (var kv in _manual) yield return kv;
            yield break;
        }
        if (MergesByCode)
        {
            foreach (var kv in Merged()) yield return kv;
            yield break;
        }
        for (var i = 0; i < _count; i++)
            yield return new KeyValuePair<string, long>(CodeOf(_entries[i]), CountOf(_entries[i]));
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private string CodeOf(uint packed) => _registry?.CodeOf((int)(packed >> 16)) ?? "id:" + (packed >> 16);
    private static long CountOf(uint packed) => packed & 0xFFFF;
}
