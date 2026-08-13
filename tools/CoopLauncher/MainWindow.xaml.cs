using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CoopLauncher.Services;

namespace CoopLauncher;

public partial class MainWindow : Window
{
    private readonly LauncherConfig _config;
    private readonly ArmoryUpdateCoordinator _updates;
    private readonly bool _continuePreparation;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(12) };

    private string? _bannerlordExe;
    private string? _modulesDir;
    private ArmorySnapshot? _snapshot;
    private bool _operationActive;
    private bool _preparationFailed;

    private SolidColorBrush Gold => (SolidColorBrush)FindResource("Gold");
    private SolidColorBrush Steel => (SolidColorBrush)FindResource("Steel");
    private SolidColorBrush Parchment => (SolidColorBrush)FindResource("Parchment");

    public MainWindow() : this(shootMode: false, continuePreparation: false) { }

    public MainWindow(bool shootMode, bool continuePreparation = false)
    {
        InitializeComponent();

        _continuePreparation = continuePreparation;

        string configPath = Path.Combine(AppContext.BaseDirectory, "launcher-config.json");
        _config = LauncherConfig.Load(configPath);
        _updates = new ArmoryUpdateCoordinator(_config);
        TitleText.Text = _config.GroupName;
        Title = _config.GroupName;
        ServerPasswordBox.Password = _config.ServerPassword;

        if (shootMode)
        {
            SetStatus(online: true, $"ONLINE — {_config.ServerHost}:{_config.ServerPort}");
            LauncherUpdateValue.Text = "Current — 2026.8.12.8";
            SuiteUpdateValue.Text = "Current — 2026.08.12.0218";
            ClientUpdateValue.Text = "Current — 2026.08.12.1517";
            ArmoryHeadline.Text = "YOUR ARMY IS READY";
            ArmoryDetail.Text = "Every required component is verified current.";
            JoinButton.Content = "MARCH TO WAR";
            JoinButton.IsEnabled = true;
            UpdateText.Text = "All update scrolls verified.";
            return;
        }

        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        CloseButton.Click += (_, _) => Close();
        JoinButton.Click += OnPrimaryClicked;
        GatherLogsButton.Click += OnGatherLogsClicked;
        _statusTimer.Tick += async (_, _) => await OnStatusTickAsync();

        Loaded += OnLoaded;
    }

    /// <summary>Renders the window's visual tree to a PNG without showing it (design review only).</summary>
    public void RenderToFile(string path)
    {
        var root = (Visual)Content;
        var element = (FrameworkElement)root;
        var size = new Size(Width, Height);
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
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

        _bannerlordExe = GameLocator.FindBannerlordExe(_config.GamePath);
        Log.Write(_bannerlordExe is null
            ? $"Bannerlord.exe NOT found (configured gamePath='{_config.GamePath}')"
            : $"Found Bannerlord.exe: {_bannerlordExe}");
        if (_bannerlordExe is null)
        {
            SetStatus(online: false, "Bannerlord not found — set gamePath in launcher-config.json");
            SetGameMissingState();
            return;
        }

        _modulesDir = GameLocator.FindModulesDir(_bannerlordExe);
        if (_modulesDir is null)
        {
            SetStatus(online: false, "Bannerlord Modules folder not found");
            SetGameMissingState();
            return;
        }

        await RefreshStatusAsync();
        _statusTimer.Start();
        await CheckArmoryAsync();

        if (_continuePreparation &&
            _snapshot?.PrimaryAction == ArmoryPrimaryAction.Prepare)
            await PrepareArmyAsync();
    }

    private void SetGameMissingState()
    {
        LauncherUpdateValue.Text = "Not checked";
        SuiteUpdateValue.Text = "Could not inspect";
        ClientUpdateValue.Text = "Could not inspect";
        ArmoryHeadline.Text = "THE MUSTER GROUND IS MISSING";
        ArmoryDetail.Text = "Bannerlord must be located before the army can be verified.";
        UpdateText.Text = "Set gamePath in launcher-config.json or repair the Steam installation.";
        JoinButton.Content = "GAME NOT FOUND";
        JoinButton.IsEnabled = false;
    }

    private async Task CheckArmoryAsync()
    {
        if (_operationActive || _modulesDir is null) return;

        _operationActive = true;
        _preparationFailed = false;
        SetCheckingState();
        try
        {
            Version currentVersion =
                Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            _snapshot = await _updates.CheckAsync(currentVersion, _modulesDir);
            LogSnapshot(_snapshot);
        }
        catch (Exception ex)
        {
            Log.Write($"Armory check failed unexpectedly: {ex}");
            _snapshot = UnexpectedFailureSnapshot(
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0");
        }
        finally
        {
            _operationActive = false;
            if (_snapshot is not null) RenderSnapshot(_snapshot);
        }
    }

    private async Task PrepareArmyAsync()
    {
        if (_operationActive || _snapshot is null || _modulesDir is null ||
            _snapshot.PrimaryAction != ArmoryPrimaryAction.Prepare)
            return;

        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            _preparationFailed = true;
            ArmoryHeadline.Text = "THE QUARTERMASTER LOST THE LAUNCHER";
            ArmoryDetail.Text = "The launcher executable could not be located. Reopen it and try again.";
            JoinButton.Content = "TRY PREPARING AGAIN";
            JoinButton.IsEnabled = true;
            return;
        }

        _operationActive = true;
        _preparationFailed = false;
        bool restarting = false;
        JoinButton.Content = "PREPARING YOUR ARMY…";
        JoinButton.IsEnabled = false;
        UpdateBar.Visibility = Visibility.Visible;
        UpdateBar.IsIndeterminate = true;
        UpdateText.Text = "Re-reading the royal update scrolls…";

        try
        {
            Version currentVersion =
                Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            ArmoryPreparationResult result = await _updates.PrepareAsync(
                executablePath,
                currentVersion,
                _modulesDir,
                (component, fraction, message) => Dispatcher.Invoke(() =>
                    RenderProgress(component, fraction, message)));

            _snapshot = result.Snapshot;
            Log.Write($"Army preparation: {result.Outcome} — {result.Message}");
            if (result.Outcome == ArmoryPreparationOutcome.Restarting)
            {
                restarting = true;
                JoinButton.Content = "RESTARTING THE MUSTER…";
                UpdateText.Text = result.Message;
                Close();
                return;
            }

            if (result.Outcome == ArmoryPreparationOutcome.Completed)
            {
                CompletePreparation(result.Snapshot);
                return;
            }

            if (result.Outcome == ArmoryPreparationOutcome.Unverified)
            {
                CompletePreparation(result.Snapshot);
                return;
            }

            _preparationFailed = true;
            RenderSnapshot(result.Snapshot);
            ArmoryHeadline.Text = "THE QUARTERMASTER FUMBLED THE CRATES";
            ArmoryDetail.Text = result.Message;
            JoinButton.Content = "TRY PREPARING AGAIN";
            JoinButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Log.Write($"Army preparation failed unexpectedly: {ex}");
            _preparationFailed = true;
            ArmoryHeadline.Text = "THE QUARTERMASTER FUMBLED THE CRATES";
            ArmoryDetail.Text = "The installed army was kept intact. Try preparing again.";
            UpdateText.Text = $"Preparation failed. Details: {Log.Path}";
            JoinButton.Content = "TRY PREPARING AGAIN";
            JoinButton.IsEnabled = true;
        }
        finally
        {
            if (!restarting)
            {
                _operationActive = false;
                UpdateBar.IsIndeterminate = false;
            }
        }
    }

    internal void CompletePreparation(ArmorySnapshot snapshot)
    {
        _operationActive = false;
        RenderSnapshot(snapshot);
    }

    private void SetCheckingState()
    {
        LauncherUpdateValue.Text = "Checking…";
        SuiteUpdateValue.Text = "Checking…";
        ClientUpdateValue.Text = "Checking…";
        LauncherUpdateValue.Foreground = Steel;
        SuiteUpdateValue.Foreground = Steel;
        ClientUpdateValue.Foreground = Steel;
        ArmoryHeadline.Text = "CHECKING THE ARMORY";
        ArmoryDetail.Text = "Reading all three royal update scrolls.";
        UpdateBar.Visibility = Visibility.Visible;
        UpdateBar.IsIndeterminate = true;
        UpdateText.Text = "No files are downloaded during this check.";
        JoinButton.Content = "CHECKING THE ARMORY…";
        JoinButton.IsEnabled = false;
    }

    private void RenderSnapshot(ArmorySnapshot snapshot)
    {
        // Reset the dispatch colour to its default: a prior gather-logs success/failure sets it to
        // Gold/Steel and neither RenderSnapshot nor RenderProgress restored it, so the tint bled between states.
        UpdateText.Foreground = Steel;
        RenderComponent(LauncherUpdateValue, snapshot.Launcher.Status);
        RenderComponent(SuiteUpdateValue, snapshot.Mods.SuiteStatus);
        RenderComponent(ClientUpdateValue, snapshot.Mods.ClientStatus);

        ArmoryHeadline.Text = ArmoryHeadlineFor(snapshot);
        ArmoryDetail.Text = ArmoryDetailFor(snapshot);
        UpdateBar.IsIndeterminate = false;
        UpdateBar.Value = 0;
        UpdateBar.Visibility = Visibility.Collapsed;

        if (snapshot.PrimaryAction == ArmoryPrimaryAction.RetryCheck)
        {
            string details = string.Join("  ", snapshot.Components
                .Where(item => item.State == ComponentUpdateState.Unverified)
                .Select(item => $"{item.Label}: {item.Detail}"));
            UpdateText.Text = details;
        }
        else if (snapshot.PrimaryAction == ArmoryPrimaryAction.Prepare)
        {
            ComponentUpdateStatus? noted = snapshot.Components.FirstOrDefault(item =>
                item.State == ComponentUpdateState.UpdateAvailable &&
                !string.IsNullOrWhiteSpace(item.Notes));
            UpdateText.Text = noted?.Notes ?? "Verified updates are ready to install.";
        }
        else
        {
            UpdateText.Text = "All update scrolls verified.";
        }

        JoinButton.Content = _preparationFailed
            ? "TRY PREPARING AGAIN"
            : PrimaryButtonText(snapshot.PrimaryAction);
        JoinButton.IsEnabled = !_operationActive;
    }

    private void RenderComponent(TextBlock target, ComponentUpdateStatus status)
    {
        target.Text = ComponentStatusText(status);
        target.Foreground = status.State switch
        {
            ComponentUpdateState.Current => Parchment,
            ComponentUpdateState.UpdateAvailable => Gold,
            _ => Steel,
        };
    }

    private void RenderProgress(ArmoryComponent component, double fraction, string message)
    {
        UpdateText.Foreground = Steel;
        UpdateBar.Visibility = Visibility.Visible;
        UpdateBar.IsIndeterminate = fraction < 0;
        if (fraction >= 0) UpdateBar.Value = fraction;
        UpdateText.Text = message;

        ComponentUpdateStatus? status = _snapshot?.Components.FirstOrDefault(item =>
            item.Component == component);
        TextBlock target = component switch
        {
            ArmoryComponent.Launcher => LauncherUpdateValue,
            ArmoryComponent.ModSuite => SuiteUpdateValue,
            _ => ClientUpdateValue,
        };
        target.Text = status?.AvailableVersion is { Length: > 0 } version
            ? $"Updating to {version}…"
            : "Updating…";
        target.Foreground = Gold;
    }

    internal static string PrimaryButtonText(ArmoryPrimaryAction action) => action switch
    {
        ArmoryPrimaryAction.Launch => "MARCH TO WAR",
        ArmoryPrimaryAction.Prepare => "PREPARE YOUR ARMY",
        _ => "TRY THE JESTER AGAIN",
    };

    internal static string ArmoryHeadlineFor(ArmorySnapshot snapshot) =>
        !snapshot.IsVerified
            ? "THE COURT JESTER IS ASLEEP"
            : snapshot.HasUpdates
                ? "YOUR ARMY NEEDS PREPARATION"
                : "YOUR ARMY IS READY";

    internal static string ArmoryDetailFor(ArmorySnapshot snapshot) =>
        !snapshot.IsVerified
            ? "The royal update scrolls cannot be reached. Wake the jester and try again."
            : snapshot.HasUpdates
                ? "Verified updates are waiting. Prepare your army before marching."
                : "Every required component is verified current.";

    internal static string ComponentStatusText(ComponentUpdateStatus status) => status.State switch
    {
        ComponentUpdateState.Unverified => "Could not verify",
        ComponentUpdateState.UpdateAvailable when string.IsNullOrWhiteSpace(status.InstalledVersion) =>
            $"Not installed  →  {status.AvailableVersion}",
        ComponentUpdateState.UpdateAvailable =>
            $"{status.InstalledVersion}  →  {status.AvailableVersion}",
        _ => $"Current — {status.InstalledVersion ?? status.AvailableVersion ?? "unknown"}",
    };

    internal static bool CanDispatchPrimaryAction(
        bool operationActive,
        ArmorySnapshot? snapshot) =>
        !operationActive && snapshot is not null;

    private static ArmorySnapshot UnexpectedFailureSnapshot(string launcherVersion)
    {
        ComponentUpdateStatus Failed(ArmoryComponent component, string label, string? installed) =>
            new(component, label, installed, null, string.Empty,
                ComponentUpdateState.Unverified, $"{label} check failed unexpectedly.");

        return new ArmorySnapshot(
            new LauncherUpdateCheck(
                Failed(ArmoryComponent.Launcher, "Launcher", launcherVersion), null),
            new ModUpdateCheck(
                Failed(ArmoryComponent.ModSuite, "Mod suite", null), null,
                Failed(ArmoryComponent.CoopClient, "Co-op client", null), null));
    }

    private static void LogSnapshot(ArmorySnapshot snapshot)
    {
        foreach (ComponentUpdateStatus component in snapshot.Components)
        {
            Log.Write(
                $"Armory {component.Label}: {component.State}; " +
                $"installed={component.InstalledVersion ?? "missing"}; " +
                $"available={component.AvailableVersion ?? "unverified"}; {component.Detail}");
        }
    }

    private async void OnPrimaryClicked(object sender, RoutedEventArgs e)
    {
        if (!CanDispatchPrimaryAction(_operationActive, _snapshot)) return;

        switch (_snapshot!.PrimaryAction)
        {
            case ArmoryPrimaryAction.RetryCheck:
                await CheckArmoryAsync();
                break;
            case ArmoryPrimaryAction.Prepare:
                await PrepareArmyAsync();
                break;
            case ArmoryPrimaryAction.Launch:
                await LaunchGameAsync();
                break;
        }
    }

    private async void OnGatherLogsClicked(object sender, RoutedEventArgs e)
    {
        GatherLogsButton.IsEnabled = false;
        object previous = GatherLogsButton.Content;
        GatherLogsButton.Content = "GATHERING LOGS…";
        try
        {
            LogPackageResult result = await Task.Run(() => LogPackager.Package(_bannerlordExe));
            UpdateText.Foreground = result.Success ? Gold : Steel;
            if (result.Success && result.ZipPath is not null)
            {
                LogPackager.RevealInExplorer(result.ZipPath);
                UpdateText.Text = $"Packaged {result.FileCount} log(s) → {result.ZipPath}  —  send this zip to Bishop.";
            }
            else
            {
                UpdateText.Text = result.Message;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Gather logs threw: {ex}");
            UpdateText.Foreground = Steel;
            UpdateText.Text = $"Couldn't gather logs — {ex.Message}";
        }
        finally
        {
            GatherLogsButton.Content = previous;
            GatherLogsButton.IsEnabled = true;
        }
    }

    private void UnfurlBanner()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var unfurl = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BannerScale.BeginAnimation(ScaleTransform.ScaleYProperty, unfurl);
    }

    /// <summary>
    /// Fires on the status timer: always refresh the server pill, and — when the last armory check
    /// came back unverified (the jester screen, usually a transient GitHub blip) — quietly re-run it so
    /// the launcher self-heals the moment the feeds recover, without the member clicking retry.
    /// </summary>
    private async Task OnStatusTickAsync()
    {
        await RefreshStatusAsync();
        if (!_operationActive && _snapshot is { PrimaryAction: ArmoryPrimaryAction.RetryCheck })
            await CheckArmoryAsync();
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
        SolidColorBrush accent = online ? Gold : Steel;
        StatusGlyph.Foreground = accent;
        StatusText.Foreground = online ? Parchment : Steel;
        StatusText.Text = text;
        Sigil.Foreground = accent;
    }

    private async Task LaunchGameAsync()
    {
        if (_bannerlordExe is null || _snapshot?.PrimaryAction != ArmoryPrimaryAction.Launch)
            return;

        _operationActive = true;
        try
        {
            JoinButton.IsEnabled = false;
            JoinButton.Content = "RIDING OUT…";

            bool steamUp = GameLauncher.IsSteamRunning();
            if (!steamUp) Log.Write("WARNING: Steam client does not appear to be running");

            var process = GameLauncher.Launch(
                _bannerlordExe, _config, ServerPasswordBox.Password);

            bool exitedFast = await Task.Run(() => process.WaitForExit(9000));
            if (exitedFast)
            {
                Log.Write(
                    $"Bannerlord exited within 9s (code 0x{process.ExitCode:X}) — launch did not take");
                _operationActive = false;
                JoinButton.IsEnabled = true;
                JoinButton.Content = "MARCH TO WAR";
                UpdateText.Foreground = Steel;
                UpdateText.Text = steamUp
                    ? $"Bannerlord closed immediately — check your game version. Log: {Log.Path}"
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
            _operationActive = false;
            JoinButton.IsEnabled = true;
            JoinButton.Content = "MARCH TO WAR";
            UpdateText.Foreground = Steel;
            UpdateText.Text = $"Couldn't launch — {ex.Message}. Log: {Log.Path}";
        }
    }
}
