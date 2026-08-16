using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Clans.Messages;
using GameInterface.Services.Heroes.Patches;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Kingdoms;
using LiteNetLib;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace GameInterface.Services.Clans.Handlers;

/// <summary>
/// Applies accepted mercenary contracts on the server and lets existing synchronization publish the results.
/// </summary>
internal class MercenaryServiceHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<MercenaryServiceHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly IPlayerManager playerManager;
    private readonly IKingdomMembershipState kingdomMembershipState;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<MercenaryJoinIntent, MercenaryServiceResult> joinRoute;
    private readonly IAuthorityRouteHandle<MercenaryLeaveIntent, MercenaryServiceResult> leaveRoute;

    public MercenaryServiceHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network,
        IPlayerManager playerManager,
        IKingdomMembershipState kingdomMembershipState,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.playerManager = playerManager;
        this.kingdomMembershipState = kingdomMembershipState;
        this.configAuthority = configAuthority;
        joinRoute = authorityRequestRouter.Register(
            AuthorityRoute<MercenaryJoinIntent, RequestMercenaryService, MercenaryServiceResult>.Define(
                "clan.mercenary.join", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new RequestMercenaryService(intent.KingdomId, intent.AwardMultiplier, intent.ClanId, header),
                request => request.Header, result => result.Header,
                request => string.IsNullOrWhiteSpace(request.KingdomId) || string.IsNullOrWhiteSpace(request.ClanId)
                    ? "mercenary-target-missing" : null,
                request => request.KingdomId + ":" + request.ClanId, ValidateHeader, ExecuteJoin, CreateTerminal,
                ProbeJoin, _ => { }, PresentJoin, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                isExpectedClientResult: (request, result) => request.KingdomId == result.KingdomId &&
                    request.ClanId == result.ClanId && result.IsUnderMercenaryService));
        leaveRoute = authorityRequestRouter.Register(
            AuthorityRoute<MercenaryLeaveIntent, RequestMercenaryDismissalService, MercenaryServiceResult>.Define(
                "clan.mercenary.leave", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new RequestMercenaryDismissalService(intent.KingdomId, intent.ClanId, header),
                request => request.Header, result => result.Header,
                request => string.IsNullOrWhiteSpace(request.KingdomId) || string.IsNullOrWhiteSpace(request.ClanId)
                    ? "mercenary-target-missing" : null,
                request => request.KingdomId + ":" + request.ClanId, ValidateHeader, ExecuteLeave, CreateTerminal,
                ProbeLeave, _ => { }, PresentLeave, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                isExpectedClientResult: (request, result) => request.KingdomId == result.KingdomId &&
                    request.ClanId == result.ClanId && !result.IsUnderMercenaryService));

        messageBroker.Subscribe<MercenaryServiceAccepted>(HandleMercenaryServiceAccepted);
        messageBroker.Subscribe<MercenaryServiceDismissalAccepted>(HandleMercenaryServiceDismissalAccepted);
        messageBroker.Subscribe<PlayerRelationChange>(HandlePlayerRelationChange);
        messageBroker.Subscribe<NetworkPlayerRelationChange>(HandleNetworkPlayerRelationChange);
        messageBroker.Subscribe<GiveGold>(HandleGiveGold);
        messageBroker.Subscribe<NetworkGiveGold>(HandleNetworkGiveGold);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<MercenaryServiceAccepted>(HandleMercenaryServiceAccepted);
        messageBroker.Unsubscribe<MercenaryServiceDismissalAccepted>(HandleMercenaryServiceDismissalAccepted);
        messageBroker.Unsubscribe<PlayerRelationChange>(HandlePlayerRelationChange);
        messageBroker.Unsubscribe<NetworkPlayerRelationChange>(HandleNetworkPlayerRelationChange);
        messageBroker.Unsubscribe<GiveGold>(HandleGiveGold);
        messageBroker.Unsubscribe<NetworkGiveGold>(HandleNetworkGiveGold);
        joinRoute.Dispose();
        leaveRoute.Dispose();
    }

    private void HandleMercenaryServiceAccepted(MessagePayload<MercenaryServiceAccepted> payload)
    {
        if (ModInformation.IsServer) return;
        // Conversation consequences publish synchronously on the game thread.
        if (!objectManager.TryGetIdWithLogging(payload.What.Kingdom, out var kingdomId)) return;
        if (!objectManager.TryGetIdWithLogging(payload.What.Clan, out var clanId)) return;

        joinRoute.Submit(new MercenaryJoinIntent(kingdomId, clanId, payload.What.AwardMultiplier));
    }

    private bool ApplyMercenaryService(string clanId, string heroId, string kingdomId)
    {
        // Only called from the GameThread.RunSafe action above.
        if (!objectManager.TryGetObjectWithLogging<Clan>(clanId, out var clan)) return false;
        if (!objectManager.TryGetObjectWithLogging<Hero>(heroId, out var hero)) return false;
        if (!objectManager.TryGetObjectWithLogging<Kingdom>(kingdomId, out var kingdom)) return false;
        if (!ReferenceEquals(hero.Clan, clan)) return false;

        if (clan.Kingdom != null || clan.IsUnderMercenaryService)
        {
            Logger.Warning("Rejected mercenary service request because clan {ClanId} already belongs to a kingdom", clanId);
            return false;
        }

        int awardMultiplier = Campaign.Current.Models.MinorFactionsModel
            .GetMercenaryAwardFactorToJoinKingdom(clan, kingdom, false);
        ChangeKingdomAction.ApplyByJoinFactionAsMercenary(clan, kingdom, default, awardMultiplier);
        kingdomMembershipState.MoveClanToKingdom(null, kingdom, clan, publishCollectionChanges: true,
            republishExistingCollections: true);
        if (clan == hero.Clan)
        {
            GainKingdomInfluenceAction.ApplyForJoiningFaction(hero, 5f);
        }
        return ReferenceEquals(clan.Kingdom, kingdom) && clan.IsUnderMercenaryService && kingdom.Clans.Contains(clan);
    }

    private void HandleMercenaryServiceDismissalAccepted(MessagePayload<MercenaryServiceDismissalAccepted> payload)
    {
        if (ModInformation.IsServer) return;
        if (!objectManager.TryGetIdWithLogging(payload.What.Kingdom, out var kingdomId)) return;
        if (!objectManager.TryGetIdWithLogging(payload.What.Clan, out var clanId)) return;

        leaveRoute.Submit(new MercenaryLeaveIntent(kingdomId, clanId));
    }

    private bool ApplyMercenaryDismissalService(string clanId, string heroId)
    {
        if (!objectManager.TryGetObjectWithLogging<Clan>(clanId, out var clan)) return false;
        if (!objectManager.TryGetObjectWithLogging<Hero>(heroId, out var hero)) return false;
        if (!ReferenceEquals(hero.Clan, clan)) return false;

        if (clan.Kingdom == null || !clan.IsUnderMercenaryService)
        {
            Logger.Warning("Rejected mercenary service removal request because clan {ClanId} does not belong to a kingdom", clanId);
            return false;
        }

        Kingdom previousKingdom = clan.Kingdom;
        ChangeClanInfluenceAction.Apply(clan, -hero.Clan.Influence);
        ChangeKingdomAction.ApplyByLeaveKingdomAsMercenary(clan, true);
        kingdomMembershipState.MoveClanToKingdom(previousKingdom, null, clan, publishCollectionChanges: true,
            republishExistingCollections: true);
        return clan.Kingdom == null && !clan.IsUnderMercenaryService;
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out var snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out var current))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        if (header.ProtocolVersion != current.ProtocolVersion)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.InvalidRequest, "unsupported-protocol");
        if (header.SessionId != current.SessionId)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session");
        if (header.ExpectedRevision != current.Revision)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
        return AuthorityHeaderValidation.Valid;
    }

    private AuthorityServerReply<MercenaryServiceResult> ExecuteJoin(
        AuthorityServerContext context, RequestMercenaryService request)
    {
        if (!TryGetAuthoritativeClan(context, out var clan, out var reason) ||
            !string.Equals(request.ClanId, clan.StringId, StringComparison.Ordinal))
            return Reply(context.Header, request.KingdomId, request.ClanId, false, AuthorityResultStatus.Unauthorized,
                reason ?? "mercenary-clan-mismatch");

        bool accepted = ApplyMercenaryService(clan.StringId, context.Player.HeroId, request.KingdomId);
        return Reply(context.Header, request.KingdomId, clan.StringId, accepted,
            accepted ? AuthorityResultStatus.Accepted : AuthorityResultStatus.Rejected,
            accepted ? null : "mercenary-ineligible");
    }

    private AuthorityServerReply<MercenaryServiceResult> ExecuteLeave(
        AuthorityServerContext context, RequestMercenaryDismissalService request)
    {
        if (!TryGetAuthoritativeClan(context, out var clan, out var reason) ||
            !string.Equals(request.ClanId, clan.StringId, StringComparison.Ordinal))
            return Reply(context.Header, request.KingdomId, request.ClanId, false, AuthorityResultStatus.Unauthorized,
                reason ?? "mercenary-clan-mismatch");
        if (clan.Kingdom == null || !clan.IsUnderMercenaryService || clan.Kingdom.StringId != request.KingdomId)
            return Reply(context.Header, request.KingdomId, clan.StringId, true, AuthorityResultStatus.Rejected,
                "mercenary-not-active");

        bool accepted = ApplyMercenaryDismissalService(clan.StringId, context.Player.HeroId);
        return Reply(context.Header, request.KingdomId, clan.StringId, false,
            accepted ? AuthorityResultStatus.Accepted : AuthorityResultStatus.ExecutionFailed,
            accepted ? null : "mercenary-leave-not-committed");
    }

    private bool TryGetAuthoritativeClan(AuthorityServerContext context, out Clan clan, out string reason)
    {
        clan = null;
        if (string.IsNullOrWhiteSpace(context.Player.ClanId) ||
            !objectManager.TryGetObject(context.Player.ClanId, out clan))
        {
            reason = "actor-clan-missing";
            return false;
        }
        if (string.IsNullOrWhiteSpace(context.Player.HeroId) ||
            !objectManager.TryGetObject(context.Player.HeroId, out Hero hero) || !ReferenceEquals(hero.Clan, clan))
        {
            reason = "actor-hero-clan-mismatch";
            return false;
        }
        reason = null;
        return true;
    }

    private static AuthorityServerReply<MercenaryServiceResult> Reply(AuthorityRequestHeader header,
        string kingdomId, string clanId, bool isMercenary, AuthorityResultStatus status, string reason) =>
        new(new MercenaryServiceResult(kingdomId, clanId, isMercenary,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason)),
            status == AuthorityResultStatus.Accepted);

    private static MercenaryServiceResult CreateTerminal(AuthorityRequestHeader header,
        AuthorityResultStatus status, string reason) =>
        new(null, null, false, new AuthorityResultHeader(header.SessionId, header.RequestId, status,
            header.ExpectedRevision, reason));

    private static AuthorityCommitProbeResult ProbeJoin(MercenaryServiceResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted) return AuthorityCommitProbeResult.Applied;
        Clan clan = Clan.PlayerClan;
        return clan != null && clan.IsUnderMercenaryService && clan.Kingdom?.StringId == result.KingdomId
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private static AuthorityCommitProbeResult ProbeLeave(MercenaryServiceResult result) =>
        result.Header.Status != AuthorityResultStatus.Accepted ||
        (Clan.PlayerClan?.Kingdom == null && Clan.PlayerClan?.IsUnderMercenaryService != true)
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;

    private static void PresentJoin(AuthorityClientOutcome<MercenaryServiceResult> outcome)
    {
        if (!outcome.Applied)
            MBInformationManager.AddQuickInformation(new TaleWorlds.Localization.TextObject(
                "{=coop_mercenary_rejected}The mercenary agreement could not be completed."));
    }

    private static void PresentLeave(AuthorityClientOutcome<MercenaryServiceResult> outcome)
    {
        if (!outcome.Applied)
            MBInformationManager.AddQuickInformation(new TaleWorlds.Localization.TextObject(
                "{=coop_mercenary_leave_rejected}Unable to leave mercenary service."));
    }

    private readonly struct MercenaryJoinIntent
    {
        public MercenaryJoinIntent(string kingdomId, string clanId, int awardMultiplier)
        {
            KingdomId = kingdomId;
            ClanId = clanId;
            AwardMultiplier = awardMultiplier;
        }

        public string KingdomId { get; }
        public string ClanId { get; }
        // The server recomputes this from campaign state; retaining it only preserves wire compatibility.
        public int AwardMultiplier { get; }
    }

    private readonly struct MercenaryLeaveIntent
    {
        public MercenaryLeaveIntent(string kingdomId, string clanId)
        {
            KingdomId = kingdomId;
            ClanId = clanId;
        }

        public string KingdomId { get; }
        public string ClanId { get; }
    }
    private void HandlePlayerRelationChange(MessagePayload<PlayerRelationChange> payload)
    {
        if (!objectManager.TryGetIdWithLogging(payload.What.Hero, out var heroId)) return;

        network.SendAll(new NetworkPlayerRelationChange(heroId, payload.What.Relation));
    }
    private void HandleNetworkPlayerRelationChange(MessagePayload<NetworkPlayerRelationChange> payload)
    {
        if (!(payload.Who is NetPeer peer) || !playerManager.TryGetPlayer(peer, out var player))
        {
            Logger.Error("Received {Message} without a registered player peer", nameof(RequestMercenaryDismissalService));
            return;
        }
        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Hero>(payload.What.HeroId, out var hero)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(player.HeroId, out var clientHero)) return;

            ResolvedMainHeroContext.ResolvedMainHero = clientHero;
            try
            {
                ChangeRelationAction.ApplyPlayerRelation(hero, payload.What.Relation, true, true);
            }
            finally
            {
                ResolvedMainHeroContext.ResolvedMainHero = null;
            }
        }); 
    }
    private void HandleGiveGold(MessagePayload<GiveGold> payload)
    {
        if (!objectManager.TryGetIdWithLogging(payload.What.Hero, out var heroId)) return;

        network.SendAll(new NetworkGiveGold(payload.What.Gold, heroId));
    }
    private void HandleNetworkGiveGold(MessagePayload<NetworkGiveGold> payload)
    {
        if (!(payload.Who is NetPeer peer) || !playerManager.TryGetPlayer(peer, out var player))
        {
            Logger.Error("Received {Message} without a registered player peer", nameof(NetworkGiveGold));
            return;
        }
        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Hero>(payload.What.HeroId, out var hero)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(player.HeroId, out var clientHero)) return;

            GiveGoldAction.ApplyBetweenCharacters(clientHero, hero, payload.What.Gold, false);
        });
    }
}
