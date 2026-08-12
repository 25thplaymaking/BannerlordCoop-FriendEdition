namespace CoopLauncher.Services;

public enum ArmoryComponent { Launcher, ModSuite, CoopClient }

public enum ComponentUpdateState { Current, UpdateAvailable, Unverified }

public enum ArmoryPrimaryAction { Launch, Prepare, RetryCheck }

public sealed record ComponentUpdateStatus(
    ArmoryComponent Component,
    string Label,
    string? InstalledVersion,
    string? AvailableVersion,
    string Notes,
    ComponentUpdateState State,
    string Detail);

public sealed record LauncherUpdateCheck(
    ComponentUpdateStatus Status,
    LauncherUpdateManifest? Manifest);

public sealed record ModUpdateCheck(
    ComponentUpdateStatus SuiteStatus,
    UpdateManifest? SuiteManifest,
    ComponentUpdateStatus ClientStatus,
    UpdateManifest? ClientManifest);

public sealed record ArmorySnapshot(
    LauncherUpdateCheck Launcher,
    ModUpdateCheck Mods)
{
    public IReadOnlyList<ComponentUpdateStatus> Components =>
        [Launcher.Status, Mods.SuiteStatus, Mods.ClientStatus];

    public bool IsVerified =>
        Components.All(item => item.State != ComponentUpdateState.Unverified);

    public bool HasUpdates =>
        Components.Any(item => item.State == ComponentUpdateState.UpdateAvailable);

    public ArmoryPrimaryAction PrimaryAction => !IsVerified
        ? ArmoryPrimaryAction.RetryCheck
        : HasUpdates
            ? ArmoryPrimaryAction.Prepare
            : ArmoryPrimaryAction.Launch;
}

public enum ArmoryPreparationOutcome { Completed, Restarting, Unverified, Failed }

public sealed record ArmoryPreparationResult(
    ArmoryPreparationOutcome Outcome,
    ArmorySnapshot Snapshot,
    string Message);

internal interface ILauncherUpdateService
{
    Task<LauncherUpdateCheck> CheckAsync(Version currentVersion);

    Task<LauncherUpdateResult> StageAsync(
        string executablePath,
        LauncherUpdateManifest manifest,
        Action<ArmoryComponent, double, string> progress,
        bool continuePreparation);
}

internal interface IModUpdateService
{
    Task<ModUpdateCheck> CheckAsync(string modulesDir);

    Task<UpdateResult> InstallAsync(
        string modulesDir,
        ModUpdateCheck check,
        Action<ArmoryComponent, double, string> progress);
}

public sealed class ArmoryUpdateCoordinator
{
    private readonly ILauncherUpdateService _launcher;
    private readonly IModUpdateService _mods;

    public ArmoryUpdateCoordinator(LauncherConfig config)
        : this(new LauncherSelfUpdater(config), new ModUpdater(config)) { }

    internal ArmoryUpdateCoordinator(
        ILauncherUpdateService launcher,
        IModUpdateService mods)
    {
        _launcher = launcher;
        _mods = mods;
    }

    public async Task<ArmorySnapshot> CheckAsync(
        Version currentVersion,
        string modulesDir)
    {
        Task<LauncherUpdateCheck> launcherTask = _launcher.CheckAsync(currentVersion);
        Task<ModUpdateCheck> modsTask = _mods.CheckAsync(modulesDir);
        await Task.WhenAll(launcherTask, modsTask);
        return new ArmorySnapshot(launcherTask.Result, modsTask.Result);
    }

    public async Task<ArmoryPreparationResult> PrepareAsync(
        string executablePath,
        Version currentVersion,
        string modulesDir,
        Action<ArmoryComponent, double, string> progress)
    {
        ArmorySnapshot check = await CheckAsync(currentVersion, modulesDir);
        if (!check.IsVerified)
            return new(
                ArmoryPreparationOutcome.Unverified,
                check,
                "The required update feeds could not be verified.");

        if (check.Launcher.Status.State == ComponentUpdateState.UpdateAvailable)
        {
            if (check.Launcher.Manifest is null)
                return new(ArmoryPreparationOutcome.Failed, check,
                    "The launcher update plan was incomplete.");

            LauncherUpdateResult launcherResult = await _launcher.StageAsync(
                executablePath,
                check.Launcher.Manifest,
                progress,
                continuePreparation: true);
            return launcherResult.Outcome == LauncherUpdateOutcome.Restarting
                ? new(ArmoryPreparationOutcome.Restarting, check, launcherResult.Message)
                : new(ArmoryPreparationOutcome.Failed, check, launcherResult.Message);
        }

        if (check.Mods.SuiteStatus.State == ComponentUpdateState.UpdateAvailable ||
            check.Mods.ClientStatus.State == ComponentUpdateState.UpdateAvailable)
        {
            UpdateResult modResult = await _mods.InstallAsync(modulesDir, check.Mods, progress);
            if (modResult.Outcome == UpdateOutcome.Failed)
                return new(ArmoryPreparationOutcome.Failed, check, modResult.Message);
        }

        ArmorySnapshot final = await CheckAsync(currentVersion, modulesDir);
        if (!final.IsVerified)
            return new(
                ArmoryPreparationOutcome.Unverified,
                final,
                "The installed versions could not be verified after preparation.");
        if (final.HasUpdates)
            return new(
                ArmoryPreparationOutcome.Failed,
                final,
                "Preparation finished, but one or more updates are still required.");

        return new(
            ArmoryPreparationOutcome.Completed,
            final,
            "Every required component is current.");
    }
}
