using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Bandits.Messages;
using GameInterface.Services.Bandits.Patches;
using GameInterface.Services.Barters;
using GameInterface.Services.Inventory.Data;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MobileParties.Interfaces;
using GameInterface.Services.MobilePartyAIs.Patches;
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
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace GameInterface.Services.Bandits.Handlers;

internal sealed class BanditBarterHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<BanditBarterHandler>();
    private static BanditBarterHandler instance;

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly IPlayerManager playerManager;
    private readonly ConversationPartyTracker conversationPartyTracker;
    private readonly ISessionInteractionsPlayerDataInterface interactions;
    private readonly IBarterClientPresentation barterClientPresentation;
    private readonly ISafePassagePartyResolver safePassagePartyResolver;
    private readonly ISendCoalescer sendCoalescer;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<BanditBarterIntent, NetworkBanditBarterResult> safePassageRoute;
    private readonly Dictionary<string, NetworkBanditSafePassageDelta> receivedDeltas =
        new Dictionary<string, NetworkBanditSafePassageDelta>(StringComparer.Ordinal);

    public BanditBarterHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network,
        IPlayerManager playerManager,
        ConversationPartyTracker conversationPartyTracker,
        ISessionInteractionsPlayerDataInterface interactions,
        IBarterClientPresentation barterClientPresentation,
        ISafePassagePartyResolver safePassagePartyResolver,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter,
        ISendCoalescer sendCoalescer = null)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.playerManager = playerManager;
        this.conversationPartyTracker = conversationPartyTracker;
        this.interactions = interactions;
        this.barterClientPresentation = barterClientPresentation;
        this.safePassagePartyResolver = safePassagePartyResolver;
        this.sendCoalescer = sendCoalescer;
        this.configAuthority = configAuthority;
        instance = this;

        safePassageRoute = authorityRequestRouter.Register(
            AuthorityRoute<BanditBarterIntent, NetworkRequestBanditBarter, NetworkBanditBarterResult>.Define(
                "barter.bandit.safe-passage", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestBanditBarter(intent.BanditPartyId, intent.PlayerGold,
                    intent.PlayerItems, intent.PlayerPrisoners, header),
                request => request.Header, result => result.Header, ValidateWireShape, BuildCommandKey,
                ValidateHeader, ExecuteSafePassage, CreateTerminalResult, ProbeClientCommit, _ => { },
                PresentTerminalOutcome, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedResult));

        messageBroker.Subscribe<NetworkBanditSafePassageDelta>(HandleDelta);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkBanditSafePassageDelta>(HandleDelta);
        safePassageRoute.Dispose();
        if (instance == this) instance = null;
        BanditBarterPatch.ClearPendingRequest();
    }

    internal static bool TrySubmit(BanditBarterIntent intent)
    {
        if (instance == null || ModInformation.IsServer) return false;
        instance.safePassageRoute.Submit(intent);
        return true;
    }

    private AuthorityServerReply<NetworkBanditBarterResult> ExecuteSafePassage(
        AuthorityServerContext context, NetworkRequestBanditBarter request)
    {
        Hero playerHero = null;
        var mutationApplied = false;
        var published = false;
        try
        {
            if (!TryResolveParties(context.Peer, request.BanditPartyId, out var player, out playerHero, out var playerParty, out var banditParty, out var reason) ||
                !string.Equals(player.HeroId, context.Player.HeroId, StringComparison.Ordinal) ||
                !HasActiveEngagement(context.Peer, playerParty, banditParty, out reason) ||
                !TryValidateOffer(request, playerHero, playerParty, banditParty, out var offer, out reason) ||
                !CanPublishCanonicalDelta(playerParty, banditParty, out reason))
            {
                return Reject(context.Header, request, playerHero?.Gold ?? 0, reason);
            }

            // The first protection change is irreversible from this request's perspective. Any failure after
            // this point is ambiguous, so its replay entry is retained with the reply suppressed and the
            // requester is isolated instead of manufacturing a retryable rejection.
            mutationApplied = true;
            var protectionUntil = CampaignTime.HoursFromNow(32);
            foreach (var protectedParty in offer.EnemyParties)
            {
                DefaultMobilePartyAIModelPatches.PreventAttacksUntil(
                    protectedParty,
                    playerParty,
                    protectionUntil);
                protectedParty.SetMoveModeHold();
                protectedParty.Ai.SetInitiative(0f, 0.8f, 8f);
            }

            interactions.AddPlayerKeys(player.HeroId);
            interactions.SetPlayerBanditsInteraction(
                player.HeroId,
                request.BanditPartyId,
                BanditInteractionsCampaignBehavior.PlayerInteraction.PaidOffParty);

            ApplyOffer(playerHero, playerParty.Party, banditParty.Party, offer);
            CompleteAcceptedRequest(context.Peer, playerHero);
            published = PublishCanonicalDelta(context.Header, player, playerHero, playerParty, banditParty,
                offer.EnemyParties, protectionUntil);
            if (!published)
                return IsolateAfterMutation(context, request, "safe-passage-publication-failed");

            return Accept(context.Header, request, playerHero.Gold);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed to apply an authoritative bandit safe-passage barter");
            if (mutationApplied)
                return IsolateAfterMutation(context, request, published ? "safe-passage-ambiguous" : "safe-passage-publication", exception);

            return Reject(context.Header, request, playerHero?.Gold ?? 0, "safe-passage-failed");
        }
    }

    private bool TryResolveParties(
        NetPeer peer,
        string banditPartyId,
        out Player player,
        out Hero playerHero,
        out MobileParty playerParty,
        out MobileParty banditParty,
        out string reason)
    {
        player = null;
        playerHero = null;
        playerParty = null;
        banditParty = null;
        reason = null;

        if (!playerManager.TryGetPlayer(peer, out player) ||
            !objectManager.TryGetObject(player.HeroId, out playerHero) ||
            !objectManager.TryGetObject(player.MobilePartyId, out playerParty))
        {
            reason = "The server could not identify your party.";
            return false;
        }

        if (!objectManager.TryGetObject(banditPartyId, out banditParty) ||
            banditParty?.IsActive != true ||
            !banditParty.IsBandit)
        {
            reason = "The bandit party is no longer available.";
            return false;
        }

        if (playerParty?.IsActive != true || playerParty.LeaderHero != playerHero ||
            playerParty.MapEvent != null || banditParty.MapEvent != null)
        {
            reason = "The encounter state changed before the barter completed.";
            return false;
        }

        return true;
    }

    private bool HasActiveEngagement(
        NetPeer peer,
        MobileParty playerParty,
        MobileParty banditParty,
        out string reason)
    {
        reason = null;
        if (!objectManager.TryGetId(playerParty.Party, out var playerPartyId) ||
            !objectManager.TryGetId(banditParty.Party, out var banditPartyId) ||
            !conversationPartyTracker.TryGetEngagement(peer, out var engagement) ||
            engagement.PartyId != banditPartyId ||
            engagement.EngagerPartyId != playerPartyId ||
            !engagement.EngagerIsDefender)
        {
            reason = "The bandit encounter is no longer active.";
            return false;
        }

        return true;
    }

    private bool TryValidateOffer(
        NetworkRequestBanditBarter request,
        Hero playerHero,
        MobileParty playerParty,
        MobileParty banditParty,
        out ValidatedOffer offer,
        out string reason)
    {
        offer = null;
        reason = null;

        if (request.PlayerGold < 0 || request.PlayerGold > playerHero.Gold)
        {
            reason = "The gold in the bandit barter is no longer available.";
            return false;
        }

        if (!TryResolveItems(request.PlayerItems, playerParty.ItemRoster, out var playerItems) ||
            !TryResolvePrisoners(
                request.PlayerPrisoners,
                playerParty.PrisonRoster,
                banditParty,
                out var playerPrisoners))
        {
            reason = "An item or prisoner in the bandit barter is no longer available.";
            return false;
        }

        if (request.PlayerGold == 0 && playerItems.Count == 0 && playerPrisoners.Count == 0)
        {
            reason = "The safe-passage offer contains no payment.";
            return false;
        }

        var safePassageParties = safePassagePartyResolver.Resolve(
            playerParty,
            banditParty);
        offer = new ValidatedOffer(
            request.PlayerGold,
            playerItems,
            playerPrisoners,
            safePassageParties.OpponentSide);

        var offeredValue = GetOfferValueForBandits(playerHero, playerParty, banditParty, offer);
        var requiredValue = GetRequiredSafePassageValue(
            playerHero,
            playerParty,
            banditParty,
            safePassageParties.PlayerSide,
            safePassageParties.OpponentSide);
        if (offeredValue < requiredValue)
        {
            offer = null;
            reason = "The bandits will not accept such a small payment.";
            return false;
        }

        return true;
    }

    private static int GetOfferValueForBandits(
        Hero playerHero,
        MobileParty playerParty,
        MobileParty banditParty,
        ValidatedOffer offer)
    {
        long value = offer.PlayerGold;
        foreach (var item in offer.PlayerItems)
        {
            var averageValue = GetAverageNearbySettlementValue(item.EquipmentElement, playerParty);
            var barterable = new ItemBarterable(
                playerHero,
                null,
                playerParty.Party,
                banditParty.Party,
                new ItemRosterElement(item.EquipmentElement, item.Amount),
                averageValue);
            barterable.CurrentAmount = item.Amount;
            value += barterable.GetValueForFaction(banditParty.MapFaction);
        }

        foreach (var prisoner in offer.PlayerPrisoners)
            value += Campaign.Current.Models.RansomValueCalculationModel.PrisonerRansomValue(prisoner);

        return (int)MathF.Clamp(value, 0L, int.MaxValue);
    }

    private static int GetAverageNearbySettlementValue(
        EquipmentElement equipmentElement,
        MobileParty playerParty)
    {
        var nearbyTowns = Campaign.Current.AllTowns
            .OrderBy(town => town.Settlement.Position.ToVec2().DistanceSquared(playerParty.Position.ToVec2()))
            .Take(3)
            .ToArray();
        if (nearbyTowns.Length == 0)
            return equipmentElement.GetBaseValue();

        long total = 0;
        foreach (var town in nearbyTowns)
            total += town.GetItemPrice(equipmentElement, playerParty, true);
        return (int)MathF.Clamp(total / nearbyTowns.Length, 0L, int.MaxValue);
    }

    private static int GetRequiredSafePassageValue(
        Hero playerHero,
        MobileParty playerParty,
        MobileParty banditParty,
        IEnumerable<MobileParty> playerSide,
        IEnumerable<MobileParty> enemySide)
    {
        var strengthContext = playerParty.IsCurrentlyAtSea
            ? MapEvent.PowerCalculationContext.SeaBattle
            : MapEvent.PowerCalculationContext.PlainBattle;
        var playerStrength = playerSide.Sum(party =>
            party.Party.GetCustomStrength(BattleSideEnum.Defender, strengthContext));
        var enemyStrength = enemySide.Sum(party =>
            party.Party.GetCustomStrength(BattleSideEnum.Attacker, strengthContext));
        if (enemyStrength <= 0f)
            enemyStrength = 0.00001f;

        var strengthRatio = MathF.Clamp(playerStrength / enemyStrength, 0f, 1f);
        long totalWealth = playerHero.Gold;
        foreach (var item in playerParty.ItemRoster)
        {
            totalWealth += (long)item.EquipmentElement.Item.Value * item.Amount;
            if (totalWealth >= int.MaxValue)
            {
                totalWealth = int.MaxValue;
                break;
            }
        }
        var wealth = (float)Math.Max(0L, totalWealth);

        var wealthFactor = strengthRatio < 1f
            ? 0.05f + ((1f - strengthRatio) * 0.2f)
            : 0.1f;
        if (playerParty.MapEvent != null || playerParty.SiegeEvent != null)
            wealthFactor *= 1.2f;

        var relationFactor = banditParty.MapFaction?.Leader == null
            ? 1f
            : MathF.Clamp(
                (50f + banditParty.MapFaction.Leader.GetRelation(playerHero)) / 50f,
                0.05f,
                1.1f);
        var price = (int)((wealth * wealthFactor) + 1000f) / 8;
        if (playerHero.GetPerkValue(DefaultPerks.Roguery.SweetTalker) && !playerParty.IsCurrentlyAtSea)
            price += MathF.Round(price * DefaultPerks.Roguery.SweetTalker.PrimaryBonus);
        if (playerHero.GetPerkValue(DefaultPerks.Trade.MarketDealer))
            price += MathF.Round(price * DefaultPerks.Trade.MarketDealer.PrimaryBonus);

        return (int)(price / (relationFactor * relationFactor));
    }

    private bool TryResolveItems(
        ItemRosterElementData[] requestedItems,
        ItemRoster sourceRoster,
        out List<ItemTransfer> transfers)
    {
        transfers = new List<ItemTransfer>();
        var totals = new Dictionary<EquipmentElement, int>();

        foreach (var requestedItem in requestedItems ?? Array.Empty<ItemRosterElementData>())
        {
            if (requestedItem.Amount <= 0 || !TryResolveEquipmentElement(requestedItem.ItemObjectData, out var equipmentElement))
                return false;

            totals.TryGetValue(equipmentElement, out var current);
            var total = (long)current + requestedItem.Amount;
            if (total > int.MaxValue) return false;
            totals[equipmentElement] = (int)total;
        }

        foreach (var item in totals)
        {
            if (item.Key.GetBaseValue() <= 100 || GetItemAmount(sourceRoster, item.Key) < item.Value)
                return false;

            transfers.Add(new ItemTransfer(item.Key, item.Value));
        }

        return true;
    }

    private bool TryResolvePrisoners(
        TroopRosterElementData[] requestedPrisoners,
        TroopRoster sourceRoster,
        MobileParty banditParty,
        out List<CharacterObject> prisoners)
    {
        prisoners = new List<CharacterObject>();
        var seen = new HashSet<CharacterObject>();

        foreach (var requestedPrisoner in requestedPrisoners ?? Array.Empty<TroopRosterElementData>())
        {
            if (requestedPrisoner.Number != 1 ||
                !objectManager.TryGetObject(requestedPrisoner.CharacterId, out CharacterObject prisoner) ||
                 !prisoner.IsHero ||
                 !seen.Add(prisoner) ||
                 sourceRoster.GetElementNumber(prisoner) < 1 ||
                 prisoner.HeroObject.MapFaction == null ||
                 banditParty.MapFaction == null ||
                 !FactionManager.IsAtWarAgainstFaction(prisoner.HeroObject.MapFaction, banditParty.MapFaction))
            {
                return false;
            }

            prisoners.Add(prisoner);
        }

        return true;
    }

    private bool TryResolveEquipmentElement(ItemObjectData itemData, out EquipmentElement equipmentElement)
    {
        equipmentElement = default;
        if (!objectManager.TryGetObject(itemData.ItemObjectId, out ItemObject itemObject))
            return false;

        ItemModifier modifier = null;
        if (!itemData.ItemModifierNull && !objectManager.TryGetObject(itemData.ItemModifierId, out modifier))
            return false;

        equipmentElement = new EquipmentElement(itemObject, modifier);
        return true;
    }

    private static int GetItemAmount(ItemRoster roster, EquipmentElement equipmentElement)
    {
        foreach (var item in roster)
        {
            if (item.EquipmentElement.Equals(equipmentElement))
                return item.Amount;
        }

        return 0;
    }

    private static void ApplyOffer(Hero playerHero, PartyBase playerParty, PartyBase banditParty, ValidatedOffer offer)
    {
        if (offer.PlayerGold > 0)
            GiveGoldAction.ApplyForCharacterToParty(playerHero, banditParty, offer.PlayerGold, false);

        ApplyItems(playerParty, banditParty, offer.PlayerItems);
        ApplyPrisoners(playerParty, banditParty, offer.PlayerPrisoners);
    }

    private static void ApplyItems(PartyBase source, PartyBase destination, IEnumerable<ItemTransfer> transfers)
    {
        foreach (var transfer in transfers)
        {
            source.ItemRoster.AddToCounts(transfer.EquipmentElement, -transfer.Amount);
            destination.ItemRoster.AddToCounts(transfer.EquipmentElement, transfer.Amount);
        }
    }

    private static void ApplyPrisoners(PartyBase source, PartyBase destination, IEnumerable<CharacterObject> prisoners)
    {
        foreach (var prisoner in prisoners)
            TransferPrisonerAction.Apply(prisoner, source, destination);
    }

    private void FlushHeroGold(Hero hero)
    {
        if (sendCoalescer == null || !objectManager.TryGetId(hero, out var heroId)) return;

        sendCoalescer.FlushInstance(heroId, network);
    }

    private void CompleteAcceptedRequest(NetPeer peer, Hero playerHero)
    {
        try
        {
            ConversationPartyHold.EndEngagement(conversationPartyTracker, peer);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed to end an accepted bandit barter engagement");
        }

        try
        {
            FlushHeroGold(playerHero);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed to flush player gold after an accepted bandit barter");
        }
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

    private static string ValidateWireShape(NetworkRequestBanditBarter request)
    {
        if (string.IsNullOrWhiteSpace(request.BanditPartyId) || request.BanditPartyId.Length > 256 || request.PlayerGold < 0)
            return "invalid-safe-passage";
        if (request.PlayerItems == null || request.PlayerPrisoners == null || request.PlayerItems.Length > 512 || request.PlayerPrisoners.Length > 128)
            return "invalid-safe-passage-offer";
        return null;
    }

    private static string BuildCommandKey(NetworkRequestBanditBarter request) => string.Concat(
        request.BanditPartyId.Length, ":", request.BanditPartyId, ":", request.PlayerGold, ":",
        request.PlayerItems.Length, ":", request.PlayerPrisoners.Length);

    private static NetworkBanditBarterResult CreateTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new NetworkBanditBarterResult(null, new AuthorityResultHeader(header.SessionId, header.RequestId,
            status, header.ExpectedRevision, reason), 0);

    private static bool IsExpectedResult(NetworkRequestBanditBarter request, NetworkBanditBarterResult result) =>
        result.Header.RequestId == request.Header.RequestId &&
        result.Header.CommittedRevision == request.Header.ExpectedRevision &&
        string.Equals(result.Header.SessionId, request.Header.SessionId, StringComparison.Ordinal) &&
        string.Equals(result.BanditPartyId, request.BanditPartyId, StringComparison.Ordinal);

    private void HandleDelta(MessagePayload<NetworkBanditSafePassageDelta> payload)
    {
        if (ModInformation.IsServer || !configAuthority.IsTrustedServer(payload.Who) ||
            !configAuthority.TryGetCurrent(out ModConfigSnapshot config)) return;
        var delta = payload.What;
        if (delta.AuthorityRequestId <= 0 || delta.CommittedRevision != config.Revision ||
            !string.Equals(delta.SessionId, config.SessionId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(delta.PlayerPartyId) || string.IsNullOrWhiteSpace(delta.BanditPartyId)) return;
        receivedDeltas[DeltaKey(delta.SessionId, delta.AuthorityRequestId, delta.CommittedRevision)] = delta;
    }

    private AuthorityCommitProbeResult ProbeClientCommit(NetworkBanditBarterResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted ||
            !configAuthority.TryGetCurrent(out ModConfigSnapshot config) ||
            config.Revision != result.Header.CommittedRevision || config.SessionId != result.Header.SessionId)
            return AuthorityCommitProbeResult.Invalid;
        if (!receivedDeltas.TryGetValue(DeltaKey(result.Header.SessionId, result.Header.RequestId,
                result.Header.CommittedRevision), out var delta))
            return AuthorityCommitProbeResult.Pending;
        if (!objectManager.TryGetObject(delta.PlayerPartyId, out PartyBase playerParty) ||
            !objectManager.TryGetObject(delta.BanditPartyId, out PartyBase banditParty) ||
            playerParty.MobileParty?.LeaderHero?.Gold != delta.PlayerGold || banditParty.Gold != delta.BanditGold ||
            !TryPackItems(playerParty.ItemRoster, out var playerItems) ||
            !TryPackPrisoners(playerParty.PrisonRoster, out var playerPrisoners) ||
            !TryPackItems(banditParty.ItemRoster, out var banditItems) ||
            !TryPackPrisoners(banditParty.PrisonRoster, out var banditPrisoners))
            return AuthorityCommitProbeResult.Pending;
        return HashItems(playerItems) == delta.PlayerItemRosterHash &&
            HashPrisoners(playerPrisoners) == delta.PlayerPrisonRosterHash &&
            HashItems(banditItems) == delta.BanditItemRosterHash &&
            HashPrisoners(banditPrisoners) == delta.BanditPrisonRosterHash
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private void PresentTerminalOutcome(AuthorityClientOutcome<NetworkBanditBarterResult> outcome)
    {
        if (outcome.Applied)
        {
            receivedDeltas.Remove(DeltaKey(outcome.Result.Header.SessionId, outcome.Result.Header.RequestId,
                outcome.Result.Header.CommittedRevision));
            BanditBarterPatch.CompleteAcceptedRequest(outcome.Result, barterClientPresentation);
            return;
        }
        BanditBarterPatch.CompleteFailedRequest(outcome.ReasonCode);
    }

    private static string DeltaKey(string sessionId, long requestId, long revision) =>
        string.Concat(sessionId, ":", requestId, ":", revision);

    private AuthorityServerReply<NetworkBanditBarterResult> IsolateAfterMutation(
        AuthorityServerContext context, NetworkRequestBanditBarter request, string stage, Exception exception = null)
    {
        if (exception != null) Logger.Fatal(exception, "Bandit safe-passage ambiguity after mutation. Stage={Stage}", stage);
        else Logger.Fatal("Bandit safe-passage ambiguity after mutation. Stage={Stage}", stage);
        // SendAll has no per-recipient acknowledgement. If its correlated publication is ambiguous, every
        // connected campaign peer is an incomplete-publication audience and must rejoin from a clean snapshot.
        foreach (var audience in playerManager.Players)
        {
            try
            {
                if (playerManager.TryGetPeer(audience.ControllerId, out var audiencePeer)) audiencePeer.Disconnect();
            }
            catch { }
        }
        try { context.Peer.Disconnect(); } catch { }
        return new AuthorityServerReply<NetworkBanditBarterResult>(new NetworkBanditBarterResult(request.BanditPartyId,
            new AuthorityResultHeader(context.Header.SessionId, context.Header.RequestId, AuthorityResultStatus.ExecutionFailed,
                context.Header.ExpectedRevision, "safe-passage-isolated"), 0), false, suppressReply: true);
    }

    private bool PublishCanonicalDelta(AuthorityRequestHeader header, Player player, Hero playerHero,
        MobileParty playerParty, MobileParty banditParty, IEnumerable<MobileParty> enemyParties, CampaignTime protectedUntil)
    {
        // The generated roster mutations are flushed before this envelope.  The envelope is the authoritative
        // commit witness: its identities, full values and hashes are what the requester correlates before UI close.
        if (!objectManager.TryGetId(playerParty.Party, out var playerPartyId) ||
            !objectManager.TryGetId(banditParty.Party, out var banditPartyId)) return false;
        sendCoalescer?.FlushInstance(playerParty.Party.StringId, network);
        sendCoalescer?.FlushInstance(banditParty.Party.StringId, network);
        if (!TryPackItems(playerParty.ItemRoster, out var playerItems) ||
            !TryPackPrisoners(playerParty.PrisonRoster, out var playerPrisoners) ||
            !TryPackItems(banditParty.ItemRoster, out var banditItems) ||
            !TryPackPrisoners(banditParty.PrisonRoster, out var banditPrisoners)) return false;
        var protections = enemyParties.Select(party => objectManager.TryGetId(party.Party, out var id)
            ? new BanditSafePassageProtectionData(id, protectedUntil.NumTicks) : default).Where(x => x.EnemyPartyId != null).ToArray();
        network.SendAll(new NetworkBanditSafePassageDelta(header, playerPartyId, banditPartyId, playerHero.Gold,
            banditParty.Party.Gold, playerPartyId + ":items", playerPartyId + ":prisoners", banditPartyId + ":items",
            banditPartyId + ":prisoners", HashItems(playerItems), HashPrisoners(playerPrisoners),
            HashItems(banditItems), HashPrisoners(banditPrisoners), playerItems, playerPrisoners, banditItems, banditPrisoners,
            protections, new BanditSafePassageInteractionData(player.HeroId, banditPartyId,
                (int)BanditInteractionsCampaignBehavior.PlayerInteraction.PaidOffParty, true)));
        return true;
    }

    private bool CanPublishCanonicalDelta(MobileParty playerParty, MobileParty banditParty, out string reason)
    {
        reason = null;
        if (!TryPackItems(playerParty.ItemRoster, out _) || !TryPackPrisoners(playerParty.PrisonRoster, out _) ||
            !TryPackItems(banditParty.ItemRoster, out _) || !TryPackPrisoners(banditParty.PrisonRoster, out _))
        {
            reason = "safe-passage-state-unavailable";
            return false;
        }
        return true;
    }

    private bool TryPackItems(ItemRoster roster, out ItemRosterElementData[] data)
    {
        var packed = new List<ItemRosterElementData>();
        foreach (var element in roster)
        {
            if (element.Amount <= 0 || element.EquipmentElement.Item == null ||
                !objectManager.TryGetCatalogId(element.EquipmentElement.Item, out var itemId)) { data = null; return false; }
            string modifierId = null;
            bool noModifier = element.EquipmentElement.ItemModifier == null;
            if (!noModifier && !objectManager.TryGetId(element.EquipmentElement.ItemModifier, out modifierId))
            { data = null; return false; }
            packed.Add(new ItemRosterElementData(new ItemObjectData(itemId, modifierId, noModifier), element.Amount));
        }
        data = packed.OrderBy(x => x.ItemObjectData.ItemObjectId, StringComparer.Ordinal)
            .ThenBy(x => x.ItemObjectData.ItemModifierId, StringComparer.Ordinal).ToArray();
        return true;
    }

    private bool TryPackPrisoners(TroopRoster roster, out TroopRosterElementData[] data)
    {
        var packed = new List<TroopRosterElementData>();
        foreach (var element in roster)
        {
            if (element.Character == null || element.Number <= 0 ||
                !objectManager.TryGetId(element.Character, out var characterId)) { data = null; return false; }
            packed.Add(new TroopRosterElementData(characterId, element.Number, element.WoundedNumber, element.Xp));
        }
        data = packed.OrderBy(x => x.CharacterId, StringComparer.Ordinal).ToArray();
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
        unchecked { foreach (char c in text ?? string.Empty) value = (value ^ c) * 1099511628211L; return value; }
    }

    private static long Hash(long value, int number) => unchecked((value ^ number) * 1099511628211L);

    private static AuthorityServerReply<NetworkBanditBarterResult> Accept(
        AuthorityRequestHeader header, NetworkRequestBanditBarter request, int playerGold) =>
        new(new NetworkBanditBarterResult(request.BanditPartyId,
            new AuthorityResultHeader(header.SessionId, header.RequestId, AuthorityResultStatus.Accepted,
                header.ExpectedRevision, null), playerGold), statePublished: true);

    private static AuthorityServerReply<NetworkBanditBarterResult> Reject(
        AuthorityRequestHeader header, NetworkRequestBanditBarter request, int playerGold, string reason)
    {
        Logger.Warning("Rejected bandit barter for {BanditPartyId}: {Reason}", request.BanditPartyId, reason);
        return new AuthorityServerReply<NetworkBanditBarterResult>(new NetworkBanditBarterResult(request.BanditPartyId,
            new AuthorityResultHeader(header.SessionId, header.RequestId, AuthorityResultStatus.Rejected,
                header.ExpectedRevision, reason), playerGold), statePublished: false);
    }

    private sealed class ValidatedOffer
    {
        public int PlayerGold { get; }
        public List<ItemTransfer> PlayerItems { get; }
        public List<CharacterObject> PlayerPrisoners { get; }
        public List<MobileParty> EnemyParties { get; }

        public ValidatedOffer(
            int playerGold,
            List<ItemTransfer> playerItems,
            List<CharacterObject> playerPrisoners,
            List<MobileParty> enemyParties)
        {
            PlayerGold = playerGold;
            PlayerItems = playerItems;
            PlayerPrisoners = playerPrisoners;
            EnemyParties = enemyParties;
        }
    }

    private readonly struct ItemTransfer
    {
        public readonly EquipmentElement EquipmentElement;
        public readonly int Amount;

        public ItemTransfer(EquipmentElement equipmentElement, int amount)
        {
            EquipmentElement = equipmentElement;
            Amount = amount;
        }
    }
}

internal readonly struct BanditBarterIntent
{
    public BanditBarterIntent(string banditPartyId, int playerGold, ItemRosterElementData[] playerItems,
        TroopRosterElementData[] playerPrisoners)
    {
        BanditPartyId = banditPartyId;
        PlayerGold = playerGold;
        PlayerItems = playerItems ?? Array.Empty<ItemRosterElementData>();
        PlayerPrisoners = playerPrisoners ?? Array.Empty<TroopRosterElementData>();
    }

    public string BanditPartyId { get; }
    public int PlayerGold { get; }
    public ItemRosterElementData[] PlayerItems { get; }
    public TroopRosterElementData[] PlayerPrisoners { get; }
}
