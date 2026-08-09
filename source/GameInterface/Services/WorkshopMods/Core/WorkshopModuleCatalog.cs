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
        bool featureActiveExpectedOnClient = false)
    {
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
    // Every bundled component runs on every peer: the group chose the full modded experience
    // over the staged per-module rollout. Cross-player consistency for actions no one has routed
    // yet is best-effort; routing work (doc/HANDOFF-workshop-routing.md §4) hardens mods in place
    // without another activation flip.
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
        new("RBM", "2859251492", "8508128689459287315", "v4.3.4", 100,
            WorkshopModuleRole.Mission, WorkshopCompatibilityProfile.DeterministicMission,
            featureActiveExpectedOnServer: true,
            featureActiveExpectedOnClient: true),
        new("ImprovedGarrisons", "2859265386", "5143458534246082850", "v4.2.0.7", 110,
            WorkshopModuleRole.Campaign, WorkshopCompatibilityProfile.ServerAuthoritativeCampaign,
            featureActiveExpectedOnServer: true,
            featureActiveExpectedOnClient: true),
        new("DismembermentPlus", "2875093027", "4587731243779119835", "v2.0.8.7", 120,
            WorkshopModuleRole.Presentation, WorkshopCompatibilityProfile.ClientPresentation,
            featureActiveExpectedOnServer: true,
            featureActiveExpectedOnClient: true),
        new("Fourberie", "2875710877", "4391404683672989722", "v1.4.7.5", 130,
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
        new("PlayerSettlement", "3720376888", "6398100776119441137", "v7.5.0", 160,
            WorkshopModuleRole.Campaign, WorkshopCompatibilityProfile.ServerAuthoritativeCampaign,
            featureActiveExpectedOnServer: true,
            featureActiveExpectedOnClient: true),
    };

    private readonly IReadOnlyDictionary<string, WorkshopModuleExpectation> modulesById =
        ExpectedModules.ToDictionary(module => module.ModuleId, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WorkshopModuleExpectation> Modules => ExpectedModules;

    public bool TryGet(string moduleId, out WorkshopModuleExpectation expectation) =>
        modulesById.TryGetValue(moduleId ?? string.Empty, out expectation);
}
