using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Coop.Core.Client.Services.SiegeEvents.Messages;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Services.SiegeEvents.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.SiegeEvents.Interfaces;
using GameInterface.Services.SiegeEvents.Messages;
using LiteNetLib;
using Serilog;
using System;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace Coop.Core.Server.Services.SiegeEvents.Handlers;

/// <summary>Applies token-bound siege aftermath choices through the typed authority lifecycle.</summary>
internal class ServerSiegeAftermathHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ServerSiegeAftermathHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly ISiegeEventInterface siegeEventInterface;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<SiegeAftermathIntent, NetworkSiegeAftermathApplied> aftermathRoute;
    // All native action callbacks are on the game thread. This narrow marker suppresses the old
    // uncorrelated broadcast until the command can publish its exact canonical state.
    private ActiveApplication activeApplication;

    public ServerSiegeAftermathHandler(IMessageBroker messageBroker, INetwork network,
        IObjectManager objectManager, IPlayerManager playerManager, ISiegeEventInterface siegeEventInterface,
        IModConfigAuthority configAuthority, IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.siegeEventInterface = siegeEventInterface;
        this.configAuthority = configAuthority;
        aftermathRoute = authorityRequestRouter.Register(
            AuthorityRoute<SiegeAftermathIntent, NetworkRequestSiegeAftermath, NetworkSiegeAftermathApplied>.Define(
                "siege.aftermath", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestSiegeAftermath(intent.PartyId, intent.SettlementId,
                    intent.AftermathType, intent.AftermathId, header),
                request => request.Header, result => result.Header, ValidateWireShape, BuildCommandKey,
                ValidateHeader, ExecuteAftermath, CreateTerminalResult, _ => AuthorityCommitProbeResult.Pending,
                _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedResult));
        messageBroker.Subscribe<SiegeAftermathApplied>(HandleApplied);
        messageBroker.Subscribe<SiegeAftermathChoicePrompted>(HandlePrompted);
        messageBroker.Subscribe<PlayerCampaignEntered>(HandlePlayerCampaignEntered);
    }

    private void HandlePrompted(MessagePayload<SiegeAftermathChoicePrompted> payload)
    {
        var obj = payload.What;
        if (!objectManager.TryGetIdWithLogging(obj.Settlement, out var settlementId) ||
            !objectManager.TryGetIdWithLogging(obj.LeaderParty, out var leaderPartyId))
            return;

        var choice = siegeEventInterface.GetSiegeAftermathChoice(obj.LeaderParty, obj.Settlement, obj.AftermathId);
        if (choice.State != SiegeAftermathChoiceState.Pending) return;
        string sessionId = configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot) ? snapshot.SessionId : null;
        network.SendAll(new NetworkPromptSiegeAftermathChoice(settlementId, leaderPartyId,
            choice.AftermathId, choice.Generation, sessionId));
    }

    private void HandleApplied(MessagePayload<SiegeAftermathApplied> payload)
    {
        var obj = payload.What;
        if (activeApplication != null && activeApplication.Matches(obj))
        {
            activeApplication.NativeApplyObserved = true;
            return;
        }

        // Internal/AI resolution remains server-owned and is deliberately not route completion proof.
        if (!objectManager.TryGetIdWithLogging(obj.Settlement, out var settlementId)) return;
        string sessionId = configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot) ? snapshot.SessionId : null;
        try
        {
            network.SendAll(new NetworkSiegeAftermathApplied(settlementId, obj.AftermathType, sessionId));
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Internal siege aftermath state publication failed after native mutation");
            DisconnectAllConnectedPeers("partial internal siege aftermath publication", null);
            throw;
        }
    }

    private AuthorityServerReply<NetworkSiegeAftermathApplied> ExecuteAftermath(
        AuthorityServerContext context, NetworkRequestSiegeAftermath request)
    {
        if (!string.Equals(context.Player.MobilePartyId, request.PartyId, StringComparison.Ordinal))
            return Reject(context, request, "invalid-requester");
        if (!objectManager.TryGetObjectWithLogging<MobileParty>(context.Player.MobilePartyId, out var party))
            return Reject(context, request, "party-not-found");
        if (!objectManager.TryGetObjectWithLogging<Settlement>(request.SettlementId, out var settlement))
            return Reject(context, request, "settlement-not-found");

        var choice = siegeEventInterface.GetSiegeAftermathChoice(party, settlement, request.AftermathId);
        if (choice.State == SiegeAftermathChoiceState.Applied)
        {
            if (choice.AftermathType != request.AftermathType)
                return Reject(context, request, "aftermath-choice-already-applied");
            try
            {
                PublishCanonicalState(context, request, party);
                return Accepted(context, request, statePublished: true);
            }
            catch (Exception exception)
            {
                return IsolateAfterMutation(context, request, party, settlement, exception, "republish-applied-state");
            }
        }
        if (choice.State == SiegeAftermathChoiceState.InProgress)
            return Reject(context, request, "aftermath-in-progress");
        if (choice.State == SiegeAftermathChoiceState.Ambiguous)
            return Reject(context, request, "aftermath-isolated");
        if (choice.State != SiegeAftermathChoiceState.Pending || !siegeEventInterface.TryBeginSiegeAftermathChoice(
                party, settlement, request.AftermathId))
            return Reject(context, request, "invalid-aftermath-token");

        var application = new ActiveApplication(party, settlement, request.AftermathType, request.AftermathId);
        activeApplication = application;
        try
        {
            // The pending entry remains InProgress through the native call. Once this crosses into
            // ApplyAftermath, no rollback exists for settlement, economy, or relation effects.
            siegeEventInterface.ApplySiegeAftermathChoice(party, settlement, request.AftermathType, request.AftermathId);
            if (!application.NativeApplyObserved ||
                siegeEventInterface.GetSiegeAftermathChoice(party, settlement, request.AftermathId).State != SiegeAftermathChoiceState.InProgress)
                throw new InvalidOperationException("Native siege aftermath did not establish the expected postcondition.");

            // This state is queued before the terminal reply and carries exact raw correlation. The
            // router ignores it because Header is default; the client commit probe records it first.
            PublishCanonicalState(context, request, party);
            if (!siegeEventInterface.TryCompleteSiegeAftermathChoice(party, settlement, request.AftermathId,
                    request.AftermathType))
                throw new InvalidOperationException("Could not retain the applied siege aftermath tombstone.");

            return Accepted(context, request, statePublished: true);
        }
        catch (Exception exception)
        {
            return IsolateAfterMutation(context, request, party, settlement, exception, "apply-or-publish");
        }
        finally
        {
            activeApplication = null;
        }
    }

    private void PublishCanonicalState(AuthorityServerContext context, NetworkRequestSiegeAftermath request,
        MobileParty party)
    {
        if (!objectManager.TryGetId(party, out var leaderPartyId))
            throw new InvalidOperationException("Could not resolve the authoritative aftermath leader.");
        network.SendAll(new NetworkSiegeAftermathApplied(request.SettlementId, request.AftermathType,
            context.Header.SessionId, context.Header.RequestId, request.AftermathId, leaderPartyId, 1));
    }

    private AuthorityServerReply<NetworkSiegeAftermathApplied> IsolateAfterMutation(AuthorityServerContext context,
        NetworkRequestSiegeAftermath request, MobileParty party, Settlement settlement, Exception exception, string stage)
    {
        Logger.Error(exception,
            "Siege aftermath became ambiguous after native mutation. Route={Route} SessionId={SessionId} RequestId={RequestId} Settlement={Settlement} Stage={Stage}",
            context.RouteId, context.Header.SessionId, context.Header.RequestId, request.SettlementId, stage);
        siegeEventInterface.MarkSiegeAftermathChoiceAmbiguous(party, settlement, request.AftermathId);
        DisconnectAllConnectedPeers("ambiguous siege aftermath mutation", context);
        return new AuthorityServerReply<NetworkSiegeAftermathApplied>(
            CreateResult(context.Header, request, AuthorityResultStatus.ExecutionFailed, "aftermath-isolated", party),
            statePublished: false, suppressReply: true);
    }

    private static bool IsKnownAftermath(int aftermathType) => aftermathType >= 0 && aftermathType <= 2;
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

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

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
        AuthorityResultStatus status, string reason) => new(null, -1, header.SessionId, header.RequestId, null,
            null, 1, new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private static NetworkSiegeAftermathApplied CreateResult(AuthorityRequestHeader header,
        NetworkRequestSiegeAftermath request, AuthorityResultStatus status, string reason, MobileParty party = null) => new(
            request.SettlementId, request.AftermathType, header.SessionId, header.RequestId, request.AftermathId,
            request.PartyId, 1, new AuthorityResultHeader(header.SessionId, header.RequestId, status,
                header.ExpectedRevision, reason));

    private static bool IsExpectedResult(NetworkRequestSiegeAftermath request, NetworkSiegeAftermathApplied result) =>
        string.Equals(request.PartyId, result.LeaderPartyId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal) &&
        request.AftermathType == result.AftermathType && string.Equals(request.AftermathId, result.AftermathId, StringComparison.Ordinal);

    private AuthorityServerReply<NetworkSiegeAftermathApplied> Accepted(AuthorityServerContext context,
        NetworkRequestSiegeAftermath request, bool statePublished) => new(
            CreateResult(context.Header, request, AuthorityResultStatus.Accepted, null), statePublished);

    private AuthorityServerReply<NetworkSiegeAftermathApplied> Reject(AuthorityServerContext context,
        NetworkRequestSiegeAftermath request, string reason) => new(
            CreateResult(context.Header, request, AuthorityResultStatus.Rejected, reason), statePublished: false);

    private void HandlePlayerCampaignEntered(MessagePayload<PlayerCampaignEntered> payload) => GameThread.RunSafe(
        () => SendPendingAftermathPrompts(payload.What.playerId), blocking: true, context: nameof(HandlePlayerCampaignEntered));

    internal void SendPendingAftermathPrompts(NetPeer peer)
    {
        var prompts = siegeEventInterface.GetPendingSiegeAftermathPrompts() ?? Array.Empty<PendingSiegeAftermathPrompt>();
        foreach (var prompt in prompts)
        {
            if (!objectManager.TryGetIdWithLogging(prompt.Settlement, out var settlementId) ||
                !objectManager.TryGetIdWithLogging(prompt.LeaderParty, out var leaderPartyId))
                continue;
            string sessionId = configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot) ? snapshot.SessionId : null;
            network.Send(peer, new NetworkPromptSiegeAftermathChoice(settlementId, leaderPartyId,
                prompt.AftermathId, prompt.Generation, sessionId));
        }
    }

    private void DisconnectAllConnectedPeers(string reason, AuthorityServerContext? context)
    {
        foreach (var player in playerManager.Players)
        {
            if (!playerManager.IsConnected(player) || !playerManager.TryGetPeer(player.ControllerId, out var peer)) continue;
            try { peer.Disconnect(); }
            catch (Exception exception)
            {
                Logger.Fatal(exception, "Could not disconnect peer after {Reason}. Route={Route} RequestId={RequestId}",
                    reason, context?.RouteId, context?.Header.RequestId);
            }
        }
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<SiegeAftermathApplied>(HandleApplied);
        messageBroker.Unsubscribe<SiegeAftermathChoicePrompted>(HandlePrompted);
        messageBroker.Unsubscribe<PlayerCampaignEntered>(HandlePlayerCampaignEntered);
        aftermathRoute.Dispose();
    }

    private sealed class ActiveApplication
    {
        public ActiveApplication(MobileParty party, Settlement settlement, int aftermathType, string aftermathId)
        { Party = party; Settlement = settlement; AftermathType = aftermathType; AftermathId = aftermathId; }
        public MobileParty Party { get; }
        public Settlement Settlement { get; }
        public int AftermathType { get; }
        public string AftermathId { get; }
        public bool NativeApplyObserved { get; set; }
        public bool Matches(SiegeAftermathApplied applied) => ReferenceEquals(Party, applied.Party) &&
            ReferenceEquals(Settlement, applied.Settlement) && AftermathType == applied.AftermathType;
    }

    private readonly struct SiegeAftermathIntent
    {
        public SiegeAftermathIntent(string partyId, string settlementId, int aftermathType, string aftermathId)
        { PartyId = partyId; SettlementId = settlementId; AftermathType = aftermathType; AftermathId = aftermathId; }
        public string PartyId { get; }
        public string SettlementId { get; }
        public int AftermathType { get; }
        public string AftermathId { get; }
    }
}
