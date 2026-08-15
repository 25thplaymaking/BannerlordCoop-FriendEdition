using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface;
using GameInterface.Configuration;
using GameInterface.Services.MapEvents;
using GameInterface.Services.WorkshopMods.Core;
using Missions.Agents.Extensions;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Missions.WorkshopMods.Combat;

public interface IDismembermentPresentationHandler : IHandler
{
    void ProcessAcceptedBlow(
        object missionLogic,
        Agent attacker,
        Agent victim,
        Blow blow,
        AttackCollisionData collisionData);
}

/// <summary>
/// Converts the accepted victim-authority blow into one deterministic DismembermentPlus cosmetic
/// outcome, then replays that outcome on the other battle peers without calling RegisterBlow.
/// </summary>
internal sealed class DismembermentPresentationHandler : IDismembermentPresentationHandler
{
    internal const string ModuleId = "DismembermentPlus";
    internal const string Operation = "ReplicatedPresentation";

    private static readonly ILogger Logger = LogManager.GetLogger<DismembermentPresentationHandler>();

    private readonly INetworkAgentRegistry agentRegistry;
    private readonly Lazy<IBattleNetwork> network;
    private readonly IMessageBroker messageBroker;
    private readonly IWorkshopCapabilityRegistry capabilities;
    private readonly GameInterface.Services.Entity.IControllerIdProvider controllerIdProvider;
    private readonly DismembermentPresentationLedger ledger = new();
    private readonly DismembermentSourceSequenceLedger sourceSequences = new();

    public DismembermentPresentationHandler(
        INetworkAgentRegistry agentRegistry,
        Lazy<IBattleNetwork> network,
        IMessageBroker messageBroker,
        IWorkshopCapabilityRegistry capabilities,
        GameInterface.Services.Entity.IControllerIdProvider controllerIdProvider)
    {
        this.agentRegistry = agentRegistry;
        this.network = network;
        this.messageBroker = messageBroker;
        this.capabilities = capabilities;
        this.controllerIdProvider = controllerIdProvider;
        messageBroker.Subscribe<DismembermentPresentationEvent>(HandleNetworkPresentation);
    }

    public void Dispose() =>
        messageBroker.Unsubscribe<DismembermentPresentationEvent>(HandleNetworkPresentation);

    public void ProcessAcceptedBlow(
        object missionLogic,
        Agent attacker,
        Agent victim,
        Blow blow,
        AttackCollisionData collisionData)
    {
        string instanceId = BattleSpawnGate.ActiveMapEventId;
        string sourceControllerId = controllerIdProvider.ControllerId;
        bool routeEnabled = capabilities.IsEnabled(ModuleId, Operation);
        bool victimAuthority = victim.IsLocallyControlled();
        if (!CombatModAuthorityPolicy.AllowDismembermentAcceptedBlow(
                IsCompatible(),
                ModInformation.IsServer,
                BattleSpawnGate.IsCoopBattleActive,
                routeEnabled,
                victimAuthority) ||
            string.IsNullOrEmpty(instanceId) ||
            string.IsNullOrEmpty(sourceControllerId) ||
            !agentRegistry.TryGetAgentInfo(victim, out CoopAgentInfo victimInfo) ||
            !agentRegistry.TryGetAgentInfo(attacker, out CoopAgentInfo attackerInfo) ||
            victimInfo.CurrentAuthority != sourceControllerId)
        {
            return;
        }

        long sequence = sourceSequences.Next(instanceId, victimInfo.AgentId);
        int collisionBoneIndex = collisionData.CollisionBoneIndex;
        int visualSeed = DismembermentEventPolicy.CreateVisualSeed(
            instanceId,
            sourceControllerId,
            victimInfo.AgentId,
            attackerInfo.AgentId,
            sequence,
            collisionBoneIndex);
        Guid eventId = DismembermentEventPolicy.CreateEventId(
            instanceId,
            sourceControllerId,
            victimInfo.AgentId,
            sequence,
            visualSeed);
        var message = new DismembermentPresentationEvent(
            instanceId,
            sourceControllerId,
            sequence,
            eventId,
            victimInfo.AgentId,
            attackerInfo.AgentId,
            collisionBoneIndex,
            visualSeed,
            blow,
            collisionData);

        if (!DismembermentReflectionAdapter.TryCreateCandidate(
                attacker,
                victim,
                blow,
                collisionData,
                eventId,
                visualSeed,
                out object candidate) ||
            !DismembermentReflectionAdapter.IsValid(candidate) ||
            ledger.TryApply(
                message,
                () => DismembermentReflectionAdapter.TryApply(missionLogic, candidate)) !=
                DismembermentApplyResult.Apply)
        {
            return;
        }

        attacker.SetWantsToYell();
        network.Value.SendAll(message);
    }

    private void HandleNetworkPresentation(MessagePayload<DismembermentPresentationEvent> payload)
    {
        DismembermentPresentationEvent message = payload.What;
        if (message.SourceControllerId == controllerIdProvider.ControllerId) return;

        GameThread.RunSafe(
            () => ApplyNetworkPresentation(message),
            context: nameof(HandleNetworkPresentation));
    }

    private void ApplyNetworkPresentation(DismembermentPresentationEvent message)
    {
        if (!capabilities.IsEnabled(ModuleId, Operation) ||
            !IsCompatible() ||
            !agentRegistry.TryGetAgentInfo(message.VictimAgentId, out CoopAgentInfo victimInfo) ||
            !agentRegistry.TryGetAgentInfo(message.AttackerAgentId, out CoopAgentInfo attackerInfo) ||
            DismembermentEventPolicy.Validate(
                message,
                BattleSpawnGate.ActiveMapEventId,
                victimInfo.CurrentAuthority,
                victimInfo.AgentId) != DismembermentEventValidation.Valid)
        {
            return;
        }

        Mission mission = Mission.Current;
        Agent victim = victimInfo.Agent;
        Agent attacker = attackerInfo.Agent;
        if (mission == null || victim == null || attacker == null || victim.Mission != mission) return;

        object missionLogic = mission.MissionBehaviors?
            .FirstOrDefault(behavior =>
                behavior?.GetType().FullName == DismembermentReflectionAdapter.MissionLogicTypeName);
        if (missionLogic == null ||
            !DismembermentReflectionAdapter.TryCreateCandidate(
                attacker,
                victim,
                message.Blow,
                message.CollisionData,
                message.EventId,
                message.VisualSeed,
                out object candidate))
        {
            Logger.Warning("[WorkshopCombat] Rejected DismembermentPlus presentation {EventId}", message.EventId);
            return;
        }

        DismembermentApplyResult result = ledger.TryApply(
            message,
            () => DismembermentReflectionAdapter.TryApply(missionLogic, candidate));
        if (result == DismembermentApplyResult.PresentationUnavailable)
            Logger.Warning("[WorkshopCombat] Could not apply DismembermentPlus presentation {EventId}", message.EventId);
    }

    private static bool IsCompatible() =>
        CombatModCompatibilityGuard.IsInitialized &&
        CombatModCompatibilityGuard.IsFamilyCompatible(CombatModFamily.DismembermentPlus2088);
}

internal sealed class DismembermentCapabilitySource : IWorkshopCapabilitySource
{
    public IEnumerable<WorkshopCapability> CaptureCapabilities()
    {
        bool enabled = CombatModAuthorityPolicy.AllowDismembermentCapability(
            CombatModCompatibilityGuard.IsInitialized,
            CombatModCompatibilityGuard.IsFamilyCompatible(CombatModFamily.DismembermentPlus2088),
            ModConfigProvider.ModOptions.IsWorkshopModuleEnabled(DismembermentPresentationHandler.ModuleId));
        yield return new WorkshopCapability(
            DismembermentPresentationHandler.ModuleId,
            DismembermentPresentationHandler.Operation,
            enabled,
            enabled ? string.Empty : "The audited DismembermentPlus route is not active on this session.");
    }
}

internal static class DismembermentReflectionAdapter
{
    internal const string MissionLogicTypeName = "DismembermentPlus.Logic.DismembermentPlusMissionLogic";
    private const string CandidateTypeName = "DismembermentPlus.Dismemberment.Dismemberment";
    private const string CandidateInterfaceName = "DismembermentPlus.Dismemberment.IDismemberment";
    private const string ValidatorTypeName = "DismembermentPlus.Validators.DismembermentValidator";

    private static readonly object gate = new();
    private static Type candidateType;
    private static Type candidateInterface;
    private static MethodInfo validator;
    private static MethodInfo apply;

    internal static bool TryCreateCandidate(
        Agent attacker,
        Agent victim,
        Blow blow,
        AttackCollisionData collisionData,
        Guid eventId,
        int visualSeed,
        out object candidate)
    {
        candidate = null;
        if (attacker == null || victim == null || eventId == Guid.Empty || !TryResolve()) return false;

        try
        {
            candidate = Activator.CreateInstance(candidateType);
            Set(candidate, "Attacker", attacker);
            Set(candidate, "Blow", blow);
            Set(candidate, "BlowId", eventId);
            Set(candidate, "CollisionData", collisionData);
            Set(candidate, "HitChance", DismembermentEventPolicy.ToChance(visualSeed));
            Set(candidate, "Id", eventId);
            Set(candidate, "InitialHitTimestamp", Mission.Current?.CurrentTime ?? 0f);
            PropertyInfo state = candidateType.GetProperty("State", BindingFlags.Instance | BindingFlags.Public);
            state.SetValue(candidate, Enum.ToObject(state.PropertyType, 0));
            Set(candidate, "Victim", victim);
            return true;
        }
        catch
        {
            candidate = null;
            return false;
        }
    }

    internal static bool IsValid(object candidate)
    {
        if (candidate == null || !TryResolve() || !candidateInterface.IsInstanceOfType(candidate)) return false;
        try { return validator.Invoke(null, new[] { candidate }) is true; }
        catch { return false; }
    }

    internal static bool TryApply(object missionLogic, object candidate)
    {
        if (missionLogic == null || candidate == null || !TryResolve() ||
            missionLogic.GetType().FullName != MissionLogicTypeName ||
            !candidateInterface.IsInstanceOfType(candidate))
        {
            return false;
        }

        try
        {
            apply.Invoke(missionLogic, new[] { candidate });
            return true;
        }
        catch { return false; }
    }

    private static bool TryResolve()
    {
        if (candidateType != null) return true;
        lock (gate)
        {
            if (candidateType != null) return true;
            Assembly assembly = CombatModCompatibilityGuard.ResolveOptionalAssembly("DismembermentPlus");
            Type resolvedCandidate = assembly?.GetType(CandidateTypeName, false);
            Type resolvedInterface = assembly?.GetType(CandidateInterfaceName, false);
            Type resolvedLogic = assembly?.GetType(MissionLogicTypeName, false);
            Type resolvedValidator = assembly?.GetType(ValidatorTypeName, false);
            MethodInfo resolvedApply = resolvedLogic?.GetMethod(
                "Dismember",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { resolvedInterface },
                null);
            MethodInfo resolvedValidation = resolvedValidator?.GetMethod(
                "IsValidDismemberment",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { resolvedInterface },
                null);
            if (resolvedCandidate == null || resolvedInterface == null || resolvedApply == null ||
                resolvedValidation?.ReturnType != typeof(bool))
            {
                return false;
            }

            candidateType = resolvedCandidate;
            candidateInterface = resolvedInterface;
            apply = resolvedApply;
            validator = resolvedValidation;
            return true;
        }
    }

    private static void Set(object target, string propertyName, object value)
    {
        PropertyInfo property = candidateType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.CanWrite != true) throw new MissingMemberException(candidateType.FullName, propertyName);
        property.SetValue(target, value);
    }
}
