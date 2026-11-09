using VSCore;
using Xunit;

namespace VSCore.Tests;

public sealed class ScanDiagnosticsTests
{
    [Fact]
    public void An_aggregate_from_parallel_for_names_the_real_cause()
    {
        // Parallel.For wraps a body failure into an AggregateException; the header must name the cause,
        // not "One or more errors occurred".
        var wrapped = new AggregateException(new InvalidOperationException("Call from invalid thread"));
        var line = Capture(() => ScanDiagnostics.Error("scan failed after 2093 ms", wrapped));
        Assert.Contains("InvalidOperationException", line);
        Assert.Contains("Call from invalid thread", line);
        Assert.DoesNotContain("One or more errors occurred", line);
    }

    [Fact]
    public void A_nested_wrapper_chain_is_followed_to_the_bottom()
    {
        var nested = new AggregateException(
            new AggregateException(new InvalidOperationException("Call from invalid thread")));
        var line = Capture(() => ScanDiagnostics.Error("scan failed", nested));
        Assert.Contains("Call from invalid thread", line);
    }

    [Fact]
    public void A_message_without_an_exception_still_reaches_the_sink()
    {
        var line = Capture(() => ScanDiagnostics.Warn("playerdata id=12 could not be parsed"));
        Assert.Equal("[scan][WARN] playerdata id=12 could not be parsed", line);
    }

    private static string Capture(Action emit)
    {
        var previous = ScanDiagnostics.Sink;
        var line = "";
        ScanDiagnostics.Sink = l => line = l;
        try
        {
            // stderr also goes to the console; keep the test output clean without hiding a failure
            var err = Console.Error;
            Console.SetError(TextWriter.Null);
            try
            {
                emit();
            }
            finally
            {
                Console.SetError(err);
            }
        }
        finally
        {
            ScanDiagnostics.Sink = previous;
        }

        return line;
    }
}
