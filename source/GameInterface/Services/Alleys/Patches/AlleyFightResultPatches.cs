using Common;
using Common.Messaging;
using GameInterface.Services.Alleys.Messages;
using HarmonyLib;
using SandBox.CampaignBehaviors;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.Alleys.Patches;

/// <summary>
/// The AI-attack defense fight is a local mission, but its win/loss and casualty roster are not trusted
/// authority inputs. These prefixes suppress the client-side vanilla mutation; the handler fail-closes
/// until a server-issued attack session can resolve the outcome.
/// Never called on the host (no PlayerAlleyData there), so the vanilla body is left intact on the server.
/// </summary>
[HarmonyPatch(typeof(AlleyCampaignBehavior.PlayerAlleyData))]
internal class AlleyFightResultPatches
{
    [HarmonyPatch("AlleyFightWon")]
    [HarmonyPrefix]
    private static bool AlleyFightWonPrefix(AlleyCampaignBehavior.PlayerAlleyData __instance)
    {
        if (ModInformation.IsServer) return true;
        // Send the post-fight garrison so the server records the defenders lost in the mission.
        MessageBroker.Instance.Publish(__instance, new AlleyDefenseResolvedRequested(__instance.Alley, won: true, __instance.TroopRoster));
        return false;
    }

    [HarmonyPatch("AlleyFightLost")]
    [HarmonyPrefix]
    private static bool AlleyFightLostPrefix(AlleyCampaignBehavior.PlayerAlleyData __instance)
    {
        if (ModInformation.IsServer) return true;
        // Keep the vanilla near-death feedback for the defender (their own hero, applied locally); the
        // authoritative destroy comes back from the server.
        if (Hero.MainHero != null) Hero.MainHero.HitPoints = 1;
        MessageBroker.Instance.Publish(__instance, new AlleyDefenseResolvedRequested(__instance.Alley, won: false, null));
        return false;
    }
}
