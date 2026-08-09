using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GameInterface.Services.WorkshopMods.Core;

[ProtoContract]
public enum WorkshopPeerRole
{
    [ProtoEnum]
    Unknown = 0,
    [ProtoEnum]
    Server = 1,
    [ProtoEnum]
    Client = 2,
}

[ProtoContract(SkipConstructor = true)]
public sealed class WorkshopCompatibilityManifestEntry
{
    [ProtoMember(1)]
    public string ModuleId { get; private set; }
    [ProtoMember(2)]
    public string WorkshopId { get; private set; }
    [ProtoMember(3)]
    public string Version { get; private set; }
    [ProtoMember(4)]
    public WorkshopModuleRole Role { get; private set; }
    [ProtoMember(5)]
    public WorkshopCompatibilityProfile Profile { get; private set; }
    [ProtoMember(6)]
    public string ContentSha256 { get; private set; }
    [ProtoMember(7)]
    public string ConfigurationSha256 { get; private set; }
    [ProtoMember(8)]
    public bool ManagedDistributionComponent { get; private set; }
    [ProtoMember(9)]
    public bool ActivationOrderValid { get; private set; }
    [ProtoMember(10)]
    public int LoadOrder { get; private set; }
    [ProtoMember(11)]
    public bool Active { get; private set; }

    public WorkshopCompatibilityManifestEntry(
        string moduleId,
        string workshopId,
        string version,
        WorkshopModuleRole role,
        WorkshopCompatibilityProfile profile,
        string contentSha256,
        string configurationSha256,
        bool managedDistributionComponent,
        bool activationOrderValid = true,
        int loadOrder = 0,
        bool active = false)
    {
        ModuleId = moduleId;
        WorkshopId = workshopId;
        Version = version;
        Role = role;
        Profile = profile;
        ContentSha256 = NormalizeHash(contentSha256);
        ConfigurationSha256 = NormalizeHash(configurationSha256);
        ManagedDistributionComponent = managedDistributionComponent;
        ActivationOrderValid = activationOrderValid;
        LoadOrder = loadOrder;
        Active = active;
    }

    internal static string NormalizeHash(string value) => value?.Trim().ToLowerInvariant();
}

/// <summary>
/// Small, deterministic wire representation of the Friend Edition component set. It carries
/// only identities and digests, never paths or arbitrary configuration contents.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public sealed class WorkshopCompatibilityManifest
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEntries = 16;
    public const int MaximumDistributionIdLength = 64;
    public const int MaximumModuleIdLength = 96;
    public const int MaximumWorkshopIdLength = 24;
    public const int MaximumVersionLength = 32;
    public const int MaximumLoadOrder = 4096;
    public const int Sha256HexLength = 64;
    public const string FriendEditionDistributionId = "bannerlord-coop-friend-edition";

    [ProtoMember(1)]
    public int SchemaVersion { get; private set; }
    [ProtoMember(2)]
    public string DistributionId { get; private set; }
    [ProtoMember(3)]
    public WorkshopPeerRole PeerRole { get; private set; }
    [ProtoMember(4)]
    public WorkshopCompatibilityManifestEntry[] Entries { get; private set; }
    [ProtoMember(5)]
    public string ManifestSha256 { get; private set; }

    public WorkshopCompatibilityManifest(
        WorkshopPeerRole peerRole,
        IEnumerable<WorkshopCompatibilityManifestEntry> entries,
        string distributionId = FriendEditionDistributionId,
        int schemaVersion = CurrentSchemaVersion)
    {
        SchemaVersion = schemaVersion;
        DistributionId = distributionId;
        PeerRole = peerRole;
        Entries = (entries ?? Enumerable.Empty<WorkshopCompatibilityManifestEntry>())
            .OrderBy(entry => entry?.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ManifestSha256 = ComputeDigest(SchemaVersion, DistributionId, PeerRole, Entries);
    }

    public bool TryValidateWireShape(out string error)
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            error = $"Unsupported Workshop manifest schema '{SchemaVersion}'.";
            return false;
        }

        if (!IsBoundedText(DistributionId, MaximumDistributionIdLength) ||
            !string.Equals(DistributionId, FriendEditionDistributionId, StringComparison.Ordinal))
        {
            error = "Invalid Friend Edition distribution identifier.";
            return false;
        }

        if (PeerRole != WorkshopPeerRole.Server && PeerRole != WorkshopPeerRole.Client)
        {
            error = "Invalid Workshop manifest peer role.";
            return false;
        }

        if (Entries == null || Entries.Length > MaximumEntries)
        {
            error = $"Workshop manifest contains more than {MaximumEntries} entries.";
            return false;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            if (entry == null)
            {
                error = "Workshop manifest contains a null entry.";
                return false;
            }

            if (!IsBoundedText(entry.ModuleId, MaximumModuleIdLength) ||
                !IsBoundedText(entry.WorkshopId, MaximumWorkshopIdLength) ||
                !IsBoundedText(entry.Version, MaximumVersionLength))
            {
                error = "Workshop manifest contains an invalid or oversized module identity.";
                return false;
            }

            if (!ids.Add(entry.ModuleId))
            {
                error = $"Workshop manifest contains duplicate module '{entry.ModuleId}'.";
                return false;
            }

            if (!IsSha256(entry.ContentSha256) || !IsSha256(entry.ConfigurationSha256))
            {
                error = $"Workshop manifest contains an invalid SHA-256 digest for '{entry.ModuleId}'.";
                return false;
            }

            if (!Enum.IsDefined(typeof(WorkshopModuleRole), entry.Role) ||
                !Enum.IsDefined(typeof(WorkshopCompatibilityProfile), entry.Profile))
            {
                error = $"Workshop manifest contains an invalid role/profile for '{entry.ModuleId}'.";
                return false;
            }

            if (entry.LoadOrder < 0 || entry.LoadOrder > MaximumLoadOrder)
            {
                error = $"Workshop manifest contains an invalid load order for '{entry.ModuleId}'.";
                return false;
            }
        }

        if (!IsSha256(ManifestSha256))
        {
            error = "Workshop manifest aggregate digest is invalid.";
            return false;
        }

        string expectedDigest = ComputeDigest(SchemaVersion, DistributionId, PeerRole, Entries);
        if (!FixedTimeEquals(expectedDigest, ManifestSha256))
        {
            error = "Workshop manifest aggregate digest does not match its entries.";
            return false;
        }

        error = null;
        return true;
    }

    public static string ComputeDigest(
        int schemaVersion,
        string distributionId,
        WorkshopPeerRole peerRole,
        IEnumerable<WorkshopCompatibilityManifestEntry> entries)
    {
        var canonical = new StringBuilder();
        Append(canonical, schemaVersion.ToString(CultureInfo.InvariantCulture));
        Append(canonical, distributionId ?? string.Empty);
        Append(canonical, ((int)peerRole).ToString(CultureInfo.InvariantCulture));

        foreach (var entry in (entries ?? Enumerable.Empty<WorkshopCompatibilityManifestEntry>())
                     .Where(value => value != null)
                     .OrderBy(value => value.ModuleId, StringComparer.OrdinalIgnoreCase))
        {
            Append(canonical, entry.ModuleId?.ToLowerInvariant() ?? string.Empty);
            Append(canonical, entry.WorkshopId ?? string.Empty);
            Append(canonical, entry.Version ?? string.Empty);
            Append(canonical, ((int)entry.Role).ToString(CultureInfo.InvariantCulture));
            Append(canonical, ((int)entry.Profile).ToString(CultureInfo.InvariantCulture));
            Append(canonical, WorkshopCompatibilityManifestEntry.NormalizeHash(entry.ContentSha256) ?? string.Empty);
            Append(canonical, WorkshopCompatibilityManifestEntry.NormalizeHash(entry.ConfigurationSha256) ?? string.Empty);
            Append(canonical, entry.ManagedDistributionComponent ? "1" : "0");
            Append(canonical, entry.ActivationOrderValid ? "1" : "0");
            Append(canonical, entry.LoadOrder.ToString(CultureInfo.InvariantCulture));
            Append(canonical, entry.Active ? "1" : "0");
        }

        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    internal static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes)
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append(';');
    }

    private static bool IsBoundedText(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;

    private static bool IsSha256(string value)
    {
        if (value == null || value.Length != Sha256HexLength) return false;
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            bool hexadecimal = current >= '0' && current <= '9' ||
                               current >= 'a' && current <= 'f' ||
                               current >= 'A' && current <= 'F';
            if (!hexadecimal) return false;
        }
        return true;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        int difference = 0;
        for (int index = 0; index < left.Length; index++)
            difference |= left[index] ^ right[index];
        return difference == 0;
    }
}
