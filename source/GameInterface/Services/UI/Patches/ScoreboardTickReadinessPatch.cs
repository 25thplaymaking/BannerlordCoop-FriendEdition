using Common.Logging;
using HarmonyLib;
using SandBox.ViewModelCollection;
using Serilog;
using System;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.UI.Patches;

/// <summary>
/// Co-op retreat-mid-battle scoreboard softlock guard.
///
/// When a player retreats out of a real battle mission, the co-op server authoritatively finalizes the
/// battle and tells the client to close the encounter AND destroy the local <c>MapEvent</c>
/// (see <c>PvPInteractionClientHandler.ClosePvpEncounter</c> and <c>MapEventRegistry.OnClientDestroyed</c>).
/// That teardown races the native <see cref="SPScoreboardVM"/> — the battle-result scoreboard still open
/// over the mission. It strips <c>PlayerEncounter.Battle</c> out from under the scoreboard, so
/// <c>SPScoreboardVM.OnBattleOver</c> can never latch <c>IsOver</c> (it requires a live
/// <c>PlayerEncounter.Battle</c>). The scoreboard is then stuck in its <c>!IsOver</c> branch, and once
/// <see cref="Mission.Current"/> finalizes to null under the lingering gauntlet layer, its per-frame
/// <c>OnTick</c> reads an unguarded <c>(int)Mission.Current.CurrentTime</c> and throws a
/// <see cref="NullReferenceException"/> every frame. The global <c>ScreenManager.Tick</c> robustness
/// finalizer then aborts the WHOLE screen tick on that throw, so the UI freezes with no native crash —
/// unless the player dismisses the dialogue fast enough to win the race before the encounter is torn out.
/// (Confirmed live 2026-08-12: "send troops"/retreat on <c>MapEvent_Created_21618</c>, encounter cleared
/// ~2 s later, then 3,378 identical <c>OnTick</c> throws until the player force-closed the game.)
///
/// This finalizer swallows that transient throw so the rest of the screen tick completes and the
/// scoreboard's exit button stays responsive — the player can dismiss it and the mission unwinds
/// normally, exactly as the map-load nav-bar guard in <c>MapNavigationReadinessPatches</c> does.
/// It is scoped to the precise softlock precondition — a <see cref="NullReferenceException"/> while
/// <see cref="Mission.Current"/> is null — so it cannot mask any scoreboard defect that happens with a
/// live mission. Full auto-resolve never opens a mission scoreboard, so it is unaffected.
/// </summary>
[HarmonyPatch(typeof(SPScoreboardVM), "OnTick")]
internal static class ScoreboardTickReadinessPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(ScoreboardTickReadinessPatch));

    [HarmonyFinalizer]
    private static Exception OnTick_Finalizer(Exception __exception)
    {
        var filtered = FilterTransientScoreboardException(__exception);
        if (__exception != null && filtered == null)
        {
            // Scoreboard ticked one frame past its torn-down encounter during a co-op battle teardown.
            // Skipping this frame's tick self-heals; the exit button stays live so the player can dismiss.
            Logger.Verbose(__exception,
                "Scoreboard OnTick skipped after co-op encounter teardown (Mission.Current gone)");
        }

        return filtered;
    }

    /// <summary>
    /// Only a null dereference while there is no current mission is the documented retreat-teardown
    /// softlock. Propagate every other exception — and any NRE that occurs with a live mission — so this
    /// narrow guard cannot hide a real scoreboard defect.
    /// </summary>
    internal static Exception FilterTransientScoreboardException(Exception exception) =>
        exception is NullReferenceException && Mission.Current == null ? null : exception;
}
