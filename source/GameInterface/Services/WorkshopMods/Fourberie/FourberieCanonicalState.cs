using GameInterface.Services.ObjectManager;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Roster;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal sealed class FourberieStateFieldSpec
{
    public FourberieStateFieldSpec(string fieldName, FourberieStateValueKind kind, string expectedTypeName)
    {
        FieldName = fieldName;
        Kind = kind;
        ExpectedTypeName = expectedTypeName;
    }

    public string FieldName { get; }
    public FourberieStateValueKind Kind { get; }
    public string ExpectedTypeName { get; }
}

/// <summary>
/// Reversible assignment of Fourberie's static persisted fields. The revision watermark is
/// advanced only while this transaction is still rollback-capable; a failed watermark commit
/// therefore restores every previous field value instead of leaving state and revision split.
/// </summary>
internal sealed class FourberieStateApplyTransaction
{
    private Dictionary<FieldInfo, object> previous;

    internal FourberieStateApplyTransaction(Dictionary<FieldInfo, object> previous) =>
        this.previous = previous ?? throw new ArgumentNullException(nameof(previous));

    public void Commit() => previous = null;

    public bool TryRollback(out string failure)
    {
        failure = null;
        if (previous == null) return true;

        var errors = new List<string>();
        foreach (var item in previous)
        {
            try
            {
                item.Key.SetValue(null, item.Value);
            }
            catch (Exception exception)
            {
                errors.Add(item.Key.Name + ":" + exception.GetType().Name + ":" + exception.Message);
            }
        }

        previous = null;
        if (errors.Count == 0) return true;
        failure = "Fourberie state rollback failed for " + string.Join("; ", errors);
        return false;
    }
}

/// <summary>
/// Stable-ID representation of every field FourberieBehavior.SyncData persists in 1.4.7.5.
/// Mission-only/transient fields are deliberately excluded because Fourberie mission entry points
/// are disabled until they can be associated with a validated Coop controller.
/// </summary>
internal static class FourberieCanonicalState
{
    private const string BehaviorTypeName = "Fourberie.FourberieBehavior";

    internal static readonly IReadOnlyList<FourberieStateFieldSpec> Fields = BuildFieldSpecs();

    public static bool TryCapture(
        Assembly assembly,
        IObjectManager objectManager,
        out FourberieStateEntry[] entries,
        out string fingerprint,
        out string failure)
    {
        entries = Array.Empty<FourberieStateEntry>();
        fingerprint = null;
        failure = null;

        if (!TryResolveFields(assembly, out var fields, out failure)) return false;

        try
        {
            var result = new List<FourberieStateEntry>();
            foreach (var spec in Fields)
            {
                var field = fields[spec.FieldName];
                var value = field.GetValue(null);
                if (!TryCaptureField(spec, value, objectManager, result, out failure)) return false;
            }

            if (result.Count > FourberieStateCodec.MaximumEntries)
            {
                failure = $"Fourberie state exceeds {FourberieStateCodec.MaximumEntries} entries";
                return false;
            }

            entries = FourberieStateCodec.Sort(result).ToArray();
            fingerprint = FourberieStateCodec.ComputeHash(entries);
            return true;
        }
        catch (Exception exception)
        {
            failure = $"Fourberie state capture failed: {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    public static bool TryApply(
        Assembly assembly,
        IObjectManager objectManager,
        IReadOnlyCollection<FourberieStateEntry> entries,
        out string failure)
    {
        if (!TryBeginApply(assembly, objectManager, entries, out var transaction, out failure))
            return false;
        transaction.Commit();
        return true;
    }

    internal static bool TryBeginApply(
        Assembly assembly,
        IObjectManager objectManager,
        IReadOnlyCollection<FourberieStateEntry> entries,
        out FourberieStateApplyTransaction transaction,
        out string failure)
    {
        transaction = null;
        failure = null;
        if (!TryResolveFields(assembly, out var fields, out failure)) return false;

        var supplied = entries ?? Array.Empty<FourberieStateEntry>();
        var knownFields = new HashSet<string>(Fields.Select(spec => spec.FieldName), StringComparer.Ordinal);
        if (supplied.Any(entry => entry == null || !knownFields.Contains(entry.Field)))
        {
            failure = "Fourberie state contains an unknown field";
            return false;
        }

        try
        {
            var replacements = new Dictionary<FieldInfo, object>();
            foreach (var spec in Fields)
            {
                var group = supplied
                    .Where(entry => string.Equals(entry.Field, spec.FieldName, StringComparison.Ordinal))
                    .ToArray();
                if (!TryBuildReplacement(spec, fields[spec.FieldName], group, objectManager, out var value, out failure))
                    return false;
                replacements.Add(fields[spec.FieldName], value);
            }

            var previous = replacements.Keys.ToDictionary(field => field, field => field.GetValue(null));
            try
            {
                foreach (var replacement in replacements)
                    replacement.Key.SetValue(null, replacement.Value);
            }
            catch (Exception exception)
            {
                var rollback = new FourberieStateApplyTransaction(previous);
                rollback.TryRollback(out var rollbackFailure);
                failure = "Fourberie state assignment failed: " + exception.GetType().Name + ": " +
                          exception.Message + (rollbackFailure == null ? string.Empty : "; " + rollbackFailure);
                return false;
            }

            transaction = new FourberieStateApplyTransaction(previous);
            return true;
        }
        catch (Exception exception)
        {
            failure = $"Fourberie state apply failed: {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static bool TryCaptureField(
        FourberieStateFieldSpec spec,
        object value,
        IObjectManager objectManager,
        ICollection<FourberieStateEntry> entries,
        out string failure)
    {
        failure = null;
        switch (spec.Kind)
        {
            case FourberieStateValueKind.Boolean:
                entries.Add(new FourberieStateEntry(spec.FieldName, spec.Kind, string.Empty,
                    value is bool boolean && boolean ? "true" : "false"));
                return true;
            case FourberieStateValueKind.ObjectReference:
                if (!TryObjectId(value, objectManager, allowNull: true, out var objectId, out failure)) return false;
                entries.Add(new FourberieStateEntry(spec.FieldName, spec.Kind, string.Empty, objectId));
                return true;
            case FourberieStateValueKind.TroopRosterElement:
                return CaptureTroops(spec, value as IEnumerable, objectManager, entries, out failure);
            case FourberieStateValueKind.StringList:
            case FourberieStateValueKind.ObjectList:
                return CaptureList(spec, value as IEnumerable, objectManager, entries, out failure);
            default:
                return CaptureDictionary(spec, value as IDictionary, objectManager, entries, out failure);
        }
    }

    private static bool CaptureDictionary(
        FourberieStateFieldSpec spec,
        IDictionary dictionary,
        IObjectManager objectManager,
        ICollection<FourberieStateEntry> entries,
        out string failure)
    {
        failure = null;
        if (dictionary == null)
        {
            failure = $"Fourberie field {spec.FieldName} is null";
            return false;
        }

        entries.Add(new FourberieStateEntry(spec.FieldName, spec.Kind, string.Empty, string.Empty));
        var values = new List<(string Key, string Value)>();
        foreach (DictionaryEntry item in dictionary)
        {
            string key;
            string value;
            switch (spec.Kind)
            {
                case FourberieStateValueKind.StringIntDictionary:
                    key = item.Key as string;
                    value = Convert.ToInt32(item.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                    break;
                case FourberieStateValueKind.IntIntDictionary:
                    key = Convert.ToInt32(item.Key, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                    value = Convert.ToInt32(item.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                    break;
                case FourberieStateValueKind.StringStringDictionary:
                    key = item.Key as string;
                    value = item.Value as string;
                    break;
                case FourberieStateValueKind.StringTimeDictionary:
                    key = item.Key as string;
                    value = item.Value is CampaignTime stringTime
                        ? stringTime.NumTicks.ToString(CultureInfo.InvariantCulture)
                        : null;
                    break;
                case FourberieStateValueKind.IntTimeDictionary:
                    key = Convert.ToInt32(item.Key, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                    value = item.Value is CampaignTime intTime
                        ? intTime.NumTicks.ToString(CultureInfo.InvariantCulture)
                        : null;
                    break;
                case FourberieStateValueKind.StringObjectDictionary:
                    key = item.Key as string;
                    if (!TryObjectId(item.Value, objectManager, allowNull: false, out value, out failure)) return false;
                    break;
                default:
                    failure = $"unsupported Fourberie dictionary kind {spec.Kind}";
                    return false;
            }

            if (key == null || value == null)
            {
                failure = $"Fourberie field {spec.FieldName} contains an invalid entry";
                return false;
            }
            values.Add((key, value));
        }

        var ordinal = 1;
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            entries.Add(new FourberieStateEntry(spec.FieldName, spec.Kind, pair.Key, pair.Value, ordinal++));
        return true;
    }

    private static bool CaptureList(
        FourberieStateFieldSpec spec,
        IEnumerable list,
        IObjectManager objectManager,
        ICollection<FourberieStateEntry> entries,
        out string failure)
    {
        failure = null;
        if (list == null)
        {
            failure = $"Fourberie field {spec.FieldName} is null";
            return false;
        }

        entries.Add(new FourberieStateEntry(spec.FieldName, spec.Kind, string.Empty, string.Empty));
        var ordinal = 1;
        foreach (var item in list)
        {
            string value;
            if (spec.Kind == FourberieStateValueKind.StringList)
            {
                value = item as string;
                if (value == null)
                {
                    failure = $"Fourberie field {spec.FieldName} contains a non-string item";
                    return false;
                }
            }
            else if (!TryObjectId(item, objectManager, allowNull: false, out value, out failure))
            {
                return false;
            }

            entries.Add(new FourberieStateEntry(spec.FieldName, spec.Kind, string.Empty, value, ordinal++));
        }
        return true;
    }

    private static bool CaptureTroops(
        FourberieStateFieldSpec spec,
        IEnumerable list,
        IObjectManager objectManager,
        ICollection<FourberieStateEntry> entries,
        out string failure)
    {
        failure = null;
        if (list == null)
        {
            failure = $"Fourberie field {spec.FieldName} is null";
            return false;
        }

        entries.Add(new FourberieStateEntry(spec.FieldName, spec.Kind, string.Empty, string.Empty));
        var ordinal = 1;
        foreach (var item in list)
        {
            if (item is not TroopRosterElement troop || troop.Character == null ||
                !TryObjectId(troop.Character, objectManager, allowNull: false, out var characterId, out failure))
            {
                failure ??= $"Fourberie field {spec.FieldName} contains an invalid troop";
                return false;
            }

            var value = troop.Number.ToString(CultureInfo.InvariantCulture) + "," +
                        troop.WoundedNumber.ToString(CultureInfo.InvariantCulture) + "," +
                        troop.Xp.ToString(CultureInfo.InvariantCulture);
            entries.Add(new FourberieStateEntry(spec.FieldName, spec.Kind, characterId, value, ordinal++));
        }
        return true;
    }

    private static bool TryBuildReplacement(
        FourberieStateFieldSpec spec,
        FieldInfo field,
        IReadOnlyCollection<FourberieStateEntry> entries,
        IObjectManager objectManager,
        out object replacement,
        out string failure)
    {
        replacement = null;
        failure = null;

        if (entries.Count == 0 || entries.Any(entry => entry.Kind != spec.Kind))
        {
            failure = $"Fourberie state is missing or has a wrong kind for {spec.FieldName}";
            return false;
        }

        if (spec.Kind == FourberieStateValueKind.Boolean || spec.Kind == FourberieStateValueKind.ObjectReference)
        {
            if (entries.Count != 1 || entries.Single().Ordinal != 0)
            {
                failure = $"Fourberie scalar field {spec.FieldName} has multiple values";
                return false;
            }

            var scalar = entries.Single();
            if (spec.Kind == FourberieStateValueKind.Boolean)
            {
                if (!bool.TryParse(scalar.Value, out var boolean))
                {
                    failure = $"Fourberie field {spec.FieldName} has an invalid boolean";
                    return false;
                }
                replacement = boolean;
                return true;
            }

            return TryResolveObject(objectManager, field.FieldType, scalar.Value, allowNull: true, out replacement, out failure);
        }

        var headers = entries.Where(entry => entry.Ordinal == 0).ToArray();
        if (headers.Length != 1 || headers[0].Key.Length != 0 || headers[0].Value.Length != 0)
        {
            failure = $"Fourberie collection field {spec.FieldName} has no valid header";
            return false;
        }

        var items = entries.Where(entry => entry.Ordinal > 0).OrderBy(entry => entry.Ordinal).ToArray();
        if (items.Select(entry => entry.Ordinal).Distinct().Count() != items.Length ||
            items.Where((entry, index) => entry.Ordinal != index + 1).Any())
        {
            failure = $"Fourberie field {spec.FieldName} has duplicate or non-contiguous ordinals";
            return false;
        }

        if (spec.Kind == FourberieStateValueKind.TroopRosterElement)
            return TryBuildTroopList(items, objectManager, out replacement, out failure);

        replacement = Activator.CreateInstance(field.FieldType);
        if (replacement is IDictionary dictionary)
            return TryPopulateDictionary(spec, dictionary, items, objectManager, out failure);
        if (replacement is IList list)
            return TryPopulateList(spec, field.FieldType, list, items, objectManager, out failure);

        failure = $"Fourberie field {spec.FieldName} is not a supported collection";
        return false;
    }

    private static bool TryPopulateDictionary(
        FourberieStateFieldSpec spec,
        IDictionary dictionary,
        IEnumerable<FourberieStateEntry> entries,
        IObjectManager objectManager,
        out string failure)
    {
        failure = null;
        var valueType = dictionary.GetType().GetGenericArguments()[1];
        foreach (var entry in entries)
        {
            object key;
            object value;
            switch (spec.Kind)
            {
                case FourberieStateValueKind.StringIntDictionary:
                    key = entry.Key;
                    if (!int.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringInt))
                        return Fail($"invalid integer in {spec.FieldName}", out failure);
                    value = stringInt;
                    break;
                case FourberieStateValueKind.IntIntDictionary:
                    if (!int.TryParse(entry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intKey) ||
                        !int.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                        return Fail($"invalid integer pair in {spec.FieldName}", out failure);
                    key = intKey;
                    value = intValue;
                    break;
                case FourberieStateValueKind.StringStringDictionary:
                    key = entry.Key;
                    value = entry.Value;
                    break;
                case FourberieStateValueKind.StringTimeDictionary:
                    key = entry.Key;
                    if (!long.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringTicks))
                        return Fail($"invalid campaign ticks in {spec.FieldName}", out failure);
                    value = new CampaignTime(stringTicks);
                    break;
                case FourberieStateValueKind.IntTimeDictionary:
                    if (!int.TryParse(entry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeKey) ||
                        !long.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intTicks))
                        return Fail($"invalid campaign time pair in {spec.FieldName}", out failure);
                    key = timeKey;
                    value = new CampaignTime(intTicks);
                    break;
                case FourberieStateValueKind.StringObjectDictionary:
                    key = entry.Key;
                    if (!TryResolveObject(objectManager, valueType, entry.Value, allowNull: false, out value, out failure))
                        return false;
                    break;
                default:
                    return Fail($"unsupported dictionary kind {spec.Kind}", out failure);
            }

            if (dictionary.Contains(key)) return Fail($"duplicate key in {spec.FieldName}", out failure);
            dictionary.Add(key, value);
        }
        return true;
    }

    private static bool TryPopulateList(
        FourberieStateFieldSpec spec,
        Type listType,
        IList list,
        IEnumerable<FourberieStateEntry> entries,
        IObjectManager objectManager,
        out string failure)
    {
        failure = null;
        var elementType = listType.GetGenericArguments()[0];
        foreach (var entry in entries)
        {
            if (spec.Kind == FourberieStateValueKind.StringList)
            {
                list.Add(entry.Value);
                continue;
            }

            if (!TryResolveObject(objectManager, elementType, entry.Value, allowNull: false, out var value, out failure))
                return false;
            list.Add(value);
        }
        return true;
    }

    private static bool TryBuildTroopList(
        IEnumerable<FourberieStateEntry> entries,
        IObjectManager objectManager,
        out object replacement,
        out string failure)
    {
        failure = null;
        var troops = new List<TroopRosterElement>();
        foreach (var entry in entries)
        {
            var values = (entry.Value ?? string.Empty).Split(',');
            if (values.Length != 3 ||
                !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ||
                !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var wounded) ||
                !int.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var xp) ||
                number < 0 || wounded < 0 || wounded > number || xp < 0 ||
                !TryResolveObject(objectManager, typeof(CharacterObject), entry.Key, allowNull: false, out var resolved, out failure))
            {
                replacement = null;
                failure ??= "invalid Fourberie troop roster element";
                return false;
            }

            troops.Add(new TroopRosterElement((CharacterObject)resolved)
            {
                _number = number,
                _woundedNumber = wounded,
                _xp = xp,
            });
        }

        replacement = troops;
        failure = null;
        return true;
    }

    private static bool TryResolveFields(
        Assembly assembly,
        out IReadOnlyDictionary<string, FieldInfo> fields,
        out string failure)
    {
        fields = null;
        failure = null;
        var behavior = assembly?.GetType(BehaviorTypeName, throwOnError: false, ignoreCase: false);
        if (behavior == null)
        {
            failure = $"missing Fourberie behavior type {BehaviorTypeName}";
            return false;
        }

        var result = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        foreach (var spec in Fields)
        {
            var field = behavior.GetField(spec.FieldName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null ||
                !string.Equals(FourberieMethodSpec.CanonicalTypeName(field.FieldType), spec.ExpectedTypeName, StringComparison.Ordinal))
            {
                failure = $"missing or changed Fourberie state field {spec.FieldName}:{spec.ExpectedTypeName}";
                return false;
            }
            result.Add(spec.FieldName, field);
        }

        fields = result;
        return true;
    }

    private static bool TryObjectId(
        object value,
        IObjectManager objectManager,
        bool allowNull,
        out string id,
        out string failure)
    {
        id = string.Empty;
        failure = null;
        if (value == null) return allowNull || Fail("null Fourberie object reference", out failure);
        if (objectManager != null && objectManager.TryGetId(value, out id) && !string.IsNullOrWhiteSpace(id)) return true;
        failure = $"Fourberie object {value.GetType().FullName} has no stable Coop id";
        return false;
    }

    private static bool TryResolveObject(
        IObjectManager objectManager,
        Type expectedType,
        string id,
        bool allowNull,
        out object value,
        out string failure)
    {
        value = null;
        failure = null;
        if (string.IsNullOrEmpty(id))
        {
            if (allowNull) return true;
            failure = $"missing Fourberie {expectedType.Name} id";
            return false;
        }
        if (objectManager == null)
        {
            failure = "Coop object manager is unavailable";
            return false;
        }

        var method = typeof(IObjectManager).GetMethods()
            .Single(candidate => candidate.Name == nameof(IObjectManager.TryGetObject) && candidate.IsGenericMethodDefinition)
            .MakeGenericMethod(expectedType);
        var arguments = new object[] { id, null };
        if (method.Invoke(objectManager, arguments) is true && arguments[1] != null)
        {
            value = arguments[1];
            return true;
        }

        failure = $"unknown Fourberie {expectedType.Name} id {id}";
        return false;
    }

    private static bool Fail(string message, out string failure)
    {
        failure = message;
        return false;
    }

    private static IReadOnlyList<FourberieStateFieldSpec> BuildFieldSpecs()
    {
        var fields = new List<FourberieStateFieldSpec>();
        void Add(string name, FourberieStateValueKind kind, string type) =>
            fields.Add(new FourberieStateFieldSpec(name, kind, type));

        const string StringTime = "System.Collections.Generic.Dictionary`2[System.String,TaleWorlds.CampaignSystem.CampaignTime]";
        foreach (var name in new[]
                 {
                     "_townScamTiming", "_townTributeTiming", "_townExtoTiming", "_townRobTiming",
                     "_townInsuScamTiming", "_townGreedyTiming", "_townCarambushTiming", "_townDomiTiming",
                     "_lastVisitSetAlley", "_larcenyDailyTiming", "_larcenyJobsTiming", "_InfiltrationAlertTiming",
                 })
            Add(name, FourberieStateValueKind.StringTimeDictionary, StringTime);

        const string StringInt = "System.Collections.Generic.Dictionary`2[System.String,System.Int32]";
        Add("_supportedBandits", FourberieStateValueKind.StringIntDictionary, StringInt);
        Add("_stringIntDico", FourberieStateValueKind.StringIntDictionary, StringInt);
        Add("_stringClanDico", FourberieStateValueKind.StringIntDictionary, StringInt);

        const string StringString = "System.Collections.Generic.Dictionary`2[System.String,System.String]";
        Add("_assignedGl", FourberieStateValueKind.StringStringDictionary, StringString);
        Add("_stringHeroIdDico", FourberieStateValueKind.StringStringDictionary, StringString);

        const string StringList = "System.Collections.Generic.List`1[System.String]";
        Add("_partnerRecomList", FourberieStateValueKind.StringList, StringList);
        Add("_territoryList", FourberieStateValueKind.StringList, StringList);
        Add("_partnershipList", FourberieStateValueKind.StringList, StringList);

        Add("_crimeValue", FourberieStateValueKind.IntIntDictionary,
            "System.Collections.Generic.Dictionary`2[System.Int32,System.Int32]");
        Add("_campaignTimeDictio", FourberieStateValueKind.IntTimeDictionary,
            "System.Collections.Generic.Dictionary`2[System.Int32,TaleWorlds.CampaignSystem.CampaignTime]");
        Add("_stringHeroDico", FourberieStateValueKind.StringObjectDictionary,
            "System.Collections.Generic.Dictionary`2[System.String,TaleWorlds.CampaignSystem.Hero]");

        Add("_getSomeHelp", FourberieStateValueKind.Boolean, "System.Boolean");
        Add("_gangLeader", FourberieStateValueKind.ObjectReference, "TaleWorlds.CampaignSystem.Hero");
        Add("_FourbParty", FourberieStateValueKind.ObjectReference, "TaleWorlds.CampaignSystem.Party.MobileParty");
        Add("_extoVillage", FourberieStateValueKind.ObjectReference, "TaleWorlds.CampaignSystem.Settlements.Village");
        Add("_robCastle", FourberieStateValueKind.ObjectReference, "TaleWorlds.CampaignSystem.Settlements.Settlement");
        Add("_crimeBase", FourberieStateValueKind.ObjectReference, "TaleWorlds.CampaignSystem.Settlements.Settlement");
        Add("_crimeBaseParty", FourberieStateValueKind.ObjectReference, "TaleWorlds.CampaignSystem.Party.MobileParty");
        Add("_insucaraF", FourberieStateValueKind.ObjectReference, "TaleWorlds.CampaignSystem.Party.MobileParty");
        Add("_insubandF", FourberieStateValueKind.ObjectReference, "TaleWorlds.CampaignSystem.Party.MobileParty");
        Add("_catchbandF", FourberieStateValueKind.ObjectReference, "TaleWorlds.CampaignSystem.Party.MobileParty");
        Add("_agentsParty", FourberieStateValueKind.ObjectReference, "TaleWorlds.CampaignSystem.Party.MobileParty");

        Add("_banditsFollowers", FourberieStateValueKind.ObjectList,
            "System.Collections.Generic.List`1[TaleWorlds.CampaignSystem.Party.MobileParty]");
        Add("_playerTroopsF", FourberieStateValueKind.TroopRosterElement,
            "System.Collections.Generic.List`1[TaleWorlds.CampaignSystem.Roster.TroopRosterElement]");

        return fields;
    }
}
