using System.Windows;
using System.Windows.Threading;
using CoopLauncher.Services;

namespace CoopLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A render/layout throw (e.g. an unresolved resource) used to leave a painted-but-dead
        // window, so a friend's click just "did nothing". Surface the real cause, but fail fast:
        // arbitrary WPF dispatcher faults do not leave the UI in a trustworthy state.
        var unhandledExceptionReporter = new UnhandledUiExceptionReporter(
            Log.Write,
            (title, message) =>
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            },
            Log.Path);
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = unhandledExceptionReporter.Report(ex.Exception);
        };

        // Offscreen design-review render: `--shoot <path>` writes a PNG of the window without ever
        // showing it (no focus steal), then exits. Not part of the shipped launch flow.
        int shootIdx = Array.FindIndex(e.Args, a => a == "--shoot");
        if (shootIdx >= 0 && shootIdx + 1 < e.Args.Length)
        {
            var window = new MainWindow(shootMode: true);
            window.RenderToFile(e.Args[shootIdx + 1]);
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }
}
