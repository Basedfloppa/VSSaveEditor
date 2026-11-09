using System.Diagnostics;

namespace VSCore;

/// <summary>
///     Console log for everything that escapes a handler: an exception thrown by a UI event, by the domain,
///     or by a background task nobody awaited. Those used to go to <see cref="Trace" /> only, which a GUI
///     launch never shows — the user saw a status line and no reason anywhere.
///     Lines go to stderr first and unconditionally: a headless probe and a terminal launch have nothing else.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _lastLine;
    private static int _repeats;
    private static int _platformNoiseReported;

    /// <summary>Optional extra sink (a probe or the editor's status bar can mirror the line).</summary>
    public static Action<string>? Sink { get; set; }

    public static void Error(string what, Exception? ex = null) => Emit("ERROR", what, ex);

    public static void Warn(string what, Exception? ex = null) => Emit("WARN", what, ex);

    public static void Info(string what) => Emit("INFO", what, null);

    /// <summary>
    ///     A failure of the desktop platform itself that the app cannot fix and does not depend on. On Linux
    ///     without xdg-desktop-portal or IBus every D-Bus call to those names ends with ServiceUnknown, and
    ///     Avalonia fires them from a task nobody awaits — so it surfaces here, mid-scan, looking like a crash
    ///     of the editor. It is neither: input and file dialogs simply fall back to the built-in ones.
    ///     Detection is deliberately narrow: a D-Bus exception whose message is about a missing name.
    /// </summary>
    public static bool IsPlatformNoise(Exception? ex)
    {
        var cause = Cause(ex);
        if (cause is null) return false;
        var type = cause.GetType().Name;
        var isDBus = type.Contains("DBus", StringComparison.OrdinalIgnoreCase)
                     || type.Contains("Portal", StringComparison.OrdinalIgnoreCase);
        if (!isDBus) return false;

        var message = cause.Message;
        return message.Contains("not activatable", StringComparison.OrdinalIgnoreCase)
               || message.Contains("ServiceUnknown", StringComparison.OrdinalIgnoreCase)
               || message.Contains("not provided by any .service files", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Reports a platform gap once per process, without a stack: the same message repeats on every
    ///     collection, and a hundred copies would bury the failures that are actually ours.
    /// </summary>
    public static bool PlatformNoise(string what, Exception? ex)
    {
        if (Interlocked.Exchange(ref _platformNoiseReported, 1) != 0) return false;
        var cause = Cause(ex);
        Warn(what + (cause is null ? "" : $" ({cause.GetType().Name}: {cause.Message})"));
        return true;
    }

    /// <summary>The log line without writing it: `[app][ERROR] what: ExceptionType: message`.</summary>
    public static string Describe(string level, string what, Exception? ex)
    {
        var cause = Cause(ex);
        return $"[app][{level}] {what}" + (cause is null ? "" : $": {cause.GetType().Name}: {cause.Message}");
    }

    /// <summary>
    ///     The innermost exception of a wrapper chain. A faulted Task surfaces wrapped twice — once by the
    ///     awaiter, once by the finalizer — so the reported message would be "A Task's exception(s) were not
    ///     observed … (…)", which names nothing. The deepest one carries the real cause.
    /// </summary>
    public static Exception? Cause(Exception? ex)
    {
        for (var depth = 0; depth < 16 && ex is not null; depth++)
        {
            if (ex is AggregateException { InnerExceptions.Count: > 0 } agg)
            {
                ex = agg.InnerExceptions[0];
                continue;
            }

            if (ex.InnerException is not null)
            {
                ex = ex.InnerException;
                continue;
            }

            break;
        }

        return ex;
    }

    /// <summary>Forgets the repeat counter (tests, so one case cannot swallow the next one's line).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _lastLine = null;
            _repeats = 0;
        }

        Interlocked.Exchange(ref _platformNoiseReported, 0);
    }

    private static void Emit(string level, string what, Exception? ex)
    {
        var line = Describe(level, what, ex);
        var note = (string?)null;
        lock (Gate)
        {
            // A DBus/finalizer failure repeats on every collection: the same line a hundred times would hide
            // everything else in the console. Identical consecutive lines are counted and reported once.
            if (string.Equals(line, _lastLine, StringComparison.Ordinal))
            {
                _repeats++;
                return;
            }

            if (_repeats > 0) note = $"[app][INFO] the previous line repeated {_repeats} more time(s)";
            _lastLine = line;
            _repeats = 0;
        }

        Write(note);
        Write(line);
        // The stack belongs to the first occurrence only: repeats are already collapsed into the note above.
        if (ex is not null) WriteRaw(ex.ToString());
    }

    private static void Write(string? line)
    {
        if (line is null) return;
        try
        {
            Console.Error.WriteLine(line);
        }
        catch
        {
            // a closed or redirected console must not turn a log line into a failure
        }

        Debug.WriteLine(line);
        try
        {
            Sink?.Invoke(line);
        }
        catch
        {
            // the sink belongs to the UI; its failure is not the failure being reported
        }
    }

    private static void WriteRaw(string text)
    {
        try
        {
            Console.Error.WriteLine(text);
        }
        catch
        {
            // same reason as in Write
        }

        Debug.WriteLine(text);
    }
}
