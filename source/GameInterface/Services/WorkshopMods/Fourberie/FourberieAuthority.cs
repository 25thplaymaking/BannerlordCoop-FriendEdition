using Common;
using Common.Messaging;
using GameInterface.Policies;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal interface IFourberiePatchRuntime
{
    void NotifyUnsupported(string method);
    void PublishIfChanged();
}

internal static class FourberiePatchRuntime
{
    public static IFourberiePatchRuntime Current { get; set; }
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

    public static bool ServerOnlyPrefix() => ModInformation.IsServer;

    public static bool ClientPresentationPrefix() => ModInformation.IsClient;

    public static bool ServerTickPrefix(MethodBase __originalMethod, object[] __args)
    {
        if (!ModInformation.IsServer) return false;

        var campaign = Campaign.Current;
        var campaignId = campaign?.UniqueGameId ?? "no-campaign";
        var tick = campaign == null ? long.MinValue : CampaignTime.Now.NumTicks;
        return TickLedger.TryEnter(campaignId, MethodKey(__originalMethod), SubjectKey(__args), tick);
    }

    public static bool UnsupportedPlayerActionPrefix(MethodBase __originalMethod)
    {
        FourberiePatchRuntime.Current?.NotifyUnsupported(
            __originalMethod?.DeclaringType?.FullName + "." + (__originalMethod?.Name ?? "unknown"));
        return false;
    }

    /// <summary>
    /// Routes a Fourberie player create-action (static void M(int)) to the server. On a client we
    /// publish the intent (acting hero + method identity + the single int arg) and skip the local
    /// call so no MobileParty/TroopRoster is authored client-side. On the server the original runs,
    /// and its creations replicate through Coop's funnels. A replay (authoritative apply) is let
    /// through untouched. See FourberieRecruit{Messages,Interface,Handler}.cs.
    /// </summary>
    public static bool RoutedCreateActionPrefix(MethodBase __originalMethod, object[] __args)
    {
        if (CallOriginalPolicy.IsOriginalAllowed()) return true;
        if (ModInformation.IsServer) return true;

        var giver = Hero.MainHero;
        if (giver == null) return false; // no local hero to attribute the action to; drop it

        int arg = __args != null && __args.Length > 0 && __args[0] is int value ? value : 0;
        MessageBroker.Instance.Publish(null, new FourberieCreateActionAttempted(
            giver, __originalMethod?.DeclaringType?.FullName, __originalMethod?.Name, arg));
        return false;
    }

    private static MethodInfo _refreshHeroDico;
    private static bool _refreshHeroDicoResolved;

    /// <summary>
    /// Replaces Fourberie's <c>Main.OnGameInitializationFinished</c>. That method validates the 14
    /// game-model replacements — which now emit red "move Fourberie in load order" warnings because
    /// Coop suppresses those models — and then refreshes a client-local hero-name cache
    /// (<c>StringDicoHelper.RefreshHeroDico()</c>) the menus rely on. We run only the useful cache
    /// refresh and skip the validation spam. Resolved by reflection; inert if Fourberie is absent.
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

        return false; // skip the original's model-validation load-order spam
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
    public static bool InitializeBehaviorsOnlyPrefix(object[] __args)
    {
        var starter = __args != null && __args.Length > 0
            ? __args[0] as CampaignGameStarter
            : null;

        if (starter == null)
            throw new InvalidOperationException(
                "Fourberie behavior initialization had no CampaignGameStarter; refusing the unsafe original initializer.");

        var behaviors = PreflightBehaviors(
            FourberieBehaviorTypeNames,
            HarmonyLib.AccessTools.TypeByName);
        foreach (var behavior in behaviors)
        {
            starter.AddBehavior(behavior);
        }

        return false; // skip original: its AddModel<...> replacements are not registered
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
