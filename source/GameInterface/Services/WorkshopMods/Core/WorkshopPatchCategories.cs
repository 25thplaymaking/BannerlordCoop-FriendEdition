namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// Harmony patch categories for Workshop module adapters. Every adapter patch class MUST carry one.
/// An uncategorised adapter patch is applied by Harmony.PatchAllUncategorized during
/// GameInterface.PatchAll(), and a TargetMethods() that resolves nothing — which is exactly what
/// happens when the mod is not installed — throws there and aborts EVERY remaining Coop patch,
/// AutoSync included. Categories let the registrar apply an adapter only when its module is live.
/// </summary>
internal static class WorkshopPatchCategories
{
    internal const string Diplomacy = "CoopWorkshopDiplomacyPatches";
    internal const string ImprovedGarrisons = "CoopWorkshopImprovedGarrisonsPatches";
    internal const string Fourberie = "CoopWorkshopFourberiePatches";
    internal const string PlayerSettlement = "CoopWorkshopPlayerSettlementPatches";

    // Missions-assembly adapters. CombatHitPresentationPatchCategory (see MissionModule) stays
    // separate and unconditional: it carries patches that must always apply (e.g.
    // MeleeHitPresentationPatch) and must never be gated on an optional mod's presence.
    internal const string Rbm = "CoopWorkshopRbmPatches";
    internal const string DismembermentPlus = "CoopWorkshopDismembermentPlusPatches";
    internal const string UnblockableThrust = "CoopWorkshopUnblockableThrustPatches";
}
