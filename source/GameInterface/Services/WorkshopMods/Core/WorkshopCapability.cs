using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GameInterface.Services.WorkshopMods.Core;

[ProtoContract(SkipConstructor = true)]
public sealed class WorkshopCapability
{
    [ProtoMember(1)] public string ModuleId { get; }
    [ProtoMember(2)] public string Operation { get; }
    [ProtoMember(3)] public bool Enabled { get; }
    [ProtoMember(4)] public string Reason { get; }

    public WorkshopCapability(string moduleId, string operation, bool enabled, string reason)
    {
        ModuleId = moduleId;
        Operation = operation;
        Enabled = enabled;
        Reason = reason;
    }
}

[ProtoContract(SkipConstructor = true)]
public sealed class WorkshopCapabilitySnapshot
{
    public const int CurrentProtocolVersion = 1;
    public const int MaximumCapabilities = 512;

    [ProtoMember(1)] public int ProtocolVersion { get; }
    [ProtoMember(2)] public string SessionId { get; }
    [ProtoMember(3)] public long Revision { get; }
    [ProtoMember(4)] public WorkshopCapability[] Capabilities { get; }
    [ProtoMember(5)] public string Sha256 { get; }

    public WorkshopCapabilitySnapshot(
        string sessionId,
        long revision,
        IEnumerable<WorkshopCapability> capabilities)
        : this(
            CurrentProtocolVersion,
            sessionId,
            revision,
            WorkshopCapabilityCodec.Canonicalize(capabilities),
            null)
    {
        Sha256 = WorkshopCapabilityCodec.ComputeSha256(
            ProtocolVersion,
            SessionId,
            Revision,
            Capabilities);
    }

    public WorkshopCapabilitySnapshot(
        int protocolVersion,
        string sessionId,
        long revision,
        WorkshopCapability[] capabilities,
        string sha256)
    {
        ProtocolVersion = protocolVersion;
        SessionId = sessionId;
        Revision = revision;
        Capabilities = capabilities;
        Sha256 = sha256;
    }
}

internal static class WorkshopCapabilityCodec
{
    internal const int MaximumIdentifierLength = 128;
    internal const int MaximumReasonLength = 512;

    internal static WorkshopCapability[] Canonicalize(IEnumerable<WorkshopCapability> capabilities) =>
        (capabilities ?? Enumerable.Empty<WorkshopCapability>())
            .OrderBy(value => value?.ModuleId, StringComparer.Ordinal)
            .ThenBy(value => value?.Operation, StringComparer.Ordinal)
            .ToArray();

    internal static bool TryValidate(WorkshopCapabilitySnapshot snapshot, out string failure)
    {
        if (snapshot == null)
            return Fail("The Workshop capability snapshot is missing.", out failure);
        if (snapshot.ProtocolVersion != WorkshopCapabilitySnapshot.CurrentProtocolVersion)
            return Fail("Unsupported Workshop capability protocol.", out failure);
        if (snapshot.Revision < 0)
            return Fail("The Workshop capability revision cannot be negative.", out failure);
        if (snapshot.SessionId == null || snapshot.SessionId.Length != 32 ||
            !Guid.TryParseExact(snapshot.SessionId, "N", out _))
            return Fail("The Workshop capability session identity is malformed.", out failure);
        if (snapshot.Capabilities == null ||
            snapshot.Capabilities.Length > WorkshopCapabilitySnapshot.MaximumCapabilities)
            return Fail("The Workshop capability count is invalid.", out failure);
        if (snapshot.Sha256 == null || snapshot.Sha256.Length != 64 || !IsLowerHex(snapshot.Sha256))
            return Fail("The Workshop capability SHA-256 is malformed.", out failure);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        string previousKey = null;
        foreach (WorkshopCapability capability in snapshot.Capabilities)
        {
            if (capability == null ||
                !ValidIdentifier(capability.ModuleId) ||
                !ValidIdentifier(capability.Operation) ||
                capability.Reason == null ||
                capability.Reason.Length > MaximumReasonLength ||
                capability.Reason.Any(char.IsControl))
                return Fail("A Workshop capability entry is malformed.", out failure);

            string key = capability.ModuleId + "\0" + capability.Operation;
            if (!keys.Add(key))
                return Fail("The Workshop capability snapshot contains a duplicate operation.", out failure);
            if (previousKey != null && StringComparer.Ordinal.Compare(previousKey, key) >= 0)
                return Fail("The Workshop capability entries are not in canonical order.", out failure);
            previousKey = key;
        }

        string expected = ComputeSha256(
            snapshot.ProtocolVersion,
            snapshot.SessionId,
            snapshot.Revision,
            snapshot.Capabilities);
        if (!string.Equals(expected, snapshot.Sha256, StringComparison.Ordinal))
            return Fail("The Workshop capability SHA-256 does not match its canonical payload.", out failure);

        failure = null;
        return true;
    }

    internal static string ComputeSha256(
        int protocolVersion,
        string sessionId,
        long revision,
        IEnumerable<WorkshopCapability> capabilities)
    {
        WorkshopCapability[] canonical = Canonicalize(capabilities);
        var text = new StringBuilder(512);
        Append(text, protocolVersion.ToString(CultureInfo.InvariantCulture));
        Append(text, sessionId ?? string.Empty);
        Append(text, revision.ToString(CultureInfo.InvariantCulture));
        Append(text, canonical.Length.ToString(CultureInfo.InvariantCulture));
        foreach (WorkshopCapability capability in canonical)
        {
            Append(text, capability?.ModuleId ?? string.Empty);
            Append(text, capability?.Operation ?? string.Empty);
            Append(text, capability?.Enabled == true ? "1" : "0");
            Append(text, capability?.Reason ?? string.Empty);
        }

        using var sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
        var result = new StringBuilder(64);
        foreach (byte value in digest)
            result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return result.ToString();
    }

    private static bool ValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumIdentifierLength &&
        !value.Any(character => char.IsControl(character) || character == '|');

    private static bool IsLowerHex(string value)
    {
        foreach (char character in value)
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                return false;
        return true;
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');

    private static bool Fail(string reason, out string failure)
    {
        failure = reason;
        return false;
    }
}
