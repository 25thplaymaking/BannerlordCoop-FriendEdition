using System;
using System.Collections.Generic;
using System.Linq;

namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// Describes where a bundled Workshop component is allowed to make decisions.
/// This is deliberately independent of the original mod assemblies: Friend Edition
/// consumes the compatibility contract without loading or referencing those assemblies.
/// </summary>
public enum WorkshopModuleRole
{
    Framework = 1,
    Mission = 2,
    Presentation = 3,
    Campaign = 4,
}

/// <summary>
/// The synchronization contract applied to a bundled Workshop component.
/// </summary>
public enum WorkshopCompatibilityProfile
{
    AllPeersExact = 1,
    DeterministicMission = 2,
    ClientPresentation = 3,
    ServerAuthoritativeCampaign = 4,
}

public sealed class WorkshopModuleExpectation
{
    public WorkshopModuleExpectation(
        string moduleId,
        string workshopId,
        string steamManifestId,
        string version,
        int loadOrder,
        WorkshopModuleRole role,
        WorkshopCompatibilityProfile profile,
        bool featureActiveExpectedOnServer = false,
        bool featureActiveExpectedOnClient = false,
        bool loadsBeforeCoop = false)
    {
        LoadsBeforeCoop = loadsBeforeCoop;
        ModuleId = moduleId ?? throw new ArgumentNullException(nameof(moduleId));
        WorkshopId = workshopId ?? throw new ArgumentNullException(nameof(workshopId));
        SteamManifestId = steamManifestId ?? throw new ArgumentNullException(nameof(steamManifestId));
        Version = version ?? throw new ArgumentNullException(nameof(version));
        LoadOrder = loadOrder;
        Role = role;
        Profile = profile;
        FeatureActiveExpectedOnServer = featureActiveExpectedOnServer;
        FeatureActiveExpectedOnClient = featureActiveExpectedOnClient;
    }

    public string ModuleId { get; }
    public string WorkshopId { get; }
    public string SteamManifestId { get; }
    public string Version { get; }
    public int LoadOrder { get; }
    public WorkshopModuleRole Role { get; }
    public WorkshopCompatibilityProfile Profile { get; }
    /// <summary>
    /// The exact activation state permitted for each peer role. This is an equality policy, not a
    /// minimum requirement: an entry that is active when false is rejected just as an entry that
    /// is inactive when true is rejected. Every component remains package-required and
    /// receipt/hash verified regardless of activation state.
    /// </summary>
    public bool FeatureActiveExpectedOnServer { get; }
    public bool FeatureActiveExpectedOnClient { get; }

    /// <summary>
    /// True when this component must be activated AFTER the native modules but BEFORE Coop,
    /// instead of the usual after-Coop slot. Coop's adapter for such a component purges and
    /// re-guards the component's module-load Harmony patches, which requires those patches to
    /// already exist when Coop's container is built. Loading it after Coop instead deadlocks or
    /// crashes client startup (verified live for PlayerSettlement).
    /// </summary>
    public bool LoadsBeforeCoop { get; }
}

public interface IWorkshopModuleCatalog
{
    IReadOnlyList<WorkshopModuleExpectation> Modules { get; }
    bool TryGet(string moduleId, out WorkshopModuleExpectation expectation);
}

/// <summary>
/// Canonical private Friend Edition component set. Updating a bundled component is an
/// intentional protocol change: change its version here and every peer will be required
/// to run the same newly packaged build.
/// </summary>
public sealed class FriendEditionWorkshopModuleCatalog : IWorkshopModuleCatalog
{
    // EMPIRES OF EUROPE 1100 LOADOUT.
    //
    // Only the four frameworks are active. Every gameplay and content component below is staged,
    // receipt-verified and hash-checked, but held INACTIVE: EoE 1100 replaces the map, cultures,
    // settlements and troop trees wholesale, and the dedicated host would not boot with the
    // previous loadout layered on top of it.
    //
    // Holding a component here rather than deleting it is deliberate. WorkshopSuiteReceipt
    // .TryValidate requires ModuleCount == catalog.Modules.Count and every receipt entry to resolve
    // through this catalog, so removing entries would invalidate the receipt, make every module
    // read as unmanaged, and refuse every join. Keeping them staged also means flipping one back on
    // is a catalog and token edit, with no repackaging and no multi-gigabyte re-upload.
    //
    // FeatureActiveExpected* is an EQUALITY policy, not a minimum: a component that is active when
    // false is rejected exactly as one that is inactive when true. Both roles must therefore agree,
    // which is why these are flipped in pairs alongside the launcher and server module tokens.
    //
    // Dormancy is safe by construction, and was audited before this change. Every adapter resolves
    // its module through AppDomain.CurrentDomain.GetAssemblies(), so an unactivated module reports
    // not-installed; WorkshopModuleRegistrar.ResolveInstalledModules then drops it, and neither its
    // Harmony patch category nor its AutoSync registration is ever applied. Each compatibility
    // handler's TryInstall() returns false rather than throwing, and every snapshot bootstrap is
    // gated on that result.
    //
    // RBM has no entry at all, which is different from the holds below: it is not part of the
    // package. Its combat-parameter initialization faults the dedicated host natively (exit 84),
    // and shipping it data-only does not help, because the engine applies that XML whether or not
    // its submodule loads.
    private static readonly WorkshopModuleExpectation[] ExpectedModules =
    {
        new("Bannerlord.Harmony", "2859188632", "5023964903723709557", "v2.4.2.248", 0,
            WorkshopModuleRole.Framework, WorkshopCompatibilityProfile.AllPeersExact,
            featureActiveExpectedOnServer: true,
            featureActiveExpectedOnClient: true),
        new("Bannerlord.ButterLib", "2859232415", "6795008217820882669", "v2.11.1", 10,
            WorkshopModuleRole.Framework, WorkshopCompatibilityProfile.AllPeersExact,
            featureActiveExpectedOnServer: true,
            featureActiveExpectedOnClient: true),
        new("Bannerlord.UIExtenderEx", "2859222409", "4162172930197019416", "v2.13.3", 20,
            WorkshopModuleRole.Framework, WorkshopCompatibilityProfile.AllPeersExact,
            featureActiveExpectedOnServer: true,
            featureActiveExpectedOnClient: true),
        new("Bannerlord.MBOptionScreen", "2859238197", "4045451207505706745", "v5.12.2", 30,
            WorkshopModuleRole.Framework, WorkshopCompatibilityProfile.AllPeersExact,
            featureActiveExpectedOnServer: true,
            featureActiveExpectedOnClient: true),
        // These modules contain only XML and assets: all peers load the same exact item surface,
        // but there is no executable campaign authority to adapt.
        new("OpenSourceSaddlery", "3010990914", "5590992806248040986", "v2.0.0", 100,
            WorkshopModuleRole.Presentation, WorkshopCompatibilityProfile.AllPeersExact,
            featureActiveExpectedOnServer: false,
            featureActiveExpectedOnClient: false,
            loadsBeforeCoop: true),
        new("OpenSourceWeaponry", "3010984416", "228880600705709326", "v2.0.1", 110,
            WorkshopModuleRole.Presentation, WorkshopCompatibilityProfile.AllPeersExact,
            featureActiveExpectedOnServer: false,
            featureActiveExpectedOnClient: false,
            loadsBeforeCoop: true),
        new("OpenSourceArmory", "3011479883", "6047321499764171194", "v2.0.0", 120,
            WorkshopModuleRole.Presentation, WorkshopCompatibilityProfile.AllPeersExact,
            featureActiveExpectedOnServer: false,
            featureActiveExpectedOnClient: false,
            loadsBeforeCoop: true),
        new("ImprovedGarrisons", "2859265386", "5143458534246082850", "v4.2.0.7", 210,
            WorkshopModuleRole.Campaign, WorkshopCompatibilityProfile.ServerAuthoritativeCampaign,
            featureActiveExpectedOnServer: false,
            featureActiveExpectedOnClient: false),
        new("DismembermentPlus", "2875093027", "751945004455697202", "v2.0.8.8", 220,
            WorkshopModuleRole.Presentation, WorkshopCompatibilityProfile.ClientPresentation,
            featureActiveExpectedOnServer: false,
            featureActiveExpectedOnClient: false),
        new("Fourberie", "2875710877", "1598945672157391038", "v1.4.7.6", 230,
            WorkshopModuleRole.Campaign, WorkshopCompatibilityProfile.ServerAuthoritativeCampaign,
            featureActiveExpectedOnServer: false,
            featureActiveExpectedOnClient: false),
        new("Bannerlord.Diplomacy", "2881380744", "3938505074920035905", "v1.4.7", 240,
            WorkshopModuleRole.Campaign, WorkshopCompatibilityProfile.ServerAuthoritativeCampaign,
            featureActiveExpectedOnServer: false,
            featureActiveExpectedOnClient: false),
        new("UnblockableThrust", "3614435151", "3108412629025003964", "v1.1.3.1", 250,
            WorkshopModuleRole.Mission, WorkshopCompatibilityProfile.DeterministicMission,
            featureActiveExpectedOnServer: false,
            featureActiveExpectedOnClient: false),
        // PlayerSettlement is HELD INACTIVE for the Empires of Europe 1100 conversion. It
        // builds its settlement prefabs per culture from the native culture set, and EoE replaces
        // that set wholesale; the dedicated host died with a native fault (exit 84) part-way
        // through PlayerSettlement's per-culture prefab XML load on the first EoE boot.
        //
        // It stays in the catalog, the packaging manifest and the receipt so its bytes are still
        // verified and the suite receipt still validates 1:1 against this catalog. Only its
        // activation changes, which is exactly the "staged but inactive" case
        // RuntimeWorkshopModuleDiscovery and WorkshopManifestValidator already handle. Note that
        // FeatureActiveExpected* is an EQUALITY policy: leaving these true while the module is
        // absent from the launch token refuses every join with "must activate 'PlayerSettlement'".
        //
        // LoadsBeforeCoop is retained: it records that, whenever this module IS activated again,
        // its co-op adapter purges and re-guards the module's load-time Harmony patches, so those
        // patches must already exist when Coop's container is built. Activating it after Coop
        // deadlocks or crashes client startup.
        new("PlayerSettlement", "3720376888", "6398100776119441137", "v7.5.0", 160,
            WorkshopModuleRole.Campaign, WorkshopCompatibilityProfile.ServerAuthoritativeCampaign,
            featureActiveExpectedOnServer: false,
            featureActiveExpectedOnClient: false,
            loadsBeforeCoop: true),
        new("RebellionsAndDemographics", "3644127631", "5679238579592221906", "v3.0.1", 260,
            WorkshopModuleRole.Campaign, WorkshopCompatibilityProfile.ServerAuthoritativeCampaign,
            featureActiveExpectedOnServer: false,
            featureActiveExpectedOnClient: false),
    };

    private readonly IReadOnlyDictionary<string, WorkshopModuleExpectation> modulesById =
        ExpectedModules.ToDictionary(module => module.ModuleId, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WorkshopModuleExpectation> Modules => ExpectedModules;

    public bool TryGet(string moduleId, out WorkshopModuleExpectation expectation) =>
        modulesById.TryGetValue(moduleId ?? string.Empty, out expectation);
}
