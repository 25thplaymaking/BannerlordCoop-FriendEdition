using CoopLauncher.Services;
using Xunit;

namespace CoopLauncher.Tests;

public sealed class ArmoryUpdateCoordinatorTests
{
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(ComponentUpdateState.Current, ComponentUpdateState.Current,
        ComponentUpdateState.Current, ArmoryPrimaryAction.Launch)]
    [InlineData(ComponentUpdateState.Current, ComponentUpdateState.UpdateAvailable,
        ComponentUpdateState.Current, ArmoryPrimaryAction.Prepare)]
    [InlineData(ComponentUpdateState.UpdateAvailable, ComponentUpdateState.Current,
        ComponentUpdateState.Current, ArmoryPrimaryAction.Prepare)]
    [InlineData(ComponentUpdateState.Current, ComponentUpdateState.Unverified,
        ComponentUpdateState.Current, ArmoryPrimaryAction.RetryCheck)]
    [InlineData(ComponentUpdateState.Unverified, ComponentUpdateState.UpdateAvailable,
        ComponentUpdateState.Current, ArmoryPrimaryAction.RetryCheck)]
    public void Snapshot_ReducesEveryRequiredComponentFailClosed(
        ComponentUpdateState launcher,
        ComponentUpdateState suite,
        ComponentUpdateState client,
        ArmoryPrimaryAction expected)
    {
        ArmorySnapshot snapshot = Snapshot(launcher, suite, client);

        Assert.Equal(expected, snapshot.PrimaryAction);
        Assert.Equal(expected != ArmoryPrimaryAction.RetryCheck, snapshot.IsVerified);
        Assert.Equal(
            launcher == ComponentUpdateState.UpdateAvailable ||
            suite == ComponentUpdateState.UpdateAvailable ||
            client == ComponentUpdateState.UpdateAvailable,
            snapshot.HasUpdates);
    }

    [Fact]
    public async Task Prepare_RechecksBeforeInstallAndAgainBeforeReportingComplete()
    {
        var launcher = new FakeLauncherUpdateService(
            LauncherCheck(ComponentUpdateState.Current),
            LauncherCheck(ComponentUpdateState.Current));
        var mods = new FakeModUpdateService(
            ModsCheck(ComponentUpdateState.Current, ComponentUpdateState.UpdateAvailable),
            ModsCheck(ComponentUpdateState.Current, ComponentUpdateState.Current));
        var coordinator = new ArmoryUpdateCoordinator(launcher, mods);

        ArmoryPreparationResult result = await coordinator.PrepareAsync(
            "CalradiaCoop.exe", new Version(1, 0), "Modules", (_, _, _) => { });

        Assert.Equal(ArmoryPreparationOutcome.Completed, result.Outcome);
        Assert.Equal(2, launcher.CheckCount);
        Assert.Equal(2, mods.CheckCount);
        Assert.Equal(1, mods.InstallCount);
        Assert.Equal(0, launcher.StageCount);
        Assert.Equal(ArmoryPrimaryAction.Launch, result.Snapshot.PrimaryAction);
    }

    [Fact]
    public async Task Prepare_UnverifiedRecheckDownloadsNothing()
    {
        var launcher = new FakeLauncherUpdateService(
            LauncherCheck(ComponentUpdateState.Unverified));
        var mods = new FakeModUpdateService(
            ModsCheck(ComponentUpdateState.Current, ComponentUpdateState.UpdateAvailable));
        var coordinator = new ArmoryUpdateCoordinator(launcher, mods);

        ArmoryPreparationResult result = await coordinator.PrepareAsync(
            "CalradiaCoop.exe", new Version(1, 0), "Modules", (_, _, _) => { });

        Assert.Equal(ArmoryPreparationOutcome.Unverified, result.Outcome);
        Assert.Equal(0, launcher.StageCount);
        Assert.Equal(0, mods.InstallCount);
    }

    [Fact]
    public async Task Prepare_PendingLauncherRestartsBeforeModPayloads()
    {
        var launcher = new FakeLauncherUpdateService(
            LauncherCheck(ComponentUpdateState.UpdateAvailable))
        {
            StageResult = new LauncherUpdateResult(
                LauncherUpdateOutcome.Restarting, "launcher restarting"),
        };
        var mods = new FakeModUpdateService(
            ModsCheck(ComponentUpdateState.UpdateAvailable, ComponentUpdateState.UpdateAvailable));
        var coordinator = new ArmoryUpdateCoordinator(launcher, mods);

        ArmoryPreparationResult result = await coordinator.PrepareAsync(
            "CalradiaCoop.exe", new Version(1, 0), "Modules", (_, _, _) => { });

        Assert.Equal(ArmoryPreparationOutcome.Restarting, result.Outcome);
        Assert.Equal(1, launcher.StageCount);
        Assert.True(launcher.LastContinuePreparation);
        Assert.Equal(0, mods.InstallCount);
    }

    [Fact]
    public async Task Prepare_PostInstallUnverifiedDoesNotReportComplete()
    {
        var launcher = new FakeLauncherUpdateService(
            LauncherCheck(ComponentUpdateState.Current),
            LauncherCheck(ComponentUpdateState.Current));
        var mods = new FakeModUpdateService(
            ModsCheck(ComponentUpdateState.Current, ComponentUpdateState.UpdateAvailable),
            ModsCheck(ComponentUpdateState.Current, ComponentUpdateState.Unverified));
        var coordinator = new ArmoryUpdateCoordinator(launcher, mods);

        ArmoryPreparationResult result = await coordinator.PrepareAsync(
            "CalradiaCoop.exe", new Version(1, 0), "Modules", (_, _, _) => { });

        Assert.Equal(ArmoryPreparationOutcome.Unverified, result.Outcome);
        Assert.NotEqual(ArmoryPrimaryAction.Launch, result.Snapshot.PrimaryAction);
    }

    private static ArmorySnapshot Snapshot(
        ComponentUpdateState launcher,
        ComponentUpdateState suite,
        ComponentUpdateState client)
    {
        ComponentUpdateStatus Status(
            ArmoryComponent component, string label, ComponentUpdateState state) =>
            new(component, label, "1.0", state == ComponentUpdateState.UpdateAvailable ? "2.0" : "1.0",
                "test notes", state, state == ComponentUpdateState.Unverified ? "could not verify" : "verified");

        return new ArmorySnapshot(
            new LauncherUpdateCheck(Status(ArmoryComponent.Launcher, "Launcher", launcher), null),
            new ModUpdateCheck(
                Status(ArmoryComponent.ModSuite, "Mod suite", suite), null,
                Status(ArmoryComponent.CoopClient, "Co-op client", client), null));
    }

    private static LauncherUpdateCheck LauncherCheck(ComponentUpdateState state) =>
        new(
            Status(ArmoryComponent.Launcher, "Launcher", state),
            state == ComponentUpdateState.UpdateAvailable
                ? new LauncherUpdateManifest
                {
                    Version = "2.0",
                    LauncherUrl = "https://updates.example/CalradiaCoop.exe",
                    Sha256 = ValidSha,
                    Notes = "launcher notes",
                }
                : null);

    private static ModUpdateCheck ModsCheck(
        ComponentUpdateState suite,
        ComponentUpdateState client) =>
        new(
            Status(ArmoryComponent.ModSuite, "Mod suite", suite),
            ManifestFor(suite, "suite.zip"),
            Status(ArmoryComponent.CoopClient, "Co-op client", client),
            ManifestFor(client, "client.zip"));

    private static ComponentUpdateStatus Status(
        ArmoryComponent component,
        string label,
        ComponentUpdateState state) =>
        new(
            component,
            label,
            "1.0",
            state == ComponentUpdateState.UpdateAvailable ? "2.0" : "1.0",
            "test notes",
            state,
            state == ComponentUpdateState.Unverified ? "could not verify" : "verified");

    private static UpdateManifest? ManifestFor(ComponentUpdateState state, string asset) =>
        state == ComponentUpdateState.UpdateAvailable
            ? new UpdateManifest
            {
                Version = "2.0",
                ClientZipUrl = $"https://updates.example/{asset}",
                Sha256 = ValidSha,
                Notes = $"{asset} notes",
            }
            : null;

    private sealed class FakeLauncherUpdateService(params LauncherUpdateCheck[] checks)
        : ILauncherUpdateService
    {
        private int _nextCheck;
        public int CheckCount { get; private set; }
        public int StageCount { get; private set; }
        public bool LastContinuePreparation { get; private set; }
        public LauncherUpdateResult StageResult { get; init; } =
            new(LauncherUpdateOutcome.Failed, "unexpected launcher stage");

        public Task<LauncherUpdateCheck> CheckAsync(Version currentVersion)
        {
            CheckCount++;
            int index = Math.Min(_nextCheck++, checks.Length - 1);
            return Task.FromResult(checks[index]);
        }

        public Task<LauncherUpdateResult> StageAsync(
            string executablePath,
            LauncherUpdateManifest manifest,
            Action<ArmoryComponent, double, string> progress,
            bool continuePreparation)
        {
            StageCount++;
            LastContinuePreparation = continuePreparation;
            return Task.FromResult(StageResult);
        }
    }

    private sealed class FakeModUpdateService(params ModUpdateCheck[] checks)
        : IModUpdateService
    {
        private int _nextCheck;
        public int CheckCount { get; private set; }
        public int InstallCount { get; private set; }

        public Task<ModUpdateCheck> CheckAsync(string modulesDir)
        {
            CheckCount++;
            int index = Math.Min(_nextCheck++, checks.Length - 1);
            return Task.FromResult(checks[index]);
        }

        public Task<UpdateResult> InstallAsync(
            string modulesDir,
            ModUpdateCheck check,
            Action<ArmoryComponent, double, string> progress)
        {
            InstallCount++;
            return Task.FromResult(new UpdateResult(UpdateOutcome.Updated, "mods updated"));
        }
    }
}
