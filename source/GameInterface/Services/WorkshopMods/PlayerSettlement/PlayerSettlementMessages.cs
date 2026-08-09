using Common.Messaging;
using LiteNetLib;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace GameInterface.Services.WorkshopMods.PlayerSettlement;

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestPlayerSettlementState : ICommand
{
}

[ProtoContract]
internal enum PlayerSettlementFeatureStatus
{
    [ProtoEnum] GuardedFeatureBlocked = 1,
}

[ProtoContract]
internal enum PlayerSettlementObjectKind
{
    [ProtoEnum] Town = 1,
    [ProtoEnum] Castle = 2,
    [ProtoEnum] ExtraVillage = 3,
    [ProtoEnum] Overwrite = 4,
    [ProtoEnum] BoundVillage = 5,
}

/// <summary>
/// Canonical description of generated settlement XML. It is intentionally informational in this
/// adapter version: clients validate it, but non-empty state is rejected rather than loaded after
/// MBObjectManager's registration phase. That is the fail-closed boundary for existing saves.
/// </summary>
[ProtoContract(SkipConstructor = true)]
internal sealed class PlayerSettlementStateEntry
{
    [ProtoMember(1)] public PlayerSettlementObjectKind Kind { get; set; }
    [ProtoMember(2)] public int Ordinal { get; set; }
    [ProtoMember(3)] public string StringId { get; set; }
    [ProtoMember(4)] public string ParentStringId { get; set; }
    [ProtoMember(5)] public string PrefabId { get; set; }
    [ProtoMember(6)] public string ModuleVersion { get; set; }
    [ProtoMember(7)] public string DisplayName { get; set; }
    [ProtoMember(8)] public int BuildTimeBits { get; set; }
    [ProtoMember(9)] public string Xml { get; set; }
    [ProtoMember(10)] public string XmlSha256 { get; set; }
    [ProtoMember(11)] public string ComponentFingerprint { get; set; }

    public PlayerSettlementStateEntry()
    {
    }

    public PlayerSettlementStateEntry(
        PlayerSettlementObjectKind kind,
        int ordinal,
        string stringId,
        string parentStringId,
        string prefabId,
        string moduleVersion,
        string displayName,
        float buildTime,
        string xml,
        string xmlSha256,
        string componentFingerprint)
    {
        Kind = kind;
        Ordinal = ordinal;
        StringId = stringId;
        ParentStringId = parentStringId;
        PrefabId = prefabId;
        ModuleVersion = moduleVersion;
        DisplayName = displayName;
        BuildTimeBits = BitConverter.ToInt32(BitConverter.GetBytes(buildTime), 0);
        Xml = xml;
        XmlSha256 = xmlSha256;
        ComponentFingerprint = componentFingerprint;
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkPlayerSettlementState : ICommand
{
    [ProtoMember(1)] public string AdapterVersion { get; set; }
    [ProtoMember(2)] public long Revision { get; set; }
    [ProtoMember(3)] public PlayerSettlementFeatureStatus FeatureStatus { get; set; }
    [ProtoMember(4)] public string StateFingerprint { get; set; }
    [ProtoMember(5)] public PlayerSettlementStateEntry[] Entries { get; set; }

    public NetworkPlayerSettlementState()
    {
    }

    public NetworkPlayerSettlementState(
        string adapterVersion,
        long revision,
        PlayerSettlementFeatureStatus featureStatus,
        string stateFingerprint,
        PlayerSettlementStateEntry[] entries)
    {
        AdapterVersion = adapterVersion;
        Revision = revision;
        FeatureStatus = featureStatus;
        StateFingerprint = stateFingerprint;
        Entries = entries;
    }
}

internal static class PlayerSettlementSnapshotOriginGuard
{
    internal static bool IsTrustedServerTransport(object source, bool localIsClient) =>
        localIsClient && source is NetPeer;
}

internal static class PlayerSettlementSnapshotFailurePolicy
{
    internal static bool MustDisconnect(bool trustedServerTransport, bool snapshotAccepted) =>
        trustedServerTransport && !snapshotAccepted;
}

internal static class PlayerSettlementStateCodec
{
    internal const int MaximumEntries = 256;
    internal const int MaximumStringIdLength = 192;
    internal const int MaximumPrefabIdLength = 192;
    internal const int MaximumVersionLength = 32;
    internal const int MaximumDisplayNameLength = 256;
    internal const int MaximumXmlLength = 524288;
    internal const int MaximumTotalXmlLength = 4194304;

    internal static IEnumerable<PlayerSettlementStateEntry> Sort(
        IEnumerable<PlayerSettlementStateEntry> entries) =>
        (entries ?? Array.Empty<PlayerSettlementStateEntry>())
        .Where(entry => entry != null)
        .OrderBy(entry => (int)entry.Kind)
        .ThenBy(entry => entry.ParentStringId ?? string.Empty, StringComparer.Ordinal)
        .ThenBy(entry => entry.Ordinal)
        .ThenBy(entry => entry.StringId ?? string.Empty, StringComparer.Ordinal);

    internal static string ComputeHash(IEnumerable<PlayerSettlementStateEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in Sort(entries))
        {
            Append(builder, ((int)entry.Kind).ToString(CultureInfo.InvariantCulture));
            Append(builder, entry.Ordinal.ToString(CultureInfo.InvariantCulture));
            Append(builder, entry.StringId);
            Append(builder, entry.ParentStringId);
            Append(builder, entry.PrefabId);
            Append(builder, entry.ModuleVersion);
            Append(builder, entry.DisplayName);
            Append(builder, entry.BuildTimeBits.ToString(CultureInfo.InvariantCulture));
            Append(builder, entry.XmlSha256);
            Append(builder, entry.ComponentFingerprint);
        }

        return Sha256(builder.ToString());
    }

    internal static bool TryValidate(NetworkPlayerSettlementState state, out string failure)
    {
        if (state == null)
        {
            failure = "snapshot is null";
            return false;
        }
        if (!string.Equals(
                state.AdapterVersion,
                PlayerSettlementCompatibilityManifest.AdapterVersion,
                StringComparison.Ordinal))
        {
            failure = $"unsupported adapter version {state.AdapterVersion ?? "missing"}";
            return false;
        }
        if (state.Revision < 0 ||
            state.FeatureStatus != PlayerSettlementFeatureStatus.GuardedFeatureBlocked ||
            !IsSha256(state.StateFingerprint))
        {
            failure = "invalid revision, feature status, or state fingerprint";
            return false;
        }

        var entries = state.Entries ?? Array.Empty<PlayerSettlementStateEntry>();
        if (entries.Length > MaximumEntries)
        {
            failure = $"snapshot contains more than {MaximumEntries} objects";
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var totalXml = 0;
        foreach (var entry in entries)
        {
            if (entry == null ||
                !Enum.IsDefined(typeof(PlayerSettlementObjectKind), entry.Kind) ||
                entry.Ordinal < 0 ||
                !Bounded(entry.StringId, MaximumStringIdLength, allowEmpty: false) ||
                !Bounded(entry.ParentStringId, MaximumStringIdLength, allowEmpty: true) ||
                !Bounded(entry.PrefabId, MaximumPrefabIdLength, allowEmpty: true) ||
                !Bounded(entry.ModuleVersion, MaximumVersionLength, allowEmpty: true) ||
                !Bounded(entry.DisplayName, MaximumDisplayNameLength, allowEmpty: true) ||
                !Bounded(entry.Xml, MaximumXmlLength, allowEmpty: false) ||
                !IsSha256(entry.XmlSha256) ||
                !IsSha256(entry.ComponentFingerprint))
            {
                failure = "snapshot contains invalid or oversized object metadata";
                return false;
            }

            if (!ids.Add(entry.StringId))
            {
                failure = $"duplicate stable object ID {entry.StringId}";
                return false;
            }

            if (entry.Kind == PlayerSettlementObjectKind.BoundVillage)
            {
                if (string.IsNullOrEmpty(entry.ParentStringId))
                {
                    failure = $"bound village {entry.StringId} has no parent ID";
                    return false;
                }
            }
            else if (!string.IsNullOrEmpty(entry.ParentStringId))
            {
                failure = $"top-level object {entry.StringId} unexpectedly has a parent ID";
                return false;
            }

            totalXml += entry.Xml.Length;
            if (totalXml > MaximumTotalXmlLength)
            {
                failure = $"snapshot XML exceeds {MaximumTotalXmlLength} characters";
                return false;
            }

            if (!TryFingerprintXml(entry.Xml, out var xmlHash, out var componentHash, out failure) ||
                !FixedTimeEquals(xmlHash, entry.XmlSha256) ||
                !FixedTimeEquals(componentHash, entry.ComponentFingerprint) ||
                !XmlDeclaresStableId(entry.Xml, entry.StringId))
            {
                failure ??= $"XML fingerprint or stable object ID mismatch for {entry.StringId}";
                return false;
            }
        }

        var kindById = entries.ToDictionary(entry => entry.StringId, entry => entry.Kind, StringComparer.Ordinal);
        foreach (var village in entries.Where(entry => entry.Kind == PlayerSettlementObjectKind.BoundVillage))
        {
            if (!kindById.TryGetValue(village.ParentStringId, out var parentKind) ||
                (parentKind != PlayerSettlementObjectKind.Town &&
                 parentKind != PlayerSettlementObjectKind.Castle))
            {
                failure = $"bound village {village.StringId} references invalid parent {village.ParentStringId}";
                return false;
            }
        }

        var computed = ComputeHash(entries);
        if (!FixedTimeEquals(computed, state.StateFingerprint))
        {
            failure = $"state fingerprint mismatch ({computed} != {state.StateFingerprint})";
            return false;
        }

        failure = null;
        return true;
    }

    internal static bool TryFingerprintXml(
        string xml,
        out string xmlSha256,
        out string componentFingerprint,
        out string failure)
    {
        xmlSha256 = null;
        componentFingerprint = null;
        failure = null;
        if (string.IsNullOrEmpty(xml) || xml.Length > MaximumXmlLength ||
            xml.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            failure = "XML is empty, oversized, or contains a document type";
            return false;
        }

        try
        {
            var document = new XmlDocument { XmlResolver = null, PreserveWhitespace = false };
            document.LoadXml(xml);
            if (document.DocumentElement == null)
            {
                failure = "XML has no document element";
                return false;
            }

            var components = new StringBuilder();
            AppendElement(components, document.DocumentElement, "/" + document.DocumentElement.Name + "[0]");
            xmlSha256 = Sha256(xml);
            componentFingerprint = Sha256(components.ToString());
            return true;
        }
        catch (XmlException exception)
        {
            failure = $"invalid generated XML: {exception.Message}";
            return false;
        }
    }

    internal static bool IsSha256(string value)
    {
        if (value == null || value.Length != 64) return false;
        foreach (var character in value)
        {
            if (!(character >= '0' && character <= '9') &&
                !(character >= 'a' && character <= 'f') &&
                !(character >= 'A' && character <= 'F'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool XmlDeclaresStableId(string xml, string expectedId)
    {
        try
        {
            var document = new XmlDocument { XmlResolver = null, PreserveWhitespace = false };
            document.LoadXml(xml);
            foreach (XmlElement settlement in document.GetElementsByTagName("Settlement"))
            {
                if (string.Equals(settlement.GetAttribute("id"), expectedId, StringComparison.Ordinal))
                    return true;
            }
        }
        catch (XmlException)
        {
        }
        return false;
    }

    private static void AppendElement(StringBuilder builder, XmlElement element, string path)
    {
        Append(builder, path);
        Append(builder, element.Name);
        foreach (XmlAttribute attribute in element.Attributes.Cast<XmlAttribute>()
                     .OrderBy(attribute => attribute.Name, StringComparer.Ordinal))
        {
            Append(builder, attribute.Name);
            Append(builder, attribute.Value);
        }

        var ordinalByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var child in element.ChildNodes.OfType<XmlElement>())
        {
            ordinalByName.TryGetValue(child.Name, out var ordinal);
            ordinalByName[child.Name] = ordinal + 1;
            AppendElement(builder, child, path + "/" + child.Name + "[" + ordinal + "]");
        }
    }

    private static void Append(StringBuilder builder, string value)
    {
        value ??= string.Empty;
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':').Append(value).Append(';');
    }

    private static bool Bounded(string value, int limit, bool allowEmpty) =>
        value != null && value.Length <= limit && (allowEmpty || value.Length > 0);

    private static string Sha256(string value)
    {
        using (var sha = SHA256.Create())
            return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        var difference = 0;
        for (var index = 0; index < left.Length; index++)
            difference |= left[index] ^ right[index];
        return difference == 0;
    }

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

internal enum PlayerSettlementRevisionDecision
{
    Apply,
    AlreadyApplied,
    Stale,
    ConflictingRevision,
    Invalid,
}

internal sealed class PlayerSettlementRevisionGate
{
    internal long Revision { get; private set; } = -1;
    internal string Fingerprint { get; private set; }

    internal PlayerSettlementRevisionDecision Evaluate(long revision, string fingerprint)
    {
        if (revision < 0 || !PlayerSettlementStateCodec.IsSha256(fingerprint))
            return PlayerSettlementRevisionDecision.Invalid;
        if (revision < Revision) return PlayerSettlementRevisionDecision.Stale;
        if (revision == Revision)
        {
            return string.Equals(Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
                ? PlayerSettlementRevisionDecision.AlreadyApplied
                : PlayerSettlementRevisionDecision.ConflictingRevision;
        }
        return PlayerSettlementRevisionDecision.Apply;
    }

    internal bool Commit(long revision, string fingerprint)
    {
        var decision = Evaluate(revision, fingerprint);
        if (decision == PlayerSettlementRevisionDecision.AlreadyApplied) return true;
        if (decision != PlayerSettlementRevisionDecision.Apply) return false;
        Revision = revision;
        Fingerprint = fingerprint;
        return true;
    }

    internal void Reset()
    {
        Revision = -1;
        Fingerprint = null;
    }
}
