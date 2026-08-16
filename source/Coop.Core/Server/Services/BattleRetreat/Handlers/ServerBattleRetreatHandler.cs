using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using Coop.Core.Client.Services.BattleRetreat.Messages;
using Coop.Core.Server.Services.BattleRetreat.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.MapEvents.Interfaces;
using GameInterface.Services.MapEvents.Messages.Retreat;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using LiteNetLib;
using Missions.Messages;
using Serilog;
using System;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace Coop.Core.Server.Services.BattleRetreat.Handlers;

/// <summary>Executes player-owned retreat mutations through typed, replay-safe authority routes.</summary>
internal class ServerBattleRetreatHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ServerBattleRetreatHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IBattleRetreatInterface retreatInterface;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<BattleRetreatIntent, NetworkBattleRetreatResolved> retreatRoute;
    private readonly IAuthorityRouteHandle<BattleMissionRetreatIntent, NetworkBattleMissionRetreatResolved> missionRetreatRoute;
    private readonly IAuthorityRouteHandle<BreakInCasualtiesIntent, NetworkBreakInCasualtiesResolved> breakInCasualtiesRoute;

    public ServerBattleRetreatHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IBattleRetreatInterface retreatInterface,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.retreatInterface = retreatInterface;
        this.configAuthority = configAuthority;

        retreatRoute = authorityRequestRouter.Register(
            AuthorityRoute<BattleRetreatIntent, NetworkRequestBattleRetreat, NetworkBattleRetreatResolved>.Define(
                "battle.retreat", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestBattleRetreat(intent.PartyId, intent.MapEventId, header),
                request => request.Header, result => result.Header, ValidateRetreatWireShape, BuildRetreatCommandKey,
                ValidateHeader, ExecuteRetreat, CreateRetreatTerminalResult, _ => AuthorityCommitProbeResult.Pending,
                _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedRetreatResult));
        missionRetreatRoute = authorityRequestRouter.Register(
            AuthorityRoute<BattleMissionRetreatIntent, NetworkRequestBattleMissionRetreat, NetworkBattleMissionRetreatResolved>.Define(
                "battle.mission-retreat", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestBattleMissionRetreat(intent.PartyId, intent.MapEventId, header),
                request => request.Header, result => result.Header, ValidateMissionRetreatWireShape, BuildMissionRetreatCommandKey,
                ValidateHeader, ExecuteMissionRetreat, CreateMissionRetreatTerminalResult, _ => AuthorityCommitProbeResult.Pending,
                _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedMissionRetreatResult));
        breakInCasualtiesRoute = authorityRequestRouter.Register(
            AuthorityRoute<BreakInCasualtiesIntent, NetworkRequestBreakInCasualties, NetworkBreakInCasualtiesResolved>.Define(
                "battle.break-in-casualties", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestBreakInCasualties(intent.PartyId, intent.SettlementId, header),
                request => request.Header, result => result.Header, ValidateBreakInWireShape, BuildBreakInCommandKey,
                ValidateHeader, ExecuteBreakInCasualties, CreateBreakInTerminalResult, _ => AuthorityCommitProbeResult.Pending,
                _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedBreakInResult));

        // These are server-originated callbacks and an authenticated mission-departure fallback, not client
        // authority commands. They remain outside the routes so replicated mission teardown keeps its owner.
        messageBroker.Subscribe<BattleMissionRetreatAttempted>(HandleHostMissionRetreat);
        messageBroker.Subscribe<NetworkMissionLeft>(HandleMissionDeparture);
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
        if (header.ExpectedRevision != current.Revision)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
        return AuthorityHeaderValidation.Valid;
    }

    private static string ValidateRetreatWireShape(NetworkRequestBattleRetreat request) =>
        ValidateIdentifiers(request.PartyId, request.MapEventId, "invalid-battle-retreat-identifiers");

    private static string ValidateMissionRetreatWireShape(NetworkRequestBattleMissionRetreat request) =>
        ValidateIdentifiers(request.PartyId, request.MapEventId, "invalid-battle-mission-retreat-identifiers");

    private static string ValidateBreakInWireShape(NetworkRequestBreakInCasualties request) =>
        ValidateIdentifiers(request.PartyId, request.SettlementId, "invalid-break-in-casualty-identifiers");

    private static string ValidateIdentifiers(string first, string second, string reason) =>
        string.IsNullOrWhiteSpace(first) || first.Length > 256 ||
        string.IsNullOrWhiteSpace(second) || second.Length > 256 ? reason : null;

    private static string BuildRetreatCommandKey(NetworkRequestBattleRetreat request) =>
        BuildCommandKey(request.PartyId, request.MapEventId);

    private static string BuildMissionRetreatCommandKey(NetworkRequestBattleMissionRetreat request) =>
        BuildCommandKey(request.PartyId, request.MapEventId);

    private static string BuildBreakInCommandKey(NetworkRequestBreakInCasualties request) =>
        BuildCommandKey(request.PartyId, request.SettlementId);

    private static string BuildCommandKey(string first, string second) =>
        string.Concat(first.Length, ":", first, ":", second.Length, ":", second);

    private AuthorityServerReply<NetworkBattleRetreatResolved> ExecuteRetreat(
        AuthorityServerContext context,
        NetworkRequestBattleRetreat request)
    {
        if (!TryResolveOwnedBattle(context, request.PartyId, request.MapEventId, out var party, out var battle, out var reason))
            return RejectRetreat(context.Header, request, reason);

        bool mutationBoundaryCrossed = false;
        try
        {
            mutationBoundaryCrossed = true;
            if (!retreatInterface.TryApplyRetreat(party, battle, out var campCleared))
                return RejectRetreat(context.Header, request, "retreat-refused");

            // Party/map-event removal, roster/item replication, position publication, and the camp cleanup are
            // all queued by the authoritative operation before this targeted terminal result.
            if (campCleared?.Length > 0)
                network.SendAll(new NetworkBattleRetreatCampsCleared(campCleared));
            return new AuthorityServerReply<NetworkBattleRetreatResolved>(
                new NetworkBattleRetreatResolved(request.PartyId, approved: true, campCleared ?? Array.Empty<string>(),
                    AcceptedHeader(context.Header)), statePublished: true);
        }
        catch (Exception exception)
        {
            Logger.Error(exception,
                "Battle retreat failed after the authority mutation boundary. Route={Route} SessionId={SessionId} RequestId={RequestId} Party={PartyId} Battle={BattleId}",
                context.RouteId, context.Header.SessionId, context.Header.RequestId, request.PartyId, request.MapEventId);
            if (mutationBoundaryCrossed) DisconnectAllConnectedPeers("ambiguous battle retreat mutation", context);
            return new AuthorityServerReply<NetworkBattleRetreatResolved>(
                CreateRetreatTerminalResult(context.Header, AuthorityResultStatus.ExecutionFailed, "battle-retreat-isolated"),
                statePublished: false, suppressReply: true);
        }
    }

    private AuthorityServerReply<NetworkBattleMissionRetreatResolved> ExecuteMissionRetreat(
        AuthorityServerContext context,
        NetworkRequestBattleMissionRetreat request)
    {
        if (!TryResolveOwnedBattle(context, request.PartyId, request.MapEventId, out var party, out var battle, out var reason))
            return RejectMissionRetreat(context.Header, request, reason);

        bool mutationBoundaryCrossed = false;
        try
        {
            mutationBoundaryCrossed = true;
            if (!retreatInterface.TryLeaveBattleAfterMissionRetreat(party, battle))
                return RejectMissionRetreat(context.Header, request, "mission-retreat-refused");

            return new AuthorityServerReply<NetworkBattleMissionRetreatResolved>(
                new NetworkBattleMissionRetreatResolved(request.PartyId, request.MapEventId, approved: true,
                    AcceptedHeader(context.Header)), statePublished: true);
        }
        catch (Exception exception)
        {
            Logger.Error(exception,
                "Battle mission retreat failed after the authority mutation boundary. Route={Route} SessionId={SessionId} RequestId={RequestId} Party={PartyId} Battle={BattleId}",
                context.RouteId, context.Header.SessionId, context.Header.RequestId, request.PartyId, request.MapEventId);
            if (mutationBoundaryCrossed) DisconnectAllConnectedPeers("ambiguous battle mission retreat mutation", context);
            return new AuthorityServerReply<NetworkBattleMissionRetreatResolved>(
                CreateMissionRetreatTerminalResult(context.Header, AuthorityResultStatus.ExecutionFailed, "mission-retreat-isolated"),
                statePublished: false, suppressReply: true);
        }
    }

    private AuthorityServerReply<NetworkBreakInCasualtiesResolved> ExecuteBreakInCasualties(
        AuthorityServerContext context,
        NetworkRequestBreakInCasualties request)
    {
        if (!string.Equals(context.Player.MobilePartyId, request.PartyId, StringComparison.Ordinal))
            return RejectBreakIn(context.Header, request, "invalid-requester");
        if (!objectManager.TryGetObjectWithLogging<MobileParty>(context.Player.MobilePartyId, out var party))
            return RejectBreakIn(context.Header, request, "party-not-found");
        if (!objectManager.TryGetObjectWithLogging<Settlement>(request.SettlementId, out var settlement))
            return RejectBreakIn(context.Header, request, "settlement-not-found");
        if (!ReferenceEquals(party.CurrentSettlement, settlement) && !ReferenceEquals(party.BesiegedSettlement, settlement))
            return RejectBreakIn(context.Header, request, "invalid-break-in-context");
        if (settlement.SiegeEvent == null || party.Party == null)
            return RejectBreakIn(context.Header, request, "invalid-break-in-context");

        bool mutationBoundaryCrossed = false;
        try
        {
            // The native sacrifice helper emits its roster replication during this call. The exact remaining
            // regular-member count in the terminal result is then the requester's correlated commit proof.
            mutationBoundaryCrossed = true;
            retreatInterface.ApplyBreakInCasualties(party, settlement);
            int regularMemberCount = party.Party.NumberOfRegularMembers;
            return new AuthorityServerReply<NetworkBreakInCasualtiesResolved>(
                new NetworkBreakInCasualtiesResolved(request.PartyId, request.SettlementId, regularMemberCount,
                    approved: true, AcceptedHeader(context.Header)), statePublished: true);
        }
        catch (Exception exception)
        {
            Logger.Error(exception,
                "Break-in casualty mutation failed after its authority boundary. Route={Route} SessionId={SessionId} RequestId={RequestId} Party={PartyId} Settlement={SettlementId}",
                context.RouteId, context.Header.SessionId, context.Header.RequestId, request.PartyId, request.SettlementId);
            if (mutationBoundaryCrossed) DisconnectAllConnectedPeers("ambiguous break-in casualty mutation", context);
            return new AuthorityServerReply<NetworkBreakInCasualtiesResolved>(
                CreateBreakInTerminalResult(context.Header, AuthorityResultStatus.ExecutionFailed, "break-in-casualties-isolated"),
                statePublished: false, suppressReply: true);
        }
    }

    private bool TryResolveOwnedBattle(
        AuthorityServerContext context,
        string partyId,
        string mapEventId,
        out MobileParty party,
        out MapEvent battle,
        out string reason)
    {
        party = null;
        battle = null;
        reason = null;
        if (!string.Equals(context.Player.MobilePartyId, partyId, StringComparison.Ordinal))
        {
            reason = "invalid-requester";
            return false;
        }
        if (!objectManager.TryGetObjectWithLogging<MobileParty>(context.Player.MobilePartyId, out party))
        {
            reason = "party-not-found";
            return false;
        }
        if (!objectManager.TryGetObjectWithLogging<MapEvent>(mapEventId, out battle))
        {
            reason = "battle-not-found";
            return false;
        }
        if (!ReferenceEquals(party.MapEvent, battle) || party.Party?.MapEventSide?.MapEvent != battle)
        {
            reason = "stale-battle-context";
            return false;
        }
        return true;
    }

    private static AuthorityResultHeader AcceptedHeader(AuthorityRequestHeader header) =>
        new(header.SessionId, header.RequestId, AuthorityResultStatus.Accepted, header.ExpectedRevision, null);

    private static AuthorityServerReply<NetworkBattleRetreatResolved> RejectRetreat(
        AuthorityRequestHeader header, NetworkRequestBattleRetreat request, string reason) =>
        new(new NetworkBattleRetreatResolved(request.PartyId, approved: false, Array.Empty<string>(),
            new AuthorityResultHeader(header.SessionId, header.RequestId, AuthorityResultStatus.Rejected,
                header.ExpectedRevision, reason)), statePublished: false);

    private static AuthorityServerReply<NetworkBattleMissionRetreatResolved> RejectMissionRetreat(
        AuthorityRequestHeader header, NetworkRequestBattleMissionRetreat request, string reason) =>
        new(new NetworkBattleMissionRetreatResolved(request.PartyId, request.MapEventId, approved: false,
            new AuthorityResultHeader(header.SessionId, header.RequestId, AuthorityResultStatus.Rejected,
                header.ExpectedRevision, reason)), statePublished: false);

    private static AuthorityServerReply<NetworkBreakInCasualtiesResolved> RejectBreakIn(
        AuthorityRequestHeader header, NetworkRequestBreakInCasualties request, string reason) =>
        new(new NetworkBreakInCasualtiesResolved(request.PartyId, request.SettlementId, -1, approved: false,
            new AuthorityResultHeader(header.SessionId, header.RequestId, AuthorityResultStatus.Rejected,
                header.ExpectedRevision, reason)), statePublished: false);

    private static NetworkBattleRetreatResolved CreateRetreatTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, status == AuthorityResultStatus.Accepted, Array.Empty<string>(),
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private static NetworkBattleMissionRetreatResolved CreateMissionRetreatTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, null, status == AuthorityResultStatus.Accepted,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private static NetworkBreakInCasualtiesResolved CreateBreakInTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, null, -1, status == AuthorityResultStatus.Accepted,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private static bool IsExpectedRetreatResult(NetworkRequestBattleRetreat request, NetworkBattleRetreatResolved result) =>
        result.Approved == (result.Header.Status == AuthorityResultStatus.Accepted) &&
        string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal);

    private static bool IsExpectedMissionRetreatResult(
        NetworkRequestBattleMissionRetreat request,
        NetworkBattleMissionRetreatResolved result) =>
        result.Approved == (result.Header.Status == AuthorityResultStatus.Accepted) &&
        string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal) &&
        string.Equals(request.MapEventId, result.MapEventId, StringComparison.Ordinal);

    private static bool IsExpectedBreakInResult(
        NetworkRequestBreakInCasualties request,
        NetworkBreakInCasualtiesResolved result) =>
        result.Approved == (result.Header.Status == AuthorityResultStatus.Accepted) &&
        result.RegularMemberCount >= 0 &&
        string.Equals(request.PartyId, result.PartyId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal);

    /// <summary>Authenticated fallback carried by mission departure; it stays internal to mission replication.</summary>
    private void HandleMissionDeparture(MessagePayload<NetworkMissionLeft> payload)
    {
        var obj = payload.What;
        if (!obj.LeaveUnresolvedBattle || payload.Who is not NetPeer peer) return;

        GameThread.RunSafe(() =>
        {
            if (!playerManager.TryGetPlayer(peer, out var player) ||
                !objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out var owned) ||
                !objectManager.TryGetObject<MapEvent>(obj.InstanceId, out var battle)) return;

            if (!retreatInterface.TryLeaveBattleAfterMissionRetreat(owned, battle) && owned.MapEvent == battle)
                Logger.Warning("Atomic mission departure could not detach party {PartyId} from {BattleId}",
                    player.MobilePartyId, obj.InstanceId);
        }, context: nameof(HandleMissionDeparture));
    }

    /// <summary>Host mission events have no remote peer and remain server-local callbacks.</summary>
    private void HandleHostMissionRetreat(MessagePayload<BattleMissionRetreatAttempted> payload)
    {
        var obj = payload.What;
        GameThread.RunSafe(() => retreatInterface.TryLeaveBattleAfterMissionRetreat(obj.Party, obj.Battle),
            context: nameof(HandleHostMissionRetreat));
    }

    private static void DisconnectPeer(NetPeer peer, string reason, AuthorityServerContext context)
    {
        try { peer?.Disconnect(); }
        catch (Exception exception)
        {
            Logger.Fatal(exception,
                "Could not disconnect peer after {Reason}. Route={Route} SessionId={SessionId} RequestId={RequestId}",
                reason, context.RouteId, context.Header.SessionId, context.Header.RequestId);
        }
    }

    private void DisconnectAllConnectedPeers(string reason, AuthorityServerContext context)
    {
        foreach (var player in playerManager.Players)
        {
            if (!playerManager.IsConnected(player) || !playerManager.TryGetPeer(player.ControllerId, out var peer)) continue;
            DisconnectPeer(peer, reason, context);
        }
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<BattleMissionRetreatAttempted>(HandleHostMissionRetreat);
        messageBroker.Unsubscribe<NetworkMissionLeft>(HandleMissionDeparture);
        retreatRoute.Dispose();
        missionRetreatRoute.Dispose();
        breakInCasualtiesRoute.Dispose();
    }

    private sealed class BattleRetreatIntent
    {
        public BattleRetreatIntent(string partyId, string mapEventId) { PartyId = partyId; MapEventId = mapEventId; }
        public string PartyId { get; }
        public string MapEventId { get; }
    }

    private sealed class BattleMissionRetreatIntent
    {
        public BattleMissionRetreatIntent(string partyId, string mapEventId) { PartyId = partyId; MapEventId = mapEventId; }
        public string PartyId { get; }
        public string MapEventId { get; }
    }

    private sealed class BreakInCasualtiesIntent
    {
        public BreakInCasualtiesIntent(string partyId, string settlementId) { PartyId = partyId; SettlementId = settlementId; }
        public string PartyId { get; }
        public string SettlementId { get; }
    }
}
