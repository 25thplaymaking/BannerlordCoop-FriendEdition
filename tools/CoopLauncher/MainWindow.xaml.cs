using System.IO;
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
    private string? _bannerlordExe;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(12) };

    private SolidColorBrush Gold => (SolidColorBrush)FindResource("Gold");
    private SolidColorBrush Steel => (SolidColorBrush)FindResource("Steel");
    private SolidColorBrush Parchment => (SolidColorBrush)FindResource("Parchment");

    public MainWindow() : this(shootMode: false) { }

    public MainWindow(bool shootMode)
    {
        InitializeComponent();

        var configPath = Path.Combine(AppContext.BaseDirectory, "launcher-config.json");
        _config = LauncherConfig.Load(configPath);
        TitleText.Text = _config.GroupName;
        Title = _config.GroupName;

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

        _bannerlordExe = GameLocator.FindBannerlordExe(_config.GamePath);
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

        await RunUpdateAsync();

        JoinButton.IsEnabled = true;
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

    private async Task RunUpdateAsync()
    {
        var modulesDir = _bannerlordExe is null ? null : GameLocator.FindModulesDir(_bannerlordExe);
        if (modulesDir is null) return;

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
    }

    private void OnJoinClicked(object sender, RoutedEventArgs e)
    {
        if (_bannerlordExe is null) return;
        try
        {
            JoinButton.IsEnabled = false;
            JoinButton.Content = "RIDING OUT…";
            GameLauncher.Launch(_bannerlordExe, _config);
            // Bannerlord owns the screen now; step out of the way.
            _statusTimer.Stop();
            Close();
        }
        catch (Exception ex)
        {
            JoinButton.IsEnabled = true;
            JoinButton.Content = "MARCH TO WAR";
            UpdateText.Foreground = Steel;
            UpdateText.Text = $"Couldn't launch Bannerlord — {ex.Message}";
        }
    }
}
