using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Barters.Messages;
using GameInterface.Services.Barters.Patches;
using GameInterface.Services.Locations.Conversations;
using GameInterface.Services.Inventory.Data;
using GameInterface.Services.MapEvents;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
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
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace GameInterface.Services.Barters.Handlers;

internal sealed class PeaceBarterHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<PeaceBarterHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly IPlayerManager playerManager;
    private readonly ConversationPartyTracker conversationPartyTracker;
    private readonly LocationConversationTracker locationConversationTracker;
    private readonly IBarterClientPresentation barterClientPresentation;
    private readonly ISendCoalescer sendCoalescer;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<PeaceBarterIntent, NetworkPeaceBarterResult> commitRoute;
    private readonly Dictionary<string, NetworkPeaceBarterDelta> receivedDeltas =
        new Dictionary<string, NetworkPeaceBarterDelta>(StringComparer.Ordinal);
    private static PeaceBarterHandler instance;

    public PeaceBarterHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network,
        IPlayerManager playerManager,
        ConversationPartyTracker conversationPartyTracker,
        LocationConversationTracker locationConversationTracker,
        IBarterClientPresentation barterClientPresentation,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter,
        ISendCoalescer sendCoalescer = null)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.playerManager = playerManager;
        this.conversationPartyTracker = conversationPartyTracker;
        this.locationConversationTracker = locationConversationTracker;
        this.barterClientPresentation = barterClientPresentation;
        this.sendCoalescer = sendCoalescer;
        this.configAuthority = configAuthority;
        instance = this;

        commitRoute = authorityRequestRouter.Register(
            AuthorityRoute<PeaceBarterIntent, NetworkRequestPeaceBarter, NetworkPeaceBarterResult>.Define(
                "barter.peace.commit", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestPeaceBarter(intent.TargetHeroId, intent.Context,
                    intent.ContextId, intent.Terms, intent.ClientRequestId, header),
                request => request.Header, result => result.Header, ValidateWireShape, BuildCommandKey,
                ValidateHeader, ExecuteCommit, CreateTerminalResult, ProbeClientCommit, _ => { },
                PresentTerminalOutcome, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedResult));
        messageBroker.Subscribe<NetworkPeaceBarterDelta>(HandleDelta);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkPeaceBarterDelta>(HandleDelta);
        commitRoute.Dispose();
        if (instance == this) instance = null;
        PeaceBarterPatch.ClearPendingRequest();
    }

    internal static bool TrySubmit(PeaceBarterIntent intent)
    {
        if (instance == null || ModInformation.IsServer) return false;
        instance.commitRoute.Submit(intent);
        return true;
    }

    private AuthorityServerReply<NetworkPeaceBarterResult> ExecuteCommit(
        AuthorityServerContext context, NetworkRequestPeaceBarter request)
    {
        Hero playerHero = null;
        BarterPlayerContext playerContext = null;
        var mutationStarted = false;
        try
        {
            if (!TryResolveContext(
                    context.Peer,
                    request,
                    out playerHero,
                    out var playerParty,
                    out var targetHero,
                    out var targetParty,
                    out var reason))
            {
                return Reject(context.Header, request, playerHero?.Gold ?? 0, reason);
            }

            playerContext = new BarterPlayerContext(playerHero, playerParty.MobileParty);
            if (!TryBuildPeaceBarter(playerHero, playerParty, targetHero, targetParty, request.Terms, out var barterData, out reason))
            {
                return Reject(context.Header, request, playerHero.Gold, reason);
            }

            var barterManager = BarterManager.Instance;
            if (barterManager == null)
            {
                return Reject(context.Header, request, playerHero.Gold, "The peace offer is not acceptable.");
            }

            var offeredBarterables = barterData.GetOfferedBarterables();
            var offerValue = barterManager.GetOfferValueForFaction(barterData, targetHero.Clan);
            if (offerValue < -0.01f)
            {
                return Reject(context.Header, request, playerHero.Gold, "The peace offer is not acceptable.");
            }

            // All requested terms, participant identities, and serializable state are checked before
            // MakePeaceAction. Once it starts, no failure is safely retryable.
            if (!CanPublishCanonicalDelta(playerHero, playerParty, targetHero, targetParty, offeredBarterables, out reason))
                return Reject(context.Header, request, playerHero.Gold, reason);

            mutationStarted = true;
            MakePeaceAction.Apply(playerHero.MapFaction, targetHero.MapFaction);
            if (!IsPeaceApplied(playerHero, targetHero))
                return IsolateAfterMutation(context, request, "peace-postcondition-missing");

            foreach (var barterable in offeredBarterables)
            {
                if (!(barterable is PeaceBarterable))
                    barterable.Apply();
            }
            CampaignEventDispatcher.Instance.OnBarterAccepted(playerHero, targetHero, offeredBarterables);
            ApplyOverpayRelationBonus(playerHero, targetHero, MathF.Max(0f, offerValue));

            if ((PeaceConversationContext)request.Context == PeaceConversationContext.MapParty)
                ConversationPartyHold.EndEngagement(conversationPartyTracker, context.Peer);
            FlushHeroGold(playerHero);
            FlushHeroGold(targetHero);
            if (!PublishCanonicalDelta(context.Header, playerHero, playerParty, targetHero, targetParty,
                    offeredBarterables, (PeaceConversationContext)request.Context == PeaceConversationContext.MapParty))
                return IsolateAfterMutation(context, request, "peace-publication-failed");

            return Accept(context.Header, request, playerHero.Gold);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed to apply an authoritative peace barter");
            if (mutationStarted)
                return IsolateAfterMutation(context, request, "peace-ambiguous", exception);

            return Reject(context.Header, request, playerHero?.Gold ?? 0, "The server could not process the peace offer.");
        }
        finally
        {
            try { playerContext?.Dispose(); }
            catch (Exception exception) { Logger.Error(exception, "Failed to restore peace barter player context"); }
        }
    }

    private bool TryResolveContext(
        NetPeer peer,
        NetworkRequestPeaceBarter request,
        out Hero playerHero,
        out PartyBase playerParty,
        out Hero targetHero,
        out PartyBase targetParty,
        out string reason)
    {
        playerHero = null;
        playerParty = null;
        targetHero = null;
        targetParty = null;
        reason = null;

        if (!Enum.IsDefined(typeof(PeaceConversationContext), request.Context) ||
            !playerManager.TryGetPlayer(peer, out Player player) ||
            !objectManager.TryGetObject(player.HeroId, out playerHero) ||
            !objectManager.TryGetObject(player.MobilePartyId, out MobileParty playerMobileParty) ||
            !objectManager.TryGetObject(request.TargetHeroId, out targetHero))
        {
            reason = "The server could not identify the peace participants.";
            return false;
        }

        playerParty = playerMobileParty.Party;
        targetParty = targetHero.PartyBelongedTo?.Party;
        if (playerMobileParty?.IsActive != true ||
            playerMobileParty.LeaderHero != playerHero)
        {
            reason = "The encounter state changed before the peace barter completed.";
            return false;
        }

        var context = (PeaceConversationContext)request.Context;
        if (context == PeaceConversationContext.MapParty)
        {
            if (!objectManager.TryGetObject(request.ContextId, out PartyBase targetPartyBase) ||
                targetPartyBase.MobileParty?.IsActive != true ||
                targetPartyBase.LeaderHero != targetHero ||
                playerMobileParty.MapEvent != null ||
                targetPartyBase.MobileParty.MapEvent != null ||
                !objectManager.TryGetId(playerParty, out var playerPartyId) ||
                !conversationPartyTracker.TryGetEngagement(peer, out var engagement) ||
                engagement.PartyId != request.ContextId ||
                engagement.EngagerPartyId != playerPartyId)
            {
                reason = "The peace encounter is no longer active.";
                return false;
            }

            targetParty = targetPartyBase;
        }
        else if (context == PeaceConversationContext.Location)
        {
            if (targetHero.CharacterObject == null ||
                !objectManager.TryGetId(targetHero.CharacterObject, out var characterId) ||
                !locationConversationTracker.TryGetEngagement(peer, out var npcKey) ||
                npcKey != LocationConversationTracker.ComposeKey(request.ContextId, characterId))
            {
                reason = "The peace conversation is no longer active.";
                return false;
            }
        }
        else if (context == PeaceConversationContext.Settlement)
        {
            if (!objectManager.TryGetObject(request.ContextId, out Settlement settlement) ||
                playerMobileParty.CurrentSettlement != settlement ||
                targetHero.CurrentSettlement != settlement)
            {
                reason = "The peace settlement conversation is no longer active.";
                return false;
            }
        }
        else
        {
            reason = "The peace conversation context is not supported.";
            return false;
        }

        if (!CanNegotiatePeace(playerHero, targetHero, out reason))
            return false;

        return true;
    }

    private static bool CanNegotiatePeace(Hero playerHero, Hero targetHero, out string reason)
    {
        reason = null;
        var playerClan = playerHero?.Clan;
        var targetClan = targetHero?.Clan;
        var playerFaction = playerHero?.MapFaction;
        var targetFaction = targetHero?.MapFaction;

        if (playerClan == null || targetClan == null || playerFaction == null || targetFaction == null ||
            !FactionManager.IsAtWarAgainstFaction(playerFaction, targetFaction))
        {
            reason = "The factions are no longer eligible to negotiate peace.";
            return false;
        }

        if (targetHero.IsPrisoner ||
            targetClan.IsRebelClan ||
            targetClan.IsUnderMercenaryService ||
            (targetClan.IsMinorFaction && Campaign.Current.Models.DiplomacyModel.IsAtConstantWar(targetFaction, playerFaction)) ||
            (targetClan.Kingdom != null && playerClan.Kingdom != null))
        {
            reason = "That lord cannot negotiate this peace agreement.";
            return false;
        }

        return true;
    }

    private static bool IsPeaceApplied(Hero playerHero, Hero targetHero)
    {
        var playerFaction = playerHero?.MapFaction;
        var targetFaction = targetHero?.MapFaction;
        return playerFaction?.FactionsAtWarWith?.Contains(targetFaction) == false &&
               targetFaction?.FactionsAtWarWith?.Contains(playerFaction) == false;
    }

    private bool TryBuildPeaceBarter(
        Hero playerHero,
        PartyBase playerParty,
        Hero targetHero,
        PartyBase targetParty,
        PeaceBarterTerm[] terms,
        out BarterData barterData,
        out string reason)
    {
        var barterManager = BarterManager.Instance;
        barterData = null;
        reason = null;
        if (barterManager == null)
        {
            reason = "The server barter system is unavailable.";
            return false;
        }

        barterData = new BarterData(
            playerHero,
            targetHero,
            playerParty,
            targetParty,
            barterManager.InitializeMakePeaceBarterContext);

        var peaceBarterable = new PeaceBarterable(
            targetHero,
            playerHero.Clan.MapFaction,
            targetHero.MapFaction,
            CampaignTime.Years(1f));
        peaceBarterable.SetIsOffered(true);
        barterData.AddBarterable<OtherBarterGroup>(peaceBarterable, true);
        CampaignEventDispatcher.Instance.OnBarterablesRequested(barterData);

        return TryApplyRequestedTerms(
            playerHero,
            targetHero,
            barterData,
            terms ?? Array.Empty<PeaceBarterTerm>(),
            out reason);
    }

    private bool TryApplyRequestedTerms(
        Hero playerHero,
        Hero targetHero,
        BarterData barterData,
        PeaceBarterTerm[] terms,
        out string reason)
    {
        var usedBarterables = new HashSet<Barterable>();
        foreach (var term in terms)
        {
            if (!Enum.IsDefined(typeof(PeaceBarterTermType), term.Type) || term.Amount <= 0)
            {
                reason = "The peace offer contains an invalid term.";
                return false;
            }

            if (string.IsNullOrEmpty(term.OwnerHeroId))
            {
                reason = "The peace offer does not identify the owner of a barter term.";
                return false;
            }

            var termType = (PeaceBarterTermType)term.Type;
            var barterable = barterData.GetBarterables().FirstOrDefault(candidate =>
                (candidate.OriginalOwner == playerHero || candidate.OriginalOwner == targetHero) &&
                objectManager.TryGetId(candidate.OriginalOwner, out var ownerHeroId) &&
                ownerHeroId == term.OwnerHeroId &&
                MatchesTerm(candidate, termType, term));

            if (barterable == null || !usedBarterables.Add(barterable) || term.Amount > barterable.MaxAmount)
            {
                reason = "The peace offer no longer matches the server's available barter terms.";
                return false;
            }

            barterable.CurrentAmount = term.Amount;
            barterable.SetIsOffered(true);
        }

        reason = null;
        return true;
    }

    private bool MatchesTerm(Barterable barterable, PeaceBarterTermType type, PeaceBarterTerm term)
    {
        switch (type)
        {
            case PeaceBarterTermType.Gold:
                return barterable is GoldBarterable;
            case PeaceBarterTermType.Item:
                return barterable is ItemBarterable itemBarterable && MatchesItem(itemBarterable, term);
            case PeaceBarterTermType.Fief:
                return barterable is FiefBarterable fiefBarterable &&
                       objectManager.TryGetId(fiefBarterable.TargetSettlement, out var settlementId) &&
                       settlementId == term.ObjectId;
            case PeaceBarterTermType.TransferPrisoner:
                return barterable is TransferPrisonerBarterable prisonerBarterable &&
                       MatchesPrisoner(prisonerBarterable._prisonerCharacter, term);
            case PeaceBarterTermType.ReleasePrisoner:
                return barterable is SetPrisonerFreeBarterable releasedPrisoner &&
                       MatchesPrisoner(releasedPrisoner._prisonerCharacter, term);
            default:
                return false;
        }
    }

    private bool MatchesItem(ItemBarterable barterable, PeaceBarterTerm term)
    {
        var equipmentElement = barterable.ItemRosterElement.EquipmentElement;
        if (!objectManager.TryGetId(equipmentElement.Item, out var itemId) || itemId != term.ObjectId)
            return false;

        var modifier = equipmentElement.ItemModifier;
        if ((modifier == null) != term.ItemModifierNull) return false;
        if (modifier == null) return true;

        return objectManager.TryGetId(modifier, out var modifierId) && modifierId == term.ItemModifierId;
    }

    private bool MatchesPrisoner(Hero prisoner, PeaceBarterTerm term)
        => prisoner?.CharacterObject != null &&
           objectManager.TryGetId(prisoner.CharacterObject, out var prisonerId) &&
           prisonerId == term.ObjectId;

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

    private static string ValidateWireShape(NetworkRequestPeaceBarter request)
    {
        if (string.IsNullOrWhiteSpace(request.TargetHeroId) || request.TargetHeroId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.ContextId) || request.ContextId.Length > 256 ||
            !Enum.IsDefined(typeof(PeaceConversationContext), request.Context))
            return "invalid-peace-barter";
        if (request.Terms == null || request.Terms.Length > 128) return "invalid-peace-barter-terms";
        foreach (var term in request.Terms)
        {
            if (!Enum.IsDefined(typeof(PeaceBarterTermType), term.Type) || term.Amount <= 0 ||
                string.IsNullOrWhiteSpace(term.OwnerHeroId) || term.OwnerHeroId.Length > 256 ||
                (term.ObjectId?.Length ?? 0) > 256 || (term.ItemModifierId?.Length ?? 0) > 256)
                return "invalid-peace-barter-term";
        }
        return null;
    }

    private static string BuildCommandKey(NetworkRequestPeaceBarter request)
    {
        var terms = string.Join("|", (request.Terms ?? Array.Empty<PeaceBarterTerm>())
            .OrderBy(term => term.Type).ThenBy(term => term.OwnerHeroId, StringComparer.Ordinal)
            .ThenBy(term => term.ObjectId, StringComparer.Ordinal).ThenBy(term => term.ItemModifierId, StringComparer.Ordinal)
            .ThenBy(term => term.ItemModifierNull).ThenBy(term => term.Amount)
            .Select(term => string.Concat(term.Type, ":", term.OwnerHeroId, ":", term.ObjectId, ":",
                term.ItemModifierId, ":", term.ItemModifierNull, ":", term.Amount)));
        return string.Concat(request.TargetHeroId, ":", request.Context, ":", request.ContextId, ":", terms);
    }

    private static NetworkPeaceBarterResult CreateTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new NetworkPeaceBarterResult(null,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason), 0, null);

    private static bool IsExpectedResult(NetworkRequestPeaceBarter request, NetworkPeaceBarterResult result) =>
        result.Header.RequestId == request.Header.RequestId &&
        result.Header.CommittedRevision == request.Header.ExpectedRevision &&
        string.Equals(result.Header.SessionId, request.Header.SessionId, StringComparison.Ordinal) &&
        string.Equals(result.ContextId, request.ContextId, StringComparison.Ordinal) &&
        string.Equals(result.RequestId, request.RequestId, StringComparison.Ordinal);

    private void HandleDelta(MessagePayload<NetworkPeaceBarterDelta> payload)
    {
        if (ModInformation.IsServer || !configAuthority.IsTrustedServer(payload.Who) ||
            !configAuthority.TryGetCurrent(out ModConfigSnapshot config)) return;
        var delta = payload.What;
        if (delta.AuthorityRequestId <= 0 || delta.CommittedRevision != config.Revision ||
            !string.Equals(delta.SessionId, config.SessionId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(delta.PlayerHeroId) || string.IsNullOrWhiteSpace(delta.TargetHeroId) ||
            string.IsNullOrWhiteSpace(delta.PlayerFactionId) || string.IsNullOrWhiteSpace(delta.TargetFactionId)) return;
        receivedDeltas[DeltaKey(delta.SessionId, delta.AuthorityRequestId, delta.CommittedRevision)] = delta;
    }

    private AuthorityCommitProbeResult ProbeClientCommit(NetworkPeaceBarterResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted ||
            !configAuthority.TryGetCurrent(out ModConfigSnapshot config) ||
            config.Revision != result.Header.CommittedRevision || config.SessionId != result.Header.SessionId ||
            !receivedDeltas.TryGetValue(DeltaKey(result.Header.SessionId, result.Header.RequestId,
                result.Header.CommittedRevision), out var delta) ||
            !objectManager.TryGetObject(delta.PlayerHeroId, out Hero playerHero) ||
            !objectManager.TryGetObject(delta.TargetHeroId, out Hero targetHero) ||
            playerHero.Gold != delta.PlayerGold || targetHero.Gold != delta.TargetGold ||
            playerHero.GetRelation(targetHero) != delta.PlayerToTargetRelation ||
            playerHero.MapFaction == null || targetHero.MapFaction == null ||
            !objectManager.TryGetId(playerHero.MapFaction, out var playerFactionId) ||
            !objectManager.TryGetId(targetHero.MapFaction, out var targetFactionId) ||
            playerFactionId != delta.PlayerFactionId || targetFactionId != delta.TargetFactionId ||
            FactionManager.IsAtWarAgainstFaction(playerHero.MapFaction, targetHero.MapFaction))
            return AuthorityCommitProbeResult.Pending;

        if (!MatchesRoster(delta.PlayerPartyId, delta.PlayerItems, delta.PlayerPrisoners,
                delta.PlayerItemRosterHash, delta.PlayerPrisonRosterHash) ||
            !MatchesRoster(delta.TargetPartyId, delta.TargetItems, delta.TargetPrisoners,
                delta.TargetItemRosterHash, delta.TargetPrisonRosterHash) ||
            !MatchesFiefs(delta.Fiefs) || !MatchesPrisoners(delta.Prisoners))
            return AuthorityCommitProbeResult.Pending;
        return AuthorityCommitProbeResult.Applied;
    }

    private void PresentTerminalOutcome(AuthorityClientOutcome<NetworkPeaceBarterResult> outcome)
    {
        if (outcome.Applied)
        {
            receivedDeltas.Remove(DeltaKey(outcome.Result.Header.SessionId, outcome.Result.Header.RequestId,
                outcome.Result.Header.CommittedRevision));
            PeaceBarterPatch.CompleteRequest(outcome.Result, barterClientPresentation);
            return;
        }
        PeaceBarterPatch.CompleteFailedRequest(outcome.ReasonCode);
    }

    private bool CanPublishCanonicalDelta(Hero playerHero, PartyBase playerParty, Hero targetHero, PartyBase targetParty,
        IEnumerable<Barterable> offeredBarterables, out string reason)
    {
        reason = null;
        if (!objectManager.TryGetId(playerHero, out _) || !objectManager.TryGetId(targetHero, out _) ||
            !objectManager.TryGetId(playerHero.MapFaction, out _) || !objectManager.TryGetId(targetHero.MapFaction, out _) ||
            !TryPackRoster(playerParty, out _, out _, out _, out _, out _) ||
            !TryPackRoster(targetParty, out _, out _, out _, out _, out _) ||
            !TryPackFiefs(offeredBarterables, out _) || !TryPackPrisonerStates(offeredBarterables, out _))
        {
            reason = "peace-state-unavailable";
            return false;
        }
        return true;
    }

    private bool PublishCanonicalDelta(AuthorityRequestHeader header, Hero playerHero, PartyBase playerParty,
        Hero targetHero, PartyBase targetParty, IEnumerable<Barterable> offeredBarterables, bool engagementEnded)
    {
        // Generated roster/fief/prisoner updates must leave first. This envelope is the correlated
        // commit witness the requester probes before its local barter UI is released.
        FlushParty(playerParty);
        FlushParty(targetParty);
        if (!objectManager.TryGetId(playerHero, out var playerHeroId) || !objectManager.TryGetId(targetHero, out var targetHeroId) ||
            !objectManager.TryGetId(playerHero.MapFaction, out var playerFactionId) ||
            !objectManager.TryGetId(targetHero.MapFaction, out var targetFactionId) ||
            !TryPackRoster(playerParty, out var playerPartyId, out var playerItems, out var playerPrisoners,
                out var playerItemHash, out var playerPrisonHash) ||
            !TryPackRoster(targetParty, out var targetPartyId, out var targetItems, out var targetPrisoners,
                out var targetItemHash, out var targetPrisonHash) ||
            !TryPackFiefs(offeredBarterables, out var fiefs) || !TryPackPrisonerStates(offeredBarterables, out var prisoners))
            return false;

        network.SendAll(new NetworkPeaceBarterDelta(header, playerHeroId, targetHeroId, playerPartyId, targetPartyId,
            playerFactionId, targetFactionId, playerHero.Gold, targetHero.Gold, playerHero.GetRelation(targetHero),
            playerItems, playerPrisoners, targetItems, targetPrisoners, playerItemHash, playerPrisonHash,
            targetItemHash, targetPrisonHash, fiefs, prisoners, engagementEnded));
        return true;
    }

    private void FlushParty(PartyBase party)
    {
        if (sendCoalescer != null && party != null && objectManager.TryGetId(party, out var partyId))
            sendCoalescer.FlushInstance(partyId, network);
    }

    private bool TryPackRoster(PartyBase party, out string partyId, out ItemRosterElementData[] items,
        out TroopRosterElementData[] prisoners, out long itemHash, out long prisonerHash)
    {
        partyId = null;
        items = Array.Empty<ItemRosterElementData>();
        prisoners = Array.Empty<TroopRosterElementData>();
        itemHash = 0;
        prisonerHash = 0;
        if (party == null) return true;
        if (!objectManager.TryGetId(party, out partyId) || !TryPackItems(party.ItemRoster, out items) ||
            !TryPackPrisoners(party.PrisonRoster, out prisoners)) return false;
        itemHash = HashItems(items);
        prisonerHash = HashPrisoners(prisoners);
        return true;
    }

    private bool MatchesRoster(string partyId, ItemRosterElementData[] items, TroopRosterElementData[] prisoners,
        long itemHash, long prisonerHash)
    {
        if (string.IsNullOrEmpty(partyId)) return itemHash == 0 && prisonerHash == 0;
        if (!objectManager.TryGetObject(partyId, out PartyBase party) || !TryPackItems(party.ItemRoster, out var localItems) ||
            !TryPackPrisoners(party.PrisonRoster, out var localPrisoners)) return false;
        return HashItems(localItems) == itemHash && HashPrisoners(localPrisoners) == prisonerHash &&
            HashItems(items ?? Array.Empty<ItemRosterElementData>()) == itemHash &&
            HashPrisoners(prisoners ?? Array.Empty<TroopRosterElementData>()) == prisonerHash;
    }

    private bool TryPackItems(ItemRoster roster, out ItemRosterElementData[] data)
    {
        var packed = new List<ItemRosterElementData>();
        foreach (var element in roster)
        {
            if (element.Amount <= 0 || element.EquipmentElement.Item == null ||
                !objectManager.TryGetCatalogId(element.EquipmentElement.Item, out var itemId)) { data = null; return false; }
            string modifierId = null;
            var noModifier = element.EquipmentElement.ItemModifier == null;
            if (!noModifier && !objectManager.TryGetId(element.EquipmentElement.ItemModifier, out modifierId))
            { data = null; return false; }
            packed.Add(new ItemRosterElementData(new ItemObjectData(itemId, modifierId, noModifier), element.Amount));
        }
        data = packed.OrderBy(item => item.ItemObjectData.ItemObjectId, StringComparer.Ordinal)
            .ThenBy(item => item.ItemObjectData.ItemModifierId, StringComparer.Ordinal).ToArray();
        return true;
    }

    private bool TryPackPrisoners(TroopRoster roster, out TroopRosterElementData[] data)
    {
        var packed = new List<TroopRosterElementData>();
        foreach (var element in roster.GetTroopRoster())
        {
            if (element.Character == null || element.Number <= 0 ||
                !objectManager.TryGetId(element.Character, out var characterId)) { data = null; return false; }
            packed.Add(new TroopRosterElementData(characterId, element.Number, element.WoundedNumber, element.Xp));
        }
        data = packed.OrderBy(prisoner => prisoner.CharacterId, StringComparer.Ordinal).ToArray();
        return true;
    }

    private bool TryPackFiefs(IEnumerable<Barterable> barterables, out PeaceBarterFiefStateData[] data)
    {
        var packed = new List<PeaceBarterFiefStateData>();
        foreach (var fief in barterables.OfType<FiefBarterable>())
        {
            if (!objectManager.TryGetId(fief.TargetSettlement, out var settlementId) ||
                !objectManager.TryGetId(fief.TargetSettlement.OwnerClan, out var ownerClanId)) { data = null; return false; }
            packed.Add(new PeaceBarterFiefStateData(settlementId, ownerClanId));
        }
        data = packed.OrderBy(fief => fief.SettlementId, StringComparer.Ordinal).ToArray();
        return true;
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
            if (prisoner.PartyBelongedToAsPrisoner != null &&
                !objectManager.TryGetId(prisoner.PartyBelongedToAsPrisoner, out captorPartyId)) { data = null; return false; }
            packed.Add(new PeaceBarterPrisonerStateData(characterId, prisoner.IsPrisoner, captorPartyId));
        }
        data = packed.OrderBy(prisoner => prisoner.HeroId, StringComparer.Ordinal).ToArray();
        return true;
    }

    private bool MatchesFiefs(IEnumerable<PeaceBarterFiefStateData> fiefs)
    {
        foreach (var fief in fiefs ?? Array.Empty<PeaceBarterFiefStateData>())
        {
            if (!objectManager.TryGetObject(fief.SettlementId, out Settlement settlement) ||
                !objectManager.TryGetId(settlement.OwnerClan, out var ownerClanId) || ownerClanId != fief.OwnerClanId)
                return false;
        }
        return true;
    }

    private bool MatchesPrisoners(IEnumerable<PeaceBarterPrisonerStateData> prisoners)
    {
        foreach (var prisoner in prisoners ?? Array.Empty<PeaceBarterPrisonerStateData>())
        {
            if (!objectManager.TryGetObject(prisoner.HeroId, out CharacterObject character) || character.HeroObject == null ||
                character.HeroObject.IsPrisoner != prisoner.IsPrisoner) return false;
            string captorPartyId = null;
            if (character.HeroObject.PartyBelongedToAsPrisoner != null &&
                !objectManager.TryGetId(character.HeroObject.PartyBelongedToAsPrisoner, out captorPartyId)) return false;
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

    private static long Hash(long value, string text)
    {
        unchecked { foreach (var character in text ?? string.Empty) value = (value ^ character) * 1099511628211L; return value; }
    }

    private static long Hash(long value, int number) => unchecked((value ^ number) * 1099511628211L);

    private static string DeltaKey(string sessionId, long requestId, long revision) =>
        string.Concat(sessionId, ":", requestId, ":", revision);

    private AuthorityServerReply<NetworkPeaceBarterResult> IsolateAfterMutation(
        AuthorityServerContext context, NetworkRequestPeaceBarter request, string stage, Exception exception = null)
    {
        if (exception == null) Logger.Fatal("Peace barter ambiguity after mutation. Stage={Stage}", stage);
        else Logger.Fatal(exception, "Peace barter ambiguity after mutation. Stage={Stage}", stage);
        // A failed broadcast can leave every current campaign recipient with a different peace/fief/
        // roster view. They must rejoin from a clean campaign snapshot; the route keeps the replay
        // record but suppresses its terminal reply so the requester cannot retry an ambiguous commit.
        foreach (var audience in playerManager.Players)
        {
            try { if (playerManager.TryGetPeer(audience.ControllerId, out var peer)) peer.Disconnect(); }
            catch { }
        }
        try { context.Peer.Disconnect(); } catch { }
        return new AuthorityServerReply<NetworkPeaceBarterResult>(
            new NetworkPeaceBarterResult(request.ContextId,
                new AuthorityResultHeader(context.Header.SessionId, context.Header.RequestId,
                    AuthorityResultStatus.ExecutionFailed, context.Header.ExpectedRevision, "peace-isolated"),
                0, request.RequestId), false, suppressReply: true);
    }

    private static AuthorityServerReply<NetworkPeaceBarterResult> Accept(
        AuthorityRequestHeader header, NetworkRequestPeaceBarter request, int playerGold) =>
        new(new NetworkPeaceBarterResult(request.ContextId,
            new AuthorityResultHeader(header.SessionId, header.RequestId, AuthorityResultStatus.Accepted,
                header.ExpectedRevision, null), playerGold, request.RequestId), statePublished: true);

    private static AuthorityServerReply<NetworkPeaceBarterResult> Reject(
        AuthorityRequestHeader header, NetworkRequestPeaceBarter request, int playerGold, string reason)
    {
        Logger.Warning("Rejected peace barter for {ContextId}: {Reason}", request.ContextId, reason);
        return new AuthorityServerReply<NetworkPeaceBarterResult>(new NetworkPeaceBarterResult(request.ContextId,
            new AuthorityResultHeader(header.SessionId, header.RequestId, AuthorityResultStatus.Rejected,
                header.ExpectedRevision, reason), playerGold, request.RequestId), statePublished: false);
    }
}

internal readonly struct PeaceBarterIntent
{
    public PeaceBarterIntent(string targetHeroId, PeaceConversationContext context, string contextId,
        PeaceBarterTerm[] terms, string clientRequestId)
    {
        TargetHeroId = targetHeroId;
        Context = context;
        ContextId = contextId;
        Terms = terms ?? Array.Empty<PeaceBarterTerm>();
        ClientRequestId = clientRequestId;
    }

    public string TargetHeroId { get; }
    public PeaceConversationContext Context { get; }
    public string ContextId { get; }
    public PeaceBarterTerm[] Terms { get; }
    public string ClientRequestId { get; }
}
