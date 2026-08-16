using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Clans.Messages;

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("clan.vassal.join", AuthorityRouteKind.Command)]
internal readonly struct RequestVassalService : ICommand
{
    [ProtoMember(1)]
    public readonly string KingdomId;

    [ProtoMember(2)]
    public readonly bool GrantRewards;

    [ProtoMember(3)]
    public readonly AuthorityRequestHeader Header;

    public RequestVassalService(string kingdomId, bool grantRewards)
        : this(kingdomId, grantRewards, default)
    {
    }

    public RequestVassalService(string kingdomId, bool grantRewards, AuthorityRequestHeader header)
    {
        KingdomId = kingdomId;
        GrantRewards = grantRewards;
        Header = header;
    }
}
