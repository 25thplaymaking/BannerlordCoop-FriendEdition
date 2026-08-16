using Common;
using Common.Messaging;
using Common.Network.Messages;
using Common.Util;
using Coop.Core.Client.Services.MobileParties.Messages;
using Coop.Core.Server.Services.MobileParties.Messages;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.GameDebug.Messages;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Settlements.Interfaces;
using System;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace Coop.Core.Client.Services.MobileParties.Handlers;

/// <summary>Routes local settlement encounter intent through the authenticated authority handshake.</summary>
public class ClientSettlementExitEnterHandler : IHandler
{
    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly ISettlementInterface settlementInterface;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<StartIntent, NetworkStartSettlementEncounter> startRoute;
    private readonly IAuthorityRouteHandle<EndIntent, NetworkSettlementEncounterLeaveResult> endRoute;
    private PendingStart pendingStart;
    private PendingLeave pendingLeave;
    private bool suppressNextStartFailure;

    public ClientSettlementExitEnterHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        ISettlementInterface settlementInterface,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.settlementInterface = settlementInterface;
        this.configAuthority = configAuthority;

        startRoute = authorityRequestRouter.Register(
            AuthorityRoute<StartIntent, NetworkRequestStartSettlementEncounter, NetworkStartSettlementEncounter>.Define(
                "settlement.encounter.start", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestStartSettlementEncounter(intent.SettlementId, header),
                request => request.Header, result => result.Header, ValidateStartWireShape,
                request => Key(request.SettlementId), ValidateHeader,
                (_, __) => throw new InvalidOperationException("Settlement encounter start executes only on the server."),
                CreateStartTerminalResult, ProbeStartCommit, ResyncStart, PresentStartOutcome,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: false, isExpectedClientResult: IsExpectedStartResult));
        endRoute = authorityRequestRouter.Register(
            AuthorityRoute<EndIntent, NetworkRequestEndSettlementEncounter, NetworkSettlementEncounterLeaveResult>.Define(
                "settlement.encounter.end", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkRequestEndSettlementEncounter(intent.SettlementId, header),
                request => request.Header, result => result.Header, ValidateEndWireShape,
                request => Key(request.SettlementId),
                ValidateHeader,
                (_, __) => throw new InvalidOperationException("Settlement encounter end executes only on the server."),
                CreateEndTerminalResult, ProbeEndCommit, ResyncEnd, PresentEndOutcome,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: false, isExpectedClientResult: IsExpectedEndResult));

        messageBroker.Subscribe<StartSettlementEncounterAttempted>(Handle);
        messageBroker.Subscribe<EndSettlementEncounterAttempted>(Handle);
        // The same correlated terminal messages are also the requester's canonical apply proof.
        messageBroker.Subscribe<NetworkStartSettlementEncounter>(Handle);
        messageBroker.Subscribe<NetworkSettlementEncounterLeaveResult>(Handle);
        messageBroker.Subscribe<NetworkPartyEnterSettlement>(Handle);
        messageBroker.Subscribe<NetworkPartyLeaveSettlement>(Handle);
    }

    private void Handle(MessagePayload<StartSettlementEncounterAttempted> payload)
    {
        if (!objectManager.TryGetIdWithLogging(payload.What.Party, out var partyId) ||
            !objectManager.TryGetIdWithLogging(payload.What.Settlement, out var settlementId) ||
            pendingStart != null)
            return;

        pendingStart = new PendingStart(partyId, settlementId);
        if (pendingLeave == null) SubmitPendingStart();
    }

    private void SubmitPendingStart()
    {
        if (pendingStart == null || pendingStart.RequestId > 0) return;
        // Local header rejection completes synchronously and terminal recovery may clear the
        // pending object from inside Submit. Never dereference state that callback already unwound.
        PendingStart submitting = pendingStart;
        var ticket = startRoute.Submit(new StartIntent(submitting.SettlementId));
        if (ReferenceEquals(pendingStart, submitting))
            submitting.RequestId = ticket.RequestId;
    }

    private void Handle(MessagePayload<EndSettlementEncounterAttempted> payload)
    {
        if (!objectManager.TryGetIdWithLogging(payload.What.Party, out var partyId) || pendingLeave != null)
            return;

        if (!TryGetEncounterSettlementId(payload.What.Party, out var settlementId))
        {
            messageBroker.Publish(this, new SendInformationMessage(
                "Unable to leave the settlement: the active settlement encounter is unavailable."));
            return;
        }

        pendingLeave = new PendingLeave(partyId, settlementId);
        PendingLeave submitting = pendingLeave;
        var ticket = endRoute.Submit(new EndIntent(settlementId));
        if (ReferenceEquals(pendingLeave, submitting))
            submitting.RequestId = ticket.RequestId;
    }

    private void Handle(MessagePayload<NetworkStartSettlementEncounter> payload)
    {
        var result = payload.What;
        if (!IsTrustedCurrent(payload.Who, result.Header) ||
            result.Header.Status != AuthorityResultStatus.Accepted ||
            !MatchesPendingStart(result))
            return;

        GameThread.RunSafe(() =>
        {
            if (!MatchesPendingStart(result)) return;
            pendingStart.ProofObserved = true;
            if (pendingLeave == null) ApplyPendingStart(result.Mode);
        }, context: nameof(NetworkStartSettlementEncounter));
    }

    private void ApplyPendingStart(SettlementEncounterStartMode mode)
    {
        if (pendingStart == null || pendingStart.AppliedLocally || !pendingStart.ProofObserved) return;
        if (!objectManager.TryGetObjectWithLogging(pendingStart.PartyId, out MobileParty party) ||
            !objectManager.TryGetObjectWithLogging(pendingStart.SettlementId, out Settlement settlement))
            return;

        using (new AllowedThread())
        {
            if (mode == SettlementEncounterStartMode.EnteredSettlement &&
                !ReferenceEquals(party.CurrentSettlement, settlement))
                settlementInterface.PartyEnterSettlement(party, settlement);
            settlementInterface.StartSettlementEncounter(party, settlement);
            if (ShouldShowRaidOccupiedMenu(party, settlement)) GameMenu.SwitchToMenu("raid_occupied");
        }
        pendingStart.AppliedLocally = true;
    }

    private void Handle(MessagePayload<NetworkSettlementEncounterLeaveResult> payload)
    {
        var result = payload.What;
        if (!IsTrustedCurrent(payload.Who, result.Header) ||
            result.Header.Status != AuthorityResultStatus.Accepted ||
            !MatchesPendingLeave(result))
            return;

        GameThread.RunSafe(() =>
        {
            if (!MatchesPendingLeave(result)) return;
            pendingLeave.ProofObserved = true;
            if (result.Outcome == SettlementEncounterLeaveOutcome.Applied)
            {
                using (new AllowedThread())
                {
                    if (objectManager.TryGetObject(result.PartyId, out MobileParty party) &&
                        party.CurrentSettlement != null)
                        settlementInterface.PartyLeaveSettlement(party);
                    settlementInterface.EndSettlementEncounter();
                }
                pendingLeave.AppliedLocally = true;
            }
            else if (result.Outcome == SettlementEncounterLeaveOutcome.AlreadyOutside)
            {
                using (new AllowedThread()) settlementInterface.EndSettlementEncounter();
                pendingLeave.AppliedLocally = true;
            }
        }, context: nameof(NetworkSettlementEncounterLeaveResult));
    }

    private AuthorityCommitProbeResult ProbeStartCommit(NetworkStartSettlementEncounter result)
    {
        if (!MatchesPendingStart(result)) return AuthorityCommitProbeResult.Invalid;
        if (!pendingStart.ProofObserved || pendingLeave != null) return AuthorityCommitProbeResult.Pending;
        ApplyPendingStart(result.Mode);
        if (!pendingStart.AppliedLocally ||
            !objectManager.TryGetObject(pendingStart.PartyId, out MobileParty party) ||
            !objectManager.TryGetObject(pendingStart.SettlementId, out Settlement settlement))
            return AuthorityCommitProbeResult.Pending;

        bool membershipMatches = result.Mode == SettlementEncounterStartMode.EnteredSettlement
            ? ReferenceEquals(party.CurrentSettlement, settlement)
            : party.CurrentSettlement == null;
        bool encounterMatches = ReferenceEquals(PlayerEncounter.Current?.EncounterSettlementAux, settlement);
        return membershipMatches && encounterMatches
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;
    }

    private AuthorityCommitProbeResult ProbeEndCommit(NetworkSettlementEncounterLeaveResult result)
    {
        if (!MatchesPendingLeave(result)) return AuthorityCommitProbeResult.Invalid;
        if (!pendingLeave.ProofObserved) return AuthorityCommitProbeResult.Pending;
        if (result.Outcome == SettlementEncounterLeaveOutcome.Suppressed)
            return AuthorityCommitProbeResult.Invalid;
        if (!pendingLeave.AppliedLocally ||
            !objectManager.TryGetObject(pendingLeave.PartyId, out MobileParty party))
            return AuthorityCommitProbeResult.Pending;
        return party.CurrentSettlement == null && PlayerEncounter.Current == null
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;
    }

    private void ResyncStart(NetworkStartSettlementEncounter result)
    {
        if (MatchesPendingStart(result)) ApplyPendingStart(result.Mode);
    }

    private void ResyncEnd(NetworkSettlementEncounterLeaveResult result)
    {
        if (!MatchesPendingLeave(result) || result.Outcome != SettlementEncounterLeaveOutcome.Applied) return;
        using (new AllowedThread())
        {
            if (objectManager.TryGetObject(result.PartyId, out MobileParty party) && party.CurrentSettlement != null)
                settlementInterface.PartyLeaveSettlement(party);
            settlementInterface.EndSettlementEncounter();
        }
        pendingLeave.AppliedLocally = true;
    }

    private void PresentStartOutcome(AuthorityClientOutcome<NetworkStartSettlementEncounter> outcome)
    {
        if (outcome.Applied)
        {
            pendingStart = null;
            return;
        }

        pendingStart = null;
        if (suppressNextStartFailure)
        {
            suppressNextStartFailure = false;
            return;
        }
        messageBroker.Publish(this, new SendInformationMessage(StartFailureMessage(outcome.ReasonCode)));
    }

    private void PresentEndOutcome(AuthorityClientOutcome<NetworkSettlementEncounterLeaveResult> outcome)
    {
        if (!outcome.Applied)
        {
            pendingLeave = null;
            if (outcome.Result != null &&
                outcome.Result.Outcome == SettlementEncounterLeaveOutcome.Suppressed &&
                string.Equals(outcome.ReasonCode, "leave-suppressed", StringComparison.Ordinal))
            {
                SubmitPendingStart();
                return;
            }
            pendingStart = null;
            messageBroker.Publish(this, new SendInformationMessage(EndFailureMessage(outcome.ReasonCode)));
            return;
        }

        var result = outcome.Result;
        pendingLeave = null;
        if (result.Outcome == SettlementEncounterLeaveOutcome.Suppressed)
        {
            SubmitPendingStart();
            return;
        }

        if (pendingStart != null)
        {
            if (pendingStart.RequestId > 0)
            {
                suppressNextStartFailure = true;
                startRoute.CancelAll("superseded-by-settlement-leave");
                suppressNextStartFailure = false;
            }
            pendingStart = null;
        }
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

    private bool IsTrustedCurrent(object source, AuthorityResultHeader header) =>
        configAuthority.IsTrustedServer(source) && configAuthority.TryGetCurrent(out ModConfigSnapshot current) &&
        string.Equals(header.SessionId, current.SessionId, StringComparison.Ordinal) &&
        header.CommittedRevision == current.Revision && header.RequestId > 0;

    private bool MatchesPendingStart(NetworkStartSettlementEncounter result) => pendingStart != null &&
        pendingStart.RequestId == result.Header.RequestId &&
        string.Equals(pendingStart.PartyId, result.PartyId, StringComparison.Ordinal) &&
        string.Equals(pendingStart.SettlementId, result.SettlementId, StringComparison.Ordinal);

    private bool MatchesPendingLeave(NetworkSettlementEncounterLeaveResult result) => pendingLeave != null &&
        pendingLeave.RequestId == result.Header.RequestId &&
        string.Equals(pendingLeave.PartyId, result.PartyId, StringComparison.Ordinal);

    private static string ValidateStartWireShape(NetworkRequestStartSettlementEncounter request) =>
        string.IsNullOrWhiteSpace(request.SettlementId) || request.SettlementId.Length > 256
            ? "invalid-settlement-id"
            : null;

    private static string Key(string value) => string.Concat(value?.Length ?? -1, ":", value ?? string.Empty);

    private static NetworkStartSettlementEncounter CreateStartTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, null, SettlementEncounterStartMode.EnteredSettlement, new AuthorityResultHeader(
            header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private static NetworkSettlementEncounterLeaveResult CreateEndTerminalResult(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, null, SettlementEncounterLeaveOutcome.Suppressed, new AuthorityResultHeader(
            header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private static bool IsExpectedStartResult(
        NetworkRequestStartSettlementEncounter request, NetworkStartSettlementEncounter result) =>
        request.Header.RequestId == result.Header.RequestId &&
        request.Header.ExpectedRevision == result.Header.CommittedRevision &&
        string.Equals(request.Header.SessionId, result.Header.SessionId, StringComparison.Ordinal) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(result.PartyId) &&
        (result.Mode == SettlementEncounterStartMode.EnteredSettlement ||
         result.Mode == SettlementEncounterStartMode.EncounterOnly);

    private static bool IsExpectedEndResult(
        NetworkRequestEndSettlementEncounter request, NetworkSettlementEncounterLeaveResult result) =>
        request.Header.RequestId == result.Header.RequestId &&
        request.Header.ExpectedRevision == result.Header.CommittedRevision &&
        string.Equals(request.Header.SessionId, result.Header.SessionId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(result.PartyId) &&
        string.Equals(request.SettlementId, result.SettlementId, StringComparison.Ordinal) &&
        (result.Outcome == SettlementEncounterLeaveOutcome.Applied ||
         result.Outcome == SettlementEncounterLeaveOutcome.Suppressed ||
         result.Outcome == SettlementEncounterLeaveOutcome.AlreadyOutside);

    private static string ValidateEndWireShape(NetworkRequestEndSettlementEncounter request) =>
        string.IsNullOrWhiteSpace(request.SettlementId) || request.SettlementId.Length > 256
            ? "invalid-settlement-id"
            : null;

    private bool TryGetEncounterSettlementId(MobileParty party, out string settlementId)
    {
        settlementId = null;
        var settlement = party?.CurrentSettlement ?? PlayerEncounter.Current?.EncounterSettlementAux;
        return settlement != null && objectManager.TryGetIdWithLogging(settlement, out settlementId);
    }

    private static bool ShouldShowRaidOccupiedMenu(MobileParty party, Settlement settlement)
    {
        if (party?.Party?.MapEvent != null) return false;
        return settlement?.Party?.MapEvent?.IsActiveSlowVillageRaid() == true;
    }

    private static string StartFailureMessage(string reason) => reason switch
    {
        "already-in-map-event" => "Unable to enter the settlement: your party is already in a map event.",
        "already-in-another-settlement" => "Unable to enter the settlement: your party is already inside another settlement.",
        "settlement-not-found" => "Unable to enter the settlement: the settlement is no longer available.",
        "party-not-found" => "Unable to enter the settlement: your party is no longer available.",
        "too-far" => "Unable to enter the settlement: your party is too far from the settlement.",
        "distance-validation-unavailable" => "Unable to enter the settlement: the server could not validate your distance to the settlement.",
        "hideout-occupied" => "Unable to enter the settlement: another player is already inside this hideout.",
        _ => "Unable to enter the settlement: the server did not confirm it.",
    };

    private static string EndFailureMessage(string reason) => reason switch
    {
        "party-not-found" => "Unable to leave the settlement: your party is no longer available.",
        _ => "Unable to leave the settlement: the server did not confirm it.",
    };

    private void Handle(MessagePayload<NetworkPartyEnterSettlement> payload)
    {
        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging(payload.What.PartyId, out MobileParty party) ||
                !objectManager.TryGetObjectWithLogging(payload.What.SettlementId, out Settlement settlement)) return;
            using (new AllowedThread()) settlementInterface.PartyEnterSettlement(party, settlement);
        });
    }

    private void Handle(MessagePayload<NetworkPartyLeaveSettlement> payload)
    {
        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging(payload.What.PartyId, out MobileParty party)) return;
            bool isMainParty = ReferenceEquals(party, MobileParty.MainParty);
            using (new AllowedThread())
            {
                settlementInterface.PartyLeaveSettlement(party);
                if (isMainParty) settlementInterface.EndSettlementEncounter();
            }
        });
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<StartSettlementEncounterAttempted>(Handle);
        messageBroker.Unsubscribe<EndSettlementEncounterAttempted>(Handle);
        messageBroker.Unsubscribe<NetworkStartSettlementEncounter>(Handle);
        messageBroker.Unsubscribe<NetworkSettlementEncounterLeaveResult>(Handle);
        messageBroker.Unsubscribe<NetworkPartyEnterSettlement>(Handle);
        messageBroker.Unsubscribe<NetworkPartyLeaveSettlement>(Handle);
        startRoute.Dispose();
        endRoute.Dispose();
    }

    private sealed class StartIntent
    {
        public StartIntent(string settlementId) => SettlementId = settlementId;
        public string SettlementId { get; }
    }

    private sealed class EndIntent
    {
        public EndIntent(string settlementId) => SettlementId = settlementId;
        public string SettlementId { get; }
    }

    private sealed class PendingStart
    {
        public PendingStart(string partyId, string settlementId)
        {
            PartyId = partyId;
            SettlementId = settlementId;
        }
        public string PartyId { get; }
        public string SettlementId { get; }
        public long RequestId { get; set; }
        public bool ProofObserved { get; set; }
        public bool AppliedLocally { get; set; }
    }

    private sealed class PendingLeave
    {
        public PendingLeave(string partyId, string settlementId)
        {
            PartyId = partyId;
            SettlementId = settlementId;
        }
        public string PartyId { get; }
        public string SettlementId { get; }
        public long RequestId { get; set; }
        public bool ProofObserved { get; set; }
        public bool AppliedLocally { get; set; }
    }
}
