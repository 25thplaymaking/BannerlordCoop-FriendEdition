using Common.Messaging;
using GameInterface.Configuration;
using ProtoBuf;
using System;
using System.Collections.Generic;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

[AuthorityRoute("workshop.diplomacy.snapshot", AuthorityRouteKind.BootstrapQuery)]
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestDiplomacySnapshot : ICommand
{
    [ProtoMember(1)] public readonly int ConfigProtocolVersion;
    [ProtoMember(2)] public readonly string ConfigSessionId;
    [ProtoMember(3)] public readonly long ConfigRevision;
    [ProtoMember(4)] public readonly string ConfigSha256;
    [ProtoMember(5)] public readonly long AuthorityRequestId;

    public NetworkRequestDiplomacySnapshot(AuthorityRequestHeader header, ModConfigSnapshot acceptedConfig)
    {
        ConfigProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        ConfigRevision = header.ExpectedRevision;
        ConfigSha256 = acceptedConfig?.Sha256;
        AuthorityRequestId = header.RequestId;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ConfigProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);

    internal bool TryValidateWireShape(out string failure)
    {
        if (ConfigProtocolVersion != ModConfigSnapshot.CurrentProtocolVersion ||
            ConfigRevision <= 0 ||
            ConfigSessionId == null ||
            ConfigSessionId.Length != ModConfigSnapshot.SessionIdLength ||
            !Guid.TryParseExact(ConfigSessionId, "N", out _) ||
            ConfigSha256 == null ||
            ConfigSha256.Length != ModConfigSnapshot.Sha256Length ||
            !IsLowerHex(ConfigSha256))
        {
            failure = "Malformed accepted mod-config identity.";
            return false;
        }

        failure = null;
        return true;
    }

    internal bool Matches(ModConfigSnapshot acceptedConfig) =>
        acceptedConfig != null &&
        ConfigProtocolVersion == acceptedConfig.ProtocolVersion &&
        ConfigRevision == acceptedConfig.Revision &&
        string.Equals(ConfigSessionId, acceptedConfig.SessionId, StringComparison.Ordinal) &&
        string.Equals(ConfigSha256, acceptedConfig.Sha256, StringComparison.Ordinal);

    private static bool IsLowerHex(string value)
    {
        foreach (char c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }
}

/// <summary>Correlated Diplomacy state bootstrap result; broadcasts keep their snapshot message.</summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkDiplomacySnapshotQueryResult : IEvent
{
    [ProtoMember(1)] public readonly NetworkDiplomacySnapshot Snapshot;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly AuthorityResultStatus Status;
    [ProtoMember(5)] public readonly long CommittedRevision;
    [ProtoMember(6)] public readonly string ReasonCode;

    public NetworkDiplomacySnapshotQueryResult(
        AuthorityRequestHeader request,
        AuthorityResultStatus status,
        NetworkDiplomacySnapshot snapshot,
        string reasonCode)
    {
        Snapshot = snapshot;
        SessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = snapshot?.Revision ?? request.ExpectedRevision;
        ReasonCode = reasonCode;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[ProtoContract]
internal sealed class NetworkDiplomacySnapshot : IEvent
{
    [ProtoMember(1)] public string AssemblyVersion { get; set; } = string.Empty;
    [ProtoMember(2)] public string SettingsFingerprint { get; set; } = string.Empty;
    [ProtoMember(3)] public List<DiplomacySettingEntry> Settings { get; set; } = new();
    [ProtoMember(4)] public string StateFingerprint { get; set; } = string.Empty;
    [ProtoMember(5)] public List<DiplomacyStateEntry> State { get; set; } = new();
    [ProtoMember(6)] public bool FriendSeparatismOwnsRebellions { get; set; }
    [ProtoMember(7)] public int StateSchema { get; set; } = DiplomacyRuntime.CurrentStateSchema;
    [ProtoMember(8)] public long Revision { get; set; }
    [ProtoMember(9)] public string AssemblySha256 { get; set; } = string.Empty;
    [ProtoMember(10)] public string CampaignId { get; set; } = string.Empty;
}

[ProtoContract]
internal sealed class DiplomacySettingEntry
{
    [ProtoMember(1)] public string Name { get; set; } = string.Empty;
    [ProtoMember(2)] public string TypeName { get; set; } = string.Empty;
    [ProtoMember(3)] public string Value { get; set; } = string.Empty;
}

[ProtoContract]
internal sealed class DiplomacyStateEntry
{
    [ProtoMember(1)] public string Section { get; set; } = string.Empty;
    [ProtoMember(2)] public string Key { get; set; } = string.Empty;
    [ProtoMember(3)] public string Faction1Id { get; set; } = string.Empty;
    [ProtoMember(4)] public string Faction2Id { get; set; } = string.Empty;
    [ProtoMember(5)] public long Ticks1 { get; set; }
    [ProtoMember(6)] public long Ticks2 { get; set; }
    [ProtoMember(7)] public float Value1 { get; set; }
    [ProtoMember(8)] public float Value2 { get; set; }
    [ProtoMember(9)] public int Flags1 { get; set; }
    [ProtoMember(10)] public int Flags2 { get; set; }
}

internal enum DiplomacySnapshotApplyStatus
{
    Applied,
    AlreadyCurrent,
    StaleRevision,
    RevisionConflict,
    MalformedSnapshot,
    ModNotLoaded,
    VersionMismatch,
    SchemaMismatch,
    CampaignMismatch,
    ConfigurationMismatch,
    SettingsMismatch,
    StateMismatch,
    ApplyFailed,
}

internal readonly struct DiplomacySnapshotApplyResult
{
    public DiplomacySnapshotApplyStatus Status { get; }
    public string Detail { get; }

    public bool Succeeded =>
        Status == DiplomacySnapshotApplyStatus.Applied ||
        Status == DiplomacySnapshotApplyStatus.AlreadyCurrent;

    public DiplomacySnapshotApplyResult(DiplomacySnapshotApplyStatus status, string detail = null)
    {
        Status = status;
        Detail = detail;
    }
}
