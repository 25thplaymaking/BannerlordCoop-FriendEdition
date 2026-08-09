namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// Harmony patch categories for Workshop module adapters. Every adapter patch class MUST carry one.
/// An uncategorised adapter patch is applied by Harmony.PatchAllUncategorized during
/// GameInterface.PatchAll(), and a TargetMethods() that resolves nothing — which is exactly what
/// happens when the mod is not installed — throws there and aborts EVERY remaining Coop patch,
/// AutoSync included. Categories let the registrar apply an adapter only when its module is live.
/// </summary>
/// <remarks>
/// Every name here must be one a real adapter carries, because this type is the vocabulary
/// <c>WorkshopModuleTestBase.PatchCategory_IsOneOfTheDeclaredWorkshopCategoriesOrNone</c> checks a
/// module's <c>PatchCategory</c> against. A name nothing applies makes that gate accept a typo, which
/// is precisely the "registered but never applied" mistake the gate exists to catch.
/// <para>
/// Constants for ImprovedGarrisons, Fourberie and Player Settlement were removed for that reason.
/// Those three mods carry no Harmony attributes at all — their adapters call <c>harmony.Patch</c>
/// imperatively from their compatibility handlers — so there is nothing for a category to gate, and
/// declaring them through <c>IWorkshopModule</c> later would still give them a null
/// <c>PatchCategory</c>. They were aspirational, not reserved. Add a name back when, and only when,
/// an adapter class carries the matching <c>[HarmonyPatchCategory]</c>.
/// </para>
/// </remarks>
internal static class WorkshopPatchCategories
{
    internal const string Diplomacy = "CoopWorkshopDiplomacyPatches";

    // Missions-assembly adapters. CombatHitPresentationPatchCategory (see MissionModule) stays
    // separate and unconditional: it carries patches that must always apply (e.g.
    // MeleeHitPresentationPatch) and must never be gated on an optional mod's presence.
    internal const string Rbm = "CoopWorkshopRbmPatches";
    internal const string DismembermentPlus = "CoopWorkshopDismembermentPlusPatches";

    /// <summary>
    /// RESERVED, and deliberately applied by nothing — the one name here that is intentionally
    /// unused rather than aspirational. UnblockableThrust's adapter
    /// (<c>UnblockableThrustAuthorityPatch</c>) patches
    /// <c>MissionCombatMechanicsHelper.GetDefendCollisionResults</c>, a native method that resolves
    /// whether or not the mod is loaded, so it has none of the empty-TargetMethods defect this split
    /// exists to fix, and it must keep applying Coop's authoritative crush-through defaults when the
    /// mod is absent. Gating it on presence would be a regression, so <c>UnblockableThrustModule</c>
    /// declares <c>PatchCategory => null</c> and the mod-presence gate lives inside the postfix
    /// instead. Kept as the anchor both of those code comments point at; do not apply it.
    /// </summary>
    internal const string UnblockableThrust = "CoopWorkshopUnblockableThrustPatches";
}
