using Common.Messaging;
using GameInterface.Services.Kingdoms.Data;
using ProtoBuf;

namespace Coop.Core.Server.Services.Kingdoms.Messages;

[ProtoContract(SkipConstructor = true)]
public sealed class NetworkCreateKingdomResult : IEvent
{
    [ProtoMember(1)] public AuthorityResultHeader Header { get; }
    [ProtoMember(2)] public string KingdomId { get; }
    [ProtoMember(3)] public string KingdomName { get; }
    [ProtoMember(4)] public string ClanId { get; }
    [ProtoMember(5)] public string CultureId { get; }
    [ProtoMember(6)] public string PartyId { get; }
    [ProtoMember(7)] public string SettlementId { get; }
    [ProtoMember(8)] public string ControllerId { get; }

    public NetworkCreateKingdomResult(AuthorityResultHeader header, string kingdomId, string kingdomName,
        string clanId, string cultureId, string partyId, string settlementId, string controllerId)
    {
        Header = header;
        KingdomId = kingdomId;
        KingdomName = kingdomName;
        ClanId = clanId;
        CultureId = cultureId;
        PartyId = partyId;
        SettlementId = settlementId;
        ControllerId = controllerId;
    }
}

[ProtoContract(SkipConstructor = true)]
public sealed class NetworkKingdomRenameResult : IEvent
{
    [ProtoMember(1)] public AuthorityResultHeader Header { get; }
    [ProtoMember(2)] public string KingdomId { get; }
    [ProtoMember(3)] public string Name { get; }
    [ProtoMember(4)] public string ControllerId { get; }
    [ProtoMember(5)] public string FullName { get; }
    [ProtoMember(6)] public string InformalName { get; }

    public NetworkKingdomRenameResult(AuthorityResultHeader header, string kingdomId, string name,
        string fullName, string informalName, string controllerId)
    {
        Header = header;
        KingdomId = kingdomId;
        Name = name;
        ControllerId = controllerId;
        FullName = fullName;
        InformalName = informalName;
    }
}

[ProtoContract(SkipConstructor = true)]
public sealed class NetworkKingdomDecisionVoteResult : IEvent
{
    [ProtoMember(1)] public AuthorityResultHeader Header { get; }
    [ProtoMember(2)] public string ClanId { get; }
    [ProtoMember(3)] public KingdomDecisionVoteData VoteData { get; }
    [ProtoMember(4)] public bool DecisionResolved { get; }
    [ProtoMember(5)] public int ResolutionOutcomeIndex { get; }
    [ProtoMember(6)] public string ResolutionOutcomeKey { get; }
    [ProtoMember(7)] public string ControllerId { get; }

    public NetworkKingdomDecisionVoteResult(AuthorityResultHeader header, string clanId,
        KingdomDecisionVoteData voteData, bool decisionResolved, int resolutionOutcomeIndex,
        string resolutionOutcomeKey, string controllerId)
    {
        Header = header;
        ClanId = clanId;
        VoteData = voteData;
        DecisionResolved = decisionResolved;
        ResolutionOutcomeIndex = resolutionOutcomeIndex;
        ResolutionOutcomeKey = resolutionOutcomeKey;
        ControllerId = controllerId;
    }
}
