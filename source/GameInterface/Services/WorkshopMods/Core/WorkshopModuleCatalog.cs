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
    // The live Friend Edition loadout runs the exact same ten Workshop modules on both roles.
    // RBM is intentionally absent: its combat-parameter initialization caused a native access
    // violation during co-op campaign startup and the project owner retired it from the loadout.
    // Retaining an RBM catalog entry would make the handshake and pack disagree with the launcher.
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
        new("ImprovedGarrisons", "2859265386", "5143458534246082850", "v4.2.0.7", 110,
            WorkshopModuleRole.Campaign, WorkshopCompatibilityProfile.ServerAuthoritativeCampaign,
            featureActiveExpectedOnServer: true,
            featureActiveExpectedOnClient: true),
        new("DismembermentPlus", "2875093027", "751945004455697202", "v2.0.8.8", 120,
            WorkshopModuleRole.Presentation, WorkshopCompatibilityProfile.ClientPresentation,
            featureActiveExpectedOnServer: true,
            featureActiveExpectedOnClient: true),
        new("Fourberie", "2875710877", "1598945672157391038", "v1.4.7.6", 130,
            WorkshopModuleRole.Campaign, WorkshopCompatibilityProfile.ServerAuthoritativeCampaign,
            featureActiveExpectedOnServer: true,
            featureActiveExpectedOnClient: true),
        new("Bannerlord.Diplomacy", "2881380744", "3938505074920035905", "v1.4.7", 140,
            WorkshopModuleRole.Campaign, WorkshopCompatibilityProfile.ServerAuthoritativeCampaign,
            featureActiveExpectedOnServer: true,
            featureActiveExpectedOnClient: true),
        new("UnblockableThrust", "3614435151", "3108412629025003964", "v1.1.3.1", 150,
            WorkshopModuleRole.Mission, WorkshopCompatibilityProfile.DeterministicMission,
            featureActiveExpectedOnServer: true,
            featureActiveExpectedOnClient: true),
        // PlayerSettlement must load BEFORE Coop: its co-op adapter purges and re-guards the
        // module's load-time Harmony patches, so those patches have to exist by the time Coop's
        // container is built. Activating it after Coop deadlocks or crashes client startup.
        new("PlayerSettlement", "3720376888", "6398100776119441137", "v7.5.0", 160,
            WorkshopModuleRole.Campaign, WorkshopCompatibilityProfile.ServerAuthoritativeCampaign,
            featureActiveExpectedOnServer: true,
            featureActiveExpectedOnClient: true,
            loadsBeforeCoop: true),
    };

    private readonly IReadOnlyDictionary<string, WorkshopModuleExpectation> modulesById =
        ExpectedModules.ToDictionary(module => module.ModuleId, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WorkshopModuleExpectation> Modules => ExpectedModules;

    public bool TryGet(string moduleId, out WorkshopModuleExpectation expectation) =>
        modulesById.TryGetValue(moduleId ?? string.Empty, out expectation);
}
