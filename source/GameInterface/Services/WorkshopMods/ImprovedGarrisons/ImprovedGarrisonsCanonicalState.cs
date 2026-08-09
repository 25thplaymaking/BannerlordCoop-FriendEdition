using GameInterface.Services.ObjectManager;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.WorkshopMods.ImprovedGarrisons;

/// <summary>
/// Converts Improved Garrisons' external XML/binary-backed configuration into stable, invariant
/// values. The digest is independent of dictionary enumeration order, locale, and local file path.
/// It is also the late-join payload for the primitive settings the compatibility layer supports.
/// </summary>
internal static class ImprovedGarrisonsCanonicalState
{
    private const string ConfigScope = "config";
    private const string GlobalScope = "global";
    private const string GlobalTemplateScope = "global-template";
    private const string TownScope = "town";
    private const string TemplateTroopPrefix = "Template.Troop:";

    public static bool TryBuild(
        Assembly assembly,
        IObjectManager objectManager,
        out ImprovedGarrisonsStateValue[] values,
        out string hash,
        out string failure)
    {
        values = Array.Empty<ImprovedGarrisonsStateValue>();
        hash = null;
        failure = null;

        try
        {
            if (!TryGetStaticInstance(assembly,
                    "ImprovedGarrisons.SaveSystem.Configuration.ConfigManager", "Instance",
                    out var configManager))
            {
                failure = "ConfigManager.Instance is unavailable";
                return false;
            }

            var configProperty = configManager.GetType().GetProperty(
                "Config", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var config = configProperty?.GetValue(configManager);
            if (config == null)
            {
                failure = "ConfigManager.Config is unavailable";
                return false;
            }

            // ConfigManager.Config returns a transient default when its backing field is null.
            // Persist that object before applying the snapshot or every mutation would be lost.
            configProperty.SetValue(configManager, config);
            if (!TrySetDeterministicModelFlags(config, force: true, out failure)) return false;

            if (!TryGetStaticInstance(assembly, "ImprovedGarrisons.Main", "GarrisonBehavior", out var behavior))
            {
                failure = "Main.GarrisonBehavior is not initialized";
                return false;
            }

            if (GetProperty(behavior, "SettlementSettingsData") is not IDictionary settings)
            {
                failure = "SettlementSettingsData is unavailable";
                return false;
            }

            var result = new List<ImprovedGarrisonsStateValue>();
            AddSimpleProperties(result, ConfigScope, string.Empty, config);

            if (!TryGetStaticInstance(
                    assembly,
                    "ImprovedGarrisons.SaveSystem.SaveData.DataTypes.GlobalSettings",
                    "Instance",
                    out var globalSettings) || GetProperty(globalSettings, "TrainingTemplates") is not IDictionary globalTemplates)
            {
                failure = "GlobalSettings.Instance is unavailable";
                return false;
            }

            AddSimpleProperties(result, GlobalScope, string.Empty, globalSettings);
            foreach (DictionaryEntry item in globalTemplates)
            {
                if (item.Key is not string templateName || string.IsNullOrEmpty(templateName) || item.Value == null)
                {
                    failure = "TrainingTemplates contains an invalid key or value";
                    return false;
                }
                AddSimpleProperties(result, GlobalTemplateScope, templateName, item.Value);
                if (!AddTemplateTroops(result, GlobalTemplateScope, templateName, item.Value, out failure))
                    return false;
            }

            foreach (DictionaryEntry item in settings)
            {
                if (item.Key is not string settlementName || string.IsNullOrEmpty(settlementName) || item.Value == null)
                {
                    failure = "SettlementSettingsData contains an invalid key or value";
                    return false;
                }
                var town = FindTown(settlementName);
                if (town == null || !objectManager.TryGetId(town, out var townId))
                {
                    failure = $"could not resolve settings key '{settlementName}' to a registered town";
                    return false;
                }

                AddSimpleProperties(result, TownScope, townId, item.Value);
                if (!AddTemplate(result, townId, item.Value, out failure)) return false;
            }

            values = Sort(result).ToArray();
            hash = ComputeHash(values);
            return true;
        }
        catch (Exception ex)
        {
            failure = $"state capture failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public static bool TryApply(
        Assembly assembly,
        IObjectManager objectManager,
        IReadOnlyCollection<ImprovedGarrisonsStateValue> values,
        out string failure)
    {
        failure = null;
        try
        {
            if (assembly == null || objectManager == null || values == null || values.Any(value => value == null))
            {
                failure = "state apply received a null dependency or canonical value";
                return false;
            }

            var payload = values.ToArray();
            var keys = new HashSet<Tuple<string, string, string>>();
            foreach (var value in payload)
            {
                if (!IsKnownScope(value.Scope))
                {
                    failure = $"unknown canonical scope '{value.Scope ?? "null"}'";
                    return false;
                }
                if (!keys.Add(Tuple.Create(value.Scope, value.TargetId, value.Property)))
                {
                    failure = $"duplicate canonical property {value.Scope}/{value.TargetId}/{value.Property}";
                    return false;
                }
                if ((value.Scope == ConfigScope || value.Scope == GlobalScope) && value.TargetId != string.Empty)
                {
                    failure = $"scope {value.Scope} has a non-empty target";
                    return false;
                }
            }

            if (!TryGetStaticInstance(assembly,
                    "ImprovedGarrisons.SaveSystem.Configuration.ConfigManager", "Instance",
                    out var configManager))
            {
                failure = "ConfigManager.Instance is unavailable";
                return false;
            }
            var configProperty = configManager.GetType().GetProperty(
                "Config", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (configProperty == null || !configProperty.CanWrite ||
                Activator.CreateInstance(configProperty.PropertyType) is not object replacementConfig ||
                !TryPopulateSimpleObject(
                    replacementConfig,
                    payload.Where(value => value.Scope == ConfigScope).ToArray(),
                    out failure) ||
                !TrySetDeterministicModelFlags(replacementConfig, force: false, out failure))
            {
                failure ??= "ConfigManager.Config cannot be replaced";
                return false;
            }

            var globalType = assembly.GetType(
                "ImprovedGarrisons.SaveSystem.SaveData.DataTypes.GlobalSettings", false);
            var globalInstanceProperty = globalType?.GetProperty(
                "Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (globalInstanceProperty == null || !globalInstanceProperty.CanWrite ||
                Activator.CreateInstance(globalType) is not object replacementGlobal ||
                !TryPopulateSimpleObject(
                    replacementGlobal,
                    payload.Where(value => value.Scope == GlobalScope).ToArray(),
                    out failure) ||
                GetProperty(replacementGlobal, "TrainingTemplates") is not IDictionary replacementTemplates ||
                !TryPopulateGlobalTemplates(assembly, replacementTemplates, payload, out failure))
            {
                failure ??= "GlobalSettings.Instance cannot be replaced";
                return false;
            }

            if (!TryGetStaticInstance(
                    assembly, "ImprovedGarrisons.SaveSystem.SaveData.IGSaveData", "Instance", out var saveData))
            {
                failure = "IGSaveData.Instance is unavailable";
                return false;
            }
            var settingsProperty = saveData.GetType().GetProperty(
                "SettlementSettingsData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (settingsProperty == null || !settingsProperty.CanWrite ||
                Activator.CreateInstance(settingsProperty.PropertyType) is not IDictionary replacementSettings)
            {
                failure = "SettlementSettingsData cannot be replaced atomically";
                return false;
            }

            var townType = assembly.GetType(
                "ImprovedGarrisons.SaveSystem.SaveData.DataTypes.GarrisonSettings", false);
            if (townType == null)
            {
                failure = "GarrisonSettings type is unavailable";
                return false;
            }
            var townGroups = payload
                .Where(item => item.Scope == TownScope)
                .GroupBy(item => item.TargetId, StringComparer.Ordinal)
                .ToArray();
            foreach (var townGroup in townGroups)
            {
                if (string.IsNullOrEmpty(townGroup.Key))
                {
                    failure = "town scope has an empty object ID";
                    return false;
                }
                if (!objectManager.TryGetObject<Town>(townGroup.Key, out var resolvedTown) || resolvedTown == null)
                {
                    failure = $"town {townGroup.Key} is not registered";
                    return false;
                }

                var townName = resolvedTown.Name?.ToString();
                if (string.IsNullOrEmpty(townName))
                {
                    failure = $"town {townGroup.Key} has no settings key";
                    return false;
                }
                if (replacementSettings.Contains(townName))
                {
                    failure = $"town settings key '{townName}' collides with another object ID";
                    return false;
                }

                var replacementTown = Activator.CreateInstance(townType);
                if (replacementTown == null || !TryPopulateSimpleObject(
                        replacementTown,
                        townGroup.Where(value => value.Property != "Template.Name" &&
                                                  !value.Property.StartsWith(TemplateTroopPrefix, StringComparison.Ordinal))
                            .ToArray(),
                        out failure))
                {
                    failure ??= $"could not build settings for town {townGroup.Key}";
                    return false;
                }

                var templateNames = townGroup.Where(value => value.Property == "Template.Name").ToArray();
                var replacementTemplate = GetProperty(replacementTown, "Template");
                if (templateNames.Length != 1 ||
                    !TrySetSimpleProperty(replacementTemplate, "Name", templateNames[0].Value, out failure) ||
                    !TryApplyTemplateTroopsStrict(replacementTemplate, townGroup, out failure))
                {
                    failure ??= $"town {townGroup.Key} has an invalid training template";
                    return false;
                }

                replacementSettings.Add(townName, replacementTown);
            }

            // All parsing, object resolution, constructors, and value setters happened on detached
            // replacements. Only these three references are live mutations; roll them all back if
            // any setter unexpectedly rejects the prepared object.
            var previousConfig = configProperty.GetValue(configManager);
            var previousGlobal = globalInstanceProperty.GetValue(null);
            var previousSettings = settingsProperty.GetValue(saveData);
            try
            {
                configProperty.SetValue(configManager, replacementConfig);
                globalInstanceProperty.SetValue(null, replacementGlobal);
                settingsProperty.SetValue(saveData, replacementSettings);
            }
            catch (Exception commitException)
            {
                try
                {
                    configProperty.SetValue(configManager, previousConfig);
                    globalInstanceProperty.SetValue(null, previousGlobal);
                    settingsProperty.SetValue(saveData, previousSettings);
                }
                catch (Exception rollbackException)
                {
                    failure = $"atomic state commit failed ({commitException.GetType().Name}: {commitException.Message}) " +
                              $"and rollback failed ({rollbackException.GetType().Name}: {rollbackException.Message})";
                    return false;
                }

                failure = $"atomic state commit failed and was rolled back: {commitException.GetType().Name}: {commitException.Message}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            failure = $"state apply failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public static string ComputeHash(IEnumerable<ImprovedGarrisonsStateValue> values)
    {
        var builder = new StringBuilder();
        foreach (var value in Sort(values ?? Array.Empty<ImprovedGarrisonsStateValue>()))
        {
            AppendFramed(builder, value.Scope);
            AppendFramed(builder, value.TargetId);
            AppendFramed(builder, value.Property);
            AppendFramed(builder, value.Value);
        }

        using (var sha = SHA256.Create())
        {
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
            var chars = new char[bytes.Length * 2];
            const string digits = "0123456789abcdef";
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = digits[bytes[i] >> 4];
                chars[(i * 2) + 1] = digits[bytes[i] & 15];
            }
            return new string(chars);
        }
    }

    internal static string FormatValue(object value)
    {
        if (value == null) return string.Empty;
        if (value is bool boolean) return boolean ? "true" : "false";
        if (value is float single) return single.ToString("R", CultureInfo.InvariantCulture);
        if (value is double dbl) return dbl.ToString("R", CultureInfo.InvariantCulture);
        if (value is decimal dec) return dec.ToString(CultureInfo.InvariantCulture);
        if (value is bool[] flags) return string.Join(",", flags.Select(flag => flag ? "1" : "0"));
        if (value.GetType().IsEnum) return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    internal static bool TryForceDeterministicModelConfiguration(Assembly assembly, out string failure)
    {
        failure = null;
        try
        {
            if (!TryGetStaticInstance(
                    assembly,
                    "ImprovedGarrisons.SaveSystem.Configuration.ConfigManager",
                    "Instance",
                    out var manager))
            {
                failure = "ConfigManager.Instance is unavailable";
                return false;
            }

            var property = manager.GetType().GetProperty(
                "Config", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var config = property?.GetValue(manager);
            if (property == null || !property.CanWrite || config == null ||
                !TrySetDeterministicModelFlags(config, force: true, out failure))
            {
                failure ??= "ConfigManager.Config cannot be pinned";
                return false;
            }

            property.SetValue(manager, config);
            return true;
        }
        catch (Exception ex)
        {
            failure = $"model configuration pin failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    internal static bool TryParseValue(string value, Type type, out object parsed)
    {
        parsed = null;
        if (type == typeof(string)) { parsed = value; return true; }
        if (type == typeof(bool) && bool.TryParse(value, out var boolean)) { parsed = boolean; return true; }
        if (type == typeof(int) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) { parsed = integer; return true; }
        if (type == typeof(float) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) &&
            !float.IsNaN(single) && !float.IsInfinity(single)) { parsed = single; return true; }
        if (type == typeof(double) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl) &&
            !double.IsNaN(dbl) && !double.IsInfinity(dbl)) { parsed = dbl; return true; }
        if (type == typeof(decimal) && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var dec))
        { parsed = dec; return true; }
        if (type == typeof(bool[]))
        {
            if (string.IsNullOrEmpty(value))
            {
                parsed = Array.Empty<bool>();
                return true;
            }
            var flags = value.Split(',');
            if (flags.Any(item => item != "0" && item != "1")) return false;
            parsed = flags.Select(item => item == "1").ToArray();
            return true;
        }
        if (type.IsEnum && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var enumValue))
        {
            var candidate = Enum.ToObject(type, enumValue);
            if (Enum.IsDefined(type, candidate)) { parsed = candidate; return true; }
        }
        return false;
    }

    private static IEnumerable<ImprovedGarrisonsStateValue> Sort(IEnumerable<ImprovedGarrisonsStateValue> values) =>
        values.Where(value => value != null)
            .OrderBy(value => value.Scope ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(value => value.TargetId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(value => value.Property ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(value => value.Value ?? string.Empty, StringComparer.Ordinal);

    private static void AppendFramed(StringBuilder builder, string value)
    {
        value ??= string.Empty;
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }

    private static void AddSimpleProperties(
        ICollection<ImprovedGarrisonsStateValue> values,
        string scope,
        string targetId,
        object source)
    {
        foreach (var property in source.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.CanRead && property.CanWrite && IsSupportedProperty(property.PropertyType))
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            values.Add(new ImprovedGarrisonsStateValue(
                scope,
                targetId,
                property.Name,
                FormatValue(property.GetValue(source))));
        }
    }

    private static bool IsSupportedProperty(Type type) =>
        type == typeof(string) || type == typeof(bool) || type == typeof(int) || type == typeof(float) ||
        type == typeof(double) || type == typeof(decimal) || type == typeof(bool[]) || type.IsEnum;

    private static bool AddTemplate(
        ICollection<ImprovedGarrisonsStateValue> result,
        string townId,
        object settings,
        out string failure)
    {
        failure = null;
        var template = GetProperty(settings, "Template");
        if (template == null)
        {
            failure = $"town {townId} has no training template";
            return false;
        }

        result.Add(new ImprovedGarrisonsStateValue(
            TownScope, townId, "Template.Name", FormatValue(GetProperty(template, "Name"))));

        return AddTemplateTroops(result, TownScope, townId, template, out failure);
    }

    private static bool AddTemplateTroops(
        ICollection<ImprovedGarrisonsStateValue> result,
        string scope,
        string targetId,
        object template,
        out string failure)
    {
        failure = null;
        if (template == null)
        {
            failure = $"template {scope}/{targetId} is null";
            return false;
        }

        var getTroops = template.GetType().GetMethod("GetTroopList", BindingFlags.Instance | BindingFlags.Public);
        if (getTroops?.Invoke(template, null) is not IDictionary troops)
        {
            failure = $"template {scope}/{targetId} has no readable troop dictionary";
            return false;
        }

        foreach (DictionaryEntry troop in troops)
        {
            if (troop.Key is not string troopId || string.IsNullOrEmpty(troopId) || troop.Value == null)
            {
                failure = $"template {scope}/{targetId} contains an invalid troop";
                return false;
            }
            var encodedId = Convert.ToBase64String(Encoding.UTF8.GetBytes(troopId));
            result.Add(new ImprovedGarrisonsStateValue(
                scope, targetId, TemplateTroopPrefix + encodedId, FormatValue(troop.Value)));
        }
        return true;
    }

    private static bool TryPopulateGlobalTemplates(
        Assembly assembly,
        IDictionary templates,
        IEnumerable<ImprovedGarrisonsStateValue> values,
        out string failure)
    {
        failure = null;
        var templateType = assembly.GetType(
            "ImprovedGarrisons.SaveSystem.SaveData.DataTypes.TrainingTemplate", false);
        var constructor = templateType?.GetConstructor(new[] { typeof(string) });
        if (constructor == null)
        {
            failure = "TrainingTemplate(string) is unavailable";
            return false;
        }

        templates.Clear();
        foreach (var group in values
                     .Where(item => item.Scope == GlobalTemplateScope)
                     .GroupBy(item => item.TargetId, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(group.Key))
            {
                failure = "global template has an empty name";
                return false;
            }
            var template = constructor.Invoke(new object[] { group.Key });
            if (!TryPopulateSimpleObject(
                    template,
                    group.Where(item => !item.Property.StartsWith(TemplateTroopPrefix, StringComparison.Ordinal)).ToArray(),
                    out failure) ||
                !string.Equals(GetProperty(template, "Name") as string, group.Key, StringComparison.Ordinal) ||
                !TryApplyTemplateTroopsStrict(template, group, out failure))
            {
                failure ??= $"global template '{group.Key}' has inconsistent name or values";
                return false;
            }
            templates.Add(group.Key, template);
        }
        return true;
    }

    private static bool TryPopulateSimpleObject(
        object target,
        IReadOnlyCollection<ImprovedGarrisonsStateValue> supplied,
        out string failure)
    {
        failure = null;
        if (target == null || supplied == null)
        {
            failure = "simple canonical target or values are null";
            return false;
        }

        var properties = target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.CanWrite && IsSupportedProperty(property.PropertyType))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();
        if (supplied.Count != properties.Length)
        {
            failure = $"{target.GetType().FullName} expected {properties.Length} simple properties but received {supplied.Count}";
            return false;
        }

        foreach (var property in properties)
        {
            var matches = supplied.Where(value => string.Equals(value.Property, property.Name, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || !TrySetSimpleProperty(target, property.Name, matches[0].Value, out failure))
            {
                failure ??= $"{target.GetType().FullName}.{property.Name} is missing or duplicated";
                return false;
            }
        }
        return true;
    }

    private static bool TryApplyTemplateTroopsStrict(
        object template,
        IEnumerable<ImprovedGarrisonsStateValue> values,
        out string failure)
    {
        failure = null;
        if (template == null)
        {
            failure = "training template is null";
            return false;
        }

        var troops = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in values.Where(item => item.Property.StartsWith(TemplateTroopPrefix, StringComparison.Ordinal)))
        {
            try
            {
                var encoded = value.Property.Substring(TemplateTroopPrefix.Length);
                var id = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded));
                if (string.IsNullOrEmpty(id) ||
                    !int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
                    count < 0 || troops.ContainsKey(id))
                {
                    failure = "training template contains an empty/duplicate troop ID or invalid count";
                    return false;
                }
                troops.Add(id, count);
            }
            catch (Exception ex) when (ex is FormatException || ex is DecoderFallbackException)
            {
                failure = $"training template troop ID is not canonical base64 UTF-8: {ex.Message}";
                return false;
            }
        }

        var setTroops = template.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == "SetTroops" &&
                                       method.GetParameters().Length == 1 &&
                                       method.GetParameters()[0].ParameterType == typeof(Dictionary<string, int>));
        var getTroops = template.GetType().GetMethod("GetTroopList", BindingFlags.Instance | BindingFlags.Public);
        if (setTroops == null || getTroops == null)
        {
            failure = "training template troop accessors are unavailable";
            return false;
        }

        setTroops.Invoke(template, new object[] { troops });
        if (getTroops.Invoke(template, null) is not IDictionary actual || actual.Count != troops.Count)
        {
            failure = "training template rejected the prepared troop dictionary";
            return false;
        }
        foreach (var troop in troops)
        {
            if (!actual.Contains(troop.Key) || Convert.ToInt32(actual[troop.Key], CultureInfo.InvariantCulture) != troop.Value)
            {
                failure = $"training template did not retain troop {troop.Key}";
                return false;
            }
        }
        return true;
    }

    internal static object GetOrCreateTownSettings(Assembly assembly, IDictionary settings, Town town)
    {
        var key = town?.Name?.ToString();
        if (string.IsNullOrEmpty(key)) return null;
        if (settings.Contains(key)) return settings[key];

        var type = assembly.GetType("ImprovedGarrisons.SaveSystem.SaveData.DataTypes.GarrisonSettings", false);
        if (type == null) return null;
        var value = Activator.CreateInstance(type);
        settings.Add(key, value);
        return value;
    }

    private static Town FindTown(string settlementName)
    {
        if (string.IsNullOrEmpty(settlementName)) return null;
        return Settlement.All
            .Where(settlement => settlement?.Town != null)
            .Select(settlement => settlement.Town)
            .FirstOrDefault(town => string.Equals(town.Name?.ToString(), settlementName, StringComparison.Ordinal));
    }

    private static bool TryGetStaticInstance(Assembly assembly, string typeName, string propertyName, out object value)
    {
        value = null;
        var type = assembly.GetType(typeName, false);
        if (type == null) return false;
        value = type.GetProperty(propertyName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
        return value != null;
    }

    private static object GetProperty(object instance, string name) =>
        instance?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(instance);

    private static bool IsKnownScope(string scope) =>
        scope == ConfigScope || scope == GlobalScope || scope == GlobalTemplateScope || scope == TownScope;

    private static bool TrySetDeterministicModelFlags(object config, bool force, out string failure)
    {
        failure = null;
        var expected = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["LoadCustomPartySizeModel"] = true,
            ["LoadCustomPartySpeedModel"] = false,
            ["LoadFoodGatheringModule"] = false
        };
        foreach (var pair in expected)
        {
            var property = config?.GetType().GetProperty(pair.Key, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || property.PropertyType != typeof(bool) || !property.CanRead || !property.CanWrite)
            {
                failure = $"required optional-model flag {pair.Key} is unavailable";
                return false;
            }

            if (force) property.SetValue(config, pair.Value);
            if (property.GetValue(config) is not bool actual || actual != pair.Value)
            {
                failure = $"optional-model flag {pair.Key} must be pinned to {pair.Value}";
                return false;
            }
        }
        return true;
    }

    private static bool TrySetSimpleProperty(
        object instance,
        string name,
        string value,
        out string failure)
    {
        failure = null;
        if (instance == null)
        {
            failure = $"cannot set {name} on a null object";
            return false;
        }
        var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property == null || !property.CanWrite || !IsSupportedProperty(property.PropertyType))
        {
            failure = $"unknown or unsupported canonical property {instance.GetType().FullName}.{name}";
            return false;
        }
        if (!TryParseValue(value, property.PropertyType, out var parsed))
        {
            failure = $"invalid {property.PropertyType.FullName} value for {instance.GetType().FullName}.{name}";
            return false;
        }
        property.SetValue(instance, parsed);
        return true;
    }
}
