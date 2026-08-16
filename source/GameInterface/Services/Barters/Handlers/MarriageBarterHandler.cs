using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Common.Network.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Barters.Messages;
using GameInterface.Services.Barters.Patches;
using GameInterface.Services.Heroes.Extensions;
using GameInterface.Services.Heroes.Messages.RomanceFlow;
using GameInterface.Services.Heroes.RomanceFlow;
using GameInterface.Services.Locations.Conversations;
using GameInterface.Services.MapEvents;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.BarterSystem;
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Settlements.Locations;
using TaleWorlds.Core;
using TaleWorlds.Library;
using Romance = TaleWorlds.CampaignSystem.Romance;

namespace GameInterface.Services.Barters.Handlers;

internal sealed class MarriageBarterHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<MarriageBarterHandler>();
    // A lease is intentionally short: the server validates the live conversation again at commit,
    // but never lets a client keep a standing authority to marry after walking away.
    private static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(1);

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IRomanceAuthority romanceAuthority;
    private readonly ConversationPartyTracker conversationPartyTracker;
    private readonly LocationConversationTracker locationConversationTracker;
    private readonly IBarterClientPresentation barterClientPresentation;
    private readonly ISendCoalescer sendCoalescer;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<MarriageBarterAuthorizeIntent, NetworkMarriageBarterAuthorizationResult> authorizeRoute;
    private readonly IAuthorityRouteHandle<MarriageBarterCommitIntent, NetworkMarriageBarterResult> commitRoute;
    // Leases belong to a transport peer + campaign session, not to a client-provided request id.
    // Consumed and expired entries are deliberately retained as tombstones for the session.
    private readonly Dictionary<string, MarriageAuthorization> marriageLeases =
        new Dictionary<string, MarriageAuthorization>(StringComparer.Ordinal);
    private readonly Dictionary<string, NetworkMarriageBarterDelta> receivedDeltas =
        new Dictionary<string, NetworkMarriageBarterDelta>(StringComparer.Ordinal);
    private static MarriageBarterHandler instance;

    public MarriageBarterHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IRomanceAuthority romanceAuthority,
        ConversationPartyTracker conversationPartyTracker,
        LocationConversationTracker locationConversationTracker,
        IBarterClientPresentation barterClientPresentation,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter,
        ISendCoalescer sendCoalescer = null)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.romanceAuthority = romanceAuthority;
        this.conversationPartyTracker = conversationPartyTracker;
        this.locationConversationTracker = locationConversationTracker;
        this.barterClientPresentation = barterClientPresentation;
        this.sendCoalescer = sendCoalescer;
        this.configAuthority = configAuthority;
        instance = this;

        authorizeRoute = authorityRequestRouter.Register(
            AuthorityRoute<MarriageBarterAuthorizeIntent, NetworkAuthorizeMarriageBarter,
                NetworkMarriageBarterAuthorizationResult>.Define(
                "barter.marriage.authorize", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkAuthorizeMarriageBarter(intent.ClientRequestId,
                    intent.CounterpartyHeroId, intent.Context, intent.ContextId, intent.HeroBeingProposedToId,
                    intent.ProposingHeroId, header), request => request.Header, result => result.Header,
                ValidateAuthorizeWireShape, BuildAuthorizeKey, ValidateHeader, ExecuteAuthorization,
                CreateAuthorizationTerminal, _ => AuthorityCommitProbeResult.Applied, _ => { },
                PresentAuthorizationOutcome, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: (request, result) =>
                    request.RequestId == result.RequestId));
        commitRoute = authorityRequestRouter.Register(
            AuthorityRoute<MarriageBarterCommitIntent, NetworkRequestMarriageBarter,
                NetworkMarriageBarterResult>.Define(
                "barter.marriage.commit", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestMarriageBarter(intent.CounterpartyHeroId, intent.Context,
                    intent.ContextId, intent.HeroBeingProposedToId, intent.ProposingHeroId, intent.Terms,
                    intent.ClientRequestId, intent.LeaseId, header), request => request.Header, result => result.Header,
                ValidateCommitWireShape, BuildCommitKey, ValidateHeader, ExecuteCommit, CreateCommitTerminal,
                ProbeClientCommit, _ => { }, PresentCommitOutcome, configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.CampaignMutation, failClosedOnApplyFailure: true,
                isExpectedClientResult: IsExpectedCommitResult));

        messageBroker.Subscribe<NetworkMarriageBarterDelta>(HandleDelta);
        messageBroker.Subscribe<PlayerDisconnected>(HandlePlayerDisconnected);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkMarriageBarterDelta>(HandleDelta);
        messageBroker.Unsubscribe<PlayerDisconnected>(HandlePlayerDisconnected);
        authorizeRoute.Dispose();
        commitRoute.Dispose();
        marriageLeases.Clear();
        receivedDeltas.Clear();
        if (instance == this) instance = null;
        MarriageBarterPatch.ClearPendingRequest();
    }

    private void HandlePlayerDisconnected(MessagePayload<PlayerDisconnected> payload)
    {
        if (!ModInformation.IsServer) return;

        var peer = payload.What.PlayerId;
        GameThread.RunSafe(
            () => TombstonePeerLeases(peer),
            context: nameof(PlayerDisconnected));
    }

    internal static bool TryAuthorize(MarriageBarterAuthorizeIntent intent)
    {
        if (instance == null || ModInformation.IsServer) return false;
        instance.authorizeRoute.Submit(intent);
        return true;
    }

    internal static bool TryCommit(MarriageBarterCommitIntent intent)
    {
        if (instance == null || ModInformation.IsServer) return false;
        instance.commitRoute.Submit(intent);
        return true;
    }

    private AuthorityServerReply<NetworkMarriageBarterResult> ExecuteCommit(
        AuthorityServerContext context, NetworkRequestMarriageBarter request)
    {
        if (!TryResolveRequester(context.Peer, out _, out var player, out var playerHero, out var requesterReason))
            return Reject(context.Header, request, 0, requesterReason);

        using var playerContext = new BarterPlayerContext(
            playerHero,
            GetPlayerParty(player, playerHero)?.MobileParty);
        var mutationStarted = false;
        try
        {
            if (!TryResolveMarriageContext(
                    context.Peer,
                    player,
                    playerHero,
                    request.CounterpartyHeroId,
                    request.Context,
                    request.ContextId,
                    request.HeroBeingProposedToId,
                    request.ProposingHeroId,
                    requireActiveConversation: true,
                    out var counterpartyHero,
                    out var heroBeingProposedTo,
                    out var proposingHero,
                    out var reason))
            {
                return Reject(context.Header, request, playerHero.Gold, reason);
            }

            if (!TryBuildMarriageBarter(
                    player,
                    playerHero,
                    counterpartyHero,
                    heroBeingProposedTo,
                    proposingHero,
                    request.Terms,
                    out var barterData,
                    out reason))
            {
                return Reject(context.Header, request, playerHero.Gold, reason);
            }

            var barterManager = BarterManager.Instance;
            if (barterManager == null)
            {
                return Reject(context.Header, request, playerHero.Gold, "The marriage offer is not acceptable.");
            }

            var offerValue = barterManager.GetOfferValueForFaction(barterData, counterpartyHero.Clan);
            if (offerValue < -0.01f)
            {
                return Reject(context.Header, request, playerHero.Gold, "The marriage offer is not acceptable.");
            }
            // This is the final reversible boundary. The lease binds the exact server-authorized
            // participants and session; terms above were rebuilt and valued from live server state.
            if (!TryConsumeLease(context, request))
                return Reject(context.Header, request, playerHero.Gold, "The marriage barter is no longer authorized.");
            if (!CanPublishCanonicalDelta(playerHero, counterpartyHero, heroBeingProposedTo, proposingHero, out reason))
                return Reject(context.Header, request, playerHero.Gold, reason);

            mutationStarted = true;
            var offeredBarterables = barterData.GetOfferedBarterables();
            foreach (var barterable in offeredBarterables) barterable.Apply();
            CampaignEventDispatcher.Instance.OnBarterAccepted(playerHero, barterData.OtherHero, offeredBarterables);
            ApplyOverpayRelationBonus(playerHero, barterData.OtherHero, MathF.Max(0f, offerValue));
            if (heroBeingProposedTo.Spouse != proposingHero || proposingHero.Spouse != heroBeingProposedTo)
                return IsolateAfterMutation(context, request, "marriage-postcondition-missing");

            FlushHeroGold(playerHero);
            FlushHeroGold(counterpartyHero);
            FlushHeroGold(heroBeingProposedTo);
            FlushHeroGold(proposingHero);
            if (!PublishCanonicalDelta(context.Header, request, playerHero, counterpartyHero, heroBeingProposedTo, proposingHero))
                return IsolateAfterMutation(context, request, "marriage-publication-failed");
            return Accept(context.Header, request, playerHero.Gold);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed to apply an authoritative marriage barter");
            if (mutationStarted) return IsolateAfterMutation(context, request, "marriage-ambiguous", exception);
            return Reject(context.Header, request, playerHero.Gold, "The server could not process the marriage offer.");
        }
    }

    private AuthorityServerReply<NetworkMarriageBarterAuthorizationResult> ExecuteAuthorization(
        AuthorityServerContext context, NetworkAuthorizeMarriageBarter request)
    {
        if (!TryResolveRequester(context.Peer, out _, out var player, out var playerHero, out var reason))
            return RejectAuthorization(context.Header, request, reason);

        if (!TryResolveMarriageContext(
                context.Peer,
                player,
                playerHero,
                request.CounterpartyHeroId,
                request.Context,
                request.ContextId,
                request.HeroBeingProposedToId,
                request.ProposingHeroId,
                requireActiveConversation: true,
                out _,
                out _,
                out _,
                out reason))
        {
            return RejectAuthorization(context.Header, request, reason);
        }

        TombstonePeerLeases(context.Peer);
        var leaseId = Guid.NewGuid().ToString("N");
        marriageLeases[leaseId] = new MarriageAuthorization(
            leaseId,
            context.Peer,
            context.Header.SessionId,
            request.CounterpartyHeroId,
            request.Context,
            request.ContextId,
            request.HeroBeingProposedToId,
            request.ProposingHeroId,
            DateTime.UtcNow.Add(AuthorizationLifetime));
        return new AuthorityServerReply<NetworkMarriageBarterAuthorizationResult>(
            new NetworkMarriageBarterAuthorizationResult(request.RequestId, leaseId,
                new AuthorityResultHeader(context.Header.SessionId, context.Header.RequestId,
                    AuthorityResultStatus.Accepted, context.Header.ExpectedRevision, null)), statePublished: true);
    }

    private bool TryConsumeLease(AuthorityServerContext context, NetworkRequestMarriageBarter request)
    {
        if (string.IsNullOrEmpty(request.LeaseId) ||
            !marriageLeases.TryGetValue(request.LeaseId, out var authorization))
            return false;

        if (authorization.ExpiresAtUtc <= DateTime.UtcNow || authorization.Consumed || authorization.Owner != context.Peer ||
            !string.Equals(authorization.SessionId, context.Header.SessionId, StringComparison.Ordinal))
        {
            authorization.Tombstone();
            return false;
        }

        if (!authorization.Matches(request))
            return false;

        authorization.Consume();
        return true;
    }

    private bool TryResolveRequester(
        object sender,
        out NetPeer peer,
        out Player player,
        out Hero playerHero,
        out string reason)
    {
        peer = sender as NetPeer;
        player = null;
        playerHero = null;
        reason = null;

        if (peer == null)
        {
            Logger.Error("Received marriage barter request without an originating peer");
            reason = "The server could not identify the marriage requester.";
            return false;
        }

        if (!playerManager.TryGetPlayer(peer, out player))
        {
            Logger.Warning("Received marriage barter request from unregistered peer {Peer}", peer.Id);
            reason = "The server could not identify the marriage requester.";
            return false;
        }

        if (!TryResolveHero(player.HeroId, out playerHero))
        {
            Logger.Warning("Unable to resolve player hero {HeroId} for peer {Peer}", player.HeroId, peer.Id);
            reason = "The server could not identify the marriage requester.";
            return false;
        }

        return true;
    }

    private bool TryResolveHero(string heroId, out Hero hero)
    {
        hero = null;
        return !string.IsNullOrEmpty(heroId) && objectManager.TryGetObject(heroId, out hero);
    }

    private bool TryResolveMarriageContext(
        NetPeer peer,
        Player player,
        Hero playerHero,
        string counterpartyHeroId,
        int contextValue,
        string contextId,
        string heroBeingProposedToId,
        string proposingHeroId,
        bool requireActiveConversation,
        out Hero counterpartyHero,
        out Hero heroBeingProposedTo,
        out Hero proposingHero,
        out string reason)
    {
        counterpartyHero = null;
        heroBeingProposedTo = null;
        proposingHero = null;
        reason = null;

        if (!Enum.IsDefined(typeof(MarriageConversationContext), contextValue) ||
            !TryResolveHero(counterpartyHeroId, out counterpartyHero) ||
            !TryResolveHero(heroBeingProposedToId, out heroBeingProposedTo) ||
            !TryResolveHero(proposingHeroId, out proposingHero))
        {
            reason = "The server could not identify the marriage participants.";
            return false;
        }

        var context = (MarriageConversationContext)contextValue;
        if ((requireActiveConversation || context == MarriageConversationContext.Settlement) &&
            !IsActiveConversation(peer, player, counterpartyHero, context, contextId))
        {
            reason = "The marriage conversation is no longer active.";
            return false;
        }

        if (heroBeingProposedTo == proposingHero ||
            !heroBeingProposedTo.IsAlive ||
            !proposingHero.IsAlive ||
            heroBeingProposedTo.Spouse != null ||
            proposingHero.Spouse != null ||
            heroBeingProposedTo.Clan == null ||
            proposingHero.Clan == null ||
            heroBeingProposedTo.Clan == proposingHero.Clan ||
            counterpartyHero.IsPlayerHero() ||
            counterpartyHero.IsPrisoner ||
            counterpartyHero.Clan?.Leader != counterpartyHero)
        {
            reason = "Those heroes are no longer eligible for marriage.";
            return false;
        }

        var playerClan = playerHero.Clan;
        var counterpartyClan = counterpartyHero.Clan;
        var heroBeingIsPlayerClan = heroBeingProposedTo.Clan == playerClan;
        var proposingIsPlayerClan = proposingHero.Clan == playerClan;
        var heroBeingIsCounterpartyClan = heroBeingProposedTo.Clan == counterpartyClan;
        var proposingIsCounterpartyClan = proposingHero.Clan == counterpartyClan;
        if (playerClan == null || counterpartyClan == null || playerClan == counterpartyClan ||
            heroBeingIsPlayerClan == proposingIsPlayerClan ||
            heroBeingIsCounterpartyClan == proposingIsCounterpartyClan)
        {
            reason = "The proposed marriage does not match the negotiating clans.";
            return false;
        }

        var romanticLevel = Romance.GetRomanticLevel(heroBeingProposedTo, proposingHero);
        if (proposingHero == playerHero ||
            (heroBeingProposedTo == playerHero &&
             romanticLevel != Romance.RomanceLevelEnum.MatchMadeByFamily))
        {
            var prospectiveSpouse = proposingHero == playerHero ? heroBeingProposedTo : proposingHero;
            if (!romanceAuthority.TryValidateMarriage(playerHero, prospectiveSpouse, out reason))
                return false;
        }
        else
        {
            if (romanticLevel != Romance.RomanceLevelEnum.MatchMadeByFamily)
            {
                reason = "The arranged marriage has not been agreed by both clans.";
                return false;
            }

            if (heroBeingProposedTo != playerHero &&
                (playerClan.Leader != playerHero ||
                 heroBeingProposedTo.IsPlayerHero() ||
                 proposingHero.IsPlayerHero()))
            {
                reason = "The player cannot authorize that arranged marriage.";
                return false;
            }
        }

        if (FactionManager.IsAtWarAgainstFaction(playerClan.MapFaction, counterpartyClan.MapFaction) ||
            !Campaign.Current.Models.MarriageModel.IsCoupleSuitableForMarriage(heroBeingProposedTo, proposingHero))
        {
            reason = "Those heroes are no longer eligible for marriage.";
            return false;
        }

        return true;
    }

    private bool IsActiveConversation(
        NetPeer peer,
        Player player,
        Hero counterpartyHero,
        MarriageConversationContext context,
        string contextId)
    {
        if (string.IsNullOrEmpty(contextId)) return false;

        if (context == MarriageConversationContext.Location)
        {
            if (counterpartyHero.CharacterObject == null ||
                !objectManager.TryGetId(counterpartyHero.CharacterObject, out var characterId))
                return false;

            if (locationConversationTracker.TryGetEngagement(peer, out var npcKey))
                return npcKey == LocationConversationTracker.ComposeKey(contextId, characterId);

            // Menu-initiated talks (settlement menu "Talk", keep shortcuts) start the conversation
            // WITHOUT the agent-interaction acquire step, so no engagement is ever tracked - this
            // gate rejected every such marriage proposal ("The marriage conversation is no longer
            // active", 2026-08-13 live loops). With no engagement to compare, verify presence
            // directly: the player's party and the counterparty must be in the same settlement,
            // and the claimed location must belong to that settlement's location complex.
            return !string.IsNullOrEmpty(player.MobilePartyId) &&
                   objectManager.TryGetObject(contextId, out Location claimedLocation) &&
                   objectManager.TryGetObject(player.MobilePartyId, out MobileParty locationPlayerParty) &&
                   locationPlayerParty.IsActive &&
                   locationPlayerParty.CurrentSettlement != null &&
                   counterpartyHero.CurrentSettlement == locationPlayerParty.CurrentSettlement &&
                   locationPlayerParty.CurrentSettlement.LocationComplex?.GetListOfLocations()
                       ?.Contains(claimedLocation) == true;
        }

        if (context == MarriageConversationContext.Settlement)
        {
            return !string.IsNullOrEmpty(player.MobilePartyId) &&
                   objectManager.TryGetObject(contextId, out Settlement settlement) &&
                   objectManager.TryGetObject(player.MobilePartyId, out MobileParty settlementPlayerParty) &&
                   settlementPlayerParty.IsActive &&
                   settlementPlayerParty.CurrentSettlement == settlement &&
                   counterpartyHero.CurrentSettlement == settlement;
        }

        if (string.IsNullOrEmpty(player.MobilePartyId) ||
            !objectManager.TryGetObject(contextId, out PartyBase counterpartyParty) ||
            counterpartyParty.LeaderHero != counterpartyHero ||
            !objectManager.TryGetObject(player.MobilePartyId, out MobileParty playerParty) ||
            !objectManager.TryGetId(playerParty.Party, out var playerPartyId) ||
            !conversationPartyTracker.TryGetEngagement(peer, out var engagement))
        {
            return false;
        }

        return engagement.PartyId == contextId && engagement.EngagerPartyId == playerPartyId;
    }

    private bool TryBuildMarriageBarter(
        Player player,
        Hero playerHero,
        Hero counterpartyHero,
        Hero heroBeingProposedTo,
        Hero proposingHero,
        MarriageBarterTerm[] terms,
        out BarterData barterData,
        out string reason)
    {
        barterData = null;
        reason = null;

        var playerParty = GetPlayerParty(player, playerHero);
        var counterpartyParty = counterpartyHero.PartyBelongedTo?.Party;
        var romanticState = Romance.GetRomanticState(heroBeingProposedTo, proposingHero);
        var persuasionCostReduction = heroBeingProposedTo == playerHero || proposingHero == playerHero
            ? (int)(romanticState?.ScoreFromPersuasion ?? 0f)
            : 0;
        var marriageBarterable = new MarriageBarterable(
            playerHero,
            playerParty,
            heroBeingProposedTo,
            proposingHero);

        barterData = new BarterData(
            playerHero,
            counterpartyHero,
            playerParty,
            counterpartyParty,
            (barterable, data, _) => BarterManager.Instance.InitializeMarriageBarterContext(
                barterable,
                data,
                new Tuple<Hero, Hero>(heroBeingProposedTo, proposingHero)),
            persuasionCostReduction,
            false);

        barterData.AddBarterGroup(new DefaultsBarterGroup());
        marriageBarterable.SetIsOffered(true);
        barterData.AddBarterable<OtherBarterGroup>(marriageBarterable, true);
        marriageBarterable.SetIsOffered(true);
        CampaignEventDispatcher.Instance.OnBarterablesRequested(barterData);

        return TryApplyRequestedTerms(
            playerHero,
            counterpartyHero,
            barterData,
            terms ?? Array.Empty<MarriageBarterTerm>(),
            out reason);
    }

    private PartyBase GetPlayerParty(Player player, Hero playerHero)
    {
        if (!string.IsNullOrEmpty(player.MobilePartyId) &&
            objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out var mobileParty))
        {
            return mobileParty.Party;
        }

        return playerHero.PartyBelongedTo?.Party;
    }

    private bool TryApplyRequestedTerms(
        Hero playerHero,
        Hero counterpartyHero,
        BarterData barterData,
        MarriageBarterTerm[] terms,
        out string reason)
    {
        var usedBarterables = new HashSet<Barterable>();
        foreach (var term in terms)
        {
            if (!Enum.IsDefined(typeof(MarriageBarterTermType), term.Type) || term.Amount <= 0)
            {
                reason = "The marriage offer contains an invalid term.";
                return false;
            }

            if (string.IsNullOrEmpty(term.OwnerHeroId))
            {
                reason = "The marriage offer does not identify the owner of a barter term.";
                return false;
            }

            var termType = (MarriageBarterTermType)term.Type;
            var barterable = barterData.GetBarterables().FirstOrDefault(candidate =>
                (candidate.OriginalOwner == playerHero || candidate.OriginalOwner == counterpartyHero) &&
                objectManager.TryGetId(candidate.OriginalOwner, out var ownerHeroId) &&
                ownerHeroId == term.OwnerHeroId &&
                MatchesTerm(candidate, termType, term));

            if (barterable == null || !usedBarterables.Add(barterable) || term.Amount > barterable.MaxAmount)
            {
                reason = "The marriage offer no longer matches the server's available barter terms.";
                return false;
            }

            barterable.CurrentAmount = term.Amount;
            barterable.SetIsOffered(true);
        }

        reason = null;
        return true;
    }

    private bool MatchesTerm(Barterable barterable, MarriageBarterTermType type, MarriageBarterTerm term)
    {
        switch (type)
        {
            case MarriageBarterTermType.Gold:
                return barterable is GoldBarterable;
            case MarriageBarterTermType.Item:
                return barterable is ItemBarterable itemBarterable && MatchesItem(itemBarterable, term);
            case MarriageBarterTermType.Fief:
                return barterable is FiefBarterable fiefBarterable &&
                       objectManager.TryGetId(fiefBarterable.TargetSettlement, out var settlementId) &&
                       settlementId == term.ObjectId;
            case MarriageBarterTermType.Prisoner:
                return barterable is TransferPrisonerBarterable prisonerBarterable &&
                       MatchesPrisoner(prisonerBarterable, term);
            default:
                return false;
        }
    }

    private bool MatchesItem(ItemBarterable barterable, MarriageBarterTerm term)
    {
        var equipmentElement = barterable.ItemRosterElement.EquipmentElement;
        if (!objectManager.TryGetId(equipmentElement.Item, out var itemId) || itemId != term.ObjectId)
            return false;

        var modifier = equipmentElement.ItemModifier;
        if ((modifier == null) != term.ItemModifierNull) return false;
        if (modifier == null) return true;

        return objectManager.TryGetId(modifier, out var modifierId) && modifierId == term.ItemModifierId;
    }

    private bool MatchesPrisoner(TransferPrisonerBarterable barterable, MarriageBarterTerm term)
    {
        var prisoner = barterable._prisonerCharacter;
        return prisoner?.CharacterObject != null &&
               objectManager.TryGetId(prisoner.CharacterObject, out var characterId) &&
               characterId == term.ObjectId;
    }

    private static void ApplyOverpayRelationBonus(Hero playerHero, Hero otherHero, float overpayAmount)
    {
        var campaign = Campaign.Current;
        if (otherHero == null ||
            overpayAmount <= 0f ||
            playerHero.MapFaction == null ||
            otherHero.MapFaction == null ||
            otherHero.MapFaction.IsAtWarWith(playerHero.MapFaction) ||
            campaign == null)
        {
            return;
        }

        var relation = otherHero.GetRelation(playerHero);
        var maximumRelation = MathF.Clamp(relation + 3, -100f, 100f);
        var relationBonus = 0f;
        for (var currentRelation = relation; currentRelation < maximumRelation; currentRelation++)
        {
            var cost = 1000 + ((100 * currentRelation) * currentRelation);
            if (overpayAmount >= cost)
            {
                overpayAmount -= cost;
                relationBonus += 1f;
                continue;
            }

            if (MBRandom.RandomFloat <= overpayAmount / cost)
                relationBonus += 1f;
            break;
        }

        if (playerHero.GetPerkValue(DefaultPerks.Charm.Tribute))
            relationBonus *= 1f + DefaultPerks.Charm.Tribute.PrimaryBonus;

        var roundedBonus = (int)MathF.Ceiling(relationBonus);
        if (roundedBonus > 0)
            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(playerHero, otherHero, roundedBonus);
    }

    private void FlushHeroGold(Hero hero)
    {
        if (sendCoalescer == null || hero == null || !objectManager.TryGetId(hero, out var heroId)) return;
        sendCoalescer.FlushInstance(heroId, network);
    }

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
        return header.ExpectedRevision == current.Revision
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
    }

    private static string ValidateAuthorizeWireShape(NetworkAuthorizeMarriageBarter request) =>
        ValidateParticipants(request.RequestId, request.CounterpartyHeroId, request.Context, request.ContextId,
            request.HeroBeingProposedToId, request.ProposingHeroId);

    private static string ValidateCommitWireShape(NetworkRequestMarriageBarter request)
    {
        var failure = ValidateParticipants(request.RequestId, request.CounterpartyHeroId, request.Context,
            request.ContextId, request.HeroBeingProposedToId, request.ProposingHeroId);
        if (failure != null || string.IsNullOrWhiteSpace(request.LeaseId) || request.LeaseId.Length > 64 ||
            request.Terms == null || request.Terms.Length > 128) return failure ?? "invalid-marriage-commit";
        return request.Terms.Any(term => !Enum.IsDefined(typeof(MarriageBarterTermType), term.Type) || term.Amount <= 0 ||
            string.IsNullOrWhiteSpace(term.OwnerHeroId) || term.OwnerHeroId.Length > 256 ||
            (term.ObjectId?.Length ?? 0) > 256 || (term.ItemModifierId?.Length ?? 0) > 256)
            ? "invalid-marriage-barter-term" : null;
    }

    private static string ValidateParticipants(string requestId, string counterpartyHeroId, int context, string contextId,
        string heroBeingProposedToId, string proposingHeroId)
    {
        return string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128 ||
            string.IsNullOrWhiteSpace(counterpartyHeroId) || counterpartyHeroId.Length > 256 ||
            string.IsNullOrWhiteSpace(contextId) || contextId.Length > 256 ||
            string.IsNullOrWhiteSpace(heroBeingProposedToId) || heroBeingProposedToId.Length > 256 ||
            string.IsNullOrWhiteSpace(proposingHeroId) || proposingHeroId.Length > 256 ||
            !Enum.IsDefined(typeof(MarriageConversationContext), context) ? "invalid-marriage-barter" : null;
    }

    private static string BuildAuthorizeKey(NetworkAuthorizeMarriageBarter request) => string.Concat(
        request.RequestId, ":", request.CounterpartyHeroId, ":", request.Context, ":", request.ContextId, ":",
        request.HeroBeingProposedToId, ":", request.ProposingHeroId);

    private static string BuildCommitKey(NetworkRequestMarriageBarter request) => string.Concat(request.LeaseId, ":",
        BuildAuthorizeKey(new NetworkAuthorizeMarriageBarter(request.RequestId, request.CounterpartyHeroId,
            (MarriageConversationContext)request.Context, request.ContextId, request.HeroBeingProposedToId,
            request.ProposingHeroId)), ":", string.Join("|", request.Terms.OrderBy(term => term.Type)
            .ThenBy(term => term.OwnerHeroId, StringComparer.Ordinal).ThenBy(term => term.ObjectId, StringComparer.Ordinal)
            .ThenBy(term => term.ItemModifierId, StringComparer.Ordinal).ThenBy(term => term.Amount)
            .Select(term => string.Concat(term.Type, ":", term.OwnerHeroId, ":", term.ObjectId, ":", term.ItemModifierId,
                ":", term.ItemModifierNull, ":", term.Amount))));

    private static NetworkMarriageBarterAuthorizationResult CreateAuthorizationTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, null, new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private static NetworkMarriageBarterResult CreateCommitTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, null, null, new AuthorityResultHeader(header.SessionId, header.RequestId, status,
            header.ExpectedRevision, reason), 0, null);

    private static AuthorityServerReply<NetworkMarriageBarterAuthorizationResult> RejectAuthorization(
        AuthorityRequestHeader header, NetworkAuthorizeMarriageBarter request, string reason) =>
        new(new NetworkMarriageBarterAuthorizationResult(request.RequestId, null,
            new AuthorityResultHeader(header.SessionId, header.RequestId, AuthorityResultStatus.Rejected,
                header.ExpectedRevision, reason)), false);

    private static AuthorityServerReply<NetworkMarriageBarterResult> Accept(AuthorityRequestHeader header,
        NetworkRequestMarriageBarter request, int playerGold) => new(new NetworkMarriageBarterResult(
            request.CounterpartyHeroId, request.HeroBeingProposedToId, request.ProposingHeroId,
            new AuthorityResultHeader(header.SessionId, header.RequestId, AuthorityResultStatus.Accepted,
                header.ExpectedRevision, null), playerGold, request.RequestId), true);

    private static AuthorityServerReply<NetworkMarriageBarterResult> Reject(AuthorityRequestHeader header,
        NetworkRequestMarriageBarter request, int playerGold, string reason) => new(new NetworkMarriageBarterResult(
            request.CounterpartyHeroId, request.HeroBeingProposedToId, request.ProposingHeroId,
            new AuthorityResultHeader(header.SessionId, header.RequestId, AuthorityResultStatus.Rejected,
                header.ExpectedRevision, reason), playerGold, request.RequestId), false);

    private void HandleDelta(MessagePayload<NetworkMarriageBarterDelta> payload)
    {
        if (ModInformation.IsServer || !configAuthority.IsTrustedServer(payload.Who) ||
            !configAuthority.TryGetCurrent(out ModConfigSnapshot config)) return;
        var delta = payload.What;
        if (delta.AuthorityRequestId <= 0 || delta.CommittedRevision != config.Revision ||
            !string.Equals(delta.SessionId, config.SessionId, StringComparison.Ordinal)) return;
        receivedDeltas[DeltaKey(delta.SessionId, delta.AuthorityRequestId, delta.CommittedRevision)] = delta;
    }

    private AuthorityCommitProbeResult ProbeClientCommit(NetworkMarriageBarterResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted || !configAuthority.TryGetCurrent(out ModConfigSnapshot config) ||
            config.Revision != result.Header.CommittedRevision || config.SessionId != result.Header.SessionId ||
            !receivedDeltas.TryGetValue(DeltaKey(result.Header.SessionId, result.Header.RequestId,
                result.Header.CommittedRevision), out var delta) ||
            !TryResolveHero(delta.PlayerHeroId, out var playerHero) || !TryResolveHero(delta.CounterpartyHeroId, out var counterpartyHero) ||
            !TryResolveHero(delta.HeroBeingProposedToId, out var proposedHero) || !TryResolveHero(delta.ProposingHeroId, out var proposingHero) ||
            playerHero.Gold != delta.PlayerGold || counterpartyHero.Gold != delta.CounterpartyGold ||
            proposedHero.Gold != delta.HeroBeingProposedToGold || proposingHero.Gold != delta.ProposingHeroGold ||
            proposedHero.Spouse != proposingHero || proposingHero.Spouse != proposedHero ||
            (int)Romance.GetRomanticLevel(proposedHero, proposingHero) != delta.RomanceLevel)
            return AuthorityCommitProbeResult.Pending;
        return AuthorityCommitProbeResult.Applied;
    }

    private static bool IsExpectedCommitResult(NetworkRequestMarriageBarter request, NetworkMarriageBarterResult result) =>
        result.Header.RequestId == request.Header.RequestId && result.Header.CommittedRevision == request.Header.ExpectedRevision &&
        string.Equals(result.Header.SessionId, request.Header.SessionId, StringComparison.Ordinal) &&
        result.RequestId == request.RequestId && result.CounterpartyHeroId == request.CounterpartyHeroId &&
        result.HeroBeingProposedToId == request.HeroBeingProposedToId && result.ProposingHeroId == request.ProposingHeroId;

    private static void PresentAuthorizationOutcome(AuthorityClientOutcome<NetworkMarriageBarterAuthorizationResult> outcome) =>
        MarriageBarterPatch.CompleteAuthorization(outcome.Result, outcome.Applied ? null : outcome.ReasonCode);

    private void PresentCommitOutcome(AuthorityClientOutcome<NetworkMarriageBarterResult> outcome)
    {
        if (outcome.Applied && outcome.Result.Header.Status == AuthorityResultStatus.Accepted)
        {
            receivedDeltas.Remove(DeltaKey(outcome.Result.Header.SessionId, outcome.Result.Header.RequestId,
                outcome.Result.Header.CommittedRevision));
            MarriageBarterPatch.CompleteRequest(outcome.Result, barterClientPresentation);
            return;
        }
        MarriageBarterPatch.CompleteFailedRequest(outcome.ReasonCode);
    }

    private bool CanPublishCanonicalDelta(Hero playerHero, Hero counterpartyHero, Hero proposedHero, Hero proposingHero,
        out string reason)
    {
        reason = null;
        if (!objectManager.TryGetId(playerHero, out _) || !objectManager.TryGetId(counterpartyHero, out _) ||
            !objectManager.TryGetId(proposedHero, out _) || !objectManager.TryGetId(proposingHero, out _))
        {
            reason = "The marriage result cannot be safely synchronized.";
            return false;
        }
        return true;
    }

    private bool PublishCanonicalDelta(AuthorityRequestHeader header, NetworkRequestMarriageBarter request,
        Hero playerHero, Hero counterpartyHero, Hero proposedHero, Hero proposingHero)
    {
        if (!objectManager.TryGetId(playerHero, out var playerId) || !objectManager.TryGetId(counterpartyHero, out var counterpartyId) ||
            !objectManager.TryGetId(proposedHero, out var proposedId) || !objectManager.TryGetId(proposingHero, out var proposingId) ||
            !objectManager.TryGetId(proposedHero.Spouse, out var proposedSpouseId) ||
            !objectManager.TryGetId(proposingHero.Spouse, out var proposingSpouseId)) return false;
        network.SendAll(new NetworkMarriageBarterDelta(header, playerId, counterpartyId, proposedId, proposingId,
            proposedSpouseId, proposingSpouseId, (int)Romance.GetRomanticLevel(proposedHero, proposingHero),
            playerHero.Gold, counterpartyHero.Gold, proposedHero.Gold, proposingHero.Gold, request.Context, request.ContextId));
        return true;
    }

    private AuthorityServerReply<NetworkMarriageBarterResult> IsolateAfterMutation(AuthorityServerContext context,
        NetworkRequestMarriageBarter request, string stage, Exception exception = null)
    {
        if (exception == null) Logger.Fatal("Marriage barter ambiguity after mutation. Stage={Stage}", stage);
        else Logger.Fatal(exception, "Marriage barter ambiguity after mutation. Stage={Stage}", stage);
        foreach (var audience in playerManager.Players)
        {
            try { if (playerManager.TryGetPeer(audience.ControllerId, out var peer)) peer.Disconnect(); } catch { }
        }
        try { context.Peer.Disconnect(); } catch { }
        return new AuthorityServerReply<NetworkMarriageBarterResult>(CreateCommitTerminal(context.Header,
            AuthorityResultStatus.ExecutionFailed, "marriage-isolated"), false, suppressReply: true);
    }

    private void TombstonePeerLeases(NetPeer peer)
    {
        foreach (var lease in marriageLeases.Values.Where(lease => lease.Owner == peer)) lease.Tombstone();
    }

    private static string DeltaKey(string sessionId, long requestId, long revision) =>
        string.Concat(sessionId, ":", requestId, ":", revision);

    private sealed class MarriageAuthorization
    {
        public string LeaseId { get; }
        public NetPeer Owner { get; }
        public string SessionId { get; }
        private string CounterpartyHeroId { get; }
        private int Context { get; }
        private string ContextId { get; }
        private string HeroBeingProposedToId { get; }
        private string ProposingHeroId { get; }
        public DateTime ExpiresAtUtc { get; }
        public bool Consumed { get; private set; }

        public MarriageAuthorization(
            string leaseId,
            NetPeer owner,
            string sessionId,
            string counterpartyHeroId,
            int context,
            string contextId,
            string heroBeingProposedToId,
            string proposingHeroId,
            DateTime expiresAtUtc)
        {
            LeaseId = leaseId;
            Owner = owner;
            SessionId = sessionId;
            CounterpartyHeroId = counterpartyHeroId;
            Context = context;
            ContextId = contextId;
            HeroBeingProposedToId = heroBeingProposedToId;
            ProposingHeroId = proposingHeroId;
            ExpiresAtUtc = expiresAtUtc;
        }

        public bool Matches(NetworkRequestMarriageBarter request)
            => CounterpartyHeroId == request.CounterpartyHeroId &&
               Context == request.Context &&
               ContextId == request.ContextId &&
               HeroBeingProposedToId == request.HeroBeingProposedToId &&
               ProposingHeroId == request.ProposingHeroId;

        public void Consume() => Consumed = true;
        public void Tombstone() => Consumed = true;
    }

    internal readonly struct MarriageBarterAuthorizeIntent
    {
        public MarriageBarterAuthorizeIntent(string clientRequestId, string counterpartyHeroId,
            MarriageConversationContext context, string contextId, string heroBeingProposedToId, string proposingHeroId)
        {
            ClientRequestId = clientRequestId;
            CounterpartyHeroId = counterpartyHeroId;
            Context = context;
            ContextId = contextId;
            HeroBeingProposedToId = heroBeingProposedToId;
            ProposingHeroId = proposingHeroId;
        }
        public string ClientRequestId { get; }
        public string CounterpartyHeroId { get; }
        public MarriageConversationContext Context { get; }
        public string ContextId { get; }
        public string HeroBeingProposedToId { get; }
        public string ProposingHeroId { get; }
    }

    internal readonly struct MarriageBarterCommitIntent
    {
        public MarriageBarterCommitIntent(string clientRequestId, string leaseId, string counterpartyHeroId,
            MarriageConversationContext context, string contextId, string heroBeingProposedToId, string proposingHeroId,
            MarriageBarterTerm[] terms)
        {
            ClientRequestId = clientRequestId;
            LeaseId = leaseId;
            CounterpartyHeroId = counterpartyHeroId;
            Context = context;
            ContextId = contextId;
            HeroBeingProposedToId = heroBeingProposedToId;
            ProposingHeroId = proposingHeroId;
            Terms = terms ?? Array.Empty<MarriageBarterTerm>();
        }
        public string ClientRequestId { get; }
        public string LeaseId { get; }
        public string CounterpartyHeroId { get; }
        public MarriageConversationContext Context { get; }
        public string ContextId { get; }
        public string HeroBeingProposedToId { get; }
        public string ProposingHeroId { get; }
        public MarriageBarterTerm[] Terms { get; }
    }
}
