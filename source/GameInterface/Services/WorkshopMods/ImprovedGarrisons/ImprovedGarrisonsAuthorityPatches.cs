using Common;
using Common.Logging;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace GameInterface.Services.WorkshopMods.ImprovedGarrisons;

internal interface IImprovedGarrisonsPatchRuntime
{
    bool TryRouteSetting(object manager, MethodBase method, object[] arguments);
    void NotifyDenied(string method);
    void OnAuthoritativeTickCompleted();
    bool TryGetTownSettings(Town town, out object settings);
    void ReconcileSettlements();
    void OnSettlementOwnerChanged(Settlement settlement);
}

internal static class ImprovedGarrisonsPatchRuntime
{
    public static IImprovedGarrisonsPatchRuntime Current { get; set; }
}

internal sealed class ImprovedGarrisonsTickLedger
{
    private readonly object sync = new object();
    private readonly Dictionary<string, long> lastTickByOperation = new Dictionary<string, long>(StringComparer.Ordinal);

    public bool TryEnter(string operation, long tick)
    {
        if (string.IsNullOrEmpty(operation)) return false;
        lock (sync)
        {
            if (lastTickByOperation.TryGetValue(operation, out var previous) && previous == tick) return false;
            lastTickByOperation[operation] = tick;
            return true;
        }
    }

    public void Reset()
    {
        lock (sync) lastTickByOperation.Clear();
    }
}

/// <summary>Harmony prefixes used without any compile-time reference to ImprovedGarrisons.dll.</summary>
internal static class ImprovedGarrisonsAuthorityPatches
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(ImprovedGarrisonsAuthorityPatches));
    private static readonly ImprovedGarrisonsTickLedger TickLedger = new ImprovedGarrisonsTickLedger();

    public static bool ServerOnlyPrefix() => ModInformation.IsServer;

    public static bool DeterministicInitializationPrefix(object __instance, MethodBase __originalMethod)
    {
        if (!string.Equals(__originalMethod?.Name, "AddModels", StringComparison.Ordinal)) return true;

        var assembly = __instance?.GetType().Assembly;
        if (ImprovedGarrisonsCanonicalState.TryForceDeterministicModelConfiguration(assembly, out var failure))
            return true;

        Logger.Fatal("Improved Garrisons optional model composition could not be made deterministic: {Failure}", failure);
        throw new InvalidOperationException(
            "Improved Garrisons model registration was aborted to prevent different client/server campaign models: " +
            failure);
    }

    public static bool ServerTickPrefix(MethodBase __originalMethod, object[] __args)
    {
        if (!ModInformation.IsServer) return false;

        var campaign = Campaign.Current;
        var tick = campaign == null ? long.MinValue : CampaignTime.Now.NumTicks;
        var campaignId = campaign?.UniqueGameId ?? "no-campaign";
        var subject = GetSubject(__args);
        var key = campaignId + "|" + MethodKey(__originalMethod) + "|" + subject;
        return TickLedger.TryEnter(key, tick);
    }

    public static void ServerTickPostfix()
    {
        if (ModInformation.IsServer)
            ImprovedGarrisonsPatchRuntime.Current?.OnAuthoritativeTickCompleted();
    }

    public static bool RoutedSettingPrefix(object __instance, MethodBase __originalMethod, object[] __args)
    {
        if (ModInformation.IsServer) return true;

        var runtime = ImprovedGarrisonsPatchRuntime.Current;
        if (runtime != null && runtime.TryRouteSetting(__instance, __originalMethod, __args)) return false;

        runtime?.NotifyDenied(__originalMethod?.Name ?? "unknown setting");
        return false;
    }

    public static bool DeniedClientUiPrefix(MethodBase __originalMethod)
    {
        if (ModInformation.IsServer) return true;
        ImprovedGarrisonsPatchRuntime.Current?.NotifyDenied(__originalMethod?.Name ?? "unsupported action");
        return false;
    }

    public static bool StablePartyIdentityPrefix(
        ref string id,
        Settlement homeSettlement,
        Settlement spawnOn)
    {
        if (!ModInformation.IsServer) return false;

        id = BuildStablePartyId(id, homeSettlement?.StringId, spawnOn?.StringId);
        return true;
    }

    public static bool PartyHomeResolverPrefix(MobileParty party, ref Settlement __result)
    {
        if (party?.HomeSettlement == null) return true;
        __result = party.HomeSettlement;
        return false;
    }

    public static bool VillagePartySpawnResolverPrefix(MobileParty party, ref Settlement __result)
    {
        if (party == null || !TryReadStablePartyIds(party.StringId, out _, out var spawnId)) return true;
        var spawn = Settlement.All.FirstOrDefault(
            settlement => string.Equals(settlement?.StringId, spawnId, StringComparison.Ordinal));
        if (spawn == null) return true;
        __result = spawn;
        return false;
    }

    public static bool VillagePartyLookupPrefix(Village village, ref PartyBase __result)
    {
        var villageId = village?.Settlement?.StringId;
        if (string.IsNullOrEmpty(villageId)) return true;

        var party = MobileParty.All.FirstOrDefault(candidate =>
            candidate?.StringId?.StartsWith("improvedgarrison_recruit_", StringComparison.Ordinal) == true &&
            TryReadStablePartyIds(candidate.StringId, out _, out var spawnId) &&
            string.Equals(spawnId, villageId, StringComparison.Ordinal));
        if (party == null) return true;
        __result = party.Party;
        return false;
    }

    public static bool ClientDefaultConfigPrefix(object __instance)
    {
        if (ModInformation.IsServer) return true;
        if (__instance == null) return false;

        try
        {
            var configProperty = __instance.GetType().GetProperty(
                "Config", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var configType = configProperty?.PropertyType;
            var config = configType == null ? null : Activator.CreateInstance(configType);
            configType?.GetMethod("ResetToDefault", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.Invoke(config, null);
            configProperty?.SetValue(__instance, config);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not initialize the Improved Garrisons client config without disk access");
        }

        // Client configuration always comes from the server snapshot. Never let the original
        // method create, read, or rewrite per-save files on a client.
        return false;
    }

    public static bool PlayerTownSettingsPrefix(Town town, ref object __result)
    {
        var runtime = ImprovedGarrisonsPatchRuntime.Current;
        if (runtime == null || !runtime.TryGetTownSettings(town, out var settings)) return true;
        __result = settings;
        return false;
    }

    public static bool PlayerSettlementInitializationPrefix()
    {
        var runtime = ImprovedGarrisonsPatchRuntime.Current;
        if (runtime == null) return false;
        runtime.ReconcileSettlements();
        return false;
    }

    public static bool PlayerSettlementOwnerChangedPrefix(Settlement settlement)
    {
        var runtime = ImprovedGarrisonsPatchRuntime.Current;
        if (runtime == null) return false;
        runtime.OnSettlementOwnerChanged(settlement);
        return false;
    }

    public static void FinanceReadPrefix(ref bool applyWithdrawals)
    {
        if (ModInformation.IsClient) applyWithdrawals = false;
    }

    internal static string BuildStablePartyId(string requestedId, string homeSettlementId, string spawnSettlementId)
    {
        var category = GetPartyCategory(requestedId);
        var suffix = GetNumericSuffix(requestedId);
        var originalToken = StableToken(requestedId ?? string.Empty);
        var home = Sanitize(homeSettlementId);
        var spawn = Sanitize(spawnSettlementId);
        return category + "coop_h" + home.Length.ToString(CultureInfo.InvariantCulture) + "_" + home +
               "_s" + spawn.Length.ToString(CultureInfo.InvariantCulture) + "_" + spawn +
               "_o" + originalToken + suffix;
    }

    internal static bool TryReadStablePartyIds(string partyId, out string homeSettlementId, out string spawnSettlementId)
    {
        homeSettlementId = null;
        spawnSettlementId = null;
        if (string.IsNullOrEmpty(partyId)) return false;

        var marker = partyId.IndexOf("coop_h", StringComparison.Ordinal);
        if (marker < 0) return false;
        var position = marker + "coop_h".Length;
        if (!TryReadLengthFramedValue(partyId, ref position, out homeSettlementId)) return false;
        if (position + 2 > partyId.Length || partyId[position] != '_' || partyId[position + 1] != 's') return false;
        position += 2;
        return TryReadLengthFramedValue(partyId, ref position, out spawnSettlementId);
    }

    internal static void ResetTickLedger() => TickLedger.Reset();

    private static string GetPartyCategory(string id)
    {
        if (id?.StartsWith("mobilegarrison_", StringComparison.Ordinal) == true) return "mobilegarrison_";
        if (id?.StartsWith("garrisontransferparty_", StringComparison.Ordinal) == true) return "garrisontransferparty_";
        if (id?.StartsWith("improvedgarrison_recruiter_", StringComparison.Ordinal) == true) return "improvedgarrison_recruiter_";
        if (id?.StartsWith("improvedgarrison_recruit_", StringComparison.Ordinal) == true) return "improvedgarrison_recruit_";
        return "improvedgarrison_party_";
    }

    private static string GetNumericSuffix(string id)
    {
        if (string.IsNullOrEmpty(id)) return string.Empty;
        var lastSeparator = id.LastIndexOf('_');
        if (lastSeparator < 0 || lastSeparator == id.Length - 1) return string.Empty;
        var candidate = id.Substring(lastSeparator + 1);
        return int.TryParse(candidate, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? "_" + candidate
            : string.Empty;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        var chars = value.Select(character => char.IsLetterOrDigit(character) || character == '-' ? character : '_').ToArray();
        return new string(chars);
    }

    private static string StableToken(string value)
    {
        using var hash = SHA256.Create();
        var digest = hash.ComputeHash(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(12);
        for (var index = 0; index < 6; index++) builder.Append(digest[index].ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static bool TryReadLengthFramedValue(string input, ref int position, out string value)
    {
        value = null;
        var separator = input.IndexOf('_', position);
        if (separator <= position ||
            !int.TryParse(input.Substring(position, separator - position), NumberStyles.None,
                CultureInfo.InvariantCulture, out var length) ||
            length < 1 || separator + 1 + length > input.Length)
            return false;

        position = separator + 1;
        value = input.Substring(position, length);
        position += length;
        return true;
    }

    private static string GetSubject(IEnumerable<object> arguments)
    {
        if (arguments == null) return string.Empty;
        foreach (var argument in arguments)
        {
            if (argument is MobileParty party) return party.StringId ?? string.Empty;
            if (argument is Settlement settlement) return settlement.StringId ?? string.Empty;
            if (argument is Town town) return town.StringId ?? string.Empty;
        }
        return string.Empty;
    }

    private static string MethodKey(MethodBase method)
    {
        if (method == null) return "unknown";
        return method.DeclaringType?.FullName + "::" + method.Name + "(" +
               string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.FullName)) + ")";
    }
}
