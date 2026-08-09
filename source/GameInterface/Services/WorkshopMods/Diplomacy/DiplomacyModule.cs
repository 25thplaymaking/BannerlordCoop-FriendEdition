using GameInterface.AutoSync;
using GameInterface.Services.WorkshopMods.Core;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Diplomacy's declaration — the first module expressed through <see cref="IWorkshopModule"/>, and
/// the shape every following mod copies. Fingerprint values come from
/// <see cref="DiplomacyCompatibilityPolicy"/> so the pin has exactly one source of truth.
/// </summary>
internal sealed class DiplomacyModule : IWorkshopModule
{
    public string ModuleId => "Bannerlord.Diplomacy";

    public ulong WorkshopId => 2881380744UL;

    public ModuleFingerprint Fingerprint => new ModuleFingerprint(
        DiplomacyCompatibilityPolicy.SupportedAssemblyName,
        DiplomacyCompatibilityPolicy.SupportedAssemblyVersion,
        DiplomacyCompatibilityPolicy.SupportedAssemblySha256);

    public string PatchCategory => WorkshopPatchCategories.Diplomacy;

    /// <summary>
    /// Delegated to the compatibility policy rather than measured independently. ResolveAssembly()
    /// returns non-null only after it has hashed the loaded DLL and compared it, in fixed time,
    /// against this same constant — and after confirming every type and method shape the adapters
    /// patch. So the constant IS the measured digest here, and the answer this module gives the
    /// registrar is exactly the answer the adapters' own [HarmonyPrepare] guards will give.
    /// Deriving it from a second, weaker probe is how the two drift apart and a category gets
    /// registered for patches that cannot bind.
    /// </summary>
    public string ResolveInstalledSha256() =>
        DiplomacyCompatibilityPolicy.ResolveAssembly() == null
            ? null
            : DiplomacyCompatibilityPolicy.SupportedAssemblySha256;

    /// <summary>
    /// Deliberately empty.
    /// <para>
    /// Every piece of Diplomacy state worth replicating is already replicated, and not by AutoSync:
    /// <see cref="DiplomacyRuntime"/> captures and applies expansionism, all three cooldown
    /// dictionaries (<c>_lastPeaceProposalTime</c>, <c>_lastAllianceFormedTime</c>,
    /// <c>_lastWarTime</c>), non-aggression agreements and the war-exhaustion score/rate tables as
    /// one server-authoritative, revisioned, validated snapshot. Registering those same members here
    /// would put a second writer on the same campaign state, and two independent replication paths
    /// over one dictionary diverge nondeterministically — the exact failure this integration exists
    /// to remove.
    /// </para>
    /// <para>
    /// Nor is there a spare member to pick up. The only field on <c>CooldownBehavior</c> is
    /// <c>_cooldownManager</c> (verified by decompiling Bannerlord.Diplomacy.1.4.7): a manager object
    /// assigned once in the constructor and again while a save loads, never during play. The
    /// mutations happen inside it, in dictionaries keyed by <c>Kingdom</c> and valued by Diplomacy's
    /// own record types, which AutoSync's serializers do not know how to move. Syncing the reference
    /// would replicate nothing and cost a patch.
    /// </para>
    /// <para>
    /// An honest empty registration is the correct declaration here; it is not a gap left for later.
    /// </para>
    /// </summary>
    public void RegisterSync(AutoSyncRegistry registry)
    {
    }
}
