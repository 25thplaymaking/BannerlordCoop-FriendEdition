using Common.Logging;
using GameInterface.Services.Heroes.Extensions;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;

namespace GameInterface.Services.PartyBases.Patches;

[HarmonyPatch(typeof(DefaultPartySizeLimitModel))]
internal class DefaultPartySizeLimitModelPatches
{
    private static readonly ILogger Logger = LogManager.GetLogger<DefaultPartySizeLimitModelPatches>();

    /// <summary>
    /// Native grants the caravan size bonus only to <c>Hero.MainHero</c>'s own caravans
    /// (<c>party.Party.Owner == Hero.MainHero</c> in <c>CalculateMobilePartyMemberSizeLimit</c>).
    /// A headless host is nobody's MainHero and a client is only its own, so every other player's
    /// caravan silently loses the bonus — on the server that meant real desertions down to the
    /// unbonused cap, and on a client it meant the clan screen advertising a maximum the caravan is
    /// already over.
    ///
    /// This used to be a prefix that skipped native entirely and returned a flat
    /// <c>20 + (elite ? 30 : 10)</c>. That threw away everything native adds first — the clan's
    /// <c>CalculateBaseMemberSize</c> contribution and the leader's Steward skill bonus — so a
    /// player caravan's cap was pinned at 30 (or 50 elite) no matter how the clan grew. Run native
    /// and only add what it skipped.
    /// </summary>
    [HarmonyPatch(nameof(DefaultPartySizeLimitModel.CalculateMobilePartyMemberSizeLimit))]
    [HarmonyPostfix]
    public static void CalculateMobilePartyMemberSizeLimitPostfix(
        DefaultPartySizeLimitModel __instance,
        ref ExplainedNumber __result,
        MobileParty party)
    {
        if (party == null || !party.IsCaravan) return;

        var caravan = party.CaravanPartyComponent;
        if (caravan == null) return;

        // Read the component's owner directly. PartyBase.Owner consults _customOwner first, and that
        // field is deliberately not replicated (see PartyBaseBinaryPackage.Excludes), so going
        // through it can resolve differently on a client than on the host.
        Hero owner = caravan.Owner;
        if (owner == null)
        {
            // Nothing to attribute the bonus to. Name it: an unresolved owner is exactly how this
            // ends up reporting the bare base size, and it is otherwise silent.
            Logger.Warning(
                "Caravan {PartyName} has no resolvable owner; its size limit will omit the player caravan bonus",
                party.StringId);
            return;
        }

        if (!owner.IsPlayerHero()) return;

        // Native already added the bonus for this peer's own caravan; adding again would double it.
        if (owner == Hero.MainHero) return;

        int bonus = caravan.IsElite ? 30 : 10;
        if (caravan.CanHaveNavalNavigationCapability) bonus = caravan.IsElite ? 46 : 33;

        __result.Add(bonus, __instance._randomSizeBonusTemporary, null);
    }

    [HarmonyPatch(nameof(DefaultPartySizeLimitModel.GetInitialPartySizeRatioForMobileParty))]
    [HarmonyPrefix]
    public static bool GetInitialPartySizeRatioForMobilePartyPrefix(ref float __result, MobileParty party, PartyTemplateObject partyTemplate)
    {
        // Override result if player caravan
        if (party.IsCaravan && party.Owner != null && party.Owner.IsPlayerHero())
        {
            __result = 1f;
            return false;
        }

        return true;
    }
}
