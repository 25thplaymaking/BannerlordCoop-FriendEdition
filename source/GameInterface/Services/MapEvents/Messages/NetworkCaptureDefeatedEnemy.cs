using Common.Messaging;
using ProtoBuf;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MapEvents.Messages;

/// <summary>[Local] The client selected the native post-battle capture consequence.</summary>
internal readonly struct CaptureDefeatedEnemyAttempted : IEvent
{
    public readonly MapEvent MapEvent;
    public readonly MobileParty PlayerParty;

    public CaptureDefeatedEnemyAttempted(MapEvent mapEvent, MobileParty playerParty)
    {
        MapEvent = mapEvent;
        PlayerParty = playerParty;
    }
}

/// <summary>
/// [Client -&gt; Server] Requests conclusion of a defeated-NPC field battle. The requesting party is
/// deliberately not accepted from the wire; the server derives it from the authenticated peer.
/// </summary>
[ProtoContract]
internal readonly struct NetworkCaptureDefeatedEnemy : ICommand
{
    [ProtoMember(1)]
    public readonly string MapEventId;

    public NetworkCaptureDefeatedEnemy(string mapEventId)
    {
        MapEventId = mapEventId;
    }
}
