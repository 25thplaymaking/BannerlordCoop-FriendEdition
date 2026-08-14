using Common;
using HarmonyLib;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.MapEvents;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// When a player accepts a bandit surrender via dialogue, the conversing client first sets the
/// encounter's victory state (SetOverrideWinner) and only then flags the enemy as surrendered
/// (EnemySurrender). The co-op server captures the defeated troops the moment the victory state
/// replicates, which is before the surrender is forwarded — so it would capture at the reduced
/// non-surrender rate. This marks that window so the victory-state relay is held back
/// (see <see cref="MapEventPatches"/>); the surrender is forwarded as its own message and the server
/// applies the whole surrender authoritatively, flagging the side as surrendered before it captures.
/// </summary>
[HarmonyPatch(typeof(BanditInteractionsCampaignBehavior))]
internal static class BanditSurrenderPatch
{
    private sealed class PendingState
    {
        public bool RequestPublished { get; set; }
    }

    private static readonly ConditionalWeakTable<MapEvent, PendingState> PendingPostBattleResults = new();

    /// <summary>
    /// True while the conversing client is applying a bandit-surrender dialogue consequence. Static
    /// because the Harmony patch methods that read and write it are static, and <c>[ThreadStatic]</c>
    /// so the window cannot bleed onto an unrelated thread.
    /// </summary>
    [ThreadStatic]
    internal static bool InSurrenderConsequence;

    [HarmonyPatch("conversation_bandits_surrender_on_consequence")]
    [HarmonyPrefix]
    private static void Prefix(out MapEvent __state)
    {
        __state = null;
        if (ModInformation.IsClient)
        {
            InSurrenderConsequence = true;

            try
            {
                __state = PlayerEncounter.Battle;
            }
            catch (NullReferenceException)
            {
                return;
            }

            MarkPendingPostBattleResults(__state);
        }
    }

    [HarmonyPatch("conversation_bandits_surrender_on_consequence")]
    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, MapEvent __state)
    {
        InSurrenderConsequence = false;

        if (__exception != null && __state != null)
            ClearPendingPostBattleResultsAfterFailure(__state);

        return __exception;
    }

    internal static void MarkPendingPostBattleResults(MapEvent mapEvent)
    {
        if (mapEvent == null) return;

        PendingPostBattleResults.Remove(mapEvent);
        PendingPostBattleResults.Add(mapEvent, new PendingState());
    }

    internal static void MarkSurrenderRequestPublished(MapEvent mapEvent)
    {
        if (mapEvent != null && PendingPostBattleResults.TryGetValue(mapEvent, out var state))
            state.RequestPublished = true;
    }

    internal static void ClearPendingPostBattleResultsAfterFailure(MapEvent mapEvent)
    {
        if (mapEvent != null && PendingPostBattleResults.TryGetValue(mapEvent, out var state) &&
            !state.RequestPublished)
        {
            PendingPostBattleResults.Remove(mapEvent);
        }
    }

    internal static bool TryConsumePendingPostBattleResults(MapEvent mapEvent)
    {
        if (mapEvent == null) return false;

        return PendingPostBattleResults.Remove(mapEvent);
    }
}
