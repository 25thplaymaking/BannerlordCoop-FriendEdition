using Common;
using GameInterface.Services.WorkshopMods.Diplomacy;

namespace E2E.Tests.Environment.Mock;

/// <summary>
/// The E2E host intentionally has no private Workshop DLLs. Register an explicit, valid
/// Diplomacy boundary instead of relying on accidental discovery of nested unit-test doubles.
/// </summary>
internal sealed class MockDiplomacyRuntime : IDiplomacyRuntime
{
    public bool IsAvailable => true;
    public string AssemblyVersion => DiplomacyCompatibilityPolicy.SupportedAssemblyVersion;

    public NetworkDiplomacySnapshot CaptureSnapshot()
    {
        var snapshot = new NetworkDiplomacySnapshot
        {
            AssemblyVersion = DiplomacyCompatibilityPolicy.SupportedAssemblyVersion,
            AssemblySha256 = DiplomacyCompatibilityPolicy.SupportedAssemblySha256,
            CampaignId = "e2e-campaign",
            StateSchema = DiplomacyRuntime.CurrentStateSchema,
            FriendSeparatismOwnsRebellions = DiplomacyCompatibilityPolicy.FriendSeparatismOwnsRebellions,
        };
        snapshot.State.AddRange(DiplomacySnapshotCodec.RequiredSections.Select(section =>
            new DiplomacyStateEntry
            {
                Section = section,
                Key = DiplomacyRuntime.MarkerKey,
            }));
        snapshot.SettingsFingerprint = DiplomacyRuntime.FingerprintSettings(snapshot.Settings);
        snapshot.StateFingerprint = DiplomacyRuntime.FingerprintState(snapshot.State);
        return snapshot;
    }

    public DiplomacySnapshotApplyResult ApplySnapshot(NetworkDiplomacySnapshot snapshot) =>
        new(DiplomacySnapshotApplyStatus.Applied);

    public DiplomacySnapshotApplyResult ValidateUiReadiness(NetworkDiplomacySnapshot snapshot) =>
        new(DiplomacySnapshotApplyStatus.AlreadyCurrent);

    public void ResetSnapshotRevision() { }
}

internal sealed class MockDiplomacyClientUiLifecycle : IDiplomacyClientUiLifecycle
{
    public bool IsReady { get; private set; }

    public void ResetForCampaign() => IsReady = ModInformation.IsServer;

    public bool TryMarkSnapshotReady(out string failure)
    {
        IsReady = true;
        failure = null;
        return true;
    }
}
