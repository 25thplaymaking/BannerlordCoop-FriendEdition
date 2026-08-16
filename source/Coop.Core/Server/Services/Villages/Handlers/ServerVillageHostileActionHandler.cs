using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Messages;
using Coop.Core.Client.Services.MobileParties.Messages;
using Coop.Core.Server.Connections.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.MapEvents.Interfaces;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Settlements.Interfaces;
using GameInterface.Services.Villages.Data;
using GameInterface.Services.Villages.Interfaces;
using GameInterface.Services.Villages.Messages;
using LiteNetLib;
using Serilog;
using System;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace Coop.Core.Server.Services.Villages.Handlers;

internal class ServerVillageHostileActionHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ServerVillageHostileActionHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly ISettlementInterface settlementInterface;
    private readonly IVillageHostileActionInterface villageHostileActionInterface;
    private readonly IRaidAiInterventionConfigInterface raidAiInterventionConfigInterface;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<VillageHostileActionIntent, NetworkVillageHostileActionResult> hostileActionRoute;

    public ServerVillageHostileActionHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        ISettlementInterface settlementInterface,
        IVillageHostileActionInterface villageHostileActionInterface,
        IRaidAiInterventionConfigInterface raidAiInterventionConfigInterface,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.settlementInterface = settlementInterface;
        this.villageHostileActionInterface = villageHostileActionInterface;
        this.raidAiInterventionConfigInterface = raidAiInterventionConfigInterface;
        this.configAuthority = configAuthority;

        hostileActionRoute = authorityRequestRouter.Register(
            AuthorityRoute<VillageHostileActionIntent, NetworkRequestVillageHostileAction,
                NetworkVillageHostileActionResult>.Define(
                "village.hostile-action", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestVillageHostileAction(
                    header, intent.Action, intent.MobilePartyId, intent.SettlementId),
                request => request.Header, result => result.Header, ValidateWireShape, BuildCommandKey,
                ValidateHeader, ExecuteHostileAction, CreateTerminalResult, _ => AuthorityCommitProbeResult.Pending,
                _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedResult));

        messageBroker.Subscribe<VillageHostileActionCooldownsChanged>(Handle_VillageHostileActionCooldownsChanged);
        messageBroker.Subscribe<PlayerCampaignEntered>(Handle_PlayerCampaignEntered);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<VillageHostileActionCooldownsChanged>(Handle_VillageHostileActionCooldownsChanged);
        messageBroker.Unsubscribe<PlayerCampaignEntered>(Handle_PlayerCampaignEntered);
        hostileActionRoute.Dispose();
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private static string ValidateWireShape(NetworkRequestVillageHostileAction request)
    {
        if ((request.Action != VillageHostileAction.Raid &&
             request.Action != VillageHostileAction.ForceVolunteers &&
             request.Action != VillageHostileAction.ForceSupplies))
            return "invalid-hostile-action";
        if (string.IsNullOrWhiteSpace(request.MobilePartyId) || request.MobilePartyId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.SettlementId) || request.SettlementId.Length > 256)
            return "invalid-hostile-action-identifiers";
        return null;
    }

    private static string BuildCommandKey(NetworkRequestVillageHostileAction request) =>
        string.Concat((int)request.Action, ":", request.MobilePartyId.Length, ":", request.MobilePartyId,
            ":", request.SettlementId.Length, ":", request.SettlementId);

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

    private AuthorityServerReply<NetworkVillageHostileActionResult> ExecuteHostileAction(
        AuthorityServerContext context,
        NetworkRequestVillageHostileAction request)
    {
        try
        {
            if (!objectManager.TryGetObjectWithLogging<MobileParty>(request.MobilePartyId, out var mobileParty))
            {
                return Reject(context.Header, request, VillageHostileActionDeniedReason.InvalidRequester);
            }

            if (!objectManager.TryGetObjectWithLogging<Settlement>(request.SettlementId, out var settlement))
            {
                return Reject(context.Header, request, VillageHostileActionDeniedReason.NonVillageSettlement);
            }

            if (!string.Equals(context.Player.MobilePartyId, request.MobilePartyId, StringComparison.Ordinal))
                return Reject(context.Header, request, VillageHostileActionDeniedReason.InvalidRequester);

            if (!villageHostileActionInterface.CanStartHostileAction(mobileParty, settlement, request.Action, out var reason))
            {
                return Reject(context.Header, request, reason);
            }

            if (request.Action == VillageHostileAction.Raid)
                KickOtherPlayersOutOfVillage(context.Player.ControllerId, mobileParty, settlement);

            villageHostileActionInterface.ApplyHostileAction(mobileParty, settlement, request.Action);
            villageHostileActionInterface.ApproveMapEventStart(mobileParty.Party, settlement, request.Action);
            // This ordered state publication is the route's commit proof. The approval remains
            // unconsumable until it has been queued, so MapEventCreation cannot race ahead.
            network.Send(context.Peer, new NetworkVillageHostileActionStarted(
                request.Action, request.MobilePartyId, request.SettlementId,
                context.Header.SessionId, context.Header.RequestId));
            if (!villageHostileActionInterface.MarkApprovedMapEventStartPublished(
                    mobileParty.Party, settlement, request.Action))
            {
                villageHostileActionInterface.CancelMapEventStartApprovals(mobileParty.Party);
                return Failed(context.Header, request, "approval-publication-failed");
            }

            return Accepted(context.Header, request);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Failed to start village hostile action");
            if (objectManager.TryGetObject<MobileParty>(request.MobilePartyId, out var mobileParty) &&
                mobileParty?.Party != null)
                villageHostileActionInterface.CancelMapEventStartApprovals(mobileParty.Party);
            return Failed(context.Header, request, "hostile-action-failed");
        }
    }

    private static AuthorityServerReply<NetworkVillageHostileActionResult> Accepted(
        AuthorityRequestHeader header, NetworkRequestVillageHostileAction request) =>
        new(new NetworkVillageHostileActionResult(new AuthorityResultHeader(
                header.SessionId, header.RequestId, AuthorityResultStatus.Accepted, header.ExpectedRevision, null),
            request.Action, request.MobilePartyId, request.SettlementId), statePublished: true);

    private static AuthorityServerReply<NetworkVillageHostileActionResult> Reject(
        AuthorityRequestHeader header, NetworkRequestVillageHostileAction request, VillageHostileActionDeniedReason reason) =>
        new(new NetworkVillageHostileActionResult(new AuthorityResultHeader(
                header.SessionId, header.RequestId, AuthorityResultStatus.Rejected, header.ExpectedRevision,
                GetReasonCode(reason)), request.Action, request.MobilePartyId, request.SettlementId), statePublished: false);

    private static AuthorityServerReply<NetworkVillageHostileActionResult> Failed(
        AuthorityRequestHeader header, NetworkRequestVillageHostileAction request, string reasonCode) =>
        new(new NetworkVillageHostileActionResult(new AuthorityResultHeader(
                header.SessionId, header.RequestId, AuthorityResultStatus.ExecutionFailed, header.ExpectedRevision,
                reasonCode), request.Action, request.MobilePartyId, request.SettlementId), statePublished: false);

    private static NetworkVillageHostileActionResult CreateTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reasonCode) =>
        new(new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reasonCode),
            VillageHostileAction.Raid, null, null);

    private static bool IsExpectedResult(NetworkRequestVillageHostileAction request,
        NetworkVillageHostileActionResult result) => request.Action == result.Action &&
        string.Equals(request.MobilePartyId, result.MobilePartyId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal);

    private static string GetReasonCode(VillageHostileActionDeniedReason reason) => reason switch
    {
        VillageHostileActionDeniedReason.InvalidRequester => "invalid-requester",
        VillageHostileActionDeniedReason.NonVillageSettlement => "non-village-settlement",
        VillageHostileActionDeniedReason.OwnFaction => "own-faction",
        VillageHostileActionDeniedReason.AlreadyInMapEvent => "already-in-map-event",
        VillageHostileActionDeniedReason.InvalidVillageState => "invalid-village-state",
        VillageHostileActionDeniedReason.HearthTooLow => "hearth-too-low",
        VillageHostileActionDeniedReason.Cooldown => "cooldown",
        VillageHostileActionDeniedReason.NotApproved => "not-approved",
        _ => "invalid-hostile-action",
    };

    private void KickOtherPlayersOutOfVillage(string requestingControllerId, MobileParty raidingParty, Settlement settlement)
    {
        foreach (var player in playerManager.Players)
        {
            if (player.ControllerId == requestingControllerId)
                continue;

            if (!objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out var playerParty))
                continue;

            if (playerParty == raidingParty || playerParty.CurrentSettlement != settlement)
                continue;

            network.SendAll(new NetworkSettlementEncounterLeaveResult(
                player.MobilePartyId,
                SettlementEncounterLeaveOutcome.Applied));
            settlementInterface.PartyLeaveSettlement(playerParty);
        }
    }

    private void Handle_VillageHostileActionCooldownsChanged(MessagePayload<VillageHostileActionCooldownsChanged> payload)
    {
        if (ModInformation.IsClient) return;

        network.SendAll(new NetworkVillageHostileActionCooldowns(payload.What.Cooldowns ?? Array.Empty<VillageHostileActionCooldownData>()));
    }

    private void Handle_PlayerCampaignEntered(MessagePayload<PlayerCampaignEntered> payload)
    {
        if (ModInformation.IsClient) return;

        GameThread.RunSafe(
            () => SendJoinSnapshots(payload.What.playerId),
            blocking: true,
            context: nameof(Handle_PlayerCampaignEntered));
    }

    private void SendJoinSnapshots(NetPeer peer)
    {
        SendCooldownSnapshot(peer);
        SendRaidAiInterventionConfigSnapshot(peer);
    }

    private void SendCooldownSnapshot(NetPeer peer)
    {
        var cooldowns = villageHostileActionInterface.GetActiveCooldowns();
        if (cooldowns.Length == 0)
            return;

        network.Send(peer, new NetworkVillageHostileActionCooldowns(cooldowns));
    }

    private void SendRaidAiInterventionConfigSnapshot(NetPeer peer)
    {
        raidAiInterventionConfigInterface.SendSnapshot(peer);
    }

    private readonly struct VillageHostileActionIntent
    {
        public VillageHostileActionIntent(VillageHostileAction action, string mobilePartyId, string settlementId)
        {
            Action = action;
            MobilePartyId = mobilePartyId;
            SettlementId = settlementId;
        }

        public VillageHostileAction Action { get; }
        public string MobilePartyId { get; }
        public string SettlementId { get; }
    }
}
