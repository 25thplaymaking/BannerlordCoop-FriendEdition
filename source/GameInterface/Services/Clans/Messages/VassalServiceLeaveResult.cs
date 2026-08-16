using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Clans.Messages;

[ProtoContract(SkipConstructor = true)]
internal readonly struct VassalServiceLeaveResult : ICommand
{
    [ProtoMember(1)] public readonly string ClanId;
    [ProtoMember(2)] public readonly AuthorityResultHeader Header;

    public VassalServiceLeaveResult(string clanId, AuthorityResultHeader header)
    {
        ClanId = clanId;
        Header = header;
    }
}
