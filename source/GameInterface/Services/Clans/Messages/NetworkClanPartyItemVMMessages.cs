using Common.Messaging;
using ProtoBuf;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.Clans.Messages;

[ProtoContract(SkipConstructor = true)]
internal readonly struct UpdatePartyBehaviorOnSelection : ICommand
{
    [ProtoMember(1)]
    public readonly string MobilePartyId;

    [ProtoMember(2)]
    public readonly MobileParty.PartyObjective PartyObjective;

    public UpdatePartyBehaviorOnSelection(
        string mobilePartyId,
        MobileParty.PartyObjective partyObjective)
    {
        MobilePartyId = mobilePartyId;
        PartyObjective = partyObjective;
    }
}

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("clan.autorecruit.set", AuthorityRouteKind.Command)]
internal readonly struct ChangeAutoRecruitForSettlement : ICommand
{
    [ProtoMember(1)]
    public readonly string HomeSettlementId;

    [ProtoMember(2)]
    public readonly bool Value;

    [ProtoMember(3)]
    public readonly AuthorityRequestHeader Header;

    public ChangeAutoRecruitForSettlement(
        string homeSettlementId,
        bool value)
        : this(homeSettlementId, value, default)
    {
    }

    public ChangeAutoRecruitForSettlement(string homeSettlementId, bool value, AuthorityRequestHeader header)
    {
        HomeSettlementId = homeSettlementId;
        Value = value;
        Header = header;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct ChangeAutoRecruitForSettlementClients : ICommand
{
    [ProtoMember(1)]
    public readonly string HomeSettlementId;

    [ProtoMember(2)]
    public readonly bool Value;

    public ChangeAutoRecruitForSettlementClients(
        string homeSettlementId,
        bool value)
    {
        HomeSettlementId = homeSettlementId;
        Value = value;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct AutoRecruitChangeResult : ICommand
{
    [ProtoMember(1)] public readonly string HomeSettlementId;
    [ProtoMember(2)] public readonly bool Value;
    [ProtoMember(3)] public readonly AuthorityResultHeader Header;

    public AutoRecruitChangeResult(string homeSettlementId, bool value, AuthorityResultHeader header)
    {
        HomeSettlementId = homeSettlementId;
        Value = value;
        Header = header;
    }
}
