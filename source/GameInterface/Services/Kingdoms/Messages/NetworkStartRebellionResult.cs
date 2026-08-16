using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Kingdoms.Messages;

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkStartRebellionResult : ICommand
{
    [ProtoMember(1)] public readonly string ClanId;
    [ProtoMember(2)] public readonly AuthorityResultHeader Header;

    public NetworkStartRebellionResult(string clanId, AuthorityResultHeader header)
    {
        ClanId = clanId;
        Header = header;
    }
}
