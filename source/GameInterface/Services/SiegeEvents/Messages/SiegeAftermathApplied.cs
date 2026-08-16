using Common.Messaging;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.SiegeEvents.Messages;

/// <summary>
/// The server applied a siege aftermath; clients set their settlement-taken menu narration from it.
/// </summary>
public readonly struct SiegeAftermathApplied : IEvent
{
    public readonly MobileParty Party;
    public readonly Settlement Settlement;
    public readonly int AftermathType;

    public SiegeAftermathApplied(MobileParty party, Settlement settlement, int aftermathType)
    {
        Party = party;
        Settlement = settlement;
        AftermathType = aftermathType;
    }
}
