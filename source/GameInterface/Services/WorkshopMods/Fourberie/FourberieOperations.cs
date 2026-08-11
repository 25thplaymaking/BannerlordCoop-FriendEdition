using Common;
using Common.Util;
using GameInterface.Policies;
using GameInterface.Services.Barters;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal sealed class FourberieOperationExecutor
{
    private const string BehaviorTypeName = "Fourberie.FourberieBehavior";
    private readonly Assembly assembly;
    private readonly IObjectManager objectManager;

    public FourberieOperationExecutor(Assembly assembly, IObjectManager objectManager)
    {
        this.assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        this.objectManager = objectManager ?? throw new ArgumentNullException(nameof(objectManager));
    }

    public bool TryExecute(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request,
        out string failure)
    {
        failure = null;
        if (actor == null || actorParty == null || request == null ||
            actor.PartyBelongedTo != actorParty || actorParty.LeaderHero != actor)
        {
            failure = "authenticated controller has no matching active party";
            return false;
        }

        if (!FourberieCanonicalState.TryCapture(
                assembly, objectManager, out var rollbackState, out _, out failure))
            return false;

        MobileParty sourceParty = null;
        Dictionary<CharacterObject, int> sourceCounts = null;
        Dictionary<CharacterObject, int> actorCounts = null;
        MobileParty previousCaravan = GetStaticField("_insucaraF") as MobileParty;
        MobileParty previousBandits = GetStaticField("_insubandF") as MobileParty;

        try
        {
            switch (request.Operation)
            {
                case FourberieOperation.EnlistAgentsFromParty:
                    sourceParty = actorParty;
                    sourceCounts = CaptureCounts(sourceParty.MemberRoster, request.Troops);
                    ApplyEnlistment(actorParty, sourceParty, request, requireCurrentSettlement: false);
                    break;
                case FourberieOperation.EnlistAgentsFromLads:
                    sourceParty = GetStaticField("_crimeBaseParty") as MobileParty;
                    if (sourceParty == null || !sourceParty.IsActive)
                        throw new InvalidOperationException("the Fourberie base party is unavailable");
                    sourceCounts = CaptureCounts(sourceParty.MemberRoster, request.Troops);
                    ApplyEnlistment(actorParty, sourceParty, request, requireCurrentSettlement: true);
                    break;
                case FourberieOperation.RecruitBandits:
                    actorCounts = CaptureCounts(actorParty.MemberRoster, request.Troops);
                    ApplyBanditRecruitment(actor, actorParty, request);
                    break;
                case FourberieOperation.StartInsuranceScam:
                    ApplyInsuranceScam(actor, actorParty, request);
                    break;
                default:
                    throw new InvalidOperationException("unknown Fourberie operation");
            }

            return true;
        }
        catch (Exception exception)
        {
            Exception reported = exception is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException
                : exception;
            var rollbackErrors = new List<string>();
            try { RestoreCounts(sourceParty?.MemberRoster, sourceCounts); }
            catch (Exception rollback) { rollbackErrors.Add("source roster: " + rollback.Message); }
            try { RestoreCounts(actorParty.MemberRoster, actorCounts); }
            catch (Exception rollback) { rollbackErrors.Add("actor roster: " + rollback.Message); }

            MobileParty createdBandits = GetStaticField("_insubandF") as MobileParty;
            MobileParty createdCaravan = GetStaticField("_insucaraF") as MobileParty;
            TryDestroyCreated(createdBandits, previousBandits, rollbackErrors);
            TryDestroyCreated(createdCaravan, previousCaravan, rollbackErrors);

            if (!FourberieCanonicalState.TryApply(assembly, objectManager, rollbackState, out var stateFailure))
                rollbackErrors.Add("canonical state: " + stateFailure);

            if (rollbackErrors.Count > 0)
                throw new InvalidOperationException(
                    "Fourberie operation rollback failed after " + reported.Message + ": " +
                    string.Join("; ", rollbackErrors), reported);

            failure = reported.GetType().Name + ": " + reported.Message;
            return false;
        }
    }

    private void ApplyEnlistment(
        MobileParty actorParty,
        MobileParty sourceParty,
        NetworkRequestFourberieOperation request,
        bool requireCurrentSettlement)
    {
        if (request.Troops.Length == 0)
            throw new InvalidOperationException("no troops were selected");
        if (requireCurrentSettlement && !TryResolveCurrentSettlement(actorParty, request.SettlementId, out _))
            throw new InvalidOperationException("the controller is no longer in the selected settlement");

        int low = 0;
        int middle = 0;
        int high = 0;
        foreach ((CharacterObject troop, int count) in ResolveTroops(request.Troops))
        {
            if ((int)troop.Occupation == 3 || (int)troop.Occupation == 16 || troop.Tier < 1 || troop.Tier > 6)
                throw new InvalidOperationException("selected troop is not eligible for agent training");
            if (sourceParty.MemberRoster.GetTroopCount(troop) < count)
                throw new InvalidOperationException("selected source roster changed before enlistment");
            if (troop.Tier < 3) low += count;
            else if (troop.Tier < 5) middle += count;
            else high += count;
        }

        using (new AllowedThread())
        {
            foreach ((CharacterObject troop, int count) in ResolveTroops(request.Troops))
                sourceParty.MemberRoster.AddToCounts(troop, -count, false, 0, 0, true, -1);
            IDictionary crime = GetDictionary("_crimeValue");
            Increment(crime, 301, low);
            Increment(crime, 311, middle);
            Increment(crime, 321, high);
        }
    }

    private void ApplyBanditRecruitment(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement) ||
            settlement.Culture == null)
            throw new InvalidOperationException("the selected bandit settlement is no longer current");
        int total = request.Troops.Sum(selection => selection.Count);
        if (request.IntValue <= 0 || total <= 0 || total > request.IntValue)
            throw new InvalidOperationException("bandit selection exceeds the confirmed maximum");
        if (actorParty.MemberRoster.TotalManCount + total > actorParty.Party.PartySizeLimit)
            throw new InvalidOperationException("the controller party has no room for the selected recruits");

        string settlementId = settlement.StringId;
        string cultureId = settlement.Culture.StringId;
        IDictionary availability = GetDictionary("_stringIntDico");
        IDictionary support = GetDictionary("_supportedBandits");
        if (ReadInt(availability, settlementId) < total)
            throw new InvalidOperationException("the settlement no longer has enough recruits");
        if (ReadInt(support, cultureId) < total * 5)
            throw new InvalidOperationException("the bandit faction no longer has enough strength");

        var resolved = ResolveTroops(request.Troops).ToArray();
        foreach ((CharacterObject troop, _) in resolved)
            if (!IsBanditRecruitEligible(troop, cultureId))
                throw new InvalidOperationException("selected troop is not valid for this bandit culture");

        MethodInfo diplomacy = RequiredMethod(
            "Fourberie.FourbBanditBehavior",
            "BanditsDiploLogic",
            parameterCount: 8);
        using (new BarterPlayerContext(actor, actorParty))
        using (new AllowedThread())
        {
            foreach ((CharacterObject troop, int count) in resolved)
                actorParty.MemberRoster.AddToCounts(troop, count, false, 0, 0, true, -1);
            availability[settlementId] = ReadInt(availability, settlementId) - total;
            diplomacy.Invoke(null, new object[]
            {
                cultureId,
                settlement.Culture.Name?.ToString() ?? cultureId,
                -(total * 5),
                false,
                0,
                false,
                false,
                null,
            });
            actor.AddSkillXp(DefaultSkills.Roguery, total);
            actor.AddSkillXp(DefaultSkills.Tactics, total);
        }
    }

    private void ApplyInsuranceScam(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        if (GetStaticField("_insucaraF") is MobileParty activeCaravan && activeCaravan.IsActive ||
            GetStaticField("_insubandF") is MobileParty activeBandits && activeBandits.IsActive)
            throw new InvalidOperationException("an insurance scam is already active");
        if (!TryResolveCurrentSettlement(actorParty, request.SettlementId, out Settlement settlement) ||
            !objectManager.TryGetObject(request.TargetId, out Hero merchant) ||
            !objectManager.TryGetObject(request.SecondaryTargetId, out Settlement destination) ||
            merchant == null || destination == null || merchant.CurrentSettlement != settlement ||
            destination == settlement || !destination.IsTown)
            throw new InvalidOperationException("the insurance-scam context is stale or invalid");

        MethodInfo spawnCaravan = RequiredMethod("Fourberie.HelperSubInsuScam", "SpawnCaravan", 2);
        MethodInfo spawnBandits = RequiredMethod("Fourberie.HelperSubInsuScam", "SpawnBandits", 1);
        MethodInfo traitXp = RequiredMethod("Fourberie.VanillaHelperFourb", "AddPlayerTraitXPAndLogEntry", 4);
        int partyCount = Math.Min(actorParty.MemberRoster.TotalHealthyCount, MBRandom.RandomInt(73, 83));
        if (partyCount <= 0) throw new InvalidOperationException("the controller party has no healthy escort force");

        using (new BarterPlayerContext(actor, actorParty))
        using (new AllowedThread())
        {
            Type noteType = traitXp.GetParameters()[2].ParameterType;
            object note = Enum.ToObject(noteType, 0);
            traitXp.Invoke(null, new object[] { DefaultTraits.Honor, -5, note, actor });
            traitXp.Invoke(null, new object[] { DefaultTraits.Mercy, -5, note, actor });
            spawnCaravan.Invoke(null, new object[] { merchant, destination });
            spawnBandits.Invoke(null, new object[] { partyCount });

            IDictionary crime = GetDictionary("_crimeValue");
            crime[91] = actor.MapFaction != null && settlement.MapFaction != null &&
                        actor.MapFaction.IsAtWarWith(settlement.MapFaction) ? 1 : 2;
            GetDictionary("_stringHeroIdDico")["insuScamMerchant"] = merchant.StringId;
            GetDictionary("_townInsuScamTiming")[settlement.StringId] = CampaignTime.Now;
        }
    }

    private IEnumerable<(CharacterObject Troop, int Count)> ResolveTroops(
        IEnumerable<FourberieTroopSelection> selections)
    {
        foreach (FourberieTroopSelection selection in selections)
        {
            if (!objectManager.TryGetObject(selection.TroopId, out CharacterObject troop) || troop == null)
                throw new InvalidOperationException("selected troop no longer exists");
            yield return (troop, selection.Count);
        }
    }

    private bool IsBanditRecruitEligible(CharacterObject troop, string cultureId)
    {
        if (troop.Culture?.StringId != cultureId) return false;
        if (troop.Tier == 2) return true;

        Type listType = assembly.GetType("Fourberie.ListHelper", throwOnError: true, ignoreCase: false);
        if (AccessTools.Field(listType, "_troopList")?.GetValue(null) is not IEnumerable list) return false;
        foreach (object entry in list)
        {
            Type tupleType = entry?.GetType();
            string item1 = tupleType?.GetField("m_Item1", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(entry) as string ??
                           tupleType?.GetProperty("Item1")?.GetValue(entry) as string;
            string item2 = tupleType?.GetField("m_Item2", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(entry) as string ??
                           tupleType?.GetProperty("Item2")?.GetValue(entry) as string;
            if (item1 == cultureId && item2 == troop.StringId) return true;
        }
        return false;
    }

    private bool TryResolveCurrentSettlement(
        MobileParty actorParty,
        string settlementId,
        out Settlement settlement)
    {
        settlement = null;
        return !string.IsNullOrEmpty(settlementId) &&
               objectManager.TryGetObject(settlementId, out settlement) && settlement != null &&
               actorParty.CurrentSettlement == settlement;
    }

    private Dictionary<CharacterObject, int> CaptureCounts(
        TroopRoster roster,
        IEnumerable<FourberieTroopSelection> selections)
    {
        if (roster == null) return null;
        return ResolveTroops(selections).ToDictionary(value => value.Troop, value => roster.GetTroopCount(value.Troop));
    }

    private static void RestoreCounts(TroopRoster roster, Dictionary<CharacterObject, int> counts)
    {
        if (roster == null || counts == null) return;
        using (new AllowedThread())
            foreach (var pair in counts)
                roster.AddToCounts(pair.Key, pair.Value - roster.GetTroopCount(pair.Key), false, 0, 0, true, -1);
    }

    private static void TryDestroyCreated(
        MobileParty candidate,
        MobileParty previous,
        ICollection<string> errors)
    {
        if (candidate == null || ReferenceEquals(candidate, previous) || !candidate.IsActive) return;
        try
        {
            using (new AllowedThread()) DestroyPartyAction.Apply(null, candidate);
        }
        catch (Exception exception)
        {
            errors.Add("created party " + candidate.StringId + ": " + exception.Message);
        }
    }

    private MethodInfo RequiredMethod(string typeName, string methodName, int parameterCount)
    {
        Type type = assembly.GetType(typeName, throwOnError: true, ignoreCase: false);
        MethodInfo method = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name == methodName &&
                                          candidate.GetParameters().Length == parameterCount);
        return method ?? throw new MissingMethodException(typeName, methodName);
    }

    private object GetStaticField(string fieldName)
    {
        Type type = assembly.GetType(BehaviorTypeName, throwOnError: true, ignoreCase: false);
        FieldInfo field = AccessTools.Field(type, fieldName) ??
                          throw new MissingFieldException(BehaviorTypeName, fieldName);
        return field.GetValue(null);
    }

    private IDictionary GetDictionary(string fieldName) =>
        GetStaticField(fieldName) as IDictionary ??
        throw new InvalidOperationException("Fourberie field " + fieldName + " is not a dictionary");

    private static int ReadInt(IDictionary dictionary, object key) =>
        dictionary.Contains(key) ? Convert.ToInt32(dictionary[key]) : 0;

    private static void Increment(IDictionary dictionary, object key, int change) =>
        dictionary[key] = ReadInt(dictionary, key) + change;
}

internal enum FourberieReplayDecision
{
    New,
    Replay,
    Conflict,
}

internal sealed class FourberieRequestLedger<TKey>
{
    private sealed class Entry
    {
        public Entry(string key, NetworkFourberieOperationResult result)
        {
            Key = key;
            Result = result;
        }

        public string Key { get; }
        public NetworkFourberieOperationResult Result { get; }
    }

    private readonly object sync = new object();
    private readonly Dictionary<TKey, Dictionary<long, Entry>> entries = new Dictionary<TKey, Dictionary<long, Entry>>();
    private readonly int capacity;

    public FourberieRequestLedger(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public FourberieReplayDecision Inspect(
        TKey peer,
        long requestId,
        string key,
        out NetworkFourberieOperationResult result)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(peer, out var peerEntries) ||
                !peerEntries.TryGetValue(requestId, out Entry entry))
            {
                result = null;
                return FourberieReplayDecision.New;
            }

            result = entry.Result;
            return string.Equals(entry.Key, key, StringComparison.Ordinal)
                ? FourberieReplayDecision.Replay
                : FourberieReplayDecision.Conflict;
        }
    }

    public void Record(TKey peer, long requestId, string key, NetworkFourberieOperationResult result)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(peer, out var peerEntries))
            {
                peerEntries = new Dictionary<long, Entry>();
                entries.Add(peer, peerEntries);
            }
            if (peerEntries.ContainsKey(requestId)) return;
            if (peerEntries.Count >= capacity)
                peerEntries.Remove(peerEntries.Keys.Min());
            peerEntries.Add(requestId, new Entry(key, result));
        }
    }

    public void Reset()
    {
        lock (sync) entries.Clear();
    }
}

internal static class FourberieCapabilityPolicy
{
    public static bool IsEnabled(bool optionEnabled, bool routeReady) => optionEnabled && routeReady;
}

internal sealed class FourberieCapabilitySource : Core.IWorkshopCapabilitySource
{
    internal const string ModuleId = "Fourberie";
    internal const string Operation = "Gameplay";

    private readonly Configuration.IModConfig modConfig;

    public FourberieCapabilitySource(Configuration.IModConfig modConfig)
    {
        this.modConfig = modConfig;
    }

    public IEnumerable<Core.WorkshopCapability> CaptureCapabilities()
    {
        var options = modConfig.Data == null
            ? Configuration.ModConfigProvider.ModOptions
            : new Configuration.ModOptions(modConfig.Data.ModOptions ?? new Configuration.ModOptionsData());
        bool enabled = FourberieCapabilityPolicy.IsEnabled(
            options.IsWorkshopModuleEnabled(ModuleId),
            FourberiePatchRuntime.Current != null);
        yield return new Core.WorkshopCapability(
            ModuleId,
            Operation,
            enabled,
            enabled ? string.Empty : "Fourberie is disabled or its authoritative gameplay route is unavailable.");
    }
}
