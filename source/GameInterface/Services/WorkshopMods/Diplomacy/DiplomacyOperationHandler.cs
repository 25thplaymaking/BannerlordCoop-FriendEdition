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
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<AllGameObjectsRegistered>(HandleAllGameObjectsRegistered);
        messageBroker.Unsubscribe<NetworkDiplomacySnapshot>(HandleSnapshotObserved);
        messageBroker.Unsubscribe<NetworkRequestDiplomacySnapshot>(HandleSnapshotRequestForPendingPrompt);
        messageBroker.Unsubscribe<NetworkRequestDiplomacyOperation>(HandleOperationRequest);
        messageBroker.Unsubscribe<NetworkDiplomacyOperationResult>(HandleOperationResult);
        messageBroker.Unsubscribe<NetworkDiplomacyKeepFiefPrompt>(HandleKeepFiefPrompt);
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
            () => ResendPendingKeepFiefPrompt(peer),
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
            if (!executor.TryExecute(actor, actorParty, request, out string failure))
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

        if (payload.What.Operation == DiplomacyOperation.SendMessenger)
            PresentMessenger(payload.What.TargetId);
        else
            InformationManager.DisplayMessage(new InformationMessage("Diplomacy action accepted by the co-op server."));
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

    private void PresentMessenger(string targetId)
    {
        if (!objectManager.TryGetObject(targetId, out Hero target) || target == null)
        {
            InformationManager.DisplayMessage(new InformationMessage(
                "The messenger was authorized, but its addressee is no longer available."));
            return;
        }

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
        MethodInfo send = manager == null ? null : AccessTools.Method(manager.GetType(), "SendMessenger", new[] { typeof(Hero) });
        if (send == null)
            throw new InvalidOperationException("Diplomacy messenger presentation manager is unavailable.");

        using (new AllowedThread()) send.Invoke(manager, new object[] { target });
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

    public bool TryExecute(
        Hero actor,
        MobileParty actorParty,
        NetworkRequestDiplomacyOperation request,
        out string failure)
    {
        failure = null;
        if (actor == null || actorParty == null || actor.PartyBelongedTo != actorParty ||
            actorParty.LeaderHero != actor || actor.Clan == null)
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
            .Single(method => method.Name == "CanSendMessengerWithCost" && method.GetParameters().Length == 2);
        if (!(bool)canSend.Invoke(null, new[] { target, cost }))
            throw new InvalidOperationException("Diplomacy messenger cost or target validation failed.");
        InvokeInstance(cost, "ApplyCost");
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
