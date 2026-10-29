using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace VSSaveEditor;

public class App : Application
{
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

            // Global safety nets (P11p). In Avalonia 11.0.10 Dispatcher has no UnhandledException event
            // (it arrived in 11.1), so the main protection is try/catch in every async void
            // window handler (OnOpen/OnSearch/OnSaveEditsToCopy/LoadSave/RunScanAsync/OnApplyHex/OnCardApply),
            // while here it is logging and clearing stuck flags for background tasks (async Task).
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Trace.TraceError("Unhandled domain exception: " + e.ExceptionObject);
            // An exception thrown by a UI event handler closes the window unless it is caught here: a scope typed
            // into the "Search in" row (already parse-checked before use) must land in the status line, not in a crash.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                Trace.TraceError("Unhandled UI exception: " + e.Exception);
                window.ReportUnhandled(e.Exception);
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                e.SetObserved(); // do not bring the process down over an unobserved Task
                Trace.TraceError("Unobserved task exception: " + e.Exception);
                window.ReportUnhandled(e.Exception);
            };

            // support for starting with a file: VSSaveEditor <save.vcdbs>
            if (desktop.Args is { Length: > 0 } args && !string.IsNullOrEmpty(args[0]))
                window.OpenStartupFile(args[0]);
        }

        base.OnFrameworkInitializationCompleted();
    }
}