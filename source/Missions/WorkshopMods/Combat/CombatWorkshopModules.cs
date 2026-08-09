using GameInterface.AutoSync;
using GameInterface.Services.WorkshopMods.Core;
using System;

namespace Missions.WorkshopMods.Combat;

/// <summary>
/// The combat mods' side of <see cref="IWorkshopModule"/>. They live in the Missions assembly, next
/// to the adapters they gate, and go through the same <see cref="WorkshopModuleRegistrar"/> as
/// Diplomacy — so "installed" means the same thing for every module Coop integrates: the audited
/// build is loaded, byte for byte, not merely something with the right assembly name.
/// </summary>
internal abstract class CombatWorkshopModule : IWorkshopModule
{
    private readonly ModuleFingerprint fingerprint;

    /// <param name="principalAssemblyName">
    /// The assembly whose digest stands for the module. RBM ships five DLLs and
    /// <see cref="ModuleFingerprint"/> pins one, so the fingerprint names the principal assembly
    /// while <see cref="ResolveInstalledSha256"/> keeps the stricter family-wide check: it answers
    /// only when EVERY assembly in the family matched its own audited digest.
    /// </param>
    protected CombatWorkshopModule(string principalAssemblyName)
    {
        CombatModFingerprint audited = CombatModFingerprintCatalog.Find(principalAssemblyName)
            ?? throw new InvalidOperationException(
                $"No audited fingerprint is declared for optional combat assembly '{principalAssemblyName}'.");

        // Built once, in the constructor, so a malformed catalog entry fails at a single
        // deterministic point during container build rather than on whichever access comes first.
        fingerprint = new ModuleFingerprint(
            audited.AssemblyName,
            audited.AssemblyVersion.ToString(),
            audited.Sha256);
    }

    public abstract string ModuleId { get; }

    public abstract ulong WorkshopId { get; }

    public ModuleFingerprint Fingerprint => fingerprint;

    public abstract string PatchCategory { get; }

    /// <summary>Which audited build family this module is.</summary>
    protected abstract CombatModFamily Family { get; }

    /// <summary>
    /// Delegated to <see cref="CombatModCompatibilityGuard.IsFamilyCompatible"/>, which returns true
    /// only after hashing every DLL in the family and matching each against
    /// <see cref="CombatModFingerprintCatalog"/>. So the catalog digest returned here IS the measured
    /// one, and the answer this module gives the registrar is the same answer the adapters' own
    /// runtime gates give. Deriving it from a second, weaker probe is how the two drift apart and a
    /// category gets registered for patches that cannot bind.
    /// </summary>
    public string ResolveInstalledSha256() =>
        CombatModCompatibilityGuard.IsFamilyCompatible(Family) ? fingerprint.Sha256 : null;

    /// <summary>
    /// Empty for all three combat mods, and not for Diplomacy's reason. These are mission-scoped:
    /// their state is per-battle presentation and collision resolution, authored by the peer that
    /// owns the agent and carried by the existing mission P2P messages. There is no campaign-lifetime
    /// member for AutoSync to replicate. Overriding this is how a future combat mod that does keep
    /// campaign state would opt in.
    /// </summary>
    public virtual void RegisterSync(AutoSyncRegistry registry)
    {
    }
}

/// <summary>RBM 4.3.4 — five audited assemblies, all of which must match for the family to count.</summary>
internal sealed class RbmModule : CombatWorkshopModule
{
    internal RbmModule() : base("RBM") { }

    public override string ModuleId => "RBM";
    public override ulong WorkshopId => 2859251492UL;
    public override string PatchCategory => WorkshopPatchCategories.Rbm;
    protected override CombatModFamily Family => CombatModFamily.Rbm434;
}

/// <summary>DismembermentPlus 2.0.8.7.</summary>
internal sealed class DismembermentPlusModule : CombatWorkshopModule
{
    internal DismembermentPlusModule() : base("DismembermentPlus") { }

    public override string ModuleId => "DismembermentPlus";
    public override ulong WorkshopId => 2875093027UL;
    public override string PatchCategory => WorkshopPatchCategories.DismembermentPlus;
    protected override CombatModFamily Family => CombatModFamily.DismembermentPlus2087;
}

/// <summary>
/// UnblockableThrust 1.1.3.1, declared with no patch category.
/// <para>
/// Its adapter, <see cref="UnblockableThrustAuthorityPatch"/>, patches
/// <c>MissionCombatMechanicsHelper.GetDefendCollisionResults</c> — a native method that always
/// resolves — so it has none of the empty-TargetMethods defect the category split exists to fix, and
/// it must keep applying Coop's authoritative crush-through defaults even when the mod is absent.
/// Gating it on presence would be a regression, so <see cref="WorkshopPatchCategories.UnblockableThrust"/>
/// stays unused and the module gate lives inside the postfix instead — on
/// <see cref="CombatModCompatibilityGuard.IsFamilyCompatible"/>, which is the same byte-exact pin the
/// registrar applies here. Declaring the module anyway is what puts its fingerprint, its Workshop id,
/// its catalog reconciliation and its operator config key in the same one place as every other mod's.
/// </para>
/// </summary>
internal sealed class UnblockableThrustModule : CombatWorkshopModule
{
    internal UnblockableThrustModule() : base("UnblockableThrust") { }

    public override string ModuleId => "UnblockableThrust";
    public override ulong WorkshopId => 3614435151UL;
    public override string PatchCategory => null;
    protected override CombatModFamily Family => CombatModFamily.UnblockableThrust1131;
}
