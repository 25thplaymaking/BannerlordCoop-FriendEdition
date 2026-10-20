using Common.Logging;
using HarmonyLib;
using Serilog;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem.Party.PartyComponents;

namespace GameInterface.Services.MobileParties.Patches;

/// <summary>
/// Keeps a war party's removal from aborting on a replica whose clan link has not arrived.
/// </summary>
/// <remarks>
/// <c>WarPartyComponent.OnFinalize</c> is a single unguarded call:
/// <code>
/// Clan => this.Party.MobileParty.ActualClan;
/// OnFinalize() => this.Clan.OnWarPartyRemoved(this);
/// </code>
/// It is a <c>callvirt</c> on <c>Clan</c>, so a null <c>ActualClan</c> throws. On the server that can
/// not happen — the clan is set when the party is built. On a client it can: <c>ActualClan</c> is
/// replicated as its own AutoSync member, so a replica can exist, and be destroyed, before that member
/// has been applied.
/// <para>
/// The throw is what does the damage, not the missing clan. It escapes
/// <c>MobileParty.OnRemoveParty</c> and aborts <c>RemoveParty</c> PART WAY THROUGH, which is worse
/// than either outcome on its own: the party keeps its entry in <c>Campaign.MobileParties</c> — so the
/// map still draws its nameplate and troop count — while its visual and cached tick state are gone.
/// That is the "party of 40 that is not there". Its broken cache then throws again every tick from
/// <c>ParallelTickMovingParties</c>, and its missing visual is reported by
/// <c>PartyVisualsRobustnessPatches</c>, so one abort produces a permanent ghost and two error streams.
/// </para>
/// <para>
/// Skipping the original when there is no clan is exactly equivalent to running it, because the whole
/// method body is that one call: with no clan there is no clan-side list holding this component, so
/// there is nothing to remove it from. Removal then completes and the party leaves the map properly.
/// A present clan is left entirely alone.
/// </para>
/// </remarks>
[HarmonyPatch(typeof(WarPartyComponent))]
internal class WarPartyComponentRobustnessPatches
{
    private static readonly ILogger Logger = LogManager.GetLogger<WarPartyComponentRobustnessPatches>();

    // One report per party. A missing clan link tends to affect a whole spawn wave, and the point is to
    // learn that it happened, not to re-line the log for every one of them.
    private static readonly HashSet<string> LoggedMissingClans = new HashSet<string>();

    [HarmonyPatch("OnFinalize")]
    [HarmonyPrefix]
    private static bool OnFinalizePrefix(WarPartyComponent __instance)
    {
        if (__instance?.Clan != null) return true;

        string stringId = __instance?.MobileParty?.StringId ?? "<unknown>";
        lock (LoggedMissingClans)
        {
            if (LoggedMissingClans.Add(stringId))
            {
                Logger.Warning(
                    "Finalizing war party {StringId} without a clan; skipping the clan-side removal so the " +
                    "party is removed completely rather than left half-removed on the map.",
                    stringId);
            }
        }

        return false;
    }
}
