using Common;
using GameInterface.Policies;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.PlayerCaptivityService.Patches;

/// <summary>
/// Stops a client re-pricing its own captivity over the server's figure.
/// </summary>
/// <remarks>
/// <c>PlayerCaptivity.SetRansomAmount</c> assigns <c>CurrentRansomAmount = GetPlayerRansomValue()</c>, and
/// <c>PlayerCaptivityCampaignBehavior.CheckCaptivityChange</c> calls it on tick — so a client left to
/// itself keeps re-rolling the price while captive.
///
/// The server is what actually charges, from the offer it priced once when it recorded the capture, so a
/// client-side re-roll can only ever produce a menu that advertises a number the player will not be
/// charged. The release offer supplies the authoritative figure; this keeps native from overwriting it.
///
/// The server still runs the original: its own <see cref="Hero.MainHero"/> is the headless host's, which
/// is never a co-op captive, and suppressing it there would change host-local state for no reason.
/// </remarks>
[HarmonyPatch(typeof(PlayerCaptivity))]
internal class PlayerCaptivityRansomAmountPatches
{
    [HarmonyPatch(nameof(PlayerCaptivity.SetRansomAmount))]
    [HarmonyPrefix]
    private static bool SetRansomAmountPrefix()
    {
        if (CallOriginalPolicy.IsOriginalAllowed()) return true;

        return ModInformation.IsServer;
    }
}
