using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Clans.Messages;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.Kingdoms.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using Helpers;
using LiteNetLib;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace GameInterface.Services.Clans.Handlers;

internal class VassalServiceHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<VassalServiceHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IKingdomMembershipState kingdomMembershipState;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly IPlayerManager playerManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<VassalJoinIntent, VassalServiceResult> joinRoute;
    private readonly IAuthorityRouteHandle<string, VassalServiceLeaveResult> leaveRoute;
    private readonly IAuthorityRouteHandle<string, NetworkStartRebellionResult> rebellionRoute;

    public VassalServiceHandler(
        IMessageBroker messageBroker,
        IKingdomMembershipState kingdomMembershipState,
        IObjectManager objectManager,
        INetwork network,
        IPlayerManager playerManager,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.kingdomMembershipState = kingdomMembershipState;
        this.objectManager = objectManager;
        this.network = network;
        this.playerManager = playerManager;
        this.configAuthority = configAuthority;
        joinRoute = authorityRequestRouter.Register(
            AuthorityRoute<VassalJoinIntent, RequestVassalService, VassalServiceResult>.Define(
                "clan.vassal.join", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new RequestVassalService(intent.KingdomId, intent.GrantRewards, header),
                request => request.Header, result => result.Header,
                request => string.IsNullOrWhiteSpace(request.KingdomId) ? "vassal-kingdom-missing" : null,
                request => request.KingdomId + ":" + request.GrantRewards,
                ValidateHeader, ExecuteJoin, CreateJoinTerminal, ProbeJoin, _ => { }, PresentJoin,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                isExpectedClientResult: (request, result) => request.KingdomId == result.KingdomId));
        leaveRoute = authorityRequestRouter.Register(
            AuthorityRoute<string, RequestLeaveVassalService, VassalServiceLeaveResult>.Define(
                "clan.vassal.leave", AuthorityRouteKind.Command, CreateHeader,
                (clanId, header) => new RequestLeaveVassalService(clanId, header),
                request => request.Header, result => result.Header,
                request => string.IsNullOrWhiteSpace(request.ClanId) ? "vassal-clan-missing" : null,
                request => request.ClanId, ValidateHeader, ExecuteLeave, CreateLeaveTerminal, ProbeLeave,
                _ => { }, PresentLeave, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                isExpectedClientResult: (request, result) => request.ClanId == result.ClanId));
        rebellionRoute = authorityRequestRouter.Register(
            AuthorityRoute<string, NetworkStartRebellion, NetworkStartRebellionResult>.Define(
                "kingdom.rebel", AuthorityRouteKind.Command, CreateHeader,
                (clanId, header) => new NetworkStartRebellion(clanId, header),
                request => request.Header, result => result.Header,
                request => string.IsNullOrWhiteSpace(request.ClanId) ? "rebellion-clan-missing" : null,
                request => request.ClanId, ValidateHeader, ExecuteRebellion, CreateRebellionTerminal, ProbeRebellion,
                _ => { }, PresentRebellion, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                isExpectedClientResult: (request, result) => request.ClanId == result.ClanId));

        messageBroker.Subscribe<VassalServiceAccepted>(HandleVassalServiceAccepted);
        messageBroker.Subscribe<VassalServiceLeft>(HandleVassalServiceLeft);
        messageBroker.Subscribe<StartRebellion>(HandleStartRebellion);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<VassalServiceAccepted>(HandleVassalServiceAccepted);
        messageBroker.Unsubscribe<VassalServiceLeft>(HandleVassalServiceLeft);
        messageBroker.Unsubscribe<StartRebellion>(HandleStartRebellion);
        joinRoute.Dispose();
        leaveRoute.Dispose();
        rebellionRoute.Dispose();
    }

    private void HandleVassalServiceAccepted(MessagePayload<VassalServiceAccepted> payload)
    {
        if (ModInformation.IsServer) return;
        if (!objectManager.TryGetIdWithLogging(payload.What.Kingdom, out var kingdomId)) return;

        joinRoute.Submit(new VassalJoinIntent(kingdomId, payload.What.GrantRewards));
    }

    private bool ApplyVassalage(string clanId, string heroId, string kingdomId, bool grantRewards)
    {
        if (!objectManager.TryGetObjectWithLogging<Clan>(clanId, out var clan)) return false;
        if (!objectManager.TryGetObjectWithLogging<Hero>(heroId, out var hero)) return false;
        if (!objectManager.TryGetObjectWithLogging<Kingdom>(kingdomId, out var kingdom)) return false;

        if (hero.Clan != clan || clan.Tier < Campaign.Current.Models.ClanTierModel.VassalEligibleTier)
        {
            Logger.Warning("Rejected vassal service request for clan {ClanId} because it is no longer eligible", clanId);
            return false;
        }

        Kingdom previousKingdom = clan.Kingdom;

        if (clan.Kingdom == kingdom)
        {
            if (!clan.IsUnderMercenaryService)
            {
                Logger.Warning("Rejected vassal service request because clan {ClanId} already belongs to kingdom {KingdomId}", clanId, kingdomId);
                return false;
            }

            EndMercenaryServiceAction.EndByBecomingVassal(clan);
        }
        else
        {
            if (clan.Kingdom != null)
            {
                if (!clan.IsUnderMercenaryService)
                {
                    Logger.Warning("Rejected vassal service request because clan {ClanId} already belongs to another kingdom", clanId);
                    return false;
                }

                EndMercenaryServiceAction.EndByLeavingKingdom(clan);
            }

            ChangeKingdomAction.ApplyByJoinToKingdom(clan, kingdom);
        }

        if (clan.Kingdom != kingdom || clan.IsUnderMercenaryService)
        {
            Logger.Error("Vassal service did not place clan {ClanId} in kingdom {KingdomId}", clanId, kingdomId);
            return false;
        }

        kingdomMembershipState.MoveClanToKingdom(
            previousKingdom,
            kingdom,
            clan,
            publishCollectionChanges: true,
            republishExistingCollections: true);
        if (!kingdom.Clans.Contains(clan))
        {
            Logger.Error("Vassal service did not add clan {ClanId} to kingdom {KingdomId} collections", clanId, kingdomId);
            return false;
        }

        var rewardsModel = Campaign.Current.Models.VassalRewardsModel;
        if (grantRewards && kingdom.Leader != null)
        {
            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(
                hero,
                kingdom.Leader,
                rewardsModel.RelationRewardWithLeader);
        }

        GainKingdomInfluenceAction.ApplyForJoiningFaction(hero, rewardsModel.InfluenceReward);
        return true;
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
        if (!string.Equals(header.SessionId, current.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session");
        if (header.ExpectedRevision != current.Revision)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
        return AuthorityHeaderValidation.Valid;
    }

    private AuthorityServerReply<VassalServiceResult> ExecuteJoin(
        AuthorityServerContext context, RequestVassalService request)
    {
        if (!TryGetAuthoritativeClan(context, out var clan, out var reason))
            return JoinReply(context.Header, request, AuthorityResultStatus.Unauthorized, reason);

        bool accepted = ApplyVassalage(clan.StringId, context.Player.HeroId, request.KingdomId, request.GrantRewards);
        return accepted
            ? JoinReply(context.Header, request, AuthorityResultStatus.Accepted, null)
            : JoinReply(context.Header, request, AuthorityResultStatus.Rejected, "vassal-ineligible");
    }

    private AuthorityServerReply<VassalServiceLeaveResult> ExecuteLeave(
        AuthorityServerContext context, RequestLeaveVassalService request)
    {
        if (!TryGetAuthoritativeClan(context, out var clan, out var reason) ||
            !string.Equals(request.ClanId, clan.StringId, StringComparison.Ordinal))
            return LeaveReply(context.Header, request.ClanId, AuthorityResultStatus.Unauthorized, reason ?? "vassal-clan-mismatch");
        if (clan.Kingdom == null || clan.IsUnderMercenaryService)
            return LeaveReply(context.Header, clan.StringId, AuthorityResultStatus.Rejected, "vassal-not-active");

        Kingdom previousKingdom = clan.Kingdom;
        ChangeKingdomAction.ApplyByLeaveKingdom(clan, true);
        kingdomMembershipState.MoveClanToKingdom(previousKingdom, null, clan, publishCollectionChanges: true,
            republishExistingCollections: true);
        return clan.Kingdom == null
            ? LeaveReply(context.Header, clan.StringId, AuthorityResultStatus.Accepted, null)
            : LeaveReply(context.Header, clan.StringId, AuthorityResultStatus.ExecutionFailed, "vassal-leave-not-committed");
    }

    private AuthorityServerReply<NetworkStartRebellionResult> ExecuteRebellion(
        AuthorityServerContext context, NetworkStartRebellion request)
    {
        if (!TryGetAuthoritativeClan(context, out var clan, out var reason) ||
            !string.Equals(request.ClanId, clan.StringId, StringComparison.Ordinal))
            return RebellionReply(context.Header, request.ClanId, AuthorityResultStatus.Unauthorized, reason ?? "rebellion-clan-mismatch");
        if (clan.Kingdom == null || clan.IsUnderMercenaryService)
            return RebellionReply(context.Header, clan.StringId, AuthorityResultStatus.Rejected, "rebellion-not-eligible");

        Kingdom previousKingdom = clan.Kingdom;
        ChangeKingdomAction.ApplyByLeaveWithRebellionAgainstKingdom(clan, true);
        kingdomMembershipState.MoveClanToKingdom(previousKingdom, null, clan, publishCollectionChanges: true,
            republishExistingCollections: true);
        return clan.Kingdom == null && previousKingdom.IsAtWarWith(clan)
            ? RebellionReply(context.Header, clan.StringId, AuthorityResultStatus.Accepted, null)
            : RebellionReply(context.Header, clan.StringId, AuthorityResultStatus.ExecutionFailed, "rebellion-not-committed");
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

    private AuthorityServerReply<VassalServiceResult> JoinReply(
        AuthorityRequestHeader header, RequestVassalService request, AuthorityResultStatus status, string reason) =>
        new(new VassalServiceResult(request.KingdomId, status == AuthorityResultStatus.Accepted,
            status == AuthorityResultStatus.Accepted && request.GrantRewards,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason)),
            status == AuthorityResultStatus.Accepted);

    private static VassalServiceResult CreateJoinTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, false, false, new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private static AuthorityServerReply<VassalServiceLeaveResult> LeaveReply(
        AuthorityRequestHeader header, string clanId, AuthorityResultStatus status, string reason) =>
        new(new VassalServiceLeaveResult(clanId,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason)),
            status == AuthorityResultStatus.Accepted);

    private static VassalServiceLeaveResult CreateLeaveTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private static AuthorityServerReply<NetworkStartRebellionResult> RebellionReply(
        AuthorityRequestHeader header, string clanId, AuthorityResultStatus status, string reason) =>
        new(new NetworkStartRebellionResult(clanId,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason)),
            status == AuthorityResultStatus.Accepted);

    private static NetworkStartRebellionResult CreateRebellionTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private static AuthorityCommitProbeResult ProbeJoin(VassalServiceResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted) return AuthorityCommitProbeResult.Applied;
        Clan clan = Clan.PlayerClan;
        return clan != null && clan.Kingdom != null && clan.Kingdom.StringId == result.KingdomId &&
               !clan.IsUnderMercenaryService
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;
    }

    private static AuthorityCommitProbeResult ProbeLeave(VassalServiceLeaveResult result) =>
        result.Header.Status != AuthorityResultStatus.Accepted || Clan.PlayerClan?.Kingdom == null
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;

    private static AuthorityCommitProbeResult ProbeRebellion(NetworkStartRebellionResult result) =>
        result.Header.Status != AuthorityResultStatus.Accepted || Clan.PlayerClan?.Kingdom == null
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;

    private void PresentJoin(AuthorityClientOutcome<VassalServiceResult> outcome)
    {
        if (!outcome.Applied)
        {
            MBInformationManager.AddQuickInformation(
                new TextObject("{=coop_vassalage_rejected}The vassalage agreement could not be completed."));
            return;
        }
        if (!outcome.Result.GrantRewards || !objectManager.TryGetObjectWithLogging<Kingdom>(outcome.Result.KingdomId, out var kingdom)) return;
        var behavior = Campaign.Current.GetCampaignBehavior<LordConversationsCampaignBehavior>();
        if (behavior == null || behavior._receivedVassalRewards) return;
        var rewardsModel = Campaign.Current.Models.VassalRewardsModel;
        InventoryScreenHelper.OpenScreenAsReceiveItems(rewardsModel.GetEquipmentRewardsForJoiningKingdom(kingdom),
            new TextObject("{=exbSCGzi}Reward Items"));
        PartyScreenHelper.OpenScreenAsReceiveTroops(rewardsModel.GetTroopRewardsForJoiningKingdom(kingdom),
            new TextObject("{=tKW8m6bZ}Reward Troops"));
        behavior._receivedVassalRewards = true;
    }

    private static void PresentLeave(AuthorityClientOutcome<VassalServiceLeaveResult> outcome)
    {
        if (!outcome.Applied)
            MBInformationManager.AddQuickInformation(new TextObject("{=coop_vassal_leave_rejected}Unable to leave vassal service."));
    }

    private static void PresentRebellion(AuthorityClientOutcome<NetworkStartRebellionResult> outcome)
    {
        if (!outcome.Applied)
            MBInformationManager.AddQuickInformation(new TextObject("{=coop_rebellion_rejected}Unable to start a rebellion."));
    }

    private void HandleVassalServiceLeft(MessagePayload<VassalServiceLeft> payload)
    {
        if (ModInformation.IsServer || !objectManager.TryGetIdWithLogging(payload.What.Clan, out var clanId)) return;
        leaveRoute.Submit(clanId);
    }

    private void HandleStartRebellion(MessagePayload<StartRebellion> payload)
    {
        if (ModInformation.IsServer || !objectManager.TryGetIdWithLogging(payload.What.Clan, out var clanId)) return;
        rebellionRoute.Submit(clanId);
    }

    private readonly struct VassalJoinIntent
    {
        public VassalJoinIntent(string kingdomId, bool grantRewards)
        {
            KingdomId = kingdomId;
            GrantRewards = grantRewards;
        }

        public string KingdomId { get; }
        public bool GrantRewards { get; }
    }
}
