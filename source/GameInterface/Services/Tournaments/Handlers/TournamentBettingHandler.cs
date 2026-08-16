using Common;
using Common.Messaging;
using Common.Network.Messages;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Tournaments.Data;
using GameInterface.Services.Tournaments.Messages;
using LiteNetLib;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace GameInterface.Services.Tournaments.Handlers;

internal sealed partial class TournamentSessionHandler
{
    private BetLedgerEntry GetOrCreateBetLedger(string sessionId, string controllerId)
    {
        string ledgerKey = GetBetKey(sessionId, controllerId);
        if (betLedger.TryGetValue(ledgerKey, out BetLedgerEntry ledger)) return ledger;
        ledger = new BetLedgerEntry();
        betLedger.Add(ledgerKey, ledger);
        return ledger;
    }

    private static string ValidateBetWireShape(NetworkRequestTournamentBet request) =>
        IsBoundedTournamentId(request.SessionId) && IsBoundedTournamentId(request.MatchId) &&
        request.ExpectedRevision >= 0 && request.BracketRevision >= 0 && request.Sequence > 0 &&
        request.Amount > 0 && request.QuoteMaximumBet > 0 && request.QuoteOdd > 0 &&
        !float.IsNaN(request.QuoteOdd) && !float.IsInfinity(request.QuoteOdd)
            ? null : "invalid-tournament-bet";

    private static string BuildBetCommandKey(NetworkRequestTournamentBet request) => string.Concat(
        FieldKey(request.SessionId), request.ExpectedRevision.ToString(), request.BracketRevision.ToString(),
        FieldKey(request.MatchId), request.Amount.ToString(), request.Sequence.ToString(),
        request.QuoteMaximumBet.ToString(), request.QuoteOdd.ToString("R"));

    private AuthorityServerReply<NetworkTournamentBetResult> ExecuteBet(
        AuthorityServerContext context, NetworkRequestTournamentBet request)
    {
        if (!sessionRegistry.TryGet(request.SessionId, out TournamentSessionSnapshot current))
            return BetReply(context.Header, AuthorityResultStatus.StaleState, null, context.Player, request, null,
                "tournament-not-found");

        // Choice revisions can drift; a match, bracket, or phase transition cannot.
        if (current.Phase != TournamentSessionPhase.AwaitingChoices || current.CurrentMatchId != request.MatchId ||
            current.BracketRevision != request.BracketRevision)
        {
            SendCanonical(context.Peer, current);
            return BetReply(context.Header, AuthorityResultStatus.StaleState, current, context.Player, request, null,
                "stale-tournament-bet", true);
        }

        BetLedgerEntry ledger = GetOrCreateBetLedger(current.SessionId, context.Player.ControllerId);
        if (request.Sequence < ledger.LastDomainSequence)
            return BetReply(context.Header, AuthorityResultStatus.StaleState, current, context.Player, request, ledger,
                "stale-bet-sequence");
        if (request.Sequence == ledger.LastDomainSequence && ledger.LastDomainSequence != 0)
            return ReplayCommittedBet(context, request, current, ledger);

        if (!TryResolvePlayer(context.Player, out var hero, out _) ||
            !TryGetPlayerSlot(current, context.Player.ControllerId, out var slot) ||
            !tournamentGameInterface.TryGetBetQuote(current, hero, slot.SlotId, out var quote) ||
            quote.MaximumBet != request.QuoteMaximumBet || quote.Odd != request.QuoteOdd)
        {
            SendCanonical(context.Peer, current);
            return BetReply(context.Header, AuthorityResultStatus.Rejected, current, context.Player, request, ledger,
                "invalid-tournament-quote", true);
        }

        ledger.MatchAmounts.TryGetValue(request.MatchId, out int roundBet);
        if (!TournamentBettingMath.IsValidStake(request.Amount, roundBet, quote.MaximumBet, hero.Gold))
            return BetReply(context.Header, AuthorityResultStatus.Rejected, current, context.Player, request, ledger,
                "invalid-tournament-bet");

        // This marker makes a throwing debit fail closed: no authority/domain sequence can debit twice.
        ledger.LastDomainSequence = request.Sequence;
        bool irreversible = false;
        try
        {
            irreversible = true;
            GiveGoldAction.ApplyBetweenCharacters(hero, null, request.Amount, true);
            roundBet += request.Amount;
            ledger.MatchAmounts[request.MatchId] = roundBet;
            ledger.ExpectedPayout += TournamentBettingMath.CalculateExpectedPayout(request.Amount, quote.Odd);
            ledger.TotalBettedDenars += request.Amount;
            ledger.LastMatchId = request.MatchId;
            ledger.LastHeroId = context.Player.HeroId;
            SendCanonical(context.Peer, current);
            SendBetState(context.Peer, context.Header, current, context.Player, hero.Gold, ledger, roundBet);
            return BetReply(context.Header, AuthorityResultStatus.Accepted, current, context.Player, request, ledger,
                null, true);
        }
        catch (Exception exception)
        {
            if (!irreversible) throw;
            Logger.Fatal(exception, "[Tournament] Ambiguous bet debit; disconnecting requester. Session={SessionId}, Request={RequestId}",
                current.SessionId, context.Header.RequestId);
            try { context.Peer.Disconnect(); } catch { }
            return new AuthorityServerReply<NetworkTournamentBetResult>(
                CreateBetTerminal(context.Header, AuthorityResultStatus.ExecutionFailed, "tournament-bet-isolated"),
                false, suppressReply: true);
        }
    }

    private AuthorityServerReply<NetworkTournamentBetResult> ReplayCommittedBet(AuthorityServerContext context,
        NetworkRequestTournamentBet request, TournamentSessionSnapshot snapshot, BetLedgerEntry ledger)
    {
        ledger.MatchAmounts.TryGetValue(ledger.LastMatchId, out int roundBet);
        try
        {
            SendCanonical(context.Peer, snapshot);
            SendBetState(context.Peer, context.Header, snapshot, context.Player, ledger.LastHeroGold, ledger, roundBet);
        }
        catch (Exception exception)
        {
            Logger.Fatal(exception, "[Tournament] Could not restore committed bet state; disconnecting requester. Request={RequestId}",
                context.Header.RequestId);
            try { context.Peer.Disconnect(); } catch { }
            return new AuthorityServerReply<NetworkTournamentBetResult>(
                CreateBetTerminal(context.Header, AuthorityResultStatus.ExecutionFailed, "tournament-bet-isolated"),
                false, suppressReply: true);
        }
        return BetReply(context.Header, AuthorityResultStatus.Accepted, snapshot, context.Player, request, ledger,
            null, true);
    }

    private void SendBetState(NetPeer peer, AuthorityRequestHeader header, TournamentSessionSnapshot snapshot,
        Player player, int heroGold, BetLedgerEntry ledger, int roundBet)
    {
        ledger.LastHeroGold = heroGold;
        network.Send(peer, new NetworkTournamentBetState(header, snapshot, ledger.LastDomainSequence,
            player.ControllerId, player.HeroId, heroGold, ledger.TotalBettedDenars, roundBet, ledger.ExpectedPayout));
    }

    private static AuthorityServerReply<NetworkTournamentBetResult> BetReply(AuthorityRequestHeader header,
        AuthorityResultStatus status, TournamentSessionSnapshot snapshot, Player player, NetworkRequestTournamentBet request,
        BetLedgerEntry ledger, string reasonCode, bool statePublished = false) => new(
        new NetworkTournamentBetResult(header, status, snapshot, player?.ControllerId, player?.HeroId, request.Sequence,
            ledger?.TotalBettedDenars ?? 0, RoundBet(ledger, request.MatchId), ledger?.ExpectedPayout ?? 0,
            ledger?.LastHeroGold ?? 0, reasonCode),
        statePublished);

    private static int RoundBet(BetLedgerEntry ledger, string matchId) => ledger != null &&
        ledger.MatchAmounts.TryGetValue(matchId, out int amount) ? amount : 0;

    private static NetworkTournamentBetResult CreateBetTerminal(AuthorityRequestHeader header,
        AuthorityResultStatus status, string reasonCode) => new(header, status, null, null, null, 0, 0, 0, 0, 0, reasonCode);

    private AuthorityCommitProbeResult ProbeBetApplied(NetworkTournamentBetResult result)
    {
        if (result.Status != AuthorityResultStatus.Accepted || result.ControllerId != controllerIdProvider.ControllerId ||
            !sessionRegistry.TryGet(result.TournamentSessionId, out TournamentSessionSnapshot snapshot) ||
            snapshot.Revision != result.Revision || snapshot.CurrentMatchId != result.MatchId ||
            snapshot.BracketRevision != result.BracketRevision ||
            !receivedBetStates.TryGetValue(BetReplicaKey(result.ConfigSessionId, result.AuthorityRequestId), out var state))
            return AuthorityCommitProbeResult.Pending;
        if (!objectManager.TryGetObject<Hero>(state.HeroId, out Hero hero) || hero.Gold != state.HeroGold)
            return AuthorityCommitProbeResult.Pending;
        return state.TournamentSessionId == result.TournamentSessionId && state.CommittedRevision == result.Revision &&
               state.MatchId == result.MatchId && state.BracketRevision == result.BracketRevision &&
               state.Sequence == result.Sequence && state.ControllerId == result.ControllerId && state.HeroId == result.HeroId &&
               state.HeroGold == result.HeroGold &&
               state.TotalBettedDenars == result.BettedDenars && state.ThisRoundBettedDenars == result.ThisRoundBettedDenars &&
               state.ExpectedPayout == result.ExpectedPayout
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Invalid;
    }

    private static bool IsExpectedBetResult(NetworkRequestTournamentBet request, NetworkTournamentBetResult result) =>
        result.Status != AuthorityResultStatus.Accepted ||
        (result.ConfigSessionId == request.ConfigSessionId && result.AuthorityRequestId == request.AuthorityRequestId &&
         result.TournamentSessionId == request.SessionId && result.MatchId == request.MatchId &&
         result.BracketRevision == request.BracketRevision && result.Sequence == request.Sequence);

    private static void PresentBetTerminal(AuthorityClientOutcome<NetworkTournamentBetResult> outcome)
    {
        if (outcome.Completion == AuthorityClientCompletion.Applied) return;
        Logger.Warning("Tournament bet did not commit a trusted ledger state. Completion={Completion}, Reason={Reason}",
            outcome.Completion, outcome.ReasonCode);
        TournamentStateSyncHandler.RequestCanonicalResync();
    }

    private static string BetReplicaKey(string configSessionId, long requestId) =>
        string.Concat(configSessionId ?? string.Empty, ":", requestId);

    private void Handle_BetState(MessagePayload<NetworkTournamentBetState> payload)
    {
        if (ModInformation.IsServer || !configAuthority.IsTrustedServer(payload.Who)) return;
        NetworkTournamentBetState state = payload.What;
        if (state.AuthorityRequestId <= 0 || state.Sequence <= 0 || state.HeroGold < 0 ||
            string.IsNullOrEmpty(state.ConfigSessionId) || string.IsNullOrEmpty(state.TournamentSessionId) ||
            string.IsNullOrEmpty(state.MatchId) || string.IsNullOrEmpty(state.ControllerId) || string.IsNullOrEmpty(state.HeroId)) return;
        receivedBetStates[BetReplicaKey(state.ConfigSessionId, state.AuthorityRequestId)] = state;
    }
}
