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
    private readonly LauncherConfig _shipped;
    private readonly LauncherSettings _settings;
    private LauncherConfig _config;
    private ArmoryUpdateCoordinator _updates;
    private readonly bool _continuePreparation;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(12) };
    private PortalClient _portal;
    private IReadOnlyList<GameLocator.GameInstallation> _installations = [];
    private List<CampaignLordStats> _rosterSource = [];
    private readonly List<string> _reportImages = [];
    private CrashReportCandidate? _pendingCrashReport;

    private string? _bannerlordExe;
    private string? _modulesDir;
    private ArmorySnapshot? _snapshot;
    private bool _operationActive;
    private bool _preparationFailed;
    private bool _optionsWiring;
    private readonly bool _shootMode;

    private SolidColorBrush Gold => (SolidColorBrush)FindResource("Gold");
    private SolidColorBrush Steel => (SolidColorBrush)FindResource("Steel");
    private SolidColorBrush Parchment => (SolidColorBrush)FindResource("Parchment");

    public MainWindow() : this(shootMode: false, continuePreparation: false) { }

    public MainWindow(bool shootMode, bool continuePreparation = false)
    {
        InitializeComponent();

        _continuePreparation = continuePreparation;

        string configPath = Path.Combine(AppContext.BaseDirectory, "launcher-config.json");
        _shipped = LauncherConfig.Load(configPath);
        _settings = LauncherSettings.Load(LauncherSettings.DefaultPath);
        _config = _settings.ApplyTo(_shipped);
        _updates = new ArmoryUpdateCoordinator(_config);
        _portal = new PortalClient(_config.PortalUrl, _config.PortalManifestUrl);
        TitleText.Text = _config.GroupName;
        Title = _config.GroupName;

        // The remembered watchword (opt-in, DPAPI) wins over the shipped config's token; both
        // fall back to empty and the member types it.
        string remembered = _settings.RememberPassword ? _settings.UnprotectPassword() : "";
        ServerPasswordBox.Password = remembered.Length > 0 ? remembered : _config.ServerPassword;

        if (shootMode)
        {
            _shootMode = true;
            SetStatus(online: true, $"ONLINE — {_config.ServerHost}:{_config.ServerPort}");
            LauncherUpdateValue.Text = "Current — 2026.8.12.8";
            SuiteUpdateValue.Text = "Current — 2026.08.12.0218";
            ClientUpdateValue.Text = "Current — 2026.08.12.1517";
            ArmoryHeadline.Text = "YOUR ARMY IS READY";
            ArmoryDetail.Text = "Every required component is verified current.";
            JoinButton.Content = "MARCH TO WAR";
            JoinButton.IsEnabled = true;
            UpdateText.Text = "All update scrolls verified.";
            ChronicleEmptyText.Visibility = Visibility.Collapsed;
            ChronicleList.ItemsSource = new List<ChronicleEntry>
            {
                new()
                {
                    Version = "2026.08.14.0001", Date = "2026-08-14", Title = "The join freeze is dead",
                    Highlights =
                    {
                        "Joining the server no longer freezes on the \"Applying patches...\" screen.",
                        "Patch failures now show a real error instead of a frozen loading screen.",
                    },
                },
                new()
                {
                    Version = "2026.08.13.2356", Date = "2026-08-13", Title = "Co-op client 2026.08.13.2356",
                    Source = "build",
                    Highlights = { "Skip the second unpatchable generic target: MCM settings getter" },
                },
            };
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

        MusterTab.Checked += (_, _) => ShowPanel(MusterPanel);
        ChronicleTab.Checked += async (_, _) =>
        {
            ShowPanel(ChroniclePanel);
            if (!_shootMode) await LoadChronicleAsync();
        };
        RosterTab.Checked += async (_, _) =>
        {
            ShowPanel(RosterPanel);
            await LoadRosterAsync();
        };
        RosterSearchBox.TextChanged += (_, _) => ApplyRosterFilters();
        RosterScopePicker.SelectionChanged += (_, _) => ApplyRosterFilters();
        RosterStatusPicker.SelectionChanged += (_, _) => ApplyRosterFilters();
        RosterSortPicker.SelectionChanged += (_, _) => ApplyRosterFilters();
        RosterRefreshButton.Click += async (_, _) => await LoadRosterAsync();
        RosterGrid.SelectionChanged += (_, _) => ShowSelectedLordDetails();
        OptionsTab.Checked += (_, _) => ShowPanel(OptionsPanel);
        WireOptions();

        Loaded += OnLoaded;
    }

    private void ShowPanel(FrameworkElement active)
    {
        if (MusterPanel is null) return; // Checked can fire during InitializeComponent.
        MusterPanel.Visibility = ReferenceEquals(active, MusterPanel) ? Visibility.Visible : Visibility.Collapsed;
        ChroniclePanel.Visibility = ReferenceEquals(active, ChroniclePanel) ? Visibility.Visible : Visibility.Collapsed;
        RosterPanel.Visibility = ReferenceEquals(active, RosterPanel) ? Visibility.Visible : Visibility.Collapsed;
        OptionsPanel.Visibility = ReferenceEquals(active, OptionsPanel) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Renders every panel to PNGs without showing the window (design review only):
    /// <c>path</c> gets the Muster, plus <c>-chronicle</c> / <c>-options</c> siblings.
    /// </summary>
    public void RenderAllPanels(string path)
    {
        string Sibling(string suffix)
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            return Path.Combine(dir,
                Path.GetFileNameWithoutExtension(path) + suffix + Path.GetExtension(path));
        }

        // Tab handlers are not wired in shoot mode, so drive both the check state (for the gold
        // underline) and the panel visibility directly.
        MusterTab.IsChecked = true;
        ShowPanel(MusterPanel);
        RenderToFile(path);
        ChronicleTab.IsChecked = true;
        ShowPanel(ChroniclePanel);
        RenderToFile(Sibling("-chronicle"));
        RosterTab.IsChecked = true;
        ShowPanel(RosterPanel);
        RenderToFile(Sibling("-roster"));
        OptionsTab.IsChecked = true;
        ShowPanel(OptionsPanel);
        RenderToFile(Sibling("-options"));
        MusterTab.IsChecked = true;
        ShowPanel(MusterPanel);
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
        SpawnEmbers();
        Log.Begin();

        RefreshGameInstallations();
        Log.Write(_bannerlordExe is null
            ? $"Bannerlord.exe NOT found (configured gamePath='{_config.GamePath}')"
            : $"Found Bannerlord.exe: {_bannerlordExe}");
        if (_bannerlordExe is null)
        {
            SetStatus(online: false, "Bannerlord not found — set the game path in OPTIONS");
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

        PromptForPendingCrashReport();

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
        UpdateText.Text = "Set the game path in the OPTIONS tab or repair the Steam installation.";
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
            if (_snapshot is not null)
            {
                RenderSnapshot(_snapshot);
                // Feed the Chronicle's build-note history so uncurated builds still appear there.
                Chronicle.RecordBuildNotes(_snapshot);
            }
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
        RenderGameVersionState(snapshot);
    }

    private string RequiredGameVersion =>
        string.IsNullOrWhiteSpace(_snapshot?.Mods.ClientManifest?.GameVersion)
            ? _config.RequiredGameVersion
            : _snapshot!.Mods.ClientManifest!.GameVersion;

    private void RenderGameVersionState(ArmorySnapshot? snapshot = null)
    {
        string required = RequiredGameVersion;
        RequiredGameVersionText.Text = $"Host requires Bannerlord {required}";
        GameLocator.GameInstallation? selected = GameVersionPicker.SelectedItem as GameLocator.GameInstallation;
        bool match = selected is not null && GameLocator.VersionsMatch(selected.Version, required);
        RequiredGameVersionText.Foreground = match ? Gold : Steel;
        if (snapshot?.PrimaryAction == ArmoryPrimaryAction.Launch && !match)
        {
            ArmoryHeadline.Text = "THE GAME VERSION DOES NOT MATCH";
            ArmoryDetail.Text = selected is null
                ? $"Select a Bannerlord {required} installation in Options."
                : $"Selected {selected.Version}; this host requires {required}. Select the matching installation in Options.";
            JoinButton.Content = $"SELECT BANNERLORD {required}";
            JoinButton.IsEnabled = false;
        }
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
            OptionsStatusText.Foreground = result.Success ? Gold : Steel;
            if (result.Success && result.ZipPath is not null)
            {
                LogPackager.RevealInExplorer(result.ZipPath);
                OptionsStatusText.Text = $"Packaged {result.FileCount} full log(s) → {result.ZipPath}. " +
                                         "Use the report form below for automatic redacted GitHub issues.";
            }
            else
            {
                OptionsStatusText.Text = result.Message;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Gather logs threw: {ex}");
            OptionsStatusText.Foreground = Steel;
            OptionsStatusText.Text = $"Couldn't gather logs — {ex.Message}";
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

    // ─────────────────────────── Options ───────────────────────────

    /// <summary>Reflect settings into the Options controls and subscribe changes back to disk.</summary>
    private void WireOptions()
    {
        _optionsWiring = true;
        try
        {
            GamePathText.Text = string.IsNullOrWhiteSpace(_settings.GamePathOverride)
                ? (string.IsNullOrWhiteSpace(_shipped.GamePath) ? "Auto-detected from Steam" : _shipped.GamePath)
                : _settings.GamePathOverride;
            LauncherChannelToggle.IsChecked = IsNightly(_settings.LauncherChannel);
            SuiteChannelToggle.IsChecked = IsNightly(_settings.SuiteChannel);
            ClientChannelToggle.IsChecked = IsNightly(_settings.ClientChannel);
            RememberPasswordCheck.IsChecked = _settings.RememberPassword;
            CloseAfterLaunchCheck.IsChecked = _settings.CloseAfterLaunch;
            VerboseLoggingCheck.IsChecked = _settings.VerboseLogging;
            UpdateNightlyWarning();
        }
        finally
        {
            _optionsWiring = false;
        }

        BrowseGameButton.Click += OnBrowseGameClicked;
        RedetectGameButton.Click += OnRedetectGameClicked;
        GameVersionPicker.SelectionChanged += OnGameVersionSelected;
        LauncherChannelToggle.Click += (_, _) => OnChannelChanged();
        SuiteChannelToggle.Click += (_, _) => OnChannelChanged();
        ClientChannelToggle.Click += (_, _) => OnChannelChanged();
        RememberPasswordCheck.Click += (_, _) => OnRememberPasswordChanged();
        CloseAfterLaunchCheck.Click += (_, _) => SaveSimpleToggles();
        VerboseLoggingCheck.Click += (_, _) => SaveSimpleToggles();
        OpenLogsFolderButton.Click += (_, _) => OpenPathInExplorer(Path.GetDirectoryName(Log.Path)!);
        GithubButton.Click += (_, _) => OpenUrl(_config.ProjectUrl);
        AttachImagesButton.Click += OnAttachImagesClicked;
        SubmitReportButton.Click += OnSubmitReportClicked;
        RefreshGameInstallations();
    }

    private static bool IsNightly(string channel) =>
        string.Equals(channel, LauncherSettings.NightlyChannel, StringComparison.OrdinalIgnoreCase);

    private void UpdateNightlyWarning() =>
        NightlyWarning.Visibility =
            LauncherChannelToggle.IsChecked == true ||
            SuiteChannelToggle.IsChecked == true ||
            ClientChannelToggle.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;

    private void OnBrowseGameClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pick the Bannerlord install folder (contains bin\\Win64_Shipping_Client)",
        };
        if (dialog.ShowDialog(this) != true) return;

        _settings.GamePathOverride = dialog.FolderName;
        _settings.Save(LauncherSettings.DefaultPath);
        RefreshGameInstallations();
        OptionsStatusText.Text = "Game path saved. Re-checking the muster ground…";
        _ = ReapplySettingsAsync();
    }

    private void OnRedetectGameClicked(object sender, RoutedEventArgs e)
    {
        _settings.GamePathOverride = "";
        _settings.Save(LauncherSettings.DefaultPath);
        GamePathText.Text = string.IsNullOrWhiteSpace(_shipped.GamePath)
            ? "Auto-detected from Steam"
            : _shipped.GamePath;
        OptionsStatusText.Text = "Auto-detecting from the Steam library…";
        RefreshGameInstallations();
        _ = ReapplySettingsAsync();
    }

    private void RefreshGameInstallations()
    {
        _installations = GameLocator.FindInstallations(_config.GamePath);
        bool previousWiring = _optionsWiring;
        _optionsWiring = true;
        GameLocator.GameInstallation? selected = _installations.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(_settings.GamePathOverride) &&
            string.Equals(item.RootPath, Path.GetFullPath(_settings.GamePathOverride), StringComparison.OrdinalIgnoreCase))
            ?? _installations.FirstOrDefault(item => GameLocator.VersionsMatch(item.Version, _config.RequiredGameVersion))
            ?? _installations.FirstOrDefault();
        try
        {
            GameVersionPicker.ItemsSource = _installations;
            GameVersionPicker.SelectedItem = selected;
        }
        finally
        {
            _optionsWiring = previousWiring;
        }
        _bannerlordExe = selected?.ExePath;
        _modulesDir = selected is null ? null : GameLocator.FindModulesDir(selected.ExePath);
        GamePathText.Text = selected?.RootPath ?? "No Bannerlord installation found";
        RenderGameVersionState(_snapshot);
    }

    private void OnGameVersionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_optionsWiring) return;
        if (GameVersionPicker.SelectedItem is not GameLocator.GameInstallation selected) return;
        _settings.GamePathOverride = selected.RootPath;
        _settings.Save(LauncherSettings.DefaultPath);
        _config = _settings.ApplyTo(_shipped);
        _bannerlordExe = selected.ExePath;
        _modulesDir = GameLocator.FindModulesDir(selected.ExePath);
        GamePathText.Text = selected.RootPath;
        OptionsStatusText.Text = $"Selected Bannerlord {selected.Version}. Re-checking the armory…";
        RenderGameVersionState(_snapshot);
        _ = ReapplySettingsAsync();
    }

    private void OnChannelChanged()
    {
        if (_optionsWiring) return;
        _settings.LauncherChannel = LauncherChannelToggle.IsChecked == true
            ? LauncherSettings.NightlyChannel : LauncherSettings.StableChannel;
        _settings.SuiteChannel = SuiteChannelToggle.IsChecked == true
            ? LauncherSettings.NightlyChannel : LauncherSettings.StableChannel;
        _settings.ClientChannel = ClientChannelToggle.IsChecked == true
            ? LauncherSettings.NightlyChannel : LauncherSettings.StableChannel;
        _settings.Save(LauncherSettings.DefaultPath);
        UpdateNightlyWarning();
        OptionsStatusText.Text = "Channels saved. Re-reading the royal scrolls…";
        _ = ReapplySettingsAsync();
    }

    private void OnRememberPasswordChanged()
    {
        if (_optionsWiring) return;
        _settings.RememberPassword = RememberPasswordCheck.IsChecked == true;
        if (_settings.RememberPassword)
            _settings.ProtectPassword(ServerPasswordBox.Password);
        else
            _settings.ProtectedPassword = "";
        _settings.Save(LauncherSettings.DefaultPath);
        OptionsStatusText.Text = _settings.RememberPassword
            ? "The watchword is remembered for your Windows account."
            : "The watchword is forgotten.";
    }

    private void SaveSimpleToggles()
    {
        if (_optionsWiring) return;
        _settings.CloseAfterLaunch = CloseAfterLaunchCheck.IsChecked == true;
        _settings.VerboseLogging = VerboseLoggingCheck.IsChecked == true;
        _settings.Save(LauncherSettings.DefaultPath);
    }

    /// <summary>
    /// Recompute the effective config after a settings change and re-run locate + armory check,
    /// unless an install/launch is mid-flight (the change still applies on the next check).
    /// </summary>
    private async Task ReapplySettingsAsync()
    {
        _config = _settings.ApplyTo(_shipped);
        _portal = new PortalClient(_config.PortalUrl, _config.PortalManifestUrl);
        if (_operationActive)
        {
            OptionsStatusText.Text += "  (applies after the current operation)";
            return;
        }

        _updates = new ArmoryUpdateCoordinator(_config);
        RefreshGameInstallations();
        if (_bannerlordExe is null || _modulesDir is null)
        {
            SetStatus(online: false, "Bannerlord not found — set the game path in OPTIONS");
            SetGameMissingState();
            return;
        }
        await CheckArmoryAsync();
    }

    private void OpenPathInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            OptionsStatusText.Text = $"Couldn't open {path} — {ex.Message}";
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            OptionsStatusText.Text = $"Couldn't open the project page — {ex.Message}";
        }
    }

    // ─────────────────────────── Chronicle ───────────────────────────

    private async Task LoadChronicleAsync()
    {
        try
        {
            List<ChronicleEntry> entries = await Chronicle.LoadAsync(_config);
            ChronicleList.ItemsSource = entries;
            ChronicleEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ChronicleEmptyText.Text = "No dispatches yet — the chronicle could not be fetched and nothing is cached.";
        }
        catch (Exception ex)
        {
            Log.Write($"Chronicle load failed: {ex}");
            ChronicleEmptyText.Visibility = Visibility.Visible;
        }
    }

    // ─────────────────────────── Roster / reports ───────────────────────────

    private void PromptForPendingCrashReport()
    {
        // Existing installations can have a long archive of diagnostics produced before this
        // guided flow existed. Baseline it once; only later bundles are actionable.
        if (!_settings.CrashReportScanInitialized)
        {
            CrashReportCandidate? latest = CrashReportLocator.FindLatest();
            _settings.CrashReportScanInitialized = true;
            if (latest is not null)
                _settings.CrashReportWatermarkUtcTicks = latest.CreatedUtc.Ticks;
            _settings.Save(LauncherSettings.DefaultPath);
            return;
        }

        CrashReportCandidate? candidate = CrashReportLocator.FindPending(
            _settings.LastSubmittedCrashReport,
            _settings.CrashReportWatermarkUtcTicks);
        if (candidate is null) return;

        _pendingCrashReport = candidate;
        ReportAttachmentsText.Text = "A local crash bundle is ready for review and will not upload without confirmation.";
        MessageBoxResult choice = MessageBox.Show(
            "Bannerlord ended unexpectedly and the co-op crash reporter prepared a local diagnostic bundle. " +
            "Would you like to review it in the Steward report form?",
            "Calradia Co-op — crash report ready",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes)
        {
            _settings.CrashReportWatermarkUtcTicks = candidate.CreatedUtc.Ticks;
            _settings.Save(LauncherSettings.DefaultPath);
            return;
        }

        OptionsTab.IsChecked = true;
        ShowPanel(OptionsPanel);
        ReportKindPicker.SelectedIndex = 0;
        ReportTitleText.Text = "Bannerlord crash report";
        ReportDescriptionText.Text =
            "The launcher found a crash bundle prepared by the co-op crash reporter. " +
            "Add what you were doing immediately before Bannerlord closed.\n\n" +
            candidate.Summary;
        RefreshReportAttachmentsText();
        ReportDescriptionText.Focus();
    }

    private void OnAttachImagesClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select report images",
            Multiselect = true,
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp",
        };
        if (dialog.ShowDialog(this) != true) return;

        foreach (string path in dialog.FileNames)
        {
            if (_reportImages.Count >= 4) break;
            if (ReportAttachmentPackager.IsEligibleImage(path) &&
                !_reportImages.Contains(path, StringComparer.OrdinalIgnoreCase))
                _reportImages.Add(path);
        }
        RefreshReportAttachmentsText();
    }

    private void RefreshReportAttachmentsText()
    {
        var parts = new List<string>();
        if (_pendingCrashReport is not null) parts.Add("1 crash bundle");
        if (_reportImages.Count > 0) parts.Add($"{_reportImages.Count} image(s)");
        ReportAttachmentsText.Text = parts.Count == 0
            ? "No images or crash bundle selected."
            : $"Selected: {string.Join(" + ", parts)}. Attachments upload only after report confirmation." +
              (_reportImages.Count > 0 ? " Existing image metadata is included." : "");
    }

    private async Task LoadRosterAsync()
    {
        if (!_portal.IsConfigured)
        {
            RosterSummaryText.Text = "The campaign portal is not configured in this launcher build.";
            RosterGrid.ItemsSource = null;
            return;
        }
        try
        {
            RosterSummaryText.Text = "Fetching the host's campaign ledger…";
            CampaignStatsSnapshot? stats = await _portal.LoadStatsAsync();
            if (stats is null)
            {
                RosterSummaryText.Text = "The campaign ledger is temporarily unavailable.";
                RosterGrid.ItemsSource = null;
                return;
            }
            _rosterSource = stats.EnumerateLords().ToList();
            ApplyRosterFilters();
            string age = stats.UpdatedAt == default ? "just now" : stats.UpdatedAt.LocalDateTime.ToString("g");
            RosterSummaryText.Text =
                $"{_rosterSource.Count:N0} lords  •  campaign day {stats.CampaignDay:N0}  •  " +
                $"{stats.OnlinePlayers} online  •  Bannerlord {stats.GameVersion}  •  updated {age}";
        }
        catch (Exception ex)
        {
            Log.Write($"Roster load failed: {ex}");
            RosterSummaryText.Text = "The campaign ledger is temporarily unavailable.";
            RosterGrid.ItemsSource = null;
        }
    }

    private void ApplyRosterFilters()
    {
        if (RosterGrid is null) return;

        string search = RosterSearchBox?.Text.Trim() ?? string.Empty;
        string scope = SelectedTag(RosterScopePicker, "all");
        string status = SelectedTag(RosterStatusPicker, "all");
        string sort = SelectedTag(RosterSortPicker, "renown");

        IEnumerable<CampaignLordStats> rows = _rosterSource.Where(lord =>
        {
            if (scope == "player" && !string.Equals(lord.Controller, "player", StringComparison.OrdinalIgnoreCase))
                return false;
            if (scope == "ai" && !string.Equals(lord.Controller, "ai", StringComparison.OrdinalIgnoreCase))
                return false;
            if (status == "online" && !lord.Online) return false;
            if (status != "all" && status != "online" &&
                !string.Equals(lord.Status, status, StringComparison.OrdinalIgnoreCase))
                return false;
            return string.IsNullOrWhiteSpace(search) ||
                   (lord.Name ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   (lord.Clan ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   (lord.Kingdom ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   (lord.Culture ?? "").Contains(search, StringComparison.OrdinalIgnoreCase);
        });

        IOrderedEnumerable<CampaignLordStats> ranked = sort switch
        {
            "gold" => rows.OrderByDescending(lord => lord.Gold),
            "influence" => rows.OrderByDescending(lord => lord.Influence),
            "level" => rows.OrderByDescending(lord => lord.Level),
            "party" => rows.OrderByDescending(lord => lord.PartySize),
            "fiefs" => rows.OrderByDescending(lord => lord.Fiefs),
            _ => rows.OrderByDescending(lord => lord.Renown),
        };

        RosterGrid.ItemsSource = ranked
            .ThenByDescending(lord => lord.Level)
            .ThenBy(lord => lord.Name, StringComparer.OrdinalIgnoreCase)
            .Select((lord, index) => new RankedLordStats(lord, index + 1))
            .ToArray();
        RosterDetailText.Text = "Select a lord to view culture, location, and clan tier.";
    }

    private void ShowSelectedLordDetails()
    {
        if (RosterGrid.SelectedItem is not RankedLordStats lord) return;
        RosterDetailText.Text =
            $"{lord.Name}  •  {lord.Controller}  •  tier {lord.ClanTier}  •  {lord.Culture}  •  " +
            $"{lord.Location}  •  {lord.Status} / {lord.CurrentAction}";
    }

    private static string SelectedTag(ComboBox? picker, string fallback)
        => (picker?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private async void OnSubmitReportClicked(object sender, RoutedEventArgs e)
    {
        string title = ReportTitleText.Text.Trim();
        string description = ReportDescriptionText.Text.Trim();
        if (title.Length < 5 || description.Length < 10)
        {
            OptionsStatusText.Foreground = Steel;
            OptionsStatusText.Text = "Add a short title and enough detail to reproduce or understand the request.";
            return;
        }

        ReportAttachmentPackage package = default;
        bool hasAttachments = _pendingCrashReport is not null || _reportImages.Count > 0;
        if (hasAttachments)
        {
            package = await Task.Run(() => ReportAttachmentPackager.Create(
                _reportImages, _pendingCrashReport?.ZipPath));
            if (!package.Success || string.IsNullOrWhiteSpace(package.Path))
            {
                OptionsStatusText.Foreground = Steel;
                OptionsStatusText.Text = package.Message;
                return;
            }
        }

        SubmitReportButton.IsEnabled = false;
        object previous = SubmitReportButton.Content;
        SubmitReportButton.Content = "CREATING ISSUE…";
        try
        {
            string kind = (ReportKindPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "bug";
            string gameVersion = (GameVersionPicker.SelectedItem as GameLocator.GameInstallation)?.Version ?? "unknown";
            string logs = AttachLogsCheck.IsChecked == true
                ? await Task.Run(() => LogPackager.BuildReportExcerpt(_bannerlordExe))
                : string.Empty;
            if (_pendingCrashReport is not null)
            {
                string crashSummary = _pendingCrashReport.Summary;
                const string summaryHeader = "\n\nCrash collector summary:\n";
                int remaining = 4_000 - description.Length - summaryHeader.Length;
                if (remaining > 0)
                    description += summaryHeader + crashSummary[..Math.Min(crashSummary.Length, remaining)];
            }
            string reportClientId = _settings.GetOrCreateReportClientId();
            _settings.Save(LauncherSettings.DefaultPath);
            var submission = new ReportSubmission(
                reportClientId, kind, title, description,
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                gameVersion, logs);
            ReportResult result = await _portal.SubmitReportAsync(submission);
            OptionsStatusText.Foreground = result.Success ? Gold : Steel;
            OptionsStatusText.Text = result.Message;
            if (result.Success)
            {
                bool uploaded = true;
                if (hasAttachments && package.Path is not null)
                {
                    ReportAttachmentResult attachment = await _portal.UploadReportBundleAsync(result, package.Path);
                    uploaded = attachment.Success;
                    OptionsStatusText.Foreground = attachment.Success ? Gold : Steel;
                    OptionsStatusText.Text = attachment.Message;
                }
                if (uploaded)
                {
                    if (_pendingCrashReport is not null)
                    {
                        _settings.LastSubmittedCrashReport = _pendingCrashReport.Id;
                        _pendingCrashReport = null;
                    }
                    _reportImages.Clear();
                    _settings.Save(LauncherSettings.DefaultPath);
                    RefreshReportAttachmentsText();
                    ReportTitleText.Clear();
                    ReportDescriptionText.Clear();
                }
                if (!string.IsNullOrWhiteSpace(result.IssueUrl)) OpenUrl(result.IssueUrl);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Report submission failed: {ex}");
            OptionsStatusText.Foreground = Steel;
            OptionsStatusText.Text = $"Could not create the issue — {ex.Message}";
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(package.Path))
            {
                try { File.Delete(package.Path); }
                catch (Exception ex) { Log.Write($"Could not remove local report package: {ex.Message}"); }
            }
            SubmitReportButton.Content = previous;
            SubmitReportButton.IsEnabled = true;
        }
    }

    private sealed class RankedLordStats
    {
        public RankedLordStats(CampaignLordStats source, int rank)
        {
            Rank = rank;
            Name = source.Name;
            Controller = Display(source.Controller, "AI");
            Clan = source.Clan;
            Kingdom = source.Kingdom;
            Culture = source.Culture;
            Location = source.Location;
            ClanTier = source.ClanTier;
            Status = Display(source.Status, "Active");
            CurrentAction = Display(source.CurrentAction, "Unknown");
            Level = source.Level;
            Gold = source.Gold;
            Renown = source.Renown;
            Influence = source.Influence;
            PartySize = source.PartySize;
            Fiefs = source.Fiefs;
            Online = source.Online;
        }
        public int Rank { get; }
        public string Name { get; }
        public string Controller { get; }
        public string Clan { get; }
        public string Kingdom { get; }
        public string Culture { get; }
        public string Location { get; }
        public int ClanTier { get; }
        public string Status { get; }
        public string CurrentAction { get; }
        public int Level { get; }
        public int Gold { get; }
        public int Renown { get; }
        public int Influence { get; }
        public int PartySize { get; }
        public int Fiefs { get; }
        public bool Online { get; }

        private static string Display(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            return string.Join(" ", value.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
        }
    }

    // ─────────────────────────── Embers ───────────────────────────

    /// <summary>
    /// A handful of gold embers drifting up from the camp. Pure ambience: capped count, each a
    /// looping storyboard, and skipped entirely when the OS says not to animate.
    /// </summary>
    private void SpawnEmbers()
    {
        if (!SystemParameters.ClientAreaAnimation) return;

        var random = new Random(20260813);
        const int emberCount = 16;
        for (int i = 0; i < emberCount; i++)
        {
            double size = 2 + random.NextDouble() * 3;
            var ember = new System.Windows.Shapes.Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(
                    Color.FromArgb((byte)(120 + random.Next(100)), 0xE7, 0xC5, 0x6A)),
                Opacity = 0,
            };
            double left = 60 + random.NextDouble() * 1100;
            System.Windows.Controls.Canvas.SetLeft(ember, left);
            System.Windows.Controls.Canvas.SetTop(ember, 0);
            EmberCanvas.Children.Add(ember);

            var rise = new TranslateTransform();
            ember.RenderTransform = rise;
            double duration = 9 + random.NextDouble() * 9;
            double delay = random.NextDouble() * 8;
            double startY = 640 + random.NextDouble() * 90;

            var yAnimation = new DoubleAnimation(startY, startY - 260 - random.NextDouble() * 160,
                TimeSpan.FromSeconds(duration))
            {
                BeginTime = TimeSpan.FromSeconds(delay),
                RepeatBehavior = RepeatBehavior.Forever,
            };
            var drift = new DoubleAnimation(0, random.NextDouble() * 44 - 22, TimeSpan.FromSeconds(duration))
            {
                BeginTime = TimeSpan.FromSeconds(delay),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            var fade = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = TimeSpan.FromSeconds(delay),
                Duration = TimeSpan.FromSeconds(duration),
                RepeatBehavior = RepeatBehavior.Forever,
            };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromPercent(0.15)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.55, KeyTime.FromPercent(0.7)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));

            rise.BeginAnimation(TranslateTransform.YProperty, yAnimation);
            rise.BeginAnimation(TranslateTransform.XProperty, drift);
            ember.BeginAnimation(OpacityProperty, fade);
        }
    }

    private async Task LaunchGameAsync()
    {
        if (_bannerlordExe is null || _snapshot?.PrimaryAction != ArmoryPrimaryAction.Launch)
            return;

        if (GameVersionPicker.SelectedItem is not GameLocator.GameInstallation selected ||
            !GameLocator.VersionsMatch(selected.Version, RequiredGameVersion))
        {
            OptionsStatusText.Text = $"Select a Bannerlord {RequiredGameVersion} installation before joining.";
            OptionsTab.IsChecked = true;
            return;
        }

        _operationActive = true;
        try
        {
            JoinButton.IsEnabled = false;
            JoinButton.Content = "RIDING OUT…";

            // Opt-in only: refresh the remembered watchword with whatever is being used to ride out.
            if (_settings.RememberPassword)
            {
                _settings.ProtectPassword(ServerPasswordBox.Password);
                _settings.Save(LauncherSettings.DefaultPath);
            }

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

            if (_settings.CloseAfterLaunch)
            {
                Log.Write("Bannerlord still running after 9s — handing off, closing launcher");
                _statusTimer.Stop();
                Close();
                return;
            }

            // The member asked the launcher to stay: hand off but keep the camp lit.
            Log.Write("Bannerlord still running after 9s — handing off, launcher stays open");
            _operationActive = false;
            JoinButton.IsEnabled = true;
            JoinButton.Content = "MARCH TO WAR";
            UpdateText.Foreground = Steel;
            UpdateText.Text = "Bannerlord has taken the field. The launcher stays open (Options → The Camp).";
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
