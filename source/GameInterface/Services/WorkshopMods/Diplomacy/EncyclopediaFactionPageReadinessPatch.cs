using Common.Logging;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using Serilog;
using System;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Co-op client Encyclopedia faction-page freeze guard.
///
/// Diplomacy's <c>EncyclopediaFactionPageVMMixin.OnRefresh</c> (a UIExtenderEx VM mixin, so it is NOT
/// one of the Harmony patches the client strips in <see cref="DiplomacyPatchCompatibilityGate"/>) calls
/// <c>DiplomaticAgreementManager.HasNonAggressionPact(...)</c>, whose first line dereferences the
/// <c>DiplomaticAgreementManager.Instance</c> singleton (<c>Instance!.…</c> — safe in single-player,
/// where Diplomacy's own behaviour always creates the manager, but unguarded in co-op). On the co-op
/// client that singleton is only created inside the join-handshake snapshot apply
/// (<c>DiplomacyRuntime.ApplySnapshot</c> → <c>EnsureManager</c>); until that has run, <c>Instance</c> is
/// null and clicking a kingdom/faction encyclopedia link throws a <see cref="NullReferenceException"/>
/// out of <c>EncyclopediaFactionPageVM..ctor</c>. That throw unwinds through <c>ScreenManager.Tick</c>,
/// whose blanket robustness finalizer aborts the WHOLE frame's tick; the pending link is never consumed
/// and re-fires every frame via <c>MapScreen.TickNavigationInput</c>, so the map UI is bricked (hotkeys
/// and the N encyclopedia key dead, only raw movement alive) until the game is closed.
/// (Confirmed live 2026-08-12: Encyclopedia → Kingdom link, then the map froze and hotkeys died.)
///
/// This finalizer swallows exactly that throw so <c>OnRefresh</c> — and therefore the VM constructor and
/// <c>ExecuteLink</c> — complete: the page opens (its Diplomacy non-aggression-pact row renders blank
/// until the manager exists) and the map tick keeps running, so nothing bricks. It is the same
/// finalizer-swallows-transient-NRE pattern the codebase already uses for the map nav bar
/// (<c>MapNavigationReadinessPatches</c>) and the retreat scoreboard (<c>ScoreboardTickReadinessPatch</c>).
/// Paired with a client-side manager pre-init so the null stops happening at the source
/// (see <c>DiplomacyClientInitializationPatch</c>), this guard remains as the belt-and-braces net.
///
/// Owned by Coop's Harmony instance (not <c>bannerlord.diplomacy</c>), so the client patch cleanup in
/// <see cref="DiplomacyPatchCompatibilityGate"/> — which only unpatches Diplomacy-owned patches — leaves
/// it in place, and the <see cref="WorkshopPatchCategories.Diplomacy"/> category applies it in the same
/// post-Diplomacy-load wave as the other Diplomacy adapters (so the target type resolves).
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class EncyclopediaFactionPageReadinessPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(EncyclopediaFactionPageReadinessPatch));

    private static MethodBase TargetMethod()
    {
        var type = DiplomacyCompatibilityPolicy.ResolveType(
            "Diplomacy.ViewModelMixin.EncyclopediaFactionPageVMMixin");
        return type == null ? null : AccessTools.Method(type, "OnRefresh");
    }

    // Keep the class inert when Diplomacy is absent (empty target) so Harmony's category apply does not
    // throw "Undefined target method" — the same defence the other Diplomacy adapters carry.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethod() != null;

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception)
    {
        var filtered = FilterTransientEncyclopediaException(__exception);
        if (__exception != null && filtered == null)
        {
            // Diplomacy read its agreement manager before the client snapshot created it; skip this
            // refresh so the page opens and the map tick survives. Self-heals once the snapshot applies.
            Logger.Verbose(__exception,
                "Diplomacy encyclopedia faction-page refresh skipped before agreement manager was ready");
        }

        return filtered;
    }

    /// <summary>
    /// Only a null dereference (the missing <c>DiplomaticAgreementManager.Instance</c>) is the documented
    /// pre-snapshot readiness failure. Propagate every other exception so this narrow guard cannot hide a
    /// real Diplomacy encyclopedia defect.
    /// </summary>
    internal static Exception FilterTransientEncyclopediaException(Exception exception) =>
        exception is NullReferenceException ? null : exception;
}
