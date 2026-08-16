using Common;
using Common.Messaging;
using Common.Network.Messages;
using Coop.Core.Client.Services.SiegeEvents.Messages;
using Coop.Core.Server.Services.SiegeEvents.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.SiegeEvents.Interfaces;
using GameInterface.Services.SiegeEvents.Messages;
using LiteNetLib;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace Coop.Core.Client.Services.SiegeEvents.Handlers;

/// <summary>Routes local aftermath picks and waits for the token-bound canonical server state.</summary>
internal class ClientSiegeAftermathHandler : IHandler
{
    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly ISiegeEventInterface siegeEventInterface;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<SiegeAftermathIntent, NetworkSiegeAftermathApplied> aftermathRoute;
    private readonly Dictionary<string, AftermathPrompt> prompts = new Dictionary<string, AftermathPrompt>(StringComparer.Ordinal);
    private readonly Dictionary<string, CanonicalAftermath> canonicalStates = new Dictionary<string, CanonicalAftermath>(StringComparer.Ordinal);
    private string pendingAttemptSettlementId;

    public ClientSiegeAftermathHandler(IMessageBroker messageBroker, IObjectManager objectManager,
        ISiegeEventInterface siegeEventInterface, IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.siegeEventInterface = siegeEventInterface;
        this.configAuthority = configAuthority;
        aftermathRoute = authorityRequestRouter.Register(
            AuthorityRoute<SiegeAftermathIntent, NetworkRequestSiegeAftermath, NetworkSiegeAftermathApplied>.Define(
                "siege.aftermath", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestSiegeAftermath(intent.PartyId, intent.SettlementId,
                    intent.AftermathType, intent.AftermathId, header),
                request => request.Header, result => result.Header, ValidateWireShape, BuildCommandKey,
                ValidateHeader, (_, __) => throw new InvalidOperationException("Siege aftermath route executes only on the server."),
                CreateTerminalResult, ProbeClientCommit, _ => { }, PresentTerminalOutcome,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedResult));

        messageBroker.Subscribe<SiegeAftermathAttempted>(HandleAttempt);
        messageBroker.Subscribe<NetworkSiegeAftermathApplied>(HandleApplied);
        messageBroker.Subscribe<NetworkPromptSiegeAftermathChoice>(HandleChoicePrompt);
        messageBroker.Subscribe<ClientSessionEnded>(HandleClientSessionEnded);
    }

    private void HandleChoicePrompt(MessagePayload<NetworkPromptSiegeAftermathChoice> payload)
    {
        if (!(payload.Who is NetPeer) || !configAuthority.IsTrustedServer(payload.Who) ||
            !configAuthority.TryGetCurrent(out ModConfigSnapshot current) ||
            !string.Equals(current.SessionId, payload.What.SessionId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(payload.What.AftermathId) || payload.What.Generation != 1 ||
            string.IsNullOrWhiteSpace(payload.What.SettlementId) || string.IsNullOrWhiteSpace(payload.What.LeaderPartyId))
            return;

        var obj = payload.What;
        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<MobileParty>(obj.LeaderPartyId, out var leaderParty) ||
                !objectManager.TryGetObjectWithLogging<Settlement>(obj.SettlementId, out var settlement))
                return;

            prompts[obj.SettlementId] = new AftermathPrompt(obj.LeaderPartyId, obj.AftermathId, obj.Generation);
            siegeEventInterface.PromptLocalAftermathChoice(leaderParty, settlement);
        }, context: nameof(HandleChoicePrompt));
    }

    private void HandleApplied(MessagePayload<NetworkSiegeAftermathApplied> payload)
    {
        if (!(payload.Who is NetPeer) || !configAuthority.IsTrustedServer(payload.Who)) return;
        var obj = payload.What;

        if (obj.AuthorityRequestId > 0 && configAuthority.TryGetCurrent(out ModConfigSnapshot current) &&
            string.Equals(current.SessionId, obj.SessionId, StringComparison.Ordinal) && obj.Generation == 1 &&
            !string.IsNullOrWhiteSpace(obj.AftermathId) && !string.IsNullOrWhiteSpace(obj.SettlementId) &&
            !string.IsNullOrWhiteSpace(obj.LeaderPartyId) && IsKnownAftermath(obj.AftermathType))
        {
            canonicalStates[CorrelationKey(obj.SessionId, obj.AuthorityRequestId, obj.AftermathId)] =
                new CanonicalAftermath(obj.SettlementId, obj.LeaderPartyId, obj.AftermathType, obj.Generation);
        }

        GameThread.RunSafe(() =>
        {
            if (objectManager.TryGetObjectWithLogging<Settlement>(obj.SettlementId, out var settlement))
                siegeEventInterface.SetLocalAftermathNarration(settlement, obj.AftermathType);
        }, context: nameof(HandleApplied));
    }

    private void HandleAttempt(MessagePayload<SiegeAftermathAttempted> payload)
    {
        var obj = payload.What;
        if (!objectManager.TryGetIdWithLogging(obj.Party, out var partyId) ||
            !objectManager.TryGetIdWithLogging(obj.Settlement, out var settlementId) ||
            !prompts.TryGetValue(settlementId, out var prompt) ||
            !string.Equals(prompt.LeaderPartyId, partyId, StringComparison.Ordinal))
            return;

        pendingAttemptSettlementId = settlementId;
        aftermathRoute.Submit(new SiegeAftermathIntent(partyId, settlementId, obj.AftermathType, prompt.AftermathId));
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private static string ValidateWireShape(NetworkRequestSiegeAftermath request)
    {
        if (!IsKnownAftermath(request.AftermathType)) return "invalid-aftermath-choice";
        if (string.IsNullOrWhiteSpace(request.PartyId) || request.PartyId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.SettlementId) || request.SettlementId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.AftermathId) || request.AftermathId.Length > 64)
            return "invalid-aftermath-identifiers";
        return null;
    }

    private static string BuildCommandKey(NetworkRequestSiegeAftermath request) => string.Concat(
        request.PartyId.Length, ":", request.PartyId, ":", request.SettlementId.Length, ":", request.SettlementId,
        ":", request.AftermathType, ":", request.AftermathId.Length, ":", request.AftermathId);

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot current))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        if (header.ProtocolVersion != current.ProtocolVersion)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.InvalidRequest, "unsupported-protocol");
        if (!string.Equals(header.SessionId, current.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session");
        if (header.ExpectedRevision != current.Revision)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
        return AuthorityHeaderValidation.Valid;
    }

    private static NetworkSiegeAftermathApplied CreateTerminalResult(AuthorityRequestHeader header,
        AuthorityResultStatus status, string reason) => new(null, -1, header.SessionId, header.RequestId,
            null, null, 1, new AuthorityResultHeader(header.SessionId, header.RequestId, status,
                header.ExpectedRevision, reason));

    private AuthorityCommitProbeResult ProbeClientCommit(NetworkSiegeAftermathApplied result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted || !configAuthority.TryGetCurrent(out ModConfigSnapshot current) ||
            !string.Equals(current.SessionId, result.Header.SessionId, StringComparison.Ordinal) ||
            current.Revision != result.Header.CommittedRevision || result.Generation != 1 ||
            string.IsNullOrWhiteSpace(result.AftermathId) || !IsKnownAftermath(result.AftermathType))
            return AuthorityCommitProbeResult.Invalid;

        return canonicalStates.TryGetValue(CorrelationKey(result.Header.SessionId, result.Header.RequestId, result.AftermathId),
            out var state) && state.Generation == result.Generation && state.AftermathType == result.AftermathType &&
            string.Equals(state.SettlementId, result.SettlementId, StringComparison.Ordinal) &&
            string.Equals(state.LeaderPartyId, result.LeaderPartyId, StringComparison.Ordinal)
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private static bool IsExpectedResult(NetworkRequestSiegeAftermath request, NetworkSiegeAftermathApplied result) =>
        string.Equals(request.PartyId, result.LeaderPartyId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal) &&
        request.AftermathType == result.AftermathType && string.Equals(request.AftermathId, result.AftermathId, StringComparison.Ordinal);

    private void PresentTerminalOutcome(AuthorityClientOutcome<NetworkSiegeAftermathApplied> outcome)
    {
        if (outcome.Applied)
        {
            prompts.Remove(outcome.Result.SettlementId);
            pendingAttemptSettlementId = null;
            canonicalStates.Remove(CorrelationKey(outcome.Result.Header.SessionId, outcome.Result.Header.RequestId,
                outcome.Result.AftermathId));
            return;
        }

        // The action prefix released the hold before the request; a normal rejection must leave the
        // saved prompt usable rather than stranding the player outside the choice menu.
        string settlementId = outcome.Result?.SettlementId ?? pendingAttemptSettlementId;
        pendingAttemptSettlementId = null;
        if (!string.IsNullOrWhiteSpace(settlementId) && prompts.TryGetValue(settlementId, out var prompt) &&
            objectManager.TryGetObject<MobileParty>(prompt.LeaderPartyId, out var leader) &&
            objectManager.TryGetObject<Settlement>(settlementId, out var settlement))
            siegeEventInterface.PromptLocalAftermathChoice(leader, settlement);
    }

    private void HandleClientSessionEnded(MessagePayload<ClientSessionEnded> _)
    {
        prompts.Clear();
        canonicalStates.Clear();
        pendingAttemptSettlementId = null;
    }

    private static bool IsKnownAftermath(int aftermathType) => aftermathType >= 0 && aftermathType <= 2;
    private static string CorrelationKey(string sessionId, long requestId, string aftermathId) =>
        string.Concat(sessionId?.Length ?? -1, ":", sessionId ?? string.Empty, ":", requestId, ":", aftermathId);

    public void Dispose()
    {
        messageBroker.Unsubscribe<SiegeAftermathAttempted>(HandleAttempt);
        messageBroker.Unsubscribe<NetworkSiegeAftermathApplied>(HandleApplied);
        messageBroker.Unsubscribe<NetworkPromptSiegeAftermathChoice>(HandleChoicePrompt);
        messageBroker.Unsubscribe<ClientSessionEnded>(HandleClientSessionEnded);
        aftermathRoute.Dispose();
    }

    private readonly struct SiegeAftermathIntent
    {
        public SiegeAftermathIntent(string partyId, string settlementId, int aftermathType, string aftermathId)
        {
            PartyId = partyId; SettlementId = settlementId; AftermathType = aftermathType; AftermathId = aftermathId;
        }
        public string PartyId { get; }
        public string SettlementId { get; }
        public int AftermathType { get; }
        public string AftermathId { get; }
    }

    private readonly struct AftermathPrompt
    {
        public AftermathPrompt(string leaderPartyId, string aftermathId, long generation)
        { LeaderPartyId = leaderPartyId; AftermathId = aftermathId; Generation = generation; }
        public string LeaderPartyId { get; }
        public string AftermathId { get; }
        public long Generation { get; }
    }

    private readonly struct CanonicalAftermath
    {
        public CanonicalAftermath(string settlementId, string leaderPartyId, int aftermathType, long generation)
        { SettlementId = settlementId; LeaderPartyId = leaderPartyId; AftermathType = aftermathType; Generation = generation; }
        public string SettlementId { get; }
        public string LeaderPartyId { get; }
        public int AftermathType { get; }
        public long Generation { get; }
    }
}
