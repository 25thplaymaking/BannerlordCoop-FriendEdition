using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.MapEventParties.Messages;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Extensions;
using GameInterface.Services.ObjectManager;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEventParties.Handlers;

internal readonly struct MapEventPartySnapshotIntent
{
    public MapEventPartySnapshotIntent(string mapEventPartyId, int hostEpoch)
    {
        MapEventPartyId = mapEventPartyId;
        HostEpoch = hostEpoch;
    }

    public string MapEventPartyId { get; }
    public int HostEpoch { get; }
}

internal class MapEventPartyHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<MapEventPartyHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IBattleHostRegistry hostRegistry;
    private readonly IAuthorityRouteHandle<MapEventPartySnapshotIntent, NetworkMapEventPartyUpdateResult> snapshotRoute;
    private readonly Dictionary<string, MapEventPartySnapshotProof> snapshotProofs = new();

    public MapEventPartyHandler(IMessageBroker messageBroker, INetwork network, IObjectManager objectManager,
        IModConfigAuthority configAuthority, IBattleHostRegistry hostRegistry,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.configAuthority = configAuthority;
        this.hostRegistry = hostRegistry;

        snapshotRoute = authorityRequestRouter.Register(
            AuthorityRoute<MapEventPartySnapshotIntent, NetworkRequestMapEventPartyUpdate,
                NetworkMapEventPartyUpdateResult>.Define(
                routeId: "map-event-party.snapshot",
                kind: AuthorityRouteKind.BootstrapQuery,
                createHeader: CreateHeader,
                buildRequest: (intent, header) => new NetworkRequestMapEventPartyUpdate(
                    header, intent.MapEventPartyId, intent.HostEpoch),
                readRequestHeader: request => request.Header,
                readResultHeader: result => result.Header,
                validateWireShape: ValidateSnapshotWireShape,
                buildCommandKey: request => string.Concat(
                    request.MapEventPartyId.Length, ":", request.MapEventPartyId, ":", request.HostEpoch),
                validateHeader: ValidateHeader,
                execute: ExecuteSnapshot,
                createTerminalResult: (header, status, reason) => new NetworkMapEventPartyUpdateResult(
                    header, status, null, null, 0, null, reason),
                probeClientCommit: ProbeSnapshotCommit,
                requestResync: _ => { },
                presentTerminalOutcome: PresentSnapshotOutcome,
                isTrustedResultSource: configAuthority.IsTrustedServer,
                timeoutPolicy: AuthorityTimeoutPolicy.BootstrapQuery,
                failClosedOnApplyFailure: true,
                isExpectedClientResult: (request, result) =>
                    string.Equals(request.MapEventPartyId, result.MapEventPartyId, StringComparison.Ordinal) &&
                    request.HostEpoch == result.HostEpoch));

        messageBroker.Subscribe<OnTroopKilledAttempted>(Handle_OnTroopKilledAttempted);
        messageBroker.Subscribe<NetworkTroopKilled>(Handle_NetworkTroopKilled);

        messageBroker.Subscribe<OnTroopWoundedAttempted>(Handle_OnTroopWoundedAttempted);
        messageBroker.Subscribe<NetworkTroopWounded>(Handle_NetworkTroopWounded);

        messageBroker.Subscribe<OnTroopRoutedAttempted>(Handle_OnTroopRoutedAttempted);
        messageBroker.Subscribe<NetworkTroopRouted>(Handle_NetworkTroopRouted);

        messageBroker.Subscribe<OnTroopScoreHitAttempted>(Handle_OnTroopScoreHitAttempted);
        messageBroker.Subscribe<NetworkTroopScoreHit>(Handle_NetworkTroopScoreHit);

        // Client
        messageBroker.Subscribe<RequestMapEventPartyUpdate>(Handle_RequestMapEventPartyUpdate);

        // Server
        messageBroker.Subscribe<MapEventPartyUpdated>(Handle_MapEventPartyUpdated);
        messageBroker.Subscribe<NetworkUpdateMapEventParty>(Handle_NetworkUpdateMapEventParty);
    }



    public void Dispose()
    {
        messageBroker.Unsubscribe<OnTroopKilledAttempted>(Handle_OnTroopKilledAttempted);
        messageBroker.Unsubscribe<NetworkTroopKilled>(Handle_NetworkTroopKilled);

        messageBroker.Unsubscribe<OnTroopWoundedAttempted>(Handle_OnTroopWoundedAttempted);
        messageBroker.Unsubscribe<NetworkTroopWounded>(Handle_NetworkTroopWounded);

        messageBroker.Unsubscribe<OnTroopRoutedAttempted>(Handle_OnTroopRoutedAttempted);
        messageBroker.Unsubscribe<NetworkTroopRouted>(Handle_NetworkTroopRouted);

        messageBroker.Unsubscribe<OnTroopScoreHitAttempted>(Handle_OnTroopScoreHitAttempted);
        messageBroker.Unsubscribe<NetworkTroopScoreHit>(Handle_NetworkTroopScoreHit);
        messageBroker.Unsubscribe<RequestMapEventPartyUpdate>(Handle_RequestMapEventPartyUpdate);
        messageBroker.Unsubscribe<MapEventPartyUpdated>(Handle_MapEventPartyUpdated);
        messageBroker.Unsubscribe<NetworkUpdateMapEventParty>(Handle_NetworkUpdateMapEventParty);
        snapshotRoute.Dispose();
        snapshotProofs.Clear();
    }

    private void Handle_RequestMapEventPartyUpdate(MessagePayload<RequestMapEventPartyUpdate> payload)
    {
        if (ModInformation.IsServer) return;

        if (!objectManager.TryGetIdWithLogging(payload.What.MapEventParty, out var mapEventPartyId))
            return;

        var mapEvent = payload.What.MapEventParty?.Party?.MapEvent;
        if (mapEvent == null || !objectManager.TryGetIdWithLogging(mapEvent, out var mapEventId) ||
            !hostRegistry.TryGet(mapEventId, out var assignment) || assignment.Epoch <= 0)
        {
            Logger.Warning("Map-event-party snapshot was not submitted because its host generation is unavailable");
            return;
        }

        snapshotRoute.Submit(new MapEventPartySnapshotIntent(mapEventPartyId, assignment.Epoch));
    }

    private void Handle_MapEventPartyUpdated(MessagePayload<MapEventPartyUpdated> payload)
    {
        if (ModInformation.IsClient) return;

        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.MapEventParty, out var mapEventPartyId))
            return;

        var flattenedTroops = FlattenedTroopSerializer.Serialize(obj.Roster, objectManager);

        var message = new NetworkUpdateMapEventParty(mapEventPartyId, flattenedTroops);
        network.SendAll(message);
    }

    private void Handle_NetworkUpdateMapEventParty(MessagePayload<NetworkUpdateMapEventParty> payload)
    {
        if (ModInformation.IsServer || !configAuthority.IsTrustedServer(payload.Who))
            return;

        var obj = payload.What;

        GameThread.Run(() =>
        {
            try
            {
                if (!objectManager.TryGetObjectWithLogging<MapEventParty>(obj.MapEventPartyId, out var mapEventParty))
                    return;

                mapEventParty._roster = FlattenedTroopSerializer.Deserialize(obj.FlattenedTroops, objectManager);

                if (obj.AuthorityRequestId > 0 && !string.IsNullOrEmpty(obj.SessionId) &&
                    !string.IsNullOrEmpty(obj.MapEventId) && obj.HostEpoch > 0 &&
                    string.Equals(obj.RosterFingerprint,
                        ComputeRosterFingerprint(obj.FlattenedTroops), StringComparison.Ordinal))
                {
                    snapshotProofs[ProofKey(obj.SessionId, obj.AuthorityRequestId)] =
                        new MapEventPartySnapshotProof(obj.SessionId, obj.AuthorityRequestId,
                            obj.MapEventId, obj.MapEventPartyId, obj.HostEpoch, obj.RosterFingerprint);
                }

                messageBroker.Publish(this, new MapEventTroopsUpdated(mapEventParty));
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to apply {Message}", nameof(NetworkUpdateMapEventParty));
            }
        });
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out var current)) return default;
        return new AuthorityRequestHeader(current.ProtocolVersion, current.SessionId, requestId, current.Revision);
    }

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out var current))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        if (header.ProtocolVersion != current.ProtocolVersion ||
            !string.Equals(header.SessionId, current.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session");
        return header.ExpectedRevision == current.Revision
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
    }

    private static string ValidateSnapshotWireShape(NetworkRequestMapEventPartyUpdate request) =>
        string.IsNullOrWhiteSpace(request.MapEventPartyId) || request.MapEventPartyId.Length > 256 ||
        request.HostEpoch <= 0
            ? "invalid-map-event-party-snapshot"
            : null;

    private AuthorityServerReply<NetworkMapEventPartyUpdateResult> ExecuteSnapshot(
        AuthorityServerContext context, NetworkRequestMapEventPartyUpdate request)
    {
        if (!objectManager.TryGetObject<MobileParty>(context.Player.MobilePartyId, out var playerParty) ||
            playerParty?.Party == null)
            return SnapshotReply(context.Header, request, AuthorityResultStatus.Rejected,
                null, null, "player-party-not-found");

        var mapEvent = playerParty.Party.MapEvent;
        var mapEventParty = mapEvent?.FindMapEventParty(playerParty.Party);
        if (mapEvent == null || mapEventParty == null || mapEvent.IsFinalized)
            return SnapshotReply(context.Header, request, AuthorityResultStatus.Rejected,
                null, null, "player-map-event-not-live");
        if (!objectManager.TryGetId(mapEvent, out var mapEventId) ||
            !objectManager.TryGetId(mapEventParty, out var derivedMapEventPartyId))
            return SnapshotReply(context.Header, request, AuthorityResultStatus.Unavailable,
                null, null, "map-event-party-unregistered");
        if (!string.Equals(request.MapEventPartyId, derivedMapEventPartyId, StringComparison.Ordinal))
            return SnapshotReply(context.Header, request, AuthorityResultStatus.Rejected,
                mapEventId, null, "map-event-party-mismatch");
        if (!hostRegistry.TryGet(mapEventId, out var assignment) || assignment.Epoch <= 0)
            return SnapshotReply(context.Header, request, AuthorityResultStatus.Unavailable,
                mapEventId, null, "battle-host-not-ready");
        if (assignment.Epoch != request.HostEpoch)
            return SnapshotReply(context.Header, request, AuthorityResultStatus.StaleState,
                mapEventId, null, "stale-host-epoch");

        FlattenedTroop[] flattenedTroops;
        string fingerprint;
        try
        {
            flattenedTroops = FlattenedTroopSerializer.Serialize(mapEventParty._roster, objectManager);
            fingerprint = ComputeRosterFingerprint(flattenedTroops);
            network.Send(context.Peer, new NetworkUpdateMapEventParty(
                derivedMapEventPartyId, flattenedTroops, mapEventId, request.HostEpoch,
                context.Header, fingerprint));
        }
        catch (Exception exception)
        {
            Logger.Error(exception,
                "Failed to publish canonical map-event-party snapshot. RequestId={RequestId} Party={PartyId}",
                context.Header.RequestId, derivedMapEventPartyId);
            return SnapshotReply(context.Header, request, AuthorityResultStatus.ExecutionFailed,
                mapEventId, null, "snapshot-publication-failed");
        }

        return SnapshotReply(context.Header, request, AuthorityResultStatus.Accepted,
            mapEventId, fingerprint, null, statePublished: true);
    }

    private static AuthorityServerReply<NetworkMapEventPartyUpdateResult> SnapshotReply(
        AuthorityRequestHeader header, NetworkRequestMapEventPartyUpdate request,
        AuthorityResultStatus status, string mapEventId, string fingerprint, string reasonCode,
        bool statePublished = false) =>
        new(new NetworkMapEventPartyUpdateResult(header, status, mapEventId,
            request.MapEventPartyId, request.HostEpoch, fingerprint, reasonCode), statePublished);

    private AuthorityCommitProbeResult ProbeSnapshotCommit(NetworkMapEventPartyUpdateResult result)
    {
        if (result.Status != AuthorityResultStatus.Accepted || string.IsNullOrEmpty(result.RosterFingerprint))
            return AuthorityCommitProbeResult.Invalid;
        if (!snapshotProofs.TryGetValue(ProofKey(result.SessionId, result.AuthorityRequestId), out var proof) ||
            !proof.Matches(result))
            return AuthorityCommitProbeResult.Pending;
        if (!objectManager.TryGetObject<MapEventParty>(result.MapEventPartyId, out var mapEventParty) ||
            mapEventParty?.Party?.MapEvent == null ||
            !objectManager.TryGetId(mapEventParty.Party.MapEvent, out var mapEventId) ||
            !string.Equals(mapEventId, result.MapEventId, StringComparison.Ordinal))
            return AuthorityCommitProbeResult.Pending;

        var fingerprint = ComputeRosterFingerprint(
            FlattenedTroopSerializer.Serialize(mapEventParty._roster, objectManager));
        if (!string.Equals(fingerprint, result.RosterFingerprint, StringComparison.Ordinal))
            return AuthorityCommitProbeResult.Pending;

        snapshotProofs.Remove(ProofKey(result.SessionId, result.AuthorityRequestId));
        return AuthorityCommitProbeResult.Applied;
    }

    private static void PresentSnapshotOutcome(AuthorityClientOutcome<NetworkMapEventPartyUpdateResult> outcome)
    {
        if (outcome.Applied) return;
        Logger.Warning("Map-event-party snapshot did not apply. Completion={Completion} Reason={Reason}",
            outcome.Completion, outcome.ReasonCode);
    }

    internal static string ComputeRosterFingerprint(FlattenedTroop[] troops)
    {
        var builder = new StringBuilder();
        foreach (var troop in troops ?? Array.Empty<FlattenedTroop>())
        {
            string id = troop.ObjectId ?? string.Empty;
            builder.Append(id.Length).Append(':').Append(id).Append('|')
                .Append(troop.IsHero ? '1' : '0').Append('|')
                .Append(troop.UniqueSeed.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(((int)troop.State).ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(troop.Xp.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(troop.XpGained.ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        using var sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())))
            .Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string ProofKey(string sessionId, long requestId) =>
        string.Concat(sessionId ?? string.Empty, ":", requestId);

    private void Handle_OnTroopKilledAttempted(MessagePayload<OnTroopKilledAttempted> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.MapEventParty, out var mapEventPartyId))
            return;

        var message = new NetworkTroopKilled(mapEventPartyId, obj.TroopSeed);

        network.SendAll(message);
    }

    private void Handle_NetworkTroopKilled(MessagePayload<NetworkTroopKilled> payload)
    {
        var obj = payload.What;

        GameThread.Run(() =>
        {
            try
            {
                if (!objectManager.TryGetObjectWithLogging(obj.MapEventPartyId, out MapEventParty mapEventParty))
                    return;

                var troopDescriptor = new UniqueTroopDescriptor(obj.TroopSeed);

                if (ModInformation.IsServer)
                {
                    mapEventParty.OnTroopKilled(troopDescriptor);
                }
                else
                {
                    // Only the scoreboard tally; Party.MemberRoster arrives separately.
                    using (new AllowedThread())
                    {
                        mapEventParty.Troops.OnTroopKilled(troopDescriptor);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error handling NetworkTroopKilled message for MapEventParty with ID {MapEventPartyId}", obj.MapEventPartyId);
            }
        });
    }

    private void Handle_OnTroopWoundedAttempted(MessagePayload<OnTroopWoundedAttempted> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.MapEventParty, out var mapEventPartyId))
            return;

        var message = new NetworkTroopWounded(mapEventPartyId, obj.TroopSeed);

        network.SendAll(message);
    }

    private void Handle_NetworkTroopWounded(MessagePayload<NetworkTroopWounded> payload)
    {
        var obj = payload.What;

        GameThread.Run(() =>
        {
            try
            {
                if (!objectManager.TryGetObjectWithLogging(obj.MapEventPartyId, out MapEventParty mapEventParty))
                    return;

                var troopDescriptor = new UniqueTroopDescriptor(obj.TroopSeed);

                if (ModInformation.IsServer)
                {
                    mapEventParty.OnTroopWounded(troopDescriptor);
                }
                else
                {
                    // Only the scoreboard tally; Party.MemberRoster arrives separately.
                    using (new AllowedThread())
                    {
                        mapEventParty.Troops.OnTroopWounded(troopDescriptor);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error handling NetworkTroopWounded message for MapEventParty with ID {MapEventPartyId}", obj.MapEventPartyId);
            }
        });
    }

    private void Handle_OnTroopScoreHitAttempted(MessagePayload<OnTroopScoreHitAttempted> payload)
    {
        var obj = payload.What;

        if (ModInformation.IsServer) return;

        if (!objectManager.TryGetIdWithLogging(obj.MapEventParty, out var mapEventPartyId))
            return;

        if (!objectManager.TryGetIdWithLogging(obj.AttackingTroop, out var attackingTroopId))
            return;

        if (!objectManager.TryGetIdWithLogging(obj.AttackedTroop, out var attackedTroopId))
            return;

        network.SendAll(new NetworkTroopScoreHit(
            mapEventPartyId,
            attackingTroopId,
            attackedTroopId,
            obj.Damage,
            obj.IsFatal,
            obj.IsSimulatedHit));
    }

    private void Handle_NetworkTroopScoreHit(MessagePayload<NetworkTroopScoreHit> payload)
    {
        // Server-authoritative: clients receive the resulting contribution through the
        // MapEventParty._contributionToBattle autosync and the roster xp through the roster sync.
        if (ModInformation.IsClient) return;

        var obj = payload.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging(obj.MapEventPartyId, out MapEventParty mapEventParty))
                return;

            if (!objectManager.TryGetObjectWithLogging(obj.AttackingTroopId, out CharacterObject attackingTroop))
                return;

            if (!objectManager.TryGetObjectWithLogging(obj.AttackedTroopId, out CharacterObject attackedTroop))
                return;

            ApplyTroopScoreHit(
                mapEventParty,
                attackingTroop,
                attackedTroop,
                obj.Damage,
                obj.IsFatal,
                obj.IsSimulatedHit);
        }, context: nameof(Handle_NetworkTroopScoreHit));
    }

    private void ApplyTroopScoreHit(
        MapEventParty mapEventParty,
        CharacterObject attackingTroop,
        CharacterObject attackedTroop,
        int damage,
        bool isFatal,
        bool isSimulatedHit)
    {
        var roster = mapEventParty.Troops;
        UniqueTroopDescriptor? fallbackDescriptor = null;
        if (roster != null)
        {
            foreach (var element in roster)
            {
                if (element.Troop != attackingTroop) continue;
                fallbackDescriptor ??= element.Descriptor;
                if (element.IsKilled || element.IsWounded || element.IsRouted) continue;

                // The attacker's weapon is not carried over the wire; native simulation also passes null.
                mapEventParty.OnTroopScoreHit(
                    element.Descriptor,
                    attackedTroop,
                    damage,
                    isFatal,
                    isTeamKill: false,
                    null,
                    isSimulatedHit);
                return;
            }
        }

        if (fallbackDescriptor.HasValue)
        {
            // The score message may arrive after the matching attacker became a casualty.
            mapEventParty.OnTroopScoreHit(
                fallbackDescriptor.Value,
                attackedTroop,
                damage,
                isFatal,
                isTeamKill: false,
                null,
                isSimulatedHit);
            return;
        }

        Logger.Warning(
            "Score hit for {AttackingTroop} dropped: no matching troop in party {Party}'s current roster",
            attackingTroop.StringId,
            mapEventParty.Party?.Id);
    }

    private void Handle_OnTroopRoutedAttempted(MessagePayload<OnTroopRoutedAttempted> payload)
    {
        var obj = payload.What;

        if (!objectManager.TryGetIdWithLogging(obj.MapEventParty, out var mapEventPartyId))
            return;

        var message = new NetworkTroopRouted(mapEventPartyId, obj.TroopSeed);

        network.SendAll(message);
    }

    private void Handle_NetworkTroopRouted(MessagePayload<NetworkTroopRouted> payload)
    {
        var obj = payload.What;

        GameThread.Run(() =>
        {
            try
            {
                if (!objectManager.TryGetObjectWithLogging(obj.MapEventPartyId, out MapEventParty mapEventParty))
                    return;

                var troopDescriptor = new UniqueTroopDescriptor(obj.TroopSeed);

                if (ModInformation.IsServer)
                {
                    mapEventParty.OnTroopRouted(troopDescriptor);
                }
                // Only the scoreboard tally (non-hero routs only, matching vanilla);
                // Party.MemberRoster arrives separately.
                else if (!mapEventParty.Troops[troopDescriptor].Troop.IsHero)
                {
                    using (new AllowedThread())
                    {
                        mapEventParty.Troops.OnTroopRouted(troopDescriptor);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error handling NetworkTroopRouted message for MapEventParty with ID {MapEventPartyId}", obj.MapEventPartyId);
            }
        });
    }

    private readonly struct MapEventPartySnapshotProof
    {
        public MapEventPartySnapshotProof(string sessionId, long requestId, string mapEventId,
            string mapEventPartyId, int hostEpoch, string rosterFingerprint)
        {
            SessionId = sessionId;
            RequestId = requestId;
            MapEventId = mapEventId;
            MapEventPartyId = mapEventPartyId;
            HostEpoch = hostEpoch;
            RosterFingerprint = rosterFingerprint;
        }

        public string SessionId { get; }
        public long RequestId { get; }
        public string MapEventId { get; }
        public string MapEventPartyId { get; }
        public int HostEpoch { get; }
        public string RosterFingerprint { get; }

        public bool Matches(NetworkMapEventPartyUpdateResult result) =>
            string.Equals(SessionId, result.SessionId, StringComparison.Ordinal) &&
            RequestId == result.AuthorityRequestId &&
            string.Equals(MapEventId, result.MapEventId, StringComparison.Ordinal) &&
            string.Equals(MapEventPartyId, result.MapEventPartyId, StringComparison.Ordinal) &&
            HostEpoch == result.HostEpoch &&
            string.Equals(RosterFingerprint, result.RosterFingerprint, StringComparison.Ordinal);
    }
}
