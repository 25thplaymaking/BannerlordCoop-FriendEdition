using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using LiteNetLib;
using ProtoBuf;

namespace GameInterface.Services.WorkshopMods.ImprovedGarrisons;

[AuthorityRoute("workshop.improved-garrisons.snapshot", AuthorityRouteKind.BootstrapQuery)]
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestImprovedGarrisonsState : ICommand
{
    [ProtoMember(1)] public readonly int ProtocolVersion;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly long ExpectedRevision;

    public NetworkRequestImprovedGarrisonsState(AuthorityRequestHeader header)
    {
        ProtocolVersion = header.ProtocolVersion;
        SessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ExpectedRevision = header.ExpectedRevision;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, SessionId, AuthorityRequestId, ExpectedRevision);
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkImprovedGarrisonsStateQueryResult : IEvent
{
    [ProtoMember(1)] public readonly NetworkImprovedGarrisonsState Snapshot;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly AuthorityResultStatus Status;
    [ProtoMember(5)] public readonly long CommittedRevision;
    [ProtoMember(6)] public readonly string ReasonCode;

    public NetworkImprovedGarrisonsStateQueryResult(
        AuthorityRequestHeader request, AuthorityResultStatus status, NetworkImprovedGarrisonsState snapshot, string reasonCode)
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

[AuthorityRoute("workshop.improved-garrisons.setting", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestImprovedGarrisonsSettingChange : ICommand
{
    [ProtoMember(1)] public readonly long RequestId;
    [ProtoMember(2)] public readonly long ExpectedRevision;
    [ProtoMember(3)] public readonly string ManagerType;
    [ProtoMember(4)] public readonly string Method;
    [ProtoMember(5)] public readonly string TownId;
    [ProtoMember(6)] public readonly string Value;
    [ProtoMember(7)] public readonly string SessionId;
    [ProtoMember(8)] public readonly int ConfigProtocolVersion;

    public NetworkRequestImprovedGarrisonsSettingChange(
        string sessionId,
        long requestId,
        long expectedRevision,
        string managerType,
        string method,
        string townId,
        string value,
        int configProtocolVersion = 0)
    {
        SessionId = sessionId;
        RequestId = requestId;
        ExpectedRevision = expectedRevision;
        ManagerType = managerType;
        Method = method;
        TownId = townId;
        Value = value;
        ConfigProtocolVersion = configProtocolVersion;
    }

    public NetworkRequestImprovedGarrisonsSettingChange(
        AuthorityRequestHeader header, string managerType, string method, string townId, string value)
        : this(header.SessionId, header.RequestId, header.ExpectedRevision, managerType, method, townId, value,
            header.ProtocolVersion)
    {
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ConfigProtocolVersion, SessionId, RequestId, ExpectedRevision);
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkImprovedGarrisonsSettingResult : IEvent
{
    [ProtoMember(1)] public readonly string SessionId;
    [ProtoMember(2)] public readonly long RequestId;
    [ProtoMember(3)] public readonly AuthorityResultStatus Status;
    [ProtoMember(4)] public readonly long CommittedRevision;
    [ProtoMember(5)] public readonly string ReasonCode;
    [ProtoMember(6)] public readonly string CommandDigest;
    [ProtoMember(7)] public readonly string CanonicalHash;
    [ProtoMember(8)] public readonly string ManagerType;
    [ProtoMember(9)] public readonly string Method;
    [ProtoMember(10)] public readonly string TownId;
    [ProtoMember(11)] public readonly string Value;

    public NetworkImprovedGarrisonsSettingResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reasonCode, string commandDigest,
        string canonicalHash, string managerType, string method, string townId, string value,
        long? committedRevision = null)
    {
        SessionId = header.SessionId;
        RequestId = header.RequestId;
        Status = status;
        CommittedRevision = committedRevision ?? header.ExpectedRevision;
        ReasonCode = reasonCode ?? string.Empty;
        CommandDigest = commandDigest ?? string.Empty;
        CanonicalHash = canonicalHash ?? string.Empty;
        ManagerType = managerType ?? string.Empty;
        Method = method ?? string.Empty;
        TownId = townId ?? string.Empty;
        Value = value ?? string.Empty;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, RequestId, Status, CommittedRevision, ReasonCode);
}

[ProtoContract(SkipConstructor = true)]
internal sealed class ImprovedGarrisonsStateValue
{
    [ProtoMember(1)] public string Scope { get; set; }
    [ProtoMember(2)] public string TargetId { get; set; }
    [ProtoMember(3)] public string Property { get; set; }
    [ProtoMember(4)] public string Value { get; set; }

    public ImprovedGarrisonsStateValue()
    {
    }

    public ImprovedGarrisonsStateValue(string scope, string targetId, string property, string value)
    {
        Scope = scope;
        TargetId = targetId;
        Property = property;
        Value = value;
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkImprovedGarrisonsState : ICommand
{
    [ProtoMember(1)] public string AdapterVersion { get; set; }
    [ProtoMember(2)] public long Revision { get; set; }
    [ProtoMember(3)] public string CanonicalHash { get; set; }
    [ProtoMember(4)] public ImprovedGarrisonsStateValue[] Values { get; set; }

    public NetworkImprovedGarrisonsState()
    {
    }

    public NetworkImprovedGarrisonsState(
        string adapterVersion,
        long revision,
        string canonicalHash,
        ImprovedGarrisonsStateValue[] values)
    {
        AdapterVersion = adapterVersion;
        Revision = revision;
        CanonicalHash = canonicalHash;
        Values = values;
    }
}

/// <summary>
/// Accepts an authoritative snapshot only when it arrived through the rendered client's server
/// transport. Local message-broker publications have no <see cref="NetPeer"/> source and are not
/// authoritative.
/// </summary>
internal static class ImprovedGarrisonsSnapshotOriginGuard
{
    public static bool IsTrustedServerTransport(object source, bool localIsClient) =>
        localIsClient && source is NetPeer;
}
