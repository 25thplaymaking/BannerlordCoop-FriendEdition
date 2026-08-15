using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Missions.WorkshopMods.Combat;

internal enum CombatModFamily
{
    Rbm434,
    DismembermentPlus2088,
    UnblockableThrust1131
}

internal sealed class CombatModFingerprint
{
    internal CombatModFamily Family { get; }
    internal string AssemblyName { get; }
    internal Version AssemblyVersion { get; }
    internal string Sha256 { get; }

    internal CombatModFingerprint(
        CombatModFamily family,
        string assemblyName,
        string assemblyVersion,
        string sha256)
    {
        Family = family;
        AssemblyName = assemblyName;
        AssemblyVersion = new Version(assemblyVersion);
        Sha256 = sha256;
    }
}

/// <summary>
/// Binary allow-list for the creator-approved workshop builds that were actually audited.  RBM
/// stamps every assembly as 1.0.0.0, so its DLL hashes (not that uninformative version) are the
/// compatibility boundary.  An update therefore fails closed until its decompiled patch surface is
/// reviewed again.
/// </summary>
internal static class CombatModFingerprintCatalog
{
    internal static readonly IReadOnlyList<CombatModFingerprint> Entries =
        new[]
        {
            new CombatModFingerprint(
                CombatModFamily.Rbm434,
                "RBM",
                "1.0.0.0",
                "1DCE47879190C09AC85F097DA91ABE2B4915455B72031E75917C963BAB3F99C2"),
            new CombatModFingerprint(
                CombatModFamily.Rbm434,
                "RBMAI",
                "1.0.0.0",
                "3902634B1B1C41A0D9F9BC44348FCB458AD9610D8C26F13527290410771B0F56"),
            new CombatModFingerprint(
                CombatModFamily.Rbm434,
                "RBMCombat",
                "1.0.0.0",
                "4629E2E331AC551D40F998413F2FB5400D4D74E958CBAECC675A312CE615A7E1"),
            new CombatModFingerprint(
                CombatModFamily.Rbm434,
                "RBMConfig",
                "1.0.0.0",
                "F94D53CF556AFF452B3541C392E0898B48A29D758A5E1368A57071797271F9A5"),
            new CombatModFingerprint(
                CombatModFamily.Rbm434,
                "RBMTournament",
                "1.0.0.0",
                "2B0BA4783FD1D64218D45E7E4EAF9327024D707D27731F9403CFF74594CDDC41"),
            new CombatModFingerprint(
                CombatModFamily.DismembermentPlus2088,
                "DismembermentPlus",
                "2.0.8.8",
                "17ABFCC4EBA59C15CACA1BCE194C0791663ED692F21FFE22675DA49418A4E79B"),
            new CombatModFingerprint(
                CombatModFamily.UnblockableThrust1131,
                "UnblockableThrust",
                "1.1.3.1",
                "FF73B80A598BCE31E8D620FAE84E21C7F633F767F03E5F192E05169425DC83DF")
        };

    internal static CombatModFingerprint Find(string assemblyName)
    {
        return Entries.SingleOrDefault(entry => string.Equals(
            entry.AssemblyName,
            assemblyName,
            StringComparison.Ordinal));
    }

    internal static bool IsAccepted(
        string assemblyName,
        Version assemblyVersion,
        string sha256)
    {
        CombatModFingerprint expected = Find(assemblyName);
        return expected != null
            && expected.AssemblyVersion == assemblyVersion
            && string.Equals(expected.Sha256, sha256, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsFamilyPresent(
        CombatModFamily family,
        IEnumerable<Assembly> assemblies)
    {
        var loadedNames = new HashSet<string>(
            assemblies.Select(assembly => assembly.GetName().Name),
            StringComparer.Ordinal);
        return Entries.Where(entry => entry.Family == family)
            .All(entry => loadedNames.Contains(entry.AssemblyName));
    }
}
