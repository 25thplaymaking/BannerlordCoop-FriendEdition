using GameInterface.Services.WorkshopMods.Core;
using System.Collections.Generic;

namespace GameInterface.Services.WorkshopMods.RebellionsAndDemographics;

/// <summary>
/// Makes the package hold visible to the shared capability handshake. The retained Workshop
/// binary is intentionally not loaded: it was authored for Bannerlord 1.3.13 and registers a
/// broad set of mutable campaign behaviours and player input callbacks without co-op authority
/// routes. Do not turn this into a permissive capability until its source migration owns those
/// mutations, snapshots, save/restart recovery, and player commands.
/// </summary>
internal sealed class RebellionsAndDemographicsCapabilitySource : IWorkshopCapabilitySource
{
    internal const string ModuleId = "RebellionsAndDemographics";
    internal const string Operation = "CampaignAuthority";
    internal const string HoldReason = "source-required-for-authority-and-1.4.8-migration";

    public IEnumerable<WorkshopCapability> CaptureCapabilities()
    {
        yield return new WorkshopCapability(ModuleId, Operation, false, HoldReason);
    }
}
