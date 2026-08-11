using System.Windows;
using System.Windows.Threading;
using CoopLauncher.Services;

namespace CoopLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A render/layout throw (e.g. an unresolved resource) used to kill the process and leave a
        // painted-but-dead window, so a friend's click just "did nothing". Catch it: log the real
        // cause, tell them where the log is, and keep the window alive instead of a silent crash.
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write($"UNHANDLED UI EXCEPTION: {ex.Exception}");
            MessageBox.Show(
                $"The launcher hit an error:\n\n{ex.Exception.Message}\n\nDetails were written to:\n{Log.Path}",
                "Calradia Co-op — launcher error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
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
