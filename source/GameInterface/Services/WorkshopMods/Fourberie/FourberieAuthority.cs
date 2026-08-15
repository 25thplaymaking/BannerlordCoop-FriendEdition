using Common;
using Common.Messaging;
using Common.Util;
using GameInterface.Configuration;
using GameInterface.Services.Alleys.Messages;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Inventory;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal interface IFourberiePatchRuntime
{
    void PublishIfChanged();
    bool TrySubmit(FourberieLocalOperation operation);
    void RunContractTick();
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

internal sealed class FourberieLocalItemSelection
{
    public FourberieLocalItemSelection(EquipmentElement equipmentElement, int deltaToSafehouse)
    {
        EquipmentElement = equipmentElement;
        DeltaToSafehouse = deltaToSafehouse;
    }

    public EquipmentElement EquipmentElement { get; }
    public int DeltaToSafehouse { get; }
}

internal sealed class FourberieLocalRosterSelection
{
    public FourberieLocalRosterSelection(
        CharacterObject troop,
        int memberDeltaToActor,
        int prisonerDeltaToActor)
    {
        Troop = troop;
        MemberDeltaToActor = memberDeltaToActor;
        PrisonerDeltaToActor = prisonerDeltaToActor;
    }

    public CharacterObject Troop { get; }
    public int MemberDeltaToActor { get; }
    public int PrisonerDeltaToActor { get; }
}

internal sealed class FourberieLocalOperation
{
    public FourberieLocalOperation(
        FourberieOperation operation,
        Settlement settlement,
        Hero targetHero,
        Settlement secondarySettlement,
        int intValue,
        FourberieLocalTroopSelection[] troops,
        Clan targetClan = null,
        FourberieLocalItemSelection[] items = null,
        object targetObject = null,
        object[] targetObjects = null,
        string secondaryId = null,
        FourberieLocalRosterSelection[] roster = null)
    {
        Operation = operation;
        Settlement = settlement;
        TargetHero = targetHero;
        SecondarySettlement = secondarySettlement;
        IntValue = intValue;
        Troops = troops ?? Array.Empty<FourberieLocalTroopSelection>();
        TargetClan = targetClan;
        Items = items ?? Array.Empty<FourberieLocalItemSelection>();
        TargetObject = targetObject;
        TargetObjects = targetObjects ?? Array.Empty<object>();
        SecondaryId = secondaryId;
        Roster = roster ?? Array.Empty<FourberieLocalRosterSelection>();
    }

    public FourberieOperation Operation { get; }
    public Settlement Settlement { get; }
    public Hero TargetHero { get; }
    public Settlement SecondarySettlement { get; }
    public int IntValue { get; }
    public FourberieLocalTroopSelection[] Troops { get; }
    public Clan TargetClan { get; }
    public FourberieLocalItemSelection[] Items { get; }
    public object TargetObject { get; }
    public object[] TargetObjects { get; }
    public string SecondaryId { get; }
    public FourberieLocalRosterSelection[] Roster { get; }
}

internal sealed class FourberiePresentationState
{
    private readonly Dictionary<FieldInfo, object> fields;

    private FourberiePresentationState(Dictionary<FieldInfo, object> fields) => this.fields = fields;

    public static FourberiePresentationState Capture(Type behavior = null)
    {
        behavior ??= AccessTools.TypeByName("Fourberie.FourberieBehavior");
        if (behavior == null) return null;
        var values = new Dictionary<FieldInfo, object>();
        foreach (FourberieStateFieldSpec spec in FourberieCanonicalState.Fields)
        {
            FieldInfo field = AccessTools.Field(behavior, spec.FieldName);
            if (field == null) continue;
            values[field] = Clone(field.GetValue(null));
        }
        return new FourberiePresentationState(values);
    }

    public void Restore()
    {
        if (fields == null) return;
        using (new AllowedThread())
            foreach (var pair in fields) pair.Key.SetValue(null, pair.Value);
    }

    private static object Clone(object value)
    {
        if (value is ItemRoster roster)
        {
            var copy = new ItemRoster();
            for (int index = 0; index < roster.Count; index++)
                copy.AddToCounts(roster.GetElementCopyAtIndex(index).EquipmentElement,
                    roster.GetElementCopyAtIndex(index).Amount);
            return copy;
        }
        if (value is IDictionary dictionary)
        {
            var copy = Activator.CreateInstance(value.GetType()) as IDictionary;
            foreach (DictionaryEntry item in dictionary) copy?.Add(item.Key, item.Value);
            return copy;
        }
        if (value is IList list)
        {
            var copy = Activator.CreateInstance(value.GetType()) as IList;
            foreach (object item in list) copy?.Add(item);
            return copy;
        }
        return value;
    }
}

internal sealed class FourberieLegacyExecutionContext : IDisposable
{
    [ThreadStatic] private static int depth;
    public static bool Active => depth > 0;
    public FourberieLegacyExecutionContext() => depth++;
    public void Dispose() => depth = Math.Max(0, depth - 1);
}

/// <summary>
/// Marks the stash opened from Fourberie's safehouse dialog and converts the temporary inventory
/// screen transaction into a stable-ID server operation. The inventory screen works on transient
/// rosters, so routing it through the generic TradeAttempted path cannot identify the safehouse.
/// </summary>
internal static class FourberieSafehouseTransferContext
{
    private enum StashKind { None, Safehouse, BanditLoot }
    [ThreadStatic] private static StashKind active;

    public static void Begin(ItemRoster stash)
    {
        active = StashKind.None;
        if (!ModInformation.IsClient || stash == null) return;
        if (ReferenceEquals(stash, CurrentCrimeBaseParty()?.ItemRoster)) active = StashKind.Safehouse;
        else if (ReferenceEquals(stash, CurrentBanditStash())) active = StashKind.BanditLoot;
    }

    public static bool TryHandleDone(InventoryLogic logic, out bool result)
    {
        result = false;
        if (active == StashKind.None || !ModInformation.IsClient || logic == null) return false;
        StashKind kind = active;
        active = StashKind.None;

        FourberieLocalItemSelection[] items = BuildSelections(
            logic.GetBoughtItems(),
            logic.GetSoldItems());

        // Undo the speculative UI copies before the server's authoritative roster deltas arrive.
        using (new AllowedThread()) logic.Reset(true);

        if (items.Length == 0)
        {
            result = true;
            return true;
        }

        bool submitted = FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
            kind == StashKind.Safehouse
                ? FourberieOperation.TransferSafehouseItems
                : FourberieOperation.CommitBanditEvent,
            kind == StashKind.Safehouse ? CurrentCrimeBase() : Settlement.CurrentSettlement,
            null,
            null,
            kind == StashKind.Safehouse ? 0 : (int)FourberieBanditEvent.DonateLoot,
            Array.Empty<FourberieLocalTroopSelection>(),
            items: items)) == true;
        if (!submitted) ShowUnavailable();
        result = submitted;
        return true;
    }

    public static void Cancel() => active = StashKind.None;

    internal static FourberieLocalItemSelection[] BuildSelections(
        IEnumerable<(ItemRosterElement, int)> bought,
        IEnumerable<(ItemRosterElement, int)> sold)
    {
        var result = new List<FourberieLocalItemSelection>();
        Accumulate(result, bought, direction: -1);
        Accumulate(result, sold, direction: 1);
        return result.Where(item => item.DeltaToSafehouse != 0).ToArray();
    }

    internal static Settlement CurrentCrimeBase()
    {
        Type behavior = CurrentBehaviorType();
        return behavior == null
            ? null
            : AccessTools.Field(behavior, "_crimeBase")?.GetValue(null) as Settlement;
    }

    internal static void ShowUnavailable() =>
        InformationManager.DisplayMessage(new InformationMessage(
            "The co-op server could not verify this Fourberie safehouse action. Reopen the safehouse and try again."));

    private static MobileParty CurrentCrimeBaseParty()
    {
        Type behavior = CurrentBehaviorType();
        return behavior == null
            ? null
            : AccessTools.Field(behavior, "_crimeBaseParty")?.GetValue(null) as MobileParty;
    }

    private static ItemRoster CurrentBanditStash()
    {
        Type behavior = CurrentBehaviorType();
        return behavior == null ? null : AccessTools.Field(behavior, "_stash")?.GetValue(null) as ItemRoster;
    }

    private static Type CurrentBehaviorType()
    {
        Type[] candidates = AppDomain.CurrentDomain.GetAssemblies()
            .Select(candidate => candidate.GetType(
                "Fourberie.FourberieBehavior",
                throwOnError: false,
                ignoreCase: false))
            .Where(candidate => candidate != null)
            .ToArray();
        return candidates.FirstOrDefault(candidate =>
                   AccessTools.Field(candidate, "_crimeBase")?.GetValue(null) != null ||
                   AccessTools.Field(candidate, "_crimeBaseParty")?.GetValue(null) != null) ??
               candidates.FirstOrDefault();
    }

    private static void Accumulate(
        IList<FourberieLocalItemSelection> selections,
        IEnumerable<(ItemRosterElement Element, int Price)> exchanges,
        int direction)
    {
        if (exchanges == null) return;
        foreach (var exchange in exchanges)
        {
            EquipmentElement equipment = exchange.Element.EquipmentElement;
            if (equipment.Item == null || exchange.Element.Amount <= 0) continue;
            int delta = checked(exchange.Element.Amount * direction);
            int index = -1;
            for (int candidate = 0; candidate < selections.Count; candidate++)
            {
                if (selections[candidate].EquipmentElement.Equals(equipment))
                {
                    index = candidate;
                    break;
                }
            }

            if (index < 0)
            {
                selections.Add(new FourberieLocalItemSelection(equipment, delta));
                continue;
            }

            var existing = selections[index];
            selections[index] = new FourberieLocalItemSelection(
                equipment,
                checked(existing.DeltaToSafehouse + delta));
        }
    }
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
    private static Settlement pendingTerritoryAbandonment;
    private static Settlement banditRecruitmentSettlement;
    private static int banditRecruitmentMaximum;
    private static MobileParty banditRosterTarget;
    private static int banditRosterRecruitment;
    [ThreadStatic] private static int deferredBanditPresentationToken;
    private sealed class FightClubBaseline { public int Fame; }
    private sealed class FightClubAdmission { public bool RandomWeapon; }
    private sealed class StealthMissionState
    {
        public bool AlertSubmitted;
        public readonly HashSet<string> WoundedHeroes = new HashSet<string>(StringComparer.Ordinal);
    }

    private static readonly ConditionalWeakTable<object, FightClubBaseline> FightClubBaselines =
        new ConditionalWeakTable<object, FightClubBaseline>();
    private static readonly ConditionalWeakTable<object, FightClubAdmission> FightClubAdmissions =
        new ConditionalWeakTable<object, FightClubAdmission>();
    private static readonly ConditionalWeakTable<object, StealthMissionState> StealthMissions =
        new ConditionalWeakTable<object, StealthMissionState>();

    public static bool ServerOnlyPrefix() => ModInformation.IsServer;

    public static bool ClientPresentationPrefix(ref FourberiePresentationState __state)
    {
        if (!ModInformation.IsClient) return false;
        __state = FourberiePresentationState.Capture();
        return true;
    }

    public static void ClientPresentationPostfix(FourberiePresentationState __state)
    {
        if (ModInformation.IsClient) __state?.Restore();
    }

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

    public static bool EnslavePrisonersConsequencePrefix(TroopRoster leftPrisonRoster, ref bool __result)
    {
        if (!ModInformation.IsClient) return true;

        bool submitted = FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
            FourberieOperation.EnslavePrisoners,
            FourberieSafehouseTransferContext.CurrentCrimeBase(),
            null,
            null,
            0,
            Selections(leftPrisonRoster))) == true;
        if (!submitted) FourberieSafehouseTransferContext.ShowUnavailable();
        FourberiePartyCommitSuppression.Request();
        __result = submitted;
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
        bool submitted = FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
            FourberieOperation.StartInsuranceScam,
            settlement,
            merchant,
            destination,
            0,
            Array.Empty<FourberieLocalTroopSelection>())) == true;
        // Tell the player when the route is unavailable instead of the button silently doing nothing,
        // matching EnslavePrisoners / the safehouse item transfer.
        if (!submitted) FourberieSafehouseTransferContext.ShowUnavailable();
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

    public static bool RoleAssignmentConsequencePrefix(object __instance, object[] __args)
    {
        if (!ModInformation.IsClient) return false;
        string role = __instance == null
            ? null
            : HarmonyLib.AccessTools.Field(__instance.GetType(), "role")?.GetValue(__instance) as string;
        Hero hero = (__args != null && __args.Length > 0
                ? __args[0] as IEnumerable<InquiryElement>
                : null)?
            .Select(element => element?.Identifier as Hero)
            .FirstOrDefault(value => value != null);
        int roleCode = FourberieRoleAuthority.RoleCode(role);
        if (hero != null && roleCode != 0)
        {
            InformationManager.HideInquiry();
            FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
                FourberieOperation.AssignCriminalRole,
                null,
                hero,
                null,
                roleCode,
                Array.Empty<FourberieLocalTroopSelection>()));
        }
        return false;
    }

    public static bool RoleRemovalConsequencePrefix(object[] __args)
    {
        if (ModInformation.IsClient)
        {
            string role = __args != null && __args.Length > 0 ? __args[0] as string : null;
            int roleCode = FourberieRoleAuthority.RoleCode(role);
            if (roleCode != 0) SubmitBusiness(FourberieOperation.RemoveCriminalRole, roleCode);
        }
        return false;
    }

    public static bool SchemeVictimConsequencePrefix(MethodBase __originalMethod, object[] __args)
    {
        if (!ModInformation.IsClient) return false;
        string typeName = __originalMethod?.DeclaringType?.FullName;
        int slot = typeName == "Fourberie.CriminalVM+<>c__DisplayClass137_0" ? 7 :
            typeName == "Fourberie.CriminalVM+<>c__DisplayClass138_0" ? 8 : 0;
        Hero target = SelectedInquiryIdentifier<Hero>(__args);
        if (slot != 0 && target != null)
        {
            InformationManager.HideInquiry();
            SubmitScheme(FourberieOperation.SelectSchemeVictim, slot, target);
        }
        return false;
    }

    public static bool SchemeTypeConsequencePrefix(object __instance, object[] __args)
    {
        if (!ModInformation.IsClient || __instance == null) return false;
        object captured = AccessTools.Field(__instance.GetType(), "schr")?.GetValue(__instance);
        int slot = captured is int value ? value : 0;
        int scheme = SelectedInquiryIdentifier<int>(__args);
        if (FourberieSchemeAuthority.IsSlot(slot) && scheme >= 1 && scheme <= 8)
        {
            InformationManager.HideInquiry();
            SubmitBusiness(FourberieOperation.SelectSchemeType, slot * 10 + scheme);
        }
        return false;
    }

    public static bool SchemeLifecycleConsequencePrefix(object[] __args)
    {
        if (!ModInformation.IsClient) return false;
        int slot = __args != null && __args.Length > 0 && __args[0] is int value ? value : 0;
        if (!FourberieSchemeAuthority.IsSlot(slot)) return false;

        FourberieOperation operation = SchemeLifecycleOperation(slot);
        if (operation == FourberieOperation.AbortScheme)
        {
            InformationManager.ShowInquiry(new InquiryData(
                "Abort the scheme",
                "Stop everything? Spent resources will not be refunded.",
                true,
                true,
                "Okay",
                "Wait a second!",
                () => SubmitBusiness(FourberieOperation.AbortScheme, slot),
                null));
        }
        else
        {
            SubmitBusiness(operation, slot);
        }
        return false;
    }

    public static bool SchemeOwnedReplacementPrefix() => false;

    public static bool SchemeStanceConsequencePrefix(object[] __args)
    {
        if (!ModInformation.IsClient) return false;
        string selected = SelectedInquiryIdentifier<string>(__args);
        if (!int.TryParse(selected, out int stance) || (stance != 1 && stance != 2)) return false;

        Type behavior = AccessTools.TypeByName("Fourberie.FourberieBehavior");
        AccessTools.Field(behavior, "_clanval")?.SetValue(null, null);
        AccessTools.Field(behavior, "_clanval2")?.SetValue(null, null);
        InformationManager.HideInquiry();
        SubmitBusiness(FourberieOperation.ChangeSchemeStance, stance);
        return false;
    }

    public static void ClientRoleRefreshPrefix(ref FourberieRoleSnapshot __state)
    {
        if (ModInformation.IsClient)
            __state = FourberieRoleAuthority.Capture(FourberieRoles());
    }

    public static void ClientRoleRefreshPostfix(FourberieRoleSnapshot __state)
    {
        if (ModInformation.IsClient) __state?.Restore(FourberieRoles());
    }

    public static bool CorruptionLevelConsequencePrefix(object[] __args)
    {
        if (ModInformation.IsClient)
        {
            int level = CorruptionLevelForSelection(SelectedInquiryIdentifier<string>(__args));
            if (level != 0)
            {
                InformationManager.HideInquiry();
                SubmitBusiness(FourberieOperation.SetCorruptionLevel, level);
            }
        }
        return false;
    }

    internal static int CorruptionLevelForSelection(string selection) => selection switch
    {
        "1" => 1,
        "2" => 2,
        "3" => 3,
        "10" => 10,
        _ => 0,
    };

    public static bool CrimeRoomSliderConsequencePrefix(MethodBase __originalMethod, object[] __args)
    {
        if (ModInformation.IsClient && __args?.Length > 0 && __args[0] is int value)
        {
            FourberieOperation? operation = CrimeRoomSliderOperation(__originalMethod?.Name);
            if (operation.HasValue) SubmitBusiness(operation.Value, value);
        }
        return false;
    }

    internal static FourberieOperation? CrimeRoomSliderOperation(string methodName) => methodName switch
    {
        "set_UpgradeSlideBar" => FourberieOperation.SetAutoInvestment,
        "set_LadsDutySlideBar" => FourberieOperation.SetLadsDuty,
        "set_SlavesDutySlideBar" => FourberieOperation.SetSlavesDuty,
        _ => null,
    };

    public static void ClientCrimeRoomReadPrefix(ref FourberieCrimeRoomSnapshot __state)
    {
        if (ModInformation.IsClient)
            __state = FourberieCrimeRoomAuthority.CaptureReadState(FourberieCrimeValues());
    }

    public static void ClientCrimeRoomReadPostfix(FourberieCrimeRoomSnapshot __state)
    {
        if (ModInformation.IsClient) __state?.Restore(FourberieCrimeValues());
    }

    public static bool ContractConsequencePrefix(MethodBase __originalMethod)
    {
        if (ModInformation.IsClient)
        {
            FourberieOperation? operation = ContractOperationForMethod(__originalMethod?.Name);
            if (operation.HasValue) SubmitBusiness(operation.Value, 0);
        }
        return false;
    }

    internal static FourberieOperation? ContractOperationForMethod(string methodName) => methodName switch
    {
        "<FContractCom>b__151_0" => FourberieOperation.EnableContractOffers,
        "<FContractCom>b__151_3" => FourberieOperation.DisableContractOffers,
        "<FContractCom>b__151_5" => FourberieOperation.AbortContract,
        _ => null,
    };

    public static void ClientSchemeFilterPrefix(ref FourberieSchemeSelectionSnapshot __state)
    {
        if (ModInformation.IsClient)
            __state = FourberieSchemeAuthority.CaptureSelection(
                FourberieCrimeValues(), FourberieRoles(), FourberieCampaignTimes());
    }

    public static void ClientSchemeFilterPostfix(FourberieSchemeSelectionSnapshot __state)
    {
        if (!ModInformation.IsClient) return;
        __state?.Restore(FourberieCrimeValues(), FourberieRoles(), FourberieCampaignTimes());

        try
        {
            Type behavior = AccessTools.TypeByName("Fourberie.FourberieBehavior");
            object layer = behavior == null ? null : AccessTools.Field(behavior, "_layer")?.GetValue(null);
            if (layer != null) AccessTools.Method(layer.GetType(), "UpdateLayout", Type.EmptyTypes)?.Invoke(layer, null);
        }
        catch
        {
            // The canonical values are already restored; a closed/replaced presentation layer is harmless.
        }
    }

    public static bool MainBaseConsequencePrefix(object[] __args)
    {
        if (ModInformation.IsClient)
        {
            Settlement settlement = SelectedInquiryIdentifier<Settlement>(__args);
            if (settlement != null)
            {
                InformationManager.HideInquiry();
                SubmitSettlement(FourberieOperation.SetMainCrimeBase, settlement);
            }
        }
        return false;
    }

    public static bool TerritorySelectionConsequencePrefix(object[] __args)
    {
        if (!ModInformation.IsClient) return false;
        Settlement selected = SelectedInquiryIdentifier<Settlement>(__args);
        if (selected == null) return false;

        Type behavior = AccessTools.TypeByName("Fourberie.FourberieBehavior");
        Settlement currentBase = behavior == null
            ? null
            : AccessTools.Field(behavior, "_crimeBase")?.GetValue(null) as Settlement;
        if (selected == currentBase)
        {
            pendingTerritoryAbandonment = selected;
            return true;
        }

        pendingTerritoryAbandonment = null;
        InformationManager.HideInquiry();
        SubmitSettlement(FourberieOperation.RemoveTerritory, selected);
        return false;
    }

    public static bool TerritoryAbandonConsequencePrefix()
    {
        if (ModInformation.IsClient && pendingTerritoryAbandonment != null)
            SubmitSettlement(FourberieOperation.AbandonTownCrimeBase, pendingTerritoryAbandonment);
        pendingTerritoryAbandonment = null;
        return false;
    }

    public static bool SafehouseAbandonConsequencePrefix(object __instance)
    {
        if (ModInformation.IsClient && __instance != null)
        {
            Settlement settlement = AccessTools.Field(__instance.GetType(), "setCur")?.GetValue(__instance) as Settlement;
            if (settlement != null) SubmitSettlement(FourberieOperation.AbandonSafehouse, settlement);
        }
        return false;
    }

    public static bool SafehouseEstablishmentConsequencePrefix(MethodBase __originalMethod)
    {
        if (ModInformation.IsClient)
        {
            AccessTools.Field(__originalMethod?.DeclaringType, "_dialogActive")?.SetValue(null, 0);
            Settlement settlement = Settlement.CurrentSettlement;
            if (settlement != null) SubmitSettlement(FourberieOperation.EstablishSafehouse, settlement);
        }
        return false;
    }

    public static bool SafehouseTraderConsequencePrefix(MethodBase __originalMethod)
    {
        if (ModInformation.IsClient)
        {
            FourberieOperation? operation = SafehouseTraderOperationForMethod(__originalMethod?.Name);
            Settlement settlement = Settlement.CurrentSettlement;
            if (operation.HasValue && settlement != null)
                SubmitSettlement(operation.Value, settlement);
        }
        return false;
    }

    internal static FourberieOperation? SafehouseTraderOperationForMethod(string methodName) => methodName switch
    {
        "<AddDialogsSafeHouse>b__9_18" => FourberieOperation.SellQuarterSlaves,
        "<AddDialogsSafeHouse>b__9_20" => FourberieOperation.SellHalfSlaves,
        "<AddDialogsSafeHouse>b__9_22" => FourberieOperation.DeclineCrookedTrader,
        "<AddDialogsSafeHouse>b__9_24" => FourberieOperation.RobCrookedTrader,
        _ => null,
    };

    public static bool SafehouseWaitConsequencePrefix(MethodBase __originalMethod)
    {
        if (ModInformation.IsClient)
        {
            FourberieOperation? operation = SafehouseWaitOperationForMethod(__originalMethod?.Name);
            Settlement settlement = Settlement.CurrentSettlement;
            if (operation.HasValue && settlement != null)
                SubmitSettlement(operation.Value, settlement);
        }
        return false;
    }

    internal static FourberieOperation? SafehouseWaitOperationForMethod(string methodName) => methodName switch
    {
        "<MenuSafeHouse>b__13_5" => FourberieOperation.StartSafehouseWait,
        "<MenuSafeHouse>b__13_7" => FourberieOperation.StopSafehouseWait,
        _ => null,
    };

    public static bool SafehouseReturnLifecyclePrefix()
    {
        if (!ModInformation.IsClient) return false;

        Type behavior = AccessTools.TypeByName("Fourberie.FourberieBehavior");
        Type safehouseBehavior = AccessTools.TypeByName("Fourberie.FourbSafeHouseBehavior");
        IDictionary crime = behavior == null
            ? null
            : AccessTools.Field(behavior, "_crimeValue")?.GetValue(null) as IDictionary;
        object safehouse = safehouseBehavior == null
            ? null
            : AccessTools.Field(safehouseBehavior, "_safehouse")?.GetValue(null);
        Settlement settlement = Settlement.CurrentSettlement;
        if (safehouse != null && settlement != null && FourberieSafehouseReturnAuthority.HasPendingReturn(crime))
            SubmitSettlement(FourberieOperation.CompleteSafehouseReturn, settlement);
        return false;
    }

    public static bool GrudgeSelectionConsequencePrefix(object[] __args)
    {
        if (ModInformation.IsClient)
        {
            Clan clan = SelectedInquiryIdentifier<Clan>(__args);
            if (clan != null)
            {
                InformationManager.HideInquiry();
                SubmitClan(FourberieOperation.RequestGrudgeQuote, clan, 0);
            }
        }
        return false;
    }

    public static bool GrudgeSettlementConsequencePrefix() => false;

    public static bool ContractTickReplacementPrefix()
    {
        if (ModInformation.IsServer) FourberiePatchRuntime.Current?.RunContractTick();
        return false;
    }

    public static bool ContractProposalLegacyConsequencePrefix() => false;

    public static bool InsideMissionOutcomePrefix(MethodBase __originalMethod, object[] __args)
    {
        if (!ModInformation.IsClient) return false;

        FourberieOperation? operation = InsideMissionOperationForMethod(__originalMethod?.Name);
        bool won = __args?.Length > 0 && __args[0] is bool value && value;
        FourberieInsideMissionOutcome outcome = won
            ? FourberieInsideMissionOutcome.Won
            : Agent.Main == null || !Agent.Main.IsActive()
                ? FourberieInsideMissionOutcome.Incapacitated
                : FourberieInsideMissionOutcome.Escaped;
        Settlement settlement = Settlement.CurrentSettlement;
        if (operation.HasValue && settlement != null)
        {
            int encoded = FourberieInsideMissionResultCodec.Encode(
                outcome,
                Campaign.Current?.IsMainHeroDisguised == true);
            bool submitted = FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
                operation.Value,
                settlement,
                null,
                null,
                encoded,
                Array.Empty<FourberieLocalTroopSelection>())) == true;
            if (!submitted) FourberieSafehouseTransferContext.ShowUnavailable();
        }

        // The creator callback mixes campaign mutation with these local teardown calls. Once the
        // host owns the former, end the client fight without executing a second campaign result.
        try { Mission.Current?.EndMission(); }
        catch { /* mission teardown is best effort after an accepted result */ }
        return false;
    }

    internal static FourberieOperation? InsideMissionOperationForMethod(string methodName) => methodName switch
    {
        "AfterMathsGrabAndRun" => FourberieOperation.CompleteGrabAndRun,
        "AfterMathsBashing" => FourberieOperation.CompleteGangLeaderBashing,
        "AfterMathsIsoRob" => FourberieOperation.CompleteIsolatedRobbery,
        "AfterMathsPickFail" => FourberieOperation.CompletePickpocketFight,
        "AfterMathsGrudgeAssassin" => FourberieOperation.CompleteGrudgeAssassination,
        "AfterMathsTavernBrawl" => FourberieOperation.CompleteTavernBrawl,
        "AfterMathsLarceny" => FourberieOperation.CompleteLarcenyFight,
        "AfterMathsEncounterAlley" => FourberieOperation.CompleteAlleyFight,
        _ => null,
    };

    public static bool FightClubOutcomePrefix(
        object __instance,
        ref InquiryData __result,
        ref bool canPlayerLeave)
    {
        if (!ModInformation.IsClient || __instance == null) return false;
        Type type = __instance.GetType();
        if (!(AccessTools.Field(type, "_fightWonCheck")?.GetValue(__instance) is bool won) || !won)
            return true;

        int fightType = ReadIntField(type, __instance, "_fightType");
        int round = Math.Max(1, ReadIntField(type, __instance, "_round"));
        int knockouts = Math.Max(0, ReadIntField(type, __instance, "_KoPoints"));
        int trialFightType = ReadIntField(type, __instance, "_fightTypeTrialPatron");
        bool training = ReadBoolField(type, "_isTraining");
        bool gangTrial = ReadBoolField(type, "_isGangTrial");
        bool patronTrial = ReadBoolField(type, "_isPatronTrial");
        Type behavior = AccessTools.TypeByName("Fourberie.FourbFightClubBehavior");
        bool handToHand = behavior != null &&
                          AccessTools.Field(behavior, "_weaponType")?.GetValue(null) is int weapon && weapon == 1;
        int fame = ReadCrimeValue(950);
        int baseline = FightClubBaselines.TryGetValue(__instance, out FightClubBaseline captured)
            ? captured.Fame
            : fame;
        int fameDelta = Math.Max(-1024, Math.Min(1023, fame - baseline));
        var result = new FourberieFightClubResult(
            fightType, round, knockouts, trialFightType,
            training, gangTrial, patronTrial, handToHand, fameDelta);
        int encoded = FourberieFightClubResultCodec.Encode(result);
        Settlement settlement = Settlement.CurrentSettlement;
        Hero patron = MappedHero("pitPatron");
        bool submitted = FourberieFightClubResultCodec.IsValid(encoded) && settlement != null &&
                         FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
                             FourberieOperation.CompleteFightClubMatch,
                             settlement,
                             patron,
                             null,
                             encoded,
                             Array.Empty<FourberieLocalTroopSelection>())) == true;
        if (!submitted) FourberieSafehouseTransferContext.ShowUnavailable();
        AccessTools.Field(type, "_fightWonCheck")?.SetValue(__instance, false);
        canPlayerLeave = true;
        __result = null;
        try { Mission.Current?.EndMission(); }
        catch { }
        return false;
    }

    public static bool FightClubMissionLocalPrefix(object __instance, MethodBase __originalMethod)
    {
        if (!ModInformation.IsClient) return false;
        if (__instance != null && string.Equals(__originalMethod?.Name, "AfterStart", StringComparison.Ordinal))
        {
            FightClubBaselines.Remove(__instance);
            FightClubBaselines.Add(__instance, new FightClubBaseline { Fame = ReadCrimeValue(950) });
        }
        return true;
    }

    public static bool FightClubFamePrefix() =>
        ModInformation.IsClient || (ModInformation.IsServer && AllowedThread.IsThisThreadAllowed());

    public static bool FightClubPatronPaymentPrefix(ref bool applyPayment)
    {
        if (ModInformation.IsClient)
        {
            applyPayment = false;
            return true;
        }
        return ModInformation.IsServer && AllowedThread.IsThisThreadAllowed();
    }

    public static bool FightClubAdmissionPrefix(object __instance, object[] __args)
    {
        if (!ModInformation.IsClient) return false;
        if (__instance != null)
        {
            FightClubAdmissions.Remove(__instance);
            FightClubAdmissions.Add(__instance, new FightClubAdmission
            {
                RandomWeapon = string.Equals(SelectedInquiryIdentifier<string>(__args), "10", StringComparison.Ordinal),
            });
        }
        return true;
    }

    public static void FightClubAdmissionPostfix(object __instance)
    {
        if (!ModInformation.IsClient) return;
        Settlement settlement = Settlement.CurrentSettlement;
        if (settlement == null) return;

        int fightType = ReadCrimeValue(900);
        int trialFightType = ReadCrimeValue(951);
        bool training = ReadBoolField(AccessTools.TypeByName("Fourberie.FourbFightClubController"), "_isTraining");
        bool gangTrial = ReadBoolField(AccessTools.TypeByName("Fourberie.FourbFightClubController"), "_isGangTrial");
        bool patronTrial = ReadBoolField(AccessTools.TypeByName("Fourberie.FourbFightClubController"), "_isPatronTrial");
        Type behavior = AccessTools.TypeByName("Fourberie.FourbFightClubBehavior");
        bool handToHand = behavior != null &&
                          AccessTools.Field(behavior, "_weaponType")?.GetValue(null) is int weapon && weapon == 1;
        bool randomWeapon = __instance != null &&
                            FightClubAdmissions.TryGetValue(__instance, out FightClubAdmission admission) &&
                            admission.RandomWeapon;
        var result = new FourberieFightClubResult(
            fightType, 1, 0, trialFightType,
            training, gangTrial, patronTrial, handToHand,
            randomWeapon ? 1 : 0);
        Hero target = patronTrial ? settlement.Owner : MappedHero("pitPatron");
        bool submitted = FourberieFightClubResultCodec.IsValid(FourberieFightClubResultCodec.Encode(result)) &&
                         FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
                             FourberieOperation.StartFightClubMatch,
                             settlement,
                             target,
                             null,
                             FourberieFightClubResultCodec.Encode(result),
                             Array.Empty<FourberieLocalTroopSelection>())) == true;
        if (!submitted) FourberieSafehouseTransferContext.ShowUnavailable();
    }

    public static bool FightClubEnrollmentPrefix()
    {
        if (ModInformation.IsClient)
        {
            Settlement settlement = Settlement.CurrentSettlement;
            Hero protector = Hero.OneToOneConversationHero;
            bool submitted = settlement != null && protector != null &&
                             FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
                                 FourberieOperation.EnrollFightClub,
                                 settlement,
                                 protector,
                                 null,
                                 0,
                                 Array.Empty<FourberieLocalTroopSelection>())) == true;
            if (!submitted) FourberieSafehouseTransferContext.ShowUnavailable();
        }
        return false;
    }

    public static bool FightClubPatronRefusalPrefix(object[] __args)
    {
        if (ModInformation.IsClient)
        {
            InformationManager.HideInquiry();
            if (string.Equals(SelectedInquiryIdentifier<string>(__args), "5", StringComparison.Ordinal))
            {
                Settlement settlement = Settlement.CurrentSettlement;
                Hero patron = MappedHero("pitPatron");
                bool submitted = settlement != null && patron != null &&
                                 FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
                                     FourberieOperation.RefuteFightClubPatron,
                                     settlement,
                                     patron,
                                     null,
                                     0,
                                     Array.Empty<FourberieLocalTroopSelection>())) == true;
                if (!submitted) FourberieSafehouseTransferContext.ShowUnavailable();
                else GameMenu.SwitchToMenu("town_fightclub");
            }
        }
        return false;
    }

    public static bool FightClubStableOwnershipPrefix()
    {
        if (ModInformation.IsClient)
        {
            Settlement settlement = Settlement.CurrentSettlement;
            Hero protector = MappedHero("pitProtector");
            bool submitted = settlement != null &&
                             FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
                                 FourberieOperation.OwnFightClubStable,
                                 settlement,
                                 protector?.IsHumanPlayerCharacter == false ? protector : null,
                                 null,
                                 0,
                                 Array.Empty<FourberieLocalTroopSelection>())) == true;
            if (!submitted) FourberieSafehouseTransferContext.ShowUnavailable();
        }
        return false;
    }

    public static bool FightClubStableRecruitmentPrefix(
        TroopRoster leftMemberRoster,
        TroopRoster leftPrisonRoster,
        ref bool __result)
    {
        if (!ModInformation.IsClient) return true;
        Settlement settlement = Settlement.CurrentSettlement;
        FourberieLocalTroopSelection[] selected = Selections(leftMemberRoster, leftPrisonRoster);
        bool submitted = settlement != null && selected.Length > 0 &&
                         FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
                             FourberieOperation.RecruitFightClubStable,
                             settlement,
                             null,
                             null,
                             0,
                             selected)) == true;
        if (!submitted) FourberieSafehouseTransferContext.ShowUnavailable();
        FourberiePartyCommitSuppression.Request();
        __result = submitted;
        return false;
    }

    public static bool FightClubMenuRefreshPrefix()
    {
        if (!ModInformation.IsClient) return false;
        Settlement settlement = Settlement.CurrentSettlement;
        if (settlement != null)
            SubmitSettlement(FourberieOperation.RefreshFightClubMenu, settlement);
        return true;
    }

    public static bool AlleyAcquisitionPrefix(object[] __args)
    {
        if (!ModInformation.IsClient) return false;

        Alley alley = CampaignMission.Current?.LastVisitedAlley;
        CharacterObject selected = SelectedInquiryIdentifier<CharacterObject>(__args);
        Hero owner = Hero.MainHero;
        Hero overseer = selected?.HeroObject;
        CharacterObject gangster = MBObjectManager.Instance?.GetObject<CharacterObject>("gangster_1");
        if (alley == null || owner == null || overseer == null || gangster == null)
        {
            FourberieSafehouseTransferContext.ShowUnavailable();
            return false;
        }

        TroopRoster garrison = TroopRoster.CreateDummyTroopRoster();
        garrison.AddToCounts(selected, 1, false, 0, 0, true, -1);
        garrison.AddToCounts(gangster, 5, false, 0, 0, true, -1);
        InformationManager.HideInquiry();
        MessageBroker.Instance.Publish(alley, new AlleyAcquiredRequested(alley, owner, overseer, garrison));
        return false;
    }

    public static bool AlleyClearPrefix()
    {
        if (ModInformation.IsClient)
        {
            Alley alley = CampaignMission.Current?.LastVisitedAlley;
            if (alley != null) MessageBroker.Instance.Publish(alley, new AlleyClearedRequested(alley));
            else FourberieSafehouseTransferContext.ShowUnavailable();
        }
        return false;
    }

    public static bool SchemeRoomOpenPrefix()
    {
        if (!ModInformation.IsClient) return false;
        SubmitBusiness(FourberieOperation.EnsureSchemeRoomDefaults, 0);
        return true;
    }

    public static bool DominanceConditionPrefix() => ModInformation.IsClient;

    public static void DominanceConditionPostfix(bool __result)
    {
        if (ModInformation.IsClient && !__result)
            SubmitBusiness(FourberieOperation.ClearDominanceConversation, 0);
    }

    public static bool StealthMissionLocalPrefix() => ModInformation.IsClient;

    public static bool StealthHitPrefix() => ModInformation.IsClient;

    public static void StealthHitPostfix(object __instance, Agent victim)
    {
        if (!ModInformation.IsClient || __instance == null || victim?.Character is not CharacterObject character ||
            character.HeroObject is not Hero hero)
            return;

        FieldInfo victimsField = AccessTools.Field(__instance.GetType(), "_lordsHallVictims");
        if (victimsField?.GetValue(__instance) is not IEnumerable victims ||
            !victims.Cast<object>().Any(candidate => ReferenceEquals(candidate, hero)))
            return;

        StealthMissionState state = StealthMissions.GetOrCreateValue(__instance);
        if (state.WoundedHeroes.Add(hero.StringId))
            SubmitStealth(FourberieStealthEvent.LordWounded, hero);
    }

    public static bool StealthMissionEndPrefix(object __instance)
    {
        if (!ModInformation.IsClient) return false;
        FourberieStealthEvent outcome = Agent.Main?.KillCount >= 1
            ? FourberieStealthEvent.FinishMissionAlerted
            : FourberieStealthEvent.FinishMission;
        SubmitStealth(outcome);
        if (__instance != null) StealthMissions.Remove(__instance);
        return true;
    }

    public static bool StealthMilitiaPaymentPrefix(int option)
    {
        if (ModInformation.IsClient)
        {
            if (option == 1) SubmitStealth(FourberieStealthEvent.MilitiaFullPayment);
            else if (option == 2) SubmitStealth(FourberieStealthEvent.MilitiaHalfPayment);
        }
        return false;
    }

    public static bool StealthMilitiaChoicePrefix(int option)
    {
        if (!ModInformation.IsClient) return false;
        if (option != 1) return true;
        if (!SubmitStealth(FourberieStealthEvent.GreedyMilitiaImmediate))
        {
            FourberieSafehouseTransferContext.ShowUnavailable();
            return false;
        }
        Campaign.Current?.GameMenuManager?.SetNextMenu("village_greedysuccess");
        try { Mission.Current?.EndMission(); }
        catch { /* the authoritative consequence has already been submitted */ }
        return false;
    }

    public static bool StealthAbortContractPrefix()
    {
        if (!ModInformation.IsClient) return false;
        if (!SubmitStealth(FourberieStealthEvent.AbortContractForRansom))
        {
            FourberieSafehouseTransferContext.ShowUnavailable();
            return false;
        }

        try { Mission.Current?.EndMission(); }
        catch { /* the server transaction remains authoritative if local teardown already began */ }
        GameMenu.SwitchToMenu("town_TimeToLeave");
        return false;
    }

    public static bool StealthAlertConsequencePrefix()
    {
        if (ModInformation.IsClient) SubmitStealth(FourberieStealthEvent.AlertRaised);
        return ModInformation.IsClient;
    }

    public static bool StealthAnswerPrefix() => ModInformation.IsClient;

    public static void StealthAnswerPostfix(object __instance)
    {
        if (!ModInformation.IsClient || __instance == null) return;
        if (AccessTools.Field(__instance.GetType(), "_dialogCheckOk")?.GetValue(__instance) is bool valid && !valid)
            SubmitStealth(FourberieStealthEvent.AlertRaised);
    }

    public static bool StealthAgentRemovedPrefix() => ModInformation.IsClient;

    public static void StealthAgentRemovedPostfix(
        object __instance,
        Agent affectedAgent,
        Agent affectorAgent)
    {
        if (!ModInformation.IsClient || __instance == null || affectedAgent == null) return;
        string location = CampaignMission.Current?.Location?.StringId ?? string.Empty;
        if (affectedAgent.IsMainAgent)
        {
            FourberieStealthEvent failure = location switch
            {
                "lordshall" => FourberieStealthEvent.FailedLordHall,
                "prison" => FourberieStealthEvent.FailedPrison,
                "center" => FourberieStealthEvent.FailedTownCenter,
                _ => FourberieStealthEvent.FailedVillage,
            };
            SubmitStealth(failure);
            return;
        }

        if (affectorAgent?.IsMainAgent != true) return;
        if (location == "lordshall") SubmitStealthAlertOnce(__instance);
        if (location != "village_center" ||
            !ReferenceEquals(AccessTools.Field(__instance.GetType(), "_militiaLeader")?.GetValue(__instance), affectedAgent))
            return;
        int report = ReadIntField(__instance.GetType(), __instance, "_reportval");
        if (report == 2) SubmitStealth(FourberieStealthEvent.GreedyMilitiaAccepted);
        else if (report == 3) SubmitStealth(FourberieStealthEvent.GreedyMilitiaRefused);
    }

    public static bool StealthAlarmPrefix() => ModInformation.IsClient;

    public static void StealthAlarmPostfix(object __instance)
    {
        if (!ModInformation.IsClient || __instance == null) return;
        if (AccessTools.Field(__instance.GetType(), "_isGuardsAlarm")?.GetValue(__instance) is bool alarmed && alarmed)
            SubmitStealthAlertOnce(__instance);
    }

    public static bool StealthScandalSuccessPrefix()
    {
        if (ModInformation.IsClient) SubmitStealth(FourberieStealthEvent.ScandalRecovered);
        return ModInformation.IsClient;
    }

    public static bool StealthPrisonSuccessPrefix()
    {
        if (ModInformation.IsClient) SubmitStealth(FourberieStealthEvent.PrisonBreakCompleted);
        return ModInformation.IsClient;
    }

    public static bool BanditConsequencePrefix(MethodBase __originalMethod, object __instance, object[] __args)
    {
        if (!ModInformation.IsClient) return false;
        int token = __originalMethod?.MetadataToken ?? 0;
        switch (token)
        {
            case 0x06000272:
                SubmitBandit(FourberieBanditEvent.RepairShips, Settlement.CurrentSettlement);
                GameMenu.SwitchToMenu("cove_main");
                return false;
            case 0x06000745:
                SubmitBandit(FourberieBanditEvent.HealWounds, Settlement.CurrentSettlement);
                GameMenu.SwitchToMenu("hideout_fourberie");
                return false;
            case 0x0600074C:
                SubmitBandit(FourberieBanditEvent.ReleaseAllFollowers, Settlement.CurrentSettlement);
                GameMenu.SwitchToMenu("hideout_fourberie");
                return false;
            case 0x0600074E:
                SubmitBandit(
                    FourberieBanditEvent.RefuseBanditJoin,
                    null,
                    PlayerEncounter.EncounteredMobileParty ?? MobileParty.ConversationParty);
                PlayerEncounter.LeaveEncounter = true;
                return false;
            case 0x0600075E:
            {
                MobileParty[] parties = SelectedInquiryIdentifiers<MobileParty>(__args);
                if (parties.Length > 0)
                    SubmitBandit(FourberieBanditEvent.FollowParties, null, targets: parties.Cast<object>().ToArray());
                InformationManager.HideInquiry();
                return false;
            }
            case 0x06000766:
            {
                IFaction faction = SelectedInquiryIdentifier<IFaction>(__args);
                if (faction != null)
                    SubmitBandit(FourberieBanditEvent.SelectWarDogKingdom, Settlement.CurrentSettlement, faction);
                InformationManager.HideInquiry();
                return false;
            }
            case 0x06000776:
            {
                ShipHull hull = SelectedInquiryIdentifier<ShipHull>(__args);
                if (hull != null)
                    SubmitBandit(FourberieBanditEvent.AcquireCoveShip, Settlement.CurrentSettlement, hull);
                InformationManager.HideInquiry();
                return false;
            }
            case 0x06000782:
            {
                MobileParty party = PlayerEncounter.EncounteredMobileParty ?? MobileParty.ConversationParty;
                if (party != null) SubmitBandit(FourberieBanditEvent.StopFollower, null, party);
                PlayerEncounter.LeaveEncounter = true;
                return false;
            }
            case 0x06000791:
                return HandleBanditConnectionSelection(__args);
            case 0x06000795:
                return HandleBanditShipSelection(__instance, __args);
            case 0x060002C0:
                SubmitBandit(FourberieBanditEvent.AcceptTruce, Settlement.CurrentSettlement);
                return false;
            default:
                return false;
        }
    }

    public static bool BanditPreparationPrefix(MethodBase __originalMethod)
    {
        int token = __originalMethod?.MetadataToken ?? 0;
        if (!ModInformation.IsClient) return false;
        if (deferredBanditPresentationToken == token)
        {
            deferredBanditPresentationToken = 0;
            return true;
        }

        FourberieBanditEvent? banditEvent = token switch
        {
            0x06000741 => FourberieBanditEvent.PrepareRecruitment,
            0x06000744 => FourberieBanditEvent.OpenBanditStash,
            0x06000746 => FourberieBanditEvent.RefreshBlackMarket,
            0x06000748 => FourberieBanditEvent.StartHideoutWait,
            0x0600074A => FourberieBanditEvent.StopHideoutWait,
            _ => null,
        };
        if (!banditEvent.HasValue || !SubmitBandit(banditEvent.Value, Settlement.CurrentSettlement))
            FourberieSafehouseTransferContext.ShowUnavailable();
        return false;
    }

    public static bool LegacyCallbackPrefix(MethodBase __originalMethod)
    {
        if (ModInformation.IsServer) return FourberieLegacyExecutionContext.Active;
        if (!ModInformation.IsClient) return false;
        int token = __originalMethod?.MetadataToken ?? 0;
        Settlement settlement = Settlement.CurrentSettlement;
        if (!FourberieOperationProtocol.IsLegacyCallbackToken(token) || settlement == null ||
            FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
                FourberieOperation.CommitLegacyCallback,
                settlement,
                null,
                null,
                token,
                Array.Empty<FourberieLocalTroopSelection>())) != true)
            FourberieSafehouseTransferContext.ShowUnavailable();
        return false;
    }

    internal static void CompleteBanditPresentation(Assembly assembly, FourberieBanditEvent banditEvent)
    {
        if (!ModInformation.IsClient || assembly == null) return;
        if (banditEvent == FourberieBanditEvent.StartHideoutWait)
        {
            GameMenu.SwitchToMenu("hide_wait_fmenus");
            return;
        }
        if (banditEvent == FourberieBanditEvent.StopHideoutWait)
        {
            if (PlayerEncounter.Current != null) PlayerEncounter.Current.IsPlayerWaiting = false;
            GameMenu.SwitchToMenu("hideout_fourberie");
            return;
        }

        int token = banditEvent switch
        {
            FourberieBanditEvent.PrepareRecruitment => 0x06000741,
            FourberieBanditEvent.OpenBanditStash => 0x06000744,
            FourberieBanditEvent.RefreshBlackMarket => 0x06000746,
            _ => 0,
        };
        if (token == 0) return;
        try
        {
            MethodBase method = assembly.ManifestModule.ResolveMethod(token);
            deferredBanditPresentationToken = token;
            method.Invoke(null, new object[] { null });
        }
        catch
        {
            FourberieSafehouseTransferContext.ShowUnavailable();
        }
        finally
        {
            deferredBanditPresentationToken = 0;
        }
    }

    public static bool BanditDonationConsequencePrefix(
        TroopRoster leftPrisonRoster,
        ref bool __result)
    {
        if (!ModInformation.IsClient) return false;
        FourberieLocalTroopSelection[] selected = Selections(leftPrisonRoster);
        bool submitted = selected.Length > 0 && SubmitBandit(
            FourberieBanditEvent.DonatePrisoners,
            Settlement.CurrentSettlement,
            troops: selected);
        if (!submitted) FourberieSafehouseTransferContext.ShowUnavailable();
        FourberiePartyCommitSuppression.Request();
        __result = submitted;
        return false;
    }

    public static bool BanditRosterOpenPrefix(MobileParty conversationParty, ref int __state)
    {
        __state = 0;
        if (!ModInformation.IsClient || conversationParty == null) return false;
        banditRosterTarget = conversationParty;
        Type type = AccessTools.TypeByName("Fourberie.FourbBanditBehavior");
        FieldInfo field = type == null ? null : AccessTools.Field(type, "_recruitedBanditsOnMap");
        banditRosterRecruitment = field?.GetValue(null) is int value ? value : 0;
        __state = banditRosterRecruitment;
        if (__state > 0) field?.SetValue(null, 0);
        return true;
    }

    public static void BanditRosterOpenPostfix(int __state)
    {
        if (!ModInformation.IsClient || __state <= 0) return;
        Type type = AccessTools.TypeByName("Fourberie.FourbBanditBehavior");
        AccessTools.Field(type, "_recruitedBanditsOnMap")?.SetValue(null, __state);
    }

    public static bool BanditRosterConsequencePrefix(
        TroopRoster leftMemberRoster,
        TroopRoster leftPrisonRoster,
        ref bool __result)
    {
        if (!ModInformation.IsClient) return false;
        MobileParty target = banditRosterTarget;
        FourberieLocalRosterSelection[] roster = target == null
            ? Array.Empty<FourberieLocalRosterSelection>()
            : RosterSelections(target, leftMemberRoster, leftPrisonRoster);
        bool submitted = target != null && roster.Length > 0 && SubmitBandit(
            FourberieBanditEvent.CommitBanditRoster,
            null,
            target,
            secondaryId: banditRosterRecruitment > 0 ? "recruit.all" : null,
            roster: roster);
        if (!submitted) FourberieSafehouseTransferContext.ShowUnavailable();
        FourberiePartyCommitSuppression.Request();
        banditRosterTarget = null;
        banditRosterRecruitment = 0;
        __result = submitted;
        return false;
    }

    private static bool HandleBanditConnectionSelection(object[] arguments)
    {
        string selected = SelectedInquiryIdentifier<string>(arguments);
        FourberieBanditEvent? banditEvent = selected switch
        {
            "breakTruce" => FourberieBanditEvent.BreakTruce,
            "betray" => FourberieBanditEvent.BetrayBandits,
            _ => null,
        };
        if (!banditEvent.HasValue) return true;
        SubmitBandit(banditEvent.Value, Settlement.CurrentSettlement);
        InformationManager.HideInquiry();
        if (selected == "betray") GameMenu.SwitchToMenu("hideout_place");
        else
        {
            PlayerEncounter.LeaveSettlement();
            PlayerEncounter.Finish(true);
        }
        return false;
    }

    private static bool HandleBanditShipSelection(object instance, object[] arguments)
    {
        Ship ship = SelectedInquiryIdentifier<Ship>(arguments);
        MobileParty follower = instance == null
            ? null
            : AccessTools.Field(instance.GetType(), "partyConv")?.GetValue(instance) as MobileParty;
        if (ship == null || follower == null) return false;
        bool fromActor = ReferenceEquals(ship.Owner, MobileParty.MainParty.Party);
        IList<Ship> ships = fromActor ? MobileParty.MainParty.Ships : follower.Ships;
        int index = -1;
        for (int candidate = 0; candidate < ships.Count; candidate++)
            if (ReferenceEquals(ships[candidate], ship)) { index = candidate; break; }
        if (index >= 0)
            SubmitBandit(
                FourberieBanditEvent.TransferFollowerShip,
                null,
                follower,
                secondaryId: (fromActor ? "actor." : "follower.") + index.ToString(CultureInfo.InvariantCulture));
        InformationManager.HideInquiry();
        return false;
    }

    private static FourberieLocalRosterSelection[] RosterSelections(
        MobileParty target,
        TroopRoster finalMembers,
        TroopRoster finalPrisoners)
    {
        var troops = target.MemberRoster.GetTroopRoster().Select(value => value.Character)
            .Concat(target.PrisonRoster.GetTroopRoster().Select(value => value.Character))
            .Concat(finalMembers?.GetTroopRoster().Select(value => value.Character) ?? Enumerable.Empty<CharacterObject>())
            .Concat(finalPrisoners?.GetTroopRoster().Select(value => value.Character) ?? Enumerable.Empty<CharacterObject>())
            .Where(value => value != null)
            .Distinct()
            .ToArray();
        return troops.Select(troop => new FourberieLocalRosterSelection(
                troop,
                target.MemberRoster.GetTroopCount(troop) - (finalMembers?.GetTroopCount(troop) ?? 0),
                target.PrisonRoster.GetTroopCount(troop) - (finalPrisoners?.GetTroopCount(troop) ?? 0)))
            .Where(value => value.MemberDeltaToActor != 0 || value.PrisonerDeltaToActor != 0)
            .ToArray();
    }

    private static bool SubmitBandit(
        FourberieBanditEvent banditEvent,
        Settlement settlement,
        object target = null,
        object[] targets = null,
        string secondaryId = null,
        FourberieLocalTroopSelection[] troops = null,
        FourberieLocalRosterSelection[] roster = null) =>
        FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
            FourberieOperation.CommitBanditEvent,
            settlement,
            null,
            null,
            (int)banditEvent,
            troops ?? Array.Empty<FourberieLocalTroopSelection>(),
            targetObject: target,
            targetObjects: targets,
            secondaryId: secondaryId,
            roster: roster)) == true;

    private static void SubmitStealthAlertOnce(object instance)
    {
        StealthMissionState state = StealthMissions.GetOrCreateValue(instance);
        if (state.AlertSubmitted) return;
        state.AlertSubmitted = SubmitStealth(FourberieStealthEvent.AlertRaised);
    }

    private static bool SubmitStealth(FourberieStealthEvent stealthEvent, Hero target = null)
    {
        Settlement settlement = Settlement.CurrentSettlement;
        return settlement != null && FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
            FourberieOperation.CommitStealthEvent,
            settlement,
            target,
            null,
            (int)stealthEvent,
            Array.Empty<FourberieLocalTroopSelection>())) == true;
    }

    private static int ReadCrimeValue(int key)
    {
        Type behavior = AccessTools.TypeByName("Fourberie.FourberieBehavior");
        IDictionary crime = behavior == null
            ? null
            : AccessTools.Field(behavior, "_crimeValue")?.GetValue(null) as IDictionary;
        return crime?.Contains(key) == true ? Convert.ToInt32(crime[key]) : 0;
    }

    private static int ReadIntField(Type type, object instance, string name) =>
        AccessTools.Field(type, name)?.GetValue(instance) is int value ? value : 0;

    private static bool ReadBoolField(Type type, string name) =>
        AccessTools.Field(type, name)?.GetValue(null) is bool value && value;

    private static Hero MappedHero(string key)
    {
        Type behavior = AccessTools.TypeByName("Fourberie.FourberieBehavior");
        IDictionary heroes = behavior == null
            ? null
            : AccessTools.Field(behavior, "_stringHeroIdDico")?.GetValue(null) as IDictionary;
        return heroes?.Contains(key) == true ? Hero.Find(heroes[key] as string) : null;
    }

    internal static FourberieOperation SchemeLifecycleOperation(int slot)
    {
        Type behavior = AccessTools.TypeByName("Fourberie.FourberieBehavior");
        IDictionary crime = behavior == null
            ? null
            : AccessTools.Field(behavior, "_crimeValue")?.GetValue(null) as IDictionary;
        return SchemeLifecycleOperation(crime, slot);
    }

    internal static FourberieOperation SchemeLifecycleOperation(IDictionary crime, int slot)
    {
        if (crime?.Contains(slot * 100 + 40) == true) return FourberieOperation.AbortScheme;
        if (crime?.Contains(slot * 100 + 41) == true) return FourberieOperation.ClearCompletedScheme;
        return FourberieOperation.StartScheme;
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
    /// Replaces Fourberie's <c>Main.OnGameInitializationFinished</c> with an authoritative rebuild
    /// of its replicated hero dictionaries. Exact model compatibility is enforced earlier by the
    /// adapter, so Fourberie's load-order diagnostic is redundant here.
    /// </summary>
    public static bool RefreshHeroDicoOnlyPrefix()
    {
        if (!ShouldRefreshHeroDico(ModInformation.IsServer)) return false;

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

    internal static bool ShouldRefreshHeroDico(bool isServer) => isServer;

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

    internal static void ResetTickLedger()
    {
        TickLedger.Reset();
        pendingTerritoryAbandonment = null;
    }

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

    private static void SubmitScheme(FourberieOperation operation, int slot, Hero target) =>
        FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
            operation,
            null,
            target,
            null,
            slot,
            Array.Empty<FourberieLocalTroopSelection>()));

    private static void SubmitSettlement(FourberieOperation operation, Settlement settlement) =>
        FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
            operation,
            settlement,
            null,
            null,
            0,
            Array.Empty<FourberieLocalTroopSelection>()));

    private static void SubmitClan(FourberieOperation operation, Clan clan, int amount) =>
        FourberiePatchRuntime.Current?.TrySubmit(new FourberieLocalOperation(
            operation,
            null,
            null,
            null,
            amount,
            Array.Empty<FourberieLocalTroopSelection>(),
            clan));

    private static T SelectedInquiryIdentifier<T>(object[] arguments)
    {
        object identifier = (arguments != null && arguments.Length > 0
                ? arguments[0] as IEnumerable<InquiryElement>
                : null)?
            .Select(element => element?.Identifier)
            .FirstOrDefault(value => value is T);
        return identifier is T selected ? selected : default;
    }

    private static T[] SelectedInquiryIdentifiers<T>(object[] arguments) where T : class =>
        (arguments != null && arguments.Length > 0
                ? arguments[0] as IEnumerable<InquiryElement>
                : null)?
            .Select(element => element?.Identifier as T)
            .Where(value => value != null)
            .Distinct()
            .ToArray() ?? Array.Empty<T>();

    private static IDictionary FourberieRoles()
    {
        Type behavior = AccessTools.TypeByName("Fourberie.FourberieBehavior");
        return behavior == null
            ? null
            : AccessTools.Field(behavior, "_stringHeroIdDico")?.GetValue(null) as IDictionary;
    }

    private static IDictionary FourberieCrimeValues()
    {
        Type behavior = AccessTools.TypeByName("Fourberie.FourberieBehavior");
        return behavior == null
            ? null
            : AccessTools.Field(behavior, "_crimeValue")?.GetValue(null) as IDictionary;
    }

    private static IDictionary FourberieCampaignTimes()
    {
        Type behavior = AccessTools.TypeByName("Fourberie.FourberieBehavior");
        return behavior == null
            ? null
            : AccessTools.Field(behavior, "_campaignTimeDictio")?.GetValue(null) as IDictionary;
    }

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

    private static FourberieLocalTroopSelection[] Selections(params TroopRoster[] rosters) =>
        (rosters ?? Array.Empty<TroopRoster>())
        .Where(roster => roster != null)
        .SelectMany(Selections)
        .GroupBy(selection => selection.Troop)
        .Select(group => new FourberieLocalTroopSelection(group.Key, group.Sum(selection => selection.Count)))
        .ToArray();

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
