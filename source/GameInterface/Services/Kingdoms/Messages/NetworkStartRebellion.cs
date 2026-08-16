using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Kingdoms.Messages;

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("kingdom.rebel", AuthorityRouteKind.Command)]
internal readonly struct NetworkStartRebellion : ICommand
{
    [ProtoMember(1)]
    public readonly string ClanId;

    [ProtoMember(2)]
    public readonly AuthorityRequestHeader Header;

    public NetworkStartRebellion(string clanId)
        : this(clanId, default)
    {
    }

    public NetworkStartRebellion(string clanId, AuthorityRequestHeader header)
    {
        ClanId = clanId;
        Header = header;
    }
}
