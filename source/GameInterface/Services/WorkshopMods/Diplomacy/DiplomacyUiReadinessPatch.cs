using Common.Logging;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Co-op client UI-readiness guard for the surfaces Bannerlord.Diplomacy feeds, so a Diplomacy state
/// that is not yet initialized on the client cannot abort a whole screen tick.
///
/// Diplomacy's UI reads its manager singletons directly, and on the co-op client those are only created
/// by the join-handshake snapshot apply (<c>DiplomacyRuntime.ApplySnapshot</c> → <c>EnsureManager</c>).
/// Two surfaces dereference them unguarded before that lands:
/// <list type="bullet">
/// <item><b>Kingdom tab</b> — opening it builds <c>KingdomManagementVM → KingdomDiplomacyVM →
/// KingdomWarItemVM</c>, whose Diplomacy mixin ctor runs <c>DiplomacyCostCalculator
/// .DetermineInfluenceCostForMakingPeace</c> (war-exhaustion + expansionism). With those managers null it
/// NREs, leaving the kingdom VM half-built; <see cref="!:GauntletKingdomScreen"/><c>.OnFrameTick</c> then
/// NREs every frame — the whole kingdom tab is dead and the native renderer can crash on the broken VM
/// (observed live 2026-08-12: entered KingdomState → NRE storm → 0xC0000005).</item>
/// <item><b>Encyclopedia faction page</b> — <c>EncyclopediaFactionPageVMMixin.OnRefresh</c> reads the
/// agreement manager (<c>HasNonAggressionPact</c>) and NREs the same way, bricking the map UI.</item>
/// </list>
///
/// The real fix is to pre-create those managers on the client so the reads succeed
/// (<see cref="DiplomacyClientInitializationPatch"/>). These finalizers are the belt-and-braces net: they
/// swallow the transient <see cref="NullReferenceException"/> at each surface so the rest of the screen
/// tick completes instead of aborting — the same pattern as <c>MapNavigationReadinessPatches</c> and
/// <c>ScoreboardTickReadinessPatch</c>. Targets are resolved by name (no compile-time reference to the
/// SandBox UI or Diplomacy assemblies) and the class is inert when none resolve. Owned by Coop's Harmony,
/// so the client's Diplomacy-patch cleanup — which only unpatches <c>bannerlord.diplomacy</c>-owned
/// patches — leaves it in place; the <see cref="WorkshopPatchCategories.Diplomacy"/> category applies it
/// in the post-Diplomacy-load wave.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class DiplomacyUiReadinessPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(DiplomacyUiReadinessPatch));

    // (typeName, methodName) resolved by name so the class needs no compile-time reference and stays
    // inert when a type is absent this session.
    private static readonly (string Type, string Method)[] Guarded =
    {
        ("SandBox.GauntletUI.GauntletKingdomScreen", "OnFrameTick"),
        ("Diplomacy.ViewModelMixin.EncyclopediaFactionPageVMMixin", "OnRefresh"),
    };

    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach ((string typeName, string methodName) in Guarded)
        {
            Type type = AccessTools.TypeByName(typeName);
            MethodBase method = type == null ? null : AccessTools.Method(type, methodName);
            if (method != null) yield return method;
        }
    }

    // Keep the class out of a categorised PatchAll when nothing resolves, so Harmony does not throw
    // "Undefined target method" — the same defence the other Diplomacy adapters carry.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, MethodBase __originalMethod)
    {
        Exception filtered = FilterTransientReadinessException(__exception);
        if (__exception != null && filtered == null)
        {
            // Diplomacy state not yet ready on the client; skip this frame's work so the screen tick
            // completes. Self-heals once the manager pre-init / snapshot has populated the singletons.
            Logger.Verbose(__exception,
                "Co-op UI readiness: swallowed transient NRE in {Method} (Diplomacy state not ready on client)",
                __originalMethod?.Name);
        }

        return filtered;
    }

    /// <summary>
    /// Only a null dereference is the documented pre-snapshot readiness failure. Propagate everything
    /// else so this narrow guard cannot hide an unrelated defect.
    /// </summary>
    internal static Exception FilterTransientReadinessException(Exception exception) =>
        exception is NullReferenceException ? null : exception;
}
