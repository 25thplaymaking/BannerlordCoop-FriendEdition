using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Clans.Messages;

[ProtoContract(SkipConstructor = true)]
internal readonly struct MercenaryServiceResult : ICommand
{
    [ProtoMember(1)] public readonly string KingdomId;
    [ProtoMember(2)] public readonly string ClanId;
    [ProtoMember(3)] public readonly bool IsUnderMercenaryService;
    [ProtoMember(4)] public readonly AuthorityResultHeader Header;

    public MercenaryServiceResult(string kingdomId, string clanId, bool isUnderMercenaryService,
        AuthorityResultHeader header)
    {
        KingdomId = kingdomId;
        ClanId = clanId;
        IsUnderMercenaryService = isUnderMercenaryService;
        Header = header;
    }
}
