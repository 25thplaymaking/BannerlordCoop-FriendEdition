using System;
using System.Linq;

namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// The exact bytes a module is pinned to. Identity is by content, not by version string: a Workshop
/// update that keeps its version but changes its assembly must not silently satisfy the pin.
/// </summary>
public sealed class ModuleFingerprint
{
    /// <summary>Simple name of the pinned assembly, as <c>AssemblyName.Name</c> reports it.</summary>
    public string AssemblyName { get; }

    /// <summary>Four-part assembly version, for diagnostics. It is NOT the pin — the hash is.</summary>
    public string AssemblyVersion { get; }

    /// <summary>Lowercase-or-uppercase hex SHA-256 of the pinned DLL. Compared case-insensitively.</summary>
    public string Sha256 { get; }

    public ModuleFingerprint(string assemblyName, string assemblyVersion, string sha256)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            throw new ArgumentException("Assembly name is required", nameof(assemblyName));
        if (string.IsNullOrWhiteSpace(assemblyVersion))
            throw new ArgumentException("Assembly version is required", nameof(assemblyVersion));
        if (sha256 == null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("SHA-256 must be 64 hex characters", nameof(sha256));

        AssemblyName = assemblyName;
        AssemblyVersion = assemblyVersion;
        Sha256 = sha256;
    }

    /// <summary>
    /// Whether a measured hash is the pinned one. Hex case is not part of the identity, so a
    /// provider that emits uppercase digests still matches a lowercase pin.
    /// </summary>
    public bool Matches(string measuredSha256) =>
        measuredSha256 != null &&
        string.Equals(measuredSha256, Sha256, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{AssemblyName} {AssemblyVersion} ({Sha256})";
}
