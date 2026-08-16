using Common;
using Common.Messaging;
using GameInterface.Services.WorkshopMods.Core;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using System;
using System.Collections.Generic;

namespace GameInterface.Services.WorkshopMods.RebellionsAndDemographics;

/// <summary>
/// Readiness gate for the active R&D compatibility boundary. The package is active on both roles,
/// while only the host constructs audited automatic campaign behaviors. Client readiness requires
/// the canonical host receipt rather than trusting local behavior state.
/// </summary>
internal sealed class RebellionsAndDemographicsCapabilitySource : IWorkshopCapabilitySource
{
    internal const string ModuleId = "RebellionsAndDemographics";
    internal const string Operation = "CampaignAuthority";
    private readonly IModConfig modConfig;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRequestRouter authorityRequestRouter;
    private readonly RebellionsAndDemographicsCompatibilityHandler handler;

    public RebellionsAndDemographicsCapabilitySource(
        IModConfig modConfig,
        IModConfigAuthority configAuthority = null,
        IAuthorityRequestRouter authorityRequestRouter = null,
        RebellionsAndDemographicsCompatibilityHandler handler = null)
    {
        this.modConfig = modConfig;
        this.configAuthority = configAuthority;
        this.authorityRequestRouter = authorityRequestRouter;
        this.handler = handler;
    }

    public IEnumerable<WorkshopCapability> CaptureCapabilities()
    {
        var options = modConfig?.Data == null ? ModConfigProvider.ModOptions :
            new ModOptions(modConfig.Data.ModOptions ?? new ModOptionsData());
        bool routeReady = authorityRequestRouter?.IsRegistered(
            RebellionsAndDemographicsCompatibilityHandler.SnapshotRouteId,
            AuthorityRouteKind.BootstrapQuery) == true &&
            authorityRequestRouter.IsRegistered(RebellionsAndDemographicsCompatibilityHandler.InterventionRouteId,
                AuthorityRouteKind.Command) &&
            authorityRequestRouter.IsRegistered(RebellionsAndDemographicsCompatibilityHandler.ChoiceRouteId,
                AuthorityRouteKind.Command);
        bool snapshotReady = configAuthority != null && configAuthority.TryGetCurrent(out var config) &&
            (ModInformation.IsServer
                ? handler?.IsCampaignReady == true
                : handler?.SnapshotReadiness == WorkshopSnapshotReadiness.Ready &&
                  string.Equals(handler.SnapshotSessionId, config.SessionId, StringComparison.Ordinal));
        bool enabled = options.IsWorkshopModuleEnabled(ModuleId) && handler?.IsInstalledAndIsolated == true &&
            routeReady && snapshotReady;
        yield return new WorkshopCapability(ModuleId, Operation, enabled,
            enabled ? string.Empty : "authority-command-route-unavailable");
    }
}
