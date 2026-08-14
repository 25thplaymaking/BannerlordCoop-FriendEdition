using Common;
using Common.Logging;
using GameInterface.Services;
using Serilog;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.ObjectSystem;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

internal interface IDiplomacyRuntime : IGameAbstraction
{
    bool IsAvailable { get; }
    string AssemblyVersion { get; }
    NetworkDiplomacySnapshot CaptureSnapshot();
    DiplomacySnapshotApplyResult ApplySnapshot(NetworkDiplomacySnapshot snapshot);
    DiplomacySnapshotApplyResult ValidateUiReadiness(NetworkDiplomacySnapshot snapshot);
    void ResetSnapshotRevision();
}

/// <summary>
/// Reflection adapter for Diplomacy's optional assembly. It synchronizes only compact scalar state
/// which Diplomacy otherwise stores in private save managers. Native campaign state (kingdoms,
/// stances, decisions, clans, settlements, gold and influence) intentionally remains owned by the
/// existing Coop services.
/// </summary>
internal sealed class DiplomacyRuntime : IDiplomacyRuntime
{
    internal const int CurrentStateSchema = 1;

    internal const string MarkerKey = "\u0001present";
    internal const string ExpansionismSection = "expansionism";
    internal const string PeaceProposalSection = "cooldown.peace-proposal";
    internal const string AllianceCooldownSection = "cooldown.alliance";
    internal const string WarCooldownSection = "cooldown.war";
    internal const string AgreementSection = "agreement.non-aggression";
    internal const string WarScoreSection = "war-exhaustion.score";
    internal const string WarRateSection = "war-exhaustion.rate";
    internal const string WarEventsSection = "war-exhaustion.events-omitted";

    private static readonly ILogger Logger = LogManager.GetLogger<DiplomacyRuntime>();
    private static bool loggedDebugSettingNormalization;
    private readonly DiplomacySnapshotRevisionSequence snapshotRevisions = new();

    public bool IsAvailable => DiplomacyCompatibilityPolicy.ResolveAssembly() != null;

    public string AssemblyVersion =>
        DiplomacyCompatibilityPolicy.ResolveAssembly()?.GetName().Version?.ToString() ?? string.Empty;

    public NetworkDiplomacySnapshot CaptureSnapshot()
    {
        if (!IsAvailable) return null;
        var settingsType = DiplomacyCompatibilityPolicy.ResolveType(DiplomacyCompatibilityPolicy.SettingsTypeName);
        var settings = GetStaticInstance(settingsType);
        if (settings == null)
        {
            Logger.Error("Refused to capture Diplomacy state because Settings.Instance is unavailable.");
            return null;
        }
        NormalizeServerUnsafeSettings(settingsType, settings);
        DiplomacyManagerCaptureBarrier.RequireReady(
            EnsureManager,
            () => TryValidateRequiredManagerShape(out var failure) ? null : failure);

        var snapshot = new NetworkDiplomacySnapshot
        {
            AssemblyVersion = AssemblyVersion,
            AssemblySha256 = DiplomacyCompatibilityPolicy.SupportedAssemblySha256,
            CampaignId = Campaign.Current?.UniqueGameId ?? string.Empty,
            StateSchema = CurrentStateSchema,
            FriendSeparatismOwnsRebellions = DiplomacyCompatibilityPolicy.FriendSeparatismOwnsRebellions,
        };

        CaptureSettings(snapshot.Settings);
        CaptureExpansionism(snapshot.State);
        CaptureCooldowns(snapshot.State);
        CaptureAgreements(snapshot.State);
        CaptureWarExhaustion(snapshot.State);

        SortSnapshot(snapshot);
        snapshot.SettingsFingerprint = FingerprintSettings(snapshot.Settings);
        snapshot.StateFingerprint = FingerprintState(snapshot.State);
        if (!DiplomacySnapshotCodec.TryValidate(snapshot, out var validationFailure))
        {
            Logger.Error("Refused to publish malformed local Diplomacy snapshot: {Failure}", validationFailure);
            return null;
        }
        snapshotRevisions.Stamp(snapshot);
        return snapshot;
    }

    public void ResetSnapshotRevision() => snapshotRevisions.Reset();

    public DiplomacySnapshotApplyResult ApplySnapshot(NetworkDiplomacySnapshot snapshot)
    {
        if (!DiplomacySnapshotCodec.TryValidate(snapshot, out var malformedFailure))
        {
            return new DiplomacySnapshotApplyResult(
                DiplomacySnapshotApplyStatus.MalformedSnapshot,
                malformedFailure);
        }
        if (!IsAvailable)
            return new DiplomacySnapshotApplyResult(
                DiplomacySnapshotApplyStatus.ModNotLoaded,
                DiplomacyCompatibilityPolicy.CompatibilityFailure);
        if (!string.Equals(AssemblyVersion, snapshot.AssemblyVersion, StringComparison.Ordinal))
        {
            return new DiplomacySnapshotApplyResult(
                DiplomacySnapshotApplyStatus.VersionMismatch,
                $"Host has Diplomacy {snapshot.AssemblyVersion}; client has {AssemblyVersion}.");
        }
        if (!string.Equals(
                DiplomacyCompatibilityPolicy.SupportedAssemblySha256,
                snapshot.AssemblySha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return new DiplomacySnapshotApplyResult(
                DiplomacySnapshotApplyStatus.VersionMismatch,
                "Host and client Diplomacy DLL fingerprints differ.");
        }
        if (snapshot.StateSchema != CurrentStateSchema)
        {
            return new DiplomacySnapshotApplyResult(
                DiplomacySnapshotApplyStatus.SchemaMismatch,
                $"Host state schema is {snapshot.StateSchema}; client supports {CurrentStateSchema}.");
        }
        string localCampaignId = Campaign.Current?.UniqueGameId ?? string.Empty;
        if (!string.Equals(localCampaignId, snapshot.CampaignId, StringComparison.Ordinal))
        {
            return new DiplomacySnapshotApplyResult(
                DiplomacySnapshotApplyStatus.CampaignMismatch,
                $"Host campaign {snapshot.CampaignId} does not match local campaign {localCampaignId}.");
        }
        if (snapshot.FriendSeparatismOwnsRebellions !=
            DiplomacyCompatibilityPolicy.FriendSeparatismOwnsRebellions)
        {
            return new DiplomacySnapshotApplyResult(
                DiplomacySnapshotApplyStatus.ConfigurationMismatch,
                "Friend Edition Separatism ownership differs from the host; wait for server mod-config synchronization.");
        }

        var preflight = ValidateApplicationShape(snapshot);
        if (preflight.Status != DiplomacySnapshotApplyStatus.Applied) return preflight;

        DiplomacyMutationTransaction transaction;
        try
        {
            transaction = CaptureMutationTransaction();
        }
        catch (Exception ex)
        {
            return new DiplomacySnapshotApplyResult(
                DiplomacySnapshotApplyStatus.ApplyFailed,
                $"Could not journal Diplomacy state before mutation: {ex.Message}");
        }

        try
        {
            EnsureManager("Diplomacy.ExpansionismManager");
            EnsureManager("Diplomacy.CooldownManager");
            EnsureManager("Diplomacy.DiplomaticAction.DiplomaticAgreementManager");
            EnsureManager("Diplomacy.WarExhaustion.WarExhaustionManager");
            // Never synced — Friend Edition Separatism owns rebellions, so Diplomacy's civil-war
            // ledger is canonically EMPTY — but it must EXIST: the pinned cost calculator consults
            // RebelFactionManager.AllRebelFactions (=> Instance.RebelFactions) for every war row the
            // Kingdom tab builds, and a null Instance NREs KingdomWarItemVMMixin and blacks out the
            // tab (2026-08-13).
            EnsureManager("Diplomacy.CivilWar.RebelFactionManager");

            if (!TryValidateRequiredManagerShape(out var managerShapeFailure))
            {
                return RollBack(
                    transaction,
                    new DiplomacySnapshotApplyResult(
                        DiplomacySnapshotApplyStatus.VersionMismatch,
                        managerShapeFailure));
            }

            ApplySettings(snapshot.Settings);
            var appliedSettings = new List<DiplomacySettingEntry>();
            CaptureSettings(appliedSettings);
            if (!string.Equals(FingerprintSettings(appliedSettings), snapshot.SettingsFingerprint, StringComparison.Ordinal))
            {
                return RollBack(
                    transaction,
                    new DiplomacySnapshotApplyResult(
                        DiplomacySnapshotApplyStatus.SettingsMismatch,
                        "The host Diplomacy settings could not be reproduced on this client."));
            }

            ApplyExpansionism(snapshot.State);
            ApplyCooldowns(snapshot.State);
            ApplyAgreements(snapshot.State);
            ApplyWarExhaustion(snapshot.State);

            var appliedState = new List<DiplomacyStateEntry>();
            CaptureExpansionism(appliedState);
            CaptureCooldowns(appliedState);
            CaptureAgreements(appliedState);
            CaptureWarExhaustion(appliedState);
            if (!string.Equals(FingerprintState(appliedState), snapshot.StateFingerprint, StringComparison.Ordinal))
            {
                return RollBack(
                    transaction,
                    new DiplomacySnapshotApplyResult(
                        DiplomacySnapshotApplyStatus.StateMismatch,
                        "The host Diplomacy manager state could not be reproduced on this client."));
            }

            return new DiplomacySnapshotApplyResult(DiplomacySnapshotApplyStatus.Applied);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to apply Diplomacy compatibility snapshot.");
            return RollBack(
                transaction,
                new DiplomacySnapshotApplyResult(DiplomacySnapshotApplyStatus.ApplyFailed, ex.Message));
        }
    }

    public DiplomacySnapshotApplyResult ValidateUiReadiness(NetworkDiplomacySnapshot snapshot)
    {
        if (!DiplomacySnapshotCodec.TryValidate(snapshot, out var malformedFailure))
            return new DiplomacySnapshotApplyResult(
                DiplomacySnapshotApplyStatus.MalformedSnapshot,
                malformedFailure);
        if (!IsAvailable)
            return new DiplomacySnapshotApplyResult(
                DiplomacySnapshotApplyStatus.ModNotLoaded,
                DiplomacyCompatibilityPolicy.CompatibilityFailure);

        try
        {
            var settingsType = DiplomacyCompatibilityPolicy.ResolveType(
                DiplomacyCompatibilityPolicy.SettingsTypeName);
            if (GetStaticInstance(settingsType) == null)
            {
                return new DiplomacySnapshotApplyResult(
                    DiplomacySnapshotApplyStatus.SettingsMismatch,
                    "Diplomacy Settings.Instance is unavailable at the Kingdom UI boundary.");
            }

            var appliedSettings = new List<DiplomacySettingEntry>();
            CaptureSettings(appliedSettings);
            if (!string.Equals(
                    FingerprintSettings(appliedSettings),
                    snapshot.SettingsFingerprint,
                    StringComparison.Ordinal))
            {
                return new DiplomacySnapshotApplyResult(
                    DiplomacySnapshotApplyStatus.SettingsMismatch,
                    "Diplomacy MCM settings no longer match the authoritative host snapshot.");
            }

            if (!TryValidateRequiredManagerShape(out var managerFailure))
            {
                return new DiplomacySnapshotApplyResult(
                    DiplomacySnapshotApplyStatus.ApplyFailed,
                    managerFailure);
            }

            return new DiplomacySnapshotApplyResult(DiplomacySnapshotApplyStatus.AlreadyCurrent);
        }
        catch (Exception ex)
        {
            return new DiplomacySnapshotApplyResult(
                DiplomacySnapshotApplyStatus.ApplyFailed,
                "Diplomacy Kingdom UI readiness validation threw: " + ex.Message);
        }
    }

    private static DiplomacySnapshotApplyResult RollBack(
        DiplomacyMutationTransaction transaction,
        DiplomacySnapshotApplyResult rejection)
    {
        if (transaction.TryRestore(out var failure)) return rejection;

        Logger.Fatal(
            "Diplomacy snapshot failed and its rollback was incomplete: {Failure}",
            failure);
        return new DiplomacySnapshotApplyResult(
            DiplomacySnapshotApplyStatus.ApplyFailed,
            $"{rejection.Detail} Rollback was incomplete: {failure}");
    }

    private static DiplomacyMutationTransaction CaptureMutationTransaction()
    {
        var valueTargets = new List<DiplomacyValueBackupTarget>();
        var dictionaryTargets = new List<IDictionary>();

        var settingsType = DiplomacyCompatibilityPolicy.ResolveType(DiplomacyCompatibilityPolicy.SettingsTypeName);
        var settings = GetStaticInstance(settingsType) ??
                       throw new InvalidOperationException("Diplomacy Settings.Instance was unavailable.");
        foreach (var property in GetSettingProperties(settingsType))
        {
            var capturedProperty = property;
            valueTargets.Add(new DiplomacyValueBackupTarget(
                () => capturedProperty.GetValue(settings),
                value => capturedProperty.SetValue(settings, value)));
        }

        CaptureManager(
            "Diplomacy.ExpansionismManager",
            new[] { "_expansionism" },
            valueTargets,
            dictionaryTargets);
        CaptureManager(
            "Diplomacy.CooldownManager",
            new[] { "_lastPeaceProposalTime", "_lastAllianceFormedTime", "_lastWarTime" },
            valueTargets,
            dictionaryTargets);
        CaptureManager(
            "Diplomacy.DiplomaticAction.DiplomaticAgreementManager",
            new[] { "Agreements" },
            valueTargets,
            dictionaryTargets);
        CaptureManager(
            "Diplomacy.WarExhaustion.WarExhaustionManager",
            new[] { "_warExhaustionScores", "_warExhaustionRates", "_warExhaustionEventRecords" },
            valueTargets,
            dictionaryTargets);

        return new DiplomacyMutationTransaction(valueTargets, dictionaryTargets);
    }

    private static void CaptureManager(
        string managerTypeName,
        IEnumerable<string> dictionaryMembers,
        ICollection<DiplomacyValueBackupTarget> valueTargets,
        ICollection<IDictionary> dictionaryTargets)
    {
        var type = DiplomacyCompatibilityPolicy.ResolveType(managerTypeName) ??
                   throw new InvalidOperationException($"Manager type {managerTypeName} was unavailable.");
        if (!TryCreateStaticInstanceTarget(type, out var instanceTarget))
            throw new InvalidOperationException($"Manager {managerTypeName}.Instance cannot be restored.");

        valueTargets.Add(instanceTarget);
        var instance = GetStaticInstance(type);
        if (instance == null) return;
        foreach (var member in dictionaryMembers)
        {
            if (GetMemberValue(instance, member) is not IDictionary dictionary)
                throw new InvalidOperationException(
                    $"Manager {managerTypeName}.{member} is not a restorable dictionary.");
            dictionaryTargets.Add(dictionary);
        }
    }

    private static bool TryCreateStaticInstanceTarget(Type type, out DiplomacyValueBackupTarget target)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var property = current.GetProperty(
                "Instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var getter = property?.GetGetMethod(nonPublic: true);
            var setter = property?.GetSetMethod(nonPublic: true);
            if (getter != null && setter != null)
            {
                target = new DiplomacyValueBackupTarget(
                    () => property.GetValue(null),
                    value => property.SetValue(null, value));
                return true;
            }

            var field = current.GetField(
                "Instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null && !field.IsInitOnly)
            {
                target = new DiplomacyValueBackupTarget(
                    () => field.GetValue(null),
                    value => field.SetValue(null, value));
                return true;
            }
        }

        target = default;
        return false;
    }

    internal static string FingerprintSettings(IEnumerable<DiplomacySettingEntry> entries) =>
        Fingerprint(entries
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.TypeName, StringComparer.Ordinal)
            .Select(entry => $"{entry.Name}\u001f{entry.TypeName}\u001f{entry.Value}"));

    internal static string FingerprintState(IEnumerable<DiplomacyStateEntry> entries) =>
        Fingerprint(entries
            .OrderBy(entry => entry.Section, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .ThenBy(entry => entry.Faction1Id, StringComparer.Ordinal)
            .ThenBy(entry => entry.Faction2Id, StringComparer.Ordinal)
            .Select(entry => string.Join("\u001f",
                entry.Section,
                entry.Key,
                entry.Faction1Id,
                entry.Faction2Id,
                entry.Ticks1.ToString(CultureInfo.InvariantCulture),
                entry.Ticks2.ToString(CultureInfo.InvariantCulture),
                entry.Value1.ToString("R", CultureInfo.InvariantCulture),
                entry.Value2.ToString("R", CultureInfo.InvariantCulture),
                entry.Flags1.ToString(CultureInfo.InvariantCulture),
                entry.Flags2.ToString(CultureInfo.InvariantCulture))));

    private static DiplomacySnapshotApplyResult ValidateApplicationShape(NetworkDiplomacySnapshot snapshot)
    {
        var settingsType = DiplomacyCompatibilityPolicy.ResolveType(DiplomacyCompatibilityPolicy.SettingsTypeName);
        var settings = GetStaticInstance(settingsType);
        if (settings == null)
        {
            return new DiplomacySnapshotApplyResult(
                DiplomacySnapshotApplyStatus.SettingsMismatch,
                "Diplomacy Settings.Instance was unavailable before snapshot application.");
        }

        var properties = GetSettingProperties(settingsType);
        if (snapshot.Settings.Count != properties.Length)
        {
            return new DiplomacySnapshotApplyResult(
                DiplomacySnapshotApplyStatus.SettingsMismatch,
                "Host snapshot does not contain the complete Diplomacy 1.4.7 setting shape.");
        }

        var entriesByName = snapshot.Settings.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (!entriesByName.TryGetValue(property.Name, out var entry) ||
                !string.Equals(
                    property.PropertyType.FullName ?? property.PropertyType.Name,
                    entry.TypeName,
                    StringComparison.Ordinal))
            {
                return new DiplomacySnapshotApplyResult(
                    DiplomacySnapshotApplyStatus.SettingsMismatch,
                    $"Host setting shape does not match Diplomacy 1.4.7 at {property.Name}.");
            }

            try
            {
                ParseScalar(entry.Value, property.PropertyType);
            }
            catch (Exception ex)
            {
                return new DiplomacySnapshotApplyResult(
                    DiplomacySnapshotApplyStatus.MalformedSnapshot,
                    $"Host setting {property.Name} could not be parsed: {ex.Message}");
            }
        }

        var agreementType = DiplomacyCompatibilityPolicy.ResolveType(
            "Diplomacy.DiplomaticAction.NonAggressionPactAgreement");
        var agreementManagerType = DiplomacyCompatibilityPolicy.ResolveType(
            "Diplomacy.DiplomaticAction.DiplomaticAgreementManager");
        var registerAgreement = agreementManagerType?.GetMethod(
            "RegisterAgreement",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var agreementConstructor = agreementType?.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.GetParameters().Length == 4);

        var recordType = DiplomacyCompatibilityPolicy.ResolveType("Diplomacy.WarExhaustion.WarExhaustionRecord");
        var victoryType = recordType?.GetNestedType("VictoriousFactionType", BindingFlags.Public | BindingFlags.NonPublic);
        var questType = recordType?.GetNestedType("ActiveQuestState", BindingFlags.Public | BindingFlags.NonPublic);
        var recordConstructor = recordType?.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 5 &&
                       parameters[2].ParameterType == victoryType &&
                       parameters[3].ParameterType == questType;
            });
        string[] managerTypes =
        {
            "Diplomacy.ExpansionismManager",
            "Diplomacy.CooldownManager",
            "Diplomacy.DiplomaticAction.DiplomaticAgreementManager",
            "Diplomacy.WarExhaustion.WarExhaustionManager",
            "Diplomacy.CivilWar.RebelFactionManager",
        };
        if (managerTypes.Any(managerTypeName => !CanEnsureManager(managerTypeName)) ||
            agreementConstructor == null || registerAgreement == null ||
            victoryType == null || questType == null || recordConstructor == null)
        {
            return new DiplomacySnapshotApplyResult(
                DiplomacySnapshotApplyStatus.VersionMismatch,
                "Diplomacy 1.4.7 manager constructor/method shape failed preflight.");
        }

        foreach (var entry in snapshot.State.Where(entry => entry.Key != MarkerKey))
        {
            switch (entry.Section)
            {
                case ExpansionismSection:
                    if (TryResolveFaction(entry.Faction1Id, out _)) break;
                    return StateReferenceMismatch(entry.Faction1Id);
                case PeaceProposalSection:
                    if (TryResolveKingdom(entry.Faction1Id, out _)) break;
                    return StateReferenceMismatch(entry.Faction1Id);
                case AllianceCooldownSection:
                case WarCooldownSection:
                    if (TryValidateFactionPairKey(entry.Key)) break;
                    return StateReferenceMismatch(entry.Key);
                case AgreementSection:
                    if (TryResolveKingdom(entry.Faction1Id, out _) &&
                        TryResolveKingdom(entry.Faction2Id, out _)) break;
                    return StateReferenceMismatch($"{entry.Faction1Id}+{entry.Faction2Id}");
                case WarScoreSection:
                case WarRateSection:
                    if (!TryResolveKingdom(entry.Faction1Id, out _) ||
                        !TryResolveKingdom(entry.Faction2Id, out _))
                    {
                        return StateReferenceMismatch($"{entry.Faction1Id}+{entry.Faction2Id}");
                    }
                    if (!Enum.IsDefined(victoryType, entry.Flags1) || !Enum.IsDefined(questType, entry.Flags2))
                    {
                        return new DiplomacySnapshotApplyResult(
                            DiplomacySnapshotApplyStatus.MalformedSnapshot,
                            $"War-exhaustion enum values are invalid for {entry.Faction1Id}+{entry.Faction2Id}.");
                    }
                    break;
            }
        }

        return new DiplomacySnapshotApplyResult(DiplomacySnapshotApplyStatus.Applied);
    }

    private static readonly (string Manager, string Member, string KeyType, string ValueType, bool ValueIsList)[]
        RequiredManagerDictionaries =
        {
            ("Diplomacy.ExpansionismManager", "_expansionism", "TaleWorlds.CampaignSystem.IFaction", "System.Single", false),
            ("Diplomacy.CooldownManager", "_lastPeaceProposalTime", "TaleWorlds.CampaignSystem.Kingdom", "TaleWorlds.CampaignSystem.CampaignTime", false),
            ("Diplomacy.CooldownManager", "_lastAllianceFormedTime", "System.String", "TaleWorlds.CampaignSystem.CampaignTime", false),
            ("Diplomacy.CooldownManager", "_lastWarTime", "System.String", "TaleWorlds.CampaignSystem.CampaignTime", false),
            ("Diplomacy.DiplomaticAction.DiplomaticAgreementManager", "Agreements", DiplomacyManagerCaptureBarrier.AgreementKeyTypeName, "Diplomacy.DiplomaticAction.DiplomaticAgreement", true),
            ("Diplomacy.WarExhaustion.WarExhaustionManager", "_warExhaustionScores", "System.String", "Diplomacy.WarExhaustion.WarExhaustionRecord", false),
            ("Diplomacy.WarExhaustion.WarExhaustionManager", "_warExhaustionRates", "System.String", "Diplomacy.WarExhaustion.WarExhaustionRecord", false),
            ("Diplomacy.WarExhaustion.WarExhaustionManager", "_warExhaustionEventRecords", "System.String", "Diplomacy.WarExhaustion.EventRecords.WarExhaustionEventRecord", true),
            // Never synced (Separatism owns rebellions; content stays empty), but existence-checked
            // here so Kingdom-UI readiness fails toward the snapshot repair path — which ensures the
            // singleton — instead of letting the pinned cost calculator NRE on a null Instance.
            ("Diplomacy.CivilWar.RebelFactionManager", "RebelFactions", "TaleWorlds.CampaignSystem.Kingdom", "Diplomacy.CivilWar.Factions.RebelFaction", true),
            ("Diplomacy.CivilWar.RebelFactionManager", "LastCivilWar", "TaleWorlds.CampaignSystem.Kingdom", "TaleWorlds.CampaignSystem.CampaignTime", false),
        };

    private static bool TryValidateRequiredManagerShape(out string failure)
    {
        foreach (var spec in RequiredManagerDictionaries)
        {
            var managerType = DiplomacyCompatibilityPolicy.ResolveType(spec.Manager);
            var manager = GetStaticInstance(managerType);
            if (manager == null)
            {
                failure = $"Diplomacy manager {spec.Manager}.Instance is unavailable.";
                return false;
            }
            if (GetMemberValue(manager, spec.Member) is not IDictionary dictionary ||
                !HasDictionaryShape(dictionary, spec.KeyType, spec.ValueType, spec.ValueIsList))
            {
                failure = $"Diplomacy manager member {spec.Manager}.{spec.Member} has an unexpected dictionary shape.";
                return false;
            }
        }

        failure = null;
        return true;
    }

    private static bool HasDictionaryShape(
        IDictionary dictionary,
        string expectedKeyType,
        string expectedValueType,
        bool valueIsList)
    {
        var dictionaryInterface = dictionary.GetType()
            .GetInterfaces()
            .Concat(new[] { dictionary.GetType() })
            .FirstOrDefault(candidate => candidate.IsGenericType &&
                                         candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        if (dictionaryInterface == null) return false;
        var arguments = dictionaryInterface.GetGenericArguments();
        if (!string.Equals(arguments[0].FullName, expectedKeyType, StringComparison.Ordinal)) return false;

        var valueType = arguments[1];
        if (!valueIsList)
            return string.Equals(valueType.FullName, expectedValueType, StringComparison.Ordinal);
        return valueType.IsGenericType &&
               valueType.GetGenericTypeDefinition() == typeof(List<>) &&
               string.Equals(valueType.GetGenericArguments()[0].FullName, expectedValueType, StringComparison.Ordinal);
    }

    private static IDictionary GetRequiredManagerDictionary(string managerTypeName, string memberName)
    {
        var spec = RequiredManagerDictionaries.SingleOrDefault(candidate =>
            candidate.Manager == managerTypeName && candidate.Member == memberName);
        var managerType = DiplomacyCompatibilityPolicy.ResolveType(managerTypeName);
        var manager = GetStaticInstance(managerType);
        if (manager == null || GetMemberValue(manager, memberName) is not IDictionary dictionary ||
            string.IsNullOrEmpty(spec.Manager) ||
            !HasDictionaryShape(dictionary, spec.KeyType, spec.ValueType, spec.ValueIsList))
        {
            throw new InvalidOperationException(
                $"Required Diplomacy manager dictionary {managerTypeName}.{memberName} is unavailable or incompatible.");
        }
        return dictionary;
    }

    private static bool CanEnsureManager(string managerTypeName)
    {
        var type = DiplomacyCompatibilityPolicy.ResolveType(managerTypeName);
        if (type == null) return false;
        if (GetStaticInstance(type) != null) return true;
        return type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(constructor => constructor.GetParameters().Length == 0);
    }

    private static DiplomacySnapshotApplyResult StateReferenceMismatch(string reference) =>
        new(
            DiplomacySnapshotApplyStatus.StateMismatch,
            $"Host Diplomacy state references unknown or non-canonical campaign ID pair {reference}.");

    private static bool TryValidateFactionPairKey(string key)
    {
        var ids = key?.Split('+');
        if (ids == null || ids.Length != 2 ||
            string.IsNullOrWhiteSpace(ids[0]) || string.IsNullOrWhiteSpace(ids[1]) ||
            string.CompareOrdinal(ids[0], ids[1]) >= 0 ||
            !TryResolveFaction(ids[0], out _) || !TryResolveFaction(ids[1], out _))
        {
            return false;
        }
        return true;
    }

    private static PropertyInfo[] GetSettingProperties(Type settingsType) =>
        settingsType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => property.DeclaringType == settingsType &&
                               property.GetIndexParameters().Length == 0 &&
                               property.GetGetMethod(nonPublic: true) != null &&
                               property.GetSetMethod(nonPublic: true) != null &&
                               IsScalar(property.PropertyType))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();

    private static void NormalizeServerUnsafeSettings(Type settingsType, object settings)
    {
        if (!ModInformation.IsServer) return;
        var property = settingsType.GetProperty(
            "EnableWarExhaustionDebugMessages",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.PropertyType != typeof(bool) || property.GetValue(settings) is not bool enabled || !enabled)
            return;

        property.GetSetMethod(nonPublic: true)?.Invoke(settings, new object[] { false });
        if (!loggedDebugSettingNormalization)
        {
            loggedDebugSettingNormalization = true;
            Logger.Warning(
                "Disabled Diplomacy war-exhaustion debug messages on the server because they dereference Hero.MainHero.");
        }
    }

    private static void CaptureSettings(ICollection<DiplomacySettingEntry> output)
    {
        var settingsType = DiplomacyCompatibilityPolicy.ResolveType(DiplomacyCompatibilityPolicy.SettingsTypeName);
        var settings = GetStaticInstance(settingsType);
        if (settings == null) return;

        foreach (var property in GetSettingProperties(settingsType))
        {
            var value = property.GetValue(settings);
            output.Add(new DiplomacySettingEntry
            {
                Name = property.Name,
                TypeName = property.PropertyType.FullName ?? property.PropertyType.Name,
                Value = FormatScalar(value, property.PropertyType),
            });
        }
    }

    private static void ApplySettings(IEnumerable<DiplomacySettingEntry> entries)
    {
        var settingsType = DiplomacyCompatibilityPolicy.ResolveType(DiplomacyCompatibilityPolicy.SettingsTypeName);
        var settings = GetStaticInstance(settingsType);
        if (settings == null) throw new InvalidOperationException("Diplomacy Settings.Instance was unavailable.");

        foreach (var entry in entries ?? Enumerable.Empty<DiplomacySettingEntry>())
        {
            var property = settingsType.GetProperty(
                entry.Name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var setter = property?.GetSetMethod(nonPublic: true);
            if (property == null || setter == null || !IsScalar(property.PropertyType))
                throw new InvalidOperationException($"Diplomacy setting {entry.Name} is not writable.");
            if (!string.Equals(property.PropertyType.FullName ?? property.PropertyType.Name, entry.TypeName, StringComparison.Ordinal))
                throw new InvalidOperationException($"Diplomacy setting {entry.Name} has a different type on the client.");

            setter.Invoke(settings, new[] { ParseScalar(entry.Value, property.PropertyType) });
        }
    }

    private static void CaptureExpansionism(ICollection<DiplomacyStateEntry> output)
    {
        var dictionary = GetRequiredManagerDictionary("Diplomacy.ExpansionismManager", "_expansionism");
        AddMarker(output, ExpansionismSection);

        foreach (DictionaryEntry pair in dictionary)
        {
            if (pair.Key is not IFaction faction)
                throw new InvalidOperationException("Diplomacy expansionism state has a non-faction key.");
            output.Add(new DiplomacyStateEntry
            {
                Section = ExpansionismSection,
                Faction1Id = faction.StringId ?? string.Empty,
                Value1 = Convert.ToSingle(pair.Value, CultureInfo.InvariantCulture),
            });
        }
    }

    private static void ApplyExpansionism(IEnumerable<DiplomacyStateEntry> state)
    {
        var dictionary = GetRequiredManagerDictionary("Diplomacy.ExpansionismManager", "_expansionism");
        dictionary.Clear();
        foreach (var entry in Section(state, ExpansionismSection))
        {
            if (!TryResolveFaction(entry.Faction1Id, out var faction))
                throw new InvalidOperationException($"Diplomacy faction {entry.Faction1Id} disappeared during apply.");
            dictionary[faction] = entry.Value1;
        }
    }

    private static void CaptureCooldowns(ICollection<DiplomacyStateEntry> output)
    {
        CaptureCooldownDictionary(output, "_lastPeaceProposalTime", PeaceProposalSection, factionKey: true);
        CaptureCooldownDictionary(output, "_lastAllianceFormedTime", AllianceCooldownSection, factionKey: false);
        CaptureCooldownDictionary(output, "_lastWarTime", WarCooldownSection, factionKey: false);
    }

    private static void CaptureCooldownDictionary(
        ICollection<DiplomacyStateEntry> output,
        string fieldName,
        string section,
        bool factionKey)
    {
        var dictionary = GetRequiredManagerDictionary("Diplomacy.CooldownManager", fieldName);
        AddMarker(output, section);

        foreach (DictionaryEntry pair in dictionary)
        {
            if (pair.Value is not CampaignTime time)
                throw new InvalidOperationException($"Diplomacy cooldown {fieldName} has a non-CampaignTime value.");
            if (factionKey && pair.Key is not Kingdom)
                throw new InvalidOperationException($"Diplomacy cooldown {fieldName} has a non-kingdom key.");
            if (!factionKey && pair.Key is not string)
                throw new InvalidOperationException($"Diplomacy cooldown {fieldName} has a non-string key.");

            string factionId = factionKey ? ((Kingdom)pair.Key).StringId : string.Empty;
            string key = factionKey ? string.Empty : (string)pair.Key;
            output.Add(new DiplomacyStateEntry
            {
                Section = section,
                Key = key,
                Faction1Id = factionId ?? string.Empty,
                Ticks1 = time.NumTicks,
            });
        }
    }

    private static void ApplyCooldowns(IEnumerable<DiplomacyStateEntry> state)
    {
        ApplyCooldownDictionary(state, "_lastPeaceProposalTime", PeaceProposalSection, factionKey: true);
        ApplyCooldownDictionary(state, "_lastAllianceFormedTime", AllianceCooldownSection, factionKey: false);
        ApplyCooldownDictionary(state, "_lastWarTime", WarCooldownSection, factionKey: false);
    }

    private static void ApplyCooldownDictionary(
        IEnumerable<DiplomacyStateEntry> state,
        string fieldName,
        string section,
        bool factionKey)
    {
        var dictionary = GetRequiredManagerDictionary("Diplomacy.CooldownManager", fieldName);
        dictionary.Clear();

        foreach (var entry in Section(state, section))
        {
            object key = entry.Key;
            if (factionKey)
            {
                if (!TryResolveKingdom(entry.Faction1Id, out var kingdom))
                    throw new InvalidOperationException($"Diplomacy kingdom {entry.Faction1Id} disappeared during apply.");
                key = kingdom;
            }
            dictionary[key] = new CampaignTime(entry.Ticks1);
        }
    }

    private static void CaptureAgreements(ICollection<DiplomacyStateEntry> output)
    {
        var dictionary = GetRequiredManagerDictionary(
            "Diplomacy.DiplomaticAction.DiplomaticAgreementManager",
            "Agreements");
        AddMarker(output, AgreementSection);

        foreach (DictionaryEntry pair in dictionary)
        {
            if (pair.Value is not IEnumerable agreements)
                throw new InvalidOperationException("Diplomacy agreement state has a non-list value.");
            foreach (var agreement in agreements)
            {
                if (agreement == null ||
                    !string.Equals(
                        agreement.GetType().FullName,
                        "Diplomacy.DiplomaticAction.NonAggressionPactAgreement",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Diplomacy agreement state contains an unsupported or null agreement record.");
                }
                var factions = GetMemberValue(agreement, "Factions");
                var faction1 = GetMemberValue(factions, "Faction1") as IFaction;
                var faction2 = GetMemberValue(factions, "Faction2") as IFaction;
                var start = GetMemberValue(agreement, "StartDate");
                var end = GetMemberValue(agreement, "EndDate");
                if (faction1 == null || faction2 == null || !(start is CampaignTime startTime) || !(end is CampaignTime endTime))
                {
                    throw new InvalidOperationException(
                        "Diplomacy non-aggression pact has an incomplete faction/time shape.");
                }
                if (endTime.IsPast) continue;

                output.Add(new DiplomacyStateEntry
                {
                    Section = AgreementSection,
                    Faction1Id = faction1.StringId ?? string.Empty,
                    Faction2Id = faction2.StringId ?? string.Empty,
                    Ticks1 = startTime.NumTicks,
                    Ticks2 = endTime.NumTicks,
                });
            }
        }
    }

    private static void ApplyAgreements(IEnumerable<DiplomacyStateEntry> state)
    {
        var managerType = DiplomacyCompatibilityPolicy.ResolveType("Diplomacy.DiplomaticAction.DiplomaticAgreementManager");
        var dictionary = GetRequiredManagerDictionary(
            "Diplomacy.DiplomaticAction.DiplomaticAgreementManager",
            "Agreements");
        dictionary.Clear();

        var agreementType = DiplomacyCompatibilityPolicy.ResolveType(
            "Diplomacy.DiplomaticAction.NonAggressionPactAgreement");
        var registerMethod = managerType?.GetMethod(
            "RegisterAgreement",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (agreementType == null || registerMethod == null)
            throw new InvalidOperationException("Diplomacy agreement constructor path is unavailable.");

        foreach (var entry in Section(state, AgreementSection))
        {
            if (!TryResolveKingdom(entry.Faction1Id, out var first) ||
                !TryResolveKingdom(entry.Faction2Id, out var second))
            {
                throw new InvalidOperationException(
                    $"Diplomacy agreement factions {entry.Faction1Id}+{entry.Faction2Id} disappeared during apply.");
            }

            var agreement = Activator.CreateInstance(
                agreementType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { new CampaignTime(entry.Ticks1), new CampaignTime(entry.Ticks2), first, second },
                culture: CultureInfo.InvariantCulture);
            registerMethod.Invoke(null, new[] { first, second, agreement });
        }
    }

    private static void CaptureWarExhaustion(ICollection<DiplomacyStateEntry> output)
    {
        CaptureWarRecordDictionary(output, "_warExhaustionScores", WarScoreSection);
        CaptureWarRecordDictionary(output, "_warExhaustionRates", WarRateSection);

        // Detailed event records reference several custom record subclasses and native objects.
        // They are deliberately not copied; clearing them makes Diplomacy's UI use its scalar
        // fallback calculation instead of presenting a stale local breakdown.
        GetRequiredManagerDictionary(
            "Diplomacy.WarExhaustion.WarExhaustionManager",
            "_warExhaustionEventRecords");
        AddMarker(output, WarEventsSection);
    }

    private static void CaptureWarRecordDictionary(
        ICollection<DiplomacyStateEntry> output,
        string fieldName,
        string section)
    {
        var dictionary = GetRequiredManagerDictionary("Diplomacy.WarExhaustion.WarExhaustionManager", fieldName);
        AddMarker(output, section);

        foreach (DictionaryEntry pair in dictionary)
        {
            var record = pair.Value;
            if (record == null)
                throw new InvalidOperationException($"Diplomacy war-exhaustion {fieldName} has a null record.");
            if (!TryResolveWarManagerKey(pair.Key?.ToString(), out var first, out var second))
            {
                throw new InvalidOperationException(
                    $"Diplomacy war-exhaustion key {pair.Key ?? "<null>"} did not resolve to two kingdoms.");
            }
            var faction1Value = GetMemberValue(record, "Faction1Value") ??
                                throw new InvalidOperationException(
                                    $"Diplomacy war-exhaustion {fieldName} record is missing Faction1Value.");
            var faction2Value = GetMemberValue(record, "Faction2Value") ??
                                throw new InvalidOperationException(
                                    $"Diplomacy war-exhaustion {fieldName} record is missing Faction2Value.");
            var victoriousFaction = GetMemberValue(record, "VictoriousFaction") ??
                                    throw new InvalidOperationException(
                                        $"Diplomacy war-exhaustion {fieldName} record is missing VictoriousFaction.");
            var questState = GetMemberValue(record, "QuestState") ??
                             throw new InvalidOperationException(
                                 $"Diplomacy war-exhaustion {fieldName} record is missing QuestState.");
            output.Add(new DiplomacyStateEntry
            {
                Section = section,
                Faction1Id = first.StringId ?? string.Empty,
                Faction2Id = second.StringId ?? string.Empty,
                Value1 = Convert.ToSingle(faction1Value, CultureInfo.InvariantCulture),
                Value2 = Convert.ToSingle(faction2Value, CultureInfo.InvariantCulture),
                Flags1 = Convert.ToInt32(victoriousFaction, CultureInfo.InvariantCulture),
                Flags2 = Convert.ToInt32(questState, CultureInfo.InvariantCulture),
            });
        }
    }

    private static void ApplyWarExhaustion(IEnumerable<DiplomacyStateEntry> state)
    {
        ApplyWarRecordDictionary(state, "_warExhaustionScores", WarScoreSection);
        ApplyWarRecordDictionary(state, "_warExhaustionRates", WarRateSection);
        GetRequiredManagerDictionary(
            "Diplomacy.WarExhaustion.WarExhaustionManager",
            "_warExhaustionEventRecords").Clear();
    }

    private static void ApplyWarRecordDictionary(
        IEnumerable<DiplomacyStateEntry> state,
        string fieldName,
        string section)
    {
        var dictionary = GetRequiredManagerDictionary("Diplomacy.WarExhaustion.WarExhaustionManager", fieldName);
        dictionary.Clear();

        var recordType = DiplomacyCompatibilityPolicy.ResolveType("Diplomacy.WarExhaustion.WarExhaustionRecord");
        var victoryType = recordType?.GetNestedType("VictoriousFactionType", BindingFlags.Public | BindingFlags.NonPublic);
        var questType = recordType?.GetNestedType("ActiveQuestState", BindingFlags.Public | BindingFlags.NonPublic);
        var constructor = recordType?.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 5 && parameters[2].ParameterType == victoryType && parameters[3].ParameterType == questType;
            });
        if (recordType == null || victoryType == null || questType == null || constructor == null)
            throw new InvalidOperationException("Diplomacy war-exhaustion record constructor is unavailable.");

        foreach (var entry in Section(state, section))
        {
            if (!TryResolveKingdom(entry.Faction1Id, out var first) ||
                !TryResolveKingdom(entry.Faction2Id, out var second))
            {
                throw new InvalidOperationException(
                    $"War-exhaustion kingdoms {entry.Faction1Id}+{entry.Faction2Id} no longer exist.");
            }

            bool reverseValues = ((MBObjectBase)first).Id > ((MBObjectBase)second).Id;
            string key = reverseValues
                ? string.Join("+", ((MBObjectBase)second).Id, ((MBObjectBase)first).Id)
                : string.Join("+", ((MBObjectBase)first).Id, ((MBObjectBase)second).Id);
            int victoriousFaction = reverseValues ? SwapFactionFlags(entry.Flags1) : entry.Flags1;
            var record = constructor.Invoke(new[]
            {
                (object)(reverseValues ? entry.Value2 : entry.Value1),
                reverseValues ? entry.Value1 : entry.Value2,
                Enum.ToObject(victoryType, victoriousFaction),
                Enum.ToObject(questType, entry.Flags2),
                false,
            });
            dictionary[key] = record;
        }
    }

    private static int SwapFactionFlags(int flags) =>
        (flags & ~3) | ((flags & 1) << 1) | ((flags & 2) >> 1);

    private static bool TryResolveWarManagerKey(string key, out Kingdom first, out Kingdom second)
    {
        first = null;
        second = null;
        if (!TrySplitWarManagerKey(key, out string firstId, out string secondId)) return false;

        first = Kingdom.All?.FirstOrDefault(candidate =>
            candidate != null && string.Equals(((MBObjectBase)candidate).Id.ToString(), firstId, StringComparison.Ordinal));
        second = Kingdom.All?.FirstOrDefault(candidate =>
            candidate != null && string.Equals(((MBObjectBase)candidate).Id.ToString(), secondId, StringComparison.Ordinal));
        return first != null && second != null && first != second;
    }

    internal static bool TrySplitWarManagerKey(string key, out string firstId, out string secondId)
    {
        firstId = null;
        secondId = null;
        var ids = key?.Split('+');
        if (ids == null || ids.Length != 2 ||
            !ulong.TryParse(ids[0], NumberStyles.None, CultureInfo.InvariantCulture, out ulong first) ||
            !ulong.TryParse(ids[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong second) ||
            first >= second)
        {
            return false;
        }

        firstId = ids[0];
        secondId = ids[1];
        return true;
    }

    /// <summary>
    /// Idempotently create a Diplomacy manager singleton by type name (no-op if already present or if
    /// Diplomacy is absent). Called from three places that must stay in lockstep: the snapshot apply
    /// (client, authoritative repopulation), <see cref="DiplomacyManagerCaptureBarrier"/> (host, before
    /// capture), and <see cref="DiplomacyClientInitializationPatch"/> (client, at map build — closing
    /// the map-build → snapshot window for every ungated reader, e.g. the campaign-map war-exhaustion
    /// widget that Diplomacy's UIBehavior installs on the first campaign tick).
    /// </summary>
    internal static void EnsureManager(string managerTypeName)
    {
        var type = DiplomacyCompatibilityPolicy.ResolveType(managerTypeName);
        if (type == null || GetStaticInstance(type) != null) return;
        Activator.CreateInstance(type, nonPublic: true);
    }

    private static object GetStaticInstance(Type type)
    {
        if (type == null) return null;
        for (var current = type; current != null; current = current.BaseType)
        {
            var property = current.GetProperty(
                "Instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property?.GetGetMethod(nonPublic: true) != null) return property.GetValue(null);

            var field = current.GetField(
                "Instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(null);
        }
        return null;
    }

    private static object GetMemberValue(object instance, string name)
    {
        if (instance == null) return null;
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property?.GetGetMethod(nonPublic: true) != null) return property.GetValue(instance);

            var field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(instance);
        }
        return null;
    }

    private static bool TryResolveFaction(string id, out IFaction faction)
    {
        if (TryResolveKingdom(id, out var kingdom))
        {
            faction = kingdom;
            return true;
        }

        var clan = Clan.All?.FirstOrDefault(candidate => candidate?.StringId == id);
        faction = clan;
        return clan != null;
    }

    private static bool TryResolveKingdom(string id, out Kingdom kingdom)
    {
        kingdom = Kingdom.All?.FirstOrDefault(candidate => candidate?.StringId == id);
        return kingdom != null;
    }

    private static IEnumerable<DiplomacyStateEntry> Section(IEnumerable<DiplomacyStateEntry> state, string section) =>
        (state ?? Enumerable.Empty<DiplomacyStateEntry>())
        .Where(entry => entry.Section == section && entry.Key != MarkerKey);

    private static void AddMarker(ICollection<DiplomacyStateEntry> output, string section) =>
        output.Add(new DiplomacyStateEntry { Section = section, Key = MarkerKey });

    private static void SortSnapshot(NetworkDiplomacySnapshot snapshot)
    {
        snapshot.Settings = snapshot.Settings
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.TypeName, StringComparer.Ordinal)
            .ToList();
        snapshot.State = snapshot.State
            .OrderBy(entry => entry.Section, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .ThenBy(entry => entry.Faction1Id, StringComparer.Ordinal)
            .ThenBy(entry => entry.Faction2Id, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum || type == typeof(string) || type == typeof(bool) || type == typeof(byte) ||
               type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
               type == typeof(int) || type == typeof(uint) || type == typeof(long) ||
               type == typeof(ulong) || type == typeof(float) || type == typeof(double) ||
               type == typeof(decimal);
    }

    private static string FormatScalar(object value, Type type)
    {
        if (value == null) return string.Empty;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum) return Enum.GetName(type, value) ?? Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        if (type == typeof(bool)) return ((bool)value) ? "true" : "false";
        if (type == typeof(float)) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
        if (type == typeof(double)) return ((double)value).ToString("R", CultureInfo.InvariantCulture);
        if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
        return value.ToString() ?? string.Empty;
    }

    private static object ParseScalar(string value, Type type)
    {
        var nullableType = Nullable.GetUnderlyingType(type);
        var targetType = nullableType ?? type;
        if (nullableType != null && string.IsNullOrEmpty(value)) return null;
        object parsed;
        if (targetType.IsEnum)
        {
            parsed = Enum.Parse(targetType, value, ignoreCase: false);
            if (!Enum.IsDefined(targetType, parsed))
                throw new FormatException($"{value} is not defined by {targetType.Name}.");
            return parsed;
        }
        if (targetType == typeof(string)) return value;
        if (targetType == typeof(bool)) return bool.Parse(value);

        parsed = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        if (parsed is float single && (float.IsNaN(single) || float.IsInfinity(single)) ||
            parsed is double doubleValue && (double.IsNaN(doubleValue) || double.IsInfinity(doubleValue)))
        {
            throw new FormatException("Non-finite numeric settings are not accepted.");
        }
        return parsed;
    }

    private static string Fingerprint(IEnumerable<string> lines)
    {
        var text = string.Join("\n", lines);
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}

/// <summary>
/// Establishes Diplomacy's manager singletons before an authoritative capture. Fresh campaigns
/// and saves first hosted with the dedicated runtime have no serialized manager instance yet;
/// every pinned 1.4.7 manager owns an idempotent parameterless constructor for that exact case.
/// Validation runs only after all managers have had a chance to initialize their dictionaries.
/// </summary>
internal static class DiplomacyManagerCaptureBarrier
{
    internal const string AgreementKeyTypeName = "Diplomacy.FactionPair";

    internal static readonly string[] RequiredManagerTypeNames =
    {
        "Diplomacy.ExpansionismManager",
        "Diplomacy.CooldownManager",
        "Diplomacy.DiplomaticAction.DiplomaticAgreementManager",
        "Diplomacy.WarExhaustion.WarExhaustionManager",
        // Existence-only: never captured into the snapshot (Separatism owns rebellions, so the
        // civil-war ledger is canonically empty), but the pinned peace/reparations cost math reads
        // RebelFactionManager.AllRebelFactions on both roles, and the retired Diplomacy behaviours
        // mean nothing else ever constructs the singleton on the dedicated host.
        "Diplomacy.CivilWar.RebelFactionManager",
    };

    internal static void RequireReady(
        Action<string> ensureManager,
        Func<string> validateFailure)
    {
        if (ensureManager == null) throw new ArgumentNullException(nameof(ensureManager));
        if (validateFailure == null) throw new ArgumentNullException(nameof(validateFailure));

        foreach (var managerTypeName in RequiredManagerTypeNames)
            ensureManager(managerTypeName);

        var failure = validateFailure();
        if (!string.IsNullOrEmpty(failure))
        {
            throw new InvalidOperationException(
                "Diplomacy manager initialization failed before authoritative capture: " + failure);
        }
    }
}
