using System;
using System.Collections.Generic;
using Common;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.WorkshopMods.RebellionsAndDemographics;

/// <summary>Static Harmony bridge; no original R&D state is ever exposed to a client process.</summary>
internal static class RebellionsAndDemographicsRuntime
{
    internal static RebellionsAndDemographicsCompatibilityHandler Current { get; set; }

    internal static bool GuardOnGameStart(object __0, object __1)
    {
        var runtime = Current ?? throw new InvalidOperationException(
            "R&D lifecycle was entered before the Coop authority adapter was installed.");
        runtime.StartAuthoritativeCampaign(__1);
        return false;
    }

    internal static bool GuardOnMissionBehaviorInitialize(object __0)
    {
        // Every original mission behavior is coupled to client-local input, UI, or cinematic state.
        // The campaign adapter never starts those mission domains on either co-op role.
        return false;
    }

    internal static void PurgeUpstreamAfterSubModuleLoad() => Current?.PurgeUpstreamAfterSubModuleLoad();

    // Upstream parameter names are not part of the binary contract. Harmony's positional aliases
    // keep this private-method prefix valid across the pinned assembly's symbol stripping.
    internal static bool InterceptTryStartRebellion(Kingdom __0, List<Clan> __1, bool __2) =>
        Current?.InterceptTryStartRebellion(__0, __1, __2) ?? false;

    internal static bool GuardPopulationAppTick() => !ModInformation.IsServer;

    internal static bool IssueDefeatChoice(Kingdom __0) => Current == null
        ? !ModInformation.IsServer : !Current.IssueDefeatChoice(__0);
    internal static bool IssueUltimatumChoice(Kingdom __0, List<Clan> __1) => Current == null
        ? !ModInformation.IsServer : !Current.IssueUltimatumChoice(__0, __1);
}
