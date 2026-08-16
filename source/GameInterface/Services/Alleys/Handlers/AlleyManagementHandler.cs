using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.GameDebug.Messages;
using GameInterface.Services.Alleys.Interfaces;
using GameInterface.Services.Alleys.Messages;
using GameInterface.Services.Heroes.Extensions;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.TroopRosters.Data;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.Alleys.Handlers;

/// <summary>
/// Routes the player's alley management actions (abandon, change overseer, manage garrison) as
/// client to server requests, performs them authoritatively on the server with patches live, and
/// replicates the resulting garrison/overseer state to the owning client so its in-game menus stay
/// correct. The authoritative garrison/overseer is held in the CoopSession via
/// <see cref="ISessionAlleyPlayerDataInterface"/>; the owning client mirror lives in the behavior
/// via <see cref="IAlleyCampaignBehaviorInterface"/>.
/// </summary>
internal class AlleyManagementHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<AlleyManagementHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly ISessionAlleyPlayerDataInterface sessionInterface;
    private readonly IAlleyCampaignBehaviorInterface behaviorInterface;
    private readonly IPlayerManager playerManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<AcquireIntent, NetworkAlleyManagementResult> acquireRoute;
    private readonly IAuthorityRouteHandle<ClearIntent, NetworkAlleyManagementResult> clearRoute;
    private readonly IAuthorityRouteHandle<AbandonIntent, NetworkAlleyManagementResult> abandonRoute;
    private readonly IAuthorityRouteHandle<OverseerIntent, NetworkAlleyManagementResult> overseerRoute;
    private readonly IAuthorityRouteHandle<RosterIntent, NetworkAlleyManagementResult> garrisonRoute;
    private readonly IAuthorityRouteHandle<RosterIntent, NetworkAlleyManagementResult> recruitRoute;
    private readonly HashSet<string> publishedManagementCommits = new HashSet<string>(StringComparer.Ordinal);

    public AlleyManagementHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network,
        ISessionAlleyPlayerDataInterface sessionInterface,
        IAlleyCampaignBehaviorInterface behaviorInterface,
        IPlayerManager playerManager,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.sessionInterface = sessionInterface;
        this.behaviorInterface = behaviorInterface;
        this.playerManager = playerManager;
        this.configAuthority = configAuthority;

        acquireRoute = authorityRequestRouter.Register(AuthorityRoute<AcquireIntent, RequestAcquireAlley, NetworkAlleyManagementResult>.Define(
            "alley.acquire", AuthorityRouteKind.Command, CreateHeader,
            (x, h) => new RequestAcquireAlley(x.AlleyId, x.OwnerId, x.OverseerId, x.Garrison, h), x => x.Header, x => x.Header,
            ValidateAcquire, AcquireKey, ValidateHeader, ExecuteAcquire, CreateTerminalResult, ProbeCommit, _ => { }, PresentTerminalOutcome,
            configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation, failClosedOnApplyFailure: true, isExpectedClientResult: ExpectedAcquire));
        clearRoute = authorityRequestRouter.Register(AuthorityRoute<ClearIntent, RequestClearAlley, NetworkAlleyManagementResult>.Define(
            "alley.clear", AuthorityRouteKind.Command, CreateHeader, (x, h) => new RequestClearAlley(x.AlleyId, h), x => x.Header, x => x.Header,
            ValidateClear, x => Key(AlleyManagementOperation.Clear, x.AlleyId, null, false, null), ValidateHeader, ExecuteClear, CreateTerminalResult, ProbeCommit,
            _ => { }, PresentTerminalOutcome, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation, failClosedOnApplyFailure: true, isExpectedClientResult: ExpectedClear));
        abandonRoute = authorityRequestRouter.Register(AuthorityRoute<AbandonIntent, RequestAbandonAlley, NetworkAlleyManagementResult>.Define(
            "alley.abandon", AuthorityRouteKind.Command, CreateHeader, (x, h) => new RequestAbandonAlley(x.AlleyId, x.FromClanScreen, h), x => x.Header, x => x.Header,
            ValidateAbandon, x => Key(AlleyManagementOperation.Abandon, x.AlleyId, null, x.FromClanScreen, null), ValidateHeader, ExecuteAbandon, CreateTerminalResult, ProbeCommit,
            _ => { }, PresentTerminalOutcome, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation, failClosedOnApplyFailure: true, isExpectedClientResult: ExpectedAbandon));
        overseerRoute = authorityRequestRouter.Register(AuthorityRoute<OverseerIntent, RequestChangeAlleyOverseer, NetworkAlleyManagementResult>.Define(
            "alley.overseer.change", AuthorityRouteKind.Command, CreateHeader, (x, h) => new RequestChangeAlleyOverseer(x.AlleyId, x.OverseerId, h), x => x.Header, x => x.Header,
            ValidateOverseer, x => Key(AlleyManagementOperation.ChangeOverseer, x.AlleyId, x.NewOverseerId, false, null), ValidateHeader, ExecuteOverseer, CreateTerminalResult, ProbeCommit,
            _ => { }, PresentTerminalOutcome, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation, failClosedOnApplyFailure: true, isExpectedClientResult: ExpectedOverseer));
        garrisonRoute = authorityRequestRouter.Register(AuthorityRoute<RosterIntent, RequestSetAlleyGarrison, NetworkAlleyManagementResult>.Define(
            "alley.garrison.transfer", AuthorityRouteKind.Command, CreateHeader, (x, h) => new RequestSetAlleyGarrison(x.AlleyId, x.Roster, h), x => x.Header, x => x.Header,
            ValidateGarrison, x => Key(AlleyManagementOperation.SetGarrison, x.AlleyId, null, false, RosterKey(x.Garrison)), ValidateHeader, ExecuteGarrison, CreateTerminalResult, ProbeCommit,
            _ => { }, PresentTerminalOutcome, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation, failClosedOnApplyFailure: true, isExpectedClientResult: ExpectedGarrison));
        recruitRoute = authorityRequestRouter.Register(AuthorityRoute<RosterIntent, RequestRecruitAlleyTroops, NetworkAlleyManagementResult>.Define(
            "alley.recruit", AuthorityRouteKind.Command, CreateHeader, (x, h) => new RequestRecruitAlleyTroops(x.AlleyId, x.Roster, h), x => x.Header, x => x.Header,
            ValidateGarrison, x => Key(AlleyManagementOperation.RecruitTroops, x.AlleyId, null, false, RosterKey(x.Troops)), ValidateHeader, ExecuteRecruit, CreateTerminalResult, ProbeCommit,
            _ => { }, PresentTerminalOutcome, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation, failClosedOnApplyFailure: true, isExpectedClientResult: ExpectedRecruit));

        messageBroker.Subscribe<AlleyAcquiredRequested>(Handle_AlleyAcquiredRequested);
        messageBroker.Subscribe<AlleyClearedRequested>(Handle_AlleyClearedRequested);
        messageBroker.Subscribe<AbandonAlleyRequested>(Handle_AbandonAlleyRequested);
        messageBroker.Subscribe<ChangeAlleyOverseerRequested>(Handle_ChangeAlleyOverseerRequested);
        messageBroker.Subscribe<SetAlleyGarrisonRequested>(Handle_SetAlleyGarrisonRequested);
        messageBroker.Subscribe<RecruitAlleyTroopsRequested>(Handle_RecruitAlleyTroopsRequested);

        messageBroker.Subscribe<NetworkAlleyManagementUpdated>(Handle_NetworkAlleyManagementUpdated);
        messageBroker.Subscribe<NetworkAlleyManagementRemoved>(Handle_NetworkAlleyManagementRemoved);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<AlleyAcquiredRequested>(Handle_AlleyAcquiredRequested);
        messageBroker.Unsubscribe<AlleyClearedRequested>(Handle_AlleyClearedRequested);
        messageBroker.Unsubscribe<AbandonAlleyRequested>(Handle_AbandonAlleyRequested);
        messageBroker.Unsubscribe<ChangeAlleyOverseerRequested>(Handle_ChangeAlleyOverseerRequested);
        messageBroker.Unsubscribe<SetAlleyGarrisonRequested>(Handle_SetAlleyGarrisonRequested);
        messageBroker.Unsubscribe<RecruitAlleyTroopsRequested>(Handle_RecruitAlleyTroopsRequested);

        messageBroker.Unsubscribe<NetworkAlleyManagementUpdated>(Handle_NetworkAlleyManagementUpdated);
        messageBroker.Unsubscribe<NetworkAlleyManagementRemoved>(Handle_NetworkAlleyManagementRemoved);
        acquireRoute.Dispose(); clearRoute.Dispose(); abandonRoute.Dispose(); overseerRoute.Dispose(); garrisonRoute.Dispose(); recruitRoute.Dispose();
    }

    // --- Local requests (requesting client) -> network request to the server ---

    private void Handle_AlleyAcquiredRequested(MessagePayload<AlleyAcquiredRequested> payload)
    {
        if (ModInformation.IsServer) return;
        if (!objectManager.TryGetIdWithLogging(payload.What.Alley, out var alleyId)) return;
        if (!objectManager.TryGetIdWithLogging(payload.What.Owner, out var ownerId)) return;
        if (!objectManager.TryGetIdWithLogging(payload.What.Overseer, out var overseerId)) return;

        acquireRoute.Submit(new AcquireIntent(alleyId, ownerId, overseerId, AlleyGarrisonData.ToData(payload.What.Garrison, objectManager)));
    }

    private void Handle_AlleyClearedRequested(MessagePayload<AlleyClearedRequested> payload)
    {
        if (ModInformation.IsServer) return;
        if (objectManager.TryGetIdWithLogging(payload.What.Alley, out var alleyId))
            clearRoute.Submit(new ClearIntent(alleyId));
    }

    private void Handle_AbandonAlleyRequested(MessagePayload<AbandonAlleyRequested> payload)
    {
        if (ModInformation.IsServer) return;
        if (!objectManager.TryGetIdWithLogging(payload.What.Alley, out var alleyId)) return;

        abandonRoute.Submit(new AbandonIntent(alleyId, payload.What.FromClanScreen));
    }

    private void Handle_ChangeAlleyOverseerRequested(MessagePayload<ChangeAlleyOverseerRequested> payload)
    {
        if (ModInformation.IsServer) return;
        if (!objectManager.TryGetIdWithLogging(payload.What.Alley, out var alleyId)) return;
        if (!objectManager.TryGetIdWithLogging(payload.What.NewOverseer, out var overseerId)) return;

        overseerRoute.Submit(new OverseerIntent(alleyId, overseerId));
    }

    private void Handle_SetAlleyGarrisonRequested(MessagePayload<SetAlleyGarrisonRequested> payload)
    {
        if (ModInformation.IsServer) return;
        if (!objectManager.TryGetIdWithLogging(payload.What.Alley, out var alleyId)) return;

        // A party-screen roster snapshot is client-computed. Keep the UI from silently mutating another
        // player's alley; this route intentionally returns a stable server-transfer-required result until
        // the canonical party transfer delta exists.
        garrisonRoute.Submit(new RosterIntent(alleyId, AlleyGarrisonData.ToData(payload.What.NewGarrison, objectManager)));
    }

    private void Handle_RecruitAlleyTroopsRequested(MessagePayload<RecruitAlleyTroopsRequested> payload)
    {
        if (ModInformation.IsServer) return;
        if (!objectManager.TryGetIdWithLogging(payload.What.Alley, out var alleyId)) return;

        recruitRoute.Submit(new RosterIntent(alleyId, AlleyGarrisonData.ToData(payload.What.Troops, objectManager)));
    }

    // --- Typed authority routes ---

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out var config)) return default;
        return new AuthorityRequestHeader(config.ProtocolVersion, config.SessionId, requestId, config.Revision);
    }

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out var current))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        if (header.ProtocolVersion != current.ProtocolVersion)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.InvalidRequest, "unsupported-protocol");
        if (!string.Equals(header.SessionId, current.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session");
        return header.ExpectedRevision == current.Revision ? AuthorityHeaderValidation.Valid :
            AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
    }

    private static string ValidateId(string id) => string.IsNullOrWhiteSpace(id) || id.Length > 256 ? "invalid-alley-id" : null;
    private static string ValidateAcquire(RequestAcquireAlley x) => ValidateId(x.AlleyId) ?? ValidateId(x.OwnerId) ?? ValidateId(x.OverseerId) ?? ValidateRoster(x.Garrison);
    private static string ValidateClear(RequestClearAlley x) => ValidateId(x.AlleyId);
    private static string ValidateAbandon(RequestAbandonAlley x) => ValidateId(x.AlleyId);
    private static string ValidateOverseer(RequestChangeAlleyOverseer x) => ValidateId(x.AlleyId) ?? ValidateId(x.NewOverseerId);
    private static string ValidateGarrison(RequestSetAlleyGarrison x) => ValidateId(x.AlleyId) ?? ValidateRoster(x.Garrison);
    private static string ValidateGarrison(RequestRecruitAlleyTroops x) => ValidateId(x.AlleyId) ?? ValidateRoster(x.Troops);
    private static string ValidateRoster(TroopRosterElementData[] roster)
    {
        if (roster == null || roster.Length > 64) return "invalid-roster";
        foreach (var x in roster)
            if (string.IsNullOrWhiteSpace(x.CharacterId) || x.CharacterId.Length > 256 || x.Number < 0 || x.WoundedNumber < 0 || x.WoundedNumber > x.Number || x.Xp < 0)
                return "invalid-roster";
        return null;
    }

    private static string AcquireKey(RequestAcquireAlley x) => Key(AlleyManagementOperation.Acquire, x.AlleyId, x.OverseerId, false, RosterKey(x.Garrison));
    private static string Key(AlleyManagementOperation operation, string alleyId, string overseerId, bool fromClanScreen, string rosterKey) =>
        string.Concat((int)operation, ":", alleyId?.Length ?? -1, ":", alleyId ?? string.Empty, ":", overseerId?.Length ?? -1, ":", overseerId ?? string.Empty,
            ":", fromClanScreen ? "1" : "0", ":", rosterKey ?? string.Empty);
    private static string RosterKey(TroopRosterElementData[] roster)
    {
        if (roster == null) return "null";
        var value = roster.Length.ToString();
        foreach (var x in roster) value = string.Concat(value, ":", x.CharacterId?.Length ?? -1, ":", x.CharacterId ?? string.Empty, ":", x.Number, ":", x.WoundedNumber, ":", x.Xp);
        return value;
    }

    private AuthorityServerReply<NetworkAlleyManagementResult> ExecuteAcquire(AuthorityServerContext context, RequestAcquireAlley request) =>
        Disabled(context.Header, AlleyManagementOperation.Acquire, request.AlleyId, request.OverseerId, false, RosterKey(request.Garrison), "takeover-session-required");
    private AuthorityServerReply<NetworkAlleyManagementResult> ExecuteClear(AuthorityServerContext context, RequestClearAlley request) =>
        Disabled(context.Header, AlleyManagementOperation.Clear, request.AlleyId, null, false, null, "clear-session-required");
    private AuthorityServerReply<NetworkAlleyManagementResult> ExecuteGarrison(AuthorityServerContext context, RequestSetAlleyGarrison request) =>
        Disabled(context.Header, AlleyManagementOperation.SetGarrison, request.AlleyId, null, false, RosterKey(request.Garrison), "server-transfer-required");
    private AuthorityServerReply<NetworkAlleyManagementResult> ExecuteRecruit(AuthorityServerContext context, RequestRecruitAlleyTroops request) =>
        Disabled(context.Header, AlleyManagementOperation.RecruitTroops, request.AlleyId, null, false, RosterKey(request.Troops), "server-derived-recruitment-required");

    private AuthorityServerReply<NetworkAlleyManagementResult> ExecuteAbandon(AuthorityServerContext context, RequestAbandonAlley request)
    {
        if (!TryGetOwnedAlley(context, request.AlleyId, out var alley, out var owner, out var data, out var reason))
            return Rejected(context.Header, AlleyManagementOperation.Abandon, request.AlleyId, null, request.FromClanScreen, null, reason);
        bool mutated = false;
        try
        {
            if (!request.FromClanScreen) { mutated = true; ReturnGarrisonToOwner(owner, data.Garrison); }
            mutated = true;
            alley.SetOwner(null);
            sessionInterface.RemoveManagementData(request.AlleyId);
            network.SendAll(new NetworkAlleyManagementRemoved(request.AlleyId, context.Header));
            if (alley.Owner != null || sessionInterface.TryGetManagementData(request.AlleyId, out _))
                return Isolate(context, AlleyManagementOperation.Abandon, request.AlleyId, null, request.FromClanScreen, null, "abandon-postcondition");
            return Accepted(context.Header, AlleyManagementOperation.Abandon, request.AlleyId, null, request.FromClanScreen, null);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Alley abandon failed. Alley={Alley} Mutated={Mutated}", request.AlleyId, mutated);
            return mutated ? Isolate(context, AlleyManagementOperation.Abandon, request.AlleyId, null, request.FromClanScreen, null, "abandon-isolated") :
                Failed(context.Header, AlleyManagementOperation.Abandon, request.AlleyId, null, request.FromClanScreen, null, "abandon-failed");
        }
    }

    private AuthorityServerReply<NetworkAlleyManagementResult> ExecuteOverseer(AuthorityServerContext context, RequestChangeAlleyOverseer request)
    {
        if (!TryGetOwnedAlley(context, request.AlleyId, out var alley, out var owner, out var stored, out var reason))
            return Rejected(context.Header, AlleyManagementOperation.ChangeOverseer, request.AlleyId, request.NewOverseerId, false, null, reason);
        if (!objectManager.TryGetObject<Hero>(request.NewOverseerId, out var newOverseer) || newOverseer == owner || newOverseer.Clan != owner.Clan || !newOverseer.IsAlive || newOverseer.IsPrisoner)
            return Rejected(context.Header, AlleyManagementOperation.ChangeOverseer, request.AlleyId, request.NewOverseerId, false, null, "ineligible-overseer");
        bool mutated = false;
        try
        {
            var garrison = SwapOverseerInGarrison(stored.Garrison, stored.OverseerId, request.NewOverseerId);
            mutated = true;
            sessionInterface.SetManagementData(request.AlleyId, request.NewOverseerId, garrison);
            TeleportOverseerToAlley(newOverseer, alley);
            if (!sessionInterface.TryGetManagementData(request.AlleyId, out var updated) || updated.OverseerId != request.NewOverseerId)
                return Isolate(context, AlleyManagementOperation.ChangeOverseer, request.AlleyId, request.NewOverseerId, false, RosterKey(garrison), "overseer-postcondition");
            network.SendAll(new NetworkAlleyManagementUpdated(request.AlleyId, updated.OverseerId, updated.Garrison, updated.LastRecruitTimeTicks, context.Header));
            return Accepted(context.Header, AlleyManagementOperation.ChangeOverseer, request.AlleyId, request.NewOverseerId, false, RosterKey(updated.Garrison));
        }
        catch (Exception e)
        {
            Logger.Error(e, "Alley overseer mutation failed. Alley={Alley} Mutated={Mutated}", request.AlleyId, mutated);
            return mutated ? Isolate(context, AlleyManagementOperation.ChangeOverseer, request.AlleyId, request.NewOverseerId, false, null, "overseer-isolated") :
                Failed(context.Header, AlleyManagementOperation.ChangeOverseer, request.AlleyId, request.NewOverseerId, false, null, "overseer-failed");
        }
    }

    private bool TryGetOwnedAlley(AuthorityServerContext context, string alleyId, out Alley alley, out Hero owner, out AlleyManagementData data, out string reason)
    {
        alley = null; owner = null; data = null; reason = "invalid-owner";
        if (!objectManager.TryGetObject<Hero>(context.Player.HeroId, out owner) || !objectManager.TryGetObject<Alley>(alleyId, out alley)) return false;
        if (alley.Owner != owner || !sessionInterface.TryGetManagementData(alleyId, out data)) { reason = "not-alley-owner"; return false; }
        return true;
    }

    private AuthorityServerReply<NetworkAlleyManagementResult> Isolate(AuthorityServerContext context, AlleyManagementOperation operation, string alleyId, string overseerId, bool fromClanScreen, string rosterKey, string reason)
    {
        foreach (var player in playerManager.Players)
            if (playerManager.TryGetPeer(player.ControllerId, out var peer)) peer.Disconnect();
        return new AuthorityServerReply<NetworkAlleyManagementResult>(CreateResult(context.Header, AuthorityResultStatus.ExecutionFailed, reason, operation, alleyId, overseerId, fromClanScreen, rosterKey), false, true);
    }
    private static AuthorityServerReply<NetworkAlleyManagementResult> Disabled(AuthorityRequestHeader h, AlleyManagementOperation op, string alley, string overseer, bool fromClan, string roster, string reason) =>
        new(CreateResult(h, AuthorityResultStatus.Unavailable, reason, op, alley, overseer, fromClan, roster), false);
    private static AuthorityServerReply<NetworkAlleyManagementResult> Rejected(AuthorityRequestHeader h, AlleyManagementOperation op, string alley, string overseer, bool fromClan, string roster, string reason) =>
        new(CreateResult(h, AuthorityResultStatus.Rejected, reason, op, alley, overseer, fromClan, roster), false);
    private static AuthorityServerReply<NetworkAlleyManagementResult> Failed(AuthorityRequestHeader h, AlleyManagementOperation op, string alley, string overseer, bool fromClan, string roster, string reason) =>
        new(CreateResult(h, AuthorityResultStatus.ExecutionFailed, reason, op, alley, overseer, fromClan, roster), false);
    private static AuthorityServerReply<NetworkAlleyManagementResult> Accepted(AuthorityRequestHeader h, AlleyManagementOperation op, string alley, string overseer, bool fromClan, string roster) =>
        new(CreateResult(h, AuthorityResultStatus.Accepted, null, op, alley, overseer, fromClan, roster), true);
    private static NetworkAlleyManagementResult CreateResult(AuthorityRequestHeader h, AuthorityResultStatus status, string reason, AlleyManagementOperation op, string alley, string overseer, bool fromClan, string roster) =>
        new(new AuthorityResultHeader(h.SessionId, h.RequestId, status, h.ExpectedRevision, reason), op, alley, overseer, fromClan, roster);
    private static NetworkAlleyManagementResult CreateTerminalResult(AuthorityRequestHeader h, AuthorityResultStatus status, string reason) =>
        CreateResult(h, status, reason, AlleyManagementOperation.Acquire, string.Empty, null, false, null);

    private static bool ExpectedAcquire(RequestAcquireAlley x, NetworkAlleyManagementResult r) => Expected(r, x.Header, AlleyManagementOperation.Acquire, x.AlleyId, x.OverseerId, false, RosterKey(x.Garrison));
    private static bool ExpectedClear(RequestClearAlley x, NetworkAlleyManagementResult r) => Expected(r, x.Header, AlleyManagementOperation.Clear, x.AlleyId, null, false, null);
    private static bool ExpectedAbandon(RequestAbandonAlley x, NetworkAlleyManagementResult r) => Expected(r, x.Header, AlleyManagementOperation.Abandon, x.AlleyId, null, x.FromClanScreen, null);
    private static bool ExpectedOverseer(RequestChangeAlleyOverseer x, NetworkAlleyManagementResult r) => Expected(r, x.Header, AlleyManagementOperation.ChangeOverseer, x.AlleyId, x.NewOverseerId, false, r.RosterKey);
    private static bool ExpectedGarrison(RequestSetAlleyGarrison x, NetworkAlleyManagementResult r) => Expected(r, x.Header, AlleyManagementOperation.SetGarrison, x.AlleyId, null, false, RosterKey(x.Garrison));
    private static bool ExpectedRecruit(RequestRecruitAlleyTroops x, NetworkAlleyManagementResult r) => Expected(r, x.Header, AlleyManagementOperation.RecruitTroops, x.AlleyId, null, false, RosterKey(x.Troops));
    private static bool Expected(NetworkAlleyManagementResult r, AuthorityRequestHeader h, AlleyManagementOperation op, string alley, string overseer, bool fromClan, string roster) =>
        r.Header.RequestId == h.RequestId && r.Header.CommittedRevision == h.ExpectedRevision && r.Header.SessionId == h.SessionId && r.Operation == op && r.AlleyId == alley && r.OverseerId == overseer && r.FromClanScreen == fromClan && (roster == null || r.RosterKey == roster);

    private AuthorityCommitProbeResult ProbeCommit(NetworkAlleyManagementResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted || !configAuthority.TryGetCurrent(out var current) || current.SessionId != result.Header.SessionId || current.Revision != result.Header.CommittedRevision)
            return AuthorityCommitProbeResult.Invalid;
        var correlation = CommitKey(result.Header.SessionId, result.Header.RequestId, result.Header.CommittedRevision);
        if (!publishedManagementCommits.Contains(correlation) || !objectManager.TryGetObject<Alley>(result.AlleyId, out var alley)) return AuthorityCommitProbeResult.Pending;
        if (result.Operation == AlleyManagementOperation.Abandon) return alley.Owner != Hero.MainHero && !behaviorInterface.TryGetPlayerAlleyManagementData(alley, out _, out _, out _) ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
        if (result.Operation != AlleyManagementOperation.ChangeOverseer || alley.Owner != Hero.MainHero || !behaviorInterface.TryGetPlayerAlleyManagementData(alley, out var overseer, out var roster, out _)) return AuthorityCommitProbeResult.Pending;
        return objectManager.TryGetId(overseer, out var overseerId) && overseerId == result.OverseerId && RosterKey(AlleyGarrisonData.ToData(roster, objectManager)) == result.RosterKey ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private void PresentTerminalOutcome(AuthorityClientOutcome<NetworkAlleyManagementResult> outcome)
    {
        if (outcome.Applied) return;
        GameThread.RunSafe(() => Common.Messaging.MessageBroker.Instance.Publish(this, new SendInformationMessage(AlleyFailureText(outcome.ReasonCode))), context: nameof(PresentTerminalOutcome));
    }
    private static string AlleyFailureText(string reason) => reason switch
    {
        "takeover-session-required" => "Alley takeovers require a server-issued encounter session.",
        "clear-session-required" => "Clearing an alley requires a server-issued encounter session.",
        "server-transfer-required" => "Alley garrison changes require a server-authoritative troop transfer.",
        "server-derived-recruitment-required" => "Alley recruitment must be calculated by the server.",
        "not-alley-owner" => "You no longer own that alley.",
        "ineligible-overseer" => "That clan member cannot oversee this alley.",
        _ => "The server could not complete the alley action."
    };
    private static string CommitKey(string session, long request, long revision) => string.Concat(session, ":", request, ":", revision);

    private readonly struct AcquireIntent { public AcquireIntent(string a, string o, string v, TroopRosterElementData[] g) { AlleyId=a; OwnerId=o; OverseerId=v; Garrison=g; } public string AlleyId {get;} public string OwnerId {get;} public string OverseerId {get;} public TroopRosterElementData[] Garrison {get;} }
    private readonly struct ClearIntent { public ClearIntent(string a) { AlleyId=a; } public string AlleyId {get;} }
    private readonly struct AbandonIntent { public AbandonIntent(string a, bool f) { AlleyId=a; FromClanScreen=f; } public string AlleyId {get;} public bool FromClanScreen {get;} }
    private readonly struct OverseerIntent { public OverseerIntent(string a, string o) { AlleyId=a; OverseerId=o; } public string AlleyId {get;} public string OverseerId {get;} }
    private readonly struct RosterIntent { public RosterIntent(string a, TroopRosterElementData[] r) { AlleyId=a; Roster=r; } public string AlleyId {get;} public TroopRosterElementData[] Roster {get;} }

    /// <summary>
    /// Sends the overseer to the alley's settlement to run it, the way vanilla does - but never a
    /// player-controlled hero: a player must stay free to move on the map and is never pinned to a
    /// settlement just for being assigned to an alley.
    /// </summary>
    private static void TeleportOverseerToAlley(Hero overseer, Alley alley)
    {
        if (overseer == null || overseer.IsPlayerHero()) return;
        TeleportHeroAction.ApplyDelayedTeleportToSettlement(overseer, alley.Settlement);
    }

    // --- Network broadcasts (client apply) ---

    private void Handle_NetworkAlleyManagementUpdated(MessagePayload<NetworkAlleyManagementUpdated> payload)
    {
        if (ModInformation.IsServer || !configAuthority.IsTrustedServer(payload.Who)) return;

        var data = payload.What;
        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Alley>(data.AlleyId, out var alley)) return;

            // Only the owning client keeps the behavior-side management data. A client that no longer
            // owns this alley (ownership transferred away without an abandon) drops its stale copy so
            // its manage-alley menus don't linger.
            if (alley.Owner != Hero.MainHero)
            {
                behaviorInterface.RemovePlayerAlleyData(alley);
                return;
            }

            Hero overseer = null;
            if (data.OverseerId != null) objectManager.TryGetObjectWithLogging(data.OverseerId, out overseer);

            behaviorInterface.AddOrUpdatePlayerAlleyData(
                alley,
                overseer,
                AlleyGarrisonData.FromData(data.Garrison, objectManager),
                new CampaignTime(data.LastRecruitTimeTicks));
            if (!string.IsNullOrWhiteSpace(data.SessionId) && data.AuthorityRequestId > 0)
                publishedManagementCommits.Add(CommitKey(data.SessionId, data.AuthorityRequestId, data.CommittedRevision));
        });
    }

    private void BroadcastManagementUpdate(string alleyId, AlleyManagementData data)
    {
        network.SendAll(new NetworkAlleyManagementUpdated(
            alleyId,
            data.OverseerId,
            data.Garrison,
            data.LastRecruitTimeTicks));
    }

    private void Handle_NetworkAlleyManagementRemoved(MessagePayload<NetworkAlleyManagementRemoved> payload)
    {
        if (ModInformation.IsServer || !configAuthority.IsTrustedServer(payload.Who)) return;

        var data = payload.What;
        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Alley>(data.AlleyId, out var alley)) return;
            behaviorInterface.RemovePlayerAlleyData(alley);
            if (!string.IsNullOrWhiteSpace(data.SessionId) && data.AuthorityRequestId > 0)
                publishedManagementCommits.Add(CommitKey(data.SessionId, data.AuthorityRequestId, data.CommittedRevision));
        });
    }

    /// <summary>
    /// Authoritative abandon: return the stored garrison troops to the owner's party, clear the
    /// owner, drop the stored management data and tell clients to remove their mirror.
    /// </summary>
    internal void AbandonAlley(string alleyId, bool fromClanScreen)
    {
        if (!objectManager.TryGetObjectWithLogging<Alley>(alleyId, out var alley)) return;

        // A menu/dialog abandon returns the garrison troops to the owner's party; a clan-screen
        // abandon forfeits them, matching vanilla AbandonTheAlley(fromClanScreen).
        if (!fromClanScreen && sessionInterface.TryGetManagementData(alleyId, out var data))
        {
            ReturnGarrisonToOwner(alley.Owner, data.Garrison);
        }

        alley.SetOwner(null);
        sessionInterface.RemoveManagementData(alleyId);
        network.SendAll(new NetworkAlleyManagementRemoved(alleyId));
    }

    private void ReturnGarrisonToOwner(Hero owner, TroopRosterElementData[] garrison)
    {
        if (garrison == null) return;

        var party = owner?.PartyBelongedTo;
        if (party == null)
        {
            // No party to return the garrison to (the owner isn't leading one); surface it rather than
            // silently dropping the troops.
            Logger.Error("Could not return alley garrison: owner {owner} has no party", owner?.StringId);
            return;
        }

        foreach (var element in garrison)
        {
            if (!objectManager.TryGetObject<CharacterObject>(element.CharacterId, out var character)) continue;
            if (character.IsHero) continue;
            party.MemberRoster.AddToCounts(character, element.Number, false, element.WoundedNumber, element.Xp, true, -1);
        }
    }

    /// <summary>
    /// Returns the stored garrison with the overseer hero swapped (old removed, new added), mirroring
    /// vanilla ChangeTheLeaderOfAlleyInternal so the stored roster reflects the current overseer.
    /// </summary>
    private TroopRosterElementData[] SwapOverseerInGarrison(TroopRosterElementData[] garrison, string oldOverseerId, string newOverseerId)
    {
        var list = new List<TroopRosterElementData>(garrison ?? Array.Empty<TroopRosterElementData>());

        if (TryGetHeroCharacterId(oldOverseerId, out var oldCharId))
            list.RemoveAll(e => e.CharacterId == oldCharId);

        if (TryGetHeroCharacterId(newOverseerId, out var newCharId) && !list.Exists(e => e.CharacterId == newCharId))
            list.Add(new TroopRosterElementData(newCharId, 1, 0, 0));

        return list.ToArray();
    }

    private bool TryGetHeroCharacterId(string heroId, out string characterId)
    {
        characterId = null;
        if (heroId == null) return false;
        if (!objectManager.TryGetObject<Hero>(heroId, out var hero) || hero.CharacterObject == null) return false;
        return objectManager.TryGetId(hero.CharacterObject, out characterId);
    }
}
