using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Tournaments.Data;
using GameInterface.Services.Tournaments.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.Core;

namespace GameInterface.Services.Tournaments.Handlers;

/// <summary>Authenticated host reports. The request router owns all request replay and conflict handling.</summary>
internal sealed partial class TournamentSessionHandler
{
    private static string ValidateSpawnManifestWireShape(NetworkSubmitTournamentSpawnManifest request) =>
        request.Manifest == null || !IsBoundedTournamentId(request.Manifest.SessionId) ||
        !IsBoundedTournamentId(request.Manifest.MatchId) || !IsBoundedTournamentId(request.MissionInstanceId) || request.Manifest.Sequence <= 0
            ? "invalid-spawn-manifest" : null;

    private static string ValidateHitProgressionWireShape(NetworkSubmitTournamentHitProgression request) =>
        !IsValidProgressionData(request.Data) || !IsBoundedTournamentId(request.MissionInstanceId) ? "invalid-hit-progression" : null;

    private static string ValidateMatchResultWireShape(NetworkSubmitTournamentMatchResult request) =>
        request.Result == null || !IsBoundedTournamentId(request.Result.SessionId) ||
        !IsBoundedTournamentId(request.Result.MatchId) || !IsBoundedTournamentId(request.MissionInstanceId) || request.Result.Sequence <= 0 ||
        request.Result.WinnerTeamIds == null || request.Result.WinnerSlotIds == null || request.Result.TeamScores == null ||
        request.Result.TeamScores.Any(score => score == null) ? "invalid-match-result" : null;

    private static string BuildSpawnManifestCommandKey(NetworkSubmitTournamentSpawnManifest request) =>
        string.Concat(FieldKey(request.Manifest.SessionId), FieldKey(request.Manifest.MatchId),
            request.Manifest.Revision.ToString(), ":", request.Manifest.BracketRevision.ToString(), ":", request.Manifest.Sequence.ToString());
    private static string BuildHitProgressionCommandKey(NetworkSubmitTournamentHitProgression request) =>
        string.Concat(FieldKey(request.Data.SessionId), FieldKey(request.Data.MatchId), FieldKey(request.Data.DamageOriginControllerId),
            request.Data.DamageSequence.ToString(), ":", request.Data.AttackerAgentId.ToString("N"), ":", request.Data.VictimAgentId.ToString("N"));
    private static string BuildMatchResultCommandKey(NetworkSubmitTournamentMatchResult request) =>
        string.Concat(FieldKey(request.Result.SessionId), FieldKey(request.Result.MatchId),
            request.Result.Revision.ToString(), ":", request.Result.BracketRevision.ToString(), ":", request.Result.Sequence.ToString());

    private AuthorityServerReply<NetworkTournamentSpawnManifestResult> ExecuteSpawnManifest(
        AuthorityServerContext context, NetworkSubmitTournamentSpawnManifest request)
    {
        TournamentSpawnManifestData submitted = request.Manifest;
        TournamentSessionSnapshot current = null;
        if (!sessionRegistry.TryGet(submitted.SessionId, out current) || current.Phase != TournamentSessionPhase.LiveMatch ||
            current.HostControllerId != context.Player.ControllerId || submitted.Revision != current.Revision ||
            submitted.BracketRevision != current.BracketRevision || submitted.MatchId != current.CurrentMatchId || request.MissionInstanceId != current.MissionInstanceId ||
            !TournamentSpawnManifestValidator.IsValid(submitted, current) || !HasValidManifestObjects(submitted))
        {
            SendCanonical(context.Peer, current);
            return new AuthorityServerReply<NetworkTournamentSpawnManifestResult>(
                new NetworkTournamentSpawnManifestResult(context.Header, HostStatus(current, submitted?.Revision, submitted?.BracketRevision),
                    null, current, "stale-or-invalid-spawn-manifest"), false);
        }

        TournamentSpawnManifestData manifest = NormalizeHostManifest(submitted);
        TournamentMutationStatus status = sessionRegistry.TryStoreSpawnManifest(manifest, context.Player.ControllerId, out var snapshot);
        if (status != TournamentMutationStatus.Applied)
        {
            SendCanonical(context.Peer, snapshot);
            return new AuthorityServerReply<NetworkTournamentSpawnManifestResult>(
                new NetworkTournamentSpawnManifestResult(context.Header, MutationStatus(status), null, snapshot, "spawn-manifest-" + status), false);
        }

        network.SendAll(new NetworkTournamentSpawnManifest(manifest));
        messageBroker.Publish(this, new TournamentSpawnManifestUpdated(manifest));
        return new AuthorityServerReply<NetworkTournamentSpawnManifestResult>(
            new NetworkTournamentSpawnManifestResult(context.Header, AuthorityResultStatus.Accepted, manifest, snapshot, null), true);
    }

    private AuthorityServerReply<NetworkTournamentHitProgressionApplied> ExecuteHitProgression(
        AuthorityServerContext context, NetworkSubmitTournamentHitProgression request)
    {
        TournamentHitProgressionData data = request.Data;
        TournamentSessionSnapshot snapshot = null;
        if (!sessionRegistry.TryGet(data.SessionId, out snapshot) || snapshot.Phase != TournamentSessionPhase.LiveMatch ||
            snapshot.HostControllerId != context.Player.ControllerId || snapshot.CurrentMatchId != data.MatchId ||
            snapshot.Revision != data.Revision || snapshot.BracketRevision != data.BracketRevision || request.MissionInstanceId != snapshot.MissionInstanceId ||
            !sessionRegistry.TryGetSpawnManifest(data.SessionId, out var manifest) || manifest.MatchId != data.MatchId ||
            manifest.BracketRevision != data.BracketRevision ||
            !TryResolveManifestCharacter(manifest, data.AttackerAgentId, out var attackerData, out var attacker, true) ||
            !TryResolveManifestCharacter(manifest, data.VictimAgentId, out _, out var victim) ||
            !TryResolveWeapon(data, out var weapon))
        {
            SendCanonical(context.Peer, snapshot);
            return new AuthorityServerReply<NetworkTournamentHitProgressionApplied>(
                new NetworkTournamentHitProgressionApplied(context.Header, HostStatus(snapshot, data?.Revision, data?.BracketRevision), snapshot, data, null, "stale-or-invalid-hit"), false);
        }
        TournamentContestantData contestant = snapshot.Contestants.FirstOrDefault(candidate => candidate.SlotId == attackerData.SlotId);
        if (contestant == null || !contestant.IsHuman || contestant.IsReplaced || contestant.ControllerId != data.DamageOriginControllerId)
        {
            SendCanonical(context.Peer, snapshot);
            return new AuthorityServerReply<NetworkTournamentHitProgressionApplied>(
                new NetworkTournamentHitProgressionApplied(context.Header, AuthorityResultStatus.Unauthorized, snapshot, data, attacker.StringId, "spoofed-hit-origin"), false);
        }

        string key = HitProgressionKey(data);
        if (!consumedHitProgression.Add(key))
            return new AuthorityServerReply<NetworkTournamentHitProgressionApplied>(
                new NetworkTournamentHitProgressionApplied(context.Header, AuthorityResultStatus.Rejected, snapshot, data, attacker.StringId, "duplicate-damage-sequence"), false);
        try
        {
            // The consumed watermark precedes the native side effect: a throw is never replayed.
            SkillLevelingManager.OnCombatHit(attacker, victim, null, null, data.MovementSpeedModifier, data.ShotDifficulty,
                weapon, data.HitpointRatio, CombatXpModel.MissionTypeEnum.Tournament, data.AttackerMounted, data.SameTeam,
                false, data.DamageAmount, data.Fatal, false, data.Charging, data.SneakAttack);
            acceptedHitProgression.Add(key);
            liveProgressionControllers.Add(ProgressionControllerKey(data.SessionId, data.DamageOriginControllerId));
            var applied = new NetworkTournamentHitProgressionApplied(context.Header, AuthorityResultStatus.Accepted,
                snapshot, data, attacker.StringId, null);
            network.SendAll(applied);
            return new AuthorityServerReply<NetworkTournamentHitProgressionApplied>(applied, true);
        }
        catch (Exception exception)
        {
            Logger.Fatal(exception, "[Tournament] irreversible hit progression failed; terminating all tournament peers.");
            DisconnectAllTournamentClients();
            return new AuthorityServerReply<NetworkTournamentHitProgressionApplied>(
                new NetworkTournamentHitProgressionApplied(context.Header, AuthorityResultStatus.ExecutionFailed, snapshot, data,
                    attacker.StringId, "irreversible-hit-failure"), false, suppressReply: true);
        }
    }

    private AuthorityServerReply<NetworkTournamentMatchResultApplied> ExecuteMatchResult(
        AuthorityServerContext context, NetworkSubmitTournamentMatchResult request)
    {
        TournamentMatchResultData result = request.Result;
        TournamentSessionSnapshot current = null;
        if (!sessionRegistry.TryGet(result.SessionId, out current) || current.Phase != TournamentSessionPhase.LiveMatch ||
            current.HostControllerId != context.Player.ControllerId || result.Revision != current.Revision ||
            result.BracketRevision != current.BracketRevision || result.MatchId != current.CurrentMatchId || request.MissionInstanceId != current.MissionInstanceId ||
            !sessionRegistry.TryGetSpawnManifest(result.SessionId, out var manifest) || manifest.MatchId != current.CurrentMatchId ||
            manifest.BracketRevision != current.BracketRevision || manifest.Revision > current.Revision)
        {
            SendCanonical(context.Peer, current);
            return new AuthorityServerReply<NetworkTournamentMatchResultApplied>(
                new NetworkTournamentMatchResultApplied(context.Header, HostStatus(current, result?.Revision, result?.BracketRevision), current, result, "stale-or-invalid-match-result"), false);
        }
        if (!tournamentGameInterface.TryAdvanceBracket(current, result, out var bracket))
            return new AuthorityServerReply<NetworkTournamentMatchResultApplied>(
                new NetworkTournamentMatchResultApplied(context.Header, AuthorityResultStatus.Rejected, current, result, "invalid-bracket-result"), false);

        IReadOnlyDictionary<string, int> scores = TournamentStateReconciliation.ReconcileContestantScores(current,
            bracket.ContestantScores, out _);
        TournamentMutationStatus status = sessionRegistry.TryApplyMatchResult(result, context.Player.ControllerId, bracket.Rounds,
            scores, bracket.CurrentMatchId, bracket.WinnerSlotId, bracket.IsCompleted, out var snapshot);
        if (status != TournamentMutationStatus.Applied)
        {
            SendCanonical(context.Peer, snapshot);
            return new AuthorityServerReply<NetworkTournamentMatchResultApplied>(
                new NetworkTournamentMatchResultApplied(context.Header, MutationStatus(status), snapshot, result, "match-result-" + status), false);
        }
        try
        {
            ResolveBets(current, bracket.MatchWinnerSlotIds);
            BroadcastSnapshot(snapshot);
            if (bracket.IsCompleted) CompleteTournament(snapshot);
            var applied = new NetworkTournamentMatchResultApplied(context.Header, AuthorityResultStatus.Accepted, snapshot, result, null);
            network.SendAll(applied);
            return new AuthorityServerReply<NetworkTournamentMatchResultApplied>(applied, true);
        }
        catch (Exception exception)
        {
            Logger.Fatal(exception, "[Tournament] irreversible match-result publication failed; terminating all tournament peers.");
            DisconnectAllTournamentClients();
            return new AuthorityServerReply<NetworkTournamentMatchResultApplied>(
                new NetworkTournamentMatchResultApplied(context.Header, AuthorityResultStatus.ExecutionFailed, snapshot, result,
                    "irreversible-match-result-failure"), false, suppressReply: true);
        }
    }

    private static AuthorityResultStatus HostStatus(TournamentSessionSnapshot current, long? revision, long? bracketRevision) =>
        current != null && (revision < current.Revision || bracketRevision < current.BracketRevision)
            ? AuthorityResultStatus.StaleState : AuthorityResultStatus.Rejected;
    private static AuthorityResultStatus MutationStatus(TournamentMutationStatus status) =>
        status == TournamentMutationStatus.StaleRevision ? AuthorityResultStatus.StaleState : AuthorityResultStatus.Rejected;
    private static string HitProgressionKey(TournamentHitProgressionData data) =>
        $"{data.SessionId}\n{data.MatchId}\n{data.DamageOriginControllerId}\n{data.DamageSequence}";
    private static TournamentSpawnManifestData NormalizeHostManifest(TournamentSpawnManifestData manifest) =>
        new(manifest.SessionId, manifest.MatchId, manifest.Revision, manifest.BracketRevision, manifest.Sequence,
            manifest.Agents.ToArray());

    private static NetworkTournamentSpawnManifestResult CreateSpawnManifestTerminal(AuthorityRequestHeader h, AuthorityResultStatus s, string reason) =>
        new(h, s, null, null, reason);
    private static NetworkTournamentHitProgressionApplied CreateHitProgressionTerminal(AuthorityRequestHeader h, AuthorityResultStatus s, string reason) =>
        new(h, s, null, null, null, reason);
    private static NetworkTournamentMatchResultApplied CreateMatchResultTerminal(AuthorityRequestHeader h, AuthorityResultStatus s, string reason) =>
        new(h, s, null, null, reason);
    private AuthorityCommitProbeResult ProbeSpawnManifestApplied(NetworkTournamentSpawnManifestResult result) =>
        receivedSpawnManifests.TryGetValue(result.Manifest?.SessionId, out var manifest) && result.Status == AuthorityResultStatus.Accepted &&
        manifest.MatchId == result.Manifest?.MatchId && manifest.Sequence == result.Manifest?.Sequence ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    private AuthorityCommitProbeResult ProbeHitProgressionApplied(NetworkTournamentHitProgressionApplied result) =>
        result.Status == AuthorityResultStatus.Accepted && acceptedHitProgression.Contains(
            $"{result.TournamentSessionId}\n{result.MatchId}\n{result.DamageOriginControllerId}\n{result.DamageSequence}")
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    private AuthorityCommitProbeResult ProbeMatchResultApplied(NetworkTournamentMatchResultApplied result) =>
        sessionRegistry.TryGet(result.TournamentSessionId, out var snapshot) && result.Status == AuthorityResultStatus.Accepted &&
        snapshot.Revision == result.CommittedRevision && snapshot.BracketRevision == result.CommittedBracketRevision &&
        snapshot.CurrentMatchId == result.Snapshot?.CurrentMatchId && snapshot.IsCompleted == result.Snapshot?.IsCompleted
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    private static bool IsExpectedSpawnManifestResult(NetworkSubmitTournamentSpawnManifest request, NetworkTournamentSpawnManifestResult result) =>
        result.Status != AuthorityResultStatus.Accepted || result.Manifest?.SessionId == request.Manifest.SessionId &&
        result.Manifest.MatchId == request.Manifest.MatchId && result.Snapshot?.MissionInstanceId == request.MissionInstanceId;
    private static bool IsExpectedHitProgressionResult(NetworkSubmitTournamentHitProgression request, NetworkTournamentHitProgressionApplied result) =>
        result.Status != AuthorityResultStatus.Accepted || result.TournamentSessionId == request.Data.SessionId && result.MatchId == request.Data.MatchId &&
        result.DamageSequence == request.Data.DamageSequence && result.MissionInstanceId == request.MissionInstanceId;
    private static bool IsExpectedMatchResult(NetworkSubmitTournamentMatchResult request, NetworkTournamentMatchResultApplied result) =>
        result.Status != AuthorityResultStatus.Accepted || result.TournamentSessionId == request.Result.SessionId && result.MatchId == request.Result.MatchId &&
        result.Sequence == request.Result.Sequence && result.Snapshot?.MissionInstanceId == request.MissionInstanceId;
    private static void PresentHostReportTerminal<T>(AuthorityClientOutcome<T> outcome) where T : Common.Messaging.IMessage
    {
        if (outcome.Completion != AuthorityClientCompletion.Applied)
            Logger.Warning("Tournament host report ended without canonical application. Completion={Completion}, Reason={Reason}", outcome.Completion, outcome.ReasonCode);
    }

    private void Handle_HitProgressionApplied(Common.Messaging.MessagePayload<NetworkTournamentHitProgressionApplied> payload)
    {
        if (ModInformation.IsServer || !TournamentServerMessageGuard.IsTrusted(payload.Who) || payload.What.Status != AuthorityResultStatus.Accepted) return;
        acceptedHitProgression.Add($"{payload.What.TournamentSessionId}\n{payload.What.MatchId}\n{payload.What.DamageOriginControllerId}\n{payload.What.DamageSequence}");
    }
}
