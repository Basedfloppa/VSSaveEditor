using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VSCore;

namespace VSSaveEditor;

public class App : Application
{
    private static bool _handlersInstalled;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            InstallGlobalHandlers(window);

            // support for starting with a file: VSSaveEditor <save.vcdbs>
            if (desktop.Args is { Length: > 0 } args && !string.IsNullOrEmpty(args[0]))
                window.OpenStartupFile(args[0]);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    ///     The global catch-all: every source of an otherwise invisible failure is routed here, and each one
    ///     writes to the console (<see cref="AppLog" />) before touching the UI. A GUI launch has no Trace
    ///     listener, so the previous Trace.TraceError left the user with a status line and no reason anywhere.
    ///     Exposed for the headless probe, which builds the window without a desktop lifetime.
    /// </summary>
    public static void InstallGlobalHandlers(MainWindow window)
    {
        if (_handlersInstalled) return; // subscribing twice would report every failure twice
        _handlersInstalled = true;

        // The process is ending: nothing can be repaired, but the reason must reach stderr.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("unhandled exception on the domain (the process is ending)",
                e.ExceptionObject as Exception);

        // A UI event handler that throws closes the window unless it is caught here: a scope typed into the
        // "Search in" row must land in the status line, not in a crash. A live failure - clear the busy flags
        // so the window stays usable.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            if (PlatformNoise(e.Exception)) return;
            AppLog.Error("unhandled exception in a UI handler", e.Exception);
            window.ReportUnhandled(e.Exception, resetBusy: true);
        };

        // A background task nobody awaited. It surfaces late - the finalizer thread rethrows it during a
        // collection, which is why it appears in the middle of an unrelated action (a scan allocates heavily,
        // so it is usually then). The task is already gone: there is nothing to repair, and clearing the busy
        // flags here would break the operation that is still running.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved(); // do not bring the process down over an unobserved Task
            if (PlatformNoise(e.Exception)) return;
            AppLog.Error("background task failed and nobody awaited it", e.Exception);
            window.ReportUnhandled(e.Exception, resetBusy: false);
        };
    }

    /// <summary>
    ///     A missing desktop D-Bus service (xdg-desktop-portal, IBus) is not a failure of the editor: Avalonia
    ///     asks for it from a task nobody awaits, and the app works without it. It gets one line in the console
    ///     per run and never touches the status line - otherwise "Unexpected error" in the middle of a scan
    ///     reads like a broken save file, which is exactly what it is not.
    /// </summary>
    private static bool PlatformNoise(Exception? ex)
    {
        if (!AppLog.IsPlatformNoise(ex)) return false;
        AppLog.PlatformNoise(
            "platform: no desktop D-Bus service for the portal/input method (xdg-desktop-portal or IBus is not " +
            "running); the editor continues without it", ex);
        return true;
    }
}
