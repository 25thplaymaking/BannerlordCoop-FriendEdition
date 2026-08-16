using Common;
using Common.Messaging;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.GameDebug.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Villages.Data;
using GameInterface.Services.Villages.Interfaces;
using GameInterface.Services.Villages.Messages;
using LiteNetLib;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;

namespace Coop.Core.Client.Services.Villages.Handlers;

internal class ClientVillageHostileActionHandler : IHandler
{
    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly IVillageHostileActionInterface villageHostileActionInterface;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<VillageHostileActionIntent, NetworkVillageHostileActionResult> hostileActionRoute;
    private readonly HashSet<string> publishedApprovals = new HashSet<string>(StringComparer.Ordinal);

    public ClientVillageHostileActionHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        IVillageHostileActionInterface villageHostileActionInterface,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.villageHostileActionInterface = villageHostileActionInterface;
        this.configAuthority = configAuthority;
        hostileActionRoute = authorityRequestRouter.Register(
            AuthorityRoute<VillageHostileActionIntent, NetworkRequestVillageHostileAction,
                NetworkVillageHostileActionResult>.Define(
                "village.hostile-action", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestVillageHostileAction(
                    header, intent.Action, intent.MobilePartyId, intent.SettlementId),
                request => request.Header, result => result.Header, ValidateWireShape, BuildCommandKey,
                ValidateHeader, (_, __) => throw new InvalidOperationException("Village hostile-action route executes only on the server."),
                CreateTerminalResult, ProbeClientCommit, _ => { }, PresentTerminalOutcome,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true, isExpectedClientResult: IsExpectedResult));

        messageBroker.Subscribe<VillageHostileActionAttempted>(Handle_VillageHostileActionAttempted);
        messageBroker.Subscribe<NetworkVillageHostileActionStarted>(Handle_NetworkVillageHostileActionStarted);
        messageBroker.Subscribe<NetworkVillageHostileActionCooldowns>(Handle_NetworkVillageHostileActionCooldowns);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<VillageHostileActionAttempted>(Handle_VillageHostileActionAttempted);
        messageBroker.Unsubscribe<NetworkVillageHostileActionStarted>(Handle_NetworkVillageHostileActionStarted);
        messageBroker.Unsubscribe<NetworkVillageHostileActionCooldowns>(Handle_NetworkVillageHostileActionCooldowns);
        hostileActionRoute.Dispose();
    }

    private void Handle_VillageHostileActionAttempted(MessagePayload<VillageHostileActionAttempted> payload)
    {
        var message = payload.What;

        if (!objectManager.TryGetIdWithLogging(message.MobileParty, out var mobilePartyId)) return;
        if (!objectManager.TryGetIdWithLogging(message.Settlement, out var settlementId)) return;

        hostileActionRoute.Submit(new VillageHostileActionIntent(message.Action, mobilePartyId, settlementId));
    }

    private void Handle_NetworkVillageHostileActionStarted(MessagePayload<NetworkVillageHostileActionStarted> payload)
    {
        if (!(payload.Who is NetPeer) || !configAuthority.IsTrustedServer(payload.Who))
            return;

        lock (publishedApprovals)
            publishedApprovals.Add(ApprovalKey(payload.What.Action, payload.What.MobilePartyId, payload.What.SettlementId));
    }

    private void Handle_NetworkVillageHostileActionCooldowns(MessagePayload<NetworkVillageHostileActionCooldowns> payload)
    {
        GameThread.RunSafe(
            () => villageHostileActionInterface.ApplyCooldowns(payload.What.Cooldowns),
            context: nameof(Handle_NetworkVillageHostileActionCooldowns));
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private static string ValidateWireShape(NetworkRequestVillageHostileAction request)
    {
        if (request.Action != VillageHostileAction.Raid &&
            request.Action != VillageHostileAction.ForceVolunteers &&
            request.Action != VillageHostileAction.ForceSupplies)
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

    private static NetworkVillageHostileActionResult CreateTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reasonCode) =>
        new(new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reasonCode),
            VillageHostileAction.Raid, null, null);

    private static bool IsExpectedResult(NetworkRequestVillageHostileAction request,
        NetworkVillageHostileActionResult result) => request.Action == result.Action &&
        string.Equals(request.MobilePartyId, result.MobilePartyId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal);

    private AuthorityCommitProbeResult ProbeClientCommit(NetworkVillageHostileActionResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted ||
            !IsKnownAction(result.Action) || string.IsNullOrWhiteSpace(result.MobilePartyId) ||
            string.IsNullOrWhiteSpace(result.SettlementId))
            return AuthorityCommitProbeResult.Invalid;

        lock (publishedApprovals)
            return publishedApprovals.Contains(ApprovalKey(result.Action, result.MobilePartyId, result.SettlementId))
                ? AuthorityCommitProbeResult.Applied
                : AuthorityCommitProbeResult.Pending;
    }

    private void PresentTerminalOutcome(AuthorityClientOutcome<NetworkVillageHostileActionResult> outcome)
    {
        if (outcome.Applied)
        {
            lock (publishedApprovals)
                publishedApprovals.Remove(ApprovalKey(outcome.Result.Action, outcome.Result.MobilePartyId, outcome.Result.SettlementId));
            villageHostileActionInterface.BeginHostileActionPresentation(outcome.Result.Action);
            return;
        }

        // No local hostile-action presentation is allowed to remain armed after any terminal failure.
        lock (publishedApprovals)
            publishedApprovals.Clear();
        PlayerEncounter.LeaveEncounter = true;
        GameMenu.ExitToLast();
        messageBroker.Publish(this, new SendInformationMessage(GetFailureMessage(outcome)));
    }

    private static bool IsKnownAction(VillageHostileAction action) => action == VillageHostileAction.Raid ||
        action == VillageHostileAction.ForceVolunteers || action == VillageHostileAction.ForceSupplies;

    private static string ApprovalKey(VillageHostileAction action, string mobilePartyId, string settlementId) =>
        string.Concat((int)action, ":", mobilePartyId?.Length ?? -1, ":", mobilePartyId ?? string.Empty,
            ":", settlementId?.Length ?? -1, ":", settlementId ?? string.Empty);

    private static string GetFailureMessage(AuthorityClientOutcome<NetworkVillageHostileActionResult> outcome)
    {
        return outcome.ReasonCode switch
        {
            "invalid-requester" => "Unable to start hostile action: the selected party is not controlled by you.",
            "non-village-settlement" => "Unable to start hostile action: the target is not a village.",
            "own-faction" => "Unable to start hostile action against your own faction.",
            "already-in-map-event" => "Unable to start hostile action: the party or village is already in an encounter.",
            "invalid-village-state" => "Unable to start hostile action: the village is not in a valid state.",
            "hearth-too-low" => "Unable to force volunteers: the village hearth is too low.",
            "cooldown" => "Unable to start hostile action: the village is recovering from a recent hostile action.",
            "not-approved" => "Unable to start hostile action: the server has not approved it.",
            "response-timeout" or "apply-timeout" or "network-disconnected" or "client-session-ended" =>
                "Unable to start hostile action: the server did not confirm it.",
            _ => "Unable to start hostile action."
        };
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
