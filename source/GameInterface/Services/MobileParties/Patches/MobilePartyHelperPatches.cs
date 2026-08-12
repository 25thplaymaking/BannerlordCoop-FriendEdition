using HarmonyLib;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.MobileParties.Patches;

[HarmonyPatch(typeof(MobilePartyHelper))]
internal class MobilePartyHelperPatches
{
    internal static bool ShouldUseSettlementSpawn(Settlement currentSettlement) => currentSettlement != null;

    [HarmonyPatch(nameof(MobilePartyHelper.CreateNewClanMobileParty))]
    [HarmonyPrefix]
    public static bool PrefixCreateNewClanMobileParty(Hero hero, ref MobileParty __result)
    {
        var currentSettlement = hero.CurrentSettlement;

        if (!ShouldUseSettlementSpawn(currentSettlement)) return true;

        // Replace hero.CurrentSettlement != null block to not use MainParty
        hero.PartyBelongedTo?.AddElementToMemberRoster(hero.CharacterObject, -1);

        __result = MobilePartyHelper.SpawnLordParty(hero, currentSettlement);

        return false;
    }
}
