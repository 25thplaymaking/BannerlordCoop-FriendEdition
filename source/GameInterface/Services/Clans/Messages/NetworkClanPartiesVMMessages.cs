using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Clans.Messages;

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("clan.party.create", AuthorityRouteKind.Command)]
internal readonly struct CreateNewClanParty : ICommand
{
    [ProtoMember(1)]
    public readonly string NewLeaderId;

    [ProtoMember(2)]
    public readonly AuthorityRequestHeader Header;

    public CreateNewClanParty(string newLeaderId, AuthorityRequestHeader header)
    {
        NewLeaderId = newLeaderId;
        Header = header;
    }
}

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("clan.party.leader.set", AuthorityRouteKind.Command)]
internal readonly struct ChangeClanPartyLeader : ICommand
{
    [ProtoMember(1)]
    public readonly string NewLeaderId;

    [ProtoMember(2)]
    public readonly string SelectedPartyId;

    [ProtoMember(3)]
    public readonly AuthorityRequestHeader Header;

    public ChangeClanPartyLeader(string newLeaderId, string selectedPartyId, AuthorityRequestHeader header)
    {
        NewLeaderId = newLeaderId;
        SelectedPartyId = selectedPartyId;
        Header = header;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct ClanPartyCreateResult : ICommand
{
    [ProtoMember(1)] public readonly string NewLeaderId;
    [ProtoMember(2)] public readonly string ClanId;
    [ProtoMember(3)] public readonly AuthorityResultHeader Header;

    public ClanPartyCreateResult(string newLeaderId, string clanId, AuthorityResultHeader header)
    {
        NewLeaderId = newLeaderId;
        ClanId = clanId;
        Header = header;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct ClanPartyLeaderChangeResult : ICommand
{
    [ProtoMember(1)] public readonly string SelectedPartyId;
    [ProtoMember(2)] public readonly string NewLeaderId;
    [ProtoMember(3)] public readonly AuthorityResultHeader Header;

    public ClanPartyLeaderChangeResult(string selectedPartyId, string newLeaderId, AuthorityResultHeader header)
    {
        SelectedPartyId = selectedPartyId;
        NewLeaderId = newLeaderId;
        Header = header;
    }
}
