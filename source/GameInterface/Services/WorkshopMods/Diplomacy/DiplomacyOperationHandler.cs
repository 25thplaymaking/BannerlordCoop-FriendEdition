using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Configuration;
using GameInterface.Registry.Messages;
using GameInterface.Services.Barters;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using LiteNetLib;
using Serilog;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

internal interface IDiplomacyPatchRuntime
{
    bool TrySubmit(DiplomacyLocalOperation operation);
    bool TryPromptKeepFief(Settlement settlement);
}

internal static class DiplomacyPatchRuntime
{
    public static IDiplomacyPatchRuntime Current { get; set; }
}

internal sealed class DiplomacyLocalOperation
{
    public DiplomacyLocalOperation(
        DiplomacyOperation operation,
        object target,
        object secondaryTarget = null,
        int intValue = 0)
    {
        Operation = operation;
        Target = target;
        SecondaryTarget = secondaryTarget;
        IntValue = intValue;
    }

    public DiplomacyOperation Operation { get; }
    public object Target { get; }
    public object SecondaryTarget { get; }
    public int IntValue { get; }
}

/// <summary>
/// Routes every Diplomacy player consequence through one authenticated, revisioned command path.
/// Native/mod actions execute only on the campaign server; messenger travel remains local
/// presentation after the server has validated and charged the request.
/// </summary>
internal sealed class DiplomacyOperationHandler : IHandler, IDiplomacyPatchRuntime
{
    private static readonly ILogger Logger = LogManager.GetLogger<DiplomacyOperationHandler>();
    private static readonly string[] MessengerAccidentTexts =
    {
        "{=A5lug0JY}Your messenger was ambushed and killed by highwaymen while trying to reach {HERO_NAME}!",
        "{=1rYSIMfX}Your messenger was ambushed and eaten by a Grue while trying to reach {HERO_NAME}!",
        "{=J2T97gNx}Your messenger lost his way and is now wandering the world aimlessly.",
        "{=hOOW2DMD}Your messenger found a treasure map and is now looking for the treasure, instead of delivering the message.",
        "{=hc52yW8O}Your messenger forgot the message, you will have to send him again.",
        "{=2rJDoi7N}Your messenger drank too much and is now sleeping it off.",
        "{=VKqC9dl0}Your messenger took the money and ran away.",
    };

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IWorkshopCapabilityRegistry capabilityRegistry;
    private readonly IDiplomacyRuntime runtime;
    private readonly IDiplomacySnapshotPublisher snapshotPublisher;
    private readonly DiplomacyOperationExecutor executor;
    private readonly DiplomacyRequestLedger<NetPeer> requestLedger = new(256);
    private readonly Dictionary<long, NetworkRequestDiplomacyOperation> pending = new();
    private readonly HashSet<int> visibleMessengerPrompts = new();

    private long nextRequestId;
    private long acceptedRevision = -1;
    private bool stateReady;

    public DiplomacyOperationHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IModConfigAuthority configAuthority,
        IWorkshopCapabilityRegistry capabilityRegistry,
        IDiplomacyRuntime runtime,
        IDiplomacySnapshotPublisher snapshotPublisher,
        IDiplomacyDonateGoldInterface donateGoldInterface)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.configAuthority = configAuthority;
        this.capabilityRegistry = capabilityRegistry;
        this.runtime = runtime;
        this.snapshotPublisher = snapshotPublisher;
        executor = new DiplomacyOperationExecutor(objectManager, donateGoldInterface);

        DiplomacyPatchRuntime.Current = this;
        messageBroker.Subscribe<AllGameObjectsRegistered>(HandleAllGameObjectsRegistered);
        messageBroker.Subscribe<NetworkDiplomacySnapshot>(HandleSnapshotObserved);
        messageBroker.Subscribe<NetworkRequestDiplomacySnapshot>(HandleSnapshotRequestForPendingPrompt);
        messageBroker.Subscribe<NetworkRequestDiplomacyOperation>(HandleOperationRequest);
        messageBroker.Subscribe<NetworkDiplomacyOperationResult>(HandleOperationResult);
        messageBroker.Subscribe<NetworkDiplomacyKeepFiefPrompt>(HandleKeepFiefPrompt);
        messageBroker.Subscribe<NetworkDiplomacyMessengerArrivalPrompt>(HandleMessengerArrivalPrompt);
        messageBroker.Subscribe<NetworkDiplomacyMessengerAccident>(HandleMessengerAccident);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<AllGameObjectsRegistered>(HandleAllGameObjectsRegistered);
        messageBroker.Unsubscribe<NetworkDiplomacySnapshot>(HandleSnapshotObserved);
        messageBroker.Unsubscribe<NetworkRequestDiplomacySnapshot>(HandleSnapshotRequestForPendingPrompt);
        messageBroker.Unsubscribe<NetworkRequestDiplomacyOperation>(HandleOperationRequest);
        messageBroker.Unsubscribe<NetworkDiplomacyOperationResult>(HandleOperationResult);
        messageBroker.Unsubscribe<NetworkDiplomacyKeepFiefPrompt>(HandleKeepFiefPrompt);
        messageBroker.Unsubscribe<NetworkDiplomacyMessengerArrivalPrompt>(HandleMessengerArrivalPrompt);
        messageBroker.Unsubscribe<NetworkDiplomacyMessengerAccident>(HandleMessengerAccident);
        if (ReferenceEquals(DiplomacyPatchRuntime.Current, this)) DiplomacyPatchRuntime.Current = null;
    }

    public bool TrySubmit(DiplomacyLocalOperation operation)
    {
        if (!ModInformation.IsClient || operation == null || !CanUseGameplayRoute(out var config) ||
            acceptedRevision < 0 || !TryGetId(operation.Target, out string targetId) ||
            !TryGetId(operation.SecondaryTarget, out string secondaryTargetId, allowNull: true))
            return false;

        long requestId = Interlocked.Increment(ref nextRequestId);
        var request = new NetworkRequestDiplomacyOperation(
            config.SessionId,
            requestId,
            acceptedRevision,
            operation.Operation,
            targetId,
            secondaryTargetId,
            operation.IntValue);
        if (!DiplomacyOperationProtocol.IsRequestShapeValid(request)) return false;

        pending[requestId] = request;
        network.SendAll(request);
        return true;
    }

    public bool TryPromptKeepFief(Settlement settlement)
    {
        if (!ModInformation.IsServer || settlement?.LastAttackerParty?.LeaderHero is not Hero attacker ||
            attacker.Clan == null || !attacker.IsHumanPlayerCharacter ||
            attacker.Clan.IsUnderMercenaryService || settlement.Town == null ||
            !settlement.Town.IsOwnerUnassigned || !CanUseGameplayRoute(out var config) ||
            !TryFindPlayer(attacker, out var player) ||
            !playerManager.TryGetPeer(player.ControllerId, out var peer) ||
            !objectManager.TryGetId(settlement, out string settlementId))
            return false;

        SendKeepFiefPrompt(peer, config, settlementId);
        return true;
    }

    private void HandleAllGameObjectsRegistered(MessagePayload<AllGameObjectsRegistered> _)
    {
        requestLedger.Reset();
        pending.Clear();
        visibleMessengerPrompts.Clear();
        nextRequestId = 0;
        acceptedRevision = -1;
        stateReady = true;
    }

    private void HandleSnapshotObserved(MessagePayload<NetworkDiplomacySnapshot> payload)
    {
        if (!ModInformation.IsClient || payload?.Who is not NetPeer serverPeer || payload.What == null ||
            configAuthority == null || !configAuthority.IsTrustedServer(serverPeer) ||
            !DiplomacySnapshotCodec.TryValidate(payload.What, out _))
            return;

        acceptedRevision = Math.Max(acceptedRevision, payload.What.Revision);
    }

    private void HandleSnapshotRequestForPendingPrompt(
        MessagePayload<NetworkRequestDiplomacySnapshot> payload)
    {
        if (!ModInformation.IsServer || payload?.Who is not NetPeer peer) return;
        GameThread.RunSafe(
            () =>
            {
                ResendPendingKeepFiefPrompt(peer);
                PublishMessengerOutcomesForPeer(peer);
            },
            context: nameof(DiplomacyOperationHandler));
    }

    private void ResendPendingKeepFiefPrompt(NetPeer peer)
    {
        if (!playerManager.TryGetPlayer(peer, out var player) ||
            !objectManager.TryGetObject(player.HeroId, out Hero actor) || actor?.Clan == null ||
            actor.Clan.IsUnderMercenaryService || !CanUseGameplayRoute(out var config))
            return;

        Settlement pendingSettlement = Settlement.All.FirstOrDefault(settlement =>
            settlement?.Town != null && settlement.Town.IsOwnerUnassigned &&
            settlement.LastAttackerParty?.LeaderHero == actor);
        if (pendingSettlement == null ||
            !objectManager.TryGetId(pendingSettlement, out string settlementId))
            return;

        SendKeepFiefPrompt(peer, config, settlementId);
    }

    private void SendKeepFiefPrompt(NetPeer peer, ModConfigSnapshot config, string settlementId)
    {
        long revision = CaptureServerRevisionOrAbort();
        network.Send(peer, new NetworkDiplomacyKeepFiefPrompt(config.SessionId, settlementId, revision));
    }

    private void HandleOperationRequest(MessagePayload<NetworkRequestDiplomacyOperation> payload)
    {
        if (!ModInformation.IsServer || payload?.Who is not NetPeer peer) return;
        GameThread.RunSafe(
            () => ApplyOperationRequest(peer, payload.What),
            context: nameof(DiplomacyOperationHandler));
    }

    private void ApplyOperationRequest(NetPeer peer, NetworkRequestDiplomacyOperation request)
    {
        if (!DiplomacyOperationProtocol.IsRequestShapeValid(request))
        {
            DisconnectPeer(peer, "sent a malformed Diplomacy operation envelope");
            return;
        }
        if (!CanUseGameplayRoute(out var config))
        {
            DisconnectPeer(peer, "requested Diplomacy gameplay while its authoritative route was unavailable");
            return;
        }
        if (!string.Equals(config.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            SendResult(peer, request, DiplomacyOperationStatus.StaleSession, CaptureServerRevisionOrAbort());
            return;
        }

        string commandKey = DiplomacyOperationProtocol.CommandKey(request);
        var replay = requestLedger.Inspect(
            peer, request.RequestId, commandKey, out NetworkDiplomacyOperationResult cached);
        if (replay == DiplomacyReplayDecision.Conflict)
        {
            DisconnectPeer(peer, "reused Diplomacy request ID " + request.RequestId + " with different payload");
            return;
        }
        if (replay == DiplomacyReplayDecision.Replay)
        {
            network.Send(peer, cached);
            return;
        }

        long revision = CaptureServerRevisionOrAbort();
        if (request.ExpectedRevision != revision)
        {
            SendAndRecord(peer, request, commandKey, DiplomacyOperationStatus.StaleState, revision);
            return;
        }
        if (!playerManager.TryGetPlayer(peer, out var player) ||
            !objectManager.TryGetObject(player.HeroId, out Hero actor) ||
            !objectManager.TryGetObject(player.MobilePartyId, out MobileParty actorParty) ||
            actor == null || actorParty == null)
        {
            SendAndRecord(peer, request, commandKey, DiplomacyOperationStatus.Rejected, revision);
            return;
        }

        try
        {
            if (!TryExecuteAuthoritativeOperation(
                    player.ControllerId,
                    actor,
                    actorParty,
                    request,
                    out string failure))
            {
                Logger.Information(
                    "Rejected Diplomacy {Operation} request {RequestId}: {Failure}",
                    request.Operation, request.RequestId, failure);
                SendAndRecord(peer, request, commandKey, DiplomacyOperationStatus.Rejected, revision);
                return;
            }

            snapshotPublisher.PublishIfChanged();
            revision = CaptureServerRevisionOrAbort();
            SendAndRecord(peer, request, commandKey, DiplomacyOperationStatus.Accepted, revision);
        }
        catch (Exception exception)
        {
            Logger.Fatal(
                exception,
                "Diplomacy operation {Operation} request {RequestId} failed after authoritative execution began",
                request.Operation,
                request.RequestId);
            throw new InvalidOperationException(
                "A Diplomacy server operation failed after execution began; the session must stop to prevent partial state.",
                exception);
        }
    }

    private bool TryExecuteAuthoritativeOperation(
        string controllerId,
        Hero actor,
        MobileParty actorParty,
        NetworkRequestDiplomacyOperation request,
        out string failure)
    {
        if (request.Operation is DiplomacyOperation.CompleteMessenger or
            DiplomacyOperation.CancelMessenger or DiplomacyOperation.AcknowledgeMessengerAccident)
            return TryExecuteMessengerFollowUp(
                controllerId, actor, actorParty, request, out failure);

        if (request.Operation != DiplomacyOperation.SendMessenger)
            return executor.TryExecute(actor, actorParty, request, out failure);

        if (!TryGetMessengerStore(out var store) || !store.CanDispatch(controllerId))
        {
            failure = "the controller already has the maximum active messengers";
            return false;
        }
        if (!objectManager.TryGetObject(request.TargetId, out Hero target) || target == null)
        {
            failure = "the messenger target no longer resolves";
            return false;
        }

        long arrivalTicks = executor.GetMessengerArrivalTicks(actor, actorParty, target);
        if (!executor.TryExecute(actor, actorParty, request, out failure)) return false;
        if (!store.TryDispatch(controllerId, request.TargetId, arrivalTicks, out _))
            throw new InvalidOperationException(
                "Diplomacy messenger cost was applied but its authoritative travel record could not be committed.");
        return true;
    }

    private bool TryExecuteMessengerFollowUp(
        string controllerId,
        Hero actor,
        MobileParty actorParty,
        NetworkRequestDiplomacyOperation request,
        out string failure)
    {
        failure = null;
        if (!TryGetMessengerStore(out var store))
        {
            failure = "the authoritative messenger store is unavailable";
            return false;
        }

        if (request.Operation == DiplomacyOperation.AcknowledgeMessengerAccident)
        {
            if (store.TryRemoveAccident(controllerId, request.IntValue, request.TargetId)) return true;
            failure = "the messenger accident is no longer pending for this controller";
            return false;
        }

        long nowTicks = CampaignTime.Now.NumTicks;
        if (!store.TryGetArrived(
                controllerId,
                request.IntValue,
                request.TargetId,
                nowTicks,
                out _))
        {
            failure = "the messenger has not arrived for this controller";
            return false;
        }

        if (request.Operation == DiplomacyOperation.CancelMessenger)
            return store.TryRemoveArrived(
                controllerId, request.IntValue, request.TargetId, nowTicks);

        if (!objectManager.TryGetObject(request.TargetId, out Hero target) || target == null ||
            !executor.TryCompleteMessenger(actor, actorParty, target, out failure))
            return false;

        if (!store.TryRemoveArrived(controllerId, request.IntValue, request.TargetId, nowTicks))
            throw new InvalidOperationException(
                "Diplomacy messenger completion applied but its authoritative travel record remained active.");
        return true;
    }

    private void HandleOperationResult(MessagePayload<NetworkDiplomacyOperationResult> payload)
    {
        if (!ModInformation.IsClient || payload?.Who is not NetPeer serverPeer ||
            configAuthority == null || !configAuthority.IsTrustedServer(serverPeer))
            return;

        if (!DiplomacyOperationProtocol.IsResultShapeValid(payload.What) ||
            !configAuthority.TryGetCurrent(out var config) ||
            !string.Equals(config.SessionId, payload.What.SessionId, StringComparison.Ordinal))
        {
            DisconnectPeer(serverPeer, "sent a malformed or wrong-session Diplomacy operation result");
            return;
        }
        if (!pending.TryGetValue(payload.What.RequestId, out var expected)) return;
        if (expected.Operation != payload.What.Operation ||
            !string.Equals(expected.SessionId, payload.What.SessionId, StringComparison.Ordinal) ||
            !string.Equals(expected.TargetId, payload.What.TargetId, StringComparison.Ordinal))
        {
            DisconnectPeer(serverPeer, "returned a Diplomacy result that did not match the pending command");
            return;
        }

        pending.Remove(payload.What.RequestId);
        acceptedRevision = Math.Max(acceptedRevision, payload.What.Revision);
        if (payload.What.Status != DiplomacyOperationStatus.Accepted)
        {
            InformationManager.DisplayMessage(new InformationMessage(
                "The Diplomacy action could not be applied because its campaign state changed. Reopen the option and try again."));
            return;
        }

        switch (payload.What.Operation)
        {
            case DiplomacyOperation.SendMessenger:
                InformationManager.DisplayMessage(new InformationMessage(
                    "Messenger dispatched by the co-op server."));
                break;
            case DiplomacyOperation.CompleteMessenger:
                PresentMessengerDialogue(payload.What.TargetId);
                break;
            default:
                InformationManager.DisplayMessage(new InformationMessage(
                    "Diplomacy action accepted by the co-op server."));
                break;
        }
    }

    private void HandleKeepFiefPrompt(MessagePayload<NetworkDiplomacyKeepFiefPrompt> payload)
    {
        if (!ModInformation.IsClient || payload?.Who is not NetPeer serverPeer ||
            configAuthority == null || !configAuthority.IsTrustedServer(serverPeer))
            return;

        if (!DiplomacyOperationProtocol.IsKeepFiefPromptShapeValid(payload.What) ||
            !configAuthority.TryGetCurrent(out var config) ||
            !string.Equals(config.SessionId, payload.What.SessionId, StringComparison.Ordinal) ||
            !objectManager.TryGetObject(payload.What.SettlementId, out Settlement settlement) ||
            settlement == null)
        {
            DisconnectPeer(serverPeer, "sent a malformed or unresolvable Diplomacy keep-fief prompt");
            return;
        }

        acceptedRevision = Math.Max(acceptedRevision, payload.What.Revision);
        var title = new TaleWorlds.Localization.TextObject("{=N06wk0dB}Settlement Captured").ToString();
        var body = new TaleWorlds.Localization.TextObject(
            "{=Zy0yjTha}As the capturer of {SETTLEMENT_NAME}, you have the right of first refusal. Would you like to claim this fief?");
        body.SetTextVariable("SETTLEMENT_NAME", settlement.Name);
        InformationManager.ShowInquiry(new InquiryData(
            title,
            body.ToString(),
            true,
            true,
            new TaleWorlds.Localization.TextObject("{=Y94H6XnK}Accept").ToString(),
            new TaleWorlds.Localization.TextObject("{=cOgmdp9e}Decline").ToString(),
            () => TrySubmit(new DiplomacyLocalOperation(DiplomacyOperation.AcceptKeepFief, settlement)),
            () => TrySubmit(new DiplomacyLocalOperation(DiplomacyOperation.DeclineKeepFief, settlement))));
    }

    private void HandleMessengerArrivalPrompt(
        MessagePayload<NetworkDiplomacyMessengerArrivalPrompt> payload)
    {
        if (!ModInformation.IsClient || payload?.Who is not NetPeer serverPeer ||
            configAuthority == null || !configAuthority.IsTrustedServer(serverPeer))
            return;

        if (!DiplomacyOperationProtocol.IsMessengerArrivalPromptShapeValid(payload.What) ||
            !configAuthority.TryGetCurrent(out var config) ||
            !string.Equals(config.SessionId, payload.What.SessionId, StringComparison.Ordinal) ||
            !objectManager.TryGetObject(payload.What.TargetId, out Hero target) || target == null)
        {
            DisconnectPeer(serverPeer, "sent a malformed or unresolvable Diplomacy messenger arrival");
            return;
        }

        if (!IsLocalMessengerPresentationAvailable() ||
            !visibleMessengerPrompts.Add(payload.What.MessengerId))
            return;

        acceptedRevision = Math.Max(acceptedRevision, payload.What.Revision);
        if (!TryGetMessengerArrivalPresentation(target, out string body, out bool canAccept))
            throw new InvalidOperationException(
                "Diplomacy messenger arrival presentation could not be prepared from the pinned manager.");

        int messengerId = payload.What.MessengerId;
        InformationManager.ShowInquiry(new InquiryData(
            new TaleWorlds.Localization.TextObject("{=uy86VZX2}Messenger Arrived").ToString(),
            body,
            canAccept,
            true,
            GameTexts.FindText("str_ok").ToString(),
            new TaleWorlds.Localization.TextObject("{=kMjfN2fB}Cancel Messenger").ToString(),
            () => TrySubmitMessengerFollowUp(
                DiplomacyOperation.CompleteMessenger, target, messengerId),
            () => TrySubmitMessengerFollowUp(
                DiplomacyOperation.CancelMessenger, target, messengerId)));
    }

    private void HandleMessengerAccident(MessagePayload<NetworkDiplomacyMessengerAccident> payload)
    {
        if (!ModInformation.IsClient || payload?.Who is not NetPeer serverPeer ||
            configAuthority == null || !configAuthority.IsTrustedServer(serverPeer))
            return;

        if (!DiplomacyOperationProtocol.IsMessengerAccidentShapeValid(payload.What) ||
            !configAuthority.TryGetCurrent(out var config) ||
            !string.Equals(config.SessionId, payload.What.SessionId, StringComparison.Ordinal) ||
            !objectManager.TryGetObject(payload.What.TargetId, out Hero target) || target == null)
        {
            DisconnectPeer(serverPeer, "sent a malformed or unresolvable Diplomacy messenger accident");
            return;
        }
        if (!visibleMessengerPrompts.Add(payload.What.MessengerId)) return;

        var body = new TaleWorlds.Localization.TextObject(
            MessengerAccidentTexts[payload.What.AccidentIndex]);
        body.SetTextVariable("HERO_NAME", target.Name);
        int messengerId = payload.What.MessengerId;
        InformationManager.ShowInquiry(new InquiryData(
            new TaleWorlds.Localization.TextObject("{=nYrezEOX}Messenger killed").ToString(),
            body.ToString(),
            true,
            false,
            GameTexts.FindText("str_ok").ToString(),
            string.Empty,
            () => TrySubmitMessengerFollowUp(
                DiplomacyOperation.AcknowledgeMessengerAccident, target, messengerId),
            null));
    }

    private void TrySubmitMessengerFollowUp(
        DiplomacyOperation operation,
        Hero target,
        int messengerId)
    {
        visibleMessengerPrompts.Remove(messengerId);
        if (TrySubmit(new DiplomacyLocalOperation(operation, target, intValue: messengerId))) return;
        InformationManager.DisplayMessage(new InformationMessage(
            "The messenger response is unavailable until the co-op authority handshake is complete."));
    }

    private void PresentMessengerDialogue(string targetId)
    {
        if (!objectManager.TryGetObject(targetId, out Hero target) || target == null)
            throw new InvalidOperationException(
                "The server-authorized Diplomacy messenger target no longer resolves.");

        object manager = GetMessengerPresentationManager();
        Type messengerType = DiplomacyCompatibilityPolicy.ResolveType("Diplomacy.Messengers.Messenger") ??
                             throw new TypeLoadException("Diplomacy.Messengers.Messenger");
        object messenger = Activator.CreateInstance(
            messengerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new object[] { target, CampaignTime.Now },
            null) ?? throw new InvalidOperationException(
            "Diplomacy messenger presentation record could not be constructed.");
        MethodInfo startDialogue = AccessTools.Method(
            manager.GetType(), "StartDialogue", new[] { typeof(Hero), messengerType }) ??
            throw new MissingMethodException(manager.GetType().FullName, "StartDialogue");

        using (new AllowedThread()) startDialogue.Invoke(manager, new[] { target, messenger });
    }

    private bool TryGetMessengerArrivalPresentation(
        Hero target,
        out string body,
        out bool canAccept)
    {
        body = null;
        canAccept = false;
        object manager = GetMessengerPresentationManager();
        MethodInfo getText = manager.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(method => method.Name == "GetMessengerArrivedText" &&
                                       method.GetParameters().Length == 4);
        if (getText == null) return false;

        object[] arguments = { Hero.MainHero.MapFaction, target.MapFaction, target, null };
        object text = getText.Invoke(manager, arguments);
        object cost = arguments[3];
        MethodInfo canPay = cost == null
            ? null
            : AccessTools.Method(cost.GetType(), "CanPayCost", Type.EmptyTypes);
        body = text?.ToString();
        canAccept = cost == null || (canPay != null && (bool)canPay.Invoke(cost, null));
        return !string.IsNullOrEmpty(body);
    }

    private bool IsLocalMessengerPresentationAvailable()
    {
        object manager = GetMessengerPresentationManager();
        MethodInfo available = AccessTools.Method(
            manager.GetType(), "IsPlayerHeroAvailable", Type.EmptyTypes);
        return available != null && (bool)available.Invoke(null, null);
    }

    private static object GetMessengerPresentationManager()
    {
        var behaviorType = DiplomacyCompatibilityPolicy.ResolveType(
            "Diplomacy.CampaignBehaviors.MessengerBehavior");
        var getBehavior = Campaign.Current?.CampaignBehaviorManager?.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(method => method.Name == "GetBehavior" && method.IsGenericMethodDefinition &&
                                       method.GetParameters().Length == 0);
        object behavior = behaviorType == null || getBehavior == null
            ? null
            : getBehavior.MakeGenericMethod(behaviorType)
                .Invoke(Campaign.Current.CampaignBehaviorManager, null);
        object manager = behavior == null
            ? null
            : AccessTools.Field(behaviorType, "_messengerManager")?.GetValue(behavior);
        return manager ?? throw new InvalidOperationException(
            "Diplomacy messenger presentation manager is unavailable.");
    }

    internal void PublishPendingMessengerOutcomes()
    {
        if (!ModInformation.IsServer) return;
        foreach (var player in playerManager.Players)
        {
            if (playerManager.TryGetPeer(player.ControllerId, out var peer))
                PublishMessengerOutcomes(player, peer);
        }
    }

    private void PublishMessengerOutcomesForPeer(NetPeer peer)
    {
        if (!ModInformation.IsServer ||
            !playerManager.TryGetPlayer(peer, out var player))
            return;
        PublishMessengerOutcomes(player, peer);
    }

    private void PublishMessengerOutcomes(Players.Data.Player player, NetPeer peer)
    {
        if (player == null || peer == null || !CanUseGameplayRoute(out var config) ||
            !TryGetMessengerStore(out var store) ||
            !objectManager.TryGetObject(player.HeroId, out Hero actor) || actor == null)
            return;

        foreach (var accident in store.AccidentsFor(player.ControllerId))
        {
            if (!objectManager.TryGetObject(accident.TargetHeroId, out Hero target) || target == null)
            {
                store.TryRemoveAccident(
                    player.ControllerId, accident.MessengerId, accident.TargetHeroId);
                continue;
            }
            network.Send(peer, new NetworkDiplomacyMessengerAccident(
                config.SessionId,
                accident.MessengerId,
                accident.TargetHeroId,
                accident.AccidentIndex));
        }

        long nowTicks = CampaignTime.Now.NumTicks;
        long revision = CaptureServerRevisionOrAbort();
        foreach (var arrival in store.ArrivalsFor(player.ControllerId, nowTicks))
        {
            if (!objectManager.TryGetObject(arrival.TargetHeroId, out Hero target) ||
                target == null || target.IsDead)
            {
                store.TryRemoveArrived(
                    player.ControllerId,
                    arrival.MessengerId,
                    arrival.TargetHeroId,
                    nowTicks);
                continue;
            }
            if (target.PartyBelongedTo != null && target.PartyBelongedTo == actor.PartyBelongedTo)
            {
                if (store.TryMarkAccident(arrival.MessengerId, accidentIndex: 1))
                    network.Send(peer, new NetworkDiplomacyMessengerAccident(
                        config.SessionId,
                        arrival.MessengerId,
                        arrival.TargetHeroId,
                        accidentIndex: 1));
                continue;
            }
            if (!executor.IsMessengerTargetAvailableNow(target)) continue;

            network.Send(peer, new NetworkDiplomacyMessengerArrivalPrompt(
                config.SessionId,
                arrival.MessengerId,
                arrival.TargetHeroId,
                revision));
        }
    }

    internal void ProcessMessengerAccidents()
    {
        if (!ModInformation.IsServer || !executor.AreMessengerAccidentsEnabled() ||
            !TryGetMessengerStore(out var store))
            return;

        foreach (var record in store.PendingForAccident(CampaignTime.Now.NumTicks))
        {
            if (MBRandom.RandomFloat >= 0.005f) continue;
            store.TryMarkAccident(
                record.MessengerId,
                MBRandom.RandomInt(0, MessengerAccidentTexts.Length));
            break;
        }
    }

    private static bool TryGetMessengerStore(out DiplomacyMessengerAuthorityStore store)
    {
        store = Campaign.Current?
            .GetCampaignBehavior<DiplomacyMessengerAuthorityBehavior>()?
            .Store;
        return store != null;
    }

    private bool CanUseGameplayRoute(out ModConfigSnapshot config)
    {
        config = null;
        return stateReady && runtime.IsAvailable && configAuthority.TryGetCurrent(out config) &&
               capabilityRegistry.IsEnabled(DiplomacyCapabilitySource.ModuleId, DiplomacyCapabilitySource.Operation);
    }

    private long CaptureServerRevisionOrAbort()
    {
        var snapshot = runtime.CaptureSnapshot();
        string failure = snapshot == null ? "snapshot is null" : null;
        if (snapshot == null || !DiplomacySnapshotCodec.TryValidate(snapshot, out failure))
            throw new InvalidOperationException(
                "Authoritative Diplomacy state could not be captured for an operation: " + failure);
        return snapshot.Revision;
    }

    private void SendAndRecord(
        NetPeer peer,
        NetworkRequestDiplomacyOperation request,
        string commandKey,
        DiplomacyOperationStatus status,
        long revision)
    {
        var result = new NetworkDiplomacyOperationResult(
            request.SessionId,
            request.RequestId,
            request.Operation,
            status,
            revision,
            request.TargetId);
        requestLedger.Record(peer, request.RequestId, commandKey, result);
        network.Send(peer, result);
    }

    private void SendResult(
        NetPeer peer,
        NetworkRequestDiplomacyOperation request,
        DiplomacyOperationStatus status,
        long revision) => network.Send(peer, new NetworkDiplomacyOperationResult(
            request.SessionId,
            request.RequestId,
            request.Operation,
            status,
            revision,
            request.TargetId));

    private bool TryGetId(object value, out string id, bool allowNull = false)
    {
        if (value == null)
        {
            id = string.Empty;
            return allowNull;
        }
        return objectManager.TryGetId(value, out id);
    }

    private bool TryFindPlayer(Hero hero, out Players.Data.Player result)
    {
        result = playerManager.Players.FirstOrDefault(player =>
            string.Equals(player.HeroId, hero.StringId, StringComparison.Ordinal));
        if (result != null) return true;

        foreach (var player in playerManager.Players)
        {
            if (objectManager.TryGetObject(player.HeroId, out Hero candidate) && ReferenceEquals(candidate, hero))
            {
                result = player;
                return true;
            }
        }
        return false;
    }

    private static void DisconnectPeer(NetPeer peer, string reason)
    {
        Logger.Fatal("Disconnecting peer {Peer}: {Reason}", peer?.Id, reason);
        peer?.Disconnect();
    }
}

internal static class DiplomacyExplicitOperationScope
{
    [ThreadStatic] private static int depth;
    public static bool IsAllowed => depth > 0;

    public static IDisposable Enter()
    {
        depth++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            depth = Math.Max(0, depth - 1);
        }
    }
}

internal static class DiplomacyAutomatedOperationScope
{
    [ThreadStatic] private static int depth;
    public static bool IsAllowed => depth > 0;

    public static IDisposable Enter()
    {
        depth++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            depth = Math.Max(0, depth - 1);
        }
    }
}

internal sealed class DiplomacyOperationExecutor
{
    private readonly IObjectManager objectManager;
    private readonly IDiplomacyDonateGoldInterface donateGoldInterface;

    public DiplomacyOperationExecutor(
        IObjectManager objectManager,
        IDiplomacyDonateGoldInterface donateGoldInterface)
    {
        this.objectManager = objectManager;
        this.donateGoldInterface = donateGoldInterface;
    }

    public long GetMessengerArrivalTicks(Hero actor, MobileParty actorParty, Hero target)
    {
        using (new BarterPlayerContext(actor, actorParty))
        using (new AllowedThread())
        {
            Type managerType = RequiredType("Diplomacy.Messengers.MessengerManager");
            MethodInfo getHours = AccessTools.Method(
                managerType, "GetHourToArrive", new[] { typeof(Hero) }) ??
                throw new MissingMethodException(managerType.FullName, "GetHourToArrive");
            int hours = Math.Max(0, (int)getHours.Invoke(null, new object[] { target }));
            return CampaignTime.HoursFromNow(hours).NumTicks;
        }
    }

    public bool TryCompleteMessenger(
        Hero actor,
        MobileParty actorParty,
        Hero target,
        out string failure)
    {
        failure = null;
        if (target == null || target.IsDead || target.IsHumanPlayerCharacter ||
            target.PartyBelongedTo == actorParty || !IsMessengerTargetAvailableNow(target))
        {
            failure = "the messenger addressee is not currently available";
            return false;
        }
        if (target.IsWanderer && target.HeroState == (Hero.CharacterStates)0 &&
            target.BornSettlement == null)
        {
            failure = "the wanderer addressee has no settlement for an authorized conversation";
            return false;
        }

        using (new BarterPlayerContext(actor, actorParty))
        using (DiplomacyExplicitOperationScope.Enter())
        using (new AllowedThread())
        {
            if (!TryGetMessengerArrivalExpense(target, out int expense, out failure)) return false;
            if (expense > 0) BribeGuardsAction.Apply(target.CurrentSettlement, expense);
            if (target.IsWanderer && target.HeroState == (Hero.CharacterStates)0)
            {
                target.ChangeState(Hero.CharacterStates.Active);
                EnterSettlementAction.ApplyForCharacterOnly(target, target.BornSettlement);
            }
        }
        return true;
    }

    public bool IsMessengerTargetAvailableNow(Hero target)
    {
        Type managerType = RequiredType("Diplomacy.Messengers.MessengerManager");
        MethodInfo available = AccessTools.Method(
            managerType, "IsTargetHeroAvailableNow", new[] { typeof(Hero) }) ??
            throw new MissingMethodException(managerType.FullName, "IsTargetHeroAvailableNow");
        return (bool)available.Invoke(null, new object[] { target });
    }

    public bool AreMessengerAccidentsEnabled()
    {
        Type settingsType = RequiredType("Diplomacy.Settings");
        PropertyInfo instanceProperty = settingsType.GetProperty(
            "Instance",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.FlattenHierarchy) ?? AccessTools.Property(settingsType, "Instance");
        object settings = instanceProperty?.GetValue(null) ??
                          throw new InvalidOperationException("Diplomacy settings instance is unavailable.");
        PropertyInfo enabledProperty = AccessTools.Property(
            settings.GetType(), "EnableMessengerAccidents") ??
            throw new MissingMemberException(settings.GetType().FullName, "EnableMessengerAccidents");
        return (bool)enabledProperty.GetValue(settings);
    }

    public bool TryExecute(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestDiplomacyOperation request,
        out string failure)
    {
        failure = null;
        if (actor == null || actorParty == null || !DiplomacyActorAuthority.CanExecute(
                request.Operation,
                actor.PartyBelongedTo == actorParty,
                actorParty.LeaderHero == actor,
                actor.Clan != null))
        {
            failure = "authenticated controller has no matching active hero, clan, and party";
            return false;
        }

        if (!TryPreflight(actor, request, out object target, out object secondary, out failure))
            return false;

        using (new BarterPlayerContext(actor, actorParty))
        using (DiplomacyExplicitOperationScope.Enter())
        using (new AllowedThread())
        {
            switch (request.Operation)
            {
                case DiplomacyOperation.DonateGold:
                    ApplyDonation(actor, (Clan)target, request.IntValue);
                    break;
                case DiplomacyOperation.GrantFief:
                    InvokeStatic("Diplomacy.Actions.GrantFiefAction", "Apply", secondary, target);
                    break;
                case DiplomacyOperation.SendMessenger:
                    ApplyMessengerCost((Hero)target);
                    break;
                case DiplomacyOperation.MakePeace:
                    InvokeStatic(
                        "Diplomacy.DiplomaticAction.WarPeace.KingdomPeaceAction",
                        "ApplyPeace",
                        target, secondary, null, true, true, false);
                    break;
                case DiplomacyOperation.DeclareWar:
                    ApplyDeclareWar((Kingdom)target, (Kingdom)secondary);
                    break;
                case DiplomacyOperation.EndAlliance:
                    Campaign.Current.GetCampaignBehavior<AllianceCampaignBehavior>()
                        .EndAlliance((Kingdom)target, (Kingdom)secondary);
                    break;
                case DiplomacyOperation.FormNonAggressionPact:
                    ApplyNonAggressionPact((Kingdom)target, (Kingdom)secondary);
                    break;
                case DiplomacyOperation.AcceptKeepFief:
                    ApplyKeepFief(actor, (Settlement)target, accept: true);
                    break;
                case DiplomacyOperation.DeclineKeepFief:
                    ApplyKeepFief(actor, (Settlement)target, accept: false);
                    break;
                default:
                    throw new InvalidOperationException("Unknown Diplomacy operation.");
            }
        }

        return true;
    }

    private bool TryPreflight(
        Hero actor,
        NetworkRequestDiplomacyOperation request,
        out object target,
        out object secondary,
        out string failure)
    {
        target = null;
        secondary = null;
        failure = null;

        bool resolved = request.Operation switch
        {
            DiplomacyOperation.DonateGold =>
                TryResolve(request.TargetId, out target, typeof(Clan)),
            DiplomacyOperation.GrantFief =>
                TryResolve(request.TargetId, out target, typeof(Clan)) &&
                TryResolve(request.SecondaryTargetId, out secondary, typeof(Settlement)),
            DiplomacyOperation.SendMessenger =>
                TryResolve(request.TargetId, out target, typeof(Hero)),
            DiplomacyOperation.MakePeace or DiplomacyOperation.DeclareWar or
                DiplomacyOperation.EndAlliance or DiplomacyOperation.FormNonAggressionPact =>
                TryResolve(request.TargetId, out target, typeof(Kingdom)) &&
                TryResolve(request.SecondaryTargetId, out secondary, typeof(Kingdom)),
            DiplomacyOperation.AcceptKeepFief or DiplomacyOperation.DeclineKeepFief =>
                TryResolve(request.TargetId, out target, typeof(Settlement)),
            _ => false,
        };
        if (!resolved)
        {
            failure = "one or more stable campaign targets no longer resolve";
            return false;
        }

        switch (request.Operation)
        {
            case DiplomacyOperation.DonateGold:
                var donatedClan = (Clan)target;
                if (donatedClan == actor.Clan || donatedClan.Leader == null ||
                    donatedClan.Kingdom == null || donatedClan.Kingdom != actor.Clan.Kingdom ||
                    request.IntValue <= 0 || request.IntValue > actor.Gold)
                    failure = "donation target or amount is no longer eligible";
                break;
            case DiplomacyOperation.GrantFief:
                var grantedClan = (Clan)target;
                var settlement = (Settlement)secondary;
                if (!IsRulingActor(actor, actor.Clan.Kingdom) || grantedClan == actor.Clan ||
                    grantedClan.Kingdom != actor.Clan.Kingdom || grantedClan.IsMinorFaction ||
                    grantedClan.IsUnderMercenaryService || grantedClan.Leader == null ||
                    settlement.Town == null || settlement.OwnerClan != actor.Clan ||
                    !actor.Clan.Fiefs.Contains(settlement.Town))
                    failure = "fief or recipient clan is no longer eligible";
                break;
            case DiplomacyOperation.SendMessenger:
                var hero = (Hero)target;
                if (hero == actor || hero.IsHumanPlayerCharacter || hero.IsDead ||
                    (!hero.IsActive && !hero.IsWanderer) ||
                    hero.PartyBelongedTo?.Position.IsOnLand == false)
                    failure = "messenger target is no longer available";
                break;
            case DiplomacyOperation.MakePeace:
                if (!ValidateKingdomPair(actor, (Kingdom)target, (Kingdom)secondary) ||
                    !((Kingdom)target).IsAtWarWith((Kingdom)secondary))
                    failure = "the actor no longer rules the proposing kingdom or the war ended";
                break;
            case DiplomacyOperation.DeclareWar:
                if (!ValidateKingdomPair(actor, (Kingdom)target, (Kingdom)secondary) ||
                    ((Kingdom)target).IsAtWarWith((Kingdom)secondary) ||
                    ((Kingdom)target).IsAllyWith((Kingdom)secondary))
                    failure = "the actor no longer rules the proposing kingdom or the stance changed";
                break;
            case DiplomacyOperation.EndAlliance:
                var allianceBehavior = Campaign.Current.GetCampaignBehavior<AllianceCampaignBehavior>();
                if (!ValidateKingdomPair(actor, (Kingdom)target, (Kingdom)secondary) ||
                    allianceBehavior == null ||
                    !allianceBehavior.IsAllyWithKingdom((Kingdom)target, (Kingdom)secondary))
                    failure = "the actor no longer rules the proposing kingdom or the alliance ended";
                break;
            case DiplomacyOperation.FormNonAggressionPact:
                if (!ValidateKingdomPair(actor, (Kingdom)target, (Kingdom)secondary) ||
                    !CanFormNonAggressionPact((Kingdom)target, (Kingdom)secondary))
                    failure = "the actor no longer rules the proposing kingdom or pact conditions changed";
                break;
            case DiplomacyOperation.AcceptKeepFief:
            case DiplomacyOperation.DeclineKeepFief:
                var captured = (Settlement)target;
                if (captured.Town == null || !captured.Town.IsOwnerUnassigned ||
                    captured.LastAttackerParty?.LeaderHero != actor || actor.Clan.IsUnderMercenaryService)
                    failure = "the first-refusal claim is no longer pending for this controller";
                break;
        }

        return failure == null;
    }

    private void ApplyDonation(Hero actor, Clan clan, int amount)
    {
        var verdict = donateGoldInterface.TryApplyDonation(actor, clan, amount);
        if (verdict != DiplomacyDonationVerdict.Applied)
            throw new InvalidOperationException("Diplomacy donation apply returned " + verdict + ".");

        var model = Campaign.Current.Models.DiplomacyModel;
        int baseGain = (int)Math.Round(
            amount * model.DenarsToInfluence() * model.GetRelationValueOfSupportingClan() /
            (float)model.GetInfluenceCostOfSupportingClan());
        if (baseGain <= 0) return;

        int generosity = MBMath.ClampInt(baseGain * 5, 0, 50);
        int relation = clan.Leader.GetRelation(actor);
        int calculatingFactor = Math.Max(70 - relation, 0) / 20 * (clan.Tier / 2);
        int calculating = MBMath.ClampInt(baseGain * calculatingFactor, 0, 50);
        InvokeStatic("Diplomacy.Character.PlayerCharacterTraitHelper", "UpdateTrait",
            DefaultTraits.Generosity, generosity, Enum.ToObject(ResolveActionNotesType(), 0), null);
        InvokeStatic("Diplomacy.Character.PlayerCharacterTraitHelper", "UpdateTrait",
            DefaultTraits.Calculating, calculating, Enum.ToObject(ResolveActionNotesType(), 0), null);
    }

    private static Type ResolveActionNotesType() =>
        typeof(Hero).Assembly.GetType(
            "TaleWorlds.CampaignSystem.ActionNotes",
            throwOnError: false) ??
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(
                "TaleWorlds.CampaignSystem.ActionNotes",
                false))
            .FirstOrDefault(type => type != null) ??
        throw new InvalidOperationException("Trait ActionNotes type is unavailable.");

    private static void ApplyMessengerCost(Hero target)
    {
        Type calculator = RequiredType("Diplomacy.Costs.DiplomacyCostCalculator");
        MethodInfo determine = AccessTools.Method(calculator, "DetermineCostForSendingMessenger", new[] { typeof(Hero) }) ??
                               throw new MissingMethodException(calculator.FullName, "DetermineCostForSendingMessenger");
        object cost = determine.Invoke(null, new object[] { target }) ??
                      throw new InvalidOperationException("Diplomacy returned no messenger cost.");
        Type managerType = RequiredType("Diplomacy.Messengers.MessengerManager");
        MethodInfo canSend = managerType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name == "CanSendMessengerWithCost" && method.GetParameters().Length == 3);
        object[] validation = { target, cost, null };
        if (!(bool)canSend.Invoke(null, validation))
            throw new InvalidOperationException("Diplomacy messenger cost or target validation failed.");
        InvokeInstance(cost, "ApplyCost");
    }

    private static bool TryGetMessengerArrivalExpense(
        Hero target,
        out int expense,
        out string failure)
    {
        expense = 0;
        failure = null;
        Type managerType = RequiredType("Diplomacy.Messengers.MessengerManager");
        object manager = Activator.CreateInstance(managerType, nonPublic: true) ??
                         throw new InvalidOperationException("Diplomacy messenger manager could not be constructed.");
        MethodInfo getText = managerType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name == "GetMessengerArrivedText" &&
                              method.GetParameters().Length == 4);
        object[] arguments = { Hero.MainHero.MapFaction, target.MapFaction, target, null };
        getText.Invoke(manager, arguments);
        object cost = arguments[3];
        if (cost == null) return true;

        MethodInfo canPay = AccessTools.Method(cost.GetType(), "CanPayCost", Type.EmptyTypes) ??
                            throw new MissingMethodException(cost.GetType().FullName, "CanPayCost");
        if (!(bool)canPay.Invoke(cost, null))
        {
            failure = "the controller can no longer afford the messenger's arrival expense";
            return false;
        }
        object rawValue = AccessTools.Property(cost.GetType(), "Value")?.GetValue(cost) ?? 0f;
        expense = Math.Max(0, (int)Convert.ToSingle(rawValue));
        return true;
    }

    private static void ApplyDeclareWar(Kingdom proposing, Kingdom other)
    {
        Type calculator = RequiredType("Diplomacy.Costs.DiplomacyCostCalculator");
        object cost = AccessTools.Method(calculator, "DetermineCostForDeclaringWar")
            ?.Invoke(null, new object[] { proposing, true }) ??
            throw new InvalidOperationException("Diplomacy declare-war cost could not be calculated.");
        InvokeInstance(cost, "ApplyCost");
        DeclareWarAction.ApplyByKingdomDecision(proposing, other);
    }

    private static bool CanFormNonAggressionPact(Kingdom proposing, Kingdom other)
    {
        Type actionType = RequiredType(
            "Diplomacy.DiplomaticAction.NonAggressionPact.FormNonAggressionPactAction");
        object instance = Activator.CreateInstance(actionType, nonPublic: true);
        MethodInfo passes = AccessTools.Method(actionType, "PassesConditions") ??
                            throw new MissingMethodException(actionType.FullName, "PassesConditions");
        return (bool)passes.Invoke(instance, new object[] { proposing, other, true, false });
    }

    private static void ApplyNonAggressionPact(Kingdom proposing, Kingdom other)
    {
        Type actionType = RequiredType(
            "Diplomacy.DiplomaticAction.NonAggressionPact.FormNonAggressionPactAction");
        Type openBase = RequiredType("Diplomacy.DiplomaticAction.AbstractDiplomaticAction`1");
        Type closedBase = openBase.MakeGenericType(actionType);
        MethodInfo apply = AccessTools.Method(closedBase, "Apply") ??
                           throw new MissingMethodException(closedBase.FullName, "Apply");
        apply.Invoke(null, new object[] { proposing, other, true, false, null, false });
    }

    private static void ApplyKeepFief(Hero actor, Settlement settlement, bool accept)
    {
        settlement.Town.IsOwnerUnassigned = !accept;
        if (!accept) return;

        ChangeOwnerOfSettlementAction.ApplyByDefault(actor, settlement);
        Type eventType = RequiredType("Diplomacy.Character.PlayerCharacterTraitEventExperience");
        object fiefClaimed = AccessTools.Property(eventType, "FiefClaimed")?.GetValue(null) ??
                             AccessTools.Field(eventType, "FiefClaimed")?.GetValue(null) ??
                             throw new InvalidOperationException("Diplomacy FiefClaimed trait event is unavailable.");
        InvokeInstance(fiefClaimed, "Apply");
    }

    private bool TryResolve(string id, out object value, Type type)
    {
        value = null;
        MethodInfo generic = objectManager.GetType().GetMethods()
            .Where(method => method.Name == "TryGetObject" && method.IsGenericMethodDefinition)
            .Single(method => method.GetParameters().Length == 2);
        object[] arguments = { id, null };
        bool found = (bool)generic.MakeGenericMethod(type).Invoke(objectManager, arguments);
        value = arguments[1];
        return found && value != null;
    }

    private static bool ValidateKingdomPair(Hero actor, Kingdom proposing, Kingdom other) =>
        proposing != null && other != null && proposing != other &&
        IsRulingActor(actor, proposing) && actor.Clan?.Kingdom == proposing &&
        !proposing.IsEliminated && !other.IsEliminated;

    private static bool IsRulingActor(Hero actor, Kingdom kingdom) =>
        actor != null && kingdom != null && kingdom.Leader == actor && kingdom.RulingClan == actor.Clan;

    private static Type RequiredType(string name) =>
        DiplomacyCompatibilityPolicy.ResolveType(name) ?? throw new TypeLoadException(name);

    private static object InvokeStatic(string typeName, string methodName, params object[] arguments)
    {
        Type type = RequiredType(typeName);
        MethodInfo method = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name == methodName &&
                                          candidate.GetParameters().Length == arguments.Length) ??
                            throw new MissingMethodException(typeName, methodName);
        return method.Invoke(null, arguments);
    }

    private static object InvokeInstance(object instance, string methodName)
    {
        MethodInfo method = AccessTools.Method(instance?.GetType(), methodName, Type.EmptyTypes) ??
                            throw new MissingMethodException(instance?.GetType().FullName, methodName);
        return method.Invoke(instance, null);
    }
}
