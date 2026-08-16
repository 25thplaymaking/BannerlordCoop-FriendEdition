using Common.Messaging;
using GameInterface.Configuration;
using ProtoBuf;

namespace GameInterface.Services.CampaignService.Messages;

[AuthorityRoute("bootstrap.mod-config", AuthorityRouteKind.BootstrapQuery)]
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestServerModConfig : ICommand
{
    [ProtoMember(1)] public readonly int ProtocolVersion;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long Revision;
    [ProtoMember(4)] public readonly long AuthorityRequestId;

    public NetworkRequestServerModConfig()
        : this(null)
    {
    }

    public NetworkRequestServerModConfig(ModConfigSnapshot current)
    {
        ProtocolVersion = ModConfigSnapshot.CurrentProtocolVersion;
        SessionId = current?.SessionId;
        Revision = current?.Revision ?? 0;
        AuthorityRequestId = 0;
    }

    public NetworkRequestServerModConfig(AuthorityRequestHeader header)
    {
        ProtocolVersion = header.ProtocolVersion;
        SessionId = header.SessionId;
        Revision = header.ExpectedRevision;
        AuthorityRequestId = header.RequestId;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, SessionId, AuthorityRequestId, Revision);

    public bool TryValidateWireShape(out string failure)
    {
        if (ProtocolVersion != ModConfigSnapshot.CurrentProtocolVersion)
        {
            failure = "Unsupported mod-config request protocol.";
            return false;
        }
        if (Revision < 0 ||
            (Revision == 0 && !string.IsNullOrEmpty(SessionId)) ||
            (Revision > 0 &&
                (SessionId == null ||
                 SessionId.Length != ModConfigSnapshot.SessionIdLength ||
                 !System.Guid.TryParseExact(SessionId, "N", out _))))
        {
            failure = "Malformed mod-config request identity.";
            return false;
        }
        failure = null;
        return true;
    }
}

/// <summary>Server → client: the options the host resolved from its config, pushed verbatim so every
/// client runs on the host's values rather than its own file.</summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkLoadModConfig : IEvent
{
    [ProtoMember(1)]
    public readonly ModConfigSnapshot Snapshot;

    public NetworkLoadModConfig(ModConfigSnapshot snapshot)
    {
        Snapshot = snapshot;
    }
}

/// <summary>Correlated response for an already-trusted late mod-config refresh.</summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkModConfigQueryResult : IEvent
{
    [ProtoMember(1)] public readonly ModConfigSnapshot Snapshot;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly AuthorityResultStatus Status;
    [ProtoMember(5)] public readonly long CommittedRevision;
    [ProtoMember(6)] public readonly string ReasonCode;

    public NetworkModConfigQueryResult(
        AuthorityRequestHeader request,
        AuthorityResultStatus status,
        ModConfigSnapshot snapshot,
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

/// <summary>
/// Local broker notification emitted only after <see cref="IModConfigAuthority"/> validated and
/// atomically committed a host snapshot. Consumers must also check the authority still owns it;
/// this type is never sent over <see cref="Common.Network.INetwork"/>.
/// </summary>
public readonly struct HostModConfigAccepted : IEvent
{
    public readonly ModConfigSnapshot Snapshot;

    public HostModConfigAccepted(ModConfigSnapshot snapshot) => Snapshot = snapshot;
}
