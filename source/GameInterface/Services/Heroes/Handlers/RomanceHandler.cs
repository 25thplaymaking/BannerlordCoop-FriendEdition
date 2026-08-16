using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Configuration;
using GameInterface.Registry.Messages;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Services.Heroes.Extensions;
using GameInterface.Services.Heroes.Messages.RomanceFlow;
using GameInterface.Services.Heroes.RomanceFlow;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.Core;
using TaleWorlds.Library;
using Romance = TaleWorlds.CampaignSystem.Romance;

namespace GameInterface.Services.Heroes.Handlers;

internal class RomanceHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<RomanceHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IRomanceAuthority romanceAuthority;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<NetworkRequestRomanceStateChange, NetworkRomanceStateChangeResult> transitionRoute;
    private readonly IAuthorityRouteHandle<object, NetworkRomanceStateSyncResult> snapshotRoute;

    public RomanceHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IRomanceAuthority romanceAuthority,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.romanceAuthority = romanceAuthority;
        this.configAuthority = configAuthority;

        transitionRoute = authorityRequestRouter.Register(
            AuthorityRoute<NetworkRequestRomanceStateChange, NetworkRequestRomanceStateChange,
                NetworkRomanceStateChangeResult>.Define(
                "romance.transition", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestRomanceStateChange(
                    intent.TargetHeroId,
                    (Romance.RomanceLevelEnum)intent.RequestedLevel,
                    progressToNextLevel: 0,
                    lastVisit: 0f,
                    scoreFromPersuasion: 0f,
                    intent.ClanMemberHeroId,
                    header),
                request => request.Header,
                result => result.Header,
                ValidateTransitionWire,
                BuildTransitionKey,
                ValidateHeader,
                ExecuteTransition,
                CreateTransitionTerminal,
                ProbeTransition,
                _ => StartSnapshotBootstrap(),
                PresentTransitionTerminal,
                configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true,
                isExpectedClientResult: (request, result) =>
                    result.Person2Id == request.TargetHeroId &&
                    result.Level == request.RequestedLevel &&
                    result.Person1Id == (request.ClanMemberHeroId ?? Hero.MainHero?.StringId)));
        snapshotRoute = authorityRequestRouter.Register(
            AuthorityRoute<object, NetworkRequestRomanceStateSync, NetworkRomanceStateSyncResult>.Define(
                "romance.snapshot", AuthorityRouteKind.BootstrapQuery, CreateHeader,
                (_, header) => new NetworkRequestRomanceStateSync(header),
                request => request.Header,
                result => result.Header,
                request => request.Header.TryValidate(out _) ? null : "invalid-romance-snapshot-query",
                request => "snapshot:" + request.Header.SessionId + ":" + request.Header.ExpectedRevision,
                ValidateHeader,
                ExecuteSnapshotQuery,
                CreateSnapshotTerminal,
                ProbeSnapshot,
                _ => { },
                PresentSnapshotTerminal,
                configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.BootstrapQuery,
                requireAuthenticatedPlayer: true,
                failClosedOnApplyFailure: true));

        messageBroker.Subscribe<AllGameObjectsRegistered>(Handle_AllGameObjectsRegistered);
        messageBroker.Subscribe<RomanticStateChangeRequested>(Handle_RomanticStateChangeRequested);
        messageBroker.Subscribe<RomanceStatesChanged>(Handle_RomanceStatesChanged);
        messageBroker.Subscribe<HostModConfigAccepted>(Handle_HostModConfigAccepted);
        messageBroker.Subscribe<NetworkRequestRomanceStateSync>(Handle_NetworkRequestRomanceStateSync);
        messageBroker.Subscribe<NetworkSyncRomanceStates>(Handle_NetworkSyncRomanceStates);
        messageBroker.Subscribe<NetworkRomanceStateChangeResult>(Handle_NetworkRomanceStateChangeResult);
        messageBroker.Subscribe<NetworkRomanceStateSyncResult>(Handle_NetworkRomanceStateSyncResult);
        messageBroker.Subscribe<NetworkRomanceRequestRejected>(Handle_NetworkRomanceRequestRejected);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<AllGameObjectsRegistered>(Handle_AllGameObjectsRegistered);
        messageBroker.Unsubscribe<RomanticStateChangeRequested>(Handle_RomanticStateChangeRequested);
        messageBroker.Unsubscribe<RomanceStatesChanged>(Handle_RomanceStatesChanged);
        messageBroker.Unsubscribe<HostModConfigAccepted>(Handle_HostModConfigAccepted);
        messageBroker.Unsubscribe<NetworkRequestRomanceStateSync>(Handle_NetworkRequestRomanceStateSync);
        messageBroker.Unsubscribe<NetworkSyncRomanceStates>(Handle_NetworkSyncRomanceStates);
        messageBroker.Unsubscribe<NetworkRomanceStateChangeResult>(Handle_NetworkRomanceStateChangeResult);
        messageBroker.Unsubscribe<NetworkRomanceStateSyncResult>(Handle_NetworkRomanceStateSyncResult);
        messageBroker.Unsubscribe<NetworkRomanceRequestRejected>(Handle_NetworkRomanceRequestRejected);
        transitionRoute.Dispose();
        snapshotRoute.Dispose();
    }

    private void Handle_AllGameObjectsRegistered(MessagePayload<AllGameObjectsRegistered> payload)
    {
        if (ModInformation.IsServer) return;

        StartSnapshotBootstrap();
    }

    private void Handle_RomanticStateChangeRequested(MessagePayload<RomanticStateChangeRequested> payload)
    {
        if (ModInformation.IsServer) return;

        var request = payload.What;
        if (TryGetControlledPair(request.Person1, request.Person2, out _, out var targetHero))
        {
            if (!objectManager.TryGetId(targetHero, out var targetHeroId)) return;

            transitionRoute.Submit(new NetworkRequestRomanceStateChange(
                targetHeroId,
                request.RequestedLevel,
                request.ProgressToNextLevel,
                request.LastVisit,
                request.ScoreFromPersuasion));
            return;
        }

        // Arranged match: (own clan member, outside hero). Send both ids so the server can
        // validate the promise between the two non-player heroes.
        if (!Patches.RomanceActionPatches.IsLocalArrangedPair(request.Person1, request.Person2)) return;

        var localClan = Hero.MainHero?.Clan;
        var clanMember = request.Person1?.Clan == localClan ? request.Person1 : request.Person2;
        var outsider = clanMember == request.Person1 ? request.Person2 : request.Person1;
        if (!objectManager.TryGetId(outsider, out var outsiderId)) return;
        if (!objectManager.TryGetId(clanMember, out var clanMemberId)) return;

        transitionRoute.Submit(new NetworkRequestRomanceStateChange(
            outsiderId,
            request.RequestedLevel,
            request.ProgressToNextLevel,
            request.LastVisit,
            request.ScoreFromPersuasion,
            clanMemberId));
    }

    private void Handle_RomanceStatesChanged(MessagePayload<RomanceStatesChanged> payload)
    {
        if (ModInformation.IsClient) return;

        network.SendAll(new NetworkSyncRomanceStates(BuildSnapshot()));
    }

    private void Handle_NetworkRequestRomanceStateSync(MessagePayload<NetworkRequestRomanceStateSync> payload)
    {
        if (ModInformation.IsClient) return;
        if (payload.Who is not NetPeer peer) return;

        // Marriage barter still has a legacy rejection recovery path while that command family is
        // being migrated. Only headerless requests use this compatibility branch; authenticated
        // snapshot requests are owned exclusively by the BootstrapQuery route above.
        if (payload.What.Header.RequestId != 0) return;
        GameThread.RunSafe(() => SendSnapshot(peer), context: nameof(Handle_NetworkRequestRomanceStateSync));
    }

    private void Handle_NetworkSyncRomanceStates(MessagePayload<NetworkSyncRomanceStates> payload)
    {
        if (ModInformation.IsServer || !configAuthority.IsTrustedServer(payload.Who)) return;

        var snapshot = payload.What;
        GameThread.RunSafe(
            () => ApplySnapshot(snapshot.States ?? Array.Empty<RomanceStateData>()),
            context: nameof(Handle_NetworkSyncRomanceStates));
    }

    private void Handle_HostModConfigAccepted(MessagePayload<HostModConfigAccepted> payload)
    {
        if (ModInformation.IsClient && payload.What.Snapshot != null && configAuthority.IsCurrent(payload.What.Snapshot))
            StartSnapshotBootstrap();
    }

    private void Handle_NetworkRomanceStateChangeResult(MessagePayload<NetworkRomanceStateChangeResult> payload)
    {
        // The router owns correlation/replay. The canonical state is broadcast by the server
        // before this terminal result, so there is intentionally no client-side mutation here.
    }

    private void Handle_NetworkRomanceStateSyncResult(MessagePayload<NetworkRomanceStateSyncResult> payload)
    {
        if (ModInformation.IsServer || !configAuthority.IsTrustedServer(payload.Who) ||
            payload.What.Status != AuthorityResultStatus.Accepted)
            return;

        GameThread.RunSafe(
            () => ApplySnapshot(payload.What.States ?? Array.Empty<RomanceStateData>()),
            context: nameof(NetworkRomanceStateSyncResult));
    }

    private void Handle_NetworkRomanceRequestRejected(MessagePayload<NetworkRomanceRequestRejected> payload)
    {
        if (ModInformation.IsServer) return;

        var reason = string.IsNullOrWhiteSpace(payload.What.Reason)
            ? "The server rejected the romance request."
            : payload.What.Reason;

        GameThread.RunSafe(
            () => InformationManager.DisplayMessage(new InformationMessage(reason)),
            context: nameof(Handle_NetworkRomanceRequestRejected));
    }

    private void StartSnapshotBootstrap()
    {
        if (!ModInformation.IsClient || !configAuthority.TryGetCurrent(out _)) return;
        snapshotRoute.Submit(default);
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out var config)) return default;
        return new AuthorityRequestHeader(config.ProtocolVersion, config.SessionId, requestId, config.Revision);
    }

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out var config))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "romance-config-unavailable");
        if (header.ProtocolVersion != config.ProtocolVersion ||
            !string.Equals(header.SessionId, config.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-config-session");
        return header.ExpectedRevision == config.Revision
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-config-revision");
    }

    private static string ValidateTransitionWire(NetworkRequestRomanceStateChange request)
    {
        if (string.IsNullOrWhiteSpace(request.TargetHeroId) ||
            !Enum.IsDefined(typeof(Romance.RomanceLevelEnum), request.RequestedLevel))
            return "invalid-romance-transition";

        // The UI formerly copied client-local progress fields into the authoritative campaign
        // state. The route carries only the target and requested transition; all progression is
        // derived/preserved on the server.
        return request.ProgressToNextLevel == 0 && request.LastVisit == 0f && request.ScoreFromPersuasion == 0f
            ? null
            : "client-romance-state-fields-not-allowed";
    }

    private static string BuildTransitionKey(NetworkRequestRomanceStateChange request) => string.Concat(
        request.TargetHeroId, ":", request.ClanMemberHeroId ?? string.Empty, ":", request.RequestedLevel);

    private AuthorityServerReply<NetworkRomanceStateChangeResult> ExecuteTransition(
        AuthorityServerContext context, NetworkRequestRomanceStateChange request)
    {
        if (!TryResolveHero(context.Player.HeroId, out var playerHero) || playerHero == null)
            return TransitionReply(context.Header, null, null, default, AuthorityResultStatus.Unauthorized,
                "requester-hero-missing");
        if (!TryResolveHero(request.TargetHeroId, out var targetHero) || targetHero == null)
            return TransitionReply(context.Header, null, request.TargetHeroId, default, AuthorityResultStatus.Rejected,
                "target-hero-missing");

        Romance.RomanceLevelEnum level = (Romance.RomanceLevelEnum)request.RequestedLevel;
        Hero subject = playerHero;
        if (!string.IsNullOrEmpty(request.ClanMemberHeroId))
        {
            if (!TryResolveHero(request.ClanMemberHeroId, out subject) || subject == null)
                return TransitionReply(context.Header, null, request.TargetHeroId, level, AuthorityResultStatus.Rejected,
                    "arranged-clan-member-missing");
            if (!romanceAuthority.TryValidateArrangedStateChange(playerHero, subject, targetHero, level, out var reason))
                return TransitionReply(context.Header, subject, targetHero, level, AuthorityResultStatus.Rejected, reason);
        }
        else if (!romanceAuthority.TryValidateStateChange(playerHero, targetHero, level, out var reason))
        {
            return TransitionReply(context.Header, playerHero, targetHero, level, AuthorityResultStatus.Rejected, reason);
        }

        try
        {
            // No client progress, visit time, or persuasion score crosses this boundary. The
            // native action owns the canonical transition and preserves its server state.
            ChangeRomanticStateAction.Apply(subject, targetHero, level);
            if (Romance.GetRomanticLevel(subject, targetHero) != level)
                return TransitionReply(context.Header, subject, targetHero, level, AuthorityResultStatus.ExecutionFailed,
                    "romance-postcondition-missing");
            return TransitionReply(context.Header, subject, targetHero, level, AuthorityResultStatus.Accepted, null,
                statePublished: true);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed authoritative romance transition {RequestId}", context.Header.RequestId);
            return TransitionReply(context.Header, subject, targetHero, level, AuthorityResultStatus.ExecutionFailed,
                "romance-transition-failed");
        }
    }

    private AuthorityServerReply<NetworkRomanceStateSyncResult> ExecuteSnapshotQuery(
        AuthorityServerContext context, NetworkRequestRomanceStateSync _)
    {
        try
        {
            return new AuthorityServerReply<NetworkRomanceStateSyncResult>(
                new NetworkRomanceStateSyncResult(context.Header, AuthorityResultStatus.Accepted, BuildSnapshot()), true);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Failed to capture romance snapshot");
            return new AuthorityServerReply<NetworkRomanceStateSyncResult>(
                CreateSnapshotTerminal(context.Header, AuthorityResultStatus.ExecutionFailed, "romance-snapshot-failed"), false);
        }
    }

    private AuthorityServerReply<NetworkRomanceStateChangeResult> TransitionReply(
        AuthorityRequestHeader header,
        Hero subject,
        Hero target,
        Romance.RomanceLevelEnum level,
        AuthorityResultStatus status,
        string reason,
        bool statePublished = false)
    {
        objectManager.TryGetId(subject, out var subjectId);
        objectManager.TryGetId(target, out var targetId);
        return new AuthorityServerReply<NetworkRomanceStateChangeResult>(
            new NetworkRomanceStateChangeResult(header, subjectId, targetId, level, status, reason), statePublished);
    }

    private static NetworkRomanceStateChangeResult CreateTransitionTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new NetworkRomanceStateChangeResult(header, null, null, default, status, reason);

    private static NetworkRomanceStateSyncResult CreateSnapshotTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new NetworkRomanceStateSyncResult(header, status, Array.Empty<RomanceStateData>(), reason);

    private AuthorityCommitProbeResult ProbeTransition(NetworkRomanceStateChangeResult result)
    {
        if (result.Status != AuthorityResultStatus.Accepted ||
            !configAuthority.TryGetCurrent(out var config) ||
            !string.Equals(config.SessionId, result.SessionId, StringComparison.Ordinal) ||
            config.Revision != result.CommittedRevision ||
            !TryResolveHero(result.Person1Id, out var subject) ||
            !TryResolveHero(result.Person2Id, out var target) ||
            !Enum.IsDefined(typeof(Romance.RomanceLevelEnum), result.Level))
            return AuthorityCommitProbeResult.Invalid;

        return Romance.GetRomanticLevel(subject, target) == (Romance.RomanceLevelEnum)result.Level
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;
    }

    private AuthorityCommitProbeResult ProbeSnapshot(NetworkRomanceStateSyncResult result)
    {
        if (result.Status != AuthorityResultStatus.Accepted ||
            !configAuthority.TryGetCurrent(out var config) ||
            !string.Equals(config.SessionId, result.SessionId, StringComparison.Ordinal) ||
            config.Revision != result.CommittedRevision)
            return AuthorityCommitProbeResult.Invalid;

        RomanceStateData[] expected = result.States ?? Array.Empty<RomanceStateData>();
        foreach (var state in expected)
        {
            if (!TryResolveHero(state.Person1Id, out var first) || !TryResolveHero(state.Person2Id, out var second) ||
                !Enum.IsDefined(typeof(Romance.RomanceLevelEnum), state.Level))
                return AuthorityCommitProbeResult.Invalid;
            var local = Romance.GetRomanticState(first, second);
            if (local == null || local.Level != (Romance.RomanceLevelEnum)state.Level ||
                local.ProgressToNextLevel != state.ProgressToNextLevel || local.LastVisit != state.LastVisit ||
                local.ScoreFromPersuasion != state.ScoreFromPersuasion)
                return AuthorityCommitProbeResult.Pending;
        }
        return AuthorityCommitProbeResult.Applied;
    }

    private static void PresentTransitionTerminal(AuthorityClientOutcome<NetworkRomanceStateChangeResult> outcome)
    {
        if (outcome.Completion == AuthorityClientCompletion.Applied) return;
        string reason = string.IsNullOrWhiteSpace(outcome.ReasonCode)
            ? "The server could not apply the romance transition."
            : outcome.ReasonCode;
        InformationManager.DisplayMessage(new InformationMessage(reason));
    }

    private static void PresentSnapshotTerminal(AuthorityClientOutcome<NetworkRomanceStateSyncResult> outcome)
    {
        if (outcome.Completion != AuthorityClientCompletion.Applied)
            Logger.Warning("Romance snapshot did not reach a canonical state. Completion={Completion} Reason={Reason}",
                outcome.Completion, outcome.ReasonCode);
    }

    private bool TryResolveRequester(object sender, out NetPeer peer, out Player player, out Hero playerHero)
    {
        peer = sender as NetPeer;
        player = null;
        playerHero = null;

        if (peer == null)
        {
            Logger.Error("Received romance request without an originating peer");
            return false;
        }

        if (!playerManager.TryGetPlayer(peer, out player))
        {
            Logger.Warning("Received romance request from unregistered peer {Peer}", peer.Id);
            return false;
        }

        if (!TryResolveHero(player.HeroId, out playerHero))
        {
            Logger.Warning("Unable to resolve player hero {HeroId} for peer {Peer}", player.HeroId, peer.Id);
            return false;
        }

        return true;
    }

    private bool TryResolveHero(string heroId, out Hero hero)
    {
        hero = null;
        return !string.IsNullOrEmpty(heroId) && objectManager.TryGetObject(heroId, out hero);
    }

    private bool TryGetControlledPair(Hero firstHero, Hero secondHero, out Hero playerHero, out Hero targetHero)
    {
        playerHero = null;
        targetHero = null;

        if (firstHero.IsControlledByThisInstance())
        {
            playerHero = firstHero;
            targetHero = secondHero;
        }
        else if (secondHero.IsControlledByThisInstance())
        {
            playerHero = secondHero;
            targetHero = firstHero;
        }

        return playerHero != null && targetHero != null && !targetHero.IsPlayerHero();
    }

    private void Reject(NetPeer peer, string reason)
    {
        network.Send(peer, new NetworkRomanceRequestRejected(reason));
        SendSnapshot(peer);
    }

    private void SendSnapshot(NetPeer peer)
        => network.Send(peer, new NetworkSyncRomanceStates(BuildSnapshot()));

    private RomanceStateData[] BuildSnapshot()
    {
        if (Romance.RomanticStateList == null) return Array.Empty<RomanceStateData>();

        var result = new List<RomanceStateData>(Romance.RomanticStateList.Count);
        foreach (var state in Romance.RomanticStateList)
        {
            if (state?.Person1 == null || state.Person2 == null) continue;
            if (!objectManager.TryGetId(state.Person1, out var person1Id) ||
                !objectManager.TryGetId(state.Person2, out var person2Id))
            {
                Logger.Warning("Could not snapshot romance state for {Person1} and {Person2}", state.Person1, state.Person2);
                continue;
            }

            result.Add(new RomanceStateData(
                person1Id,
                person2Id,
                state.Level,
                state.ProgressToNextLevel,
                state.LastVisit,
                state.ScoreFromPersuasion));
        }

        return result.ToArray();
    }

    private void ApplySnapshot(RomanceStateData[] snapshot)
    {
        if (Campaign.Current == null || Romance.RomanticStateList == null) return;

        var resolvedStates = new List<(Hero Person1, Hero Person2, RomanceStateData Data)>(snapshot.Length);
        foreach (var state in snapshot)
        {
            if (!global::System.Enum.IsDefined(typeof(Romance.RomanceLevelEnum), state.Level))
            {
                Logger.Warning("Ignoring romance snapshot with invalid level {Level}", state.Level);
                return;
            }

            if (!TryResolveHero(state.Person1Id, out var person1) || !TryResolveHero(state.Person2Id, out var person2))
            {
                Logger.Warning(
                    "Waiting to apply romance snapshot until heroes {Person1Id} and {Person2Id} exist",
                    state.Person1Id,
                    state.Person2Id);
                return;
            }

            resolvedStates.Add((person1, person2, state));
        }

        using (new AllowedThread())
        {
            Romance.RomanticStateList.Clear();

            foreach (var state in resolvedStates)
            {
                ChangeRomanticStateAction.Apply(
                    state.Person1,
                    state.Person2,
                    (Romance.RomanceLevelEnum)state.Data.Level);

                var romanticState = Romance.GetRomanticState(state.Person1, state.Person2);
                if (romanticState == null) continue;

                romanticState.ProgressToNextLevel = state.Data.ProgressToNextLevel;
                romanticState.LastVisit = state.Data.LastVisit;
                romanticState.ScoreFromPersuasion = state.Data.ScoreFromPersuasion;
            }
        }
    }

    private static bool TryApplyClientStateFields(
        Hero playerHero,
        Hero targetHero,
        NetworkRequestRomanceStateChange request,
        out string reason)
    {
        if (float.IsNaN(request.LastVisit) || float.IsInfinity(request.LastVisit) ||
            float.IsNaN(request.ScoreFromPersuasion) || float.IsInfinity(request.ScoreFromPersuasion))
        {
            reason = "The romance progress data is invalid.";
            return false;
        }

        var state = Romance.GetRomanticState(playerHero, targetHero);
        if (state == null)
        {
            if (request.ProgressToNextLevel != 0 || request.LastVisit != 0f || request.ScoreFromPersuasion != 0f)
            {
                reason = "The romance progress does not match the server state.";
                return false;
            }

            reason = null;
            return true;
        }

        state.ProgressToNextLevel = request.ProgressToNextLevel;
        state.LastVisit = request.LastVisit;
        state.ScoreFromPersuasion = request.ScoreFromPersuasion;
        reason = null;
        return true;
    }
}
