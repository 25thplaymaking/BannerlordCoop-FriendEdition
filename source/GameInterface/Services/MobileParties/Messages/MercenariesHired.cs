using Common.Messaging;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.MobileParties.Messages;

/// <summary>Local UI event. It deliberately carries no hero, party, troop, price, or gold authority.</summary>
internal readonly struct MercenariesHired : IEvent
{
    public readonly Town Town;
    public readonly int Count;
    public MercenariesHired(Town town, int count) { Town = town; Count = count; }
}
