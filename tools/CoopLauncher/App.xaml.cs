using System.Windows;

namespace CoopLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
