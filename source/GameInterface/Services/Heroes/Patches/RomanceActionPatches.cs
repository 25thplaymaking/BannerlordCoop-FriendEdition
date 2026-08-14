using Common;
using Common.Messaging;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Services.Heroes.Extensions;
using GameInterface.Services.Heroes.Messages.RomanceFlow;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using Romance = TaleWorlds.CampaignSystem.Romance;

namespace GameInterface.Services.Heroes.Patches;

[HarmonyPatch(typeof(ChangeRomanticStateAction))]
internal class RomanceActionPatches
{
    [HarmonyPatch(nameof(ChangeRomanticStateAction.Apply))]
    [HarmonyPrefix]
    private static bool ApplyPrefix(Hero person1, Hero person2, Romance.RomanceLevelEnum toWhat)
    {
        if (ModInformation.IsServer || CallOriginalPolicy.IsOriginalAllowed()) return true;

        // Route the change when this client is a legitimate driver of it: either the local player
        // hero is one of the couple (personal courtship), or the couple is (own clan member,
        // outside hero) - an ARRANGED match this player is negotiating in conversation. The
        // arranged case used to fall through silently, so MatchMadeByFamily never reached the
        // server and every arranged marriage barter was rejected with "has not been agreed by
        // both clans" (58 live rejections, zero successful marriages, 2026-08-10..14).
        if (person1.IsControlledByThisInstance() || person2.IsControlledByThisInstance() ||
            IsLocalArrangedPair(person1, person2))
        {
            var state = Romance.GetRomanticState(person1, person2);
            MessageBroker.Instance.Publish(
                person1,
                new RomanticStateChangeRequested(
                    person1,
                    person2,
                    toWhat,
                    state?.ProgressToNextLevel ?? 0,
                    state?.LastVisit ?? 0f,
                    state?.ScoreFromPersuasion ?? 0f));

            using (new AllowedThread())
            {
                ChangeRomanticStateAction.Apply(person1, person2, toWhat);
            }
        }

        return false;
    }

    /// <summary>
    /// True when exactly one of the pair is a (non-player) member of THIS client's clan and the
    /// other belongs to another clan - the shape of an arranged match the local player negotiates.
    /// </summary>
    internal static bool IsLocalArrangedPair(Hero person1, Hero person2)
    {
        var localClan = Hero.MainHero?.Clan;
        if (localClan == null) return false;

        bool firstIsLocalMember = person1?.Clan == localClan && !person1.IsPlayerHero();
        bool secondIsLocalMember = person2?.Clan == localClan && !person2.IsPlayerHero();
        if (firstIsLocalMember == secondIsLocalMember) return false;

        var outsider = firstIsLocalMember ? person2 : person1;
        return outsider != null && outsider.Clan != localClan && !outsider.IsPlayerHero();
    }

    [HarmonyPatch(nameof(ChangeRomanticStateAction.Apply))]
    [HarmonyPostfix]
    private static void ApplyPostfix()
    {
        if (!ModInformation.IsServer) return;

        MessageBroker.Instance.Publish(null, new RomanceStatesChanged());
    }
}

[HarmonyPatch(typeof(MarriageAction))]
internal class MarriageActionPatches
{
    [HarmonyPatch(nameof(MarriageAction.Apply))]
    [HarmonyPrefix]
    private static bool ApplyPrefix()
    {
        if (ModInformation.IsServer || CallOriginalPolicy.IsOriginalAllowed()) return true;
        return false;
    }
}
