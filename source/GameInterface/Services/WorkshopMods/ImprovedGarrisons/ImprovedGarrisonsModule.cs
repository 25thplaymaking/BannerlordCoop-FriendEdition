using GameInterface.AutoSync;
using GameInterface.Services.WorkshopMods.Core;
using System;
using System.Linq;

namespace GameInterface.Services.WorkshopMods.ImprovedGarrisons;

internal sealed class ImprovedGarrisonsModule : IWorkshopModule
{
    private static readonly ModuleFingerprint PinnedFingerprint = new ModuleFingerprint(
        ImprovedGarrisonsCompatibilityManifest.AssemblyName,
        "1.0.0.0",
        ImprovedGarrisonsCompatibilityManifest.SupportedSha256);

    public string ModuleId => ImprovedGarrisonsCompatibilityManifest.AssemblyName;
    public ulong WorkshopId => 2859265386UL;
    public ModuleFingerprint Fingerprint => PinnedFingerprint;
    public string PatchCategory => null;

    public string ResolveInstalledSha256()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
            string.Equals(
                candidate.GetName().Name,
                ImprovedGarrisonsCompatibilityManifest.AssemblyName,
                StringComparison.Ordinal));
        return ImprovedGarrisonsCompatibilityManifest.TryValidate(assembly, out _, out _)
            ? ImprovedGarrisonsCompatibilityManifest.SupportedSha256
            : null;
    }

    public void RegisterSync(AutoSyncRegistry registry)
    {
    }
}
