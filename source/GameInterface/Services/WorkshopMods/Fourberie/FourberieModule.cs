using GameInterface.AutoSync;
using GameInterface.Services.WorkshopMods.Core;
using System;
using System.Linq;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal sealed class FourberieModule : IWorkshopModule
{
    private static readonly ModuleFingerprint PinnedFingerprint = new ModuleFingerprint(
        FourberieCompatibilityManifest.AssemblyName,
        FourberieCompatibilityManifest.SupportedAssemblyVersion,
        FourberieCompatibilityManifest.SupportedSha256);

    public string ModuleId => FourberieCompatibilityManifest.ModuleId;
    public ulong WorkshopId => 2875710877UL;
    public ModuleFingerprint Fingerprint => PinnedFingerprint;
    public string PatchCategory => null;

    public string ResolveInstalledSha256()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
            string.Equals(
                candidate.GetName().Name,
                FourberieCompatibilityManifest.AssemblyName,
                StringComparison.Ordinal));
        return FourberieCompatibilityManifest.TryValidate(assembly, out _, out _)
            ? FourberieCompatibilityManifest.SupportedSha256
            : null;
    }

    public void RegisterSync(AutoSyncRegistry registry)
    {
    }
}
