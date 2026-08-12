using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CoopLauncher.Services;

namespace CoopLauncher;

public partial class MainWindow : Window
{
    private readonly LauncherConfig _config;
    private readonly bool _skipLauncherUpdate;
    private string? _bannerlordExe;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(12) };

    private SolidColorBrush Gold => (SolidColorBrush)FindResource("Gold");
    private SolidColorBrush Steel => (SolidColorBrush)FindResource("Steel");
    private SolidColorBrush Parchment => (SolidColorBrush)FindResource("Parchment");

    public MainWindow() : this(shootMode: false, skipLauncherUpdate: false) { }

    public MainWindow(bool shootMode, bool skipLauncherUpdate = false)
    {
        InitializeComponent();

        _skipLauncherUpdate = skipLauncherUpdate;

        var configPath = Path.Combine(AppContext.BaseDirectory, "launcher-config.json");
        _config = LauncherConfig.Load(configPath);
        TitleText.Text = _config.GroupName;
        Title = _config.GroupName;
        ServerPasswordBox.Password = _config.ServerPassword;

        if (shootMode)
        {
            // Static, representative "host online, ready to ride" look for the offscreen render.
            SetStatus(online: true, $"ONLINE — {_config.ServerHost}:{_config.ServerPort}");
            JoinButton.IsEnabled = true;
            UpdateText.Text = "Up to date (build 2026.08.10)";
            return;
        }

        TitleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        CloseButton.Click += (_, _) => Close();
        JoinButton.Click += OnJoinClicked;

        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();

        Loaded += OnLoaded;
    }

    /// <summary>Renders the window's visual tree to a PNG without showing it (design review only).</summary>
    public void RenderToFile(string path)
    {
        var root = (System.Windows.Media.Visual)Content;
        var element = (FrameworkElement)root;
        var size = new Size(Width, Height);
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Width, (int)Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(element);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UnfurlBanner();
        Log.Begin();

        if (!_skipLauncherUpdate)
        {
            LauncherUpdateResult launcherUpdate = await RunLauncherUpdateAsync();
            if (launcherUpdate.Outcome == LauncherUpdateOutcome.Restarting)
            {
                JoinButton.Content = "RESTARTING…";
                JoinButton.IsEnabled = false;
                Close();
                return;
            }
            if (!CanContinueAfterLauncherUpdate(launcherUpdate))
            {
                JoinButton.Content = "UPDATE REQUIRED";
                JoinButton.IsEnabled = false;
                return;
            }
        }

        _bannerlordExe = GameLocator.FindBannerlordExe(_config.GamePath);
        Log.Write(_bannerlordExe is null
            ? $"Bannerlord.exe NOT found (configured gamePath='{_config.GamePath}')"
            : $"Found Bannerlord.exe: {_bannerlordExe}");
        if (_bannerlordExe is null)
        {
            SetStatus(online: false, "Bannerlord not found — set gamePath in launcher-config.json");
            JoinButton.Content = "GAME NOT FOUND";
            JoinButton.IsEnabled = false;
            return;
        }

        // First status read, then keep it live so the banner reflects the host without a relaunch.
        await RefreshStatusAsync();
        _statusTimer.Start();

        var update = await RunUpdateAsync();
        JoinButton.IsEnabled = CanJoinAfterUpdate(update);
        if (!JoinButton.IsEnabled)
            JoinButton.Content = "UPDATE REQUIRED";
    }

    private async Task<LauncherUpdateResult> RunLauncherUpdateAsync()
    {
        string? executablePath = Environment.ProcessPath;
        Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return new(LauncherUpdateOutcome.Failed, "launcher executable path was unavailable");

        UpdateBar.Visibility = Visibility.Visible;
        UpdateBar.IsIndeterminate = true;
        var updater = new LauncherSelfUpdater(_config);
        LauncherUpdateResult result = await updater.CheckAndStageAsync(
            executablePath, currentVersion, (fraction, message) => Dispatcher.Invoke(() =>
            {
                UpdateBar.IsIndeterminate = fraction < 0;
                if (fraction >= 0) UpdateBar.Value = fraction;
                UpdateText.Text = message;
            }));

        UpdateBar.IsIndeterminate = false;
        UpdateBar.Value = result.Outcome == LauncherUpdateOutcome.Restarting ? 1 : 0;
        if (result.Outcome is LauncherUpdateOutcome.Disabled or LauncherUpdateOutcome.UpToDate)
            UpdateBar.Visibility = Visibility.Collapsed;
        UpdateText.Text = result.Message;
        Log.Write($"Launcher update: {result.Outcome} — {result.Message}");
        return result;
    }

    private void UnfurlBanner()
    {
        if (!SystemParameters.ClientAreaAnimation) return;   // respect reduced-motion
        var unfurl = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BannerScale.BeginAnimation(ScaleTransform.ScaleYProperty, unfurl);
    }

    private async Task RefreshStatusAsync()
    {
        bool online = await ServerProbe.IsOnlineAsync(_config.ServerHost, _config.ServerPort);
        SetStatus(online, online
            ? $"ONLINE — {_config.ServerHost}:{_config.ServerPort}"
            : $"OFFLINE — {_config.ServerHost}:{_config.ServerPort}");
    }

    private void SetStatus(bool online, string text)
    {
        var accent = online ? Gold : Steel;
        StatusGlyph.Foreground = accent;
        StatusText.Foreground = online ? Parchment : Steel;
        StatusText.Text = text;
        Sigil.Foreground = accent;   // the banner's sigil is the at-a-glance host indicator
    }

    private async Task<UpdateResult> RunUpdateAsync()
    {
        var modulesDir = _bannerlordExe is null ? null : GameLocator.FindModulesDir(_bannerlordExe);
        if (modulesDir is null)
        {
            var missing = new UpdateResult(UpdateOutcome.Failed, "Modules folder not found — update required");
            UpdateText.Text = missing.Message;
            return missing;
        }

        UpdateBar.Visibility = Visibility.Visible;
        UpdateBar.IsIndeterminate = false;

        var updater = new ModUpdater(_config);
        var result = await updater.RunAsync(modulesDir, (fraction, msg) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (fraction < 0)
                {
                    UpdateBar.IsIndeterminate = true;
                }
                else
                {
                    UpdateBar.IsIndeterminate = false;
                    UpdateBar.Value = fraction;
                }
                UpdateText.Text = msg;
            });
        });

        UpdateBar.IsIndeterminate = false;
        UpdateBar.Value = result.Outcome == UpdateOutcome.Updated ? 1 : 0;
        if (result.Outcome is UpdateOutcome.Disabled or UpdateOutcome.UpToDate)
            UpdateBar.Visibility = Visibility.Collapsed;
        UpdateText.Text = result.Message;
        return result;
    }

    internal static bool CanJoinAfterUpdate(UpdateResult result) => result.Outcome != UpdateOutcome.Failed;

    internal static bool CanContinueAfterLauncherUpdate(LauncherUpdateResult result) =>
        result.Outcome != LauncherUpdateOutcome.Failed;

    private async void OnJoinClicked(object sender, RoutedEventArgs e)
    {
        if (_bannerlordExe is null) return;
        try
        {
            JoinButton.IsEnabled = false;
            JoinButton.Content = "RIDING OUT…";

            bool steamUp = GameLauncher.IsSteamRunning();
            if (!steamUp) Log.Write("WARNING: Steam client does not appear to be running");

            var proc = GameLauncher.Launch(_bannerlordExe, _config, ServerPasswordBox.Password);

            // Catch an instant exit (failed Steam init, a crash) so the launcher explains it instead of
            // just vanishing — the classic "I hit play and nothing happened".
            bool exitedFast = await Task.Run(() => proc.WaitForExit(9000));
            if (exitedFast)
            {
                Log.Write($"Bannerlord exited within 9s (code 0x{proc.ExitCode:X}) — launch did not take");
                JoinButton.IsEnabled = true;
                JoinButton.Content = "MARCH TO WAR";
                UpdateText.Foreground = Steel;
                UpdateText.Text = steamUp
                    ? $"Bannerlord closed immediately — check your game is v1.4.7. Log: {Log.Path}"
                    : "Bannerlord closed immediately — start Steam first, then try again.";
                return;
            }

            Log.Write("Bannerlord still running after 9s — handing off, closing launcher");
            _statusTimer.Stop();
            Close();
        }
        catch (Exception ex)
        {
            Log.Write($"Launch threw: {ex}");
            JoinButton.IsEnabled = true;
            JoinButton.Content = "MARCH TO WAR";
            UpdateText.Foreground = Steel;
            UpdateText.Text = $"Couldn't launch — {ex.Message}. Log: {Log.Path}";
        }
    }
}
