namespace VSSaveEditor;

/// <summary>
///     A message channel. Separate channels exist so that the source can be read
///     (diagnostics, headless probes) even when another message occupies the single status line.
/// </summary>
public enum StatusChannel
{
    Scan,
    App,
    Chunk,
    Search,
    Filter,
    Selection,
    Hover
}

/// <summary>
///     One status line for the whole application (P1-7). Sources write each to its own channel; the line
///     shows the message with the highest priority, and on a tie the one written last.
///     A message has a lifetime, after which it stops occupying the line - otherwise
///     a "Scan complete" shown once would block the live line under the cursor forever (its priority
///     is the lowest). The text itself is NOT lost: <see cref="TextOf" /> keeps the latest message of
///     the channel, so diagnostics and headless probes read the state, not whatever has already faded.
/// </summary>
public sealed class AppStatus
{
    /// <summary>
    ///     An error is not a separate channel but the highest priority: the message stays in its own
    ///     channel (diagnostics read it where they did before) yet overrides everything on the line.
    /// </summary>
    public const int PriorityError = 100;

    private readonly Dictionary<StatusChannel, Entry> _entries = new();
    private readonly Func<DateTime> _now;
    private long _seq;
    private string _shown = "";

    public AppStatus(Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Whether a timer is worth running: a message with a limited lifetime still occupies the line.</summary>
    public bool NeedsTimer
    {
        get
        {
            var now = _now();
            return _entries.Values.Any(e => e.ExpiresAt != DateTime.MaxValue && e.ExpiresAt > now);
        }
    }

    /// <summary>Text of the single line: highest priority among NOT expired, and on a tie the latest.</summary>
    public string Text => ComputeText(_now());

    public event Action? Changed;

    public static int DefaultPriority(StatusChannel channel)
    {
        return channel switch
        {
            StatusChannel.Scan => 85,
            StatusChannel.App => 75,
            StatusChannel.Selection => 65, // live feedback while dragging - above action results
            StatusChannel.Chunk => 60,
            StatusChannel.Search => 60,
            StatusChannel.Filter => 60,
            StatusChannel.Hover => 10,
            _ => 60
        };
    }

    /// <summary>0 means the message occupies the line until replaced or explicitly cleared.</summary>
    private static double DefaultTtlSeconds(StatusChannel channel)
    {
        return channel switch
        {
            StatusChannel.Hover => 0, // lives while the cursor is over the map
            StatusChannel.Selection => 5, // every selection change extends it
            _ => 15
        };
    }

    /// <summary>Writes a message to the channel; an empty string clears the channel.</summary>
    public void Set(StatusChannel channel, string text, double? ttlSeconds = null, int? priority = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            Clear(channel);
            return;
        }

        var prio = priority ?? DefaultPriority(channel);
        var ttl = ttlSeconds ?? (prio >= PriorityError ? 30 : DefaultTtlSeconds(channel));
        _entries[channel] = new Entry
        {
            Text = text,
            Priority = prio,
            ExpiresAt = ttl > 0 ? _now().AddSeconds(ttl) : DateTime.MaxValue,
            Seq = ++_seq
        };
        Refresh();
    }

    public void Clear(StatusChannel channel)
    {
        if (_entries.Remove(channel)) Refresh();
    }

    public void ClearAll()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        Refresh();
    }

    /// <summary>The latest message of the channel - regardless of whether it is visible on the line right now.</summary>
    public string TextOf(StatusChannel channel)
    {
        return _entries.TryGetValue(channel, out var e) ? e.Text : "";
    }

    private string ComputeText(DateTime now)
    {
        Entry? best = null;
        foreach (var e in _entries.Values)
        {
            if (e.ExpiresAt <= now) continue;
            if (best is null || e.Priority > best.Priority || (e.Priority == best.Priority && e.Seq > best.Seq))
                best = e;
        }

        return best?.Text ?? "";
    }

    /// <summary>
    ///     Recomputes the visible line at <paramref name="now" />: expired messages
    ///     stop occupying it but stay in the channel. Returns true when the visible text changed.
    ///     Not exposed publicly: the next timer tick recomputes the line from real time anyway.
    /// </summary>
    private bool Expire(DateTime now)
    {
        var text = ComputeText(now);
        if (string.Equals(text, _shown, StringComparison.Ordinal)) return false;
        _shown = text;
        Changed?.Invoke();
        return true;
    }

    public bool Expire()
    {
        return Expire(_now());
    }

    private void Refresh()
    {
        Expire(_now());
    }

    private sealed class Entry
    {
        public DateTime ExpiresAt = DateTime.MaxValue;
        public int Priority;
        public long Seq;
        public string Text = "";
    }
}