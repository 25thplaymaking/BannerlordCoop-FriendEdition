using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CoopLauncher.Services;

namespace CoopLauncher;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Two launchers racing the same install directory is what left players with a
        // half-replaced module: one held the module files open while the other tried to
        // replace them. Only one may run at a time.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\CalradiaCoop.Launcher", out bool isOnly);
        if (!isOnly && e.Args.FirstOrDefault() != LauncherUpdateCommand.ApplySwitch)
        {
            MessageBox.Show(
                "The Europe 1100 Co-op launcher is already running. Use that window instead.",
                "Europe 1100 Co-op",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

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

        string runningExecutable = Environment.ProcessPath ??
            System.IO.Path.Combine(AppContext.BaseDirectory, "CalradiaCoop.exe");
        if (e.Args.FirstOrDefault() == LauncherUpdateCommand.ApplySwitch)
        {
            Log.Begin();
            if (LauncherUpdateApplier.TryParseApplyArguments(
                    e.Args, runningExecutable, out LauncherApplyRequest? apply))
            {
                LauncherApplyResult result = LauncherUpdateApplier.ApplyAndRelaunch(apply!);
                Log.Write(result.Message);
            }
            else
            {
                Log.Write("Rejected malformed launcher apply arguments.");
            }
            Shutdown();
            return;
        }

        LauncherCompletionRequest? completion = null;
        if (e.Args.FirstOrDefault() == LauncherUpdateApplier.CompletionSwitch &&
            !LauncherUpdateApplier.TryParseCompletionArguments(e.Args, runningExecutable, out completion))
        {
            Log.Write("Rejected malformed launcher completion arguments.");
            completion = null;
        }

        // Offscreen design-review render: `--shoot <path>` writes a PNG of the window without ever
        // showing it (no focus steal), then exits. Not part of the shipped launch flow.
        int shootIdx = Array.FindIndex(e.Args, a => a == "--shoot");
        if (shootIdx >= 0 && shootIdx + 1 < e.Args.Length)
        {
            var window = new MainWindow(shootMode: true);
            window.RenderAllPanels(e.Args[shootIdx + 1]);
            Shutdown();
            return;
        }

        bool continuePreparation = completion?.ContinuePreparation == true &&
                                   !LauncherUpdateApplier.ShouldSkipSelfUpdate(e.Args);
        new MainWindow(shootMode: false, continuePreparation).Show();
        if (completion is not null)
            _ = Task.Run(() => LauncherUpdateApplier.CleanupAfterStartup(completion));
    }
}
