using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Clans.Messages;

[ProtoContract(SkipConstructor = true)]
internal readonly struct VassalServiceResult : ICommand
{
    [ProtoMember(1)]
    public readonly string KingdomId;

    [ProtoMember(2)]
    public readonly bool Accepted;

    [ProtoMember(3)]
    public readonly bool GrantRewards;

    [ProtoMember(4)]
    public readonly AuthorityResultHeader Header;

    public VassalServiceResult(string kingdomId, bool accepted, bool grantRewards)
        : this(kingdomId, accepted, grantRewards, default)
    {
    }

    public VassalServiceResult(string kingdomId, bool accepted, bool grantRewards, AuthorityResultHeader header)
    {
        KingdomId = kingdomId;
        Accepted = accepted;
        GrantRewards = grantRewards;
        Header = header;
    }
}
