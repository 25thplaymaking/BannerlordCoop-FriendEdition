using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Clans.Messages;

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("clan.mercenary.join", AuthorityRouteKind.Command)]
internal readonly struct RequestMercenaryService : ICommand
{
    [ProtoMember(1)]
    public readonly string KingdomId;
    [ProtoMember(2)]
    public readonly int AwardMultiplier;
    [ProtoMember(3)]
    public readonly string ClanId;

    [ProtoMember(4)]
    public readonly AuthorityRequestHeader Header;

    public RequestMercenaryService(string kingdomId, int awardMultiplier, string clanId)
        : this(kingdomId, awardMultiplier, clanId, default)
    {
    }

    public RequestMercenaryService(string kingdomId, int awardMultiplier, string clanId, AuthorityRequestHeader header)
    {
        KingdomId = kingdomId;
        AwardMultiplier = awardMultiplier;
        ClanId = clanId;
        Header = header;
    }
}
