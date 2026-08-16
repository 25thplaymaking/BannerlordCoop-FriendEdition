using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Clans.Messages;

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("clan.vassal.leave", AuthorityRouteKind.Command)]
internal readonly struct RequestLeaveVassalService : ICommand
{
    [ProtoMember(1)]
    public readonly string ClanId;

    [ProtoMember(2)]
    public readonly AuthorityRequestHeader Header;

    public RequestLeaveVassalService(string clanId)
        : this(clanId, default)
    {
    }

    public RequestLeaveVassalService(string clanId, AuthorityRequestHeader header)
    {
        ClanId = clanId;
        Header = header;
    }
}
