using System;
using System.Collections.Generic;
using System.Linq;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

internal static class DiplomacySnapshotCodec
{
    internal const int MaximumSettings = 256;
    internal const int MaximumStateEntries = 4096;
    internal const int MaximumIdentityLength = 128;
    internal const int MaximumKeyLength = 256;
    internal const int MaximumSettingValueLength = 512;

    private const string MarkerKey = "\u0001present";

    internal static readonly string[] RequiredSections =
    {
        "expansionism",
        "cooldown.peace-proposal",
        "cooldown.alliance",
        "cooldown.war",
        "agreement.non-aggression",
        "war-exhaustion.score",
        "war-exhaustion.rate",
        "war-exhaustion.events-omitted",
    };

    private static readonly HashSet<string> AllowedSections =
        new(RequiredSections, StringComparer.Ordinal);

    internal static bool TryValidate(NetworkDiplomacySnapshot snapshot, out string failure)
    {
        if (snapshot == null)
        {
            failure = "snapshot is null";
            return false;
        }
        if (snapshot.Revision < 0)
        {
            failure = "snapshot revision is negative";
            return false;
        }
        if (!Bounded(snapshot.AssemblyVersion, 64, allowEmpty: false) ||
            !Bounded(snapshot.CampaignId, MaximumIdentityLength, allowEmpty: false) ||
            !IsSha256(snapshot.AssemblySha256) ||
            !IsSha256(snapshot.SettingsFingerprint) ||
            !IsSha256(snapshot.StateFingerprint))
        {
            failure = "snapshot implementation identity or fingerprint is malformed";
            return false;
        }
        if (snapshot.Settings == null || snapshot.Settings.Count > MaximumSettings ||
            snapshot.State == null || snapshot.State.Count > MaximumStateEntries)
        {
            failure = "snapshot exceeds its bounded collection shape";
            return false;
        }

        var settingNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Settings)
        {
            if (entry == null ||
                !Bounded(entry.Name, MaximumIdentityLength, allowEmpty: false) ||
                !Bounded(entry.TypeName, MaximumIdentityLength, allowEmpty: false) ||
                !Bounded(entry.Value, MaximumSettingValueLength, allowEmpty: true) ||
                !HasFiniteFloatingPointValue(entry) ||
                !settingNames.Add(entry.Name))
            {
                failure = "snapshot contains a duplicate, invalid, or oversized setting";
                return false;
            }
        }

        var stateKeys = new HashSet<string>(StringComparer.Ordinal);
        var markedSections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in snapshot.State)
        {
            if (entry == null ||
                !Bounded(entry.Section, MaximumIdentityLength, allowEmpty: false) ||
                !Bounded(entry.Key, MaximumKeyLength, allowEmpty: true) ||
                !Bounded(entry.Faction1Id, MaximumIdentityLength, allowEmpty: true) ||
                !Bounded(entry.Faction2Id, MaximumIdentityLength, allowEmpty: true) ||
                !AllowedSections.Contains(entry.Section) ||
                float.IsNaN(entry.Value1) || float.IsInfinity(entry.Value1) ||
                float.IsNaN(entry.Value2) || float.IsInfinity(entry.Value2))
            {
                failure = "snapshot contains an invalid, non-finite, or oversized state entry";
                return false;
            }

            string identity = string.Join("\u001f", entry.Section, entry.Key, entry.Faction1Id, entry.Faction2Id);
            if (!stateKeys.Add(identity))
            {
                failure = "snapshot contains duplicate state identities";
                return false;
            }

            if (entry.Key == MarkerKey)
            {
                if (!IsMarker(entry) || !markedSections.Add(entry.Section))
                {
                    failure = "snapshot contains an invalid or duplicate section marker";
                    return false;
                }
            }
            else if (!ValidateSectionEntry(entry))
            {
                failure = $"snapshot contains an invalid {entry.Section} entry shape";
                return false;
            }
        }

        foreach (var section in snapshot.State.Where(entry => entry.Key != MarkerKey).Select(entry => entry.Section))
        {
            if (markedSections.Contains(section)) continue;
            failure = $"snapshot section {section} has data without a presence marker";
            return false;
        }

        if (!markedSections.SetEquals(RequiredSections))
        {
            var missing = RequiredSections.Where(section => !markedSections.Contains(section));
            failure = "snapshot is missing required manager section marker(s): " + string.Join(", ", missing);
            return false;
        }

        string settingsFingerprint = DiplomacyRuntime.FingerprintSettings(snapshot.Settings);
        string stateFingerprint = DiplomacyRuntime.FingerprintState(snapshot.State);
        if (!FixedTimeEquals(settingsFingerprint, snapshot.SettingsFingerprint) ||
            !FixedTimeEquals(stateFingerprint, snapshot.StateFingerprint))
        {
            failure = "snapshot contents do not match their SHA-256 fingerprints";
            return false;
        }

        failure = null;
        return true;
    }

    internal static bool IsSha256(string value)
    {
        if (value == null || value.Length != 64) return false;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }
        return true;
    }

    internal static string Identity(NetworkDiplomacySnapshot snapshot) => string.Join(
        "|",
        snapshot.AssemblyVersion,
        snapshot.AssemblySha256,
        snapshot.CampaignId,
        snapshot.StateSchema,
        snapshot.FriendSeparatismOwnsRebellions,
        snapshot.SettingsFingerprint,
        snapshot.StateFingerprint);

    private static bool ValidateSectionEntry(DiplomacyStateEntry entry)
    {
        switch (entry.Section)
        {
            case "expansionism":
                return NonEmpty(entry.Faction1Id) && Empty(entry.Key) && Empty(entry.Faction2Id) &&
                       entry.Ticks1 == 0 && entry.Ticks2 == 0 &&
                       entry.Value1 >= 0f && entry.Value1 <= 1_000_000f && entry.Value2 == 0f &&
                       entry.Flags1 == 0 && entry.Flags2 == 0;
            case "cooldown.peace-proposal":
                return NonEmpty(entry.Faction1Id) && Empty(entry.Key) && Empty(entry.Faction2Id) &&
                       entry.Ticks1 >= 0 && entry.Ticks2 == 0 &&
                       entry.Value1 == 0f && entry.Value2 == 0f &&
                       entry.Flags1 == 0 && entry.Flags2 == 0;
            case "cooldown.alliance":
            case "cooldown.war":
                return NonEmpty(entry.Key) && Empty(entry.Faction1Id) && Empty(entry.Faction2Id) &&
                       entry.Ticks1 >= 0 && entry.Ticks2 == 0 &&
                       entry.Value1 == 0f && entry.Value2 == 0f &&
                       entry.Flags1 == 0 && entry.Flags2 == 0;
            case "war-exhaustion.score":
                return Empty(entry.Key) && NonEmpty(entry.Faction1Id) && NonEmpty(entry.Faction2Id) &&
                       !string.Equals(entry.Faction1Id, entry.Faction2Id, StringComparison.Ordinal) &&
                       entry.Ticks1 == 0 && entry.Ticks2 == 0 &&
                       entry.Value1 >= 0f && entry.Value1 <= 100f &&
                       entry.Value2 >= 0f && entry.Value2 <= 100f &&
                       entry.Flags1 is >= 0 and <= 3 && entry.Flags2 is >= 0 and <= 2;
            case "war-exhaustion.rate":
                return Empty(entry.Key) && NonEmpty(entry.Faction1Id) && NonEmpty(entry.Faction2Id) &&
                       !string.Equals(entry.Faction1Id, entry.Faction2Id, StringComparison.Ordinal) &&
                       entry.Ticks1 == 0 && entry.Ticks2 == 0 &&
                       entry.Value1 >= 0f && entry.Value1 <= 100f &&
                       entry.Value2 >= 0f && entry.Value2 <= 100f &&
                       entry.Flags1 is >= 0 and <= 3 && entry.Flags2 is >= 0 and <= 2;
            case "agreement.non-aggression":
                return NonEmpty(entry.Faction1Id) && NonEmpty(entry.Faction2Id) && Empty(entry.Key) &&
                       !string.Equals(entry.Faction1Id, entry.Faction2Id, StringComparison.Ordinal) &&
                       entry.Ticks1 >= 0 && entry.Ticks2 >= entry.Ticks1 &&
                       entry.Value1 == 0f && entry.Value2 == 0f &&
                       entry.Flags1 == 0 && entry.Flags2 == 0;
            case "war-exhaustion.events-omitted":
                return false;
            default:
                return false;
        }
    }

    private static bool HasFiniteFloatingPointValue(DiplomacySettingEntry entry)
    {
        if (entry.TypeName == "System.Single")
        {
            return float.TryParse(
                       entry.Value,
                       System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var value) &&
                   !float.IsNaN(value) && !float.IsInfinity(value);
        }
        if (entry.TypeName == "System.Double")
        {
            return double.TryParse(
                       entry.Value,
                       System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var value) &&
                   !double.IsNaN(value) && !double.IsInfinity(value);
        }
        return true;
    }

    private static bool IsMarker(DiplomacyStateEntry entry) =>
        Empty(entry.Faction1Id) && Empty(entry.Faction2Id) &&
        entry.Ticks1 == 0 && entry.Ticks2 == 0 &&
        entry.Value1 == 0f && entry.Value2 == 0f &&
        entry.Flags1 == 0 && entry.Flags2 == 0;

    private static bool Bounded(string value, int maximum, bool allowEmpty)
    {
        if (value == null || value.Length > maximum || (!allowEmpty && value.Length == 0)) return false;
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '\0') return false;
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) ||
                index + 1 >= value.Length ||
                !char.IsLowSurrogate(value[++index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        int difference = 0;
        for (int index = 0; index < left.Length; index++)
            difference |= char.ToLowerInvariant(left[index]) ^ char.ToLowerInvariant(right[index]);
        return difference == 0;
    }

    private static bool Empty(string value) => string.IsNullOrEmpty(value);
    private static bool NonEmpty(string value) => !string.IsNullOrEmpty(value);
}

internal enum DiplomacyRevisionDecision
{
    Apply,
    AlreadyCurrent,
    Stale,
    Conflict,
    Invalid,
}

internal sealed class DiplomacyRevisionGate
{
    private long revision = -1;
    private string identity;

    internal long Revision => revision;

    internal DiplomacyRevisionDecision Evaluate(NetworkDiplomacySnapshot snapshot)
    {
        if (snapshot == null || snapshot.Revision < 0 ||
            !DiplomacySnapshotCodec.IsSha256(snapshot.SettingsFingerprint) ||
            !DiplomacySnapshotCodec.IsSha256(snapshot.StateFingerprint))
        {
            return DiplomacyRevisionDecision.Invalid;
        }
        if (snapshot.Revision < revision) return DiplomacyRevisionDecision.Stale;
        if (snapshot.Revision > revision) return DiplomacyRevisionDecision.Apply;
        return string.Equals(DiplomacySnapshotCodec.Identity(snapshot), identity, StringComparison.Ordinal)
            ? DiplomacyRevisionDecision.AlreadyCurrent
            : DiplomacyRevisionDecision.Conflict;
    }

    internal bool Commit(NetworkDiplomacySnapshot snapshot)
    {
        if (Evaluate(snapshot) != DiplomacyRevisionDecision.Apply) return false;
        revision = snapshot.Revision;
        identity = DiplomacySnapshotCodec.Identity(snapshot);
        return true;
    }

    internal void Reset()
    {
        revision = -1;
        identity = null;
    }
}

internal sealed class DiplomacySnapshotRevisionSequence
{
    private readonly object sync = new();
    private long revision = -1;
    private string identity;

    internal long Stamp(NetworkDiplomacySnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        lock (sync)
        {
            string candidateIdentity = DiplomacySnapshotCodec.Identity(snapshot);
            if (!string.Equals(candidateIdentity, identity, StringComparison.Ordinal))
            {
                if (revision == long.MaxValue)
                    throw new InvalidOperationException("Diplomacy snapshot revision was exhausted.");
                revision++;
                identity = candidateIdentity;
            }

            snapshot.Revision = revision;
            return revision;
        }
    }

    internal void Reset()
    {
        lock (sync)
        {
            revision = -1;
            identity = null;
        }
    }
}
