using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace GameInterface.Services.PlayerCaptivityService;

/// <summary>
/// Server-side port of <c>PlayerCaptivity.GetPlayerRansomValue</c>, parameterised by the captive.
/// </summary>
/// <remarks>
/// Native's version reads <see cref="Hero.MainHero"/> at every step — gold, captor, and the Man of Means
/// perk — so on a headless host it prices the *host's* hero, not the captive client's. It is also
/// unreachable for a co-op client in another sense: <c>RansomPlayerValuePatch</c> forces
/// <c>PrisonerRansomValue</c> to 0 for player heroes so the AI never ransoms them.
///
/// That left the server with no number of its own to check a client's claimed ransom against, which is
/// why player self-release was disabled outright (audit F15). This is the same arithmetic taken from the
/// shipped IL of <c>PlayerCaptivity.GetPlayerRansomValue</c>, with the hero passed in:
///
/// <code>
/// (int)((rand * 0.5 + 0.5)
///       * (gold * 0.05 + 300)
///       * (settlement ? (kingdom ? 4 : 2) : 1)
///       * (mobile ? (lordParty ? 2 : 1) : 1)
///       * (manOfMeans ? 1 + secondaryBonus : 1))
/// </code>
///
/// The roll is taken once, when captivity begins, and stored on the offer — native does the same
/// (<c>SetRansomAmount</c> assigns <c>CurrentRansomAmount</c> once), so the price cannot drift between
/// what the player is quoted and what they are charged.
/// </remarks>
internal static class PlayerCaptivityRansom
{
    /// <summary>
    /// The arithmetic, with every game lookup already resolved. Separated so it can be tested without a
    /// campaign: the multipliers are the part worth pinning, and they are not observable in a live game.
    /// </summary>
    /// <param name="roll">A value in [0,1), native's <c>MBRandom.RandomFloat</c>.</param>
    internal static int Compute(
        float roll,
        int captiveGold,
        bool captorIsSettlement,
        bool captorSettlementIsKingdom,
        bool captorIsMobile,
        bool captorIsLordParty,
        bool captiveHasManOfMeans,
        float manOfMeansSecondaryBonus)
    {
        float value = roll * 0.5f + 0.5f;
        value *= captiveGold * 0.05f + 300f;
        value *= captorIsSettlement ? (captorSettlementIsKingdom ? 4f : 2f) : 1f;
        value *= captorIsMobile ? (captorIsLordParty ? 2f : 1f) : 1f;
        value *= captiveHasManOfMeans ? 1f + manOfMeansSecondaryBonus : 1f;
        return (int)value;
    }

    /// <summary>
    /// Prices a captive against the party actually holding them. Returns 0 when the hero is not held,
    /// which the caller treats as "no offer to make".
    /// </summary>
    internal static int ForCaptive(Hero captive, PartyBase captor)
    {
        if (captive == null || captor == null) return 0;

        bool hasPerk = captive.GetPerkValue(DefaultPerks.Trade.ManOfMeans);

        return Compute(
            MBRandom.RandomFloat,
            captive.Gold,
            captor.IsSettlement,
            captor.IsSettlement && captor.Settlement?.MapFaction?.IsKingdomFaction == true,
            captor.IsMobile,
            captor.IsMobile && captor.MobileParty?.IsLordParty == true,
            hasPerk,
            DefaultPerks.Trade.ManOfMeans.SecondaryBonus);
    }
}
