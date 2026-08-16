using Common.Messaging;
using GameInterface.Configuration;
using GameInterface.Services.Tournaments.Data;
using ProtoBuf;
using System;
using System.Linq;

namespace GameInterface.Services.Tournaments.Messages;

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentRequestRejected : ICommand
{
    [ProtoMember(1)]
    public readonly string TownId;
    [ProtoMember(2)]
    public readonly string Reason;

    public NetworkTournamentRequestRejected(string townId, string reason)
    {
        TownId = townId;
        Reason = reason;
    }
}

[AuthorityRoute("tournament.join", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRequestJoinTournament : ICommand
{
    [ProtoMember(1)]
    public readonly string TownId;
    [ProtoMember(2)]
    public readonly string SessionId;
    [ProtoMember(3)]
    public readonly long ExpectedRevision;
    [ProtoMember(4)] public readonly int ProtocolVersion;
    [ProtoMember(5)] public readonly string ConfigSessionId;
    [ProtoMember(6)] public readonly long AuthorityRequestId;
    [ProtoMember(7)] public readonly long ConfigRevision;

    public NetworkRequestJoinTournament(AuthorityRequestHeader header, string townId, string sessionId, long expectedRevision)
    {
        TownId = townId;
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        ProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ConfigRevision = header.ExpectedRevision;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);
}

[AuthorityRoute("tournament.leave-preparation", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRequestLeaveTournamentPreparation : ICommand
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly long ExpectedRevision;
    [ProtoMember(3)] public readonly int ProtocolVersion;
    [ProtoMember(4)] public readonly string ConfigSessionId;
    [ProtoMember(5)] public readonly long AuthorityRequestId;
    [ProtoMember(6)] public readonly long ConfigRevision;

    public NetworkRequestLeaveTournamentPreparation(AuthorityRequestHeader header, string sessionId, long expectedRevision)
    {
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        ProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ConfigRevision = header.ExpectedRevision;
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentJoinResult : IEvent
{
    [ProtoMember(1)] public readonly TournamentSessionSnapshot Snapshot;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly AuthorityResultStatus Status;
    [ProtoMember(5)] public readonly long CommittedRevision;
    [ProtoMember(6)] public readonly string ReasonCode;

    public NetworkTournamentJoinResult(AuthorityRequestHeader request, AuthorityResultStatus status,
        TournamentSessionSnapshot snapshot, string reasonCode)
    {
        Snapshot = snapshot;
        SessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = request.ExpectedRevision;
        ReasonCode = reasonCode;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentLeavePreparationResult : IEvent
{
    [ProtoMember(1)] public readonly TournamentSessionSnapshot Snapshot;
    [ProtoMember(2)] public readonly string RemovedSessionId;
    [ProtoMember(3)] public readonly string RemovedTownId;
    [ProtoMember(4)] public readonly string SessionId;
    [ProtoMember(5)] public readonly long AuthorityRequestId;
    [ProtoMember(6)] public readonly AuthorityResultStatus Status;
    [ProtoMember(7)] public readonly long CommittedRevision;
    [ProtoMember(8)] public readonly string ReasonCode;

    public NetworkTournamentLeavePreparationResult(AuthorityRequestHeader request, AuthorityResultStatus status,
        TournamentSessionSnapshot snapshot, string removedSessionId, string removedTownId, string reasonCode)
    {
        Snapshot = snapshot;
        RemovedSessionId = removedSessionId;
        RemovedTownId = removedTownId;
        SessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = request.ExpectedRevision;
        ReasonCode = reasonCode;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[AuthorityRoute("tournament.start", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRequestStartTournament : ICommand
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly long ExpectedRevision;
    [ProtoMember(3)] public readonly int ProtocolVersion;
    [ProtoMember(4)] public readonly string ConfigSessionId;
    [ProtoMember(5)] public readonly long AuthorityRequestId;
    [ProtoMember(6)] public readonly long ConfigRevision;

    public NetworkRequestStartTournament(AuthorityRequestHeader header, string sessionId, long expectedRevision)
    {
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        ProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ConfigRevision = header.ExpectedRevision;
    }

    public AuthorityRequestHeader Header => new(ProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);
}

[AuthorityRoute("tournament.spectate", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRequestSpectateTournament : ICommand
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly long ExpectedRevision;
    [ProtoMember(3)] public readonly int ProtocolVersion;
    [ProtoMember(4)] public readonly string ConfigSessionId;
    [ProtoMember(5)] public readonly long AuthorityRequestId;
    [ProtoMember(6)] public readonly long ConfigRevision;

    public NetworkRequestSpectateTournament(AuthorityRequestHeader header, string sessionId, long expectedRevision)
    {
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        ProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ConfigRevision = header.ExpectedRevision;
    }

    public AuthorityRequestHeader Header => new(ProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentLaunchResult : IEvent
{
    [ProtoMember(1)] public readonly TournamentSessionSnapshot Snapshot;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly AuthorityResultStatus Status;
    [ProtoMember(5)] public readonly long CommittedRevision;
    [ProtoMember(6)] public readonly string ReasonCode;
    [ProtoMember(7)] public readonly string TournamentSessionId;
    [ProtoMember(8)] public readonly string MissionInstanceId;
    [ProtoMember(9)] public readonly string RequesterControllerId;
    [ProtoMember(10)] public readonly bool IsSpectator;

    public NetworkTournamentLaunchResult(AuthorityRequestHeader request, AuthorityResultStatus status,
        TournamentSessionSnapshot snapshot, string requesterControllerId, bool isSpectator, string reasonCode)
    {
        Snapshot = snapshot;
        SessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = snapshot?.Revision ?? request.ExpectedRevision;
        ReasonCode = reasonCode;
        TournamentSessionId = snapshot?.SessionId;
        MissionInstanceId = snapshot?.MissionInstanceId;
        RequesterControllerId = requesterControllerId;
        IsSpectator = isSpectator;
    }

    public AuthorityResultHeader Header => new(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[AuthorityRoute("tournament.leave-active", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRequestLeaveActiveTournament : ICommand
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly long ExpectedRevision;
    [ProtoMember(3)] public readonly int ProtocolVersion;
    [ProtoMember(4)] public readonly string ConfigSessionId;
    [ProtoMember(5)] public readonly long AuthorityRequestId;
    [ProtoMember(6)] public readonly long ConfigRevision;

    public NetworkRequestLeaveActiveTournament(AuthorityRequestHeader header, string sessionId, long expectedRevision)
    {
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        ProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ConfigRevision = header.ExpectedRevision;
    }

    public AuthorityRequestHeader Header => new(ProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentLeaveActiveResult : IEvent
{
    [ProtoMember(1)] public readonly TournamentSessionSnapshot Snapshot;
    [ProtoMember(2)] public readonly NetworkTournamentSessionRemoved Tombstone;
    [ProtoMember(3)] public readonly string SessionId;
    [ProtoMember(4)] public readonly long AuthorityRequestId;
    [ProtoMember(5)] public readonly AuthorityResultStatus Status;
    [ProtoMember(6)] public readonly long CommittedRevision;
    [ProtoMember(7)] public readonly string ReasonCode;

    public NetworkTournamentLeaveActiveResult(AuthorityRequestHeader request, AuthorityResultStatus status,
        TournamentSessionSnapshot snapshot, NetworkTournamentSessionRemoved tombstone, string reasonCode)
    {
        Snapshot = snapshot;
        Tombstone = tombstone;
        SessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = tombstone.TerminalRevision != 0
            ? tombstone.TerminalRevision
            : snapshot?.Revision ?? request.ExpectedRevision;
        ReasonCode = reasonCode;
    }

    public AuthorityResultHeader Header => new(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[AuthorityRoute("tournament.choice", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRequestTournamentChoice : ICommand
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly long ExpectedRevision;
    [ProtoMember(3)]
    public readonly string MatchId;
    [ProtoMember(4)]
    public readonly TournamentPlayerChoice Choice;
    [ProtoMember(5)] public readonly int ProtocolVersion;
    [ProtoMember(6)] public readonly string ConfigSessionId;
    [ProtoMember(7)] public readonly long AuthorityRequestId;
    [ProtoMember(8)] public readonly long ConfigRevision;
    [ProtoMember(9)] public readonly long BracketRevision;
    [ProtoMember(10)] public readonly string StructuralDigest;

    public NetworkRequestTournamentChoice(
        string sessionId,
        long expectedRevision,
        string matchId,
        TournamentPlayerChoice choice)
    {
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        MatchId = matchId;
        Choice = choice;
        ProtocolVersion = 0;
        ConfigSessionId = null;
        AuthorityRequestId = 0;
        ConfigRevision = 0;
        BracketRevision = 0;
        StructuralDigest = null;
    }

    public NetworkRequestTournamentChoice(AuthorityRequestHeader header, string sessionId, long expectedRevision,
        long bracketRevision, string matchId, TournamentPlayerChoice choice, string structuralDigest)
    {
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        MatchId = matchId;
        Choice = choice;
        ProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ConfigRevision = header.ExpectedRevision;
        BracketRevision = bracketRevision;
        StructuralDigest = structuralDigest;
    }

    public AuthorityRequestHeader Header => new(ProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);
}

[AuthorityRoute("tournament.bet", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkRequestTournamentBet : ICommand
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly long ExpectedRevision;
    [ProtoMember(3)]
    public readonly string MatchId;
    [ProtoMember(4)]
    public readonly int Amount;
    [ProtoMember(5)]
    public readonly long Sequence;
    [ProtoMember(6)] public readonly int ProtocolVersion;
    [ProtoMember(7)] public readonly string ConfigSessionId;
    [ProtoMember(8)] public readonly long AuthorityRequestId;
    [ProtoMember(9)] public readonly long ConfigRevision;
    [ProtoMember(10)] public readonly long BracketRevision;
    [ProtoMember(11)] public readonly int QuoteMaximumBet;
    [ProtoMember(12)] public readonly float QuoteOdd;

    public NetworkRequestTournamentBet(
        string sessionId,
        long expectedRevision,
        string matchId,
        int amount,
        long sequence)
    {
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        MatchId = matchId;
        Amount = amount;
        Sequence = sequence;
        ProtocolVersion = 0;
        ConfigSessionId = null;
        AuthorityRequestId = 0;
        ConfigRevision = 0;
        BracketRevision = 0;
        QuoteMaximumBet = 0;
        QuoteOdd = 0;
    }

    public NetworkRequestTournamentBet(AuthorityRequestHeader header, string sessionId, long expectedRevision,
        long bracketRevision, string matchId, int amount, long sequence, TournamentBetQuote quote)
    {
        SessionId = sessionId;
        ExpectedRevision = expectedRevision;
        MatchId = matchId;
        Amount = amount;
        Sequence = sequence;
        ProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ConfigRevision = header.ExpectedRevision;
        BracketRevision = bracketRevision;
        QuoteMaximumBet = quote?.MaximumBet ?? 0;
        QuoteOdd = quote?.Odd ?? 0;
    }

    public AuthorityRequestHeader Header => new(ProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);
}

[AuthorityRoute("tournament.spawn-manifest", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkSubmitTournamentSpawnManifest : ICommand
{
    [ProtoMember(1)] public readonly TournamentSpawnManifestData Manifest;
    [ProtoMember(2)] public readonly int ProtocolVersion;
    [ProtoMember(3)] public readonly string ConfigSessionId;
    [ProtoMember(4)] public readonly long AuthorityRequestId;
    [ProtoMember(5)] public readonly long ConfigRevision;
    [ProtoMember(6)] public readonly string MissionInstanceId;

    public NetworkSubmitTournamentSpawnManifest(AuthorityRequestHeader header, TournamentSpawnManifestData manifest, string missionInstanceId)
    {
        Manifest = manifest;
        ProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ConfigRevision = header.ExpectedRevision;
        MissionInstanceId = missionInstanceId;
    }

    public AuthorityRequestHeader Header => new(ProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);
}

[AuthorityRoute("tournament.match-result", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkSubmitTournamentMatchResult : ICommand
{
    [ProtoMember(1)] public readonly TournamentMatchResultData Result;
    [ProtoMember(2)] public readonly int ProtocolVersion;
    [ProtoMember(3)] public readonly string ConfigSessionId;
    [ProtoMember(4)] public readonly long AuthorityRequestId;
    [ProtoMember(5)] public readonly long ConfigRevision;
    [ProtoMember(6)] public readonly string MissionInstanceId;

    public NetworkSubmitTournamentMatchResult(AuthorityRequestHeader header, TournamentMatchResultData result, string missionInstanceId)
    {
        Result = result;
        ProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ConfigRevision = header.ExpectedRevision;
        MissionInstanceId = missionInstanceId;
    }

    public AuthorityRequestHeader Header => new(ProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentSpawnManifestResult : IEvent
{
    [ProtoMember(1)] public readonly TournamentSpawnManifestData Manifest;
    [ProtoMember(2)] public readonly TournamentSessionSnapshot Snapshot;
    [ProtoMember(3)] public readonly string SessionId;
    [ProtoMember(4)] public readonly long AuthorityRequestId;
    [ProtoMember(5)] public readonly AuthorityResultStatus Status;
    [ProtoMember(6)] public readonly long CommittedRevision;
    [ProtoMember(7)] public readonly string ReasonCode;
    [ProtoMember(8)] public readonly long CommittedBracketRevision;

    public NetworkTournamentSpawnManifestResult(AuthorityRequestHeader request, AuthorityResultStatus status,
        TournamentSpawnManifestData manifest, TournamentSessionSnapshot snapshot, string reasonCode)
    {
        Manifest = manifest; Snapshot = snapshot; SessionId = request.SessionId; AuthorityRequestId = request.RequestId;
        Status = status; CommittedRevision = snapshot?.Revision ?? manifest?.Revision ?? request.ExpectedRevision;
        ReasonCode = reasonCode; CommittedBracketRevision = snapshot?.BracketRevision ?? manifest?.BracketRevision ?? 0;
    }
    public AuthorityResultHeader Header => new(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentMatchResultApplied : IEvent
{
    [ProtoMember(1)] public readonly TournamentSessionSnapshot Snapshot;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly AuthorityResultStatus Status;
    [ProtoMember(5)] public readonly long CommittedRevision;
    [ProtoMember(6)] public readonly string ReasonCode;
    [ProtoMember(7)] public readonly long CommittedBracketRevision;
    [ProtoMember(8)] public readonly string TournamentSessionId;
    [ProtoMember(9)] public readonly string MatchId;
    [ProtoMember(10)] public readonly long Sequence;

    public NetworkTournamentMatchResultApplied(AuthorityRequestHeader request, AuthorityResultStatus status,
        TournamentSessionSnapshot snapshot, TournamentMatchResultData result, string reasonCode)
    {
        Snapshot = snapshot; SessionId = request.SessionId; AuthorityRequestId = request.RequestId; Status = status;
        CommittedRevision = snapshot?.Revision ?? result?.Revision ?? request.ExpectedRevision; ReasonCode = reasonCode;
        CommittedBracketRevision = snapshot?.BracketRevision ?? result?.BracketRevision ?? 0;
        TournamentSessionId = result?.SessionId; MatchId = result?.MatchId; Sequence = result?.Sequence ?? 0;
    }
    public AuthorityResultHeader Header => new(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentSessionSnapshot : ICommand
{
    [ProtoMember(1)]
    public readonly TournamentSessionSnapshot Snapshot;

    public NetworkTournamentSessionSnapshot(TournamentSessionSnapshot snapshot)
    {
        Snapshot = snapshot;
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentSpawnManifest : ICommand
{
    [ProtoMember(1)]
    public readonly TournamentSpawnManifestData Manifest;

    public NetworkTournamentSpawnManifest(TournamentSpawnManifestData manifest)
    {
        Manifest = manifest;
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentBetResult : IEvent
{
    [ProtoMember(1)]
    public readonly string SessionId;
    [ProtoMember(2)]
    public readonly long Revision;
    [ProtoMember(3)]
    public readonly bool Accepted;
    [ProtoMember(4)]
    public readonly string Reason;
    [ProtoMember(5)]
    public readonly int BettedDenars;
    [ProtoMember(6)]
    public readonly int ExpectedPayout;
    [ProtoMember(7)]
    public readonly long Sequence;
    [ProtoMember(8)]
    public readonly string MatchId;
    [ProtoMember(9)]
    public readonly int ThisRoundBettedDenars;
    [ProtoMember(10)]
    public readonly bool IsSettlement;
    [ProtoMember(11)] public readonly string ConfigSessionId;
    [ProtoMember(12)] public readonly long AuthorityRequestId;
    [ProtoMember(13)] public readonly AuthorityResultStatus Status;
    [ProtoMember(14)] public readonly string ReasonCode;
    [ProtoMember(15)] public readonly string TournamentSessionId;
    [ProtoMember(16)] public readonly long BracketRevision;
    [ProtoMember(17)] public readonly string ControllerId;
    [ProtoMember(18)] public readonly string HeroId;
    [ProtoMember(19)] public readonly int HeroGold;

    public NetworkTournamentBetResult(
        string sessionId,
        long revision,
        long sequence,
        string matchId,
        bool accepted,
        string reason,
        int bettedDenars,
        int thisRoundBettedDenars,
        int expectedPayout,
        bool isSettlement)
    {
        SessionId = sessionId;
        Revision = revision;
        Sequence = sequence;
        MatchId = matchId;
        Accepted = accepted;
        Reason = reason;
        BettedDenars = bettedDenars;
        ThisRoundBettedDenars = thisRoundBettedDenars;
        ExpectedPayout = expectedPayout;
        IsSettlement = isSettlement;
        ConfigSessionId = null;
        AuthorityRequestId = 0;
        Status = accepted ? AuthorityResultStatus.Accepted : AuthorityResultStatus.Rejected;
        ReasonCode = reason;
        TournamentSessionId = sessionId;
        BracketRevision = 0;
        ControllerId = null;
        HeroId = null;
        HeroGold = 0;
    }

    public NetworkTournamentBetResult(AuthorityRequestHeader request, AuthorityResultStatus status,
        TournamentSessionSnapshot snapshot, string controllerId, string heroId, long sequence, int totalBet,
        int roundBet, int expectedPayout, int heroGold, string reasonCode)
    {
        SessionId = request.SessionId;
        Revision = snapshot?.Revision ?? request.ExpectedRevision;
        Accepted = status == AuthorityResultStatus.Accepted;
        Reason = reasonCode;
        BettedDenars = totalBet;
        ExpectedPayout = expectedPayout;
        Sequence = sequence;
        MatchId = snapshot?.CurrentMatchId;
        ThisRoundBettedDenars = roundBet;
        IsSettlement = false;
        ConfigSessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        ReasonCode = reasonCode;
        TournamentSessionId = snapshot?.SessionId;
        BracketRevision = snapshot?.BracketRevision ?? 0;
        ControllerId = controllerId;
        HeroId = heroId;
        HeroGold = heroGold;
    }

    public AuthorityResultHeader Header => new(ConfigSessionId, AuthorityRequestId, Status, Revision, ReasonCode);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentChoiceResult : IEvent
{
    [ProtoMember(1)] public readonly TournamentSessionSnapshot Snapshot;
    [ProtoMember(2)] public readonly string ConfigSessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly AuthorityResultStatus Status;
    [ProtoMember(5)] public readonly long CommittedRevision;
    [ProtoMember(6)] public readonly string ReasonCode;
    [ProtoMember(7)] public readonly string TournamentSessionId;
    [ProtoMember(8)] public readonly string MatchId;
    [ProtoMember(9)] public readonly long BracketRevision;
    [ProtoMember(10)] public readonly TournamentPlayerChoice Choice;
    [ProtoMember(11)] public readonly TournamentBallotOutcome Outcome;
    [ProtoMember(12)] public readonly string StructuralDigest;
    [ProtoMember(13)] public readonly string ControllerId;

    public NetworkTournamentChoiceResult(AuthorityRequestHeader request, AuthorityResultStatus status,
        TournamentSessionSnapshot snapshot, TournamentPlayerChoice choice, TournamentBallotOutcome outcome,
        string controllerId, string reasonCode)
    {
        Snapshot = snapshot;
        ConfigSessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = snapshot?.Revision ?? request.ExpectedRevision;
        ReasonCode = reasonCode;
        TournamentSessionId = snapshot?.SessionId;
        MatchId = snapshot?.CurrentMatchId;
        BracketRevision = snapshot?.BracketRevision ?? 0;
        Choice = choice;
        Outcome = outcome;
        StructuralDigest = TournamentAuthorityProtocol.StructuralDigest(snapshot);
        ControllerId = controllerId;
    }

    public AuthorityResultHeader Header => new(ConfigSessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentBetState : IEvent
{
    [ProtoMember(1)] public readonly string ConfigSessionId;
    [ProtoMember(2)] public readonly long AuthorityRequestId;
    [ProtoMember(3)] public readonly string TournamentSessionId;
    [ProtoMember(4)] public readonly long CommittedRevision;
    [ProtoMember(5)] public readonly string MatchId;
    [ProtoMember(6)] public readonly long Sequence;
    [ProtoMember(7)] public readonly string ControllerId;
    [ProtoMember(8)] public readonly string HeroId;
    [ProtoMember(9)] public readonly int HeroGold;
    [ProtoMember(10)] public readonly int TotalBettedDenars;
    [ProtoMember(11)] public readonly int ThisRoundBettedDenars;
    [ProtoMember(12)] public readonly int ExpectedPayout;
    [ProtoMember(13)] public readonly long BracketRevision;

    public NetworkTournamentBetState(AuthorityRequestHeader request, TournamentSessionSnapshot snapshot,
        long sequence, string controllerId, string heroId, int heroGold, int totalBettedDenars,
        int thisRoundBettedDenars, int expectedPayout)
    {
        ConfigSessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        TournamentSessionId = snapshot?.SessionId;
        CommittedRevision = snapshot?.Revision ?? request.ExpectedRevision;
        MatchId = snapshot?.CurrentMatchId;
        Sequence = sequence;
        ControllerId = controllerId;
        HeroId = heroId;
        HeroGold = heroGold;
        TotalBettedDenars = totalBettedDenars;
        ThisRoundBettedDenars = thisRoundBettedDenars;
        ExpectedPayout = expectedPayout;
        BracketRevision = snapshot?.BracketRevision ?? 0;
    }
}

internal static class TournamentAuthorityProtocol
{
    internal static string StructuralDigest(TournamentSessionSnapshot snapshot)
    {
        if (snapshot == null) return "missing";
        string voters = string.Join(",", (snapshot.Contestants ?? Array.Empty<TournamentContestantData>())
            .Where(contestant => contestant.IsHuman && !contestant.IsReplaced)
            .Select(contestant => contestant.ControllerId ?? string.Empty)
            .Concat(snapshot.SpectatorControllerIds ?? Array.Empty<string>())
            .OrderBy(controllerId => controllerId, StringComparer.Ordinal)
            .Select(Part));
        return string.Concat("v1|", Part(snapshot.SessionId), "|", Part(snapshot.CurrentMatchId), "|",
            snapshot.BracketRevision, "|", (int)snapshot.Phase, "|", voters);
    }

    internal static bool IsBoundedDigest(string value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 2048 && !value.Any(char.IsControl);

    private static string Part(string value) => string.Concat(value?.Length ?? -1, ":", value ?? string.Empty);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkEnterTournamentMission : ICommand
{
    [ProtoMember(1)]
    public readonly TournamentSessionSnapshot Snapshot;
    [ProtoMember(2)]
    public readonly bool IsSpectator;
    [ProtoMember(3)] public readonly string ConfigSessionId;
    [ProtoMember(4)] public readonly long AuthorityRequestId;
    [ProtoMember(5)] public readonly string TournamentSessionId;
    [ProtoMember(6)] public readonly string MissionInstanceId;
    [ProtoMember(7)] public readonly string RequesterControllerId;

    public NetworkEnterTournamentMission(AuthorityRequestHeader request, TournamentSessionSnapshot snapshot,
        string requesterControllerId, bool isSpectator)
    {
        Snapshot = snapshot;
        IsSpectator = isSpectator;
        ConfigSessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        TournamentSessionId = snapshot?.SessionId;
        MissionInstanceId = snapshot?.MissionInstanceId;
        RequesterControllerId = requesterControllerId;
    }

    public bool IsExactLaunch(AuthorityRequestHeader request, TournamentSessionSnapshot snapshot,
        string requesterControllerId, bool isSpectator) =>
        request.SessionId == ConfigSessionId && request.RequestId == AuthorityRequestId &&
        snapshot != null && snapshot.SessionId == TournamentSessionId &&
        snapshot.MissionInstanceId == MissionInstanceId && requesterControllerId == RequesterControllerId &&
        isSpectator == IsSpectator;
}

public sealed class TournamentSessionUpdated : IEvent
{
    public TournamentSessionSnapshot Snapshot { get; }

    public TournamentSessionUpdated(TournamentSessionSnapshot snapshot)
    {
        Snapshot = snapshot;
    }
}

public sealed class TournamentSpawnManifestUpdated : IEvent
{
    public TournamentSpawnManifestData Manifest { get; }

    public TournamentSpawnManifestUpdated(TournamentSpawnManifestData manifest)
    {
        Manifest = manifest;
    }
}
