using VSCore;
using Xunit;

namespace VSCore.Tests;

public sealed class AppLogTests
{
    [Fact]
    public void An_aggregate_names_the_real_cause_not_the_task_wrapper()
    {
        // What the finalizer rethrows for an unobserved Task is wrapped twice; its own message names nothing
        // ("A Task's exception(s) were not observed …"), so the line must quote the innermost exception.
        var faulted = Task.FromException(new InvalidOperationException("The name is not activatable"));
        var wrapper = faulted.Exception!;
        var line = AppLog.Describe("ERROR", "background task failed", wrapper);
        Assert.Equal("[app][ERROR] background task failed: InvalidOperationException: The name is not activatable", line);
        Assert.DoesNotContain("were not observed", line);
    }

    [Fact]
    public void A_repeating_line_is_counted_instead_of_filling_the_console()
    {
        var lines = Capture(() =>
        {
            for (var i = 0; i < 4; i++)
                AppLog.Error("background task failed", new InvalidOperationException("The name is not activatable"));
            AppLog.Warn("something else happened");
        });

        // one occurrence of the repeated line, one note with the count, then the different line
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("[app][ERROR] background task failed", lines[0]);
        Assert.Equal("[app][INFO] the previous line repeated 3 more time(s)", lines[1]);
        Assert.Equal("[app][WARN] something else happened", lines[2]);
    }

    [Fact]
    public void A_line_without_an_exception_still_reaches_the_sink()
    {
        var lines = Capture(() => AppLog.Info("window closed"));
        Assert.Equal("[app][INFO] window closed", Assert.Single(lines));
    }

    [Fact]
    public void The_counter_does_not_leak_between_different_failures()
    {
        var lines = Capture(() =>
        {
            AppLog.Error("first", new InvalidOperationException("a"));
            AppLog.Error("second", new InvalidOperationException("b"));
        });
        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain(lines, l => l.Contains("repeated"));
    }


    [Fact]
    public void A_missing_dbus_service_is_platform_noise_not_an_app_failure()
    {
        // What Avalonia produces on a machine without xdg-desktop-portal: an unawaited D-Bus task faults, and
        // the finalizer rethrows it wrapped. It is not the editor's failure and there is nothing to fix.
        var dbus = new DBusException("org.freedesktop.DBus.Error.ServiceUnknown: The name is not activatable");
        Assert.True(AppLog.IsPlatformNoise(new AggregateException(dbus)));
        // ... and the ordinary failures still go through the error path
        Assert.False(AppLog.IsPlatformNoise(new InvalidOperationException("The name is not activatable")));
        Assert.False(AppLog.IsPlatformNoise(new DBusException("Connection refused")));
        Assert.False(AppLog.IsPlatformNoise(null));
    }

    [Fact]
    public void Platform_noise_is_reported_once_per_run_and_without_a_stack()
    {
        var lines = Capture(() =>
        {
            var ex = new AggregateException(new DBusException(
                "org.freedesktop.DBus.Error.ServiceUnknown: The name is not activatable"));
            for (var i = 0; i < 5; i++) AppLog.PlatformNoise("platform: no desktop D-Bus service", ex);
        });

        var line = Assert.Single(lines);
        Assert.StartsWith("[app][WARN] platform: no desktop D-Bus service", line);
        Assert.Contains("DBusException", line);
        Assert.DoesNotContain("at Tmds", line); // no stack: the line has to stay one line
    }

    /// <summary>A stand-in for Tmds.DBus.Protocol.DBusException: the check keys off the type name and message.</summary>
    private sealed class DBusException(string message) : Exception(message);

    private static List<string> Capture(Action emit)
    {
        var previous = AppLog.Sink;
        var lines = new List<string>();
        AppLog.Reset();
        AppLog.Sink = lines.Add;
        var err = Console.Error;
        Console.SetError(TextWriter.Null); // stderr also goes to the console; keep the test output clean
        try
        {
            emit();
        }
        finally
        {
            Console.SetError(err);
            AppLog.Sink = previous;
            AppLog.Reset();
        }

        return lines;
    }
}
