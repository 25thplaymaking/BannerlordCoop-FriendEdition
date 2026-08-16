using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Clans.Messages;

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("clan.mercenary.leave", AuthorityRouteKind.Command)]
internal readonly struct RequestMercenaryDismissalService : ICommand
{
    [ProtoMember(1)]
    public readonly string KingdomId;
    [ProtoMember(2)]
    public readonly string ClanId;

    [ProtoMember(3)]
    public readonly AuthorityRequestHeader Header;

    public RequestMercenaryDismissalService(string kingdomId, string clanId)
        : this(kingdomId, clanId, default)
    {
    }

    public RequestMercenaryDismissalService(string kingdomId, string clanId, AuthorityRequestHeader header)
    {
        KingdomId = kingdomId;
        ClanId = clanId;
        Header = header;
    }
}
