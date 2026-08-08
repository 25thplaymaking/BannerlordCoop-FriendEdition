using Common.Messaging;
using ProtoBuf;
using System;

namespace GameInterface.Services.MapEvents.Messages;

/// <summary>
/// Server -> clients controlling raid attackers: the militia-resistance battle was finalized and the
/// server either created the follow-on raid event or fell back to the village when continuation failed.
/// </summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRaidBattleTransition : ICommand
{
    [ProtoMember(1)]
    public readonly string[] PartyIds;

    [ProtoMember(2)]
    public readonly string SettlementId;

    [ProtoMember(3)]
    public readonly string MapEventId;

    public NetworkRaidBattleTransition(string[] partyIds, string settlementId, string mapEventId)
    {
        PartyIds = partyIds ?? Array.Empty<string>();
        SettlementId = settlementId;
        MapEventId = mapEventId;
    }
}
