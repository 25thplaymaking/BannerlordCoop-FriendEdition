using GameInterface.AutoSync;

namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// Everything Coop needs to know to integrate one Workshop module. Implementing this is the entire
/// surface a new mod requires: <see cref="WorkshopModuleRegistrar"/> handles presence, fingerprinting,
/// config gating, patch application and sync registration on its behalf.
/// </summary>
public interface IWorkshopModule
{
    /// <summary>
    /// The module's folder/Id as the launcher knows it, e.g. <c>Bannerlord.Diplomacy</c>. Must match
    /// the <c>FriendEditionWorkshopModuleCatalog</c> entry for the same mod — that agreement is what
    /// lets an operator's config key, the packaged suite receipt and this declaration name one thing.
    /// </summary>
    string ModuleId { get; }

    /// <summary>Steam Workshop file id, for diagnostics and operator-facing messages.</summary>
    ulong WorkshopId { get; }

    /// <summary>The exact assembly build this declaration was audited against.</summary>
    ModuleFingerprint Fingerprint { get; }

    /// <summary>
    /// Harmony category holding this module's adapter patches, applied only when the module resolves.
    /// <para>
    /// <c>null</c> is legitimate and means "this module owns no presence-gated category". That is the
    /// right answer when a module's adapters patch methods that always resolve — a native TaleWorlds
    /// method, say — because such patches have none of the empty-TargetMethods defect the category
    /// split exists to fix, and moving them behind a presence gate would stop Coop applying its own
    /// authoritative behaviour whenever the mod is absent. Those adapters gate on the module at call
    /// time instead. <c>UnblockableThrustModule</c> is the worked example.
    /// </para>
    /// </summary>
    string PatchCategory { get; }

    /// <summary>
    /// SHA-256 of the module assembly this process would actually patch, or <c>null</c> when there is
    /// no single verified implementation loaded.
    /// <para>
    /// Returning a hash is a promise, not a hint: the registrar takes a match against
    /// <see cref="Fingerprint"/> as authority to register <see cref="PatchCategory"/>, and a category
    /// whose patch classes resolve no targets throws out of <c>Harmony.PatchCategory</c> and aborts
    /// EVERY remaining Coop patch. An implementation that cannot prove which assembly it measured
    /// must return <c>null</c>.
    /// </para>
    /// </summary>
    string ResolveInstalledSha256();

    /// <summary>
    /// Declare the module members whose changes replicate. Members are resolved reflectively from the
    /// mod assembly, so this runs only after presence and fingerprint have been confirmed.
    /// <para>
    /// Registering nothing is a legitimate answer, and the right one whenever the module's shared
    /// state already has an authoritative replication path: two independent writers over the same
    /// campaign state diverge nondeterministically.
    /// </para>
    /// </summary>
    void RegisterSync(AutoSyncRegistry registry);

    // Inbound gameplay intent uses per-mod typed command handlers. A generic reflection/action list
    // is deliberately not part of this contract; IWorkshopCapabilitySource advertises which of
    // those concrete routes are available without becoming an authorization boundary.
}
