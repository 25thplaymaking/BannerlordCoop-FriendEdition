using CoopLauncher.Services;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace CoopLauncher.Tests;

public sealed class LauncherUpdateApplierTests
{
    [Fact]
    public void ApplyArguments_RejectMalformedOrUnsafeTargets()
    {
        using var fixture = new ApplyFixture();
        string outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.exe");

        Assert.False(LauncherUpdateApplier.TryParseApplyArguments(
            [LauncherUpdateCommand.ApplySwitch, fixture.TargetExe, "not-a-pid", fixture.StagedSha],
            fixture.StagedExe, out _));
        Assert.False(LauncherUpdateApplier.TryParseApplyArguments(
            [LauncherUpdateCommand.ApplySwitch, fixture.TargetExe, "123", fixture.StagedSha],
            outside, out _));
    }

    [Fact]
    public void VerifiedStage_ReplacesTargetAndBuildsCompletionRelaunch()
    {
        using var fixture = new ApplyFixture();
        ProcessStartInfo? relaunch = null;
        var request = new LauncherApplyRequest(
            fixture.StagedExe, fixture.TargetExe, 123, fixture.StagedSha);

        LauncherApplyResult result = LauncherUpdateApplier.ApplyAndRelaunch(
            request,
            _ => true,
            info => { relaunch = info; return new Process(); });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(fixture.NewBytes, File.ReadAllBytes(fixture.TargetExe));
        Assert.Equal(fixture.ConfigBytes, File.ReadAllBytes(fixture.ConfigPath));
        Assert.NotNull(relaunch);
        Assert.Equal(fixture.TargetExe, relaunch!.FileName);
        Assert.Equal(LauncherUpdateApplier.CompletionSwitch, relaunch.ArgumentList[0]);
        Assert.Equal(fixture.StageDirectory, relaunch.ArgumentList[2]);
        Assert.True(File.Exists(relaunch.ArgumentList[3]));
    }

    [Fact]
    public void RelaunchFailure_RestoresOriginalAndStartsRollbackOnce()
    {
        using var fixture = new ApplyFixture();
        var starts = new List<ProcessStartInfo>();
        var request = new LauncherApplyRequest(
            fixture.StagedExe, fixture.TargetExe, 123, fixture.StagedSha);

        LauncherApplyResult result = LauncherUpdateApplier.ApplyAndRelaunch(
            request,
            _ => true,
            info =>
            {
                starts.Add(info);
                if (starts.Count == 1) throw new InvalidOperationException("new launcher would not start");
                return new Process();
            });

        Assert.False(result.Succeeded);
        Assert.Equal(fixture.OldBytes, File.ReadAllBytes(fixture.TargetExe));
        Assert.Equal(fixture.ConfigBytes, File.ReadAllBytes(fixture.ConfigPath));
        Assert.Equal(2, starts.Count);
        Assert.Equal(LauncherUpdateApplier.SkipOnceSwitch, starts[1].ArgumentList[0]);
    }

    [Fact]
    public void FinalIntegrityFailure_RestartsInstalledLauncherWithoutApplying()
    {
        using var fixture = new ApplyFixture();
        ProcessStartInfo? rollback = null;
        var request = new LauncherApplyRequest(
            fixture.StagedExe, fixture.TargetExe, 123, new string('0', 64));

        LauncherApplyResult result = LauncherUpdateApplier.ApplyAndRelaunch(
            request,
            _ => true,
            info => { rollback = info; return new Process(); });

        Assert.False(result.Succeeded);
        Assert.Equal(fixture.OldBytes, File.ReadAllBytes(fixture.TargetExe));
        Assert.NotNull(rollback);
        Assert.Equal(LauncherUpdateApplier.SkipOnceSwitch, rollback!.ArgumentList[0]);
    }

    [Fact]
    public void CompletionCleanup_RemovesOnlyBackupAndValidatedStage()
    {
        using var fixture = new ApplyFixture();
        ProcessStartInfo? relaunch = null;
        var apply = LauncherUpdateApplier.ApplyAndRelaunch(
            new LauncherApplyRequest(fixture.StagedExe, fixture.TargetExe, 123, fixture.StagedSha),
            _ => true,
            info => { relaunch = info; return new Process(); });
        Assert.True(apply.Succeeded);
        string unrelated = Path.Combine(fixture.Root, "keep.txt");
        File.WriteAllText(unrelated, "keep");

        Assert.True(LauncherUpdateApplier.TryParseCompletionArguments(
            relaunch!.ArgumentList.ToArray(), fixture.TargetExe, out LauncherCompletionRequest? completion));
        LauncherUpdateApplier.CleanupAfterStartup(completion!, _ => true);

        Assert.False(Directory.Exists(fixture.StageDirectory));
        Assert.False(File.Exists(completion!.BackupPath));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(fixture.ConfigBytes, File.ReadAllBytes(fixture.ConfigPath));
    }

    private sealed class ApplyFixture : IDisposable
    {
        public byte[] OldBytes { get; } = Encoding.UTF8.GetBytes("old launcher");
        public byte[] NewBytes { get; } = Encoding.UTF8.GetBytes("new verified launcher");
        public byte[] ConfigBytes { get; } = Encoding.UTF8.GetBytes("{\"serverPassword\":\"local-only\"}");
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"launcher-apply-test-{Guid.NewGuid():N}");
        public string TargetExe => Path.Combine(Root, "CalradiaCoop.exe");
        public string ConfigPath => Path.Combine(Root, "launcher-config.json");
        public string StageDirectory => Path.Combine(Root, ".calradia-launcher-update-test");
        public string StagedExe => Path.Combine(StageDirectory, "CalradiaCoop.exe");
        public string StagedSha => Convert.ToHexString(SHA256.HashData(NewBytes)).ToLowerInvariant();

        public ApplyFixture()
        {
            Directory.CreateDirectory(StageDirectory);
            File.WriteAllBytes(TargetExe, OldBytes);
            File.WriteAllBytes(ConfigPath, ConfigBytes);
            File.WriteAllBytes(StagedExe, NewBytes);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
