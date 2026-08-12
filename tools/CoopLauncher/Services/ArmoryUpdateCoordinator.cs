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
