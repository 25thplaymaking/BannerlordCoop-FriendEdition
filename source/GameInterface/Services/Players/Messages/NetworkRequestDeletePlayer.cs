using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Players.Messages;

/// <summary>
/// Client request to delete the requesting player: the server removes the player registration,
/// kills the hero, destroys the party, and disconnects the requesting client.
/// </summary>
[AuthorityRoute("player.self-delete", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestDeletePlayer : ICommand
{
    [ProtoMember(1)] public readonly int ProtocolVersion;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly long ExpectedRevision;

    public NetworkRequestDeletePlayer(AuthorityRequestHeader header)
    {
        ProtocolVersion = header.ProtocolVersion;
        SessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ExpectedRevision = header.ExpectedRevision;
    }

    /// <summary>Compatibility-only constructor: the former hero id is deliberately not serialized.</summary>
    public NetworkRequestDeletePlayer(string _) : this((AuthorityRequestHeader)default) { }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, SessionId, AuthorityRequestId, ExpectedRevision);
}
