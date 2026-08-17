using Common;
using Common.Logging;
using GameInterface.Policies;
using Helpers;
using HarmonyLib;
using Serilog;
using System;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Roster;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// Stops a client locally simulating a battle the server owns.
/// </summary>
/// <remarks>
/// Leaving an encounter runs <c>MenuHelper.EncounterLeaveConsequence</c>, whose tail simulates whatever is
/// left of the battle when the player walks away from one still in progress:
///
///     battle.SimulateBattleSetup(PlayerEncounter.Current?.BattleSimulation?.SelectedTroops);
///     battle.SimulateBattleRound(...);
///
/// In coop that decision belongs to the server, which has already resolved the event - so this is a desync
/// by construction. It is also an outright crash. <c>SimulateBattleSetup</c> indexes the roster array by each
/// side's mission side:
///
///     roster = selectedTroops == null ? null : selectedTroops[side.MissionSide];
///
/// and on a client whose sides were never assigned one, <c>MissionSide</c> is <c>None</c> - which is -1, not a
/// valid index. Observed live after a won sally-out: every click of "Leave..." threw
/// IndexOutOfRangeException out of the menu consequence, so the encounter never finished and the player was
/// pinned on the menu with no way off it while everyone else had moved on.
///
/// Scoped to the leave consequence ITSELF, via <see cref="inEncounterLeave"/>, and not to client-side
/// simulation in general. That distinction is the whole patch: "send troops" and every other auto-resolve
/// draws its battle screen by simulating locally, through these same two methods. Suppressing those as well
/// skips <c>MakeReadyForSimulation</c> on each side and the <c>_battleState</c> reset, so the player gets no
/// battle screen and the event is left showing the enemy at zero troops while the player still holds all of
/// theirs - which is precisely what an earlier, broader version of this guard caused.
///
/// The encounter-leave scope itself is the ownership proof. A server teardown can unregister the event
/// before the rendered client processes the leave-menu click, so consulting the object registry here
/// creates a false negative in precisely the crash window this guard exists to cover. The server's own path
/// is untouched, and it passes null for the roster, which is the branch that never indexes.
/// </remarks>
[HarmonyPatch]
internal class ClientBattleSimulationGuardPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<ClientBattleSimulationGuardPatch>();

    // True only while MenuHelper.EncounterLeaveConsequence is on the stack. Game-thread only; ThreadStatic is
    // belt-and-braces, matching CoopEmptyTeamDeploymentPatch's scoping of the same kind of override.
    [ThreadStatic] private static bool inEncounterLeave;

    [HarmonyPatch(typeof(MenuHelper), nameof(MenuHelper.EncounterLeaveConsequence))]
    [HarmonyPrefix]
    private static void EncounterLeavePrefix() => inEncounterLeave = true;

    // Finalizer, not postfix: the method is expected to throw here, and the flag must clear either way.
    [HarmonyPatch(typeof(MenuHelper), nameof(MenuHelper.EncounterLeaveConsequence))]
    [HarmonyFinalizer]
    private static void EncounterLeaveFinalizer() => inEncounterLeave = false;

    [HarmonyPatch(typeof(MapEvent), nameof(MapEvent.SimulateBattleSetup), new[] { typeof(FlattenedTroopRoster[]) })]
    [HarmonyPrefix]
    private static bool SimulateBattleSetupPrefix(MapEvent __instance)
        => !SuppressLocalSimulation(__instance, nameof(MapEvent.SimulateBattleSetup));

    [HarmonyPatch(typeof(MapEvent), nameof(MapEvent.SimulateBattleRound), new[] { typeof(int), typeof(int) })]
    [HarmonyPrefix]
    private static bool SimulateBattleRoundPrefix(MapEvent __instance)
        => !SuppressLocalSimulation(__instance, nameof(MapEvent.SimulateBattleRound));

    private static bool SuppressLocalSimulation(MapEvent mapEvent, string step)
    {
        // The one path that must not simulate. Everything else - auto-resolve, "send troops", any battle the
        // player watches resolve without a mission - needs these to run.
        if (!ShouldSuppressLocalSimulation(
                inEncounterLeave,
                CallOriginalPolicy.IsOriginalAllowed(),
                ModInformation.IsServer,
                mapEvent != null))
            return false;

        string mapEventId = mapEvent.StringId ?? "<unregistered>";

        Logger.Warning(
            "[BattleSync] Skipping local {Step} for server-owned battle {MapEventId}; the server resolves it",
            step, mapEventId);
        return true;
    }

    internal static bool ShouldSuppressLocalSimulation(
        bool isInEncounterLeave,
        bool isOriginalAllowed,
        bool isServer,
        bool hasMapEvent) =>
        isInEncounterLeave && !isOriginalAllowed && !isServer && hasMapEvent;
}
