using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.PlayerSettlement;

/// <summary>
/// Reads the save metadata already produced by Player Settlement without taking a compile-time
/// dependency on it. The result contains the exact generated XML, stable object IDs, parent graph,
/// prefab/version fields, and a deterministic component fingerprint. Adapter v1 does not apply
/// non-empty results on clients because MBObjectManager registration has already passed by the time
/// a late join snapshot arrives.
/// </summary>
internal static class PlayerSettlementCanonicalState
{
    private const string BehaviorTypeName =
        "BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour";

    internal static bool TryCapture(
        Assembly assembly,
        out PlayerSettlementStateEntry[] entries,
        out string fingerprint,
        out string failure)
    {
        entries = Array.Empty<PlayerSettlementStateEntry>();
        fingerprint = null;
        failure = null;

        try
        {
            var behaviorType = assembly?.GetType(BehaviorTypeName, throwOnError: false, ignoreCase: false);
            if (behaviorType == null)
            {
                failure = $"missing {BehaviorTypeName}";
                return false;
            }

            var behavior = behaviorType
                .GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(null);
            if (behavior == null)
            {
                entries = Array.Empty<PlayerSettlementStateEntry>();
                fingerprint = PlayerSettlementStateCodec.ComputeHash(entries);
                return true;
            }

            var meta = behaviorType
                .GetProperty("MetaV3", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(behavior);
            if (meta == null)
            {
                entries = Array.Empty<PlayerSettlementStateEntry>();
                fingerprint = PlayerSettlementStateCodec.ComputeHash(entries);
                return true;
            }

            return TryCaptureMetadata(meta, out entries, out fingerprint, out failure);
        }
        catch (Exception exception)
        {
            failure = $"Player Settlement state capture failed: {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// Validates a deserialized MetaV3 payload before it is assigned to the optional behavior.
    /// This keeps non-empty/corrupt save rejection free of partial behavior-field mutations.
    /// </summary>
    internal static bool TryCaptureMetadata(
        object meta,
        out PlayerSettlementStateEntry[] entries,
        out string fingerprint,
        out string failure)
    {
        entries = Array.Empty<PlayerSettlementStateEntry>();
        fingerprint = null;
        failure = null;
        if (meta == null)
        {
            fingerprint = PlayerSettlementStateCodec.ComputeHash(entries);
            return true;
        }

        try
        {
            var result = new List<PlayerSettlementStateEntry>();
            if (!CaptureTopLevel(meta, "Towns", PlayerSettlementObjectKind.Town, result, out failure) ||
                !CaptureTopLevel(meta, "Castles", PlayerSettlementObjectKind.Castle, result, out failure) ||
                !CaptureTopLevel(meta, "ExtraVillages", PlayerSettlementObjectKind.ExtraVillage, result, out failure) ||
                !CaptureTopLevel(meta, "OverwriteSettlements", PlayerSettlementObjectKind.Overwrite, result, out failure))
            {
                return false;
            }

            if (result.Count > PlayerSettlementStateCodec.MaximumEntries)
            {
                failure = $"Player Settlement metadata exceeds {PlayerSettlementStateCodec.MaximumEntries} objects";
                return false;
            }

            entries = PlayerSettlementStateCodec.Sort(result).ToArray();
            fingerprint = PlayerSettlementStateCodec.ComputeHash(entries);
            return true;
        }
        catch (Exception exception)
        {
            failure = $"Player Settlement metadata capture failed: {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static bool CaptureTopLevel(
        object meta,
        string property,
        PlayerSettlementObjectKind kind,
        ICollection<PlayerSettlementStateEntry> output,
        out string failure)
    {
        failure = null;
        var values = ReadMember(meta, property) as IEnumerable;
        if (values == null)
        {
            failure = $"Player Settlement metadata list {property} is missing";
            return false;
        }

        var ordinal = 0;
        foreach (var value in values)
        {
            if (!TryCreateEntry(value, kind, ordinal++, string.Empty, out var entry, out failure))
                return false;
            output.Add(entry);

            if (kind != PlayerSettlementObjectKind.Town && kind != PlayerSettlementObjectKind.Castle)
                continue;

            var villages = ReadMember(value, "Villages") as IEnumerable;
            if (villages == null)
            {
                failure = $"Player Settlement parent {entry.StringId} has no village list";
                return false;
            }

            var villageOrdinal = 0;
            foreach (var village in villages)
            {
                if (!TryCreateEntry(
                        village,
                        PlayerSettlementObjectKind.BoundVillage,
                        villageOrdinal++,
                        entry.StringId,
                        out var villageEntry,
                        out failure))
                {
                    return false;
                }
                output.Add(villageEntry);
            }
        }

        return true;
    }

    private static bool TryCreateEntry(
        object value,
        PlayerSettlementObjectKind kind,
        int ordinal,
        string parentId,
        out PlayerSettlementStateEntry entry,
        out string failure)
    {
        entry = null;
        failure = null;
        if (value == null)
        {
            failure = "Player Settlement metadata contains a null object";
            return false;
        }

        var stringId = ReadMember(value, "StringId") as string;
        var xml = ReadMember(value, "XML") as string;
        var prefabId = ReadMember(value, "PrefabId") as string ?? string.Empty;
        var moduleVersion = ReadMember(value, "Version") as string ?? string.Empty;
        var displayName = ReadMember(value, "DisplayName") as string ?? string.Empty;
        var buildTimeValue = ReadMember(value, "BuildTime");
        if (string.IsNullOrEmpty(stringId) || string.IsNullOrEmpty(xml) || buildTimeValue == null)
        {
            failure = "Player Settlement metadata is missing StringId, XML, or BuildTime";
            return false;
        }

        if (!PlayerSettlementStateCodec.TryFingerprintXml(
                xml,
                out var xmlSha256,
                out var componentFingerprint,
                out failure))
        {
            return false;
        }

        entry = new PlayerSettlementStateEntry(
            kind,
            ordinal,
            stringId,
            parentId,
            prefabId,
            moduleVersion,
            displayName,
            Convert.ToSingle(buildTimeValue, System.Globalization.CultureInfo.InvariantCulture),
            xml,
            xmlSha256,
            componentFingerprint);
        return true;
    }

    private static object ReadMember(object instance, string name)
    {
        if (instance == null) return null;
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = instance.GetType();
        var property = type.GetProperty(name, flags);
        if (property != null) return property.GetValue(instance);
        return type.GetField(name, flags)?.GetValue(instance);
    }
}

/// <summary>
/// Pure admission boundary used after metadata capture and before either object registration or
/// network publication. Keeping this separate makes the no-mutation/fail-closed behavior directly
/// testable without loading the optional Workshop assembly.
/// </summary>
internal static class PlayerSettlementStateAdmission
{
    internal static PlayerSettlementStateEntry[] RequireEmpty(
        bool captureSucceeded,
        PlayerSettlementStateEntry[] entries,
        string captureFailure,
        string phase)
    {
        if (!captureSucceeded)
        {
            throw new InvalidOperationException(
                $"Player Settlement failed closed during {phase}: save metadata could not be captured ({captureFailure ?? "unknown failure"}).");
        }

        entries ??= Array.Empty<PlayerSettlementStateEntry>();
        if (entries.Length != 0)
        {
            throw new InvalidOperationException(
                $"Player Settlement failed closed during {phase}: this save contains {entries.Length} generated objects. Adapter v{PlayerSettlementCompatibilityManifest.AdapterVersion} will not load or publish them until their complete XML/object/component graph has an atomic Coop registration transaction.");
        }

        return entries;
    }
}
