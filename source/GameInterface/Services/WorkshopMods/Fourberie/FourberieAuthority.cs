using Common;
using GameInterface.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal interface IFourberiePatchRuntime
{
    void PublishIfChanged();
    bool TrySubmit(FourberieLocalOperation operation);
}

internal static class FourberiePatchRuntime
{
    public static IFourberiePatchRuntime Current { get; set; }
}

internal sealed class FourberieLocalTroopSelection
{
    public FourberieLocalTroopSelection(CharacterObject troop, int count)
    {
        Troop = troop;
        Count = count;
    }

    public CharacterObject Troop { get; }
    public int Count { get; }
}

internal sealed class FourberieLocalOperation
{
    public FourberieLocalOperation(
        FourberieOperation operation,
        Settlement settlement,
        Hero targetHero,
        Settlement secondarySettlement,
        int intValue,
        FourberieLocalTroopSelection[] troops)
    {
        Operation = operation;
        Settlement = settlement;
        TargetHero = targetHero;
        SecondarySettlement = secondarySettlement;
        IntValue = intValue;
        Troops = troops ?? Array.Empty<FourberieLocalTroopSelection>();
    }

    public FourberieOperation Operation { get; }
    public Settlement Settlement { get; }
    public Hero TargetHero { get; }
    public Settlement SecondarySettlement { get; }
    public int IntValue { get; }
    public FourberieLocalTroopSelection[] Troops { get; }
}

internal static class FourberiePartyCommitSuppression
{
    [ThreadStatic] private static bool pending;

    public static void Request() => pending = true;

    public static bool Consume()
    {
        bool result = pending;
        pending = false;
        return result;
    }

    public static void Reset() => pending = false;
}

internal sealed class FourberieTickLedger
{
    private readonly object sync = new object();
    private readonly Dictionary<string, long> highWatermarks = new Dictionary<string, long>(StringComparer.Ordinal);

    public bool TryEnter(string campaignId, string operation, string subject, long tick)
    {
        if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(operation)) return false;
        var key = campaignId + "|" + operation + "|" + (subject ?? string.Empty);

        lock (sync)
        {
            if (highWatermarks.TryGetValue(key, out var previous) && previous >= tick) return false;
            highWatermarks[key] = tick;
            return true;
        }
    }

    public void Reset()
    {
        lock (sync) highWatermarks.Clear();
    }
}

internal enum FourberieRevisionDecision
{
    Apply,
    AlreadyApplied,
    Stale,
    Conflict,
    Invalid,
}

/// <summary>
/// Monotonic snapshot watermark. Evaluation does not mutate the watermark; callers commit only
/// after a snapshot has passed its digest/config validation and was successfully applied.
/// </summary>
internal sealed class FourberieRevisionGate
{
    private long revision = -1;
    private string fingerprint;

    public long Revision => revision;
    public string Fingerprint => fingerprint;

    public FourberieRevisionDecision Evaluate(long candidateRevision, string candidateFingerprint)
    {
        if (candidateRevision < 0 || !FourberieStateCodec.IsSha256(candidateFingerprint))
            return FourberieRevisionDecision.Invalid;
        if (candidateRevision < revision) return FourberieRevisionDecision.Stale;
        if (candidateRevision > revision) return FourberieRevisionDecision.Apply;
        return string.Equals(candidateFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
            ? FourberieRevisionDecision.AlreadyApplied
            : FourberieRevisionDecision.Conflict;
    }

    public bool Commit(long candidateRevision, string candidateFingerprint)
    {
        if (Evaluate(candidateRevision, candidateFingerprint) != FourberieRevisionDecision.Apply)
            return false;

        revision = candidateRevision;
        fingerprint = candidateFingerprint.ToLowerInvariant();
        return true;
    }

    public void Reset()
    {
        revision = -1;
        fingerprint = null;
    }
}

internal static class FourberieAuthorityPatches
{
    private static readonly FourberieTickLedger TickLedger = new FourberieTickLedger();
    private static int agentEnlistSource;
    private static Settlement banditRecruitmentSettlement;
    private static int banditRecruitmentMaximum;

    public static bool ServerOnlyPrefix() => ModInformation.IsServer;

    public static bool ClientPresentationPrefix() => ModInformation.IsClient;

    public static bool ClientOperationPresentationPrefix(MethodBase __originalMethod, object[] __args)
    {
        if (!ModInformation.IsClient) return false;

        if (string.Equals(__originalMethod?.DeclaringType?.FullName, "Fourberie.CriminalVM", StringComparison.Ordinal))
        {
            agentEnlistSource = __args != null && __args.Length > 0 && __args[0] is int value ? value : 0;
        }
        else
        {
            banditRecruitmentSettlement = Settlement.CurrentSettlement;
            banditRecruitmentMaximum = __args != null && __args.Length > 0 && __args[0] is int value ? value : 0;
        }

        return true;
    }

    public static bool EnlistPartyConsequencePrefix(TroopRoster leftMemberRoster, ref bool __result)
    {
        if (!ModInformation.IsClient) return true;
        bool submitted = SubmitEnlistment(leftMemberRoster, FourberieOperation.EnlistAgentsFromParty);
        agentEnlistSource = 0;
        FourberiePartyCommitSuppression.Request();
        __result = submitted;
        return false;
    }

    public static bool EnlistLadsConsequencePrefix(TroopRoster leftMemberRoster, bool fromCancel)
    {
        if (!ModInformation.IsClient) return true;
        if (!fromCancel) SubmitEnlistment(leftMemberRoster, FourberieOperation.EnlistAgentsFromLads);
        agentEnlistSource = 0;
        return false;
    }

    public static bool RecruitBanditsConsequencePrefix(TroopRoster leftMemberRoster, ref bool __result)
    {
        if (!ModInformation.IsClient) return true;

        var type = HarmonyLib.AccessTools.TypeByName("Fourberie.FourbBanditBehavior");
        var baseline = type == null
            ? null
            : HarmonyLib.AccessTools.Field(type, "_dummyTroopRooster")?.GetValue(null) as TroopRoster;
        var selected = Difference(baseline, leftMemberRoster);
        bool submitted = FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
            FourberieOperation.RecruitBandits,
            banditRecruitmentSettlement,
            null,
            null,
            banditRecruitmentMaximum,
            selected)) == true;
        banditRecruitmentSettlement = null;
        banditRecruitmentMaximum = 0;
        FourberiePartyCommitSuppression.Request();
        __result = submitted;
        return false;
    }

    public static bool InsuranceScamConsequencePrefix(object __instance)
    {
        if (!ModInformation.IsClient) return false;
        if (__instance == null) return false;

        var type = __instance.GetType();
        var merchant = HarmonyLib.AccessTools.Field(type, "merchtarg")?.GetValue(__instance) as Hero;
        var settlement = HarmonyLib.AccessTools.Field(type, "currentSet")?.GetValue(__instance) as Settlement;
        var destination = HarmonyLib.AccessTools.Field(type, "settofrom")?.GetValue(__instance) as Settlement;
        FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
            FourberieOperation.StartInsuranceScam,
            settlement,
            merchant,
            destination,
            0,
            Array.Empty<FourberieLocalTroopSelection>()));
        return false;
    }

    public static bool BusinessStartConsequencePrefix(MethodBase __originalMethod)
    {
        if (!ModInformation.IsClient) return false;

        string typeName = __originalMethod?.DeclaringType?.FullName;
        int businessKey = typeName switch
        {
            "Fourberie.CriminalVM+<>c" => 11,
            "Fourberie.CriminalVM+<>c__DisplayClass16_0" => 21,
            "Fourberie.CriminalVM+<>c__DisplayClass28_0" => 31,
            _ => 0,
        };
        if (businessKey != 0)
            SubmitBusiness(FourberieOperation.StartCriminalBusiness, businessKey);
        return false;
    }

    public static bool BusinessUpgradeConsequencePrefix(object[] __args)
    {
        if (ModInformation.IsClient && TryBusinessKey(__args, out int businessKey))
            SubmitBusiness(FourberieOperation.UpgradeCriminalBusiness, businessKey);
        return false;
    }

    public static bool BusinessDowngradeConsequencePrefix(object[] __args)
    {
        if (ModInformation.IsClient && TryBusinessKey(__args, out int businessKey))
            SubmitBusiness(FourberieOperation.DowngradeCriminalBusiness, businessKey);
        return false;
    }

    public static bool SchemeBonusUpgradeConsequencePrefix(object[] __args)
    {
        if (ModInformation.IsClient && TrySchemeSlot(__args, out int slot))
            SubmitBusiness(FourberieOperation.UpgradeSchemeBonus, slot);
        return false;
    }

    public static bool SchemeBonusDowngradeConsequencePrefix(object[] __args)
    {
        if (ModInformation.IsClient && TrySchemeSlot(__args, out int slot))
            SubmitBusiness(FourberieOperation.DowngradeSchemeBonus, slot);
        return false;
    }

    public static bool SchemeBonusResetConsequencePrefix(MethodBase __originalMethod)
    {
        if (!ModInformation.IsClient) return false;
        int slot = string.Equals(__originalMethod?.Name, "SchBonus1Re", StringComparison.Ordinal) ? 7 :
            string.Equals(__originalMethod?.Name, "SchBonus2Re", StringComparison.Ordinal) ? 8 : 0;
        if (slot != 0) SubmitBusiness(FourberieOperation.ResetSchemeBonus, slot);
        return false;
    }

    public static bool AgentPartyCreateConsequencePrefix()
    {
        if (ModInformation.IsClient) SubmitBusiness(FourberieOperation.CreateAgentParty, 0);
        return false;
    }

    public static bool AgentPartyDisbandConsequencePrefix()
    {
        if (ModInformation.IsClient) SubmitBusiness(FourberieOperation.DisbandAgentParty, 0);
        return false;
    }

    public static bool AgentPartySelectionConsequencePrefix(object[] __args)
    {
        if (!ModInformation.IsClient) return false;
        string selection = (__args != null && __args.Length > 0
                ? __args[0] as IEnumerable<InquiryElement>
                : null)?
            .Select(element => element?.Identifier as string)
            .FirstOrDefault(identifier => !string.IsNullOrEmpty(identifier));
        FourberieOperation? operation = AgentPartyOperationForSelection(selection);
        if (!operation.HasValue) return true;

        SubmitBusiness(operation.Value, 0);
        return false;
    }

    internal static FourberieOperation? AgentPartyOperationForSelection(string selection) =>
        selection switch
        {
            "createAgentsParty" => FourberieOperation.CreateAgentParty,
            "disbandAgentsParty" => FourberieOperation.DisbandAgentParty,
            "addAgentsToParty" => FourberieOperation.RefillAgentParty,
            _ => null,
        };

    public static bool CrimeBaseResetConsequencePrefix()
    {
        if (ModInformation.IsClient) SubmitBusiness(FourberieOperation.ResetCrimeBaseParty, 0);
        return false;
    }

    public static bool MissionInitializationPrefix() => true;

    public static bool SeparatismLoyaltyCompositionPrefix(MethodBase __originalMethod, ref int __result)
    {
        if (!ModConfigProvider.ModOptions.Separatism.Enabled) return true;
        __result = string.Equals(
                __originalMethod?.Name,
                "get_RebellionStartLoyaltyThreshold",
                StringComparison.Ordinal)
            ? ModConfigProvider.ModOptions.Separatism.SettlementRebellionStartLoyaltyThreshold
            : ModConfigProvider.ModOptions.Separatism.SettlementRebellionEndLoyaltyThreshold;
        return false;
    }

    public static bool ServerTickPrefix(MethodBase __originalMethod, object[] __args)
    {
        if (!ModInformation.IsServer) return false;

        var campaign = Campaign.Current;
        var campaignId = campaign?.UniqueGameId ?? "no-campaign";
        var tick = campaign == null ? long.MinValue : CampaignTime.Now.NumTicks;
        return TickLedger.TryEnter(campaignId, MethodKey(__originalMethod), SubjectKey(__args), tick);
    }

    private static MethodInfo _refreshHeroDico;
    private static bool _refreshHeroDicoResolved;

    /// <summary>
    /// Replaces Fourberie's <c>Main.OnGameInitializationFinished</c> with the client-local hero-name
    /// cache refresh its menus need. Exact model compatibility is enforced earlier by the adapter,
    /// so Fourberie's load-order diagnostic is redundant here.
    /// </summary>
    public static bool RefreshHeroDicoOnlyPrefix()
    {
        if (!_refreshHeroDicoResolved)
        {
            _refreshHeroDicoResolved = true;
            var type = HarmonyLib.AccessTools.TypeByName("Fourberie.StringDicoHelper");
            _refreshHeroDico = type == null ? null : HarmonyLib.AccessTools.Method(type, "RefreshHeroDico");
        }

        try { _refreshHeroDico?.Invoke(null, null); }
        catch { /* cache refresh is best-effort; never abort game init on it */ }

        return false;
    }

    /// <summary>
    /// Fourberie's gameplay behaviors, added by name. Deliberately excludes its optional
    /// HomesSteadsAddOn / BellumCivileAddOn (cross-mod add-ons) — only the mod's own content.
    /// </summary>
    private static readonly string[] FourberieBehaviorTypeNames =
    {
        "Fourberie.FourberieBehavior",
        "Fourberie.FourbSafeHouseBehavior",
        "Fourberie.FourbEscapeBehavior",
        "Fourberie.FourbFightClubBehavior",
        "Fourberie.FourbBanditBehavior",
        "Fourberie.FourbRecruitableBehavior",
        "Fourberie.FourbContactMenu",
        "Fourberie.FourbContractBehavior",
    };

    /// <summary>
    /// Replaces Fourberie's monolithic <c>InitializeCampaignBehaviors</c>: adds its gameplay
    /// behaviors so the mod's content (safe houses, fight clubs, contracts, bandit systems, menus)
    /// is available in co-op, then returns <c>false</c> to skip the original — whose tail registers
    /// 14 game-model replacements that overlap Coop's authority. The behaviors' periodic ticks and
    /// state mutations stay gated by the separate ServerTick/ServerOnly guards; player-triggered
    /// actions are routed through Coop incrementally.
    /// </summary>
    public static bool InitializeBehaviorsAndModelsPrefix(object[] __args)
    {
        var starter = __args != null && __args.Length > 0
            ? __args[0] as CampaignGameStarter
            : null;

        if (starter == null)
            throw new InvalidOperationException(
                "Fourberie behavior initialization had no CampaignGameStarter; refusing the unsafe original initializer.");

        // The pinned original registers the eight Fourberie behaviors and fourteen decorator
        // models. Its two embedded cross-mod flags remain false because their prerequisite modules
        // are absent from Friend Edition, so those add-ons cannot register.
        return true;
    }

    internal static IReadOnlyList<CampaignBehaviorBase> PreflightBehaviors(
        IEnumerable<string> typeNames,
        Func<string, Type> resolveType)
    {
        if (typeNames == null) throw new ArgumentNullException(nameof(typeNames));
        if (resolveType == null) throw new ArgumentNullException(nameof(resolveType));

        var behaviors = new List<CampaignBehaviorBase>();
        foreach (var typeName in typeNames)
        {
            var type = resolveType(typeName);
            if (type == null || !typeof(CampaignBehaviorBase).IsAssignableFrom(type))
                throw new InvalidOperationException(
                    "Fourberie behavior preflight failed for " + (typeName ?? "missing type name") + ".");

            try
            {
                if (Activator.CreateInstance(type) is not CampaignBehaviorBase behavior)
                    throw new InvalidOperationException("constructor returned no campaign behavior");
                behaviors.Add(behavior);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Fourberie behavior preflight failed for " + typeName + ".",
                    exception);
            }
        }

        return behaviors;
    }

    public static void FinanceReadPrefix(ref bool applyWithdrawals)
    {
        if (ModInformation.IsClient) applyWithdrawals = false;
    }

    public static void ServerTickPostfix()
    {
        if (ModInformation.IsServer) FourberiePatchRuntime.Current?.PublishIfChanged();
    }

    internal static void ResetTickLedger() => TickLedger.Reset();

    private static bool SubmitEnlistment(TroopRoster roster, FourberieOperation operation)
    {
        if (agentEnlistSource != (operation == FourberieOperation.EnlistAgentsFromParty ? 1 : 2))
            return false;
        return FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
            operation,
            Settlement.CurrentSettlement,
            null,
            null,
            0,
            Selections(roster))) == true;
    }

    private static void SubmitBusiness(FourberieOperation operation, int businessKey) =>
        FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
            operation,
            null,
            null,
            null,
            businessKey,
            Array.Empty<FourberieLocalTroopSelection>()));

    private static bool TryBusinessKey(object[] arguments, out int businessKey)
    {
        businessKey = arguments != null && arguments.Length > 1 && arguments[1] is int value
            ? value
            : 0;
        return businessKey == 11 || businessKey == 12 || businessKey == 21 ||
               businessKey == 22 || businessKey == 31 || businessKey == 32;
    }

    private static bool TrySchemeSlot(object[] arguments, out int slot)
    {
        slot = arguments != null && arguments.Length > 0 && arguments[0] is int value ? value : 0;
        return slot == 7 || slot == 8;
    }

    private static FourberieLocalTroopSelection[] Selections(TroopRoster roster)
    {
        if (roster == null) return Array.Empty<FourberieLocalTroopSelection>();
        var selected = new List<FourberieLocalTroopSelection>();
        for (int index = 0; index < roster.Count; index++)
        {
            CharacterObject troop = roster.GetCharacterAtIndex(index);
            int count = roster.GetElementNumber(index);
            if (troop != null && count > 0) selected.Add(new FourberieLocalTroopSelection(troop, count));
        }
        return selected.ToArray();
    }

    private static FourberieLocalTroopSelection[] Difference(TroopRoster baseline, TroopRoster remaining)
    {
        if (baseline == null) return Array.Empty<FourberieLocalTroopSelection>();
        var selected = new List<FourberieLocalTroopSelection>();
        for (int index = 0; index < baseline.Count; index++)
        {
            CharacterObject troop = baseline.GetCharacterAtIndex(index);
            int count = baseline.GetElementNumber(index) - (remaining?.GetTroopCount(troop) ?? 0);
            if (troop != null && count > 0) selected.Add(new FourberieLocalTroopSelection(troop, count));
        }
        return selected.ToArray();
    }

    /// <summary>
    /// Produces a bounded canonical key from every stable object id and primitive/enum argument.
    /// The full argument position is retained, while the returned SHA-256 is fixed-size. This lets
    /// a duplicated listener invocation collapse without conflating, for example, two same-tick
    /// war events that share one faction but have different opponents.
    /// </summary>
    internal static string SubjectKey(IEnumerable<object> arguments)
    {
        if (arguments == null) return string.Empty;
        var canonical = new StringBuilder();
        var relevant = 0;
        var index = 0;
        foreach (var argument in arguments)
        {
            if (argument == null)
            {
                AppendToken(canonical, index, "null", string.Empty);
                relevant++;
            }
            else if (TryStableId(argument, out var stableId))
            {
                AppendToken(canonical, index, "id:" + argument.GetType().FullName, stableId);
                relevant++;
            }
            else if (TryPrimitive(argument, out var primitive))
            {
                AppendToken(canonical, index, "value:" + argument.GetType().FullName, primitive);
                relevant++;
            }
            index++;
        }

        return relevant == 0 ? string.Empty : Hash(canonical.ToString());
    }

    private static bool TryStableId(object argument, out string stableId)
    {
        stableId = null;
        try
        {
            var property = argument.GetType().GetProperty(
                "StringId",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.PropertyType != typeof(string) || property.GetIndexParameters().Length != 0)
                return false;
            stableId = property.GetValue(argument) as string;
            return !string.IsNullOrWhiteSpace(stableId);
        }
        catch
        {
            stableId = null;
            return false;
        }
    }

    private static bool TryPrimitive(object argument, out string value)
    {
        var type = argument.GetType();
        if (type.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(type);
            var numeric = Convert.ChangeType(argument, underlying, CultureInfo.InvariantCulture);
            value = Convert.ToString(numeric, CultureInfo.InvariantCulture);
            return true;
        }

        switch (Type.GetTypeCode(type))
        {
            case TypeCode.Boolean:
            case TypeCode.Byte:
            case TypeCode.SByte:
            case TypeCode.Int16:
            case TypeCode.UInt16:
            case TypeCode.Int32:
            case TypeCode.UInt32:
            case TypeCode.Int64:
            case TypeCode.UInt64:
            case TypeCode.Char:
            case TypeCode.Decimal:
                value = Convert.ToString(argument, CultureInfo.InvariantCulture);
                return true;
            case TypeCode.DateTime:
                var dateTime = (DateTime)argument;
                value = dateTime.Ticks.ToString(CultureInfo.InvariantCulture) + ":" +
                        ((int)dateTime.Kind).ToString(CultureInfo.InvariantCulture);
                return true;
            case TypeCode.Single:
                value = ((float)argument).ToString("R", CultureInfo.InvariantCulture);
                return true;
            case TypeCode.Double:
                value = ((double)argument).ToString("R", CultureInfo.InvariantCulture);
                return true;
            case TypeCode.String:
                value = (string)argument;
                return true;
            default:
                value = null;
                return false;
        }
    }

    private static void AppendToken(StringBuilder builder, int index, string kind, string value)
    {
        builder.Append(index.ToString(CultureInfo.InvariantCulture));
        builder.Append(':').Append(kind ?? string.Empty);
        builder.Append(':').Append(Hash(value ?? string.Empty)).Append(';');
    }

    private static string Hash(string value)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
        const string digits = "0123456789abcdef";
        var characters = new char[bytes.Length * 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            characters[index * 2] = digits[bytes[index] >> 4];
            characters[(index * 2) + 1] = digits[bytes[index] & 15];
        }
        return new string(characters);
    }

    private static string MethodKey(MethodBase method)
    {
        if (method == null) return "unknown";
        return method.DeclaringType?.FullName + "::" + method.Name + "(" +
               string.Join(",", method.GetParameters().Select(parameter =>
                   FourberieMethodSpec.CanonicalTypeName(parameter.ParameterType))) + ")";
    }
}
