using Common.Messaging;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEventComponents.Messages;

/// <summary>
/// Server-side notification that a raiding party looted more items this tick. The delta is carried as a
/// plain list of (item, amount) pairs — deliberately NOT an <see cref="TaleWorlds.CampaignSystem.Roster.ItemRoster"/>.
/// Constructing a scratch <c>ItemRoster</c> here would trip the global roster lifetime/mutation patches
/// (<c>ItemRosterLifetimePatches</c> / <c>ItemRosterPatch</c>), which broadcast <c>NetworkCreateItemRoster</c>
/// + per-element <c>NetworkItemRosterUpdate</c> and log "Failed to get id" for the unregistered throwaway —
/// once per raid tick, per item. During a live raid that produced a ~1 MB/s packet storm that softlocked
/// clients and desynced loot totals (grain.silo 2026-08-13).
/// </summary>
internal readonly struct RaidLootedItemsUpdated : IEvent
{
    public readonly MobileParty MobileParty;
    public readonly IReadOnlyList<(ItemObject Item, int Amount)> LootedItems;

    public RaidLootedItemsUpdated(MobileParty mobileParty, IReadOnlyList<(ItemObject Item, int Amount)> lootedItems)
    {
        MobileParty = mobileParty;
        LootedItems = lootedItems;
    }
}
