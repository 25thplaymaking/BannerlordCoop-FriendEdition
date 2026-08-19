using GameInterface.Services.Save.Patches;
using HarmonyLib;
using System;
using Xunit;

namespace GameInterface.Tests.Services.Save;

/// <summary>
/// The autosave deferral targets a third-party obfuscated assembly, so the behaviour that matters
/// most is what happens when that assembly is absent: nothing, quietly.
/// </summary>
public class DeferAutosaveWhileCampaignRunningPatchTests
{
    [Fact]
    public void WithoutTheDedicatedServerAssembly_ApplyIsAQuietNoOp()
    {
        // DedicatedServer.Core is not loaded here, which is also every client's situation. This is
        // why the patch installs itself instead of going through PatchAll: Harmony treats an
        // unresolved target as an error and would take the whole assembly's patches down with it.
        var harmony = new Harmony($"test.{nameof(WithoutTheDedicatedServerAssembly_ApplyIsAQuietNoOp)}");

        try
        {
            Exception thrown = Record.Exception(() => DeferAutosaveWhileCampaignRunningPatch.Apply(harmony));

            Assert.Null(thrown);
            Assert.False(Harmony.HasAnyPatches(harmony.Id));
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    [Fact]
    public void ANullHarmonyIsRefusedRatherThanThrown()
    {
        Assert.Null(Record.Exception(() => DeferAutosaveWhileCampaignRunningPatch.Apply(null)));
    }

    [Fact]
    public void TheDeferralIsCappedSoAWorldThatNeverPausesStillSaves()
    {
        // Losing progress is worse than a visible stall, so the hold must have a hard ceiling.
        Assert.True(DeferAutosaveWhileCampaignRunningPatch.MaximumDeferral > TimeSpan.Zero);
        Assert.True(DeferAutosaveWhileCampaignRunningPatch.MaximumDeferral <= TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void OffTheDedicatedHost_TheSaveIsNeverHeld()
    {
        // ModInformation.IsServer is false in this fixture, so the prefix must always allow the
        // original through: a client has no business rescheduling anyone's autosave.
        Assert.True(DeferAutosaveWhileCampaignRunningPatch.Prefix());
    }
}
