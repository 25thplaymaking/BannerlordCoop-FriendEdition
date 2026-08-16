using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Server.Services.BattleRetreat.Messages;

/// <summary>Replica notification for other player parties detached from their local siege UI by a retreat.</summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkBattleRetreatCampsCleared : IEvent
{
    [ProtoMember(1)]
    public string[] PartyIds { get; }

    public NetworkBattleRetreatCampsCleared(string[] partyIds)
    {
        PartyIds = partyIds;
    }
}
