using Common.Messaging;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal enum FourberieOperation
{
    EnlistAgentsFromParty = 1,
    EnlistAgentsFromLads = 2,
    RecruitBandits = 3,
    StartInsuranceScam = 4,
    StartCriminalBusiness = 5,
    UpgradeCriminalBusiness = 6,
    DowngradeCriminalBusiness = 7,
    UpgradeSchemeBonus = 8,
    DowngradeSchemeBonus = 9,
    ResetSchemeBonus = 10,
    CreateAgentParty = 11,
    DisbandAgentParty = 12,
    RefillAgentParty = 13,
    ResetCrimeBaseParty = 14,
    AssignCriminalRole = 15,
    RemoveCriminalRole = 16,
    SelectSchemeVictim = 17,
    SelectSchemeType = 18,
    StartScheme = 19,
    AbortScheme = 20,
    ClearCompletedScheme = 21,
    ChangeSchemeStance = 22,
    SetCorruptionLevel = 23,
    SetAutoInvestment = 24,
    SetLadsDuty = 25,
    SetSlavesDuty = 26,
    EnableContractOffers = 27,
    DisableContractOffers = 28,
    AbortContract = 29,
    SetMainCrimeBase = 30,
    RemoveTerritory = 31,
    AbandonTownCrimeBase = 32,
    AbandonSafehouse = 33,
    RequestGrudgeQuote = 34,
    SettleClanGrudge = 35,
    AcceptContractProposal = 36,
    DeclineContractProposal = 37,
    SellQuarterSlaves = 38,
    SellHalfSlaves = 39,
    DeclineCrookedTrader = 40,
    RobCrookedTrader = 41,
    EstablishSafehouse = 42,
    StartSafehouseWait = 43,
    StopSafehouseWait = 44,
    CompleteSafehouseReturn = 45,
    EnslavePrisoners = 46,
    TransferSafehouseItems = 47,
    CompleteGrabAndRun = 48,
    CompleteGangLeaderBashing = 49,
    CompleteIsolatedRobbery = 50,
    CompletePickpocketFight = 51,
    CompleteGrudgeAssassination = 52,
    CompleteTavernBrawl = 53,
    CompleteLarcenyFight = 54,
    CompleteAlleyFight = 55,
    CompleteFightClubMatch = 56,
    StartFightClubMatch = 57,
    EnrollFightClub = 58,
    RefuteFightClubPatron = 59,
    OwnFightClubStable = 60,
    RecruitFightClubStable = 61,
    RefreshFightClubMenu = 62,
    EnsureSchemeRoomDefaults = 63,
    ClearDominanceConversation = 64,
    CommitStealthEvent = 65,
    CommitBanditEvent = 66,
    CommitLegacyCallback = 67,
    CommitConversationEvent = 68,
    CommitCampaignConsequence = 69,
    RecruitMinorTroops = 70,
    LeaveKingdom = 71,
    CommitGuardKills = 72,
    CommitSafehouseEncounter = 73,
    CommitCriminalConsequence = 74,
}

internal enum FourberieCriminalConsequence
{
    PrisonBreakSuccess = 1,
    EstablishCrimeBase = 2,
    DominancePartnership = 3,
    DominanceTakeover = 4,
    Fortune = 5,
    ClearRivalry = 6,
    GatherFollowers = 7,
    PromoteCompanion = 8,
    EscapeCaptivity = 9,
    SabotageFood = 10,
    SabotageWalls = 11,
    SabotageWater = 12,
    ManageWorkshopOwner = 13,
    ConvertWorkshop = 14,
    PickAction = 15,
    PickFailure = 16,
    CaravanAmbushResult = 17,
    TributeResult = 18,
    ExtortionResult = 19,
    RiotResult = 20,
    CaravanAmbushHire = 21,
    AbandonGreedyMilitia = 22,
    AbandonLarceny = 23,
    StartRiot = 24,
    PayRiotInfluence = 25,
    DefectRiotVictim = 26,
    DeclareRiotWar = 27,
    BanishRiotActor = 28,
}

internal enum FourberieStealthEvent
{
    AlertRaised = 1,
    MilitiaFullPayment = 2,
    MilitiaHalfPayment = 3,
    AbortContractForRansom = 4,
    LordWounded = 5,
    FinishMission = 6,
    ScandalRecovered = 7,
    PrisonBreakCompleted = 8,
    GreedyMilitiaAccepted = 9,
    GreedyMilitiaRefused = 10,
    FailedLordHall = 11,
    FailedPrison = 12,
    FailedTownCenter = 13,
    FailedVillage = 14,
    FinishMissionAlerted = 15,
    GreedyMilitiaImmediate = 16,
}

internal enum FourberieBanditEvent
{
    RepairShips = 1,
    HealWounds = 2,
    ReleaseAllFollowers = 3,
    RefuseBanditJoin = 4,
    FollowParties = 5,
    StopFollower = 6,
    AcceptTruce = 7,
    BreakTruce = 8,
    BetrayBandits = 9,
    SelectWarDogKingdom = 10,
    AcquireCoveShip = 11,
    TransferFollowerShip = 12,
    DonatePrisoners = 13,
    CommitBanditRoster = 14,
    PrepareRecruitment = 15,
    OpenBanditStash = 16,
    RefreshBlackMarket = 17,
    StartHideoutWait = 18,
    StopHideoutWait = 19,
    DonateLoot = 20,
}

internal enum FourberieConversationEvent
{
    PromoteGangLeader = 1,
    EstablishPartnership = 2,
    AcceptRecommendation = 3,
    RejectRivalry = 4,
    ResolveGangLeaderBashing = 5,
    RejectBashing = 6,
}

internal enum FourberieCampaignConsequence
{
    StartAssassination = 1,
    RanAway = 2,
    HealWound = 3,
    SafehouseCompanionRelation = 4,
    BribeGuard = 5,
}

internal enum FourberieOperationStatus
{
    Accepted = 1,
    Rejected = 2,
    StaleSession = 3,
    StaleState = 4,
    Failed = 5,
}

[ProtoContract(SkipConstructor = true)]
internal sealed class FourberieTroopSelection
{
    [ProtoMember(1)] public string TroopId { get; private set; }
    [ProtoMember(2)] public int Count { get; private set; }

    private FourberieTroopSelection()
    {
    }

    public FourberieTroopSelection(string troopId, int count)
    {
        TroopId = troopId;
        Count = count;
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class FourberieItemSelection
{
    [ProtoMember(1)] public string ItemId { get; private set; }
    [ProtoMember(2)] public string ItemModifierId { get; private set; }
    [ProtoMember(3)] public int DeltaToSafehouse { get; private set; }

    private FourberieItemSelection()
    {
    }

    public FourberieItemSelection(string itemId, string itemModifierId, int deltaToSafehouse)
    {
        ItemId = itemId;
        ItemModifierId = itemModifierId ?? string.Empty;
        DeltaToSafehouse = deltaToSafehouse;
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class FourberieRosterSelection
{
    [ProtoMember(1)] public string TroopId { get; private set; }
    [ProtoMember(2)] public int MemberDeltaToActor { get; private set; }
    [ProtoMember(3)] public int PrisonerDeltaToActor { get; private set; }

    private FourberieRosterSelection()
    {
    }

    public FourberieRosterSelection(string troopId, int memberDeltaToActor, int prisonerDeltaToActor)
    {
        TroopId = troopId;
        MemberDeltaToActor = memberDeltaToActor;
        PrisonerDeltaToActor = prisonerDeltaToActor;
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkRequestFourberieOperation : ICommand
{
    [ProtoMember(1)] public string SessionId { get; private set; }
    [ProtoMember(2)] public long RequestId { get; private set; }
    [ProtoMember(3)] public long ExpectedRevision { get; private set; }
    [ProtoMember(4)] public FourberieOperation Operation { get; private set; }
    [ProtoMember(5)] public string SettlementId { get; private set; }
    [ProtoMember(6)] public string TargetId { get; private set; }
    [ProtoMember(7)] public string SecondaryTargetId { get; private set; }
    [ProtoMember(8)] public int IntValue { get; private set; }
    [ProtoMember(9)] private FourberieTroopSelection[] troops;
    [ProtoMember(10)] private FourberieItemSelection[] items;
    [ProtoMember(11)] private string[] objectIds;
    [ProtoMember(12)] private FourberieRosterSelection[] roster;

    public FourberieTroopSelection[] Troops => troops ?? Array.Empty<FourberieTroopSelection>();
    public FourberieItemSelection[] Items => items ?? Array.Empty<FourberieItemSelection>();
    public string[] ObjectIds => objectIds ?? Array.Empty<string>();
    public FourberieRosterSelection[] Roster => roster ?? Array.Empty<FourberieRosterSelection>();

    private NetworkRequestFourberieOperation()
    {
    }

    public NetworkRequestFourberieOperation(
        string sessionId,
        long requestId,
        long expectedRevision,
        FourberieOperation operation,
        string settlementId,
        string targetId,
        int intValue,
        FourberieTroopSelection[] troops)
        : this(sessionId, requestId, expectedRevision, operation, settlementId, targetId,
            string.Empty, intValue, troops, Array.Empty<FourberieItemSelection>())
    {
    }

    public NetworkRequestFourberieOperation(
        string sessionId,
        long requestId,
        long expectedRevision,
        FourberieOperation operation,
        string settlementId,
        string targetId,
        string secondaryTargetId,
        int intValue,
        FourberieTroopSelection[] troops)
        : this(sessionId, requestId, expectedRevision, operation, settlementId, targetId,
            secondaryTargetId, intValue, troops, Array.Empty<FourberieItemSelection>())
    {
    }

    public NetworkRequestFourberieOperation(
        string sessionId,
        long requestId,
        long expectedRevision,
        FourberieOperation operation,
        string settlementId,
        string targetId,
        string secondaryTargetId,
        int intValue,
        FourberieTroopSelection[] troops,
        FourberieItemSelection[] items,
        string[] objectIds = null,
        FourberieRosterSelection[] roster = null)
    {
        SessionId = sessionId;
        RequestId = requestId;
        ExpectedRevision = expectedRevision;
        Operation = operation;
        SettlementId = settlementId ?? string.Empty;
        TargetId = targetId ?? string.Empty;
        SecondaryTargetId = secondaryTargetId ?? string.Empty;
        IntValue = intValue;
        this.troops = troops ?? Array.Empty<FourberieTroopSelection>();
        this.items = items ?? Array.Empty<FourberieItemSelection>();
        this.objectIds = objectIds ?? Array.Empty<string>();
        this.roster = roster ?? Array.Empty<FourberieRosterSelection>();
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkFourberieOperationResult : ICommand
{
    [ProtoMember(1)] public string SessionId { get; private set; }
    [ProtoMember(2)] public long RequestId { get; private set; }
    [ProtoMember(3)] public FourberieOperationStatus Status { get; private set; }
    [ProtoMember(4)] public long Revision { get; private set; }
    [ProtoMember(5)] public int IntValue { get; private set; }

    private NetworkFourberieOperationResult()
    {
    }

    public NetworkFourberieOperationResult(
        string sessionId,
        long requestId,
        FourberieOperationStatus status,
        long revision,
        int intValue = 0)
    {
        SessionId = sessionId;
        RequestId = requestId;
        Status = status;
        Revision = revision;
        IntValue = intValue;
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkFourberieContractProposal : ICommand
{
    [ProtoMember(1)] public string SessionId { get; private set; }
    [ProtoMember(2)] public long Revision { get; private set; }
    [ProtoMember(3)] public string GiverId { get; private set; }
    [ProtoMember(4)] public string TargetId { get; private set; }
    [ProtoMember(5)] public int ContractType { get; private set; }
    [ProtoMember(6)] public int Reward { get; private set; }

    private NetworkFourberieContractProposal()
    {
    }

    public NetworkFourberieContractProposal(
        string sessionId,
        long revision,
        string giverId,
        string targetId,
        int contractType,
        int reward)
    {
        SessionId = sessionId;
        Revision = revision;
        GiverId = giverId;
        TargetId = targetId;
        ContractType = contractType;
        Reward = reward;
    }
}

internal static class FourberieOperationProtocol
{
    internal const int MaxTroopSelections = 64;
    internal const int MaxItemSelections = 256;
    internal const int MaxObjectSelections = 64;
    internal const int MaxRosterSelections = 128;
    internal const int MaxStableIdLength = 256;
    internal const int MaxSelectedTroops = 2_000;
    internal const int MaxSelectedItems = 20_000;

    public static bool IsRequestShapeValid(NetworkRequestFourberieOperation request)
    {
        if (request == null || request.SessionId == null || request.SessionId.Length != 32 ||
            !Guid.TryParseExact(request.SessionId, "N", out _) || request.RequestId <= 0 ||
            request.ExpectedRevision < 0 || !Enum.IsDefined(typeof(FourberieOperation), request.Operation) ||
            !IsStableId(request.SettlementId, allowEmpty: true) ||
            !IsStableId(request.TargetId, allowEmpty: true) || request.IntValue < 0 ||
            !IsStableId(request.SecondaryTargetId, allowEmpty: true) ||
            (request.Operation != FourberieOperation.SettleClanGrudge &&
             request.Operation != FourberieOperation.CompleteFightClubMatch &&
             request.Operation != FourberieOperation.StartFightClubMatch &&
             request.Operation != FourberieOperation.CommitLegacyCallback &&
             request.IntValue > MaxSelectedTroops) ||
            request.Troops.Length > MaxTroopSelections || request.Items.Length > MaxItemSelections ||
            request.ObjectIds.Length > MaxObjectSelections || request.Roster.Length > MaxRosterSelections ||
            (request.Operation != FourberieOperation.TransferSafehouseItems &&
             request.Operation != FourberieOperation.CommitBanditEvent && request.Items.Length != 0) ||
            (request.Operation != FourberieOperation.CommitBanditEvent &&
             request.Operation != FourberieOperation.CommitCriminalConsequence &&
             (request.ObjectIds.Length != 0 || request.Roster.Length != 0)))
            return false;

        int total = 0;
        foreach (FourberieTroopSelection troop in request.Troops)
        {
            if (troop == null || !IsStableId(troop.TroopId, allowEmpty: false) ||
                troop.Count <= 0 || troop.Count > MaxSelectedTroops)
                return false;
            total += troop.Count;
            if (total > MaxSelectedTroops) return false;
        }

        if (request.Troops
            .Select(troop => troop.TroopId)
            .Distinct(StringComparer.Ordinal)
            .Count() != request.Troops.Length)
            return false;

        long selectedItems = 0;
        foreach (FourberieItemSelection item in request.Items)
        {
            if (item == null || !IsStableId(item.ItemId, allowEmpty: false) ||
                !IsStableId(item.ItemModifierId, allowEmpty: true) || item.DeltaToSafehouse == 0 ||
                Math.Abs((long)item.DeltaToSafehouse) > MaxSelectedItems)
                return false;
            selectedItems += Math.Abs((long)item.DeltaToSafehouse);
            if (selectedItems > MaxSelectedItems) return false;
        }

        if (request.Items
            .Select(item => item.ItemId + "\0" + item.ItemModifierId)
            .Distinct(StringComparer.Ordinal)
            .Count() != request.Items.Length)
            return false;

        if (request.ObjectIds.Any(value => !IsStableId(value, allowEmpty: false)) ||
            request.ObjectIds.Distinct(StringComparer.Ordinal).Count() != request.ObjectIds.Length)
            return false;

        long rosterMagnitude = 0;
        foreach (FourberieRosterSelection selection in request.Roster)
        {
            if (selection == null || !IsStableId(selection.TroopId, allowEmpty: false) ||
                selection.MemberDeltaToActor == 0 && selection.PrisonerDeltaToActor == 0)
                return false;
            rosterMagnitude += Math.Abs((long)selection.MemberDeltaToActor) +
                               Math.Abs((long)selection.PrisonerDeltaToActor);
            if (rosterMagnitude > MaxSelectedTroops) return false;
        }
        if (request.Roster.Select(value => value.TroopId).Distinct(StringComparer.Ordinal).Count() !=
            request.Roster.Length)
            return false;

        int selected = request.Troops.Sum(troop => troop.Count);
        return request.Operation switch
        {
            FourberieOperation.EnlistAgentsFromParty =>
                EmptyTargets(request) && request.IntValue == 0 && request.Troops.Length > 0,
            FourberieOperation.EnlistAgentsFromLads =>
                !string.IsNullOrEmpty(request.SettlementId) && EmptyTargets(request) &&
                request.IntValue == 0 && request.Troops.Length > 0,
            FourberieOperation.RecruitBandits =>
                !string.IsNullOrEmpty(request.SettlementId) && EmptyTargets(request) &&
                request.IntValue > 0 && selected > 0 && selected <= request.IntValue,
            FourberieOperation.EnslavePrisoners =>
                !string.IsNullOrEmpty(request.SettlementId) && EmptyTargets(request) &&
                request.IntValue == 0 && request.Troops.Length > 0,
            FourberieOperation.TransferSafehouseItems =>
                !string.IsNullOrEmpty(request.SettlementId) && EmptyTargets(request) &&
                request.IntValue == 0 && request.Troops.Length == 0 && request.Items.Length > 0,
            FourberieOperation.StartInsuranceScam =>
                !string.IsNullOrEmpty(request.SettlementId) &&
                !string.IsNullOrEmpty(request.TargetId) &&
                !string.IsNullOrEmpty(request.SecondaryTargetId) &&
                request.IntValue == 0 && request.Troops.Length == 0,
            FourberieOperation.StartCriminalBusiness =>
                EmptyContext(request) && (request.IntValue == 11 || request.IntValue == 21 || request.IntValue == 31),
            FourberieOperation.UpgradeCriminalBusiness or FourberieOperation.DowngradeCriminalBusiness =>
                EmptyContext(request) && IsBusinessKey(request.IntValue),
            FourberieOperation.UpgradeSchemeBonus or FourberieOperation.DowngradeSchemeBonus or
                FourberieOperation.ResetSchemeBonus =>
                EmptyContext(request) && (request.IntValue == 7 || request.IntValue == 8),
            FourberieOperation.CreateAgentParty or FourberieOperation.DisbandAgentParty or
                FourberieOperation.RefillAgentParty or FourberieOperation.ResetCrimeBaseParty =>
                EmptyContext(request) && request.IntValue == 0,
            FourberieOperation.AssignCriminalRole =>
                string.IsNullOrEmpty(request.SettlementId) && !string.IsNullOrEmpty(request.TargetId) &&
                string.IsNullOrEmpty(request.SecondaryTargetId) && request.Troops.Length == 0 &&
                IsRoleCode(request.IntValue),
            FourberieOperation.RemoveCriminalRole =>
                EmptyContext(request) && IsRoleCode(request.IntValue),
            FourberieOperation.SelectSchemeVictim =>
                string.IsNullOrEmpty(request.SettlementId) && !string.IsNullOrEmpty(request.TargetId) &&
                string.IsNullOrEmpty(request.SecondaryTargetId) && request.Troops.Length == 0 &&
                IsSchemeSlot(request.IntValue),
            FourberieOperation.SelectSchemeType =>
                EmptyContext(request) && IsSchemeSelection(request.IntValue),
            FourberieOperation.StartScheme or FourberieOperation.AbortScheme or
                FourberieOperation.ClearCompletedScheme =>
                EmptyContext(request) && IsSchemeSlot(request.IntValue),
            FourberieOperation.ChangeSchemeStance =>
                EmptyContext(request) && (request.IntValue == 1 || request.IntValue == 2),
            FourberieOperation.SetCorruptionLevel =>
                EmptyContext(request) && IsCorruptionLevel(request.IntValue),
            FourberieOperation.SetAutoInvestment =>
                EmptyContext(request) && request.IntValue <= 5,
            FourberieOperation.SetLadsDuty or FourberieOperation.SetSlavesDuty =>
                EmptyContext(request) && request.IntValue <= 100,
            FourberieOperation.EnableContractOffers or FourberieOperation.DisableContractOffers or
                FourberieOperation.AbortContract or FourberieOperation.AcceptContractProposal or
                FourberieOperation.DeclineContractProposal =>
                EmptyContext(request) && request.IntValue == 0,
            FourberieOperation.SetMainCrimeBase or FourberieOperation.RemoveTerritory or
                FourberieOperation.AbandonTownCrimeBase or FourberieOperation.AbandonSafehouse or
                FourberieOperation.SellQuarterSlaves or FourberieOperation.SellHalfSlaves or
                FourberieOperation.DeclineCrookedTrader or FourberieOperation.RobCrookedTrader or
                FourberieOperation.EstablishSafehouse or FourberieOperation.StartSafehouseWait or
                FourberieOperation.StopSafehouseWait or FourberieOperation.CompleteSafehouseReturn =>
                !string.IsNullOrEmpty(request.SettlementId) && EmptyTargets(request) &&
                request.IntValue == 0 && request.Troops.Length == 0,
            FourberieOperation.RequestGrudgeQuote =>
                string.IsNullOrEmpty(request.SettlementId) && !string.IsNullOrEmpty(request.TargetId) &&
                string.IsNullOrEmpty(request.SecondaryTargetId) && request.IntValue == 0 && request.Troops.Length == 0,
            FourberieOperation.SettleClanGrudge =>
                string.IsNullOrEmpty(request.SettlementId) && !string.IsNullOrEmpty(request.TargetId) &&
                string.IsNullOrEmpty(request.SecondaryTargetId) &&
                request.IntValue <= FourberieGrudgeAuthority.MaximumPayment && request.Troops.Length == 0,
            FourberieOperation.CompleteGrabAndRun or
                FourberieOperation.CompleteGangLeaderBashing or
                FourberieOperation.CompleteIsolatedRobbery or
                FourberieOperation.CompletePickpocketFight or
                FourberieOperation.CompleteGrudgeAssassination or
                FourberieOperation.CompleteTavernBrawl or
                FourberieOperation.CompleteLarcenyFight or
                FourberieOperation.CompleteAlleyFight =>
                !string.IsNullOrEmpty(request.SettlementId) && EmptyTargets(request) &&
                request.IntValue >= 1 && request.IntValue <= 6 && request.Troops.Length == 0,
            FourberieOperation.CompleteFightClubMatch =>
                !string.IsNullOrEmpty(request.SettlementId) &&
                string.IsNullOrEmpty(request.SecondaryTargetId) && request.Troops.Length == 0 &&
                FourberieFightClubResultCodec.IsValid(request.IntValue),
            FourberieOperation.StartFightClubMatch =>
                !string.IsNullOrEmpty(request.SettlementId) &&
                string.IsNullOrEmpty(request.SecondaryTargetId) && request.Troops.Length == 0 &&
                FourberieFightClubResultCodec.IsValid(request.IntValue),
            FourberieOperation.EnrollFightClub =>
                !string.IsNullOrEmpty(request.SettlementId) && !string.IsNullOrEmpty(request.TargetId) &&
                string.IsNullOrEmpty(request.SecondaryTargetId) &&
                request.IntValue == 0 && request.Troops.Length == 0,
            FourberieOperation.OwnFightClubStable =>
                !string.IsNullOrEmpty(request.SettlementId) &&
                string.IsNullOrEmpty(request.SecondaryTargetId) &&
                request.IntValue == 0 && request.Troops.Length == 0,
            FourberieOperation.RefuteFightClubPatron =>
                !string.IsNullOrEmpty(request.SettlementId) && !string.IsNullOrEmpty(request.TargetId) &&
                string.IsNullOrEmpty(request.SecondaryTargetId) && request.IntValue == 0 &&
                request.Troops.Length == 0,
            FourberieOperation.RecruitFightClubStable =>
                !string.IsNullOrEmpty(request.SettlementId) && EmptyTargets(request) &&
                request.IntValue == 0 && request.Troops.Length > 0 && selected <= 50,
            FourberieOperation.RefreshFightClubMenu =>
                !string.IsNullOrEmpty(request.SettlementId) && EmptyTargets(request) &&
                request.IntValue == 0 && request.Troops.Length == 0,
            FourberieOperation.EnsureSchemeRoomDefaults or FourberieOperation.ClearDominanceConversation =>
                EmptyContext(request) && request.IntValue == 0,
            FourberieOperation.CommitStealthEvent =>
                !string.IsNullOrEmpty(request.SettlementId) &&
                string.IsNullOrEmpty(request.SecondaryTargetId) && request.Troops.Length == 0 &&
                IsStealthEvent(request.IntValue) &&
                (StealthEventRequiresTarget(request.IntValue)
                    ? !string.IsNullOrEmpty(request.TargetId)
                    : string.IsNullOrEmpty(request.TargetId)),
            FourberieOperation.CommitBanditEvent => IsBanditEventShapeValid(request),
            FourberieOperation.CommitLegacyCallback => IsLegacyCallbackShapeValid(request),
            FourberieOperation.CommitConversationEvent => IsConversationEventShapeValid(request),
            FourberieOperation.CommitCampaignConsequence => IsCampaignConsequenceShapeValid(request),
            FourberieOperation.RecruitMinorTroops =>
                !string.IsNullOrEmpty(request.SettlementId) && EmptyTargets(request) &&
                request.IntValue == 0 && request.Troops.Length > 0 && selected <= 30,
            FourberieOperation.LeaveKingdom =>
                string.IsNullOrEmpty(request.SettlementId) && string.IsNullOrEmpty(request.TargetId) &&
                (request.SecondaryTargetId == "keep" || request.SecondaryTargetId == "dontkeep") &&
                request.IntValue == 0 && request.Troops.Length == 0,
            FourberieOperation.CommitGuardKills =>
                !string.IsNullOrEmpty(request.SettlementId) && EmptyTargets(request) &&
                request.IntValue <= MaxSelectedTroops && request.Troops.Length == 0,
            FourberieOperation.CommitSafehouseEncounter =>
                !string.IsNullOrEmpty(request.SettlementId) && EmptyTargets(request) &&
                request.IntValue >= 2 && request.IntValue <= 5 && request.Troops.Length == 0,
            FourberieOperation.CommitCriminalConsequence => IsCriminalConsequenceShapeValid(request),
            _ => false,
        };
    }

    public static bool IsProposalShapeValid(NetworkFourberieContractProposal proposal) =>
        proposal != null && IsSessionId(proposal.SessionId) && proposal.Revision >= 0 &&
        IsStableId(proposal.GiverId, allowEmpty: false) && IsStableId(proposal.TargetId, allowEmpty: false) &&
        proposal.GiverId != proposal.TargetId && (proposal.ContractType == 0 || proposal.ContractType == 1) &&
        proposal.Reward > 0 && proposal.Reward <= 200_000;

    public static string CommandKey(NetworkRequestFourberieOperation request)
    {
        var builder = new StringBuilder();
        builder.Append(request.SessionId).Append('|')
            .Append(request.ExpectedRevision.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(((int)request.Operation).ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(request.SettlementId).Append('|').Append(request.TargetId).Append('|')
            .Append(request.SecondaryTargetId).Append('|')
            .Append(request.IntValue.ToString(CultureInfo.InvariantCulture));
        foreach (FourberieTroopSelection troop in request.Troops.OrderBy(value => value.TroopId, StringComparer.Ordinal))
            builder.Append('|').Append(troop.TroopId).Append(':')
                .Append(troop.Count.ToString(CultureInfo.InvariantCulture));
        foreach (FourberieItemSelection item in request.Items
                     .OrderBy(value => value.ItemId, StringComparer.Ordinal)
                     .ThenBy(value => value.ItemModifierId, StringComparer.Ordinal))
            builder.Append('|').Append(item.ItemId).Append(':').Append(item.ItemModifierId).Append(':')
                .Append(item.DeltaToSafehouse.ToString(CultureInfo.InvariantCulture));
        foreach (string objectId in request.ObjectIds.OrderBy(value => value, StringComparer.Ordinal))
            builder.Append("|object:").Append(objectId);
        foreach (FourberieRosterSelection selection in request.Roster.OrderBy(value => value.TroopId, StringComparer.Ordinal))
            builder.Append("|roster:").Append(selection.TroopId).Append(':')
                .Append(selection.MemberDeltaToActor.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(selection.PrisonerDeltaToActor.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    public static bool CanApplyAtRevision(FourberieOperation operation, long expected, long current) =>
        expected == current || IsAbsoluteSetting(operation) && expected >= 0 && expected < current;

    public static bool IsAbsoluteSetting(FourberieOperation operation) => operation is
        FourberieOperation.SetCorruptionLevel or
        FourberieOperation.SetAutoInvestment or
        FourberieOperation.SetLadsDuty or
        FourberieOperation.SetSlavesDuty or
        FourberieOperation.EnsureSchemeRoomDefaults or
        FourberieOperation.ClearDominanceConversation;

    private static bool IsStableId(string value, bool allowEmpty)
    {
        if (string.IsNullOrEmpty(value)) return allowEmpty;
        return value.Length <= MaxStableIdLength && value.All(character =>
            !char.IsControl(character) && character != '|' && character != ':');
    }

    private static bool IsSessionId(string value) =>
        value != null && value.Length == 32 && Guid.TryParseExact(value, "N", out _);

    private static bool EmptyTargets(NetworkRequestFourberieOperation request) =>
        string.IsNullOrEmpty(request.TargetId) && string.IsNullOrEmpty(request.SecondaryTargetId);

    private static bool EmptyContext(NetworkRequestFourberieOperation request) =>
        string.IsNullOrEmpty(request.SettlementId) && EmptyTargets(request) && request.Troops.Length == 0;

    private static bool IsBusinessKey(int key) =>
        key == 11 || key == 12 || key == 21 || key == 22 || key == 31 || key == 32;

    private static bool IsRoleCode(int value) => value == 1 || value == 2;

    private static bool IsSchemeSlot(int value) => value == 7 || value == 8;

    private static bool IsSchemeSelection(int value) =>
        FourberieSchemeAuthority.TryDecodeSelection(value, out _, out _);

    private static bool IsCorruptionLevel(int value) =>
        value == 1 || value == 2 || value == 3 || value == 10;

    internal static bool IsStealthEvent(int value) =>
        Enum.IsDefined(typeof(FourberieStealthEvent), value);

    internal static bool StealthEventRequiresTarget(int value) =>
        value == (int)FourberieStealthEvent.LordWounded;

    internal static bool IsBanditEvent(int value) =>
        Enum.IsDefined(typeof(FourberieBanditEvent), value);

    private static bool IsBanditEventShapeValid(NetworkRequestFourberieOperation request)
    {
        if (!IsBanditEvent(request.IntValue))
            return false;

        var value = (FourberieBanditEvent)request.IntValue;
        bool settlementRequired = value is FourberieBanditEvent.RepairShips or
            FourberieBanditEvent.HealWounds or FourberieBanditEvent.ReleaseAllFollowers or
            FourberieBanditEvent.AcceptTruce or FourberieBanditEvent.BreakTruce or
            FourberieBanditEvent.BetrayBandits or FourberieBanditEvent.SelectWarDogKingdom or
            FourberieBanditEvent.AcquireCoveShip or FourberieBanditEvent.DonatePrisoners or
            FourberieBanditEvent.PrepareRecruitment or FourberieBanditEvent.OpenBanditStash or
            FourberieBanditEvent.RefreshBlackMarket or FourberieBanditEvent.StartHideoutWait or
            FourberieBanditEvent.StopHideoutWait or FourberieBanditEvent.DonateLoot;
        if (settlementRequired != !string.IsNullOrEmpty(request.SettlementId)) return false;
        return value switch
        {
            FourberieBanditEvent.RepairShips or FourberieBanditEvent.HealWounds or
                FourberieBanditEvent.ReleaseAllFollowers or
                FourberieBanditEvent.AcceptTruce or FourberieBanditEvent.BreakTruce or
                FourberieBanditEvent.BetrayBandits =>
                string.IsNullOrEmpty(request.TargetId) && request.ObjectIds.Length == 0 &&
                string.IsNullOrEmpty(request.SecondaryTargetId) && request.Troops.Length == 0 &&
                request.Roster.Length == 0 && request.Items.Length == 0,
            FourberieBanditEvent.FollowParties =>
                string.IsNullOrEmpty(request.TargetId) && request.ObjectIds.Length > 0 &&
                string.IsNullOrEmpty(request.SecondaryTargetId) && request.Troops.Length == 0 &&
                request.Roster.Length == 0 && request.Items.Length == 0,
            FourberieBanditEvent.RefuseBanditJoin or FourberieBanditEvent.StopFollower or
                FourberieBanditEvent.SelectWarDogKingdom or FourberieBanditEvent.AcquireCoveShip =>
                !string.IsNullOrEmpty(request.TargetId) && request.ObjectIds.Length == 0 &&
                string.IsNullOrEmpty(request.SecondaryTargetId) && request.Troops.Length == 0 &&
                request.Roster.Length == 0 && request.Items.Length == 0,
            FourberieBanditEvent.TransferFollowerShip =>
                !string.IsNullOrEmpty(request.TargetId) && request.ObjectIds.Length == 0 &&
                IsShipSelection(request.SecondaryTargetId) && request.Troops.Length == 0 &&
                request.Roster.Length == 0 && request.Items.Length == 0,
            FourberieBanditEvent.DonatePrisoners =>
                string.IsNullOrEmpty(request.TargetId) && request.ObjectIds.Length == 0 &&
                string.IsNullOrEmpty(request.SecondaryTargetId) && request.Troops.Length > 0 &&
                request.Roster.Length == 0 && request.Items.Length == 0,
            FourberieBanditEvent.CommitBanditRoster =>
                !string.IsNullOrEmpty(request.TargetId) && request.ObjectIds.Length == 0 &&
                (string.IsNullOrEmpty(request.SecondaryTargetId) || request.SecondaryTargetId == "recruit.all") &&
                request.Troops.Length == 0 &&
                request.Roster.Length > 0 && request.Items.Length == 0,
            FourberieBanditEvent.PrepareRecruitment or FourberieBanditEvent.OpenBanditStash or
                FourberieBanditEvent.RefreshBlackMarket or FourberieBanditEvent.StartHideoutWait or
                FourberieBanditEvent.StopHideoutWait =>
                string.IsNullOrEmpty(request.TargetId) && request.ObjectIds.Length == 0 &&
                string.IsNullOrEmpty(request.SecondaryTargetId) && request.Troops.Length == 0 &&
                request.Roster.Length == 0 && request.Items.Length == 0,
            FourberieBanditEvent.DonateLoot =>
                string.IsNullOrEmpty(request.TargetId) && request.ObjectIds.Length == 0 &&
                string.IsNullOrEmpty(request.SecondaryTargetId) && request.Troops.Length == 0 &&
                request.Roster.Length == 0 && request.Items.Length > 0,
            _ => false,
        };
    }

    private static bool IsShipSelection(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        string[] parts = value.Split('.');
        return parts.Length == 2 && (parts[0] == "actor" || parts[0] == "follower") &&
               int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
               index >= 0 && index < 64;
    }

    internal static bool IsLegacyCallbackToken(int value) => LegacyCallbackTokens.Contains(value);

    internal static bool IsConversationEventToken(int value) => ConversationEventTokens.ContainsKey(value);

    internal static FourberieConversationEvent ConversationEventForToken(int value) =>
        ConversationEventTokens.TryGetValue(value, out FourberieConversationEvent result) ? result : 0;

    private static bool IsConversationEventShapeValid(NetworkRequestFourberieOperation request)
    {
        if (!Enum.IsDefined(typeof(FourberieConversationEvent), request.IntValue) ||
            string.IsNullOrEmpty(request.SettlementId) ||
            !string.IsNullOrEmpty(request.SecondaryTargetId) || request.Troops.Length != 0)
            return false;
        return (FourberieConversationEvent)request.IntValue == FourberieConversationEvent.ResolveGangLeaderBashing
            ? string.IsNullOrEmpty(request.TargetId)
            : !string.IsNullOrEmpty(request.TargetId);
    }

    private static bool IsCampaignConsequenceShapeValid(NetworkRequestFourberieOperation request)
    {
        if (!Enum.IsDefined(typeof(FourberieCampaignConsequence), request.IntValue) ||
            !string.IsNullOrEmpty(request.SecondaryTargetId) || request.Troops.Length != 0 ||
            request.Items.Length != 0 || request.ObjectIds.Length != 0 || request.Roster.Length != 0)
            return false;
        var consequence = (FourberieCampaignConsequence)request.IntValue;
        if (consequence == FourberieCampaignConsequence.SafehouseCompanionRelation)
            return !string.IsNullOrEmpty(request.TargetId);
        if (consequence == FourberieCampaignConsequence.BribeGuard)
            return !string.IsNullOrEmpty(request.SettlementId) && string.IsNullOrEmpty(request.TargetId);
        return string.IsNullOrEmpty(request.TargetId);
    }

    private static bool IsCriminalConsequenceShapeValid(NetworkRequestFourberieOperation request)
    {
        if (!Enum.IsDefined(typeof(FourberieCriminalConsequence), request.IntValue) ||
            request.Troops.Length != 0 || request.Items.Length != 0 || request.Roster.Length != 0)
            return false;
        var value = (FourberieCriminalConsequence)request.IntValue;
        bool political = value is FourberieCriminalConsequence.PayRiotInfluence or
            FourberieCriminalConsequence.DefectRiotVictim or FourberieCriminalConsequence.DeclareRiotWar or
            FourberieCriminalConsequence.BanishRiotActor;
        if (!political && string.IsNullOrEmpty(request.SettlementId)) return false;
        return value switch
        {
            FourberieCriminalConsequence.ClearRivalry or FourberieCriminalConsequence.PromoteCompanion =>
                !string.IsNullOrEmpty(request.TargetId) && string.IsNullOrEmpty(request.SecondaryTargetId) &&
                request.ObjectIds.Length == 0,
            FourberieCriminalConsequence.GatherFollowers =>
                string.IsNullOrEmpty(request.TargetId) && string.IsNullOrEmpty(request.SecondaryTargetId) &&
                request.ObjectIds.Length > 0,
            FourberieCriminalConsequence.Fortune =>
                string.IsNullOrEmpty(request.TargetId) && request.ObjectIds.Length == 0 &&
                request.SecondaryTargetId.StartsWith("attempts.", StringComparison.Ordinal) &&
                int.TryParse(request.SecondaryTargetId.Substring(9), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int attempts) && attempts >= 1 && attempts <= 10,
            FourberieCriminalConsequence.ManageWorkshopOwner =>
                !string.IsNullOrEmpty(request.TargetId) && request.ObjectIds.Length == 0 &&
                IsWorkshopSelection(request.SecondaryTargetId, requireType: false),
            FourberieCriminalConsequence.ConvertWorkshop =>
                string.IsNullOrEmpty(request.TargetId) && request.ObjectIds.Length == 0 &&
                IsWorkshopSelection(request.SecondaryTargetId, requireType: true),
            FourberieCriminalConsequence.PickAction or FourberieCriminalConsequence.PickFailure =>
                request.ObjectIds.Length == 0 &&
                (!string.IsNullOrEmpty(request.TargetId) || request.SecondaryTargetId.Contains("|character.")) &&
                request.SecondaryTargetId.StartsWith(value == FourberieCriminalConsequence.PickAction ? "pick." : "fail.",
                    StringComparison.Ordinal),
            FourberieCriminalConsequence.CaravanAmbushResult or FourberieCriminalConsequence.TributeResult or
            FourberieCriminalConsequence.ExtortionResult =>
                string.IsNullOrEmpty(request.TargetId) && request.ObjectIds.Length == 0 &&
                (request.SecondaryTargetId == "result.0" || request.SecondaryTargetId == "result.1"),
            FourberieCriminalConsequence.RiotResult =>
                string.IsNullOrEmpty(request.TargetId) && request.ObjectIds.Length == 0 &&
                request.SecondaryTargetId.StartsWith("riot.", StringComparison.Ordinal),
            FourberieCriminalConsequence.AbandonLarceny =>
                !string.IsNullOrEmpty(request.TargetId) && string.IsNullOrEmpty(request.SecondaryTargetId) &&
                request.ObjectIds.Length == 0,
            FourberieCriminalConsequence.PayRiotInfluence or FourberieCriminalConsequence.DefectRiotVictim or
            FourberieCriminalConsequence.DeclareRiotWar or FourberieCriminalConsequence.BanishRiotActor =>
                !string.IsNullOrEmpty(request.TargetId) && string.IsNullOrEmpty(request.SecondaryTargetId) &&
                request.ObjectIds.Length == 0,
            _ => string.IsNullOrEmpty(request.SecondaryTargetId) &&
                 string.IsNullOrEmpty(request.TargetId) && request.ObjectIds.Length == 0,
        };
    }

    private static bool IsWorkshopSelection(string value, bool requireType)
    {
        if (string.IsNullOrEmpty(value)) return false;
        string[] parts = value.Split('|');
        if (parts.Length != (requireType ? 2 : 1) || !parts[0].StartsWith("workshop.", StringComparison.Ordinal) ||
            !int.TryParse(parts[0].Substring(9), NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
            index < 0 || index >= 64) return false;
        return !requireType || parts[1].StartsWith("type.", StringComparison.Ordinal) && parts[1].Length > 5;
    }

    private static bool IsLegacyCallbackShapeValid(NetworkRequestFourberieOperation request) =>
        IsLegacyCallbackToken(request.IntValue) && !string.IsNullOrEmpty(request.SettlementId) &&
        string.IsNullOrEmpty(request.TargetId) && string.IsNullOrEmpty(request.SecondaryTargetId) &&
        request.Troops.Length == 0 && request.Items.Length == 0 && request.ObjectIds.Length == 0 &&
        request.Roster.Length == 0;

    private static readonly HashSet<int> LegacyCallbackTokens = new HashSet<int>
    {
        0x060007C1, 0x060007E8, 0x060007EA,
        0x0600094E, 0x0600096D, 0x0600098E, 0x06000998,
        0x060009A3, 0x060009B1, 0x060009B6, 0x060009B7,
        0x060009C0, 0x060009D4, 0x060009E6,
        0x060009F9, 0x060009FA, 0x060009FE, 0x06000A03,
        0x06000A11, 0x06000A12, 0x06000A13, 0x06000A15,
    };

    private static readonly IReadOnlyDictionary<int, FourberieConversationEvent> ConversationEventTokens =
        new Dictionary<int, FourberieConversationEvent>
        {
            [0x060007D7] = FourberieConversationEvent.PromoteGangLeader,
            [0x060007DC] = FourberieConversationEvent.EstablishPartnership,
            [0x060007DE] = FourberieConversationEvent.AcceptRecommendation,
            [0x060007E1] = FourberieConversationEvent.RejectRivalry,
            [0x060007E2] = FourberieConversationEvent.ResolveGangLeaderBashing,
            [0x060007E3] = FourberieConversationEvent.RejectBashing,
        };
}
