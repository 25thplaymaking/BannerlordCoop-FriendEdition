using Common.Messaging;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GameInterface.Services.Separatism;

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestSeparatismRecruitment : ICommand
{
    [ProtoMember(1)] public readonly string SessionId;
    [ProtoMember(2)] public readonly long RequestId;
    [ProtoMember(3)] public readonly long ExpectedRevision;
    [ProtoMember(4)] public readonly string ExpectedKingdomId;
    [ProtoMember(5)] public readonly string TargetClanId;
    [ProtoMember(6)] public readonly string TargetHeroId;

    public NetworkRequestSeparatismRecruitment(
        string sessionId,
        long requestId,
        long expectedRevision,
        string expectedKingdomId,
        string targetClanId,
        string targetHeroId)
    {
        SessionId = sessionId;
        RequestId = requestId;
        ExpectedRevision = expectedRevision;
        ExpectedKingdomId = expectedKingdomId;
        TargetClanId = targetClanId;
        TargetHeroId = targetHeroId;
    }
}

internal enum SeparatismRecruitmentStatus
{
    Accepted = 0,
    Malformed = 1,
    Unavailable = 2,
    Unauthorized = 3,
    StaleSession = 4,
    StaleState = 5,
    IneligibleActor = 6,
    IneligibleTarget = 7,
    AtWar = 8,
    Failed = 9,
    StaleRequest = 10,
    ConflictingRequest = 11,
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkSeparatismRecruitmentResult : ICommand
{
    [ProtoMember(1)] public readonly string SessionId;
    [ProtoMember(2)] public readonly long RequestId;
    [ProtoMember(3)] public readonly SeparatismRecruitmentStatus Status;
    [ProtoMember(4)] public readonly string TargetClanId;
    [ProtoMember(5)] public readonly long Revision;

    public NetworkSeparatismRecruitmentResult(
        string sessionId,
        long requestId,
        SeparatismRecruitmentStatus status,
        string targetClanId,
        long revision)
    {
        SessionId = sessionId;
        RequestId = requestId;
        Status = status;
        TargetClanId = targetClanId;
        Revision = revision;
    }
}

internal static class SeparatismRecruitmentProtocol
{
    internal const int MaximumObjectIdLength = 256;

    internal static bool IsRequestShapeValid(NetworkRequestSeparatismRecruitment request) =>
        IsSessionId(request.SessionId) &&
        request.RequestId > 0 &&
        request.ExpectedRevision >= 0 &&
        IsIdentifier(request.ExpectedKingdomId) &&
        IsIdentifier(request.TargetClanId) &&
        IsIdentifier(request.TargetHeroId);

    internal static bool IsResultShapeValid(NetworkSeparatismRecruitmentResult result) =>
        IsSessionId(result.SessionId) &&
        result.RequestId > 0 &&
        Enum.IsDefined(typeof(SeparatismRecruitmentStatus), result.Status) &&
        IsIdentifier(result.TargetClanId) &&
        result.Revision >= 0;

    internal static bool SameCommand(
        NetworkRequestSeparatismRecruitment left,
        NetworkRequestSeparatismRecruitment right) =>
        left.RequestId == right.RequestId &&
        left.ExpectedRevision == right.ExpectedRevision &&
        string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
        string.Equals(left.ExpectedKingdomId, right.ExpectedKingdomId, StringComparison.Ordinal) &&
        string.Equals(left.TargetClanId, right.TargetClanId, StringComparison.Ordinal) &&
        string.Equals(left.TargetHeroId, right.TargetHeroId, StringComparison.Ordinal);

    private static bool IsSessionId(string value) =>
        value != null &&
        value.Length == 32 &&
        Guid.TryParseExact(value, "N", out _);

    private static bool IsIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumObjectIdLength &&
        !value.Any(char.IsControl);
}

internal enum SeparatismReplayDecision
{
    New,
    Replay,
    Conflict,
    Stale,
}

internal sealed class SeparatismRequestReplayLedger<TKey>
{
    private sealed class ControllerRequests
    {
        public readonly Dictionary<long, Entry> Entries = new();
        public readonly Queue<long> Order = new();
        public long HighestRequestId;
    }

    private readonly struct Entry
    {
        public readonly NetworkRequestSeparatismRecruitment Request;
        public readonly NetworkSeparatismRecruitmentResult Result;

        public Entry(
            NetworkRequestSeparatismRecruitment request,
            NetworkSeparatismRecruitmentResult result)
        {
            Request = request;
            Result = result;
        }
    }

    private readonly object gate = new();
    private readonly Dictionary<TKey, ControllerRequests> controllers = new();
    private readonly int capacityPerController;

    public SeparatismRequestReplayLedger(int capacityPerController)
    {
        if (capacityPerController < 1)
            throw new ArgumentOutOfRangeException(nameof(capacityPerController));
        this.capacityPerController = capacityPerController;
    }

    public SeparatismReplayDecision Inspect(
        TKey controller,
        NetworkRequestSeparatismRecruitment request,
        out NetworkSeparatismRecruitmentResult result)
    {
        lock (gate)
        {
            result = default;
            if (!controllers.TryGetValue(controller, out var requests))
                return SeparatismReplayDecision.New;

            if (requests.Entries.TryGetValue(request.RequestId, out var entry))
            {
                if (!SeparatismRecruitmentProtocol.SameCommand(entry.Request, request))
                    return SeparatismReplayDecision.Conflict;

                result = entry.Result;
                return SeparatismReplayDecision.Replay;
            }

            return request.RequestId <= requests.HighestRequestId
                ? SeparatismReplayDecision.Stale
                : SeparatismReplayDecision.New;
        }
    }

    public void Record(
        TKey controller,
        NetworkRequestSeparatismRecruitment request,
        NetworkSeparatismRecruitmentResult result)
    {
        lock (gate)
        {
            if (!controllers.TryGetValue(controller, out var requests))
            {
                requests = new ControllerRequests();
                controllers.Add(controller, requests);
            }

            if (requests.Entries.ContainsKey(request.RequestId)) return;
            requests.Entries.Add(request.RequestId, new Entry(request, result));
            requests.Order.Enqueue(request.RequestId);
            requests.HighestRequestId = Math.Max(requests.HighestRequestId, request.RequestId);

            while (requests.Order.Count > capacityPerController)
                requests.Entries.Remove(requests.Order.Dequeue());
        }
    }
}

internal static class SeparatismConversationPolicy
{
    internal static bool CanOfferFallenClanRecruitment(
        bool capabilityEnabled,
        bool actorHasKingdom,
        bool actorIsKingdomRuler,
        bool targetExists,
        bool targetHasNoKingdom,
        bool targetIsNotMinorFaction,
        bool targetIsFactionLeader,
        bool factionsAreAtPeace) =>
        capabilityEnabled &&
        actorHasKingdom &&
        actorIsKingdomRuler &&
        targetExists &&
        targetHasNoKingdom &&
        targetIsNotMinorFaction &&
        targetIsFactionLeader &&
        factionsAreAtPeace;
}

internal static class SeparatismCapabilityPolicy
{
    internal static bool AllowRecruitment(bool optionEnabled, bool routeReady) =>
        optionEnabled && routeReady;
}
