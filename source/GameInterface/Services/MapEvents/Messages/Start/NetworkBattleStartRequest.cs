using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages.Start;

/// <summary>
/// [Client -&gt; Server] Asks the server to start the battle for a map event in a given mode (0 = live mission,
/// 1 = auto-resolve simulation; see <c>BattleStartMode</c>). The coordinator is the sole route owner;
/// the legacy fields remain for compatibility while the added canonical authority header carries correlation.
/// </summary>
[AuthorityRoute("map-event.battle-start", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkBattleStartRequest : ICommand
{
    [ProtoMember(1)]
    public readonly string RequestId;

    [ProtoMember(2)]
    public readonly int Mode;

    [ProtoMember(3)]
    public readonly string MapEventId;

    [ProtoMember(4)]
    public readonly string AttackerPartyId;

    [ProtoMember(5)] public readonly int ProtocolVersion;
    [ProtoMember(6)] public readonly string SessionId;
    [ProtoMember(7)] public readonly long ExpectedRevision;
    [ProtoMember(8)] public readonly long AuthorityRequestId;

    public NetworkBattleStartRequest(string requestId, int mode, string mapEventId, string attackerPartyId)
    {
        RequestId = requestId;
        Mode = mode;
        MapEventId = mapEventId;
        AttackerPartyId = attackerPartyId;
        ProtocolVersion = 0;
        SessionId = null;
        ExpectedRevision = 0;
        AuthorityRequestId = 0;
    }

    public NetworkBattleStartRequest(AuthorityRequestHeader header, int mode, string mapEventId, string attackerPartyId)
    {
        RequestId = header.RequestId.ToString();
        Mode = mode;
        MapEventId = mapEventId;
        AttackerPartyId = attackerPartyId;
        ProtocolVersion = header.ProtocolVersion;
        SessionId = header.SessionId;
        ExpectedRevision = header.ExpectedRevision;
        AuthorityRequestId = header.RequestId;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, SessionId, AuthorityRequestId, ExpectedRevision);
}
