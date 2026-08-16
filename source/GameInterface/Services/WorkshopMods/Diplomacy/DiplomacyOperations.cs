using Common.Messaging;
using GameInterface.Configuration;
using GameInterface.Services.WorkshopMods.Core;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

internal enum DiplomacyOperation
{
    DonateGold = 1,
    GrantFief = 2,
    SendMessenger = 3,
    MakePeace = 4,
    DeclareWar = 5,
    EndAlliance = 6,
    FormNonAggressionPact = 7,
    AcceptKeepFief = 8,
    DeclineKeepFief = 9,
    CompleteMessenger = 10,
    CancelMessenger = 11,
    AcknowledgeMessengerAccident = 12,
}

internal enum DiplomacyOperationStatus
{
    Accepted = 1,
    Rejected = 2,
    StaleSession = 3,
    StaleState = 4,
    Failed = 5,
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkRequestDiplomacyOperation : ICommand
{
    [ProtoMember(1)] public string SessionId { get; private set; }
    [ProtoMember(2)] public long RequestId { get; private set; }
    [ProtoMember(3)] public long ExpectedRevision { get; private set; }
    [ProtoMember(4)] public DiplomacyOperation Operation { get; private set; }
    [ProtoMember(5)] public string TargetId { get; private set; }
    [ProtoMember(6)] public string SecondaryTargetId { get; private set; }
    [ProtoMember(7)] public int IntValue { get; private set; }

    private NetworkRequestDiplomacyOperation()
    {
    }

    public NetworkRequestDiplomacyOperation(
        string sessionId,
        long requestId,
        long expectedRevision,
        DiplomacyOperation operation,
        string targetId,
        string secondaryTargetId,
        int intValue)
    {
        SessionId = sessionId;
        RequestId = requestId;
        ExpectedRevision = expectedRevision;
        Operation = operation;
        TargetId = targetId ?? string.Empty;
        SecondaryTargetId = secondaryTargetId ?? string.Empty;
        IntValue = intValue;
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkDiplomacyOperationResult : ICommand
{
    [ProtoMember(1)] public string SessionId { get; private set; }
    [ProtoMember(2)] public long RequestId { get; private set; }
    [ProtoMember(3)] public DiplomacyOperation Operation { get; private set; }
    [ProtoMember(4)] public DiplomacyOperationStatus Status { get; private set; }
    [ProtoMember(5)] public long Revision { get; private set; }
    [ProtoMember(6)] public string TargetId { get; private set; }

    private NetworkDiplomacyOperationResult()
    {
    }

    public NetworkDiplomacyOperationResult(
        string sessionId,
        long requestId,
        DiplomacyOperation operation,
        DiplomacyOperationStatus status,
        long revision,
        string targetId)
    {
        SessionId = sessionId;
        RequestId = requestId;
        Operation = operation;
        Status = status;
        Revision = revision;
        TargetId = targetId ?? string.Empty;
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkDiplomacyKeepFiefPrompt : ICommand
{
    [ProtoMember(1)] public string SessionId { get; private set; }
    [ProtoMember(2)] public string SettlementId { get; private set; }
    [ProtoMember(3)] public long Revision { get; private set; }

    private NetworkDiplomacyKeepFiefPrompt()
    {
    }

    public NetworkDiplomacyKeepFiefPrompt(string sessionId, string settlementId, long revision)
    {
        SessionId = sessionId;
        SettlementId = settlementId ?? string.Empty;
        Revision = revision;
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkDiplomacyMessengerArrivalPrompt : ICommand
{
    [ProtoMember(1)] public string SessionId { get; private set; }
    [ProtoMember(2)] public int MessengerId { get; private set; }
    [ProtoMember(3)] public string TargetId { get; private set; }
    [ProtoMember(4)] public long Revision { get; private set; }

    private NetworkDiplomacyMessengerArrivalPrompt()
    {
    }

    public NetworkDiplomacyMessengerArrivalPrompt(
        string sessionId,
        int messengerId,
        string targetId,
        long revision)
    {
        SessionId = sessionId;
        MessengerId = messengerId;
        TargetId = targetId ?? string.Empty;
        Revision = revision;
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkDiplomacyMessengerAccident : ICommand
{
    [ProtoMember(1)] public string SessionId { get; private set; }
    [ProtoMember(2)] public int MessengerId { get; private set; }
    [ProtoMember(3)] public string TargetId { get; private set; }
    [ProtoMember(4)] public int AccidentIndex { get; private set; }

    private NetworkDiplomacyMessengerAccident()
    {
    }

    public NetworkDiplomacyMessengerAccident(
        string sessionId,
        int messengerId,
        string targetId,
        int accidentIndex)
    {
        SessionId = sessionId;
        MessengerId = messengerId;
        TargetId = targetId ?? string.Empty;
        AccidentIndex = accidentIndex;
    }
}

internal static class DiplomacyOperationProtocol
{
    internal const int MaximumStableIdLength = 256;
    internal const int MaximumGoldAmount = 2_000_000_000;

    public static bool IsRequestShapeValid(NetworkRequestDiplomacyOperation request)
    {
        if (request == null || !IsSessionId(request.SessionId) || request.RequestId <= 0 ||
            request.ExpectedRevision < 0 || !Enum.IsDefined(typeof(DiplomacyOperation), request.Operation) ||
            !IsStableId(request.TargetId, allowEmpty: true) ||
            !IsStableId(request.SecondaryTargetId, allowEmpty: true) ||
            request.IntValue < 0 || request.IntValue > MaximumGoldAmount)
            return false;

        bool targetOnly = !string.IsNullOrEmpty(request.TargetId) &&
                          string.IsNullOrEmpty(request.SecondaryTargetId) && request.IntValue == 0;
        bool pair = !string.IsNullOrEmpty(request.TargetId) &&
                    !string.IsNullOrEmpty(request.SecondaryTargetId) && request.IntValue == 0;

        return request.Operation switch
        {
            DiplomacyOperation.DonateGold =>
                !string.IsNullOrEmpty(request.TargetId) &&
                string.IsNullOrEmpty(request.SecondaryTargetId) && request.IntValue > 0,
            DiplomacyOperation.GrantFief => pair,
            DiplomacyOperation.SendMessenger => targetOnly,
            DiplomacyOperation.MakePeace => pair,
            DiplomacyOperation.DeclareWar => pair,
            DiplomacyOperation.EndAlliance => pair,
            DiplomacyOperation.FormNonAggressionPact => pair,
            DiplomacyOperation.AcceptKeepFief => targetOnly,
            DiplomacyOperation.DeclineKeepFief => targetOnly,
            DiplomacyOperation.CompleteMessenger or DiplomacyOperation.CancelMessenger or
                DiplomacyOperation.AcknowledgeMessengerAccident =>
                !string.IsNullOrEmpty(request.TargetId) &&
                string.IsNullOrEmpty(request.SecondaryTargetId) && request.IntValue > 0,
            _ => false,
        };
    }

    public static bool IsResultShapeValid(NetworkDiplomacyOperationResult result) =>
        result != null && IsSessionId(result.SessionId) && result.RequestId > 0 &&
        Enum.IsDefined(typeof(DiplomacyOperation), result.Operation) &&
        Enum.IsDefined(typeof(DiplomacyOperationStatus), result.Status) &&
        result.Revision >= 0 && IsStableId(result.TargetId, allowEmpty: false);

    public static bool IsKeepFiefPromptShapeValid(NetworkDiplomacyKeepFiefPrompt prompt) =>
        prompt != null && IsSessionId(prompt.SessionId) && prompt.Revision >= 0 &&
        IsStableId(prompt.SettlementId, allowEmpty: false);

    public static bool IsMessengerArrivalPromptShapeValid(
        NetworkDiplomacyMessengerArrivalPrompt prompt) =>
        prompt != null && IsSessionId(prompt.SessionId) && prompt.MessengerId > 0 &&
        prompt.Revision >= 0 && IsStableId(prompt.TargetId, allowEmpty: false);

    public static bool IsMessengerAccidentShapeValid(NetworkDiplomacyMessengerAccident accident) =>
        accident != null && IsSessionId(accident.SessionId) && accident.MessengerId > 0 &&
        accident.AccidentIndex is >= 0 and <= 6 && IsStableId(accident.TargetId, allowEmpty: false);

    public static string CommandKey(NetworkRequestDiplomacyOperation request) => string.Join(
        "|",
        request.SessionId,
        request.ExpectedRevision.ToString(CultureInfo.InvariantCulture),
        ((int)request.Operation).ToString(CultureInfo.InvariantCulture),
        request.TargetId,
        request.SecondaryTargetId,
        request.IntValue.ToString(CultureInfo.InvariantCulture));

    private static bool IsStableId(string value, bool allowEmpty)
    {
        if (string.IsNullOrEmpty(value)) return allowEmpty;
        return value.Length <= MaximumStableIdLength && value.All(character =>
            !char.IsControl(character) && character != '|');
    }

    private static bool IsSessionId(string value) =>
        value != null && value.Length == 32 && Guid.TryParseExact(value, "N", out _);
}

internal enum DiplomacyReplayDecision
{
    New,
    Replay,
    Conflict,
}

internal sealed class DiplomacyRequestLedger<TKey>
{
    private sealed class Entry
    {
        public Entry(string commandKey, NetworkDiplomacyOperationResult result)
        {
            CommandKey = commandKey;
            Result = result;
        }

        public string CommandKey { get; }
        public NetworkDiplomacyOperationResult Result { get; }
    }

    private readonly object sync = new();
    private readonly Dictionary<TKey, Dictionary<long, Entry>> entries = new();
    private readonly Dictionary<TKey, long> highWater = new();
    private readonly int capacity;

    public DiplomacyRequestLedger(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public DiplomacyReplayDecision Inspect(
        TKey peer,
        long requestId,
        string commandKey,
        out NetworkDiplomacyOperationResult result)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(peer, out var peerEntries) ||
                !peerEntries.TryGetValue(requestId, out var entry))
            {
                if (highWater.TryGetValue(peer, out long highestRecorded) && requestId <= highestRecorded)
                {
                    result = null;
                    return DiplomacyReplayDecision.Conflict;
                }
                result = null;
                return DiplomacyReplayDecision.New;
            }

            result = entry.Result;
            return string.Equals(entry.CommandKey, commandKey, StringComparison.Ordinal)
                ? DiplomacyReplayDecision.Replay
                : DiplomacyReplayDecision.Conflict;
        }
    }

    public void Record(
        TKey peer,
        long requestId,
        string commandKey,
        NetworkDiplomacyOperationResult result)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(peer, out var peerEntries))
            {
                peerEntries = new Dictionary<long, Entry>();
                entries.Add(peer, peerEntries);
            }
            if (peerEntries.ContainsKey(requestId)) return;
            if (peerEntries.Count >= capacity) peerEntries.Remove(peerEntries.Keys.Min());
            peerEntries.Add(requestId, new Entry(commandKey, result));
            if (!highWater.TryGetValue(peer, out long previous) || requestId > previous)
                highWater[peer] = requestId;
        }
    }

    public void Reset()
    {
        lock (sync)
        {
            entries.Clear();
            highWater.Clear();
        }
    }
}

internal static class DiplomacyCapabilityPolicy
{
    public static bool IsEnabled(bool optionEnabled, bool routeReady) => optionEnabled && routeReady;
}

internal static class DiplomacyActorAuthority
{
    public static bool CanExecute(
        DiplomacyOperation operation,
        bool actorBelongsToRegisteredParty,
        bool actorLeadsRegisteredParty,
        bool actorHasClan)
    {
        if (!actorBelongsToRegisteredParty || !actorHasClan) return false;

        // A joined player embedded in the clan leader's party can still perform this personal action.
        // Clan, kingdom, fief, and gold actions remain controlled by the party leader.
        return actorLeadsRegisteredParty || operation == DiplomacyOperation.SendMessenger;
    }
}

internal sealed class DiplomacyCapabilitySource : IWorkshopCapabilitySource
{
    internal const string ModuleId = "Bannerlord.Diplomacy";
    internal const string Operation = "Gameplay";

    private readonly IModConfig modConfig;

    public DiplomacyCapabilitySource(IModConfig modConfig)
    {
        this.modConfig = modConfig;
    }

    public IEnumerable<WorkshopCapability> CaptureCapabilities()
    {
        var options = modConfig.Data == null
            ? ModConfigProvider.ModOptions
            : new ModOptions(modConfig.Data.ModOptions ?? new ModOptionsData());
        bool optionEnabled = options.IsWorkshopModuleEnabled(ModuleId);
        // The legacy operation transport is not an authority route. Task 4 may establish a
        // snapshot, but it must never advertise gameplay until Task 6 owns the real command.
        bool enabled = false;
        yield return new WorkshopCapability(
            ModuleId,
            Operation,
            enabled,
            enabled ? string.Empty : optionEnabled
                ? "authority-command-route-unavailable"
                : "module-disabled");
    }
}
