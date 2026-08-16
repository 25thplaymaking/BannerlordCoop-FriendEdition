using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Clans.Messages;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.Clans.Handlers;

internal class ClanPartyItemVMHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ClanPartyItemVMHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly IPlayerManager playerManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<AutoRecruitIntent, AutoRecruitChangeResult> autoRecruitRoute;
    private readonly IAuthorityRouteHandle<ClanPartyBehaviorIntent, ClanPartyBehaviorChangeResult> behaviorRoute;

    public ClanPartyItemVMHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network,
        IPlayerManager playerManager,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.playerManager = playerManager;
        this.configAuthority = configAuthority;
        autoRecruitRoute = authorityRequestRouter.Register(
            AuthorityRoute<AutoRecruitIntent, ChangeAutoRecruitForSettlement, AutoRecruitChangeResult>.Define(
                "clan.autorecruit.set", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new ChangeAutoRecruitForSettlement(intent.HomeSettlementId, intent.Value, header),
                request => request.Header, result => result.Header,
                request => string.IsNullOrWhiteSpace(request.HomeSettlementId) ? "autorecruit-settlement-missing" : null,
                request => request.HomeSettlementId + ":" + request.Value, ValidateHeader, ExecuteAutoRecruit,
                CreateAutoRecruitTerminal, ProbeAutoRecruit, _ => { }, PresentAutoRecruit,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                isExpectedClientResult: (request, result) => request.HomeSettlementId == result.HomeSettlementId && request.Value == result.Value));
        behaviorRoute = authorityRequestRouter.Register(
            AuthorityRoute<ClanPartyBehaviorIntent, UpdatePartyBehaviorOnSelection, ClanPartyBehaviorChangeResult>.Define(
                "clan.party.behavior.set", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new UpdatePartyBehaviorOnSelection(intent.MobilePartyId, intent.Objective, header),
                request => request.Header, result => result.Header,
                request => string.IsNullOrWhiteSpace(request.MobilePartyId) ? "clan-party-missing" : null,
                request => request.MobilePartyId + ":" + (int)request.PartyObjective, ValidateHeader, ExecuteBehavior,
                CreateBehaviorTerminal, ProbeBehavior, _ => { }, PresentBehavior,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                isExpectedClientResult: (request, result) => request.MobilePartyId == result.MobilePartyId && request.PartyObjective == result.PartyObjective));

        messageBroker.Subscribe<PartyBehaviorUpdatedOnSelection>(Handle_PartyBehaviorUpdatedOnSelection);
        messageBroker.Subscribe<ClanPartyBehaviorApplied>(Handle_ClanPartyBehaviorApplied);
        messageBroker.Subscribe<AutoRecruitChangedForSettlement>(Handle_AutoRecruitChangedForSettlement);
        messageBroker.Subscribe<ChangeAutoRecruitForSettlementClients>(Handle_ChangeAutoRecruitForSettlementClients);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PartyBehaviorUpdatedOnSelection>(Handle_PartyBehaviorUpdatedOnSelection);
        messageBroker.Unsubscribe<ClanPartyBehaviorApplied>(Handle_ClanPartyBehaviorApplied);
        messageBroker.Unsubscribe<AutoRecruitChangedForSettlement>(Handle_AutoRecruitChangedForSettlement);
        messageBroker.Unsubscribe<ChangeAutoRecruitForSettlementClients>(Handle_ChangeAutoRecruitForSettlementClients);
        autoRecruitRoute.Dispose();
        behaviorRoute.Dispose();
    }

    private void Handle_PartyBehaviorUpdatedOnSelection(MessagePayload<PartyBehaviorUpdatedOnSelection> obj)
    {
        if (ModInformation.IsServer || !objectManager.TryGetIdWithLogging(obj.What.MobileParty, out var mobilePartyId)) return;
        behaviorRoute.Submit(new ClanPartyBehaviorIntent(mobilePartyId, obj.What.PartyObjective));
    }

    private void Handle_ClanPartyBehaviorApplied(MessagePayload<ClanPartyBehaviorApplied> obj)
    {
        if (!objectManager.TryGetObjectWithLogging<MobileParty>(obj.What.MobilePartyId, out var party)) return;
        GameThread.RunSafe(() => party.SetPartyObjective(obj.What.PartyObjective));
    }

    private void Handle_AutoRecruitChangedForSettlement(MessagePayload<AutoRecruitChangedForSettlement> obj)
    {
        if (ModInformation.IsServer) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.HomeSettlement, out var homeSettlementId)) return;
        autoRecruitRoute.Submit(new AutoRecruitIntent(homeSettlementId, obj.What.Value));
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out var snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out var current)) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        if (header.ProtocolVersion != current.ProtocolVersion) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.InvalidRequest, "unsupported-protocol");
        if (header.SessionId != current.SessionId) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session");
        return header.ExpectedRevision == current.Revision ? AuthorityHeaderValidation.Valid :
            AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
    }

    private AuthorityServerReply<AutoRecruitChangeResult> ExecuteAutoRecruit(
        AuthorityServerContext context, ChangeAutoRecruitForSettlement request)
    {
        if (!objectManager.TryGetObject(request.HomeSettlementId, out Settlement settlement) || settlement.Town == null)
            return AutoRecruitReply(context.Header, request, AuthorityResultStatus.Rejected, "autorecruit-settlement-missing");
        if (string.IsNullOrWhiteSpace(context.Player.ClanId) ||
            !objectManager.TryGetObject(context.Player.ClanId, out Clan clan) ||
            !string.Equals(settlement.OwnerClan?.StringId, clan.StringId, StringComparison.Ordinal))
            return AutoRecruitReply(context.Header, request, AuthorityResultStatus.Unauthorized, "autorecruit-owner-mismatch");
        if (string.IsNullOrWhiteSpace(context.Player.HeroId) || !objectManager.TryGetObject(context.Player.HeroId, out Hero hero) ||
            !ReferenceEquals(hero.Clan, clan))
            return AutoRecruitReply(context.Header, request, AuthorityResultStatus.Unauthorized, "actor-hero-clan-mismatch");

        settlement.Town.GarrisonAutoRecruitmentIsEnabled = request.Value;
        network.SendAll(new ChangeAutoRecruitForSettlementClients(request.HomeSettlementId, request.Value));
        return AutoRecruitReply(context.Header, request, AuthorityResultStatus.Accepted, null);
    }

    private AuthorityServerReply<ClanPartyBehaviorChangeResult> ExecuteBehavior(
        AuthorityServerContext context, UpdatePartyBehaviorOnSelection request)
    {
        if (!objectManager.TryGetObject(request.MobilePartyId, out MobileParty party) ||
            string.IsNullOrWhiteSpace(context.Player.ClanId) || !objectManager.TryGetObject(context.Player.ClanId, out Clan clan))
            return BehaviorReply(context.Header, request, AuthorityResultStatus.Unauthorized, "clan-party-owner-mismatch");
        if (party.ActualClan != clan || party.IsPlayerParty())
            return BehaviorReply(context.Header, request, AuthorityResultStatus.Unauthorized, "clan-party-owner-mismatch");
        if (string.IsNullOrWhiteSpace(context.Player.HeroId) || !objectManager.TryGetObject(context.Player.HeroId, out Hero actor) ||
            actor.Clan != clan || clan.Leader != actor)
            return BehaviorReply(context.Header, request, AuthorityResultStatus.Unauthorized, "clan-party-leader-permission-denied");
        if (string.IsNullOrWhiteSpace(context.Player.MobilePartyId) ||
            !objectManager.TryGetObject(context.Player.MobilePartyId, out MobileParty actorParty) ||
            actor.PartyBelongedTo != actorParty || actorParty.Party?.LeaderHero != actor)
            return BehaviorReply(context.Header, request, AuthorityResultStatus.Unauthorized, "actor-party-mismatch");
        if (!Enum.IsDefined(typeof(MobileParty.PartyObjective), request.PartyObjective))
            return BehaviorReply(context.Header, request, AuthorityResultStatus.InvalidRequest, "clan-party-objective-invalid");

        party.SetPartyObjective(request.PartyObjective);
        network.SendAll(new ClanPartyBehaviorApplied(request.MobilePartyId, request.PartyObjective));
        return BehaviorReply(context.Header, request,
            party.Objective == request.PartyObjective ? AuthorityResultStatus.Accepted : AuthorityResultStatus.ExecutionFailed,
            party.Objective == request.PartyObjective ? null : "clan-party-objective-not-committed");
    }

    private static AuthorityServerReply<AutoRecruitChangeResult> AutoRecruitReply(AuthorityRequestHeader header,
        ChangeAutoRecruitForSettlement request, AuthorityResultStatus status, string reason) =>
        new(new AutoRecruitChangeResult(request.HomeSettlementId, request.Value,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason)),
            status == AuthorityResultStatus.Accepted);

    private static AuthorityServerReply<ClanPartyBehaviorChangeResult> BehaviorReply(AuthorityRequestHeader header,
        UpdatePartyBehaviorOnSelection request, AuthorityResultStatus status, string reason) =>
        new(new ClanPartyBehaviorChangeResult(request.MobilePartyId, request.PartyObjective,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason)),
            status == AuthorityResultStatus.Accepted);

    private static AutoRecruitChangeResult CreateAutoRecruitTerminal(AuthorityRequestHeader header,
        AuthorityResultStatus status, string reason) => new(null, false,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private static ClanPartyBehaviorChangeResult CreateBehaviorTerminal(AuthorityRequestHeader header,
        AuthorityResultStatus status, string reason) => new(null, default,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private AuthorityCommitProbeResult ProbeAutoRecruit(AutoRecruitChangeResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted) return AuthorityCommitProbeResult.Applied;
        return objectManager.TryGetObject(result.HomeSettlementId, out Settlement settlement) &&
               settlement.Town?.GarrisonAutoRecruitmentIsEnabled == result.Value
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private AuthorityCommitProbeResult ProbeBehavior(ClanPartyBehaviorChangeResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted) return AuthorityCommitProbeResult.Applied;
        return objectManager.TryGetObject(result.MobilePartyId, out MobileParty party) && party.Objective == result.PartyObjective
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private static void PresentAutoRecruit(AuthorityClientOutcome<AutoRecruitChangeResult> outcome)
    {
        if (!outcome.Applied) Logger.Warning("Auto recruit update rejected: {Reason}", outcome.ReasonCode);
    }

    private static void PresentBehavior(AuthorityClientOutcome<ClanPartyBehaviorChangeResult> outcome)
    {
        if (!outcome.Applied) Logger.Warning("Clan party behavior update rejected: {Reason}", outcome.ReasonCode);
    }

    private void Handle_ChangeAutoRecruitForSettlementClients(MessagePayload<ChangeAutoRecruitForSettlementClients> obj)
    {
        if (!objectManager.TryGetObjectWithLogging<Settlement>(obj.What.HomeSettlementId, out var homeSettlement)) return;
        
        homeSettlement.Town.GarrisonAutoRecruitmentIsEnabled = obj.What.Value;
    }
}

internal readonly struct ClanPartyBehaviorIntent
{
    public ClanPartyBehaviorIntent(string mobilePartyId, MobileParty.PartyObjective objective)
    {
        MobilePartyId = mobilePartyId;
        Objective = objective;
    }

    public string MobilePartyId { get; }
    public MobileParty.PartyObjective Objective { get; }
}

internal readonly struct AutoRecruitIntent
{
    public AutoRecruitIntent(string homeSettlementId, bool value) { HomeSettlementId = homeSettlementId; Value = value; }
    public string HomeSettlementId { get; }
    public bool Value { get; }
}
