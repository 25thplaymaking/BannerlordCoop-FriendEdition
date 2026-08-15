using Common.Messaging;
using LiteNetLib;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GameInterface.Services.WorkshopMods.Fourberie;

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestFourberieState : ICommand
{
}

[ProtoContract]
internal enum FourberieStateValueKind
{
    [ProtoEnum] Boolean = 1,
    [ProtoEnum] StringIntDictionary = 2,
    [ProtoEnum] IntIntDictionary = 3,
    [ProtoEnum] StringStringDictionary = 4,
    [ProtoEnum] StringTimeDictionary = 5,
    [ProtoEnum] IntTimeDictionary = 6,
    [ProtoEnum] StringList = 7,
    [ProtoEnum] ObjectReference = 8,
    [ProtoEnum] StringObjectDictionary = 9,
    [ProtoEnum] ObjectList = 10,
    [ProtoEnum] TroopRosterElement = 11,
    [ProtoEnum] ItemRosterElement = 12,
}

[ProtoContract(SkipConstructor = true)]
internal sealed class FourberieStateEntry
{
    [ProtoMember(1)] public string Field { get; set; }
    [ProtoMember(2)] public FourberieStateValueKind Kind { get; set; }
    [ProtoMember(3)] public string Key { get; set; }
    [ProtoMember(4)] public string Value { get; set; }
    [ProtoMember(5)] public int Ordinal { get; set; }

    public FourberieStateEntry()
    {
    }

    public FourberieStateEntry(
        string field,
        FourberieStateValueKind kind,
        string key,
        string value,
        int ordinal = 0)
    {
        Field = field;
        Kind = kind;
        Key = key;
        Value = value;
        Ordinal = ordinal;
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkFourberieState : ICommand
{
    [ProtoMember(1)] public string AdapterVersion { get; set; }
    [ProtoMember(2)] public long Revision { get; set; }
    [ProtoMember(3)] public string ConfigurationFingerprint { get; set; }
    [ProtoMember(4)] public string StateFingerprint { get; set; }
    [ProtoMember(5)] public FourberieStateEntry[] Entries { get; set; }

    public NetworkFourberieState()
    {
    }

    public NetworkFourberieState(
        string adapterVersion,
        long revision,
        string configurationFingerprint,
        string stateFingerprint,
        FourberieStateEntry[] entries)
    {
        AdapterVersion = adapterVersion;
        Revision = revision;
        ConfigurationFingerprint = configurationFingerprint;
        StateFingerprint = stateFingerprint;
        Entries = entries;
    }
}

internal static class FourberieStateCodec
{
    public const int MaximumEntries = 4096;
    public const int MaximumFieldLength = 64;
    public const int MaximumKeyLength = 256;
    public const int MaximumValueLength = 512;

    public static string ComputeHash(IEnumerable<FourberieStateEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in Sort(entries ?? Array.Empty<FourberieStateEntry>()))
        {
            Append(builder, entry.Field);
            Append(builder, ((int)entry.Kind).ToString(CultureInfo.InvariantCulture));
            Append(builder, entry.Ordinal.ToString(CultureInfo.InvariantCulture));
            Append(builder, entry.Key);
            Append(builder, entry.Value);
        }

        using (var sha = SHA256.Create())
            return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public static bool TryValidate(NetworkFourberieState state, out string failure)
    {
        if (state == null)
        {
            failure = "snapshot is null";
            return false;
        }
        if (!string.Equals(state.AdapterVersion, FourberieCompatibilityManifest.AdapterVersion, StringComparison.Ordinal))
        {
            failure = $"unsupported adapter version {state.AdapterVersion ?? "missing"}";
            return false;
        }
        if (state.Revision < 0)
        {
            failure = "negative snapshot revision";
            return false;
        }
        if (!IsSha256(state.ConfigurationFingerprint) || !IsSha256(state.StateFingerprint))
        {
            failure = "snapshot contains an invalid SHA-256 fingerprint";
            return false;
        }

        var entries = state.Entries ?? Array.Empty<FourberieStateEntry>();
        if (entries.Length > MaximumEntries)
        {
            failure = $"snapshot contains more than {MaximumEntries} entries";
            return false;
        }

        foreach (var entry in entries)
        {
            if (entry == null ||
                !Bounded(entry.Field, MaximumFieldLength, allowEmpty: false) ||
                !Bounded(entry.Key, MaximumKeyLength, allowEmpty: true) ||
                !Bounded(entry.Value, MaximumValueLength, allowEmpty: true) ||
                entry.Ordinal < 0 ||
                !Enum.IsDefined(typeof(FourberieStateValueKind), entry.Kind))
            {
                failure = "snapshot contains an invalid or oversized state entry";
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

    internal static bool IsSha256(string value)
    {
        if (value == null || value.Length != 64) return false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!(character >= '0' && character <= '9') &&
                !(character >= 'a' && character <= 'f') &&
                !(character >= 'A' && character <= 'F'))
            {
                return false;
            }
        }
        return true;
    }

    internal static IEnumerable<FourberieStateEntry> Sort(IEnumerable<FourberieStateEntry> entries) =>
        entries.Where(entry => entry != null)
            .OrderBy(entry => entry.Field ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(entry => (int)entry.Kind)
            .ThenBy(entry => entry.Ordinal)
            .ThenBy(entry => entry.Key ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(entry => entry.Value ?? string.Empty, StringComparer.Ordinal);

    private static void Append(StringBuilder builder, string value)
    {
        value ??= string.Empty;
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':').Append(value).Append(';');
    }

    private static bool Bounded(string value, int limit, bool allowEmpty) =>
        value != null && value.Length <= limit && (allowEmpty || value.Length > 0);

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
        var characters = new char[bytes.Length * 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            characters[index * 2] = digits[bytes[index] >> 4];
            characters[(index * 2) + 1] = digits[bytes[index] & 15];
        }
        return new string(characters);
    }
}

/// <summary>
/// Trusts only a state command delivered through the rendered client's server transport.
/// Local broker publications have no <see cref="NetPeer"/> source and are rejected.
/// </summary>
internal static class FourberieSnapshotOriginGuard
{
    public static bool IsTrustedServerTransport(object source, bool localIsClient) =>
        localIsClient && source is NetPeer;
}
