using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Clans.Messages;
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

        messageBroker.Subscribe<PartyBehaviorUpdatedOnSelection>(Handle_PartyBehaviorUpdatedOnSelection);
        messageBroker.Subscribe<UpdatePartyBehaviorOnSelection>(Handle_UpdatePartyBehaviorOnSelection);
        messageBroker.Subscribe<AutoRecruitChangedForSettlement>(Handle_AutoRecruitChangedForSettlement);
        messageBroker.Subscribe<ChangeAutoRecruitForSettlementClients>(Handle_ChangeAutoRecruitForSettlementClients);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PartyBehaviorUpdatedOnSelection>(Handle_PartyBehaviorUpdatedOnSelection);
        messageBroker.Unsubscribe<UpdatePartyBehaviorOnSelection>(Handle_UpdatePartyBehaviorOnSelection);
        messageBroker.Unsubscribe<AutoRecruitChangedForSettlement>(Handle_AutoRecruitChangedForSettlement);
        messageBroker.Unsubscribe<ChangeAutoRecruitForSettlementClients>(Handle_ChangeAutoRecruitForSettlementClients);
        autoRecruitRoute.Dispose();
    }

    private void Handle_PartyBehaviorUpdatedOnSelection(MessagePayload<PartyBehaviorUpdatedOnSelection> obj)
    {
        if (!objectManager.TryGetIdWithLogging(obj.What.MobileParty, out var mobilePartyId)) return;

        network.SendAll(new UpdatePartyBehaviorOnSelection(mobilePartyId, obj.What.PartyObjective));
    }

    private void Handle_UpdatePartyBehaviorOnSelection(MessagePayload<UpdatePartyBehaviorOnSelection> obj)
    {
        if (!objectManager.TryGetObjectWithLogging<MobileParty>(obj.What.MobilePartyId, out var mobileParty)) return;

        GameThread.RunSafe(() =>
        {
            mobileParty.SetPartyObjective(obj.What.PartyObjective);
        });
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

    private static AuthorityServerReply<AutoRecruitChangeResult> AutoRecruitReply(AuthorityRequestHeader header,
        ChangeAutoRecruitForSettlement request, AuthorityResultStatus status, string reason) =>
        new(new AutoRecruitChangeResult(request.HomeSettlementId, request.Value,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason)),
            status == AuthorityResultStatus.Accepted);

    private static AutoRecruitChangeResult CreateAutoRecruitTerminal(AuthorityRequestHeader header,
        AuthorityResultStatus status, string reason) => new(null, false,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private AuthorityCommitProbeResult ProbeAutoRecruit(AutoRecruitChangeResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted) return AuthorityCommitProbeResult.Applied;
        return objectManager.TryGetObject(result.HomeSettlementId, out Settlement settlement) &&
               settlement.Town?.GarrisonAutoRecruitmentIsEnabled == result.Value
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private static void PresentAutoRecruit(AuthorityClientOutcome<AutoRecruitChangeResult> outcome)
    {
        if (!outcome.Applied) Logger.Warning("Auto recruit update rejected: {Reason}", outcome.ReasonCode);
    }

    private void Handle_ChangeAutoRecruitForSettlementClients(MessagePayload<ChangeAutoRecruitForSettlementClients> obj)
    {
        if (!objectManager.TryGetObjectWithLogging<Settlement>(obj.What.HomeSettlementId, out var homeSettlement)) return;
        
        homeSettlement.Town.GarrisonAutoRecruitmentIsEnabled = obj.What.Value;
    }
}

internal readonly struct AutoRecruitIntent
{
    public AutoRecruitIntent(string homeSettlementId, bool value) { HomeSettlementId = homeSettlementId; Value = value; }
    public string HomeSettlementId { get; }
    public bool Value { get; }
}
