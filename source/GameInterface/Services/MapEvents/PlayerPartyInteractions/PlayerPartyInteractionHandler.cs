using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Messages;
using Common.Util;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Inventory.Data;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.MapEvents.Messages.Conversation;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using LiteNetLib;
using SandBox.View.Map;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TroopRosterElementData = GameInterface.Services.TroopRosters.Data.TroopRosterElementData;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.BarterSystem;
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Inventory;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.ViewModelCollection.Barter;
using TaleWorlds.Core;
using TaleWorlds.ScreenSystem;
using Helpers;
using TaleWorlds.Library;

namespace GameInterface.Services.MapEvents.PlayerPartyInteractions;

internal enum PlayerPartyInteractionStartOutcome { Started, Existing, Busy }

internal readonly struct PlayerPartyInteractionStartResult
{
    public static readonly PlayerPartyInteractionStartResult Busy = new(PlayerPartyInteractionStartOutcome.Busy, null);
    public PlayerPartyInteractionStartResult(PlayerPartyInteractionStartOutcome outcome, string sessionId)
    { Outcome = outcome; SessionId = sessionId; }
    public PlayerPartyInteractionStartOutcome Outcome { get; }
    public string SessionId { get; }
}

internal readonly struct PlayerPartyInteractionShownIntent
{
    public PlayerPartyInteractionShownIntent(string sessionId, long revision) { SessionId = sessionId; Revision = revision; }
    public string SessionId { get; }
    public long Revision { get; }
}

internal readonly struct PlayerPartyInteractionOptionIntent
{
    public PlayerPartyInteractionOptionIntent(string sessionId, long revision, PlayerPartyInteractionOption option) { SessionId = sessionId; Revision = revision; Option = option; }
    public string SessionId { get; }
    public long Revision { get; }
    public PlayerPartyInteractionOption Option { get; }
}

internal readonly struct PlayerPartyTradeOfferIntent
{
    public PlayerPartyTradeOfferIntent(string sessionId, long revision, ItemRosterElementData[] items, TroopRosterElementData[] troops, int gold, string[] fiefs, TroopRosterElementData[] prisoners, bool peace)
    { SessionId = sessionId; Revision = revision; Items = items; Troops = troops; Gold = gold; Fiefs = fiefs; Prisoners = prisoners; Peace = peace; }
    public string SessionId { get; } public long Revision { get; } public ItemRosterElementData[] Items { get; } public TroopRosterElementData[] Troops { get; } public int Gold { get; } public string[] Fiefs { get; } public TroopRosterElementData[] Prisoners { get; } public bool Peace { get; }
}

internal readonly struct PlayerPartyTradeAcceptIntent
{
    public PlayerPartyTradeAcceptIntent(string sessionId, long revision, bool accepted) { SessionId = sessionId; Revision = revision; Accepted = accepted; }
    public string SessionId { get; } public long Revision { get; } public bool Accepted { get; }
}

internal class PlayerPartyInteractionHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<PlayerPartyInteractionHandler>();
    private static readonly FieldInfo PrisonerCharacterField =
        typeof(TransferPrisonerBarterable).GetField("_prisonerCharacter", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly ConversationPartyTracker conversationPartyTracker;
    private readonly INetworkConfig configuration;
    private readonly IPlayerPartyHostileEncounterService hostileEncounterService;
    private readonly PlayerPartyInteractionOutcomeHandler outcomeHandler;
    private readonly IPlayerClanMembershipService clanMembershipService;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<PlayerPartyInteractionShownIntent, PlayerPartyInteractionShownResult> shownRoute;
    private readonly IAuthorityRouteHandle<PlayerPartyInteractionOptionIntent, PlayerPartyInteractionOptionResult> optionRoute;
    private readonly IAuthorityRouteHandle<PlayerPartyTradeOfferIntent, PlayerPartyTradeOfferResult> tradeOfferRoute;
    private readonly IAuthorityRouteHandle<PlayerPartyTradeAcceptIntent, PlayerPartyTradeAcceptResult> tradeAcceptRoute;

    private readonly ConcurrentDictionary<string, PlayerPartyInteractionSession> sessionsById = new ConcurrentDictionary<string, PlayerPartyInteractionSession>();
    private readonly ConcurrentDictionary<string, string> sessionsByPartyId = new ConcurrentDictionary<string, string>();
    private readonly ConcurrentDictionary<string, long> endedSessionRevisions = new ConcurrentDictionary<string, long>();
    private readonly object sessionGate = new object();
    private readonly HashSet<string> openedConversationSessionIds = new HashSet<string>();
    private readonly HashSet<string> endedInteractionSessionIds = new HashSet<string>();
    private readonly HashSet<string> hostileEncounterSessionIds = new HashSet<string>();
    private readonly HashSet<string> closedHostileEncounterPartyIds = new HashSet<string>();
    private bool presentationTickRegistered;

    public PlayerPartyInteractionHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        ConversationPartyTracker conversationPartyTracker,
        INetworkConfig configuration,
        IPlayerPartyHostileEncounterService hostileEncounterService,
        IKingdomMembershipState kingdomMembershipState,
        IPlayerClanMembershipService clanMembershipService,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.conversationPartyTracker = conversationPartyTracker;
        this.configuration = configuration;
        this.hostileEncounterService = hostileEncounterService;
        this.clanMembershipService = clanMembershipService;
        this.configAuthority = configAuthority;
        outcomeHandler = new PlayerPartyInteractionOutcomeHandler(objectManager, kingdomMembershipState, clanMembershipService);

        shownRoute = authorityRequestRouter.Register(AuthorityRoute<PlayerPartyInteractionShownIntent, RequestPlayerPartyInteractionShown, PlayerPartyInteractionShownResult>.Define(
            "player-interaction.shown", AuthorityRouteKind.Command, CreateAuthorityHeader,
            (intent, header) => new RequestPlayerPartyInteractionShown(header, intent.SessionId, intent.Revision), request => request.Header, result => result.Header,
            ValidateShownWire, request => request.InteractionSessionId, ValidateAuthorityHeader, ExecuteShown, CreateShownTerminal, ProbeShown,
            result => ResyncInteraction(result.InteractionSessionId), PresentRouteOutcome, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
            failClosedOnApplyFailure: false, isExpectedClientResult: (request, result) => string.Equals(request.InteractionSessionId, result.InteractionSessionId, StringComparison.Ordinal)));
        optionRoute = authorityRequestRouter.Register(AuthorityRoute<PlayerPartyInteractionOptionIntent, RequestPlayerPartyInteractionOption, PlayerPartyInteractionOptionResult>.Define(
            "player-interaction.option", AuthorityRouteKind.Command, CreateAuthorityHeader,
            (intent, header) => new RequestPlayerPartyInteractionOption(header, intent.SessionId, intent.Revision, (int)intent.Option), request => request.Header, result => result.Header,
            ValidateOptionWire, request => request.InteractionSessionId + ":" + request.ExpectedInteractionRevision + ":" + request.Option, ValidateAuthorityHeader, ExecuteOption, CreateOptionTerminal, ProbeOption,
            result => ResyncInteraction(result.InteractionSessionId), PresentRouteOutcome, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
            failClosedOnApplyFailure: false, isExpectedClientResult: (request, result) => string.Equals(request.InteractionSessionId, result.InteractionSessionId, StringComparison.Ordinal)));
        tradeOfferRoute = authorityRequestRouter.Register(AuthorityRoute<PlayerPartyTradeOfferIntent, RequestPlayerPartyTradeOffer, PlayerPartyTradeOfferResult>.Define(
            "player-interaction.trade-offer", AuthorityRouteKind.Command, CreateAuthorityHeader,
            (intent, header) => new RequestPlayerPartyTradeOffer(header, intent.SessionId, intent.Revision, intent.Items, intent.Troops, intent.Gold, intent.Fiefs, intent.Prisoners, intent.Peace), request => request.Header, result => result.Header,
            ValidateTradeOfferWire, BuildTradeOfferKey, ValidateAuthorityHeader, ExecuteTradeOffer, CreateTradeOfferTerminal, ProbeTradeOffer,
            result => ResyncInteraction(result.InteractionSessionId), PresentRouteOutcome, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
            failClosedOnApplyFailure: false, isExpectedClientResult: (request, result) => string.Equals(request.InteractionSessionId, result.InteractionSessionId, StringComparison.Ordinal)));
        tradeAcceptRoute = authorityRequestRouter.Register(AuthorityRoute<PlayerPartyTradeAcceptIntent, RequestPlayerPartyTradeAccept, PlayerPartyTradeAcceptResult>.Define(
            "player-interaction.trade-accept", AuthorityRouteKind.Command, CreateAuthorityHeader,
            (intent, header) => new RequestPlayerPartyTradeAccept(header, intent.SessionId, intent.Revision, intent.Accepted), request => request.Header, result => result.Header,
            ValidateTradeAcceptWire, request => request.InteractionSessionId + ":" + request.ExpectedInteractionRevision + ":" + request.Accepted, ValidateAuthorityHeader, ExecuteTradeAccept, CreateTradeAcceptTerminal, ProbeTradeAccept,
            result => ResyncInteraction(result.InteractionSessionId), PresentRouteOutcome, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
            failClosedOnApplyFailure: false, isExpectedClientResult: (request, result) => string.Equals(request.InteractionSessionId, result.InteractionSessionId, StringComparison.Ordinal)));

        messageBroker.Subscribe<NetworkPlayerPartyInteractionStarted>(Handle_NetworkPlayerPartyInteractionStarted);
        messageBroker.Subscribe<NetworkPlayerPartyInteractionState>(Handle_NetworkPlayerPartyInteractionState);
        messageBroker.Subscribe<NetworkPlayerPartyInteractionEnded>(Handle_NetworkPlayerPartyInteractionEnded);
        messageBroker.Subscribe<NetworkPlayerPartyInteractionDenied>(Handle_NetworkPlayerPartyInteractionDenied);
        messageBroker.Subscribe<NetworkPlayerPartyHostileEncounterStarted>(Handle_NetworkPlayerPartyHostileEncounterStarted);
        messageBroker.Subscribe<NetworkClosePvpEncounter>(Handle_NetworkClosePvpEncounter);
        messageBroker.Subscribe<PlayerPartyInteractionOptionSelected>(Handle_PlayerPartyInteractionOptionSelected);
        messageBroker.Subscribe<PlayerPartyTradeOfferChanged>(Handle_PlayerPartyTradeOfferChanged);
        messageBroker.Subscribe<PlayerPartyTradeAcceptSelected>(Handle_PlayerPartyTradeAcceptSelected);
        messageBroker.Subscribe<NetworkPlayerPartyTradeOfferUpdated>(Handle_NetworkPlayerPartyTradeOfferUpdated);
        messageBroker.Subscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
    }

    public void Dispose()
    {
        if (presentationTickRegistered && Campaign.Current != null)
            CampaignEvents.TickEvent.ClearListeners(this);

        messageBroker.Unsubscribe<NetworkPlayerPartyInteractionStarted>(Handle_NetworkPlayerPartyInteractionStarted);
        messageBroker.Unsubscribe<NetworkPlayerPartyInteractionState>(Handle_NetworkPlayerPartyInteractionState);
        messageBroker.Unsubscribe<NetworkPlayerPartyInteractionEnded>(Handle_NetworkPlayerPartyInteractionEnded);
        messageBroker.Unsubscribe<NetworkPlayerPartyInteractionDenied>(Handle_NetworkPlayerPartyInteractionDenied);
        messageBroker.Unsubscribe<NetworkPlayerPartyHostileEncounterStarted>(Handle_NetworkPlayerPartyHostileEncounterStarted);
        messageBroker.Unsubscribe<NetworkClosePvpEncounter>(Handle_NetworkClosePvpEncounter);
        messageBroker.Unsubscribe<PlayerPartyInteractionOptionSelected>(Handle_PlayerPartyInteractionOptionSelected);
        messageBroker.Unsubscribe<PlayerPartyTradeOfferChanged>(Handle_PlayerPartyTradeOfferChanged);
        messageBroker.Unsubscribe<PlayerPartyTradeAcceptSelected>(Handle_PlayerPartyTradeAcceptSelected);
        messageBroker.Unsubscribe<NetworkPlayerPartyTradeOfferUpdated>(Handle_NetworkPlayerPartyTradeOfferUpdated);
        messageBroker.Unsubscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
        shownRoute.Dispose();
        optionRoute.Dispose();
        tradeOfferRoute.Dispose();
        tradeAcceptRoute.Dispose();

        // The dialog state is a process-wide static, not container state: a session that ends mid-dialog
        // would otherwise leave HasActiveState set after this handler's container is torn down, and
        // ConversationRequestHandler silently drops every conversation request while it is set.
        PlayerPartyInteractionDialogState.Clear();
    }

    internal PlayerPartyInteractionStartResult TryStartSessionDetailed(
        NetPeer initiatorPeer,
        NetworkRequestConversation request,
        PartyBase initiatorParty,
        PartyBase responderParty)
    {
        if (ModInformation.IsClient) return PlayerPartyInteractionStartResult.Busy;

        PlayerPartyInteractionSession session;
        lock (sessionGate)
        {
            var existing = FindExistingSession(request.AttackerId) ?? FindExistingSession(request.DefenderId);
            if (existing != null)
            {
                if (!IsSamePair(existing, request.AttackerId, request.DefenderId))
                    return PlayerPartyInteractionStartResult.Busy;
                return new PlayerPartyInteractionStartResult(PlayerPartyInteractionStartOutcome.Existing, existing.SessionId);
            }

            session = new PlayerPartyInteractionSession(
                Guid.NewGuid().ToString("N"),
                request.AttackerId,
                request.DefenderId,
                GetPartyName(initiatorParty, "Player"),
                GetPartyName(responderParty, "Player"),
                initiatorPeer,
                AreHostile(initiatorParty, responderParty));
            AddInitialOptions(session, initiatorParty, responderParty);
            if (!sessionsById.TryAdd(session.SessionId, session)) return PlayerPartyInteractionStartResult.Busy;
            sessionsByPartyId[session.InitiatorPartyId] = session.SessionId;
            sessionsByPartyId[session.ResponderPartyId] = session.SessionId;
            conversationPartyTracker.BeginPvpConversation(session.InitiatorPartyId, session.ResponderPartyId);
        }

        GameThread.RunSafe(() =>
        {
            HoldParty(initiatorParty.MobileParty);
            HoldParty(responderParty.MobileParty);
        }, context: "Hold player-party interaction parties");

        network.SendAll(new NetworkPlayerPartyInteractionStarted(
            session.SessionId,
            session.InitiatorPartyId,
            session.ResponderPartyId,
            session.InitiatorName,
            session.ResponderName,
            session.Revision));

        SendInitialStates(session);

        return new PlayerPartyInteractionStartResult(PlayerPartyInteractionStartOutcome.Started, session.SessionId);
    }

    public bool TryStartSession(NetPeer initiatorPeer, NetworkRequestConversation request, PartyBase initiatorParty,
        PartyBase responderParty)
    {
        var result = TryStartSessionDetailed(initiatorPeer, request, initiatorParty, responderParty);
        if (result.Outcome == PlayerPartyInteractionStartOutcome.Busy)
            network.Send(initiatorPeer, new NetworkPlayerPartyInteractionDenied(PlayerPartyInteractionDeniedReason.Busy));
        return result.Outcome == PlayerPartyInteractionStartOutcome.Started;
    }

    private void Handle_NetworkPlayerPartyInteractionStarted(MessagePayload<NetworkPlayerPartyInteractionStarted> payload)
    {
        if (ModInformation.IsServer) return;

        var message = payload.What;

        GameThread.RunSafe(() =>
        {
            if (!TryGetControlledSessionParty(message, out var myPartyId, out _)) return;

            closedHostileEncounterPartyIds.Remove(message.InitiatorPartyId);
            closedHostileEncounterPartyIds.Remove(message.ResponderPartyId);

            shownRoute.Submit(new PlayerPartyInteractionShownIntent(message.SessionId, message.Revision));
        }, context: "Confirm player-party interaction party");
    }

    private void Handle_NetworkPlayerPartyInteractionState(MessagePayload<NetworkPlayerPartyInteractionState> payload)
    {
        if (ModInformation.IsServer) return;

        var message = payload.What;

        GameThread.RunSafe(() =>
        {
            if (endedInteractionSessionIds.Contains(message.SessionId)) return;
            if (!objectManager.TryGetObject<PartyBase>(message.PartyId, out var party)) return;
            if (party.MobileParty?.IsControlledByThisInstance() != true) return;

            PlayerPartyInteractionDialogState.Apply(message);
            EnsurePresentationTickListener();
            TryOpenPendingMapConversation();

            if (message.Phase == PlayerPartyInteractionPhase.TradeActive)
            {
                var localAccepted = message.IsInitiator ? message.InitiatorAcceptedTrade : message.ResponderAcceptedTrade;
                var remoteAccepted = message.IsInitiator ? message.ResponderAcceptedTrade : message.InitiatorAcceptedTrade;
                OpenTrade(message);
                PlayerPartyTradeContext.UpdateAcceptance(localAccepted, remoteAccepted);
                PlayerPartyTradeOverlay.Instance.UpdateState(localAccepted, remoteAccepted);
            }
        }, context: "Apply player-party interaction state");
    }

    private void Handle_PlayerPartyInteractionOptionSelected(MessagePayload<PlayerPartyInteractionOptionSelected> payload)
    {
        if (ModInformation.IsServer) return;

        var message = payload.What;
        optionRoute.Submit(new PlayerPartyInteractionOptionIntent(
            message.SessionId,
            PlayerPartyInteractionDialogState.Revision,
            message.Option));
    }

    private void Handle_NetworkPlayerPartyInteractionEnded(MessagePayload<NetworkPlayerPartyInteractionEnded> payload)
    {
        if (ModInformation.IsServer) return;

        var message = payload.What;

        GameThread.RunSafe(() =>
        {
            var isLocalInteraction = TryGetLocalInteractionParty(message, out var localParty);
            if (!isLocalInteraction && !IsCurrentLocalInteractionSession(message.SessionId)) return;

            endedInteractionSessionIds.Add(message.SessionId);
            PlayerPartyInteractionDialogState.RecordReplication(message.SessionId, message.Revision);
            PlayerPartyInteractionDialogState.Clear(message.SessionId);
            PlayerPartyTradeContext.End(message.SessionId, message.OutcomeType);
            PlayerPartyTradeOverlay.Instance.Hide(message.SessionId);
            var conversationWasOpened = openedConversationSessionIds.Remove(message.SessionId);

            var conversationManager = Campaign.Current?.ConversationManager;
            if (conversationWasOpened && conversationManager?.IsConversationInProgress == true)
                conversationManager.EndConversation();

            var hostileEncounterStarted = hostileEncounterSessionIds.Remove(message.SessionId);
            if (message.OutcomeType != PlayerPartyInteractionOutcomeType.HostileDemandAccepted || !hostileEncounterStarted)
            {
                if (conversationWasOpened)
                    CloseLocalPlayerPartyEncounter(localParty?.MobileParty);
                else
                    ClearLocalPartyEngageOrder(localParty?.MobileParty);
            }
        }, context: "End player-party interaction");
    }

    private void Handle_NetworkPlayerPartyInteractionDenied(MessagePayload<NetworkPlayerPartyInteractionDenied> payload)
    {
        if (ModInformation.IsServer) return;

        if (payload.What.Reason == PlayerPartyInteractionDeniedReason.Hostile)
            return;

        GameThread.RunSafe(
            ConversationPartyHold.ShowInteractionBlockedMessage,
            context: "Show player-party interaction denied");
    }

    private void Handle_NetworkPlayerPartyHostileEncounterStarted(MessagePayload<NetworkPlayerPartyHostileEncounterStarted> payload)
    {
        if (ModInformation.IsServer) return;

        var message = payload.What;
        GameThread.RunSafe(
            () => TryOpenHostileEncounter(message),
            context: "Open player-party hostile encounter");
    }

    private void Handle_NetworkClosePvpEncounter(MessagePayload<NetworkClosePvpEncounter> payload)
    {
        if (ModInformation.IsServer) return;

        GameThread.RunSafe(() =>
        {
            foreach (var partyId in payload.What.PartyIds ?? Array.Empty<string>())
                closedHostileEncounterPartyIds.Add(partyId);
        }, context: "Record closed player-party hostile encounter");
    }

    private void Handle_PlayerPartyTradeOfferChanged(MessagePayload<PlayerPartyTradeOfferChanged> payload)
    {
        if (ModInformation.IsServer) return;

        var message = payload.What;
        if (!TryGetLocalPartyId(out var partyId)) return;

        var offeredItems = message.InventoryLogic != null
            ? ResolveItemIds(message.InventoryLogic.GetSoldItems())
            : ResolveItemIds(GetOfferedBarterItems(message.BarterVM));
        var offeredGold = message.BarterVM != null
            ? GetOfferedGold(message.BarterVM)
            : 0;
        var offeredFiefs = message.BarterVM != null
            ? ResolveSettlementIds(GetOfferedBarterFiefs(message.BarterVM))
            : Array.Empty<string>();
        var offeredPrisoners = message.BarterVM != null
            ? ResolveCharacterIds(GetOfferedBarterPrisoners(message.BarterVM))
            : Array.Empty<TroopRosterElementData>();
        var offeredTroops = message.BarterVM != null
            ? ResolveTroopIds(GetOfferedBarterTroops(message.BarterVM))
            : Array.Empty<TroopRosterElementData>();
        var offeredPeace = message.BarterVM != null && HasOfferedPeace(message.BarterVM);
        tradeOfferRoute.Submit(new PlayerPartyTradeOfferIntent(
            message.SessionId,
            PlayerPartyInteractionDialogState.Revision,
            offeredItems,
            offeredTroops,
            offeredGold,
            offeredFiefs,
            offeredPrisoners,
            offeredPeace));
    }

    private void Handle_PlayerPartyTradeAcceptSelected(MessagePayload<PlayerPartyTradeAcceptSelected> payload)
    {
        if (ModInformation.IsServer) return;

        var message = payload.What;
        tradeAcceptRoute.Submit(new PlayerPartyTradeAcceptIntent(
            message.SessionId,
            PlayerPartyInteractionDialogState.Revision,
            message.Accepted));
    }

    private void Handle_NetworkPlayerPartyTradeOfferUpdated(MessagePayload<NetworkPlayerPartyTradeOfferUpdated> payload)
    {
        if (ModInformation.IsServer) return;

        var clientMessage = payload.What;
        if (clientMessage.Revision > 0 && clientMessage.Revision < PlayerPartyInteractionDialogState.Revision)
            return;
        GameThread.RunSafe(
            () => PlayerPartyTradeContext.ApplyOfferUpdate(clientMessage, objectManager),
            context: "Apply player-party trade offer update");
    }

    private bool CanOfferPeace(PlayerPartyInteractionSession session, string partyId)
    {
        var otherPartyId = session.GetOtherPartyId(partyId);
        if (!objectManager.TryGetObject<PartyBase>(partyId, out var party)) return false;
        if (!objectManager.TryGetObject<PartyBase>(otherPartyId, out var otherParty)) return false;

        return PlayerPartyPeaceBarterable.CanOfferPeace(party, otherParty);
    }

    // --- Authority-routed client mutations.  The old Network* command packets remain replication-only. ---

    private AuthorityRequestHeader CreateAuthorityHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out var snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private AuthorityHeaderValidation ValidateAuthorityHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out var snapshot))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        if (header.ProtocolVersion != snapshot.ProtocolVersion)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.InvalidRequest, "unsupported-protocol");
        if (!string.Equals(header.SessionId, snapshot.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session");
        return header.ExpectedRevision == snapshot.Revision
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-config");
    }

    private static string ValidateInteraction(string sessionId, long revision)
        => string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > AuthorityRequestHeader.MaximumSessionIdLength
            ? "invalid-interaction-session" : revision < 0 ? "invalid-interaction-revision" : null;
    private static string ValidateShownWire(RequestPlayerPartyInteractionShown request) => ValidateInteraction(request.InteractionSessionId, request.ExpectedInteractionRevision);
    private static string ValidateOptionWire(RequestPlayerPartyInteractionOption request)
        => ValidateInteraction(request.InteractionSessionId, request.ExpectedInteractionRevision) ??
           (!Enum.IsDefined(typeof(PlayerPartyInteractionOption), request.Option) || request.Option == (int)PlayerPartyInteractionOption.None ? "invalid-option" : null);
    private static string ValidateTradeAcceptWire(RequestPlayerPartyTradeAccept request) => ValidateInteraction(request.InteractionSessionId, request.ExpectedInteractionRevision);
    private static string ValidateTradeOfferWire(RequestPlayerPartyTradeOffer request)
    {
        var basic = ValidateInteraction(request.InteractionSessionId, request.ExpectedInteractionRevision);
        if (basic != null) return basic;
        if (request.OfferedGold < 0 || request.OfferedItems == null || request.OfferedTroops == null || request.OfferedFiefs == null || request.OfferedPrisoners == null) return "invalid-offer";
        return request.OfferedItems.Length > 128 || request.OfferedTroops.Length > 128 || request.OfferedFiefs.Length > 64 || request.OfferedPrisoners.Length > 128 ? "offer-too-large" : null;
    }
    private static string BuildTradeOfferKey(RequestPlayerPartyTradeOffer request)
        => request.InteractionSessionId + ":" + request.ExpectedInteractionRevision + ":" + request.OfferedGold + ":" + request.OfferedPeace + ":" +
           string.Join("|", request.OfferedItems.Select(x => x.ItemObjectData.ItemObjectId + ":" + x.ItemObjectData.ItemModifierId + ":" + x.Amount)) + ":" +
           string.Join("|", request.OfferedTroops.Select(x => x.CharacterId + ":" + x.Number + ":" + x.WoundedNumber + ":" + x.Xp)) + ":" +
           string.Join("|", request.OfferedFiefs) + ":" + string.Join("|", request.OfferedPrisoners.Select(x => x.CharacterId + ":" + x.Number));

    private AuthorityServerReply<PlayerPartyInteractionShownResult> ExecuteShown(AuthorityServerContext context, RequestPlayerPartyInteractionShown request)
    {
        AuthorityServerReply<PlayerPartyInteractionShownResult> reply = default;
        GameThread.RunSafe(() =>
        {
            if (!TryGetRoutableSession(request.InteractionSessionId, request.ExpectedInteractionRevision, context.Peer, out var session, out var partyId, out var status, out var reason))
            { reply = new AuthorityServerReply<PlayerPartyInteractionShownResult>(ShownResult(context.Header, request.InteractionSessionId, null, RevisionFor(request.InteractionSessionId), status, reason), false); return; }
            if (partyId == session.ResponderPartyId && session.ResponderPeer == null) session.ResponderPeer = context.Peer;
            session.Touch();
            SendInitialStates(session);
            reply = new AuthorityServerReply<PlayerPartyInteractionShownResult>(ShownResult(context.Header, session.SessionId, partyId, session.Revision, AuthorityResultStatus.Accepted, null), true);
        }, blocking: true, context: "Route player-party interaction shown");
        return reply;
    }

    private AuthorityServerReply<PlayerPartyInteractionOptionResult> ExecuteOption(AuthorityServerContext context, RequestPlayerPartyInteractionOption request)
    {
        AuthorityServerReply<PlayerPartyInteractionOptionResult> reply = default;
        GameThread.RunSafe(() =>
        {
            if (!TryGetRoutableSession(request.InteractionSessionId, request.ExpectedInteractionRevision, context.Peer, out var session, out var partyId, out var status, out var reason))
            { reply = new AuthorityServerReply<PlayerPartyInteractionOptionResult>(OptionResult(context.Header, request.InteractionSessionId, null, RevisionFor(request.InteractionSessionId), status, reason), false); return; }
            var option = (PlayerPartyInteractionOption)request.Option;
            if (!IsOptionLegal(session, partyId, option))
            { reply = new AuthorityServerReply<PlayerPartyInteractionOptionResult>(OptionResult(context.Header, session.SessionId, partyId, session.Revision, AuthorityResultStatus.Rejected, "illegal-option"), false); return; }
            if (partyId == session.ResponderPartyId && option == PlayerPartyInteractionOption.AcceptProposal && session.Proposal != PlayerPartyInteractionProposal.Trade && !CanApplyAcceptedProposal(session))
            { reply = new AuthorityServerReply<PlayerPartyInteractionOptionResult>(OptionResult(context.Header, session.SessionId, partyId, session.Revision, AuthorityResultStatus.Unavailable, "proposal-unavailable"), false); return; }
            if (partyId == session.ResponderPartyId && option == PlayerPartyInteractionOption.AcceptProposal)
                session.ResponderAcceptedProposal = true;
            session.AdvanceRevision();
            if (partyId == session.InitiatorPartyId) HandleInitiatorOption(session, option); else HandleResponderOption(session, option);
            reply = new AuthorityServerReply<PlayerPartyInteractionOptionResult>(OptionResult(context.Header, session.SessionId, partyId, RevisionFor(session.SessionId), AuthorityResultStatus.Accepted, null), true);
        }, blocking: true, context: "Route player-party interaction option");
        return reply;
    }

    private AuthorityServerReply<PlayerPartyTradeOfferResult> ExecuteTradeOffer(AuthorityServerContext context, RequestPlayerPartyTradeOffer request)
    {
        AuthorityServerReply<PlayerPartyTradeOfferResult> reply = default;
        GameThread.RunSafe(() =>
        {
            if (!TryGetRoutableSession(request.InteractionSessionId, request.ExpectedInteractionRevision, context.Peer, out var session, out var partyId, out var status, out var reason))
            { reply = new AuthorityServerReply<PlayerPartyTradeOfferResult>(TradeOfferResult(context.Header, request.InteractionSessionId, null, RevisionFor(request.InteractionSessionId), status, reason), false); return; }
            if (session.InitiatorPhase != PlayerPartyInteractionPhase.TradeActive || session.ResponderPhase != PlayerPartyInteractionPhase.TradeActive ||
                !ValidateCanonicalOffer(session, partyId, request, out reason))
            { reply = new AuthorityServerReply<PlayerPartyTradeOfferResult>(TradeOfferResult(context.Header, session.SessionId, partyId, session.Revision, AuthorityResultStatus.Rejected, reason ?? "trade-inactive"), false); return; }
            session.AdvanceRevision();
            session.SetTradeOffer(partyId, request.OfferedItems, request.OfferedTroops, request.OfferedGold, request.OfferedFiefs, request.OfferedPrisoners, request.OfferedPeace);
            session.InitiatorAcceptedTrade = false; session.ResponderAcceptedTrade = false;
            SendTradeOffers(session); SendTradeStates(session, false);
            reply = new AuthorityServerReply<PlayerPartyTradeOfferResult>(TradeOfferResult(context.Header, session.SessionId, partyId, session.Revision, AuthorityResultStatus.Accepted, null), true);
        }, blocking: true, context: "Route player-party trade offer");
        return reply;
    }

    private AuthorityServerReply<PlayerPartyTradeAcceptResult> ExecuteTradeAccept(AuthorityServerContext context, RequestPlayerPartyTradeAccept request)
    {
        AuthorityServerReply<PlayerPartyTradeAcceptResult> reply = default;
        GameThread.RunSafe(() =>
        {
            if (!TryGetRoutableSession(request.InteractionSessionId, request.ExpectedInteractionRevision, context.Peer, out var session, out var partyId, out var status, out var reason))
            { reply = new AuthorityServerReply<PlayerPartyTradeAcceptResult>(TradeAcceptResult(context.Header, request.InteractionSessionId, null, RevisionFor(request.InteractionSessionId), status, reason), false); return; }
            if (session.InitiatorPhase != PlayerPartyInteractionPhase.TradeActive || session.ResponderPhase != PlayerPartyInteractionPhase.TradeActive)
            { reply = new AuthorityServerReply<PlayerPartyTradeAcceptResult>(TradeAcceptResult(context.Header, session.SessionId, partyId, session.Revision, AuthorityResultStatus.Rejected, "trade-inactive"), false); return; }
            if (request.Accepted && !ValidateCurrentTrade(session, out reason))
            { reply = new AuthorityServerReply<PlayerPartyTradeAcceptResult>(TradeAcceptResult(context.Header, session.SessionId, partyId, session.Revision, AuthorityResultStatus.Unavailable, reason), false); return; }
            session.AdvanceRevision();
            if (partyId == session.InitiatorPartyId) session.InitiatorAcceptedTrade = request.Accepted; else session.ResponderAcceptedTrade = request.Accepted;
            SendTradeStates(session);
            if (session.InitiatorAcceptedTrade && session.ResponderAcceptedTrade) EndSession(session, PlayerPartyInteractionOutcomeType.TradeAccepted);
            reply = new AuthorityServerReply<PlayerPartyTradeAcceptResult>(TradeAcceptResult(context.Header, session.SessionId, partyId, RevisionFor(session.SessionId), AuthorityResultStatus.Accepted, null), true);
        }, blocking: true, context: "Route player-party trade accept");
        return reply;
    }

    private bool TryGetRoutableSession(string sessionId, long expectedRevision, NetPeer peer, out PlayerPartyInteractionSession session,
        out string partyId, out AuthorityResultStatus status, out string reason)
    {
        session = null; partyId = null; status = AuthorityResultStatus.Rejected; reason = "interaction-missing";
        if (!sessionsById.TryGetValue(sessionId, out session))
        {
            if (endedSessionRevisions.ContainsKey(sessionId)) { status = AuthorityResultStatus.StaleState; reason = "interaction-ended"; }
            return false;
        }
        if (DateTime.UtcNow - session.LastActivityUtc > TimeSpan.FromMinutes(5))
        {
            EndSession(session, PlayerPartyInteractionOutcomeType.Disconnected);
            session = null; status = AuthorityResultStatus.Unavailable; reason = "interaction-expired";
            return false;
        }
        if (session.Revision != expectedRevision) { status = AuthorityResultStatus.StaleState; reason = "stale-interaction"; return false; }
        if (!TryGetSessionPartyId(session, peer, out partyId)) { status = AuthorityResultStatus.Unauthorized; reason = "interaction-controller"; return false; }
        return true;
    }

    private long RevisionFor(string sessionId)
        => sessionsById.TryGetValue(sessionId, out var active) ? active.Revision : endedSessionRevisions.TryGetValue(sessionId, out var ended) ? ended : 0;

    private static bool IsOptionLegal(PlayerPartyInteractionSession session, string partyId, PlayerPartyInteractionOption option)
    {
        if (partyId == session.InitiatorPartyId)
        {
            if (session.InitiatorPhase == PlayerPartyInteractionPhase.InitialOptions)
                return session.InitiatorEnabledOptions.Contains(option) && option != PlayerPartyInteractionOption.OfferServices;
            if (session.InitiatorPhase == PlayerPartyInteractionPhase.ClanJoinConfirm)
                return option == PlayerPartyInteractionOption.ConfirmJoinClan || option == PlayerPartyInteractionOption.CancelJoinClan;
            if (session.InitiatorPhase == PlayerPartyInteractionPhase.HostileDemandConfirm)
                return option == PlayerPartyInteractionOption.ConfirmHostileDemand || option == PlayerPartyInteractionOption.CancelHostileDemand;
            return session.InitiatorPhase == PlayerPartyInteractionPhase.TradeActive && option == PlayerPartyInteractionOption.Leave;
        }
        if (partyId != session.ResponderPartyId) return false;
        if (session.ResponderPhase == PlayerPartyInteractionPhase.ProposalPending)
            return option == PlayerPartyInteractionOption.AcceptProposal || option == PlayerPartyInteractionOption.DeclineProposal || option == PlayerPartyInteractionOption.Leave;
        if (session.ResponderPhase == PlayerPartyInteractionPhase.HostileDemandPending)
            return option == PlayerPartyInteractionOption.RefuseHostileDemand || option == PlayerPartyInteractionOption.YieldHostileDemand;
        return session.ResponderPhase == PlayerPartyInteractionPhase.TradeActive && option == PlayerPartyInteractionOption.Leave;
    }

    private bool CanApplyAcceptedProposal(PlayerPartyInteractionSession session)
    {
        if (!objectManager.TryGetObject(session.InitiatorPartyId, out PartyBase initiator) || !objectManager.TryGetObject(session.ResponderPartyId, out PartyBase responder)) return false;
        switch (session.Proposal)
        {
            case PlayerPartyInteractionProposal.JoinClan: return clanMembershipService.CanJoin(initiator, responder);
            case PlayerPartyInteractionProposal.Marriage: return clanMembershipService.CanMarry(initiator, responder);
            case PlayerPartyInteractionProposal.TravelTogether: return PlayerPartyTravelGroup.CanCreate(initiator, responder);
            case PlayerPartyInteractionProposal.Vassal: return IsVassalServiceAvailable(initiator, responder, out _);
            default: return false;
        }
    }

    private bool ValidateCurrentTrade(PlayerPartyInteractionSession session, out string reason)
    {
        reason = null;
        if (!objectManager.TryGetObject(session.InitiatorPartyId, out PartyBase initiator) || !objectManager.TryGetObject(session.ResponderPartyId, out PartyBase responder)) { reason = "party-unavailable"; return false; }
        return ValidateCanonicalOffer(initiator, responder, session.InitiatorOfferedItems, session.InitiatorOfferedTroops, session.InitiatorOfferedGold, session.InitiatorOfferedFiefs, session.InitiatorOfferedPrisoners, session.InitiatorOfferedPeace, out reason) &&
               ValidateCanonicalOffer(responder, initiator, session.ResponderOfferedItems, session.ResponderOfferedTroops, session.ResponderOfferedGold, session.ResponderOfferedFiefs, session.ResponderOfferedPrisoners, session.ResponderOfferedPeace, out reason);
    }

    private bool ValidateCanonicalOffer(PlayerPartyInteractionSession session, string partyId, RequestPlayerPartyTradeOffer request, out string reason)
    {
        reason = null;
        if (!objectManager.TryGetObject(partyId, out PartyBase party) || !objectManager.TryGetObject(session.GetOtherPartyId(partyId), out PartyBase other)) { reason = "party-unavailable"; return false; }
        return ValidateCanonicalOffer(party, other, request.OfferedItems, request.OfferedTroops, request.OfferedGold, request.OfferedFiefs, request.OfferedPrisoners, request.OfferedPeace, out reason);
    }

    private bool ValidateCanonicalOffer(PartyBase party, PartyBase other, ItemRosterElementData[] items, TroopRosterElementData[] troops, int gold,
        string[] fiefs, TroopRosterElementData[] prisoners, bool peace, out string reason)
    {
        reason = null;
        if (gold < 0 || gold > (party.LeaderHero?.Gold ?? 0)) { reason = "invalid-gold"; return false; }
        if (peace && !PlayerPartyPeaceBarterable.CanOfferPeace(party, other)) { reason = "invalid-peace"; return false; }
        var itemKeys = new HashSet<string>();
        foreach (var item in items ?? Array.Empty<ItemRosterElementData>())
        {
            var data = item.ItemObjectData;
            if (item.Amount <= 0 || string.IsNullOrWhiteSpace(data.ItemObjectId) || !itemKeys.Add(data.ItemObjectId + "|" + data.ItemModifierId + "|" + data.ItemModifierNull)) { reason = "invalid-item"; return false; }
            if (!objectManager.TryGetObject(data.ItemObjectId, out ItemObject itemObject)) { reason = "unknown-item"; return false; }
            ItemModifier modifier = null;
            if (!data.ItemModifierNull && !objectManager.TryGetObject(data.ItemModifierId, out modifier)) { reason = "unknown-modifier"; return false; }
            var element = new EquipmentElement(itemObject, modifier);
            var available = party.ItemRoster.Where(x => x.EquipmentElement.Equals(element)).Sum(x => x.Amount);
            if (item.Amount > available) { reason = "item-not-owned"; return false; }
        }
        if (!ValidateRosterOffer(party, party.MemberRoster, troops, false, out reason) || !ValidateRosterOffer(party, party.PrisonRoster, prisoners, true, out reason)) return false;
        var fiefIds = new HashSet<string>();
        foreach (var fiefId in fiefs ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(fiefId) || !fiefIds.Add(fiefId) || !objectManager.TryGetObject(fiefId, out Settlement fief) || fief.OwnerClan?.Leader != party.LeaderHero)
            { reason = "fief-not-owned"; return false; }
        }
        return true;
    }

    private bool ValidateRosterOffer(PartyBase party, TroopRoster roster, TroopRosterElementData[] requested, bool prisoners, out string reason)
    {
        reason = null; var ids = new HashSet<string>();
        foreach (var entry in requested ?? Array.Empty<TroopRosterElementData>())
        {
            if (string.IsNullOrWhiteSpace(entry.CharacterId) || entry.Number <= 0 || entry.WoundedNumber < 0 || entry.WoundedNumber > entry.Number || entry.Xp < 0 || !ids.Add(entry.CharacterId)) { reason = "invalid-troop"; return false; }
            if (!objectManager.TryGetObject(entry.CharacterId, out CharacterObject character) || (!prisoners && character == party.LeaderHero?.CharacterObject)) { reason = "troop-not-owned"; return false; }
            if (entry.Number > roster.GetElementNumber(character)) { reason = "troop-not-owned"; return false; }
        }
        return true;
    }

    private PlayerPartyInteractionShownResult ShownResult(AuthorityRequestHeader header, string sessionId, string partyId, long revision, AuthorityResultStatus status, string reason)
        => new PlayerPartyInteractionShownResult(new AuthorityResultHeader(header.SessionId, header.RequestId, status, revision, reason), sessionId, partyId, revision);
    private PlayerPartyInteractionOptionResult OptionResult(AuthorityRequestHeader header, string sessionId, string partyId, long revision, AuthorityResultStatus status, string reason)
        => new PlayerPartyInteractionOptionResult(new AuthorityResultHeader(header.SessionId, header.RequestId, status, revision, reason), sessionId, partyId, revision);
    private PlayerPartyTradeOfferResult TradeOfferResult(AuthorityRequestHeader header, string sessionId, string partyId, long revision, AuthorityResultStatus status, string reason)
        => new PlayerPartyTradeOfferResult(new AuthorityResultHeader(header.SessionId, header.RequestId, status, revision, reason), sessionId, partyId, revision);
    private PlayerPartyTradeAcceptResult TradeAcceptResult(AuthorityRequestHeader header, string sessionId, string partyId, long revision, AuthorityResultStatus status, string reason)
        => new PlayerPartyTradeAcceptResult(new AuthorityResultHeader(header.SessionId, header.RequestId, status, revision, reason), sessionId, partyId, revision);
    private PlayerPartyInteractionShownResult CreateShownTerminal(AuthorityRequestHeader h, AuthorityResultStatus s, string r) => ShownResult(h, string.Empty, null, 0, s, r);
    private PlayerPartyInteractionOptionResult CreateOptionTerminal(AuthorityRequestHeader h, AuthorityResultStatus s, string r) => OptionResult(h, string.Empty, null, 0, s, r);
    private PlayerPartyTradeOfferResult CreateTradeOfferTerminal(AuthorityRequestHeader h, AuthorityResultStatus s, string r) => TradeOfferResult(h, string.Empty, null, 0, s, r);
    private PlayerPartyTradeAcceptResult CreateTradeAcceptTerminal(AuthorityRequestHeader h, AuthorityResultStatus s, string r) => TradeAcceptResult(h, string.Empty, null, 0, s, r);
    private static AuthorityCommitProbeResult ProbeShown(PlayerPartyInteractionShownResult x) => ProbePostState(x.InteractionSessionId, x.PartyId, x.InteractionRevision);
    private static AuthorityCommitProbeResult ProbeOption(PlayerPartyInteractionOptionResult x) => ProbePostState(x.InteractionSessionId, x.PartyId, x.InteractionRevision);
    private static AuthorityCommitProbeResult ProbeTradeOffer(PlayerPartyTradeOfferResult x) => ProbePostState(x.InteractionSessionId, x.PartyId, x.InteractionRevision);
    private static AuthorityCommitProbeResult ProbeTradeAccept(PlayerPartyTradeAcceptResult x) => ProbePostState(x.InteractionSessionId, x.PartyId, x.InteractionRevision);
    private static AuthorityCommitProbeResult ProbePostState(string sessionId, string partyId, long revision)
        => string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(partyId) ? AuthorityCommitProbeResult.Invalid :
           PlayerPartyInteractionDialogState.HasAppliedPostState(sessionId, partyId, revision) ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    private void ResyncInteraction(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        shownRoute.Submit(new PlayerPartyInteractionShownIntent(sessionId, PlayerPartyInteractionDialogState.Revision));
    }
    private static void PresentRouteOutcome(AuthorityClientOutcome<PlayerPartyInteractionShownResult> outcome) { if (!outcome.Applied) ConversationPartyHold.ShowInteractionBlockedMessage(); }
    private static void PresentRouteOutcome(AuthorityClientOutcome<PlayerPartyInteractionOptionResult> outcome) { if (!outcome.Applied) ConversationPartyHold.ShowInteractionBlockedMessage(); }
    private static void PresentRouteOutcome(AuthorityClientOutcome<PlayerPartyTradeOfferResult> outcome) { if (!outcome.Applied) ConversationPartyHold.ShowInteractionBlockedMessage(); }
    private static void PresentRouteOutcome(AuthorityClientOutcome<PlayerPartyTradeAcceptResult> outcome) { if (!outcome.Applied) ConversationPartyHold.ShowInteractionBlockedMessage(); }

    private void Handle_PlayerDisconnected(MessagePayload<PlayerDisconnected> payload)
    {
        if (ModInformation.IsClient) return;

        var peer = payload.What.PlayerId;
        foreach (var session in sessionsById.Values.ToArray())
        {
            if (session.InitiatorPeer == peer || session.ResponderPeer == peer)
                EndSession(session, PlayerPartyInteractionOutcomeType.Disconnected);
        }
    }

    private void HandleInitiatorOption(PlayerPartyInteractionSession session, PlayerPartyInteractionOption option)
    {
        if (TryHandleInitiatorLeaveOption(session, option)) return;
        if (option == PlayerPartyInteractionOption.OfferServices) return;
        if (TryHandleInitiatorClanJoinOption(session, option)) return;
        if (TryHandleInitiatorHostileDemandOption(session, option)) return;

        HandleInitiatorProposalOption(session, option);
    }

    private bool TryHandleInitiatorClanJoinOption(
        PlayerPartyInteractionSession session,
        PlayerPartyInteractionOption option)
    {
        if (option == PlayerPartyInteractionOption.JoinClan)
        {
            if (!session.InitiatorEnabledOptions.Contains(option)) return true;
            SendInitiatorState(
                session,
                PlayerPartyInteractionPhase.ClanJoinConfirm,
                PlayerPartyInteractionProposal.JoinClan,
                new[]
                {
                    PlayerPartyInteractionOption.ConfirmJoinClan,
                    PlayerPartyInteractionOption.CancelJoinClan
                });
            return true;
        }

        if (option == PlayerPartyInteractionOption.ConfirmJoinClan)
        {
            HandleInitiatorProposalOption(session, PlayerPartyInteractionOption.JoinClan);
            return true;
        }

        if (option == PlayerPartyInteractionOption.CancelJoinClan)
        {
            EndSession(session, PlayerPartyInteractionOutcomeType.Left);
            return true;
        }

        return false;
    }

    private bool TryHandleInitiatorLeaveOption(PlayerPartyInteractionSession session, PlayerPartyInteractionOption option)
    {
        if (option != PlayerPartyInteractionOption.Leave) return false;

        if (session.Proposal == PlayerPartyInteractionProposal.HostileDemand && session.HostileDemandConfirmed)
            return true;

        EndSession(session, GetLeaveOutcome(session));
        return true;
    }

    private bool TryHandleInitiatorHostileDemandOption(PlayerPartyInteractionSession session, PlayerPartyInteractionOption option)
    {
        switch (option)
        {
            case PlayerPartyInteractionOption.HostileDemand:
                HandleHostileDemandSelected(session, option);
                return true;
            case PlayerPartyInteractionOption.ConfirmHostileDemand:
                HandleHostileDemandConfirmed(session);
                return true;
            case PlayerPartyInteractionOption.CancelHostileDemand:
                HandleHostileDemandCanceled(session);
                return true;
            default:
                return false;
        }
    }

    private void HandleHostileDemandSelected(PlayerPartyInteractionSession session, PlayerPartyInteractionOption option)
    {
        if (!session.InitiatorEnabledOptions.Contains(option)) return;

        session.Proposal = PlayerPartyInteractionProposal.HostileDemand;
        session.HostileDemandConfirmed = false;
        SendInitiatorState(
            session,
            PlayerPartyInteractionPhase.HostileDemandConfirm,
            session.Proposal,
            new[]
            {
                PlayerPartyInteractionOption.ConfirmHostileDemand,
                PlayerPartyInteractionOption.CancelHostileDemand
            });
    }

    private void HandleHostileDemandConfirmed(PlayerPartyInteractionSession session)
    {
        if (session.Proposal != PlayerPartyInteractionProposal.HostileDemand) return;
        if (session.HostileDemandConfirmed) return;

        session.HostileDemandConfirmed = true;
        SendInitiatorState(session, PlayerPartyInteractionPhase.WaitingForResponse, session.Proposal, Array.Empty<PlayerPartyInteractionOption>());
        SendResponderState(
            session,
            PlayerPartyInteractionPhase.HostileDemandPending,
            session.Proposal,
            new[]
            {
                PlayerPartyInteractionOption.RefuseHostileDemand,
                PlayerPartyInteractionOption.YieldHostileDemand
            });
    }

    private void HandleHostileDemandCanceled(PlayerPartyInteractionSession session)
    {
        if (session.Proposal != PlayerPartyInteractionProposal.HostileDemand) return;
        if (session.HostileDemandConfirmed) return;

        EndSession(session, PlayerPartyInteractionOutcomeType.Left);
    }

    private void HandleInitiatorProposalOption(PlayerPartyInteractionSession session, PlayerPartyInteractionOption option)
    {
        var proposal = ToProposal(option);
        if (proposal == PlayerPartyInteractionProposal.None) return;
        if (!session.InitiatorEnabledOptions.Contains(option)) return;

        session.Proposal = proposal;
        SendInitiatorState(session, PlayerPartyInteractionPhase.WaitingForResponse, proposal, Array.Empty<PlayerPartyInteractionOption>());
        SendResponderState(
            session,
            PlayerPartyInteractionPhase.ProposalPending,
            proposal,
            new[]
            {
                PlayerPartyInteractionOption.AcceptProposal,
                PlayerPartyInteractionOption.DeclineProposal,
                PlayerPartyInteractionOption.Leave
            });
    }

    private void HandleResponderOption(PlayerPartyInteractionSession session, PlayerPartyInteractionOption option)
    {
        if (option == PlayerPartyInteractionOption.YieldHostileDemand)
        {
            if (session.Proposal != PlayerPartyInteractionProposal.HostileDemand) return;
            if (!session.HostileDemandConfirmed) return;

            EndSession(session, PlayerPartyInteractionOutcomeType.HostileDemandYielded);
            return;
        }

        if (option == PlayerPartyInteractionOption.RefuseHostileDemand)
        {
            if (session.Proposal != PlayerPartyInteractionProposal.HostileDemand) return;
            if (!session.HostileDemandConfirmed) return;

            EndSession(session, PlayerPartyInteractionOutcomeType.HostileDemandAccepted);
            return;
        }

        if (option == PlayerPartyInteractionOption.Leave)
        {
            if (session.Proposal == PlayerPartyInteractionProposal.HostileDemand && session.HostileDemandConfirmed)
                return;

            EndSession(session, GetLeaveOutcome(session));
            return;
        }

        if (option == PlayerPartyInteractionOption.DeclineProposal)
        {
            EndSession(session, GetDeclinedOutcome(session.Proposal));
            return;
        }

        if (option != PlayerPartyInteractionOption.AcceptProposal) return;

        if (session.Proposal == PlayerPartyInteractionProposal.Trade)
        {
            session.InitiatorAcceptedTrade = false;
            session.ResponderAcceptedTrade = false;
            SendTradeStates(session);
            return;
        }

        if (!session.ResponderAcceptedProposal) return;
        EndSession(session, GetAcceptedOutcome(session.Proposal));
    }

    private void SendInitialStates(PlayerPartyInteractionSession session)
    {
        SendInitiatorState(
            session,
            PlayerPartyInteractionPhase.InitialOptions,
            PlayerPartyInteractionProposal.None,
            session.InitiatorOptions.ToArray(),
            session.InitiatorEnabledOptions.ToArray());

        SendResponderState(
            session,
            PlayerPartyInteractionPhase.WaitingForProposal,
            PlayerPartyInteractionProposal.None,
            new[] { PlayerPartyInteractionOption.Leave });
    }

    private void SendTradeStates(PlayerPartyInteractionSession session)
        => SendTradeStates(session, true);

    private void SendTradeStates(PlayerPartyInteractionSession session, bool sendOffers)
    {
        SendInitiatorState(session, PlayerPartyInteractionPhase.TradeActive, session.Proposal, Array.Empty<PlayerPartyInteractionOption>());
        SendResponderState(session, PlayerPartyInteractionPhase.TradeActive, session.Proposal, Array.Empty<PlayerPartyInteractionOption>());

        if (sendOffers)
            SendTradeOffers(session);
    }

    private void SendTradeOffers(PlayerPartyInteractionSession session)
    {
        SendToParticipants(session, new NetworkPlayerPartyTradeOfferUpdated(
            session.SessionId,
            session.InitiatorPartyId,
            session.InitiatorOfferedItems,
            session.InitiatorOfferedTroops,
            session.InitiatorOfferedGold,
            session.InitiatorOfferedFiefs,
            session.InitiatorOfferedPrisoners,
            session.InitiatorOfferedPeace,
            session.Revision));

        SendToParticipants(session, new NetworkPlayerPartyTradeOfferUpdated(
            session.SessionId,
            session.ResponderPartyId,
            session.ResponderOfferedItems,
            session.ResponderOfferedTroops,
            session.ResponderOfferedGold,
            session.ResponderOfferedFiefs,
            session.ResponderOfferedPrisoners,
            session.ResponderOfferedPeace,
            session.Revision));
    }

    private void SendInitiatorState(
        PlayerPartyInteractionSession session,
        PlayerPartyInteractionPhase phase,
        PlayerPartyInteractionProposal proposal,
        PlayerPartyInteractionOption[] options,
        PlayerPartyInteractionOption[] enabledOptions = null)
    {
        session.InitiatorPhase = phase;
        var partyItems = phase == PlayerPartyInteractionPhase.TradeActive
            ? ResolvePartyItemIds(session.InitiatorPartyId)
            : Array.Empty<ItemRosterElementData>();
        var otherPartyItems = phase == PlayerPartyInteractionPhase.TradeActive
            ? ResolvePartyItemIds(session.ResponderPartyId)
            : Array.Empty<ItemRosterElementData>();

        SendToParticipants(session, new NetworkPlayerPartyInteractionState(
            session.SessionId,
            session.InitiatorPartyId,
            session.ResponderPartyId,
            session.ResponderName,
            phase,
            proposal,
            options,
            true,
            session.InitiatorAcceptedTrade,
            session.ResponderAcceptedTrade,
            partyItems,
            otherPartyItems,
            enabledOptions,
            session.IsHostile,
            session.VassalUnavailableReason,
            session.Revision));
    }

    private void SendResponderState(
        PlayerPartyInteractionSession session,
        PlayerPartyInteractionPhase phase,
        PlayerPartyInteractionProposal proposal,
        PlayerPartyInteractionOption[] options,
        PlayerPartyInteractionOption[] enabledOptions = null)
    {
        session.ResponderPhase = phase;
        var partyItems = phase == PlayerPartyInteractionPhase.TradeActive
            ? ResolvePartyItemIds(session.ResponderPartyId)
            : Array.Empty<ItemRosterElementData>();
        var otherPartyItems = phase == PlayerPartyInteractionPhase.TradeActive
            ? ResolvePartyItemIds(session.InitiatorPartyId)
            : Array.Empty<ItemRosterElementData>();

        SendToParticipants(session, new NetworkPlayerPartyInteractionState(
            session.SessionId,
            session.ResponderPartyId,
            session.InitiatorPartyId,
            session.InitiatorName,
            phase,
            proposal,
            options,
            false,
            session.InitiatorAcceptedTrade,
            session.ResponderAcceptedTrade,
            partyItems,
            otherPartyItems,
            enabledOptions,
            session.IsHostile,
            session.VassalUnavailableReason,
            session.Revision));
    }

    private void EndSession(PlayerPartyInteractionSession session, PlayerPartyInteractionOutcomeType outcomeType)
    {
        if ((outcomeType == PlayerPartyInteractionOutcomeType.ClanJoinAccepted ||
             outcomeType == PlayerPartyInteractionOutcomeType.MarriageAccepted ||
             outcomeType == PlayerPartyInteractionOutcomeType.TravelTogetherAccepted ||
             outcomeType == PlayerPartyInteractionOutcomeType.VassalAccepted) &&
            !session.ResponderAcceptedProposal)
        {
            Logger.Warning("Refused player-party outcome without a recorded responder acceptance. SessionId={SessionId} Outcome={Outcome}", session.SessionId, outcomeType);
            return;
        }
        lock (sessionGate)
        {
            if (!sessionsById.TryRemove(session.SessionId, out _)) return;

            session.AdvanceRevision();
            endedSessionRevisions[session.SessionId] = session.Revision;

            sessionsByPartyId.TryRemove(session.InitiatorPartyId, out _);
            sessionsByPartyId.TryRemove(session.ResponderPartyId, out _);
            conversationPartyTracker.EndPvpConversation(session.InitiatorPartyId);
        }

        var outcome = new PlayerPartyInteractionOutcome(session, outcomeType);
        outcomeHandler.Handle(outcome);

        SendToParticipants(session, new NetworkPlayerPartyInteractionEnded(
            session.SessionId,
            session.InitiatorPartyId,
            session.ResponderPartyId,
            outcomeType,
            session.Revision));

        if (outcomeType == PlayerPartyInteractionOutcomeType.HostileDemandAccepted ||
            outcomeType == PlayerPartyInteractionOutcomeType.HostileDemandYielded)
        {
            hostileEncounterService.TryStartHostileEncounter(
                session.SessionId,
                session.InitiatorPartyId,
                session.ResponderPartyId,
                outcomeType == PlayerPartyInteractionOutcomeType.HostileDemandYielded);
        }
    }

    private void SendToParticipants(PlayerPartyInteractionSession session, IMessage message)
    {
        if (session?.InitiatorPeer != null)
            network.Send(session.InitiatorPeer, message);
        if (session?.ResponderPeer != null && !ReferenceEquals(session.ResponderPeer, session.InitiatorPeer))
            network.Send(session.ResponderPeer, message);
    }

    private void AddInitialOptions(PlayerPartyInteractionSession session, PartyBase initiatorParty, PartyBase responderParty)
    {
        AddInitiatorOption(session, PlayerPartyInteractionOption.TradeProposal, enabled: true);
        AddInitiatorOption(
            session,
            PlayerPartyInteractionOption.MarriageProposal,
            clanMembershipService.CanMarry(initiatorParty, responderParty));
        AddInitiatorOption(
            session,
            PlayerPartyInteractionOption.TravelTogether,
            PlayerPartyTravelGroup.CanCreate(initiatorParty, responderParty));
        AddInitiatorOption(session, PlayerPartyInteractionOption.OfferServices, enabled: !session.IsHostile);
        AddInitiatorOption(session, PlayerPartyInteractionOption.HostileDemand, hostileEncounterService.CanStartHostileEncounter(initiatorParty, responderParty));
        AddInitiatorOption(
            session,
            PlayerPartyInteractionOption.JoinClan,
            clanMembershipService.CanJoin(initiatorParty, responderParty));
        var vassalAvailable = IsVassalServiceAvailable(initiatorParty, responderParty, out var vassalUnavailableReason);
        session.VassalUnavailableReason = vassalUnavailableReason;
        AddInitiatorOption(
            session,
            PlayerPartyInteractionOption.Vassal,
            vassalAvailable);
        AddInitiatorOption(session, PlayerPartyInteractionOption.Leave, enabled: true);
    }

    private static void AddInitiatorOption(PlayerPartyInteractionSession session, PlayerPartyInteractionOption option, bool enabled)
    {
        session.InitiatorOptions.Add(option);
        if (enabled)
            session.InitiatorEnabledOptions.Add(option);
    }

    private static bool IsVassalServiceAvailable(
        PartyBase initiatorParty,
        PartyBase responderParty,
        out PlayerPartyInteractionVassalUnavailableReason unavailableReason)
    {
        var initiatorClan = initiatorParty.LeaderHero?.Clan ?? initiatorParty.MobileParty?.ActualClan;
        var responderHero = responderParty.LeaderHero;
        var responderKingdom = responderHero?.Clan?.Kingdom;

        if (responderHero?.IsKingdomLeader != true || responderKingdom?.RulingClan != responderHero.Clan)
        {
            unavailableReason = PlayerPartyInteractionVassalUnavailableReason.TargetIsNotKingdomLeader;
            return false;
        }

        if (initiatorClan == null)
        {
            unavailableReason = PlayerPartyInteractionVassalUnavailableReason.InitiatorHasNoClan;
            return false;
        }

        if (initiatorClan.Kingdom != null)
        {
            unavailableReason = PlayerPartyInteractionVassalUnavailableReason.InitiatorIsInKingdom;
            return false;
        }

        if (initiatorClan.Tier < 2)
        {
            unavailableReason = PlayerPartyInteractionVassalUnavailableReason.InitiatorClanTierTooLow;
            return false;
        }

        unavailableReason = PlayerPartyInteractionVassalUnavailableReason.None;
        return true;
    }

    private static PlayerPartyInteractionProposal ToProposal(PlayerPartyInteractionOption option)
    {
        switch (option)
        {
            case PlayerPartyInteractionOption.TradeProposal:
                return PlayerPartyInteractionProposal.Trade;
            case PlayerPartyInteractionOption.JoinClan:
                return PlayerPartyInteractionProposal.JoinClan;
            case PlayerPartyInteractionOption.Vassal:
                return PlayerPartyInteractionProposal.Vassal;
            case PlayerPartyInteractionOption.HostileDemand:
                return PlayerPartyInteractionProposal.HostileDemand;
            case PlayerPartyInteractionOption.TravelTogether:
                return PlayerPartyInteractionProposal.TravelTogether;
            case PlayerPartyInteractionOption.MarriageProposal:
                return PlayerPartyInteractionProposal.Marriage;
            default:
                return PlayerPartyInteractionProposal.None;
        }
    }

    private static PlayerPartyInteractionOutcomeType GetAcceptedOutcome(PlayerPartyInteractionProposal proposal)
    {
        switch (proposal)
        {
            case PlayerPartyInteractionProposal.JoinClan:
                return PlayerPartyInteractionOutcomeType.ClanJoinAccepted;
            case PlayerPartyInteractionProposal.Vassal:
                return PlayerPartyInteractionOutcomeType.VassalAccepted;
            case PlayerPartyInteractionProposal.TravelTogether:
                return PlayerPartyInteractionOutcomeType.TravelTogetherAccepted;
            case PlayerPartyInteractionProposal.Marriage:
                return PlayerPartyInteractionOutcomeType.MarriageAccepted;
            default:
                return PlayerPartyInteractionOutcomeType.None;
        }
    }

    private static PlayerPartyInteractionOutcomeType GetDeclinedOutcome(PlayerPartyInteractionProposal proposal)
    {
        switch (proposal)
        {
            case PlayerPartyInteractionProposal.Trade:
                return PlayerPartyInteractionOutcomeType.TradeDeclined;
            case PlayerPartyInteractionProposal.JoinClan:
                return PlayerPartyInteractionOutcomeType.ClanJoinDeclined;
            case PlayerPartyInteractionProposal.Vassal:
                return PlayerPartyInteractionOutcomeType.VassalDeclined;
            case PlayerPartyInteractionProposal.TravelTogether:
                return PlayerPartyInteractionOutcomeType.TravelTogetherDeclined;
            case PlayerPartyInteractionProposal.Marriage:
                return PlayerPartyInteractionOutcomeType.MarriageDeclined;
            default:
                return PlayerPartyInteractionOutcomeType.None;
        }
    }

    private static PlayerPartyInteractionOutcomeType GetLeaveOutcome(PlayerPartyInteractionSession session)
    {
        if (session?.Proposal == PlayerPartyInteractionProposal.Trade)
            return PlayerPartyInteractionOutcomeType.TradeDeclined;

        return PlayerPartyInteractionOutcomeType.Left;
    }

    private PlayerPartyInteractionSession FindExistingSession(string partyId)
    {
        if (!sessionsByPartyId.TryGetValue(partyId, out var sessionId))
            return null;

        if (sessionsById.TryGetValue(sessionId, out var session))
            return session;

        sessionsByPartyId.TryRemove(partyId, out _);
        return null;
    }

    private static bool IsSamePair(PlayerPartyInteractionSession session, string partyA, string partyB)
        => (session.InitiatorPartyId == partyA && session.ResponderPartyId == partyB) ||
           (session.InitiatorPartyId == partyB && session.ResponderPartyId == partyA);

    private static bool AreHostile(PartyBase initiatorParty, PartyBase responderParty)
    {
        var initiatorFaction = initiatorParty?.MapFaction;
        var responderFaction = responderParty?.MapFaction;

        if (initiatorFaction == null || responderFaction == null) return false;
        if (initiatorFaction == responderFaction) return false;

        return FactionManager.IsAtWarAgainstFaction(initiatorFaction, responderFaction) ||
               HasFactionWar(initiatorFaction, responderFaction) ||
               HasFactionWar(responderFaction, initiatorFaction);
    }

    private static bool HasFactionWar(IFaction faction, IFaction otherFaction)
    {
        try
        {
            return faction.FactionsAtWarWith?.Contains(otherFaction) == true;
        }
        catch (NullReferenceException)
        {
            return false;
        }
    }

    private static void HoldParty(MobileParty party)
    {
        if (party == null) return;

        party.SetMoveModeHold();
        MessageBroker.Instance.Publish(party.Ai, new PartyBehaviorChangeAttempted(party));
    }

    private static string GetPartyName(PartyBase party, string fallback)
        => party?.LeaderHero?.Name?.ToString() ?? party?.Name?.ToString() ?? fallback;

    private static bool TryGetSessionPartyId(PlayerPartyInteractionSession session, NetPeer peer, out string partyId)
    {
        partyId = null;

        if (ReferenceEquals(peer, session.InitiatorPeer))
        {
            partyId = session.InitiatorPartyId;
            return true;
        }

        if (session.ResponderPeer != null && ReferenceEquals(peer, session.ResponderPeer))
        {
            partyId = session.ResponderPartyId;
            return true;
        }

        return false;
    }

    private void TryOpenHostileEncounter(NetworkPlayerPartyHostileEncounterStarted message)
    {
        if (!TryResolveHostileEncounter(message, out var attacker, out var defender, out var mapEvent))
            return;

        var localSide = GetLocalHostileEncounterSide(attacker, defender);
        if (localSide == BattleSideEnum.None)
            return;

        if (IsHostileEncounterClosed(message))
        {
            CloseLocalPlayerPartyEncounter(localSide == BattleSideEnum.Attacker ? attacker.MobileParty : defender.MobileParty);
            closedHostileEncounterPartyIds.Remove(message.AttackerPartyId);
            closedHostileEncounterPartyIds.Remove(message.DefenderPartyId);
            return;
        }

        if (IsCurrentLocalInteractionSession(message.SessionId))
            hostileEncounterSessionIds.Add(message.SessionId);

        OpenHostileEncounter(attacker, defender, mapEvent, localSide);
    }

    private bool IsHostileEncounterClosed(NetworkPlayerPartyHostileEncounterStarted message)
        => closedHostileEncounterPartyIds.Contains(message.AttackerPartyId) ||
           closedHostileEncounterPartyIds.Contains(message.DefenderPartyId);

    private bool TryResolveHostileEncounter(
        NetworkPlayerPartyHostileEncounterStarted message,
        out PartyBase attacker,
        out PartyBase defender,
        out MapEvent mapEvent)
    {
        attacker = null;
        defender = null;
        mapEvent = null;

        var resolvedAttacker = default(PartyBase);
        var resolvedDefender = default(PartyBase);
        var resolvedMapEvent = default(MapEvent);
        var deadline = DateTime.UtcNow + configuration.ObjectCreationTimeout;
        bool IsReady() =>
            objectManager.TryGetObject(message.AttackerPartyId, out resolvedAttacker) &&
            objectManager.TryGetObject(message.DefenderPartyId, out resolvedDefender) &&
            objectManager.TryGetObject(message.MapEventId, out resolvedMapEvent) &&
            IsHostileEncounterReady(resolvedMapEvent, resolvedAttacker, resolvedDefender);

        if (GameThread.WaitWhilePumping(IsReady, deadline))
        {
            attacker = resolvedAttacker;
            defender = resolvedDefender;
            mapEvent = resolvedMapEvent;
            return true;
        }
        Logger.Error(
            "Timed out waiting for player-party hostile encounter map event. SessionId={SessionId}, MapEventId={MapEventId}, AttackerPartyId={AttackerPartyId}, DefenderPartyId={DefenderPartyId}",
            message.SessionId,
            message.MapEventId,
            message.AttackerPartyId,
            message.DefenderPartyId);
        return false;
    }

    private static bool IsHostileEncounterReady(MapEvent mapEvent, PartyBase attacker, PartyBase defender)
    {
        if (mapEvent == null || attacker == null || defender == null)
            return false;

        if (attacker.MapEventSide == null || defender.MapEventSide == null)
            return false;

        return HasMapEventParty(attacker.MapEventSide, attacker) &&
               HasMapEventParty(defender.MapEventSide, defender) &&
               (mapEvent.AttackerSide == attacker.MapEventSide || mapEvent.DefenderSide == attacker.MapEventSide) &&
               (mapEvent.AttackerSide == defender.MapEventSide || mapEvent.DefenderSide == defender.MapEventSide);
    }

    private static bool HasMapEventParty(MapEventSide side, PartyBase party)
        => side?.Parties?.Any(p => p.Party == party) == true;

    private static void OpenHostileEncounter(PartyBase attacker, PartyBase defender, MapEvent mapEvent, BattleSideEnum localSide)
    {
        if (PlayerEncounter.Current != null && PlayerEncounter.Battle != mapEvent)
            PlayerEncounter.Finish(true);

        using (new AllowedThread())
        {
            EncounterManager.RestartPlayerEncounter(attacker, defender);
        }

        AssignLocalHostileEncounter(attacker, defender, mapEvent, localSide);
    }

    private static BattleSideEnum GetLocalHostileEncounterSide(PartyBase attacker, PartyBase defender)
    {
        if (attacker.MobileParty?.IsControlledByThisInstance() == true)
            return BattleSideEnum.Attacker;

        if (defender.MobileParty?.IsControlledByThisInstance() == true)
            return BattleSideEnum.Defender;

        return BattleSideEnum.None;
    }

    private static void AssignLocalHostileEncounter(PartyBase attacker, PartyBase defender, MapEvent mapEvent, BattleSideEnum localSide)
    {
        var encounter = PlayerEncounter.Current;
        if (encounter == null) return;
        if (localSide != BattleSideEnum.Attacker && localSide != BattleSideEnum.Defender) return;

        var localParty = localSide == BattleSideEnum.Attacker ? attacker : defender;
        localParty._mapEventSide = mapEvent.GetMapEventSide(localSide);
        encounter._attackerParty = attacker;
        encounter._defenderParty = defender;
        encounter._encounteredParty = localSide == BattleSideEnum.Attacker ? defender : attacker;
        encounter._mapEvent = mapEvent;
        encounter.PlayerSide = localSide;
        encounter.OpponentSide = localSide == BattleSideEnum.Attacker ? BattleSideEnum.Defender : BattleSideEnum.Attacker;
        encounter.IsJoinedBattle = true;

        GameMenu.SwitchToMenu("encounter");
    }

    private bool TryGetControlledSessionParty(
        NetworkPlayerPartyInteractionStarted message,
        out string myPartyId,
        out string otherPartyId)
    {
        myPartyId = null;
        otherPartyId = null;

        if (objectManager.TryGetObject<PartyBase>(message.InitiatorPartyId, out var initiatorParty) &&
            initiatorParty.MobileParty?.IsControlledByThisInstance() == true)
        {
            myPartyId = message.InitiatorPartyId;
            otherPartyId = message.ResponderPartyId;
            return true;
        }

        if (objectManager.TryGetObject<PartyBase>(message.ResponderPartyId, out var responderParty) &&
            responderParty.MobileParty?.IsControlledByThisInstance() == true)
        {
            myPartyId = message.ResponderPartyId;
            otherPartyId = message.InitiatorPartyId;
            return true;
        }

        return false;
    }

    private bool TryGetLocalInteractionParty(NetworkPlayerPartyInteractionEnded message, out PartyBase localParty)
    {
        localParty = null;

        if (objectManager.TryGetObject<PartyBase>(message.InitiatorPartyId, out var initiatorParty) &&
            initiatorParty.MobileParty?.IsControlledByThisInstance() == true)
        {
            localParty = initiatorParty;
            return true;
        }

        if (objectManager.TryGetObject<PartyBase>(message.ResponderPartyId, out var responderParty) &&
            responderParty.MobileParty?.IsControlledByThisInstance() == true)
        {
            localParty = responderParty;
            return true;
        }

        return false;
    }

    private static bool IsCurrentLocalInteractionSession(string sessionId)
        => PlayerPartyInteractionDialogState.SessionId == sessionId ||
           PlayerPartyTradeContext.SessionId == sessionId;

    private void EnsurePresentationTickListener()
    {
        if (presentationTickRegistered || Campaign.Current == null) return;

        CampaignEvents.TickEvent.AddNonSerializedListener(this, _ => TryOpenPendingMapConversation());
        presentationTickRegistered = true;
    }

    private void TryOpenPendingMapConversation()
    {
        if (!PlayerPartyInteractionDialogState.HasActiveState) return;

        var sessionId = PlayerPartyInteractionDialogState.SessionId;
        if (endedInteractionSessionIds.Contains(sessionId)) return;

        TryOpenMapConversation(sessionId, PlayerPartyInteractionDialogState.PartyId, PlayerPartyInteractionDialogState.OtherPartyId);
    }

    private static bool CanOpenMapConversation()
    {
        if (!(GameStateManager.Current?.ActiveState is MapState mapState) || mapState.AtMenu)
            return false;

        var mapScreen = MapScreen.Instance;
        return mapScreen != null && ScreenManager.TopScreen == mapScreen;
    }

    private static void CloseLocalPlayerPartyEncounter(MobileParty localParty)
    {
        if (PlayerEncounter.Current != null)
        {
            PlayerEncounter.LeaveEncounter = true;
            try
            {
                PlayerEncounter.Finish(true);
            }
            finally
            {
                Campaign.Current.PlayerEncounter = null;
            }
        }

        if (Campaign.Current?.CurrentMenuContext != null)
            GameMenu.ExitToLast();

        ClearLocalPartyEngageOrder(localParty);
    }

    private static void ClearLocalPartyEngageOrder(MobileParty party)
    {
        if (party?.Ai == null) return;
        if (party.MapEvent != null) return;

        party.SetMoveModeHold();
        MessageBroker.Instance.Publish(party.Ai, new PartyBehaviorChangeAttempted(party));
    }

    private bool TryGetLocalPartyId(out string partyId)
    {
        partyId = PlayerPartyInteractionDialogState.PartyId;
        return !string.IsNullOrEmpty(partyId);
    }

    private void TryOpenMapConversation(string sessionId, string myPartyId, string otherPartyId)
    {
        if (openedConversationSessionIds.Contains(sessionId)) return;
        if (endedInteractionSessionIds.Contains(sessionId)) return;
        if (!CanOpenMapConversation()) return;

        if (!objectManager.TryGetObject<PartyBase>(myPartyId, out var myParty)) return;
        if (!objectManager.TryGetObject<PartyBase>(otherPartyId, out var otherParty)) return;

        var myCharacter = myParty.LeaderHero?.CharacterObject;
        var otherCharacter = otherParty.LeaderHero?.CharacterObject;
        if (myCharacter == null || otherCharacter == null) return;

        var conversationManager = Campaign.Current?.ConversationManager;
        if (conversationManager == null) return;
        if (conversationManager.IsConversationInProgress) return;

        var playerData = new ConversationCharacterData(myCharacter, myParty, false, false, false, false, false, false);
        var otherData = new ConversationCharacterData(otherCharacter, otherParty, false, false, false, false, false, false);

        try
        {
            conversationManager.OpenMapConversation(playerData, otherData);
            PlayerPartyInteractionDialogState.RefreshConversation();
            openedConversationSessionIds.Add(sessionId);
        }
        catch (NullReferenceException ex)
        {
            Logger.Warning(ex, "Unable to open player party map conversation view");
        }
    }

    private void OpenTrade(NetworkPlayerPartyInteractionState state)
    {
        if (PlayerPartyTradeContext.IsActive) return;
        if (!objectManager.TryGetObject<PartyBase>(state.PartyId, out var myParty)) return;
        if (!objectManager.TryGetObject<PartyBase>(state.OtherPartyId, out var otherParty)) return;

        PlayerPartyTradeContext.Begin(state.SessionId, myParty);

        try
        {
            PlayerPartyTradeOverlay.Instance.Show(state.SessionId, state.OtherPlayerName);
        }
        catch (NullReferenceException ex)
        {
            Logger.Warning(ex, "Unable to open player party trade overlay");
        }

        try
        {
            ApplyTradeItemSnapshots(myParty, otherParty, state);
            OpenBarter(myParty, otherParty);
        }
        catch (NullReferenceException ex)
        {
            Logger.Warning(ex, "Unable to open player party barter screen");
        }
    }

    private void ApplyTradeItemSnapshots(PartyBase myParty, PartyBase otherParty, NetworkPlayerPartyInteractionState state)
    {
        ApplyTradeItemSnapshot(myParty?.ItemRoster, state.PartyItems);
        ApplyTradeItemSnapshot(otherParty?.ItemRoster, state.OtherPartyItems);
    }

    private void ApplyTradeItemSnapshot(ItemRoster itemRoster, ItemRosterElementData[] items)
    {
        if (itemRoster == null || items == null) return;

        using (new AllowedThread())
        {
            itemRoster.Clear();

            foreach (var item in items)
            {
                if (item.Amount <= 0) continue;
                if (!TryResolveEquipmentElement(item.ItemObjectData, out var equipmentElement)) continue;

                itemRoster.AddToCounts(equipmentElement, item.Amount);
            }
        }
    }

    private bool TryResolveEquipmentElement(ItemObjectData itemObjectData, out EquipmentElement equipmentElement)
    {
        equipmentElement = default;

        if (!objectManager.TryGetObject(itemObjectData.ItemObjectId, out ItemObject itemObject))
            return false;

        ItemModifier itemModifier = null;
        if (!itemObjectData.ItemModifierNull &&
            !objectManager.TryGetObject(itemObjectData.ItemModifierId, out itemModifier))
            return false;

        equipmentElement = new EquipmentElement(itemObject, itemModifier);
        return true;
    }

    private static void OpenBarter(PartyBase myParty, PartyBase otherParty)
    {
        var myHero = myParty?.LeaderHero;
        var otherHero = otherParty?.LeaderHero;
        if (myHero == null || otherHero == null) return;

        var barterData = new BarterData(myHero, otherHero, myParty, otherParty, null, 0, false);
        AddBarterGroups(barterData);
        AddPartyBarterables(barterData, myHero, otherHero, myParty, otherParty);
        AddPartyBarterables(barterData, otherHero, myHero, otherParty, myParty);

        BarterManager.Instance.BeginPlayerBarter(barterData);
    }

    private static void AddBarterGroups(BarterData barterData)
    {
        barterData.AddBarterGroup(new FiefBarterGroup());
        barterData.AddBarterGroup(new PrisonerBarterGroup());
        barterData.AddBarterGroup(new ItemBarterGroup());
        barterData.AddBarterGroup(new OtherBarterGroup());
        barterData.AddBarterGroup(new GoldBarterGroup());
    }

    private static void AddPartyBarterables(
        BarterData barterData,
        Hero ownerHero,
        Hero otherHero,
        PartyBase ownerParty,
        PartyBase otherParty)
    {
        barterData.AddBarterable<GoldBarterGroup>(new GoldBarterable(
            ownerHero,
            otherHero,
            ownerParty,
            otherParty,
            Math.Max(0, ownerHero.Gold)), false);

        AddPartyPeaceBarterable(barterData, ownerHero, otherHero, ownerParty, otherParty);
        AddPartyFiefBarterables(barterData, ownerHero, otherHero);
        AddPartyPrisonerBarterables(barterData, ownerHero, otherHero, ownerParty, otherParty);
        AddPartyItemBarterables(barterData, ownerHero, otherHero, ownerParty, otherParty);
        AddPartyTroopBarterables(barterData, ownerHero, otherHero, ownerParty, otherParty);
    }

    private static void AddPartyPeaceBarterable(
        BarterData barterData,
        Hero ownerHero,
        Hero otherHero,
        PartyBase ownerParty,
        PartyBase otherParty)
    {
        if (!PlayerPartyPeaceBarterable.CanOfferPeace(ownerParty, otherParty)) return;

        barterData.AddBarterable<OtherBarterGroup>(
            new PlayerPartyPeaceBarterable(ownerHero, otherHero, ownerParty, otherParty),
            false);
    }

    private static void AddPartyFiefBarterables(BarterData barterData, Hero ownerHero, Hero otherHero)
    {
        if (ownerHero?.Clan == null || otherHero == null) return;

        foreach (var fief in GetAllFiefs())
        {
            if (fief?.OwnerClan?.Leader != ownerHero) continue;
            if (fief.Settlement == null) continue;

            barterData.AddBarterable<FiefBarterGroup>(
                new FiefBarterable(fief.Settlement, ownerHero, otherHero),
                false);
        }
    }

    private static IEnumerable<Town> GetAllFiefs()
    {
        try
        {
            return Town.AllFiefs ?? Enumerable.Empty<Town>();
        }
        catch (NullReferenceException)
        {
            return Enumerable.Empty<Town>();
        }
    }

    private static void AddPartyPrisonerBarterables(
        BarterData barterData,
        Hero ownerHero,
        Hero otherHero,
        PartyBase ownerParty,
        PartyBase otherParty)
    {
        if (ownerParty == null) return;

        foreach (var prisoner in GetPrisonerHeroes(ownerParty))
        {
            if (prisoner?.HeroObject == null) continue;

            barterData.AddBarterable<PrisonerBarterGroup>(
                new TransferPrisonerBarterable(prisoner.HeroObject, ownerHero, ownerParty, otherHero, otherParty),
                false);
        }
    }

    private static IEnumerable<CharacterObject> GetPrisonerHeroes(PartyBase party)
    {
        try
        {
            return party.PrisonerHeroes ?? Enumerable.Empty<CharacterObject>();
        }
        catch (NullReferenceException)
        {
            return Enumerable.Empty<CharacterObject>();
        }
    }

    private static void AddPartyItemBarterables(
        BarterData barterData,
        Hero ownerHero,
        Hero otherHero,
        PartyBase ownerParty,
        PartyBase otherParty)
    {
        if (ownerParty?.ItemRoster == null) return;

        foreach (var item in ownerParty.ItemRoster)
        {
            if (item.Amount <= 0 || item.EquipmentElement.Item == null) continue;

            barterData.AddBarterable<ItemBarterGroup>(new ItemBarterable(
                ownerHero,
                otherHero,
                ownerParty,
                otherParty,
                item,
                Math.Max(0, item.EquipmentElement.Item.Value)), false);
        }
    }

    private static void AddPartyTroopBarterables(
        BarterData barterData,
        Hero ownerHero,
        Hero otherHero,
        PartyBase ownerParty,
        PartyBase otherParty)
    {
        if (ownerParty?.MemberRoster == null) return;

        foreach (var troop in ownerParty.MemberRoster.GetTroopRoster())
        {
            if (troop.Number <= 0 || troop.Character == null) continue;
            if (troop.Character == ownerHero?.CharacterObject) continue;

            barterData.AddBarterable<OtherBarterGroup>(
                new PlayerPartyTroopBarterable(ownerHero, otherHero, ownerParty, otherParty, troop),
                false);
        }
    }

    private ItemRosterElementData[] ResolveItemIds(IEnumerable<(ItemRosterElement, int)> items)
    {
        var result = new List<ItemRosterElementData>();

        foreach (var (item, count) in items)
        {
            if (!objectManager.TryGetId(item.EquipmentElement.Item, out var itemObjectId))
                continue;

            string itemModifierId = null;
            if (item.EquipmentElement.ItemModifier != null &&
                !objectManager.TryGetId(item.EquipmentElement.ItemModifier, out itemModifierId))
                continue;

            result.Add(new ItemRosterElementData(
                new ItemObjectData(itemObjectId, itemModifierId, item.EquipmentElement.ItemModifier == null),
                count));
        }

        return result.ToArray();
    }

    private ItemRosterElementData[] ResolvePartyItemIds(string partyId)
    {
        if (!objectManager.TryGetObject<PartyBase>(partyId, out var party))
            return Array.Empty<ItemRosterElementData>();

        return ResolveItemIds(GetItemRosterElements(party.ItemRoster));
    }

    private string[] ResolveSettlementIds(IEnumerable<Settlement> settlements)
    {
        var result = new List<string>();

        foreach (var settlement in settlements)
        {
            if (settlement == null || string.IsNullOrEmpty(settlement.StringId))
                continue;

            result.Add(settlement.StringId);
        }

        return result.ToArray();
    }

    private TroopRosterElementData[] ResolveCharacterIds(IEnumerable<(CharacterObject, int)> characters)
    {
        var result = new List<TroopRosterElementData>();

        foreach (var (character, count) in characters)
        {
            if (character == null || count <= 0) continue;

            if (!objectManager.TryGetIdWithLogging(character, out var characterId)) continue;

            result.Add(new TroopRosterElementData(characterId, count, 0, 0));
        }

        return result.ToArray();
    }

    private TroopRosterElementData[] ResolveTroopIds(IEnumerable<(TroopRosterElement, int)> troops)
    {
        var result = new List<TroopRosterElementData>();

        foreach (var (troop, count) in troops)
        {
            var character = troop.Character;
            if (character == null || count <= 0) continue;

            if (!objectManager.TryGetIdWithLogging(character, out var characterId))
                continue;

            result.Add(new TroopRosterElementData(characterId, count, troop.WoundedNumber, troop.Xp));
        }

        return result.ToArray();
    }

    private static IEnumerable<(ItemRosterElement, int)> GetItemRosterElements(ItemRoster itemRoster)
    {
        if (itemRoster == null) yield break;

        foreach (var item in itemRoster)
        {
            if (item.Amount <= 0) continue;

            yield return (item, item.Amount);
        }
    }

    private static IEnumerable<(ItemRosterElement, int)> GetOfferedBarterItems(BarterVM barterVM)
    {
        var result = new List<(ItemRosterElement, int)>();
        if (barterVM == null) return result;

        AddOfferedBarterItems(result, barterVM.LeftOfferList);
        AddOfferedBarterItems(result, barterVM.RightOfferList);

        return result;
    }

    private static int GetOfferedGold(BarterVM barterVM)
    {
        if (barterVM == null) return 0;

        return GetOfferedGold(barterVM.LeftOfferList) + GetOfferedGold(barterVM.RightOfferList);
    }

    private static int GetOfferedGold(IEnumerable<BarterItemVM> offeredItems)
    {
        if (offeredItems == null) return 0;

        var result = 0;
        foreach (var offeredItem in offeredItems)
        {
            if (!PlayerPartyTradeContext.CanOffer(offeredItem.Barterable)) continue;
            if (!(offeredItem.Barterable is GoldBarterable)) continue;

            result += Math.Max(0, offeredItem.Barterable.CurrentAmount);
        }

        return result;
    }

    private static bool HasOfferedPeace(BarterVM barterVM)
    {
        if (barterVM == null) return false;

        return HasOfferedPeace(barterVM.LeftOfferList) ||
               HasOfferedPeace(barterVM.RightOfferList);
    }

    private static bool HasOfferedPeace(IEnumerable<BarterItemVM> offeredItems)
    {
        if (offeredItems == null) return false;

        foreach (var offeredItem in offeredItems)
        {
            if (!PlayerPartyTradeContext.CanOffer(offeredItem.Barterable)) continue;
            if (offeredItem.Barterable is PlayerPartyPeaceBarterable) return true;
        }

        return false;
    }

    private static IEnumerable<Settlement> GetOfferedBarterFiefs(BarterVM barterVM)
    {
        var result = new List<Settlement>();
        if (barterVM == null) return result;

        AddOfferedBarterFiefs(result, barterVM.LeftOfferList);
        AddOfferedBarterFiefs(result, barterVM.RightOfferList);

        return result;
    }

    private static IEnumerable<(CharacterObject, int)> GetOfferedBarterPrisoners(BarterVM barterVM)
    {
        var result = new List<(CharacterObject, int)>();
        if (barterVM == null) return result;

        AddOfferedBarterPrisoners(result, barterVM.LeftOfferList);
        AddOfferedBarterPrisoners(result, barterVM.RightOfferList);

        return result;
    }

    private static IEnumerable<(TroopRosterElement, int)> GetOfferedBarterTroops(BarterVM barterVM)
    {
        var result = new List<(TroopRosterElement, int)>();
        if (barterVM == null) return result;

        AddOfferedBarterTroops(result, barterVM.LeftOfferList);
        AddOfferedBarterTroops(result, barterVM.RightOfferList);

        return result;
    }

    private static void AddOfferedBarterItems(List<(ItemRosterElement, int)> result, IEnumerable<BarterItemVM> offeredItems)
    {
        if (offeredItems == null) return;

        foreach (var offeredItem in offeredItems)
        {
            if (!PlayerPartyTradeContext.CanOffer(offeredItem.Barterable)) continue;
            if (!(offeredItem.Barterable is ItemBarterable itemBarterable)) continue;

            var count = Math.Min(offeredItem.Barterable.CurrentAmount, itemBarterable.ItemRosterElement.Amount);
            if (count <= 0) continue;

            result.Add((itemBarterable.ItemRosterElement, count));
        }
    }

    private static void AddOfferedBarterFiefs(List<Settlement> result, IEnumerable<BarterItemVM> offeredItems)
    {
        if (offeredItems == null) return;

        foreach (var offeredItem in offeredItems)
        {
            if (!PlayerPartyTradeContext.CanOffer(offeredItem.Barterable)) continue;
            if (!(offeredItem.Barterable is FiefBarterable fiefBarterable)) continue;

            result.Add(fiefBarterable.TargetSettlement);
        }
    }

    private static void AddOfferedBarterPrisoners(List<(CharacterObject, int)> result, IEnumerable<BarterItemVM> offeredItems)
    {
        if (offeredItems == null) return;

        foreach (var offeredItem in offeredItems)
        {
            if (!PlayerPartyTradeContext.CanOffer(offeredItem.Barterable)) continue;
            if (!(offeredItem.Barterable is TransferPrisonerBarterable transferPrisonerBarterable)) continue;
            if (!(PrisonerCharacterField?.GetValue(transferPrisonerBarterable) is Hero prisonerHero)) continue;

            result.Add((prisonerHero.CharacterObject, 1));
        }
    }

    private static void AddOfferedBarterTroops(List<(TroopRosterElement, int)> result, IEnumerable<BarterItemVM> offeredItems)
    {
        if (offeredItems == null) return;

        foreach (var offeredItem in offeredItems)
        {
            if (!PlayerPartyTradeContext.CanOffer(offeredItem.Barterable)) continue;
            if (!(offeredItem.Barterable is PlayerPartyTroopBarterable troopBarterable)) continue;

            var count = Math.Min(offeredItem.Barterable.CurrentAmount, troopBarterable.TroopRosterElement.Number);
            if (count <= 0) continue;

            result.Add((troopBarterable.TroopRosterElement, count));
        }
    }
}
