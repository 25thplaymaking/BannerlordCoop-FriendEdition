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
using GameInterface.Services.Kingdoms;
using GameInterface.Services.Inventory.Data;
using GameInterface.Services.Locations.Conversations;
using GameInterface.Services.MapEvents;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.SiegeEvents.Interfaces;
using GameInterface.Services.TroopRosters.Data;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.BarterSystem;
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

namespace GameInterface.Services.Barters.Handlers;

internal sealed partial class LordBarterHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<LordBarterHandler>();
    private static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(15);
    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly IPlayerManager playerManager;
    private readonly IKingdomMembershipState kingdomMembershipState;
    private readonly ConversationPartyTracker conversationPartyTracker;
    private readonly LocationConversationTracker locationConversationTracker;
    private readonly IBarterClientPresentation presentation;
    private readonly ISafePassagePartyResolver safePassagePartyResolver;
    private readonly ISiegeEventInterface siegeEventInterface;
    private readonly ISendCoalescer sendCoalescer;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<LordBarterIntent, NetworkLordBarterResult> commitRoute;
    private readonly Dictionary<NetPeer, LordBarterAuthorization> authorizations =
        new Dictionary<NetPeer, LordBarterAuthorization>();
    private readonly Dictionary<string, NetworkLordBarterDelta> receivedDeltas =
        new Dictionary<string, NetworkLordBarterDelta>(StringComparer.Ordinal);
    private static LordBarterHandler instance;

    public LordBarterHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network,
        IPlayerManager playerManager,
        IKingdomMembershipState kingdomMembershipState,
        ConversationPartyTracker conversationPartyTracker,
        LocationConversationTracker locationConversationTracker,
        IBarterClientPresentation presentation,
        ISafePassagePartyResolver safePassagePartyResolver,
        ISiegeEventInterface siegeEventInterface,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter,
        ISendCoalescer sendCoalescer = null)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.playerManager = playerManager;
        this.kingdomMembershipState = kingdomMembershipState;
        this.conversationPartyTracker = conversationPartyTracker;
        this.locationConversationTracker = locationConversationTracker;
        this.presentation = presentation;
        this.safePassagePartyResolver = safePassagePartyResolver;
        this.siegeEventInterface = siegeEventInterface;
        this.sendCoalescer = sendCoalescer;
        this.configAuthority = configAuthority;
        instance = this;
        commitRoute = authorityRequestRouter.Register(
            AuthorityRoute<LordBarterIntent, NetworkRequestLordBarter, NetworkLordBarterResult>.Define(
                "barter.lord.commit", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestLordBarter(intent.TargetHeroId, intent.Context,
                    intent.ContextId, intent.Kind, intent.Terms, intent.ClientRequestId, intent.PersuasionOutcomes, header),
                request => request.Header, result => result.Header, ValidateWireShape, BuildCommandKey,
                ValidateHeader, ExecuteCommit, CreateTerminalResult, ProbeClientCommit, _ => { },
                PresentTerminalOutcome, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedResult));
        messageBroker.Subscribe<NetworkAuthorizeLordBarter>(HandleAuthorization);
        messageBroker.Subscribe<NetworkCancelLordBarterAuthorization>(HandleAuthorizationCanceled);
        messageBroker.Subscribe<NetworkLordBarterDelta>(HandleDelta);
        messageBroker.Subscribe<PlayerDisconnected>(HandlePlayerDisconnected);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkAuthorizeLordBarter>(HandleAuthorization);
        messageBroker.Unsubscribe<NetworkCancelLordBarterAuthorization>(HandleAuthorizationCanceled);
        messageBroker.Unsubscribe<NetworkLordBarterDelta>(HandleDelta);
        messageBroker.Unsubscribe<PlayerDisconnected>(HandlePlayerDisconnected);

        // Every other access to these dictionaries happens on the game thread (handlers are drained
        // there), so clearing them from the disposing thread would be a data race - marshal instead.
        // NOT blocking: Dispose runs during container teardown, when the game loop may already have
        // stopped pumping, and a blocking wait there just times out after 30s and fails the teardown.
        // If the queued clear never runs, the handler is being discarded anyway.
        GameThread.RunSafe(() =>
        {
            authorizations.Clear();
        },
            blocking: false,
            context: nameof(LordBarterHandler));

        receivedDeltas.Clear();
        commitRoute.Dispose();
        if (instance == this) instance = null;
        LordBarterPatch.ClearPendingRequest();
    }

    internal static bool TryCommit(LordBarterIntent intent)
    {
        if (instance == null || ModInformation.IsServer) return false;
        instance.commitRoute.Submit(intent);
        return true;
    }

    private void HandleAuthorization(MessagePayload<NetworkAuthorizeLordBarter> payload)
    {
        if (ModInformation.IsClient || !(payload.Who is NetPeer peer)) return;
        var request = payload.What;
        GameThread.RunSafe(() => ProcessAuthorization(peer, request), context: nameof(NetworkAuthorizeLordBarter));
    }

    private void HandleAuthorizationCanceled(MessagePayload<NetworkCancelLordBarterAuthorization> payload)
    {
        if (ModInformation.IsClient || !(payload.Who is NetPeer peer)) return;
        var requestId = payload.What.RequestId;
        GameThread.RunSafe(() =>
        {
            if (authorizations.TryGetValue(peer, out var authorization) && authorization.RequestId == requestId)
                authorizations.Remove(peer);
        }, context: nameof(NetworkCancelLordBarterAuthorization));
    }

    private void HandlePlayerDisconnected(MessagePayload<PlayerDisconnected> payload)
    {
        if (!ModInformation.IsServer) return;
        var peer = payload.What.PlayerId;
        GameThread.RunSafe(() =>
        {
            authorizations.Remove(peer);
        }, context: nameof(PlayerDisconnected));
    }

    private AuthorityServerReply<NetworkLordBarterResult> ExecuteCommit(
        AuthorityServerContext context, NetworkRequestLordBarter request)
    {
        var peer = context.Peer;
        Hero playerHero = null;
        var mutationStarted = false;
        try
        {
            if (!TryResolveContext(peer, request, out playerHero, out var playerParty, out var targetHero, out var targetParty, out var reason))
            {
                return Reject(context.Header, request, playerHero?.Gold ?? 0, reason);
            }

            if (!TryGetAuthorization(peer, request, out var authorization, out reason))
            {
                return Reject(context.Header, request, playerHero.Gold, reason);
            }

            Kingdom targetKingdom = null;
            if ((LordBarterKind)request.Kind == LordBarterKind.JoinKingdomAsClan &&
                !objectManager.TryGetObject(authorization.TargetKingdomId, out targetKingdom))
            {
                return Reject(context.Header, request, playerHero.Gold, "The destination kingdom is no longer available.");
            }

            if (!CanAuthorizeKind(peer, playerHero, targetHero, request, targetKingdom, out reason))
            {
                return Reject(context.Header, request, playerHero.Gold, reason);
            }

            using var playerContext = new BarterPlayerContext(playerHero, playerParty.MobileParty);
            if (!TryBuildBarter(
                    playerHero,
                    playerParty,
                    targetHero,
                    targetParty,
                    request,
                    targetKingdom,
                    out var barter,
                    out reason))
            {
                return Reject(context.Header, request, playerHero.Gold, reason);
            }

            var kind = (LordBarterKind)request.Kind;
            var isSafePassage = kind == LordBarterKind.SafePassage;
            var previousTargetKingdom = kind == LordBarterKind.JoinKingdomAsClan
                ? targetHero.Clan.Kingdom
                : null;

            var (offerValue, safePassageOpponents) =
                EvaluateOffer(barter, playerHero, playerParty, targetHero, targetParty, isSafePassage);

            if (offerValue < -0.01f)
            {
                // The client's barter UI auto-balances the offer to land the total at exactly the
                // acceptance boundary (BarterVM.AutoBalanceAdd, fulfillRatio 1f), so it always shows
                // the deal as acceptable at the minimum price. Both sides then run the SAME test
                // (GetOfferValueForFaction vs targetHero.Clan, threshold -0.01f) - but any drift in
                // the inputs it reads, above all Kingdom._clans / Kingdom._fiefsCache, moves the
                // result. Those feed a quadratic term in DefaultDiplomacyModel
                // (10000 - 100 * sum(WarPartyLimit)^2), so a roster difference of a couple of clans
                // is worth hundreds of thousands of denars - and a one-denar gap at the boundary
                // flips accept into reject.
                //
                // Log the number so this is diagnosable, and tell the player the shortfall instead of
                // a flat refusal they have no way to act on.
                LogOfferValueBreakdown(playerHero, targetHero, targetKingdom, barter, offerValue);

                var shortfall = (int)Math.Ceiling(-offerValue);
                return Reject(
                    context.Header,
                    request,
                    playerHero.Gold,
                    $"The lord will not accept this offer - it is short by about {shortfall} denars. Offer more than the suggested amount.");
            }

            var offeredBarterables = barter.GetOfferedBarterables();
            if (!CanPublishCanonicalDelta(playerHero, playerParty, targetHero, targetParty, offeredBarterables, kind, out reason))
                return Reject(context.Header, request, playerHero.Gold, reason);
            authorizations.Remove(peer);
            mutationStarted = true;

            ApplyAcceptedBarter(
                peer,
                request,
                barter,
                playerHero,
                playerParty,
                targetHero,
                targetParty,
                targetKingdom,
                previousTargetKingdom,
                kind,
                isSafePassage,
                offerValue,
                safePassageOpponents);
            if (!IsKindEffectApplied(peer, playerHero, targetHero, kind, targetKingdom))
                return IsolateAfterMutation(context, request, "lord-kind-postcondition-missing");
            FlushParty(playerParty);
            FlushParty(targetParty);
            if (!PublishCanonicalDelta(context.Header, playerHero, playerParty, targetHero, targetParty,
                    offeredBarterables, kind, (PeaceConversationContext)request.Context == PeaceConversationContext.MapParty))
                return IsolateAfterMutation(context, request, "lord-publication-failed");
            return Accept(context.Header, request, playerHero.Gold);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed to apply authoritative lord barter");
            if (mutationStarted)
                return IsolateAfterMutation(context, request, "lord-ambiguous", exception);
            return Reject(context.Header, request, playerHero?.Gold ?? 0, "The server could not process the lord barter.");
        }
    }

    /// <summary>
    /// What the target thinks the offer is worth. Safe passage is priced against the parties it
    /// would call off; everything else against the target's clan.
    /// </summary>
    private (float OfferValue, IReadOnlyList<MobileParty> OpponentParties) EvaluateOffer(
        BarterData barter,
        Hero playerHero,
        PartyBase playerParty,
        Hero targetHero,
        PartyBase targetParty,
        bool isSafePassage)
    {
        if (isSafePassage)
        {
            return EvaluateSafePassageOffer(
                barter, playerHero, playerParty.MobileParty, targetHero, targetParty.MobileParty);
        }

        return (BarterManager.Instance.GetOfferValueForFaction(barter, targetHero.Clan),
                Array.Empty<MobileParty>());
    }

    /// <summary>
    /// Commits an accepted barter.
    /// </summary>
    /// <remarks>
    /// Everything here runs past the point of no return - the authorization has been spent and
    /// mutationStarted is set - so a throw from this point on is reported as success rather than
    /// telling the client to roll back a change the server really made.
    /// </remarks>
    private void ApplyAcceptedBarter(
        NetPeer peer,
        NetworkRequestLordBarter request,
        BarterData barter,
        Hero playerHero,
        PartyBase playerParty,
        Hero targetHero,
        PartyBase targetParty,
        Kingdom targetKingdom,
        Kingdom previousTargetKingdom,
        LordBarterKind kind,
        bool isSafePassage,
        float offerValue,
        IReadOnlyList<MobileParty> safePassageOpponents)
    {
        // Captured before Apply(), which is what moves the clan out of targetHero's reach.
        var joinTargetClan = kind == LordBarterKind.JoinKingdomAsClan ? targetHero.Clan : null;

        var offered = barter.GetOfferedBarterables();
        foreach (var barterable in offered)
        {
            if (!(barterable is SafePassageBarterable) &&
                !(barterable is NoAttackBarterable))
            {
                barterable.Apply();
            }
        }

        if (joinTargetClan != null)
            CompleteDefection(playerHero, request, joinTargetClan, previousTargetKingdom, targetKingdom);

        if (isSafePassage)
            ApplySafePassage(
                targetParty?.MobileParty,
                playerParty?.MobileParty,
                safePassageOpponents);

        CampaignEventDispatcher.Instance.OnBarterAccepted(playerHero, targetHero, offered);
        ApplyOverpayRelationBonus(playerHero, targetHero, offerValue);

        if (isSafePassage)
            ConversationPartyHold.EndEngagement(conversationPartyTracker, peer);

        FlushGold(playerHero);
        FlushGold(targetHero);
        FlushHeroDeveloper(playerHero);
    }

    /// <summary>
    /// Moves the defecting clan into its new kingdom and awards the persuasion XP.
    /// </summary>
    /// <remarks>
    /// Clan._kingdom is AutoSynced, but the Kingdom._clans / fief collections are not reliably
    /// intercepted, so clients would otherwise see the clan claim the kingdom while the kingdom's
    /// own roster still omitted it. The clan and its previous kingdom are captured BEFORE the
    /// barterables are applied, which is what moves the clan out of targetHero's reach.
    /// </remarks>
    private void CompleteDefection(
        Hero playerHero,
        NetworkRequestLordBarter request,
        Clan joinTargetClan,
        Kingdom previousTargetKingdom,
        Kingdom targetKingdom)
    {
        kingdomMembershipState.MoveClanToKingdom(
            previousTargetKingdom,
            targetKingdom,
            joinTargetClan,
            publishCollectionChanges: true,
            republishExistingCollections: true);

        if (targetKingdom != null && !targetKingdom.Clans.Contains(joinTargetClan))
        {
            Logger.Error(
                "Lord defection did not add clan {Clan} to kingdom {Kingdom} collections",
                joinTargetClan.StringId,
                targetKingdom.StringId);
        }

        ApplyDefectionPersuasionXp(playerHero, request.PersuasionOutcomes);
    }

    private void ProcessAuthorization(NetPeer peer, NetworkAuthorizeLordBarter authorization)
    {
        if (string.IsNullOrEmpty(authorization.RequestId) ||
            !Enum.IsDefined(typeof(PeaceConversationContext), authorization.Context) ||
            !Enum.IsDefined(typeof(LordBarterKind), authorization.Kind))
        {
            return;
        }

        var kind = (LordBarterKind)authorization.Kind;
        Kingdom targetKingdom = null;
        if (kind == LordBarterKind.JoinKingdomAsClan)
        {
            if (string.IsNullOrEmpty(authorization.TargetKingdomId) ||
                !objectManager.TryGetObject(authorization.TargetKingdomId, out targetKingdom))
            {
                return;
            }
        }
        else if (!string.IsNullOrEmpty(authorization.TargetKingdomId))
        {
            return;
        }

        var request = new NetworkRequestLordBarter(
            authorization.TargetHeroId,
            (PeaceConversationContext)authorization.Context,
            authorization.ContextId,
            kind,
            Array.Empty<PeaceBarterTerm>(),
            authorization.RequestId);
        if (!TryResolveContext(
                peer,
                request,
                out var playerHero,
                out _,
                out var targetHero,
                out _,
                out _) ||
            !CanAuthorizeKind(
                peer,
                playerHero,
                targetHero,
                request,
                targetKingdom,
                out _))
        {
            return;
        }

        // A map-party conversation is already exclusive: ConversationPartyTracker holds the target for
        // exactly one peer. A settlement-menu conversation acquires no such hold - authority there is
        // co-location - and co-location is NOT exclusive: every player standing in the settlement
        // satisfies it, so without this two kingdom leaders could each authorize, each pay, and each
        // move the same clan in turn. Reserve the target hero for one peer at a time.
        if (IsTargetHeldByAnotherPeer(peer, authorization.TargetHeroId))
        {
            Logger.Warning(
                "Rejected lord barter authorization for {TargetHeroId}: another player is already negotiating with that lord",
                authorization.TargetHeroId);
            return;
        }

        authorizations[peer] = new LordBarterAuthorization(
            authorization.RequestId,
            authorization.TargetHeroId,
            authorization.Context,
            authorization.ContextId,
            authorization.Kind,
            authorization.TargetKingdomId,
            DateTime.UtcNow.Add(AuthorizationLifetime));
    }

    /// <summary>
    /// Whether a DIFFERENT peer already holds a live authorization against this lord. Expired entries
    /// do not reserve anything, so a player who walked away cannot block the lord for the rest of the
    /// session - the authorization lifetime is what releases it.
    /// </summary>
    private bool IsTargetHeldByAnotherPeer(NetPeer peer, string targetHeroId)
    {
        if (string.IsNullOrEmpty(targetHeroId)) return false;

        var now = DateTime.UtcNow;
        foreach (var entry in authorizations)
        {
            if (entry.Key == peer) continue;
            if (entry.Value.ExpiresAtUtc <= now) continue;
            if (entry.Value.TargetHeroId == targetHeroId) return true;
        }

        return false;
    }

    private bool TryGetAuthorization(
        NetPeer peer,
        NetworkRequestLordBarter request,
        out LordBarterAuthorization authorization,
        out string reason)
    {
        reason = null;
        if (!authorizations.TryGetValue(peer, out authorization))
        {
            reason = "The lord barter is no longer authorized.";
            return false;
        }

        if (authorization.ExpiresAtUtc <= DateTime.UtcNow)
        {
            authorizations.Remove(peer);
            authorization = null;
            reason = "The lord barter authorization expired.";
            return false;
        }

        if (!authorization.Matches(request))
        {
            reason = "The lord barter authorization does not match this offer.";
            return false;
        }

        return true;
    }

    private bool TryResolveContext(NetPeer peer, NetworkRequestLordBarter request, out Hero playerHero, out PartyBase playerParty, out Hero targetHero, out PartyBase targetParty, out string reason)
    {
        playerHero = null;
        playerParty = null;
        targetHero = null;
        targetParty = null;
        reason = null;

        if (!IsRequestWellFormed(request))
        {
            reason = "The server received an invalid lord barter request format.";
            return false;
        }

        if (!TryResolveParticipants(peer, request, out playerHero, out var mobileParty, out targetHero))
        {
            reason = "The server could not identify the lord barter participants.";
            return false;
        }

        if (!AreParticipantsAvailable(playerHero, targetHero, mobileParty))
        {
            reason = "The lord barter participants are no longer available.";
            return false;
        }

        playerParty = mobileParty.Party;
        targetParty = targetHero.PartyBelongedTo?.Party;

        if (!TryValidateConversation(peer, request, mobileParty, playerParty, targetParty, targetHero, out reason))
            return false;

        if (targetHero.IsPrisoner || targetHero.Clan == null)
        {
            reason = "That lord is no longer available for barter.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the request is structurally valid, before anything is resolved from it.
    /// </summary>
    /// <remarks>
    /// Persuasion outcomes only belong on a defection, and the count is bounded so a tampered
    /// client cannot claim an arbitrary number of successful attempts. Rejected outright rather
    /// than truncated, so a malformed request fails loudly instead of earning partial credit.
    /// </remarks>
    private static bool IsRequestWellFormed(NetworkRequestLordBarter request)
    {
        if (string.IsNullOrEmpty(request.RequestId)) return false;
        if (!Enum.IsDefined(typeof(PeaceConversationContext), request.Context)) return false;
        if (!Enum.IsDefined(typeof(LordBarterKind), request.Kind)) return false;

        var outcomes = request.PersuasionOutcomes;
        if (outcomes == null) return true;
        if (outcomes.Length > LordBarterPatch.MaxDefectionPersuasionOutcomes) return false;

        return (LordBarterKind)request.Kind == LordBarterKind.JoinKingdomAsClan || outcomes.Length == 0;
    }

    /// <summary>
    /// Resolves the requesting player and the target. Outputs stay partially assigned on failure,
    /// so a caller can still report the requester's gold when the target is what went missing.
    /// </summary>
    private bool TryResolveParticipants(
        NetPeer peer,
        NetworkRequestLordBarter request,
        out Hero playerHero,
        out MobileParty mobileParty,
        out Hero targetHero)
    {
        playerHero = null;
        mobileParty = null;
        targetHero = null;

        return playerManager.TryGetPlayer(peer, out Player player) &&
               objectManager.TryGetObject(player.HeroId, out playerHero) &&
               objectManager.TryGetObject(player.MobilePartyId, out mobileParty) &&
               objectManager.TryGetObject(request.TargetHeroId, out targetHero);
    }

    /// <summary>
    /// IsAlive matches MarriageBarterHandler's participant check: a hero can die between the
    /// authorization and the request, and every barterable dereferences both heroes.
    /// </summary>
    private static bool AreParticipantsAvailable(Hero playerHero, Hero targetHero, MobileParty mobileParty)
    {
        return !targetHero.IsPlayerHero() &&
               targetHero.IsAlive &&
               playerHero.IsAlive &&
               mobileParty.LeaderHero == playerHero &&
               mobileParty.IsActive;
    }

    /// <summary>
    /// Confirms the conversation the request claims is still live, by the rules of its context.
    /// </summary>
    private bool TryValidateConversation(
        NetPeer peer,
        NetworkRequestLordBarter request,
        MobileParty mobileParty,
        PartyBase playerParty,
        PartyBase targetParty,
        Hero targetHero,
        out string reason)
    {
        switch ((PeaceConversationContext)request.Context)
        {
            case PeaceConversationContext.Settlement:
                reason = "The lord settlement conversation is no longer active.";
                return IsSettlementConversationLive(request, mobileParty, targetHero, ref reason);

            case PeaceConversationContext.MapParty:
                reason = "The lord conversation is no longer active.";
                return IsMapPartyConversationLive(peer, request, mobileParty, playerParty, targetParty, ref reason);

            case PeaceConversationContext.Location:
                reason = "The lord conversation is no longer active.";
                return IsLocationConversationLive(peer, request, targetHero, ref reason);

            default:
                // Refused rather than validated as a settlement conversation - accepting a context we
                // do not understand is how an unvalidated barter gets through.
                reason = "The lord conversation context is not supported.";
                return false;
        }
    }

    /// <summary>
    /// A settlement-menu conversation acquires no engagement - there is no agent and no location
    /// mission to lock - so authority comes from co-location instead: both the requesting party and
    /// the target must actually be inside the settlement named by the request. That is as strong as
    /// the hold for this case, because a player who is not in the settlement cannot be talking to
    /// someone who is.
    /// </summary>
    private bool IsSettlementConversationLive(
        NetworkRequestLordBarter request, MobileParty mobileParty, Hero targetHero, ref string reason)
    {
        if (!objectManager.TryGetObject(request.ContextId, out Settlement conversationSettlement) ||
            mobileParty.CurrentSettlement != conversationSettlement ||
            targetHero.CurrentSettlement != conversationSettlement)
        {
            return false;
        }

        reason = null;
        return true;
    }

    private bool IsMapPartyConversationLive(
        NetPeer peer,
        NetworkRequestLordBarter request,
        MobileParty mobileParty,
        PartyBase playerParty,
        PartyBase targetParty,
        ref string reason)
    {
        if (!objectManager.TryGetObject(request.ContextId, out PartyBase requestedParty) ||
            requestedParty != targetParty ||
            requestedParty.MobileParty?.IsActive != true ||
            requestedParty.MobileParty.MapEvent != null ||
            mobileParty.MapEvent != null ||
            !objectManager.TryGetId(playerParty, out var playerPartyId) ||
            !conversationPartyTracker.TryGetEngagement(peer, out var engagement) ||
            engagement.PartyId != request.ContextId ||
            engagement.EngagerPartyId != playerPartyId)
        {
            return false;
        }

        reason = null;
        return true;
    }

    private bool IsLocationConversationLive(
        NetPeer peer, NetworkRequestLordBarter request, Hero targetHero, ref string reason)
    {
        if (targetHero.CharacterObject == null ||
            !objectManager.TryGetId(targetHero.CharacterObject, out var characterId) ||
            !locationConversationTracker.TryGetEngagement(peer, out var npcKey) ||
            npcKey != LocationConversationTracker.ComposeKey(request.ContextId, characterId))
        {
            return false;
        }

        reason = null;
        return true;
    }

    private bool CanAuthorizeKind(
        NetPeer peer,
        Hero playerHero,
        Hero targetHero,
        NetworkRequestLordBarter request,
        Kingdom targetKingdom,
        out string reason)
    {
        reason = null;
        var kind = (LordBarterKind)request.Kind;
        if (kind == LordBarterKind.Generic)
            return true;

        if (kind == LordBarterKind.SafePassage)
        {
            if ((PeaceConversationContext)request.Context != PeaceConversationContext.MapParty ||
                playerHero.MapFaction == null ||
                targetHero.MapFaction == null ||
                !FactionManager.IsAtWarAgainstFaction(playerHero.MapFaction, targetHero.MapFaction) ||
                !conversationPartyTracker.TryGetEngagement(peer, out var engagement) ||
                !engagement.EngagerIsDefender)
            {
                reason = "This encounter is not eligible for a safe-passage barter.";
                return false;
            }

            return true;
        }

        var playerClan = playerHero.Clan;
        var targetClan = targetHero.Clan;
        if (playerClan?.Kingdom == null ||
            targetKingdom == null ||
            playerClan.Kingdom != targetKingdom ||
            playerClan.Leader != playerHero ||
            targetClan?.Leader != targetHero ||
            targetClan.Kingdom == null ||
            targetClan.Kingdom == playerClan.Kingdom ||
            targetClan.IsMinorFaction ||
            targetClan.IsRebelClan ||
            targetClan.IsUnderMercenaryService)
        {
            reason = "Those clans are not eligible for a kingdom defection.";
            return false;
        }

        return true;
    }

    private bool TryBuildBarter(
        Hero playerHero,
        PartyBase playerParty,
        Hero targetHero,
        PartyBase targetParty,
        NetworkRequestLordBarter request,
        Kingdom targetKingdom,
        out BarterData barter,
        out string reason)
    {
        barter = null; reason = null;
        if (BarterManager.Instance == null)
        {
            reason = "The server barter system is unavailable.";
            return false;
        }
        var kind = (LordBarterKind)request.Kind;
        if (kind == LordBarterKind.JoinKingdomAsClan &&
            !CanAuthorizeKind(null, playerHero, targetHero, request, targetKingdom, out reason))
            return false;
        if (kind == LordBarterKind.SafePassage && targetParty?.MobileParty == null)
        {
            reason = "The safe-passage party is no longer available.";
            return false;
        }

        BarterManager.BarterContextInitializer initializer = null;
        var baseBarterables = new List<Barterable>();
        if (kind == LordBarterKind.SafePassage)
        {
            initializer = BarterManager.Instance.InitializeSafePassageBarterContext;
            baseBarterables.Add(new SafePassageBarterable(targetHero, playerHero, targetParty, playerParty));
            baseBarterables.Add(new NoAttackBarterable(playerHero, targetHero, playerParty, targetParty, CampaignTime.Days(5f)));
        }
        else if (kind == LordBarterKind.JoinKingdomAsClan)
        {
            initializer = BarterManager.Instance.InitializeJoinFactionBarterContext;
            baseBarterables.Add(new JoinKingdomAsClanBarterable(
                targetHero,
                targetKingdom,
                isDefecting: true));
        }

        barter = new BarterData(playerHero, targetHero, playerParty, targetParty, initializer);
        barter.AddBarterGroup(new DefaultsBarterGroup());
        foreach (var baseBarterable in baseBarterables)
        {
            baseBarterable.SetIsOffered(true);
            barter.AddBarterable<DefaultsBarterGroup>(baseBarterable, true);
        }
        CampaignEventDispatcher.Instance.OnBarterablesRequested(barter);
        return TryApplyTerms(playerHero, targetHero, barter, request.Terms, out reason);
    }

    private bool TryApplyTerms(Hero playerHero, Hero targetHero, BarterData barter, IEnumerable<PeaceBarterTerm> terms, out string reason)
    {
        var used = new HashSet<Barterable>();
        foreach (var term in terms ?? Array.Empty<PeaceBarterTerm>())
        {
            if (!Enum.IsDefined(typeof(PeaceBarterTermType), term.Type) || term.Amount <= 0 || string.IsNullOrEmpty(term.OwnerHeroId))
            {
                reason = "The lord barter contains an invalid term.";
                return false;
            }
            var type = (PeaceBarterTermType)term.Type;
            var barterable = barter.GetBarterables().FirstOrDefault(candidate =>
                (candidate.OriginalOwner == playerHero || candidate.OriginalOwner == targetHero) &&
                objectManager.TryGetId(candidate.OriginalOwner, out var ownerId) && ownerId == term.OwnerHeroId && Matches(candidate, type, term));
            if (barterable == null || !used.Add(barterable) || term.Amount > barterable.MaxAmount)
            {
                reason = "The lord barter no longer matches the server's available terms.";
                return false;
            }
            barterable.CurrentAmount = term.Amount;
            barterable.SetIsOffered(true);
        }
        reason = null;
        return true;
    }

    private bool Matches(Barterable barterable, PeaceBarterTermType type, PeaceBarterTerm term)
    {
        switch (type)
        {
            case PeaceBarterTermType.Gold: return barterable is GoldBarterable;
            case PeaceBarterTermType.Item:
                return barterable is ItemBarterable item && MatchesItem(item, term);
            case PeaceBarterTermType.Fief:
                return barterable is FiefBarterable fief && objectManager.TryGetId(fief.TargetSettlement, out var settlementId) && settlementId == term.ObjectId;
            case PeaceBarterTermType.TransferPrisoner:
                return barterable is TransferPrisonerBarterable transfer && MatchesPrisoner(transfer._prisonerCharacter, term);
            case PeaceBarterTermType.ReleasePrisoner:
                return barterable is SetPrisonerFreeBarterable release && MatchesPrisoner(release._prisonerCharacter, term);
            default: return false;
        }
    }

    /// <summary>
    /// The item must be the same one, and carry the same modifier - or the same absence of one,
    /// since an unmodified item and a modified one are different goods at a different price.
    /// </summary>
    private bool MatchesItem(ItemBarterable item, PeaceBarterTerm term)
    {
        var equipment = item.ItemRosterElement.EquipmentElement;

        if (!objectManager.TryGetId(equipment.Item, out var itemId) ||
            itemId != term.ObjectId ||
            (equipment.ItemModifier == null) != term.ItemModifierNull)
        {
            return false;
        }

        return equipment.ItemModifier == null ||
               (objectManager.TryGetId(equipment.ItemModifier, out var modifierId) &&
                modifierId == term.ItemModifierId);
    }

    private bool MatchesPrisoner(Hero prisoner, PeaceBarterTerm term) => prisoner?.CharacterObject != null && objectManager.TryGetId(prisoner.CharacterObject, out var id) && id == term.ObjectId;

    internal static void ApplyOverpayRelationBonus(Hero playerHero, Hero otherHero, float overpayAmount)
    {
        var campaign = Campaign.Current;
        if (otherHero == null ||
            overpayAmount <= 0f ||
            playerHero?.MapFaction == null ||
            otherHero.MapFaction == null ||
            otherHero.MapFaction.IsAtWarWith(playerHero.MapFaction) ||
            campaign?.Models?.BarterModel == null)
        {
            return;
        }

        var relationBonus = campaign.Models.BarterModel
            .CalculateOverpayRelationIncreaseCosts(otherHero, overpayAmount);
        if (relationBonus > 0)
            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(playerHero, otherHero, relationBonus);
    }

    private void FlushGold(Hero hero)
    {
        if (sendCoalescer != null && hero != null && objectManager.TryGetId(hero, out var id)) sendCoalescer.FlushInstance(id, network);
    }

    // Both the Charm XP and the total XP are coalesced dictionary upserts on HeroDeveloper, so
    // without this the recruiter sees no skill change until the next coalescer tick - long after the
    // barter result lands.
    private void FlushHeroDeveloper(Hero hero)
    {
        if (sendCoalescer != null && hero?.HeroDeveloper != null &&
            objectManager.TryGetId(hero.HeroDeveloper, out var id))
            sendCoalescer.FlushInstance(id, network);
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
        return header.ExpectedRevision == current.Revision ? AuthorityHeaderValidation.Valid :
            AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
    }

    private static string ValidateWireShape(NetworkRequestLordBarter request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128 ||
            string.IsNullOrWhiteSpace(request.TargetHeroId) || request.TargetHeroId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.ContextId) || request.ContextId.Length > 256 ||
            !Enum.IsDefined(typeof(PeaceConversationContext), request.Context) ||
            !Enum.IsDefined(typeof(LordBarterKind), request.Kind) || request.Terms == null || request.Terms.Length > 128 ||
            request.PersuasionOutcomes == null || request.PersuasionOutcomes.Length > LordBarterPatch.MaxDefectionPersuasionOutcomes)
            return "invalid-lord-barter";
        if ((LordBarterKind)request.Kind != LordBarterKind.JoinKingdomAsClan && request.PersuasionOutcomes.Length != 0)
            return "invalid-lord-persuasion";
        return request.Terms.Any(term => !Enum.IsDefined(typeof(PeaceBarterTermType), term.Type) || term.Amount <= 0 ||
            string.IsNullOrWhiteSpace(term.OwnerHeroId) || term.OwnerHeroId.Length > 256 ||
            (term.ObjectId?.Length ?? 0) > 256 || (term.ItemModifierId?.Length ?? 0) > 256)
            ? "invalid-lord-barter-term" : null;
    }

    private static string BuildCommandKey(NetworkRequestLordBarter request) => string.Concat(
        request.RequestId, ":", request.TargetHeroId, ":", request.Context, ":", request.ContextId, ":", request.Kind, ":",
        string.Join("|", request.Terms.OrderBy(term => term.Type).ThenBy(term => term.OwnerHeroId, StringComparer.Ordinal)
            .ThenBy(term => term.ObjectId, StringComparer.Ordinal).ThenBy(term => term.ItemModifierId, StringComparer.Ordinal)
            .ThenBy(term => term.Amount).Select(term => string.Concat(term.Type, ":", term.OwnerHeroId, ":", term.ObjectId,
                ":", term.ItemModifierId, ":", term.ItemModifierNull, ":", term.Amount))), ":",
        string.Join("|", request.PersuasionOutcomes.Select(outcome => string.Concat(outcome.Result, ":", outcome.ArgumentStrength))));

    private static NetworkLordBarterResult CreateTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason), 0, null);

    private static AuthorityServerReply<NetworkLordBarterResult> Accept(
        AuthorityRequestHeader header, NetworkRequestLordBarter request, int playerGold) => new(
        new NetworkLordBarterResult(request.ContextId, new AuthorityResultHeader(header.SessionId, header.RequestId,
            AuthorityResultStatus.Accepted, header.ExpectedRevision, null), playerGold, request.RequestId), true);

    private static AuthorityServerReply<NetworkLordBarterResult> Reject(
        AuthorityRequestHeader header, NetworkRequestLordBarter request, int playerGold, string reason)
    {
        Logger.Warning("Rejected lord barter with {TargetHeroId}: {Reason}", request.TargetHeroId, reason);
        return new AuthorityServerReply<NetworkLordBarterResult>(new NetworkLordBarterResult(request.ContextId,
            new AuthorityResultHeader(header.SessionId, header.RequestId, AuthorityResultStatus.Rejected,
                header.ExpectedRevision, reason), playerGold, request.RequestId), false);
    }

    private static bool IsExpectedResult(NetworkRequestLordBarter request, NetworkLordBarterResult result) =>
        result.Header.RequestId == request.Header.RequestId && result.Header.CommittedRevision == request.Header.ExpectedRevision &&
        string.Equals(result.Header.SessionId, request.Header.SessionId, StringComparison.Ordinal) &&
        result.ContextId == request.ContextId && result.RequestId == request.RequestId;

    private void HandleDelta(MessagePayload<NetworkLordBarterDelta> payload)
    {
        if (ModInformation.IsServer || !configAuthority.IsTrustedServer(payload.Who) ||
            !configAuthority.TryGetCurrent(out ModConfigSnapshot config)) return;
        var delta = payload.What;
        if (delta.AuthorityRequestId <= 0 || delta.CommittedRevision != config.Revision ||
            !string.Equals(delta.SessionId, config.SessionId, StringComparison.Ordinal)) return;
        receivedDeltas[DeltaKey(delta.SessionId, delta.AuthorityRequestId, delta.CommittedRevision)] = delta;
    }

    private AuthorityCommitProbeResult ProbeClientCommit(NetworkLordBarterResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted || !configAuthority.TryGetCurrent(out ModConfigSnapshot config) ||
            config.Revision != result.Header.CommittedRevision || config.SessionId != result.Header.SessionId ||
            !receivedDeltas.TryGetValue(DeltaKey(result.Header.SessionId, result.Header.RequestId, result.Header.CommittedRevision), out var delta) ||
            !objectManager.TryGetObject(delta.PlayerHeroId, out Hero playerHero) || !objectManager.TryGetObject(delta.TargetHeroId, out Hero targetHero) ||
            playerHero.Gold != delta.PlayerGold || targetHero.Gold != delta.TargetGold ||
            !MatchesRoster(delta.PlayerPartyId, delta.PlayerItems, delta.PlayerPrisoners, delta.PlayerItemRosterHash, delta.PlayerPrisonRosterHash) ||
            !MatchesRoster(delta.TargetPartyId, delta.TargetItems, delta.TargetPrisoners, delta.TargetItemRosterHash, delta.TargetPrisonRosterHash) ||
            !MatchesFiefs(delta.Fiefs) || !MatchesPrisoners(delta.Prisoners) ||
            ((LordBarterKind)delta.Kind == LordBarterKind.JoinKingdomAsClan && !MatchesDefection(delta)))
            return AuthorityCommitProbeResult.Pending;
        return AuthorityCommitProbeResult.Applied;
    }

    private void PresentTerminalOutcome(AuthorityClientOutcome<NetworkLordBarterResult> outcome)
    {
        if (outcome.Applied)
        {
            receivedDeltas.Remove(DeltaKey(outcome.Result.Header.SessionId, outcome.Result.Header.RequestId, outcome.Result.Header.CommittedRevision));
            LordBarterPatch.CompleteRequest(outcome.Result, presentation);
            return;
        }
        LordBarterPatch.CompleteFailedRequest(outcome.ReasonCode);
    }

    private static string DeltaKey(string sessionId, long requestId, long revision) => string.Concat(sessionId, ":", requestId, ":", revision);

    private bool CanPublishCanonicalDelta(Hero playerHero, PartyBase playerParty, Hero targetHero, PartyBase targetParty,
        IEnumerable<Barterable> offered, LordBarterKind kind, out string reason)
    {
        reason = null;
        if (!objectManager.TryGetId(playerHero, out _) || !objectManager.TryGetId(targetHero, out _) ||
            !TryPackRoster(playerParty, out _, out _, out _, out _, out _) || !TryPackRoster(targetParty, out _, out _, out _, out _, out _) ||
            !TryPackFiefs(offered, out _) || !TryPackPrisonerStates(offered, out _))
        {
            reason = "lord-state-unavailable";
            return false;
        }
        if (kind == LordBarterKind.JoinKingdomAsClan && targetHero.Clan == null)
        {
            reason = "lord-defection-state-unavailable";
            return false;
        }
        return true;
    }

    private bool PublishCanonicalDelta(AuthorityRequestHeader header, Hero playerHero, PartyBase playerParty,
        Hero targetHero, PartyBase targetParty, IEnumerable<Barterable> offered, LordBarterKind kind, bool engagementEnded)
    {
        if (!objectManager.TryGetId(playerHero, out var playerHeroId) || !objectManager.TryGetId(targetHero, out var targetHeroId) ||
            !TryPackRoster(playerParty, out var playerPartyId, out var playerItems, out var playerPrisoners, out var playerItemHash, out var playerPrisonHash) ||
            !TryPackRoster(targetParty, out var targetPartyId, out var targetItems, out var targetPrisoners, out var targetItemHash, out var targetPrisonHash) ||
            !TryPackFiefs(offered, out var fiefs) || !TryPackPrisonerStates(offered, out var prisoners)) return false;
        string clanId = null;
        string kingdomId = null;
        if (kind == LordBarterKind.JoinKingdomAsClan &&
            (!objectManager.TryGetId(targetHero.Clan, out clanId) || !objectManager.TryGetId(targetHero.Clan.Kingdom, out kingdomId))) return false;
        network.SendAll(new NetworkLordBarterDelta(header, kind, playerHeroId, targetHeroId, playerPartyId, targetPartyId,
            playerHero.Gold, targetHero.Gold, playerItems, playerPrisoners, targetItems, targetPrisoners,
            playerItemHash, playerPrisonHash, targetItemHash, targetPrisonHash, fiefs, prisoners, clanId, kingdomId, engagementEnded));
        return true;
    }

    private bool IsKindEffectApplied(NetPeer peer, Hero playerHero, Hero targetHero, LordBarterKind kind, Kingdom targetKingdom)
    {
        if (kind == LordBarterKind.JoinKingdomAsClan)
            return targetHero.Clan?.Kingdom == targetKingdom && targetKingdom?.Clans.Contains(targetHero.Clan) == true;
        return kind != LordBarterKind.SafePassage || !conversationPartyTracker.TryGetEngagement(peer, out _);
    }

    private bool MatchesDefection(NetworkLordBarterDelta delta) =>
        !string.IsNullOrEmpty(delta.DefectingClanId) && !string.IsNullOrEmpty(delta.DefectingClanKingdomId) &&
        objectManager.TryGetObject(delta.DefectingClanId, out Clan clan) && objectManager.TryGetId(clan.Kingdom, out var kingdomId) &&
        kingdomId == delta.DefectingClanKingdomId && clan.Kingdom.Clans.Contains(clan);

    private void FlushParty(PartyBase party)
    {
        if (sendCoalescer != null && party != null && objectManager.TryGetId(party, out var partyId)) sendCoalescer.FlushInstance(partyId, network);
    }

    private bool TryPackRoster(PartyBase party, out string partyId, out ItemRosterElementData[] items,
        out TroopRosterElementData[] prisoners, out long itemHash, out long prisonerHash)
    {
        partyId = null; items = Array.Empty<ItemRosterElementData>(); prisoners = Array.Empty<TroopRosterElementData>(); itemHash = 0; prisonerHash = 0;
        if (party == null) return true;
        if (!objectManager.TryGetId(party, out partyId) || !TryPackItems(party.ItemRoster, out items) || !TryPackPrisoners(party.PrisonRoster, out prisoners)) return false;
        itemHash = HashItems(items); prisonerHash = HashPrisoners(prisoners); return true;
    }

    private bool MatchesRoster(string partyId, ItemRosterElementData[] items, TroopRosterElementData[] prisoners, long itemHash, long prisonerHash)
    {
        if (string.IsNullOrEmpty(partyId)) return itemHash == 0 && prisonerHash == 0;
        if (!objectManager.TryGetObject(partyId, out PartyBase party) || !TryPackItems(party.ItemRoster, out var localItems) || !TryPackPrisoners(party.PrisonRoster, out var localPrisoners)) return false;
        return HashItems(localItems) == itemHash && HashPrisoners(localPrisoners) == prisonerHash &&
            HashItems(items ?? Array.Empty<ItemRosterElementData>()) == itemHash && HashPrisoners(prisoners ?? Array.Empty<TroopRosterElementData>()) == prisonerHash;
    }

    private bool TryPackItems(ItemRoster roster, out ItemRosterElementData[] data)
    {
        var packed = new List<ItemRosterElementData>();
        foreach (var element in roster)
        {
            if (element.Amount <= 0 || element.EquipmentElement.Item == null || !objectManager.TryGetCatalogId(element.EquipmentElement.Item, out var itemId)) { data = null; return false; }
            string modifierId = null; var noModifier = element.EquipmentElement.ItemModifier == null;
            if (!noModifier && !objectManager.TryGetId(element.EquipmentElement.ItemModifier, out modifierId)) { data = null; return false; }
            packed.Add(new ItemRosterElementData(new ItemObjectData(itemId, modifierId, noModifier), element.Amount));
        }
        data = packed.OrderBy(item => item.ItemObjectData.ItemObjectId, StringComparer.Ordinal).ThenBy(item => item.ItemObjectData.ItemModifierId, StringComparer.Ordinal).ToArray(); return true;
    }

    private bool TryPackPrisoners(TroopRoster roster, out TroopRosterElementData[] data)
    {
        var packed = new List<TroopRosterElementData>();
        foreach (var element in roster.GetTroopRoster())
        {
            if (element.Character == null || element.Number <= 0 || !objectManager.TryGetId(element.Character, out var characterId)) { data = null; return false; }
            packed.Add(new TroopRosterElementData(characterId, element.Number, element.WoundedNumber, element.Xp));
        }
        data = packed.OrderBy(prisoner => prisoner.CharacterId, StringComparer.Ordinal).ToArray(); return true;
    }

    private bool TryPackFiefs(IEnumerable<Barterable> barterables, out PeaceBarterFiefStateData[] data)
    {
        var packed = new List<PeaceBarterFiefStateData>();
        foreach (var fief in barterables.OfType<FiefBarterable>())
        {
            if (!objectManager.TryGetId(fief.TargetSettlement, out var settlementId) || !objectManager.TryGetId(fief.TargetSettlement.OwnerClan, out var ownerClanId)) { data = null; return false; }
            packed.Add(new PeaceBarterFiefStateData(settlementId, ownerClanId));
        }
        data = packed.OrderBy(fief => fief.SettlementId, StringComparer.Ordinal).ToArray(); return true;
    }

    private bool TryPackPrisonerStates(IEnumerable<Barterable> barterables, out PeaceBarterPrisonerStateData[] data)
    {
        var packed = new List<PeaceBarterPrisonerStateData>();
        foreach (var barterable in barterables)
        {
            Hero prisoner = barterable is TransferPrisonerBarterable transfer ? transfer._prisonerCharacter :
                barterable is SetPrisonerFreeBarterable released ? released._prisonerCharacter : null;
            if (prisoner == null) continue;
            if (!objectManager.TryGetId(prisoner.CharacterObject, out var characterId)) { data = null; return false; }
            string captorPartyId = null;
            if (prisoner.PartyBelongedToAsPrisoner != null && !objectManager.TryGetId(prisoner.PartyBelongedToAsPrisoner, out captorPartyId)) { data = null; return false; }
            packed.Add(new PeaceBarterPrisonerStateData(characterId, prisoner.IsPrisoner, captorPartyId));
        }
        data = packed.OrderBy(prisoner => prisoner.HeroId, StringComparer.Ordinal).ToArray(); return true;
    }

    private bool MatchesFiefs(IEnumerable<PeaceBarterFiefStateData> fiefs) => (fiefs ?? Array.Empty<PeaceBarterFiefStateData>()).All(fief =>
        objectManager.TryGetObject(fief.SettlementId, out Settlement settlement) && objectManager.TryGetId(settlement.OwnerClan, out var ownerClanId) && ownerClanId == fief.OwnerClanId);

    private bool MatchesPrisoners(IEnumerable<PeaceBarterPrisonerStateData> prisoners)
    {
        foreach (var prisoner in prisoners ?? Array.Empty<PeaceBarterPrisonerStateData>())
        {
            if (!objectManager.TryGetObject(prisoner.HeroId, out CharacterObject character) || character.HeroObject == null || character.HeroObject.IsPrisoner != prisoner.IsPrisoner) return false;
            string captorPartyId = null;
            if (character.HeroObject.PartyBelongedToAsPrisoner != null && !objectManager.TryGetId(character.HeroObject.PartyBelongedToAsPrisoner, out captorPartyId)) return false;
            if (captorPartyId != prisoner.CaptorPartyId) return false;
        }
        return true;
    }

    private static long HashItems(IEnumerable<ItemRosterElementData> items)
    {
        long hash = 1469598103934665603L;
        foreach (var item in items) { hash = Hash(hash, item.ItemObjectData.ItemObjectId); hash = Hash(hash, item.ItemObjectData.ItemModifierId); hash = Hash(hash, item.Amount); }
        return hash;
    }

    private static long HashPrisoners(IEnumerable<TroopRosterElementData> prisoners)
    {
        long hash = 1469598103934665603L;
        foreach (var prisoner in prisoners) { hash = Hash(hash, prisoner.CharacterId); hash = Hash(hash, prisoner.Number); hash = Hash(hash, prisoner.WoundedNumber); hash = Hash(hash, prisoner.Xp); }
        return hash;
    }

    private static long Hash(long value, string text) { unchecked { foreach (var character in text ?? string.Empty) value = (value ^ character) * 1099511628211L; return value; } }
    private static long Hash(long value, int number) => unchecked((value ^ number) * 1099511628211L);

    private AuthorityServerReply<NetworkLordBarterResult> IsolateAfterMutation(AuthorityServerContext context,
        NetworkRequestLordBarter request, string stage, Exception exception = null)
    {
        if (exception == null) Logger.Fatal("Lord barter ambiguity after mutation. Stage={Stage}", stage);
        else Logger.Fatal(exception, "Lord barter ambiguity after mutation. Stage={Stage}", stage);
        foreach (var audience in playerManager.Players)
        {
            try { if (playerManager.TryGetPeer(audience.ControllerId, out var peer)) peer.Disconnect(); } catch { }
        }
        try { context.Peer.Disconnect(); } catch { }
        return new AuthorityServerReply<NetworkLordBarterResult>(CreateTerminalResult(context.Header,
            AuthorityResultStatus.ExecutionFailed, "lord-isolated"), false, suppressReply: true);
    }

    private sealed class LordBarterAuthorization
    {
        public string RequestId { get; }
        public string TargetHeroId { get; }
        private int Context { get; }
        private string ContextId { get; }
        private int Kind { get; }
        public string TargetKingdomId { get; }
        public DateTime ExpiresAtUtc { get; }

        public LordBarterAuthorization(
            string requestId,
            string targetHeroId,
            int context,
            string contextId,
            int kind,
            string targetKingdomId,
            DateTime expiresAtUtc)
        {
            RequestId = requestId;
            TargetHeroId = targetHeroId;
            Context = context;
            ContextId = contextId;
            Kind = kind;
            TargetKingdomId = targetKingdomId;
            ExpiresAtUtc = expiresAtUtc;
        }

        public bool Matches(NetworkRequestLordBarter request)
        {
            return request.RequestId == RequestId &&
                   request.TargetHeroId == TargetHeroId &&
                   request.Context == Context &&
                   request.ContextId == ContextId &&
                   request.Kind == Kind;
        }
    }
}

internal readonly struct LordBarterIntent
{
    public LordBarterIntent(string targetHeroId, PeaceConversationContext context, string contextId, LordBarterKind kind,
        PeaceBarterTerm[] terms, string clientRequestId, DefectionPersuasionOutcome[] persuasionOutcomes)
    {
        TargetHeroId = targetHeroId;
        Context = context;
        ContextId = contextId;
        Kind = kind;
        Terms = terms ?? Array.Empty<PeaceBarterTerm>();
        ClientRequestId = clientRequestId;
        PersuasionOutcomes = persuasionOutcomes ?? Array.Empty<DefectionPersuasionOutcome>();
    }

    public string TargetHeroId { get; }
    public PeaceConversationContext Context { get; }
    public string ContextId { get; }
    public LordBarterKind Kind { get; }
    public PeaceBarterTerm[] Terms { get; }
    public string ClientRequestId { get; }
    public DefectionPersuasionOutcome[] PersuasionOutcomes { get; }
}
