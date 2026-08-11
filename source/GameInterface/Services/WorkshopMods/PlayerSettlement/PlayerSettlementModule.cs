using GameInterface.AutoSync;
using GameInterface.Services.WorkshopMods.Core;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace GameInterface.Services.WorkshopMods.PlayerSettlement;

internal sealed class PlayerSettlementModule : IWorkshopModule
{
    private const string SupportedWin64Sha256 =
        "74f9ab2ebc82bdc755886c6ad0802500c2df89015d7c65cd5018c543dbf18119";

    private static readonly ModuleFingerprint PinnedFingerprint = new ModuleFingerprint(
        PlayerSettlementCompatibilityManifest.AssemblyName,
        PlayerSettlementCompatibilityManifest.AssemblyVersion,
        SupportedWin64Sha256);

    public string ModuleId => PlayerSettlementCompatibilityManifest.AssemblyName;
    public ulong WorkshopId => 3720376888UL;
    public ModuleFingerprint Fingerprint => PinnedFingerprint;
    public string PatchCategory => null;

    public string ResolveInstalledSha256()
    {
        var assembly = FindAssembly(PlayerSettlementCompatibilityManifest.AssemblyName);
        var fixesAssembly = FindAssembly(PlayerSettlementCompatibilityManifest.FixesAssemblyName);
        if (!PlayerSettlementCompatibilityManifest.TryValidate(assembly, fixesAssembly, out _, out _))
            return null;

        using var stream = File.OpenRead(assembly.Location);
        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(stream));
    }

    public void RegisterSync(AutoSyncRegistry registry)
    {
    }

    private static System.Reflection.Assembly FindAssembly(string name) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
            string.Equals(candidate.GetName().Name, name, StringComparison.Ordinal));

    private static string ToHex(byte[] bytes)
    {
        const string digits = "0123456789abcdef";
        var result = new char[bytes.Length * 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            result[index * 2] = digits[bytes[index] >> 4];
            result[(index * 2) + 1] = digits[bytes[index] & 15];
        }
        return new string(result);
    }
}
