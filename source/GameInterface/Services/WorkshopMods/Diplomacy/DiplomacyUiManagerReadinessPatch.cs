using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Guarantees Diplomacy's manager singletons exist on the co-op client at the exact moment its UI reads
/// them, so opening the Kingdom → Diplomacy tab (or an encyclopedia faction page) never dereferences a
/// null manager.
///
/// <para>
/// The client only gets those managers created eagerly by <see cref="DiplomacyClientInitializationPatch"/>'s
/// <c>MapScreen.OnInitialize</c> prefix, which fires during the character-creation → initial-map handoff.
/// After the client then loads the host's save into a fresh campaign, each manager's static
/// <c>Instance</c> is null again and <c>MapScreen.OnInitialize</c> does not re-fire. Opening the Kingdom
/// tab then runs <c>KingdomWarItemVMMixin..ctor → DiplomacyCostCalculator
/// .DetermineInfluenceCostForMakingPeace</c>, which reads <c>WarExhaustionManager.Instance</c> unguarded
/// and NREs, leaving the kingdom VM half-built; <c>GauntletKingdomScreen.OnFrameTick</c> then NREs every
/// frame and freezes input (observed live 2026-08-12: entered KingdomState → single build NRE → ~2.4k
/// per-frame NREs). The same null bricks the encyclopedia faction page.
/// </para>
///
/// <para>
/// This re-creates the managers idempotently right before those surfaces refresh — the precise,
/// timing-proof point — so the reads succeed and the tab renders normally. It is deliberately targeted at
/// <c>KingdomDiplomacyVM.RefreshValues</c> (the top-level refresh that precedes every diplomacy sub-list —
/// wars, alliances, NAPs) in the always-loaded <c>TaleWorlds.CampaignSystem</c>
/// assembly (rather than a SandBox screen type, which is not yet loaded when the co-op container patches
/// at boot) and is <b>uncategorised</b> so it applies through <c>GameInterface.PatchAllUncategorized</c>
/// regardless of Workshop patch-category wiring. Inert on the server: <see cref="DiplomacyClientInitializationPatch.EnsureClientManagers"/>
/// is client-only and the Diplomacy behaviours create the managers there anyway.
/// </para>
/// </summary>
[HarmonyPatch]
internal static class DiplomacyUiManagerReadinessPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(DiplomacyUiManagerReadinessPatch));

    // (typeName, methodName) resolved by name so the class needs no compile-time reference and stays
    // inert when a surface is absent this session. The kingdom-tab VM ships in the always-loaded
    // TaleWorlds.CampaignSystem assembly; the encyclopedia mixin in the optional Diplomacy assembly.
    private static readonly (string Type, string Method)[] Guarded =
    {
        ("TaleWorlds.CampaignSystem.ViewModelCollection.KingdomManagement.Diplomacy.KingdomDiplomacyVM", "RefreshValues"),
        ("Diplomacy.ViewModelMixin.EncyclopediaFactionPageVMMixin", "OnRefresh"),
    };

    // Belt-and-braces: the only Diplomacy mixins that read the manager singletons (verified against
    // Bannerlord.Diplomacy 1.4.7 — WarExhaustion/Agreement managers) are these three. Guarding their
    // constructors directly guarantees the managers exist no matter which VM refresh path builds them.
    private static readonly string[] GuardedMixinConstructors =
    {
        "Diplomacy.ViewModelMixin.KingdomWarItemVMMixin",
        "Diplomacy.ViewModelMixin.KingdomTruceItemVMMixin",
        "Diplomacy.ViewModelMixin.EncyclopediaFactionPageVMMixin",
    };

    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach ((string typeName, string methodName) in Guarded)
        {
            Type type = AccessTools.TypeByName(typeName);
            MethodBase method = type == null ? null : AccessTools.Method(type, methodName);
            if (method != null) yield return method;
        }

        foreach (string typeName in GuardedMixinConstructors)
        {
            Type type = AccessTools.TypeByName(typeName);
            MethodBase ctor = type == null
                ? null
                : AccessTools.GetDeclaredConstructors(type)?.FirstOrDefault(c => !c.IsStatic);
            if (ctor != null) yield return ctor;
        }
    }

    // Keep the class out of PatchAll when nothing resolves, so Harmony does not throw
    // "Undefined target method" — the same defence the other Diplomacy adapters carry.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static void Prefix()
    {
        try
        {
            DiplomacyClientInitializationPatch.EnsureClientManagers();
        }
        catch (Exception ex)
        {
            // EnsureClientManagers already guards each manager individually; this is the belt-and-braces
            // net so a surprise here can never abort the VM refresh it precedes.
            Logger.Error(ex, "Failed to ensure Diplomacy client managers before UI refresh");
        }
    }
}
