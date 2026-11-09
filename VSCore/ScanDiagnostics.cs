using System.Diagnostics;

namespace VSCore;

/// <summary>
///     Runtime diagnostics of the scan path. Most failures here are per-record and best effort — a damaged
///     playerdata blob must not cancel the scan — but they must not vanish either: when a user reports
///     "the scan is wrong", the console is the only place where the reason can be seen.
/// </summary>
public static class ScanDiagnostics
{
    /// <summary>Optional extra sink (the editor installs one that also shows the line in the status bar).</summary>
    public static Action<string>? Sink { get; set; }

    public static void Warn(string what, Exception? ex = null) => Emit("WARN", what, ex);

    public static void Error(string what, Exception? ex = null) => Emit("ERROR", what, ex);

    /// <summary>Short progress/piece of state that explains a slow or partial run (no exception attached).</summary>
    public static void Info(string what) => Emit("INFO", what, null);

    private static void Emit(string level, string what, Exception? ex)
    {
        // Parallel.For wraps whatever the body throws into an AggregateException, so the header line would
        // read "One or more errors occurred. (…)" and the real cause would sit only in the printed stack.
        // The innermost exception is what names the failure; the full chain still goes to stderr below.
        var cause = AppLog.Cause(ex);
        var line = $"[scan][{level}] {what}" + (cause is null ? "" : $": {cause.GetType().Name}: {cause.Message}");
        // Console first and unconditionally: the headless probes and a terminal launch have nothing else,
        // and an editor started from a desktop icon simply loses the line instead of failing.
        try
        {
            Console.Error.WriteLine(line);
            if (ex is not null) Console.Error.WriteLine(ex);
        }
        catch
        {
            // a closed or redirected console must not turn a warning into a failure
        }
        Debug.WriteLine(line);
        try
        {
            Sink?.Invoke(line);
        }
        catch
        {
            // the sink belongs to the UI; its failure is not the scan's
        }
    }
}
