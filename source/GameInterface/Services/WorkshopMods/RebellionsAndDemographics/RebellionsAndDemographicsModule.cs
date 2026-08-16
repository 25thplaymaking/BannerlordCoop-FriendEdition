using GameInterface.AutoSync;
using GameInterface.Services.WorkshopMods.Core;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace GameInterface.Services.WorkshopMods.RebellionsAndDemographics;

internal sealed class RebellionsAndDemographicsModule : IWorkshopModule
{
    internal const string ModuleIdValue = "RebellionsAndDemographics";
    // Workshop folder and TaleWorlds module token differ from the managed assembly identity.
    // v3.0.1 is emitted as ClassLibrary22; treating the token as the assembly name made the
    // adapter silently unavailable on every legitimate load.
    internal const string AssemblyName = "ClassLibrary22";
    internal const string AssemblyVersion = "1.0.0.0";
    internal const string SupportedSha256 = "115ca5f26eaa50f9ce6fa4ac88dc2b65b94be1eb4ff27a895ea29982983463a8";

    private static readonly ModuleFingerprint PinnedFingerprint = new(
        AssemblyName, AssemblyVersion, SupportedSha256);

    public string ModuleId => ModuleIdValue;
    public ulong WorkshopId => 3644127631UL;
    public ModuleFingerprint Fingerprint => PinnedFingerprint;
    public string PatchCategory => null;

    public string ResolveInstalledSha256()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
            string.Equals(candidate.GetName().Name, AssemblyName, StringComparison.Ordinal));
        if (assembly == null || string.IsNullOrEmpty(assembly.Location) || !File.Exists(assembly.Location)) return null;

        using var stream = File.OpenRead(assembly.Location);
        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(stream));
    }

    public void RegisterSync(AutoSyncRegistry registry)
    {
        // Original R&D behavior data is host-only. Native campaign object mutations travel through
        // Coop's existing authoritative object/action funnels; adapter state uses its typed snapshot.
    }

    private static string ToHex(byte[] bytes)
    {
        const string digits = "0123456789abcdef";
        var result = new char[bytes.Length * 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            result[index * 2] = digits[bytes[index] >> 4];
            result[index * 2 + 1] = digits[bytes[index] & 15];
        }
        return new string(result);
    }
}
