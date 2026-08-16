using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Messages.Start;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Villages.Data;
using GameInterface.Services.Villages.Interfaces;
using Serilog;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents.Handlers;

internal readonly struct MapEventCreationResult
{
    public MapEventCreationOutcome Outcome { get; }
    public MapEvent MapEvent { get; }

    private MapEventCreationResult(MapEventCreationOutcome outcome, MapEvent mapEvent)
    {
        Outcome = outcome;
        MapEvent = mapEvent;
    }

    public static MapEventCreationResult Created(MapEvent mapEvent) =>
        new MapEventCreationResult(MapEventCreationOutcome.Created, mapEvent);

    public static MapEventCreationResult Rejected() =>
        new MapEventCreationResult(MapEventCreationOutcome.Rejected, null);

    public static MapEventCreationResult Unresolved() =>
        new MapEventCreationResult(MapEventCreationOutcome.Unresolved, null);
}

/// <summary>
/// The first live authority-router route. Map-event creation remains feature-owned; the shared
/// router owns correlation, replay, retry, terminal responses and the client application barrier.
/// </summary>
internal class MapEventCreationCoordinator : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<MapEventCreationCoordinator>();

    internal static MapEventCreationCoordinator Instance { get; private set; }

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    // Retained as the single authority timeout source and for compatibility with E2E fixture reflection.
    private readonly INetworkConfig configuration;
    private readonly IModConfigAuthority configAuthority;
    private readonly IVillageHostileActionInterface villageHostileActionInterface;
    private readonly IAuthorityRouteHandle<MapEventCreationIntent, NetworkMapEventCreated> mapEventRoute;

    internal AuthorityRequestLifecycle RequestLifecycle => mapEventRoute.Lifecycle;

    public MapEventCreationCoordinator(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetworkConfig configuration,
        IModConfigAuthority configAuthority,
        IVillageHostileActionInterface villageHostileActionInterface,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.configuration = configuration;
        this.configAuthority = configAuthority;
        this.villageHostileActionInterface = villageHostileActionInterface;

        mapEventRoute = authorityRequestRouter.Register(
            AuthorityRoute<MapEventCreationIntent, NetworkRequestCreateMapEvent, NetworkMapEventCreated>.Define(
                routeId: "map-event.create",
                kind: AuthorityRouteKind.Command,
                createHeader: CreateHeader,
                buildRequest: BuildRequest,
                readRequestHeader: request => request.Header,
                readResultHeader: result => result.Header,
                validateWireShape: ValidateWireShape,
                buildCommandKey: BuildCommandKey,
                validateHeader: ValidateHeader,
                execute: ExecuteAuthoritatively,
                createTerminalResult: CreateTerminalResult,
                probeClientCommit: ProbeClientCommit,
                requestResync: _ => { },
                presentTerminalOutcome: PresentTerminalOutcome,
                isTrustedResultSource: configAuthority.IsTrustedServer,
                timeoutPolicy: new AuthorityTimeoutPolicy(
                    configuration.ObjectCreationTimeout,
                    configuration.ObjectCreationTimeout,
                    retryCount: 1),
                failClosedOnApplyFailure: true));

        Instance = this;
    }

    public void Dispose()
    {
        mapEventRoute.Dispose();
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Blocks until canonical creation is applied. An accepted result without an applied graph is
    /// deliberately fail-closed by the shared route: reliable ordered transport delivered the graph
    /// messages, so reconnect/save-transfer is the only existing complete reconstruction path.
    /// </summary>
    public MapEventCreationResult RequestBlocking(PartyBase attacker, PartyBase defender, BattleCreationFlags flags)
    {
        if (attacker == null || defender == null)
        {
            Logger.Error("Cannot request map event creation with a null attacker or defender party");
            return MapEventCreationResult.Unresolved();
        }

        if (!objectManager.TryGetIdWithLogging(attacker, out var attackerId) ||
            !objectManager.TryGetIdWithLogging(defender, out var defenderId))
            return MapEventCreationResult.Unresolved();

        string expectedMapEventId = null;
        MapEvent expectedMapEvent = attacker.MapEvent ?? defender.MapEvent;
        if (expectedMapEvent != null && !objectManager.TryGetIdWithLogging(expectedMapEvent, out expectedMapEventId))
            return MapEventCreationResult.Unresolved();

        var outcome = mapEventRoute.SubmitBlocking(
            new MapEventCreationIntent(attackerId, defenderId, flags, expectedMapEventId));
        if (outcome.Applied && outcome.Result.Outcome == MapEventCreationOutcome.Created &&
            objectManager.TryGetObject(outcome.Result.MapEventId, out MapEvent mapEvent) && mapEvent != null)
            return MapEventCreationResult.Created(mapEvent);

        if (outcome.Completion == AuthorityClientCompletion.Rejected)
            return MapEventCreationResult.Rejected();

        Logger.Error(
            "Authoritative map event creation remained unresolved. Completion={Completion} Reason={Reason}",
            outcome.Completion,
            outcome.ReasonCode);
        return MapEventCreationResult.Unresolved();
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot config)) return default;
        return new AuthorityRequestHeader(config.ProtocolVersion, config.SessionId, requestId, config.Revision);
    }

    private static NetworkRequestCreateMapEvent BuildRequest(
        MapEventCreationIntent intent,
        AuthorityRequestHeader header) =>
        new NetworkRequestCreateMapEvent(header, intent.AttackerId, intent.DefenderId, intent.Flags, intent.ExpectedMapEventId);

    private static string ValidateWireShape(NetworkRequestCreateMapEvent request)
    {
        if (string.IsNullOrWhiteSpace(request.AttackerId) || request.AttackerId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.DefenderId) || request.DefenderId.Length > 256 ||
            request.ExpectedMapEventId?.Length > 256)
            return "invalid-map-event-identifiers";

        return null;
    }

    private static string BuildCommandKey(NetworkRequestCreateMapEvent request)
    {
        // Length-prefix every variable field so distinct stable-id tuples cannot collide through
        // a delimiter embedded in an object id.
        static string Field(string value) => $"{value?.Length ?? -1}:{value ?? string.Empty}";
        return string.Concat(
            Field(request.AttackerId), Field(request.DefenderId), Field(request.ExpectedMapEventId),
            request.ForceRaid ? "1" : "0", request.ForceSallyOut ? "1" : "0",
            request.ForceVolunteers ? "1" : "0", request.ForceSupplies ? "1" : "0",
            request.IsSallyOutAmbush ? "1" : "0", request.ForceBlockadeAttack ? "1" : "0",
            request.ForceBlockadeSallyOutAttack ? "1" : "0", request.ForceHideoutSendTroops ? "1" : "0");
    }

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot config))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "authority-config-unavailable");
        if (header.ProtocolVersion != config.ProtocolVersion ||
            !string.Equals(header.SessionId, config.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-config-session");
        if (header.ExpectedRevision != config.Revision)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-config-revision");

        return AuthorityHeaderValidation.Valid;
    }

    private AuthorityServerReply<NetworkMapEventCreated> ExecuteAuthoritatively(
        AuthorityServerContext context,
        NetworkRequestCreateMapEvent request)
    {
        bool joined = false;
        string reservationControllerId = null;
        Guid reservationId = Guid.NewGuid();
        if (!string.IsNullOrEmpty(request.ExpectedMapEventId) && !string.IsNullOrEmpty(context.Player?.ControllerId))
        {
            reservationControllerId = context.Player.ControllerId;
            messageBroker.Publish(context.Peer, new BattleJoinAccepted(
                request.ExpectedMapEventId,
                reservationControllerId,
                reservationId));
        }

        try
        {
            if (!TryResolveRequestParties(request, out PartyBase attacker, out PartyBase defender))
                return Rejected(context.Header, request, "party-not-found");

            if (!objectManager.TryGetObject<MobileParty>(context.Player.MobilePartyId, out MobileParty requestingParty) ||
                (!ReferenceEquals(attacker.MobileParty, requestingParty) &&
                 !ReferenceEquals(defender.MobileParty, requestingParty)))
                return Rejected(context.Header, request, "party-not-controlled");

            if (TryHandleExistingMapEventRequest(
                    request,
                    attacker,
                    defender,
                    requestingParty,
                    out MapEventCreationOutcome existingOutcome,
                    out string existingMapEventId,
                    out joined))
                return ReplyForOutcome(context.Header, request, existingOutcome, existingMapEventId);

            if (!TryConsumeApprovedMapEventStart(request, attacker, defender))
                return Rejected(context.Header, request, "hostile-action-not-approved");

            var creation = CreateMapEvent(request, attacker, defender);
            return ReplyForOutcome(context.Header, request, creation.Outcome, creation.MapEventId);
        }
        finally
        {
            if (!joined && reservationControllerId != null)
            {
                messageBroker.Publish(context.Peer, new BattleJoinCancelled(
                    request.ExpectedMapEventId,
                    reservationControllerId,
                    reservationId));
            }
        }
    }

    private AuthorityServerReply<NetworkMapEventCreated> ReplyForOutcome(
        AuthorityRequestHeader header,
        NetworkRequestCreateMapEvent request,
        MapEventCreationOutcome outcome,
        string mapEventId)
    {
        if (outcome == MapEventCreationOutcome.Created && !string.IsNullOrEmpty(mapEventId))
        {
            return new AuthorityServerReply<NetworkMapEventCreated>(
                new NetworkMapEventCreated(header, AuthorityResultStatus.Accepted, outcome, mapEventId,
                    null, request.AttackerId, request.DefenderId, header.ExpectedRevision),
                statePublished: true);
        }

        if (outcome == MapEventCreationOutcome.Rejected)
            return Rejected(header, request, "map-event-rejected");

        return new AuthorityServerReply<NetworkMapEventCreated>(
            new NetworkMapEventCreated(header, AuthorityResultStatus.ExecutionFailed, outcome, null,
                "map-event-unresolved", request.AttackerId, request.DefenderId, header.ExpectedRevision),
            statePublished: false);
    }

    private static AuthorityServerReply<NetworkMapEventCreated> Rejected(
        AuthorityRequestHeader header,
        NetworkRequestCreateMapEvent request,
        string reasonCode) =>
        new AuthorityServerReply<NetworkMapEventCreated>(
            new NetworkMapEventCreated(header, AuthorityResultStatus.Rejected, MapEventCreationOutcome.Rejected,
                null, reasonCode, request.AttackerId, request.DefenderId, header.ExpectedRevision),
            statePublished: false);

    private static NetworkMapEventCreated CreateTerminalResult(
        AuthorityRequestHeader header,
        AuthorityResultStatus status,
        string reasonCode) =>
        new NetworkMapEventCreated(
            header,
            status,
            status == AuthorityResultStatus.Accepted ? MapEventCreationOutcome.Created : MapEventCreationOutcome.Rejected,
            null,
            reasonCode,
            null,
            null,
            header.ExpectedRevision);

    private AuthorityCommitProbeResult ProbeClientCommit(NetworkMapEventCreated result)
    {
        if (result.Status != AuthorityResultStatus.Accepted || string.IsNullOrEmpty(result.MapEventId) ||
            string.IsNullOrEmpty(result.AttackerId) || string.IsNullOrEmpty(result.DefenderId))
            return AuthorityCommitProbeResult.Invalid;

        if (!objectManager.TryGetObject(result.MapEventId, out MapEvent mapEvent) || mapEvent == null ||
            !objectManager.TryGetObject(result.AttackerId, out PartyBase attacker) || attacker == null ||
            !objectManager.TryGetObject(result.DefenderId, out PartyBase defender) || defender == null ||
            Campaign.Current?.MapEventManager == null)
            return AuthorityCommitProbeResult.Pending;

        return Campaign.Current.MapEventManager.MapEvents.Contains(mapEvent) &&
            ReferenceEquals(attacker.MapEvent, mapEvent) && ReferenceEquals(defender.MapEvent, mapEvent)
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;
    }

    private void PresentTerminalOutcome(AuthorityClientOutcome<NetworkMapEventCreated> outcome)
    {
        if (outcome.Completion == AuthorityClientCompletion.ReplicaApplyFailed)
        {
            Logger.Error(
                "Map event authoritative state did not apply; disconnecting for save-transfer reconstruction. Reason={Reason}",
                outcome.ReasonCode);
        }
    }

    private bool TryResolveRequestParties(
        NetworkRequestCreateMapEvent request,
        out PartyBase attacker,
        out PartyBase defender)
    {
        attacker = null;
        defender = null;
        return objectManager.TryGetObjectWithLogging(request.AttackerId, out attacker) &&
            objectManager.TryGetObjectWithLogging(request.DefenderId, out defender);
    }

    private bool TryConsumeApprovedMapEventStart(
        NetworkRequestCreateMapEvent request,
        PartyBase attacker,
        PartyBase defender)
    {
        if (villageHostileActionInterface.TryConsumeApprovedMapEventStart(attacker, defender, request.Flags, out VillageHostileActionDeniedReason reason))
            return true;

        Logger.Warning(
            "Rejecting hostile-action map event creation. RequestId={RequestId}, AttackerId={AttackerId}, DefenderId={DefenderId}, Reason={Reason}",
            request.AuthorityRequestId,
            request.AttackerId,
            request.DefenderId,
            reason);
        return false;
    }

    private bool TryHandleExistingMapEventRequest(
        NetworkRequestCreateMapEvent request,
        PartyBase attacker,
        PartyBase defender,
        MobileParty requestingParty,
        out MapEventCreationOutcome outcome,
        out string mapEventId,
        out bool joinedExistingBattle)
    {
        outcome = MapEventCreationOutcome.Rejected;
        mapEventId = null;
        joinedExistingBattle = false;
        MapEventSide attackerSide = attacker.MapEventSide;
        MapEventSide defenderSide = defender.MapEventSide;
        if (attackerSide == null && defenderSide == null)
            return !string.IsNullOrEmpty(request.ExpectedMapEventId);

        if (ReferenceEquals(attacker, defender) || request.Flags.IsForced) return true;

        if (attackerSide != null && defenderSide != null)
        {
            MapEvent attackerEvent = attackerSide.MapEvent;
            if (IsActiveFieldBattle(attackerEvent) && IsExpectedMapEvent(request, attackerEvent) &&
                ReferenceEquals(attackerEvent, defenderSide.MapEvent) &&
                ReferenceEquals(attackerSide.OtherSide, defenderSide))
            {
                outcome = objectManager.TryGetIdWithLogging(attackerEvent, out mapEventId)
                    ? MapEventCreationOutcome.Created
                    : MapEventCreationOutcome.Unresolved;
            }
            return true;
        }

        MapEventSide occupiedSide = attackerSide ?? defenderSide;
        PartyBase joiningParty = attackerSide == null ? attacker : defender;
        MapEvent mapEvent = occupiedSide?.MapEvent;
        MapEventSide joiningSide = occupiedSide?.OtherSide;
        MobileParty joiningMobileParty = joiningParty.MobileParty;
        if (!ReferenceEquals(joiningMobileParty, requestingParty) || !IsActiveFieldBattle(mapEvent) ||
            !IsExpectedMapEvent(request, mapEvent) || joiningSide == null || joiningMobileParty?.IsActive != true ||
            joiningMobileParty.CurrentSettlement != null || !CanJoinFieldBattle(joiningParty, joiningSide))
            return true;

        joiningParty.MapEventSide = joiningSide;
        outcome = objectManager.TryGetIdWithLogging(mapEvent, out mapEventId)
            ? MapEventCreationOutcome.Created
            : MapEventCreationOutcome.Unresolved;
        joinedExistingBattle = outcome == MapEventCreationOutcome.Created;
        return true;
    }

    private bool IsExpectedMapEvent(NetworkRequestCreateMapEvent request, MapEvent mapEvent) =>
        string.IsNullOrEmpty(request.ExpectedMapEventId) ||
        objectManager.TryGetId(mapEvent, out string mapEventId) && mapEventId == request.ExpectedMapEventId;

    private static bool IsActiveFieldBattle(MapEvent mapEvent) =>
        mapEvent?.IsFieldBattle == true && mapEvent.BattleState == BattleState.None && !mapEvent.IsFinalized;

    private static bool CanJoinFieldBattle(PartyBase party, MapEventSide side)
    {
        IFaction faction = party?.MapFaction;
        return faction != null && side?.OtherSide != null &&
            side.Parties.All(x => IsFactionCompatible(x?.Party, faction, false)) &&
            side.OtherSide.Parties.All(x => IsFactionCompatible(x?.Party, faction, true));
    }

    private static bool IsFactionCompatible(PartyBase involved, IFaction joining, bool hostile) =>
        involved?.MapFaction != null && involved.IsActive &&
        VillageHostileFactionStanceHelper.HasWarStance(involved.MapFaction, joining) == hostile;

    private (MapEventCreationOutcome Outcome, string MapEventId) CreateMapEvent(
        NetworkRequestCreateMapEvent request,
        PartyBase attacker,
        PartyBase defender)
    {
        var parties = GetMapEventParties(attacker, defender);
        MapEvent mapEvent = MapEventBattleFactory.CreateMapEvent(parties.Attacker, parties.Defender, request.Flags);
        if (mapEvent == null) return (MapEventCreationOutcome.Rejected, null);

        if (mapEvent.IsVillageHostileAction())
            MapEventHostileActionConsequences.Apply(mapEvent, parties.Attacker, "village hostile action start");

        if (!objectManager.TryGetIdWithLogging(mapEvent, out string mapEventId))
        {
            Logger.Error("Server created a map event but it has no registered id. RequestId={RequestId}", request.AuthorityRequestId);
            return (MapEventCreationOutcome.Unresolved, null);
        }

        return (MapEventCreationOutcome.Created, mapEventId);
    }

    private static (PartyBase Attacker, PartyBase Defender) GetMapEventParties(PartyBase attacker, PartyBase defender)
    {
        if (attacker.MobileParty?.IsPlayerParty() == true &&
            defender.MobileParty?.IsCurrentlyEngagingParty == true &&
            defender.MobileParty?.ShortTermTargetParty == attacker.MobileParty)
            return (defender, attacker);

        return (attacker, defender);
    }

    private readonly struct MapEventCreationIntent
    {
        public MapEventCreationIntent(string attackerId, string defenderId, BattleCreationFlags flags, string expectedMapEventId)
        {
            AttackerId = attackerId;
            DefenderId = defenderId;
            Flags = flags;
            ExpectedMapEventId = expectedMapEventId;
        }

        public string AttackerId { get; }
        public string DefenderId { get; }
        public BattleCreationFlags Flags { get; }
        public string ExpectedMapEventId { get; }
    }
}
