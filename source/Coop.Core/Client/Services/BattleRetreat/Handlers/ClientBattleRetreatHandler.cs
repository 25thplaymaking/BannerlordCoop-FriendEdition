using Common;
using Common.Logging;
using Common.Messaging;
using Common.Util;
using Coop.Core.Client.Services.BattleRetreat.Messages;
using Coop.Core.Server.Services.BattleRetreat.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.MapEvents.Messages.Retreat;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.ObjectManager;
using Serilog;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;

namespace Coop.Core.Client.Services.BattleRetreat.Handlers;

/// <summary>Submits local retreat intent through typed authority routes and only unwinds UI after canonical state arrives.</summary>
internal class ClientBattleRetreatHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ClientBattleRetreatHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<BattleRetreatIntent, NetworkBattleRetreatResolved> retreatRoute;
    private readonly IAuthorityRouteHandle<BattleMissionRetreatIntent, NetworkBattleMissionRetreatResolved> missionRetreatRoute;
    private readonly IAuthorityRouteHandle<BreakInCasualtiesIntent, NetworkBreakInCasualtiesResolved> breakInCasualtiesRoute;

    public ClientBattleRetreatHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.configAuthority = configAuthority;

        retreatRoute = authorityRequestRouter.Register(
            AuthorityRoute<BattleRetreatIntent, NetworkRequestBattleRetreat, NetworkBattleRetreatResolved>.Define(
                "battle.retreat", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestBattleRetreat(intent.PartyId, intent.MapEventId, header),
                request => request.Header, result => result.Header, ValidateRetreatWireShape, BuildRetreatCommandKey,
                ValidateHeader, (_, __) => throw new InvalidOperationException("Battle retreat routes execute only on the server."),
                CreateRetreatTerminalResult, ProbeRetreatCommit, _ => { }, PresentRetreatOutcome,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation, failClosedOnApplyFailure: true,
                isExpectedClientResult: IsExpectedRetreatResult));
        missionRetreatRoute = authorityRequestRouter.Register(
            AuthorityRoute<BattleMissionRetreatIntent, NetworkRequestBattleMissionRetreat, NetworkBattleMissionRetreatResolved>.Define(
                "battle.mission-retreat", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestBattleMissionRetreat(intent.PartyId, intent.MapEventId, header),
                request => request.Header, result => result.Header, ValidateMissionRetreatWireShape, BuildMissionRetreatCommandKey,
                ValidateHeader, (_, __) => throw new InvalidOperationException("Battle mission-retreat routes execute only on the server."),
                CreateMissionRetreatTerminalResult, ProbeMissionRetreatCommit, _ => { }, PresentMissionRetreatOutcome,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation, failClosedOnApplyFailure: true,
                isExpectedClientResult: IsExpectedMissionRetreatResult));
        breakInCasualtiesRoute = authorityRequestRouter.Register(
            AuthorityRoute<BreakInCasualtiesIntent, NetworkRequestBreakInCasualties, NetworkBreakInCasualtiesResolved>.Define(
                "battle.break-in-casualties", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestBreakInCasualties(intent.PartyId, intent.SettlementId, header),
                request => request.Header, result => result.Header, ValidateBreakInWireShape, BuildBreakInCommandKey,
                ValidateHeader, (_, __) => throw new InvalidOperationException("Break-in casualty routes execute only on the server."),
                CreateBreakInTerminalResult, ProbeBreakInCommit, _ => { }, PresentBreakInOutcome,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation, failClosedOnApplyFailure: true,
                isExpectedClientResult: IsExpectedBreakInResult));

        messageBroker.Subscribe<BattleRetreatAttempted>(HandleAttempt);
        messageBroker.Subscribe<BattleMissionRetreatAttempted>(HandleMissionRetreat);
        messageBroker.Subscribe<BreakInCasualtiesAttempted>(HandleBreakInCasualties);
        messageBroker.Subscribe<NetworkBattleRetreatCampsCleared>(HandleCampsCleared);
    }

    private void HandleMissionRetreat(MessagePayload<BattleMissionRetreatAttempted> payload)
    {
        var obj = payload.What;
        if (!objectManager.TryGetIdWithLogging(obj.Party, out var partyId) ||
            !objectManager.TryGetIdWithLogging(obj.Battle, out var mapEventId)) return;

        missionRetreatRoute.Submit(new BattleMissionRetreatIntent(partyId, mapEventId));
    }

    private void HandleAttempt(MessagePayload<BattleRetreatAttempted> payload)
    {
        var obj = payload.What;
        if (!objectManager.TryGetIdWithLogging(obj.Party, out var partyId) ||
            !objectManager.TryGetIdWithLogging(obj.Battle, out var mapEventId)) return;

        retreatRoute.Submit(new BattleRetreatIntent(partyId, mapEventId));
    }

    private void HandleBreakInCasualties(MessagePayload<BreakInCasualtiesAttempted> payload)
    {
        var obj = payload.What;
        if (!objectManager.TryGetIdWithLogging(obj.Party, out var partyId) ||
            !objectManager.TryGetIdWithLogging(obj.Settlement, out var settlementId)) return;

        breakInCasualtiesRoute.Submit(new BreakInCasualtiesIntent(partyId, settlementId));
    }

    private void HandleCampsCleared(MessagePayload<NetworkBattleRetreatCampsCleared> payload)
    {
        if (!configAuthority.IsTrustedServer(payload.Who)) return;
        var mine = MobileParty.MainParty;
        if (mine == null || !objectManager.TryGetId(mine, out var myId) ||
            payload.What.PartyIds?.Contains(myId) != true) return;

        GameThread.RunSafe(() =>
        {
            using (new AllowedThread())
            {
                if (PlayerSiege.PlayerSiegeEvent != null) PlayerSiege.FinalizePlayerSiege();
            }
        }, context: nameof(HandleCampsCleared));
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

    private AuthorityCommitProbeResult ProbeRetreatCommit(NetworkBattleRetreatResolved result)
    {
        if (!objectManager.TryGetObject<MobileParty>(result.PartyId, out var party))
            return AuthorityCommitProbeResult.Pending;

        return party.MapEvent == null && party.BesiegerCamp == null
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;
    }

    private AuthorityCommitProbeResult ProbeMissionRetreatCommit(NetworkBattleMissionRetreatResolved result)
    {
        if (!objectManager.TryGetObject<MobileParty>(result.PartyId, out var party))
            return AuthorityCommitProbeResult.Pending;

        return party.Party?.MapEventSide == null
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;
    }

    private AuthorityCommitProbeResult ProbeBreakInCommit(NetworkBreakInCasualtiesResolved result)
    {
        if (!objectManager.TryGetObject<MobileParty>(result.PartyId, out var party) ||
            !objectManager.TryGetObject<Settlement>(result.SettlementId, out var settlement))
            return AuthorityCommitProbeResult.Pending;

        return (ReferenceEquals(party.CurrentSettlement, settlement) || ReferenceEquals(party.BesiegedSettlement, settlement)) &&
            party.Party?.NumberOfRegularMembers == result.RegularMemberCount
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;
    }

    private static void PresentRetreatOutcome(AuthorityClientOutcome<NetworkBattleRetreatResolved> outcome)
    {
        if (!outcome.Applied)
        {
            Logger.Information("Server refused battle retreat: {Reason}", outcome.ReasonCode);
            return;
        }

        using (new AllowedThread())
        {
            if (PlayerEncounter.Current != null) PlayerEncounter.Finish(true);
            else GameMenu.ExitToLast();
        }
    }

    private static void PresentMissionRetreatOutcome(AuthorityClientOutcome<NetworkBattleMissionRetreatResolved> outcome)
    {
        if (!outcome.Applied)
            Logger.Information("Server refused battle mission retreat: {Reason}", outcome.ReasonCode);
    }

    private static void PresentBreakInOutcome(AuthorityClientOutcome<NetworkBreakInCasualtiesResolved> outcome)
    {
        if (!outcome.Applied)
            Logger.Warning("Server did not apply break-in casualties: {Reason}", outcome.ReasonCode);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<BattleRetreatAttempted>(HandleAttempt);
        messageBroker.Unsubscribe<BattleMissionRetreatAttempted>(HandleMissionRetreat);
        messageBroker.Unsubscribe<BreakInCasualtiesAttempted>(HandleBreakInCasualties);
        messageBroker.Unsubscribe<NetworkBattleRetreatCampsCleared>(HandleCampsCleared);
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
