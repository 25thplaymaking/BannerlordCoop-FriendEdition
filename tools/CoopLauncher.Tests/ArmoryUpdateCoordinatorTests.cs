using CoopLauncher.Services;
using Xunit;

namespace CoopLauncher.Tests;

public sealed class ArmoryUpdateCoordinatorTests
{
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
}
