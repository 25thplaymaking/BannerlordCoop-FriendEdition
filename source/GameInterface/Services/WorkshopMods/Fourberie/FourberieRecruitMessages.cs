using Common.Messaging;
using ProtoBuf;
using System;
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

    public FourberieTroopSelection[] Troops => troops ?? Array.Empty<FourberieTroopSelection>();

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
            string.Empty, intValue, troops)
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
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkFourberieOperationResult : ICommand
{
    [ProtoMember(1)] public string SessionId { get; private set; }
    [ProtoMember(2)] public long RequestId { get; private set; }
    [ProtoMember(3)] public FourberieOperationStatus Status { get; private set; }
    [ProtoMember(4)] public long Revision { get; private set; }

    private NetworkFourberieOperationResult()
    {
    }

    public NetworkFourberieOperationResult(
        string sessionId,
        long requestId,
        FourberieOperationStatus status,
        long revision)
    {
        SessionId = sessionId;
        RequestId = requestId;
        Status = status;
        Revision = revision;
    }
}

internal static class FourberieOperationProtocol
{
    internal const int MaxTroopSelections = 64;
    internal const int MaxStableIdLength = 256;
    internal const int MaxSelectedTroops = 2_000;

    public static bool IsRequestShapeValid(NetworkRequestFourberieOperation request)
    {
        if (request == null || request.SessionId == null || request.SessionId.Length != 32 ||
            !Guid.TryParseExact(request.SessionId, "N", out _) || request.RequestId <= 0 ||
            request.ExpectedRevision < 0 || !Enum.IsDefined(typeof(FourberieOperation), request.Operation) ||
            !IsStableId(request.SettlementId, allowEmpty: true) ||
            !IsStableId(request.TargetId, allowEmpty: true) || request.IntValue < 0 ||
            !IsStableId(request.SecondaryTargetId, allowEmpty: true) ||
            request.IntValue > MaxSelectedTroops || request.Troops.Length > MaxTroopSelections)
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
                FourberieOperation.AbortContract =>
                EmptyContext(request) && request.IntValue == 0,
            _ => false,
        };
    }

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
        return builder.ToString();
    }

    public static bool CanApplyAtRevision(FourberieOperation operation, long expected, long current) =>
        expected == current || IsAbsoluteSetting(operation) && expected >= 0 && expected < current;

    public static bool IsAbsoluteSetting(FourberieOperation operation) => operation is
        FourberieOperation.SetCorruptionLevel or
        FourberieOperation.SetAutoInvestment or
        FourberieOperation.SetLadsDuty or
        FourberieOperation.SetSlavesDuty;

    private static bool IsStableId(string value, bool allowEmpty)
    {
        if (string.IsNullOrEmpty(value)) return allowEmpty;
        return value.Length <= MaxStableIdLength && value.All(character =>
            !char.IsControl(character) && character != '|' && character != ':');
    }

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
}
