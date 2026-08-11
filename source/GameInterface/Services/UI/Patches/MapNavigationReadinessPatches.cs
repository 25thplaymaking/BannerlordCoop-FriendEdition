using Common.Logging;
using HarmonyLib;
using SandBox.View.Map;
using SandBox.View.Map.Navigation;
using Serilog;
using System;

namespace GameInterface.Services.UI.Patches;

/// <summary>
/// Co-op client map-load NRE guard.
///
/// Coop force-ticks the <c>MapState</c> while the campaign is still activating
/// (see <c>GameStateManagerPatches.OnTick</c> and <c>MapStatePatch</c>). During that window
/// the map bar's navigation refresh runs before the map screen is fully wired, so the stock
/// <see cref="MapNavigationHelper.IsNavigationBarEnabled"/> throws a
/// <see cref="NullReferenceException"/> on one of several not-yet-initialized members
/// (the <c>MapNavigationHandler</c> itself, <c>MapScreen.EncyclopediaScreenManager</c>, …).
/// The global <c>ScreenManager.Tick</c> robustness finalizer then aborts the WHOLE screen
/// tick on that throw, so the map bar never finishes initializing and the HUD stays dead —
/// the client sits frozen on the map with no UI (surfaced once the campaign mods lengthen
/// the load window).
///
/// Rather than guess which member is null (they vary by frame), these finalizers swallow the
/// transient exception at the innermost method and report the nav bar "disabled" for that
/// frame. The map-bar refresh then runs to completion instead of aborting; once the map
/// finishes activating the members are populated and stock behavior resumes — the real HUD
/// renders. This is scoped to the two map-load methods, not a blanket catch.
/// </summary>
[HarmonyPatch]
internal static class MapNavigationReadinessPatches
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(MapNavigationReadinessPatches));

    [HarmonyPatch(typeof(MapNavigationHelper), nameof(MapNavigationHelper.IsNavigationBarEnabled))]
    [HarmonyFinalizer]
    private static Exception IsNavigationBarEnabled_Finalizer(Exception __exception, ref bool __result)
    {
        if (__exception != null)
        {
            // Nav bar treated as disabled until the map screen is fully wired; self-heals.
            __result = false;
            Logger.Verbose(__exception, "Map nav-bar readiness check skipped during co-op load window");
        }

        return null; // swallow so it does not bubble to the global ScreenManager.Tick finalizer
    }

    [HarmonyPatch(typeof(MapScreen), "HandleIfBlockerStatesDisabled")]
    [HarmonyFinalizer]
    private static Exception HandleIfBlockerStatesDisabled_Finalizer(Exception __exception)
    {
        // Render-readiness helper. A transient null during the co-op load window is harmless
        // to skip and self-heals next frame; swallow it so it does not abort Game.OnTick.
        if (__exception != null)
        {
            Logger.Verbose(__exception, "Map blocker-state check skipped during co-op load window");
        }

        return null;
    }
}
