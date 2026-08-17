using Common;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using LiteNetLib;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.Core;

namespace GameInterface.Services.WorkshopMods.RebellionsAndDemographics;

/// <summary>
/// The exact upstream package remains active, but its unqualified lifecycle is replaced before a
/// campaign starts.  Only audited automatic campaign behaviors are constructed, only on the host.
/// This keeps upstream algorithms and save contracts while preventing a rendered client from ever
/// becoming a second campaign writer.
/// </summary>
internal sealed class RebellionsAndDemographicsCompatibilityHandler : IHandler
{
    internal const string SnapshotRouteId = "workshop.rebellions-demographics.snapshot";
    internal const string InterventionRouteId = "workshop.rebellions-demographics.intervention";
    internal const string ChoiceRouteId = "workshop.rebellions-demographics.choice";
    private static readonly string[] ServerBehaviorTypes =
    {
        "RebellionsAndDemographics.PopulationBehavior",
        "RebellionsAndDemographics.PlagueBehavior",
        "RebellionsAndDemographics.RebellionCoreBehavior",
        "RebellionsAndDemographics.RecruitmentLimiterBehavior",
        "RebellionsAndDemographics.DemographicsBehavior",
    };

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IModConfigAuthority configAuthority;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IWorkshopCapabilityRegistry capabilityRegistry;
    private readonly IAuthorityRequestRouter authorityRequestRouter;
    private readonly Harmony harmony = new(RebellionsAndDemographicsHarmonyIsolation.AdapterOwner);
    private readonly IAuthorityRouteHandle<RdSnapshotIntent, NetworkRebellionsAndDemographicsStateQueryResult> snapshotRoute;
    private readonly IAuthorityRouteHandle<RdInterventionIntent, NetworkRebellionsAndDemographicsInterventionResult> interventionRoute;
    private readonly IAuthorityRouteHandle<RdChoiceIntent, NetworkRebellionsAndDemographicsChoiceResult> choiceRoute;

    private Assembly assembly;
    private Type rebellionCoreType;
    private bool compatible;
    private bool campaignStarted;
    private long revision;
    private string lastStateFingerprint = string.Empty;
    private readonly Dictionary<long, RdInterventionWatermark> interventionWatermarks = new();
    private readonly Dictionary<string, RdPromptLease> promptLeases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RdPromptTombstone> promptTombstones = new(StringComparer.Ordinal);
    internal WorkshopSnapshotReadiness SnapshotReadiness { get; private set; }
    internal string SnapshotSessionId { get; private set; }
    internal long SnapshotRevision { get; private set; } = -1;
    internal string SnapshotFingerprint { get; private set; } = string.Empty;
    internal RebellionsAndDemographicsState CurrentState { get; private set; }
    internal bool IsInstalledAndIsolated => compatible;
    internal bool IsCampaignReady => campaignStarted;

    public RebellionsAndDemographicsCompatibilityHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IModConfigAuthority configAuthority,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IWorkshopCapabilityRegistry capabilityRegistry,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.configAuthority = configAuthority;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.capabilityRegistry = capabilityRegistry;
        this.authorityRequestRouter = authorityRequestRouter;
        compatible = TryInstall();
        if (compatible) RebellionsAndDemographicsRuntime.Current = this;
        else AppDomain.CurrentDomain.AssemblyLoad += HandleAssemblyLoad;

        snapshotRoute = authorityRequestRouter.Register(
            AuthorityRoute<RdSnapshotIntent, NetworkRequestRebellionsAndDemographicsState,
                NetworkRebellionsAndDemographicsStateQueryResult>.Define(
                SnapshotRouteId, AuthorityRouteKind.BootstrapQuery,
                CreateHeader,
                (_, header) => new NetworkRequestRebellionsAndDemographicsState(header),
                request => request.Header,
                result => result.Header,
                request => request.Header.TryValidate(out _) ? null : "invalid-rd-snapshot-query",
                request => "rd-snapshot:" + request.Header.SessionId + ":" + request.Header.ExpectedRevision,
                ValidateHeader,
                ExecuteSnapshot,
                CreateSnapshotTerminal,
                ProbeSnapshotApplied,
                _ => { },
                _ => { },
                configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.BootstrapQuery,
                requireAuthenticatedPlayer: false));

        interventionRoute = authorityRequestRouter.Register(
            AuthorityRoute<RdInterventionIntent, NetworkRequestRebellionsAndDemographicsIntervention,
                NetworkRebellionsAndDemographicsInterventionResult>.Define(
                InterventionRouteId, AuthorityRouteKind.Command,
                CreateHeader,
                (intent, header) => new NetworkRequestRebellionsAndDemographicsIntervention(header, intent.TargetHeroId, intent.AllyCount, intent.ExpectedStateRevision),
                request => request.Header,
                result => result.Header,
                request => request.IsValid ? null : "invalid-rd-intervention",
                request => "rd-intervention:" + request.TargetHeroId + ":" + request.AllyCount + ":" + request.ExpectedStateRevision,
                ValidateHeader,
                ExecuteIntervention,
                CreateInterventionTerminal,
                ProbeInterventionApplied,
                _ => snapshotRoute.Submit(default),
                _ => { },
                configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true,
                isExpectedClientResult: (request, result) => request.AuthorityRequestId == result.AuthorityRequestId &&
                    string.Equals(request.SessionId, result.SessionId, StringComparison.Ordinal)));

        choiceRoute = authorityRequestRouter.Register(
            AuthorityRoute<RdChoiceIntent, NetworkRequestRebellionsAndDemographicsChoice,
                NetworkRebellionsAndDemographicsChoiceResult>.Define(
                ChoiceRouteId, AuthorityRouteKind.Command,
                CreateHeader,
                (intent, header) => new NetworkRequestRebellionsAndDemographicsChoice(
                    header, intent.LeaseId, intent.Kind, intent.Accept, intent.ExpectedStateRevision),
                request => request.Header,
                result => result.Header,
                request => request.IsValid ? null : "invalid-rd-choice",
                request => "rd-choice:" + request.LeaseId + ":" + (int)request.Kind + ":" + request.Accept + ":" + request.ExpectedStateRevision,
                ValidateHeader,
                ExecuteChoice,
                CreateChoiceTerminal,
                ProbeChoiceApplied,
                _ => snapshotRoute.Submit(default),
                _ => { },
                configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true,
                isExpectedClientResult: (request, result) => request.AuthorityRequestId == result.AuthorityRequestId &&
                    string.Equals(request.SessionId, result.SessionId, StringComparison.Ordinal) && request.Kind == result.Kind &&
                    request.Accept == result.Accept && string.Equals(request.LeaseId, result.LeaseId, StringComparison.Ordinal)));

        messageBroker.Subscribe<CampaignReady>(HandleCampaignReady);
        messageBroker.Subscribe<HostModConfigAccepted>(HandleHostModConfigAccepted);
        messageBroker.Subscribe<NetworkRebellionsAndDemographicsState>(HandleState);
        messageBroker.Subscribe<NetworkRebellionsAndDemographicsStateQueryResult>(HandleStateQueryResult);
        messageBroker.Subscribe<NetworkRebellionsAndDemographicsPrompt>(HandlePrompt);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<CampaignReady>(HandleCampaignReady);
        messageBroker.Unsubscribe<HostModConfigAccepted>(HandleHostModConfigAccepted);
        messageBroker.Unsubscribe<NetworkRebellionsAndDemographicsState>(HandleState);
        messageBroker.Unsubscribe<NetworkRebellionsAndDemographicsStateQueryResult>(HandleStateQueryResult);
        messageBroker.Unsubscribe<NetworkRebellionsAndDemographicsPrompt>(HandlePrompt);
        snapshotRoute.Dispose();
        interventionRoute.Dispose();
        choiceRoute.Dispose();
        AppDomain.CurrentDomain.AssemblyLoad -= HandleAssemblyLoad;
        if (ReferenceEquals(RebellionsAndDemographicsRuntime.Current, this))
            RebellionsAndDemographicsRuntime.Current = null;
    }

    private void HandleAssemblyLoad(object sender, AssemblyLoadEventArgs eventArgs)
    {
        if (compatible || !string.Equals(eventArgs.LoadedAssembly.GetName().Name, RebellionsAndDemographicsModule.AssemblyName,
                StringComparison.Ordinal)) return;
        compatible = TryInstall();
        if (!compatible) return;
        RebellionsAndDemographicsRuntime.Current = this;
        AppDomain.CurrentDomain.AssemblyLoad -= HandleAssemblyLoad;
    }

    internal void StartAuthoritativeCampaign(object starterObject)
    {
        if (!compatible || campaignStarted) return;
        if (starterObject is not CampaignGameStarter starter)
            throw new InvalidOperationException("R&D adapter received a non-campaign game starter.");
        if (!ModInformation.IsServer)
        {
            // This is presentation only: the original dialog behavior is never allowed to own a
            // local campaign callback.  Its replacement submits the typed host command.
            starter.AddBehavior(new RebellionsAndDemographicsClientPresentationBehavior());
            campaignStarted = true;
            return;
        }
        // Construct all objects before registration. A constructor/API failure cannot leave a
        // partially registered upstream behavior set running in the authoritative campaign.
        var behaviors = new List<CampaignBehaviorBase>(ServerBehaviorTypes.Length);
        foreach (string typeName in ServerBehaviorTypes)
        {
            try
            {
                var behavior = Activator.CreateInstance(assembly.GetType(typeName, true, false)) as CampaignBehaviorBase;
                if (behavior == null) throw new InvalidOperationException("does not implement CampaignBehaviorBase");
                behaviors.Add(behavior);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("R&D adapter could not construct audited behavior '" + typeName + "'.", exception);
            }
        }

        foreach (CampaignBehaviorBase behavior in behaviors) starter.AddBehavior(behavior);
        starter.AddBehavior(new RebellionsAndDemographicsStateObserverBehavior(this));
        campaignStarted = true;
        revision++;
        lastStateFingerprint = CaptureState(string.Empty).Fingerprint;
    }

    private bool TryInstall()
    {
        assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
            string.Equals(candidate.GetName().Name, RebellionsAndDemographicsModule.AssemblyName, StringComparison.Ordinal));
        if (assembly == null) return false;
        if (!string.Equals(new RebellionsAndDemographicsModule().ResolveInstalledSha256(),
                RebellionsAndDemographicsModule.SupportedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("R&D adapter refused an unpinned assembly payload.");

        Type subModule = assembly.GetType("RebellionsAndDemographics.SubModule", true, false);
        rebellionCoreType = assembly.GetType("RebellionsAndDemographics.RebellionCoreBehavior", true, false);
        foreach (string typeName in ServerBehaviorTypes)
        {
            Type type = assembly.GetType(typeName, true, false);
            if (!typeof(CampaignBehaviorBase).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
                throw new InvalidOperationException("R&D adapter preflight rejected behavior '" + typeName + "'.");
        }

        MethodInfo onSubModuleLoad = FindLifecycleMethod(subModule, "OnSubModuleLoad", 0);
        MethodInfo onGameStart = FindLifecycleMethod(subModule, "OnGameStart", 2);
        MethodInfo onMissionInitialize = FindLifecycleMethod(subModule, "OnMissionBehaviorInitialize", 1);
        RebellionsAndDemographicsHarmonyIsolation.Purge(assembly, harmony);
        RebellionsAndDemographicsHarmonyIsolation.InstallSaveDefinitionCompatibility(harmony);
        harmony.Patch(onSubModuleLoad, postfix: new HarmonyMethod(AccessTools.Method(
            typeof(RebellionsAndDemographicsRuntime), nameof(RebellionsAndDemographicsRuntime.PurgeUpstreamAfterSubModuleLoad))));
        harmony.Patch(onGameStart, prefix: new HarmonyMethod(AccessTools.Method(
            typeof(RebellionsAndDemographicsRuntime), nameof(RebellionsAndDemographicsRuntime.GuardOnGameStart))));
        harmony.Patch(onMissionInitialize, prefix: new HarmonyMethod(AccessTools.Method(
            typeof(RebellionsAndDemographicsRuntime), nameof(RebellionsAndDemographicsRuntime.GuardOnMissionBehaviorInitialize))));
        harmony.Patch(rebellionCoreType.GetMethod("TryStartRebellion", BindingFlags.Instance | BindingFlags.NonPublic),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(RebellionsAndDemographicsRuntime), nameof(RebellionsAndDemographicsRuntime.InterceptTryStartRebellion))));
        Type population = assembly.GetType("RebellionsAndDemographics.PopulationBehavior", true, false);
        harmony.Patch(population.GetMethod("OnAppTick", BindingFlags.Instance | BindingFlags.NonPublic),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(RebellionsAndDemographicsRuntime), nameof(RebellionsAndDemographicsRuntime.GuardPopulationAppTick))));
        harmony.Patch(rebellionCoreType.GetMethod("ProcessRebelDefeat", BindingFlags.Instance | BindingFlags.NonPublic),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(RebellionsAndDemographicsRuntime), nameof(RebellionsAndDemographicsRuntime.IssueDefeatChoice))));
        harmony.Patch(rebellionCoreType.GetMethod("TriggerPlayerUltimatum", BindingFlags.Instance | BindingFlags.NonPublic),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(RebellionsAndDemographicsRuntime), nameof(RebellionsAndDemographicsRuntime.IssueUltimatumChoice))));
        return true;
    }

    internal void PurgeUpstreamAfterSubModuleLoad() => RebellionsAndDemographicsHarmonyIsolation.Purge(assembly, harmony);

    private static MethodInfo FindLifecycleMethod(Type type, string name, int parameters) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == name && method.GetParameters().Length == parameters) ??
        throw new InvalidOperationException("R&D adapter could not find lifecycle method " + type.FullName + "." + name + ".");

    private AuthorityRequestHeader CreateHeader(long requestId) =>
        configAuthority.TryGetCurrent(out var config)
            ? new AuthorityRequestHeader(config.ProtocolVersion, config.SessionId, requestId, config.Revision)
            : default;

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out var config))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        return header.ProtocolVersion == config.ProtocolVersion &&
            string.Equals(header.SessionId, config.SessionId, StringComparison.Ordinal)
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-config-session");
    }

    private AuthorityServerReply<NetworkRebellionsAndDemographicsStateQueryResult> ExecuteSnapshot(
        AuthorityServerContext context, NetworkRequestRebellionsAndDemographicsState request)
    {
        if (!compatible || !campaignStarted || !capabilityRegistry.IsEnabled(
                RebellionsAndDemographicsCapabilitySource.ModuleId,
                RebellionsAndDemographicsCapabilitySource.Operation))
            return new(new NetworkRebellionsAndDemographicsStateQueryResult(context.Header,
                AuthorityResultStatus.Unavailable, null, "rd-authority-unavailable"), false);
        return new(new NetworkRebellionsAndDemographicsStateQueryResult(context.Header,
            AuthorityResultStatus.Accepted, CaptureState(context.Header.SessionId), null), true);
    }

    internal bool TryRequestPlayerIntervention(Hero target, int allyCount)
    {
        if (!ModInformation.IsClient || !capabilityRegistry.IsEnabled(
                RebellionsAndDemographicsCapabilitySource.ModuleId,
                RebellionsAndDemographicsCapabilitySource.Operation) || allyCount < 1 || allyCount > 5 ||
            target == null || !objectManager.TryGetId(target, out string targetHeroId))
            return false;
        interventionRoute.Submit(new RdInterventionIntent(targetHeroId, allyCount, SnapshotRevision));
        return true;
    }

    private AuthorityServerReply<NetworkRebellionsAndDemographicsInterventionResult> ExecuteIntervention(
        AuthorityServerContext context, NetworkRequestRebellionsAndDemographicsIntervention request)
    {
        if (!compatible || !campaignStarted || !capabilityRegistry.IsEnabled(
                RebellionsAndDemographicsCapabilitySource.ModuleId,
                RebellionsAndDemographicsCapabilitySource.Operation))
            return Intervention(context.Header, AuthorityResultStatus.Unavailable, request.TargetHeroId, request.AllyCount, revision, null, 0, 0, "rd-authority-unavailable");
        if (request.ExpectedStateRevision != revision)
        {
            network.Send(context.Peer, new NetworkRebellionsAndDemographicsState(CaptureState(context.Header.SessionId)));
            return Intervention(context.Header, AuthorityResultStatus.StaleState, request.TargetHeroId, request.AllyCount, revision, null, 0, 0, "stale-rd-state");
        }
        if (!objectManager.TryGetObject<Hero>(context.Player.HeroId, out var actor) || actor?.Clan?.Kingdom == null ||
            actor.Clan.Kingdom.Leader != actor)
            return Intervention(context.Header, AuthorityResultStatus.Unauthorized, request.TargetHeroId, request.AllyCount, revision, null, 0, 0, "actor-not-kingdom-leader");
        if (!objectManager.TryGetObject<Hero>(request.TargetHeroId, out var target) || target?.Clan?.Kingdom == null ||
            target.Clan.Leader != target || target.Clan.Kingdom.RulingClan == target.Clan || target.Clan.Kingdom == actor.Clan.Kingdom)
            return Intervention(context.Header, AuthorityResultStatus.Rejected, request.TargetHeroId, request.AllyCount, revision, null, 0, 0, "target-not-foreign-nonruler-leader");

        var actorClan = actor.Clan;
        var previousTargetKingdom = target.Clan.Kingdom;
        object core = rebellionCoreType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        var launch = rebellionCoreType.GetMethod("LaunchRebellion", BindingFlags.Instance | BindingFlags.Public,
            binder: null, types: new[] { typeof(Kingdom), typeof(List<Clan>), typeof(bool) }, modifiers: null);
        MethodInfo bribe = rebellionCoreType.GetMethod("CalculatePlayerBribeCost", new[] { typeof(int) });
        MethodInfo influence = rebellionCoreType.GetMethod("CalculatePlayerInfluenceCost", new[] { typeof(int) });
        MethodInfo rebelPool = rebellionCoreType.GetMethod("GetRebelPoolCount", new[] { typeof(Clan) });
        if (core == null || launch == null || bribe == null || influence == null || rebelPool == null)
            throw new InvalidOperationException("R&D intervention route lost its preflighted RebellionCore contract.");

        long goldCost = (long)bribe.Invoke(core, new object[] { request.AllyCount });
        float influenceCost = (float)influence.Invoke(core, new object[] { request.AllyCount });
        var allies = target.Clan.Kingdom.Clans.Where(clan => clan != target.Clan && clan != target.Clan.Kingdom.RulingClan &&
                clan.Leader != null && !playerManager.Contains(clan) && !playerManager.Contains(clan.Leader))
            .OrderByDescending(clan => clan.GetRelationWithClan(target.Clan)).Take(request.AllyCount - 1).ToList();
        int poolCount = (int)rebelPool.Invoke(core, new object[] { target.Clan });
        if (goldCost < 0 || influenceCost < 0 || allies.Count != request.AllyCount - 1 || actor.Gold < goldCost || actorClan.Influence < influenceCost)
            return Intervention(context.Header, AuthorityResultStatus.Rejected, request.TargetHeroId, request.AllyCount, revision, null, goldCost, influenceCost, "intervention-precondition-failed");

        bool mutationStarted = false;
        try
        {
            // This is the audited original ordering: exact native costs, then the native rebellion transition.
            mutationStarted = true;
            GiveGoldAction.ApplyBetweenCharacters(actor, null, checked((int)goldCost), false);
            ChangeClanInfluenceAction.Apply(actorClan, -influenceCost);
            var rebels = new List<Clan> { target.Clan };
            rebels.AddRange(allies);
            launch.Invoke(core, new object[] { previousTargetKingdom, rebels, true });
            bool applied = target.Clan.Kingdom != previousTargetKingdom;
            Kingdom newKingdom = target.Clan.Kingdom;
            bool committed = applied && newKingdom != null && newKingdom != previousTargetKingdom && newKingdom.RulingClan == target.Clan;
            if (!committed)
                throw new InvalidOperationException("native kingdom transition did not satisfy the authoritative postcondition");

            objectManager.TryGetId(actorClan, out var actorClanId);
            objectManager.TryGetId(target.Clan, out var targetClanId);
            RememberIntervention(new RdInterventionWatermark(context.Header.RequestId, context.Player.HeroId, actorClanId,
                request.TargetHeroId, targetClanId, previousTargetKingdom.StringId, newKingdom.StringId, actor.Gold,
                actorClan.Influence, request.AllyCount));
            revision++;
            network.SendAll(new NetworkRebellionsAndDemographicsState(CaptureState(context.Header.SessionId)));
            return Intervention(context.Header, AuthorityResultStatus.Accepted, request.TargetHeroId, request.AllyCount, revision,
                newKingdom.StringId, goldCost, influenceCost, null);
        }
        catch (Exception exception)
        {
            if (!mutationStarted) throw;
            compatible = false;
            DisconnectAllCampaignPeers("intervention mutation/publication ambiguity: " +
                (exception is TargetInvocationException invocation ? invocation.InnerException?.Message ?? invocation.Message : exception.Message));
            return new AuthorityServerReply<NetworkRebellionsAndDemographicsInterventionResult>(
                new NetworkRebellionsAndDemographicsInterventionResult(context.Header, AuthorityResultStatus.ExecutionFailed,
                    request.TargetHeroId, request.AllyCount, revision, null, goldCost, influenceCost, "campaign-isolated"),
                statePublished: false, suppressReply: true);
        }
    }

    private static AuthorityServerReply<NetworkRebellionsAndDemographicsInterventionResult> Intervention(
        AuthorityRequestHeader header, AuthorityResultStatus status, string targetHeroId, int allyCount, long committedRevision,
        string newKingdomId, long goldCost, float influenceCost, string reason) =>
        new(new NetworkRebellionsAndDemographicsInterventionResult(header, status, targetHeroId, allyCount, committedRevision,
            newKingdomId, goldCost, influenceCost, reason),
            status == AuthorityResultStatus.Accepted);

    private static NetworkRebellionsAndDemographicsInterventionResult CreateInterventionTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(header, status, string.Empty, 0, header.ExpectedRevision, null, 0, 0, reason);

    private AuthorityCommitProbeResult ProbeInterventionApplied(NetworkRebellionsAndDemographicsInterventionResult result)
    {
        if (result?.Header.Status != AuthorityResultStatus.Accepted) return AuthorityCommitProbeResult.Invalid;
        var watermark = CurrentState?.InterventionWatermarks?.SingleOrDefault(value => value.AuthorityRequestId == result.Header.RequestId);
        if (SnapshotReadiness != WorkshopSnapshotReadiness.Ready || SnapshotRevision < result.Header.CommittedRevision || watermark == null)
            return AuthorityCommitProbeResult.Pending;
        if (watermark.AuthorityRequestId != result.Header.RequestId || watermark.AllyCount != result.AllyCount ||
            !string.Equals(watermark.TargetHeroId, result.TargetHeroId, StringComparison.Ordinal) ||
            !string.Equals(watermark.NewKingdomId, result.NewKingdomId, StringComparison.Ordinal) ||
            !objectManager.TryGetId(Hero.MainHero, out var localHeroId) || !string.Equals(localHeroId, watermark.ActorHeroId, StringComparison.Ordinal) ||
            Hero.MainHero.Gold != watermark.PostActorGold || Hero.MainHero.Clan == null ||
            Math.Abs(Hero.MainHero.Clan.Influence - watermark.PostActorInfluence) > 0.01f ||
            !objectManager.TryGetObject<Hero>(result.TargetHeroId, out var target) || target?.Clan?.Kingdom == null ||
            target.Clan.Kingdom.RulingClan != target.Clan || !string.Equals(target.Clan.Kingdom.StringId, result.NewKingdomId, StringComparison.Ordinal))
            return AuthorityCommitProbeResult.Invalid;
        return AuthorityCommitProbeResult.Applied;
    }

    private static NetworkRebellionsAndDemographicsStateQueryResult CreateSnapshotTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(header, status, null, reason);

    private AuthorityCommitProbeResult ProbeSnapshotApplied(NetworkRebellionsAndDemographicsStateQueryResult result) =>
        result?.State != null && SnapshotReadiness == WorkshopSnapshotReadiness.Ready &&
        string.Equals(SnapshotSessionId, result.State.SessionId, StringComparison.Ordinal) &&
        SnapshotRevision >= result.State.Revision
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;

    private void HandleCampaignReady(MessagePayload<CampaignReady> _)
    {
        if (!ModInformation.IsServer || !compatible || !campaignStarted || !configAuthority.TryGetCurrent(out var config)) return;
        network.SendAll(new NetworkRebellionsAndDemographicsState(CaptureState(config.SessionId)));
    }

    private void HandleHostModConfigAccepted(MessagePayload<HostModConfigAccepted> payload)
    {
        if (!ModInformation.IsClient || !configAuthority.IsCurrent(payload?.What.Snapshot)) return;

        // The same accepted snapshot is republished several times during one join: the module
        // validation barrier, CampaignReady, and the mod-config refresh acceptance each publish it.
        // Only a genuine session change invalidates an applied snapshot. Resetting readiness on
        // every republish knocked an already-applied bootstrap replica back to Loading, so the
        // router commit probe never observed Ready, failed the query closed with apply-timeout,
        // and disconnected the client back to the main menu.
        if (!string.Equals(SnapshotSessionId, payload.What.Snapshot.SessionId, StringComparison.Ordinal))
        {
            SnapshotReadiness = WorkshopSnapshotReadiness.Loading;
            SnapshotRevision = -1;
            SnapshotFingerprint = string.Empty;
            CurrentState = null;
        }
        snapshotRoute.Submit(default);
    }

    private void HandleState(MessagePayload<NetworkRebellionsAndDemographicsState> payload)
    {
        if (!ModInformation.IsClient || payload?.Who is not NetPeer peer || !configAuthority.IsTrustedServer(peer)) return;
        ApplyState(payload.What.State);
    }

    private void HandleStateQueryResult(MessagePayload<NetworkRebellionsAndDemographicsStateQueryResult> payload)
    {
        if (!ModInformation.IsClient || payload?.Who is not NetPeer peer || !configAuthority.IsTrustedServer(peer) ||
            payload.What.Header.Status != AuthorityResultStatus.Accepted) return;
        ApplyState(payload.What.State);
    }

    internal bool IssueDefeatChoice(Kingdom rebels) => IssuePrompt(RdPromptKind.Defeat, rebels, rebels?.Clans?.ToList());
    internal bool IssueUltimatumChoice(Kingdom kingdom, List<Clan> rebels) => IssuePrompt(RdPromptKind.Ultimatum, kingdom, rebels);

    private bool IssuePrompt(RdPromptKind kind, Kingdom kingdom, List<Clan> clans)
    {
        if (!ModInformation.IsServer) return false;
        if (kingdom == null || clans == null || clans.Count == 0 || !configAuthority.TryGetCurrent(out var config))
        {
            compatible = false;
            DisconnectAllCampaignPeers("prompt issuance precondition unavailable");
            return true;
        }
        Hero owner = kind == RdPromptKind.Ultimatum ? kingdom.Leader : Clan.PlayerClan?.Leader;
        if (owner == null || !objectManager.TryGetId(owner, out var ownerHeroId) || !TryGetPeer(ownerHeroId, out var peer))
        {
            // The original method has not mutated yet.  A missing owner therefore resolves to the
            // same deterministic negative branch instead of retaining a server-local callback.
            try
            {
                ResolveUnownedPrompt(kind, kingdom, clans);
                revision++;
                var unownedState = CaptureState(config.SessionId);
                lastStateFingerprint = unownedState.Fingerprint;
                network.SendAll(new NetworkRebellionsAndDemographicsState(unownedState));
            }
            catch (Exception exception)
            {
                compatible = false;
                DisconnectAllCampaignPeers("unowned prompt mutation/publication ambiguity: " + exception.Message);
            }
            return true;
        }
        var ids = new List<string>();
        foreach (var clan in clans) if (objectManager.TryGetId(clan, out var id)) ids.Add(id);
        if (ids.Count != clans.Count || ids.Any(string.IsNullOrWhiteSpace))
        {
            compatible = false;
            DisconnectAllCampaignPeers("prompt issuance could not resolve every clan identity");
            return true;
        }
        string leaseId = Guid.NewGuid().ToString("N");
        int cost = kind == RdPromptKind.Ultimatum ? clans.Sum(clan => clan.Tier * 50000 + clan.Fiefs.Count * 100000) : 0;
        long issuedRevision = revision + 1;
        promptLeases[leaseId] = new RdPromptLease(leaseId, kind, config.SessionId, ownerHeroId, kingdom.StringId,
            ids, cost, issuedRevision, (float)CampaignTime.Now.ToDays + 1f);
        try
        {
            revision = issuedRevision;
            var state = CaptureState(config.SessionId);
            lastStateFingerprint = state.Fingerprint;
            network.SendAll(new NetworkRebellionsAndDemographicsState(state));
            network.Send(peer, new NetworkRebellionsAndDemographicsPrompt(leaseId, kind, config.SessionId, ownerHeroId,
                kingdom.StringId, ids.ToArray(), cost, issuedRevision, authorityRequestId: 0));
        }
        catch (Exception exception)
        {
            compatible = false;
            DisconnectAllCampaignPeers("prompt issuance/publication ambiguity: " + exception.Message);
        }
        return true;
    }

    private bool TryGetPeer(string heroId, out NetPeer peer)
    {
        peer = null;
        foreach (var player in playerManager.Players)
            if (string.Equals(player.HeroId, heroId, StringComparison.Ordinal) && playerManager.TryGetPeer(player.ControllerId, out peer)) return true;
        return false;
    }

    private void HandlePrompt(MessagePayload<NetworkRebellionsAndDemographicsPrompt> payload)
    {
        if (!ModInformation.IsClient || payload?.Who is not NetPeer peer || !configAuthority.IsTrustedServer(peer) || payload.What == null ||
            !configAuthority.TryGetCurrent(out var config) || !string.Equals(config.SessionId, payload.What.SessionId, StringComparison.Ordinal) ||
            payload.What.Revision != SnapshotRevision || !objectManager.TryGetId(Hero.MainHero, out var localHeroId) ||
            !string.Equals(localHeroId, payload.What.OwnerHeroId, StringComparison.Ordinal) || !MatchesActivePrompt(payload.What)) return;
        var prompt = payload.What;
        InformationManager.ShowInquiry(new InquiryData("Rebellions & Demographics", prompt.Kind == RdPromptKind.Ultimatum
            ? "Rebels demand " + prompt.GoldCost + " denars. Pay them?" : "The crushed rebels demand asylum. Grant it?",
            true, true, prompt.Kind == RdPromptKind.Ultimatum ? "Pay" : "Grant asylum", prompt.Kind == RdPromptKind.Ultimatum ? "Refuse" : "Execute",
            () => SubmitChoice(prompt.LeaseId, prompt.Kind, true), () => SubmitChoice(prompt.LeaseId, prompt.Kind, false)), true, false);
    }

    private bool MatchesActivePrompt(NetworkRebellionsAndDemographicsPrompt prompt) => CurrentState?.ActivePrompts?.Any(state =>
        string.Equals(state.LeaseId, prompt.LeaseId, StringComparison.Ordinal) && state.Kind == prompt.Kind &&
        string.Equals(state.SessionId, prompt.SessionId, StringComparison.Ordinal) && string.Equals(state.OwnerHeroId, prompt.OwnerHeroId, StringComparison.Ordinal) &&
        string.Equals(state.KingdomId, prompt.KingdomId, StringComparison.Ordinal) && state.Revision == prompt.Revision && state.GoldCost == prompt.GoldCost &&
        state.ClanIds.SequenceEqual(prompt.ClanIds ?? Array.Empty<string>(), StringComparer.Ordinal)) == true;

    private void SubmitChoice(string leaseId, RdPromptKind kind, bool accept)
    {
        if (!string.IsNullOrWhiteSpace(leaseId))
            choiceRoute.Submit(new RdChoiceIntent(leaseId, kind, accept, SnapshotRevision));
    }

    private AuthorityServerReply<NetworkRebellionsAndDemographicsChoiceResult> ExecuteChoice(
        AuthorityServerContext context, NetworkRequestRebellionsAndDemographicsChoice request)
    {
        if (!compatible || !campaignStarted)
            return Choice(context.Header, AuthorityResultStatus.Unavailable, request.LeaseId, request.Kind, request.Accept, revision, "rd-authority-unavailable");
        if (request.ExpectedStateRevision != revision)
        {
            network.Send(context.Peer, new NetworkRebellionsAndDemographicsState(CaptureState(context.Header.SessionId)));
            return Choice(context.Header, AuthorityResultStatus.StaleState, request.LeaseId, request.Kind, request.Accept, revision, "stale-rd-state");
        }
        if (!promptLeases.TryGetValue(request.LeaseId, out var lease) || lease.Completed ||
            !string.Equals(lease.SessionId, context.Header.SessionId, StringComparison.Ordinal) || lease.Kind != request.Kind ||
            !string.Equals(context.Player.HeroId, lease.OwnerHeroId, StringComparison.Ordinal) ||
            CampaignTime.Now.ToDays > lease.ExpiresAtDays ||
            !objectManager.TryGetObject<Hero>(lease.OwnerHeroId, out var owner))
            return Choice(context.Header, AuthorityResultStatus.Rejected, request.LeaseId, request.Kind, request.Accept, revision, "invalid-or-expired-rd-lease");

        bool mutationStarted = false;
        try
        {
            // A tombstone records the router correlation before an Accepted result is possible.
            // This means a replay cannot re-enter a native callback even if delivery races the result.
            mutationStarted = true;
            lease.Completed = true;
            lease.AuthorityRequestId = context.Header.RequestId;
            promptLeases.Remove(lease.LeaseId);
            RememberTombstone(new RdPromptTombstone(lease, context.Header.RequestId, request.Accept, false));
            if (!ResolveLease(lease, owner, request.Accept))
                throw new InvalidOperationException("R&D prompt postcondition failed");
            revision++;
            RememberTombstone(new RdPromptTombstone(lease, context.Header.RequestId, request.Accept, true));
            var state = CaptureState(context.Header.SessionId);
            lastStateFingerprint = state.Fingerprint;
            network.SendAll(new NetworkRebellionsAndDemographicsState(state));
            return Choice(context.Header, AuthorityResultStatus.Accepted, request.LeaseId, request.Kind, request.Accept, revision, null);
        }
        catch (Exception exception)
        {
            if (!mutationStarted) throw;
            compatible = false;
            DisconnectAllCampaignPeers("prompt lease mutation/publication ambiguity: " +
                (exception is TargetInvocationException invocation ? invocation.InnerException?.Message ?? invocation.Message : exception.Message));
            return new AuthorityServerReply<NetworkRebellionsAndDemographicsChoiceResult>(
                new NetworkRebellionsAndDemographicsChoiceResult(context.Header, AuthorityResultStatus.ExecutionFailed,
                    request.LeaseId, request.Kind, request.Accept, revision, "campaign-isolated"), statePublished: false, suppressReply: true);
        }
    }

    private bool ResolveLease(RdPromptLease lease, Hero owner, bool accept)
    {
        var clans = lease.ClanIds.Select(id => objectManager.TryGetObject<Clan>(id, out var clan) ? clan : null).Where(clan => clan != null).ToList();
        if (clans.Count != lease.ClanIds.Count || !objectManager.TryGetObject<Kingdom>(lease.KingdomId, out var kingdom)) return false;
        if (lease.Kind == RdPromptKind.Defeat)
        {
            if (accept && owner.Clan?.Kingdom != null)
            {
                foreach (var clan in clans) ChangeKingdomAction.ApplyByJoinToKingdom(clan, owner.Clan.Kingdom, default, true);
                foreach (Kingdom enemy in Kingdom.All.Where(candidate =>
                    FactionManager.IsAtWarAgainstFaction(candidate, kingdom) && !FactionManager.IsAtWarAgainstFaction(candidate, owner.Clan.Kingdom)).ToList())
                    DeclareWarAction.ApplyByDefault(enemy, owner.Clan.Kingdom);
                DestroyKingdomAction.Apply(kingdom);
                return clans.All(clan => clan.Kingdom == owner.Clan.Kingdom) && kingdom.IsEliminated;
            }
            foreach (var clan in clans.ToList())
            {
                if (clan.Leader != null) KillCharacterAction.ApplyByExecution(clan.Leader, kingdom.Leader, true, false);
                DestroyClanAction.Apply(clan);
            }
            DestroyKingdomAction.Apply(kingdom);
            return kingdom.IsEliminated && clans.All(clan => clan.IsEliminated);
        }
        object core = rebellionCoreType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        if (accept && owner.Gold >= lease.GoldCost)
        {
            int goldBefore = owner.Gold;
            GiveGoldAction.ApplyBetweenCharacters(owner, null, lease.GoldCost, true);
            var peace = rebellionCoreType.GetMethod("ApplyPeaceTreaty", BindingFlags.Instance | BindingFlags.NonPublic);
            if (core == null || peace == null) return false;
            peace.Invoke(core, new object[] { clans });
            var unrest = rebellionCoreType.GetField("_clanUnrest", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(core) as IDictionary;
            var treaties = rebellionCoreType.GetField("_peaceTreaties", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(core) as IDictionary;
            return owner.Gold == goldBefore - lease.GoldCost && unrest != null && treaties != null && clans.All(clan =>
                unrest.Contains(clan.StringId) && (int)unrest[clan.StringId] == 0 && treaties[clan.StringId] is CampaignTime expiry &&
                expiry.ToDays > CampaignTime.Now.ToDays);
        }
        InvokeLaunch(kingdom, clans);
        return clans.All(clan => clan.Kingdom != null && clan.Kingdom != kingdom && clan.Kingdom.StringId.StartsWith("rebel_", StringComparison.Ordinal));
    }

    private void ResolveUnownedPrompt(RdPromptKind kind, Kingdom kingdom, List<Clan> clans)
    {
        if (kind == RdPromptKind.Defeat)
        {
            foreach (var clan in clans.ToList())
            {
                if (clan.Leader != null) KillCharacterAction.ApplyByExecution(clan.Leader, kingdom.Leader, true, false);
                DestroyClanAction.Apply(clan);
            }
            DestroyKingdomAction.Apply(kingdom);
            return;
        }
        InvokeLaunch(kingdom, clans);
    }

    private static AuthorityServerReply<NetworkRebellionsAndDemographicsChoiceResult> Choice(
        AuthorityRequestHeader header, AuthorityResultStatus status, string leaseId, RdPromptKind kind, bool accept, long committedRevision, string reason) =>
        new(new NetworkRebellionsAndDemographicsChoiceResult(header, status, leaseId, kind, accept, committedRevision, reason),
            status == AuthorityResultStatus.Accepted);

    private static NetworkRebellionsAndDemographicsChoiceResult CreateChoiceTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(header, status, string.Empty, 0, false, header.ExpectedRevision, reason);

    private AuthorityCommitProbeResult ProbeChoiceApplied(NetworkRebellionsAndDemographicsChoiceResult result)
    {
        if (result?.Header.Status != AuthorityResultStatus.Accepted) return AuthorityCommitProbeResult.Invalid;
        return CurrentState?.PromptTombstones?.Any(tombstone => tombstone.AuthorityRequestId == result.Header.RequestId &&
            string.Equals(tombstone.LeaseId, result.LeaseId, StringComparison.Ordinal) && tombstone.Accepted == result.Accept && tombstone.Completed) == true &&
            SnapshotRevision == result.Header.CommittedRevision
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private void InvokeLaunch(Kingdom kingdom, List<Clan> clans)
    {
        object core = rebellionCoreType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        MethodInfo launch = rebellionCoreType.GetMethod("LaunchRebellion", BindingFlags.Instance | BindingFlags.Public,
            binder: null, types: new[] { typeof(Kingdom), typeof(List<Clan>), typeof(bool) }, modifiers: null);
        if (core == null || launch == null) throw new InvalidOperationException("R&D native rebellion launch contract is unavailable.");
        launch.Invoke(core, new object[] { kingdom, clans, false });
    }

    private void RememberTombstone(RdPromptTombstone tombstone)
    {
        promptTombstones[tombstone.LeaseId] = tombstone;
        foreach (string stale in promptTombstones.Values.OrderBy(value => value.AuthorityRequestId).ThenBy(value => value.LeaseId, StringComparer.Ordinal)
            .Take(Math.Max(0, promptTombstones.Count - 128)).Select(value => value.LeaseId).ToArray())
            promptTombstones.Remove(stale);
    }

    private void RememberIntervention(RdInterventionWatermark watermark)
    {
        interventionWatermarks[watermark.AuthorityRequestId] = watermark;
        foreach (long stale in interventionWatermarks.Keys.OrderBy(value => value).Take(Math.Max(0, interventionWatermarks.Count - 128)).ToArray())
            interventionWatermarks.Remove(stale);
    }

    private void ApplyState(RebellionsAndDemographicsState state)
    {
        if (state == null || !configAuthority.TryGetCurrent(out var config) ||
            !string.Equals(state.SessionId, config.SessionId, StringComparison.Ordinal) || state.Revision < SnapshotRevision ||
            state.Fingerprint?.Length != 64 || !IsValidState(state))
            return;
        if (state.Revision == SnapshotRevision && !string.Equals(state.Fingerprint, SnapshotFingerprint, StringComparison.Ordinal))
        {
            SnapshotReadiness = WorkshopSnapshotReadiness.Unavailable;
            return;
        }
        SnapshotSessionId = state.SessionId;
        SnapshotRevision = state.Revision;
        SnapshotFingerprint = state.Fingerprint;
        CurrentState = state;
        SnapshotReadiness = WorkshopSnapshotReadiness.Ready;
    }

    private static bool IsValidState(RebellionsAndDemographicsState state) => state.Settlements != null && state.Settlements.Length <= 2048 &&
        state.Settlements.All(settlement => settlement != null && settlement.SettlementId?.Length is > 0 and <= 96 &&
            settlement.TotalPopulation >= 0 && settlement.Manpower >= 0 && settlement.StarvationDays >= 0 &&
            settlement.Cultures != null && settlement.Cultures.Length <= 128 && settlement.Cultures.All(culture => culture != null &&
                culture.CultureId?.Length is > 0 and <= 96 && culture.Population >= 0)) &&
        state.ActivePrompts != null && state.ActivePrompts.Length <= 32 && state.ActivePrompts.All(prompt => prompt != null &&
            prompt.LeaseId?.Length is > 0 and <= 64 && prompt.OwnerHeroId?.Length is > 0 and <= 128 &&
            prompt.SessionId?.Length is > 0 and <= 96 && prompt.ClanIds != null && prompt.ClanIds.Length is > 0 and <= 16 &&
            prompt.ClanIds.All(id => id?.Length is > 0 and <= 128) && prompt.GoldCost >= 0 && prompt.ExpiresAtDays >= 0 &&
            (prompt.Kind == RdPromptKind.Defeat || prompt.Kind == RdPromptKind.Ultimatum)) &&
        state.PromptTombstones != null && state.PromptTombstones.Length <= 128 && state.PromptTombstones.All(tombstone => tombstone != null &&
            tombstone.LeaseId?.Length is > 0 and <= 64 && (tombstone.Kind == RdPromptKind.Defeat || tombstone.Kind == RdPromptKind.Ultimatum)) &&
        state.InterventionWatermarks != null && state.InterventionWatermarks.Length <= 128 && state.InterventionWatermarks.All(watermark => watermark != null &&
            watermark.AuthorityRequestId > 0 && watermark.ActorHeroId?.Length is > 0 and <= 128 && watermark.TargetHeroId?.Length is > 0 and <= 128 &&
            watermark.NewKingdomId?.Length is > 0 and <= 128 && watermark.AllyCount is >= 1 and <= 5);

    private RebellionsAndDemographicsState CaptureState(string sessionId) =>
        new(sessionId, revision, ServerBehaviorTypes, CapturePopulation(), CapturePlague(), promptLeases.Values,
            promptTombstones.Values, interventionWatermarks.Values);

    internal void ObserveAuthoritativeState()
    {
        if (!ModInformation.IsServer || !compatible || !campaignStarted || !configAuthority.TryGetCurrent(out var config)) return;
        bool expired = ExpirePromptLeases();
        var state = CaptureState(config.SessionId);
        if (!expired && string.Equals(lastStateFingerprint, state.Fingerprint, StringComparison.Ordinal)) return;
        revision++;
        state = CaptureState(config.SessionId);
        lastStateFingerprint = state.Fingerprint;
        network.SendAll(new NetworkRebellionsAndDemographicsState(state));
    }

    // Leases are deliberately non-persistent: the Harmony prefix skipped the original inquiry
    // before it mutated anything.  On a reload no callback survives, and the next daily core pass
    // reissues the same unresolved condition.  While a campaign stays live, expiry takes the
    // deterministic negative branch and leaves a tombstone so stale client choices are rejected.
    private bool ExpirePromptLeases()
    {
        var expired = promptLeases.Values.Where(lease => CampaignTime.Now.ToDays > lease.ExpiresAtDays).ToArray();
        if (expired.Length == 0) return false;
        foreach (var lease in expired)
        {
            promptLeases.Remove(lease.LeaseId);
            lease.Completed = true;
            try
            {
                if (!objectManager.TryGetObject<Kingdom>(lease.KingdomId, out var kingdom)) throw new InvalidOperationException("lease kingdom missing");
                var clans = lease.ClanIds.Select(id => objectManager.TryGetObject<Clan>(id, out var clan) ? clan : null)
                    .Where(clan => clan != null).ToList();
                if (clans.Count != lease.ClanIds.Count) throw new InvalidOperationException("lease clan missing");
                ResolveUnownedPrompt(lease.Kind, kingdom, clans);
                RememberTombstone(new RdPromptTombstone(lease, 0, false, true));
            }
            catch (Exception exception)
            {
                compatible = false;
                DisconnectAllCampaignPeers("prompt lease timeout mutation ambiguity: " + exception.Message);
            }
        }
        return true;
    }

    private IReadOnlyList<RdSettlementPopulationState> CapturePopulation()
    {
        object behavior = assembly.GetType("RebellionsAndDemographics.PopulationBehavior", false)?
            .GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        var registry = behavior?.GetType().GetField("_populationRegistry", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(behavior) as IDictionary;
        if (registry == null) return Array.Empty<RdSettlementPopulationState>();
        var records = new List<RdSettlementPopulationState>();
        foreach (DictionaryEntry entry in registry)
        {
            if (entry.Key is not Settlement settlement || entry.Value == null) continue;
            Type data = entry.Value.GetType();
            var cultures = data.GetProperty("CulturalPopulation")?.GetValue(entry.Value) as IDictionary;
            var cultureValues = new List<RdCulturePopulationState>();
            if (cultures != null) foreach (DictionaryEntry culture in cultures)
            {
                string cultureId = culture.Key is CultureObject cultureObject ? cultureObject.StringId : culture.Key as string ?? string.Empty;
                if (cultureId.Length > 0 && cultureId.Length <= 96 && culture.Value is int count && count >= 0)
                    cultureValues.Add(new RdCulturePopulationState(cultureId, count));
            }
            records.Add(new RdSettlementPopulationState(settlement.StringId,
                (int)(data.GetProperty("TotalPopulation")?.GetValue(entry.Value) ?? 0),
                (int)(data.GetProperty("Manpower")?.GetValue(entry.Value) ?? 0),
                (int)(data.GetProperty("StarvationDays")?.GetValue(entry.Value) ?? 0),
                (int)(data.GetProperty("LastDailyMigration")?.GetValue(entry.Value) ?? 0), cultureValues.OrderBy(value => value.CultureId, StringComparer.Ordinal)));
        }
        return records.OrderBy(record => record.SettlementId, StringComparer.Ordinal).ToArray();
    }

    private RdPlagueState CapturePlague()
    {
        object behavior = assembly.GetType("RebellionsAndDemographics.PlagueBehavior", false)?
            .GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        if (behavior == null) return new RdPlagueState(string.Empty, 0, string.Empty);
        Type type = behavior.GetType();
        var city = type.GetField("_activePlagueCity", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(behavior) as Settlement;
        var days = (int)(type.GetField("_activePlagueDaysLeft", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(behavior) ?? 0);
        object next = type.GetField("_nextPlagueDate", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(behavior);
        return new RdPlagueState(city?.StringId ?? string.Empty, days, CanonicalCampaignTime(next));
    }

    internal static string CanonicalCampaignTime(object value) => value is CampaignTime campaignTime
        ? campaignTime.NumTicks.ToString(CultureInfo.InvariantCulture)
        : string.Empty;

    private void DisconnectAllCampaignPeers(string reason)
    {
        foreach (var player in playerManager.Players)
            if (playerManager.TryGetPeer(player.ControllerId, out var peer)) peer.Disconnect();
    }

    internal bool InterceptTryStartRebellion(Kingdom kingdom, List<Clan> pool, bool isForcedDebug)
    {
        FilterAutomaticRebellionPool(pool);
        if (kingdom?.RulingClan == null || !IsPlayerClan(kingdom.RulingClan)) return true;

        // Upstream recognizes only Clan.PlayerClan.  In co-op any connected ruler must receive
        // the same ultimatum before native execution, with all player clans excluded from rebels.
        var candidates = (pool ?? new List<Clan>()).Where(clan => clan != kingdom.RulingClan).ToList();
        var leader = candidates.OrderBy(clan => clan.GetRelationWithClan(kingdom.RulingClan)).FirstOrDefault();
        if (isForcedDebug && leader == null && kingdom.Clans.Count >= 3)
            leader = kingdom.Clans.Where(clan => clan != kingdom.RulingClan && !IsPlayerClan(clan)).FirstOrDefault();
        if (leader == null) return false;
        int packSize = (int)(rebellionCoreType.GetMethod("DetermineRebelPackSize", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(rebellionCoreType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null), null) ?? 2);
        var source = !isForcedDebug || candidates.Count >= packSize ? candidates : kingdom.Clans.Where(clan => !IsPlayerClan(clan)).ToList();
        var rebels = new List<Clan> { leader };
        rebels.AddRange(source.Where(clan => clan != leader && clan != kingdom.RulingClan)
            .OrderByDescending(clan => clan.GetRelationWithClan(leader)).Take(packSize - 1));
        if (rebels.Count >= 2) IssuePrompt(RdPromptKind.Ultimatum, kingdom, rebels);
        return false;
    }

    private bool IsPlayerClan(Clan clan) => clan != null && (playerManager.Contains(clan) ||
        (clan.Leader != null && playerManager.Contains(clan.Leader)));

    internal void FilterAutomaticRebellionPool(List<Clan> pool) => pool?.RemoveAll(IsPlayerClan);

    private readonly struct RdSnapshotIntent { }
    private readonly struct RdInterventionIntent
    {
        internal RdInterventionIntent(string targetHeroId, int allyCount, long expectedStateRevision)
        { TargetHeroId = targetHeroId; AllyCount = allyCount; ExpectedStateRevision = expectedStateRevision; }
        internal string TargetHeroId { get; }
        internal int AllyCount { get; }
        internal long ExpectedStateRevision { get; }
    }
    private readonly struct RdChoiceIntent
    {
        internal RdChoiceIntent(string leaseId, RdPromptKind kind, bool accept, long expectedStateRevision)
        { LeaseId = leaseId; Kind = kind; Accept = accept; ExpectedStateRevision = expectedStateRevision; }
        internal string LeaseId { get; }
        internal RdPromptKind Kind { get; }
        internal bool Accept { get; }
        internal long ExpectedStateRevision { get; }
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class RebellionsAndDemographicsState
{
    [ProtoMember(1)] public string SessionId { get; private set; }
    [ProtoMember(2)] public long Revision { get; private set; }
    [ProtoMember(3)] public string[] ServerBehaviorTypes { get; private set; }
    [ProtoMember(4)] public RdSettlementPopulationState[] Settlements { get; private set; }
    [ProtoMember(5)] public RdPlagueState Plague { get; private set; }
    [ProtoMember(6)] public string Fingerprint { get; private set; }
    [ProtoMember(7)] public RdPromptLeaseState[] ActivePrompts { get; private set; }
    [ProtoMember(8)] public RdPromptTombstone[] PromptTombstones { get; private set; }
    [ProtoMember(9)] public RdInterventionWatermark[] InterventionWatermarks { get; private set; }
    private RebellionsAndDemographicsState() { }
    internal RebellionsAndDemographicsState(string sessionId, long revision, IEnumerable<string> serverBehaviorTypes,
        IReadOnlyList<RdSettlementPopulationState> settlements, RdPlagueState plague,
        IEnumerable<RdPromptLease> activePrompts, IEnumerable<RdPromptTombstone> promptTombstones,
        IEnumerable<RdInterventionWatermark> interventionWatermarks)
    {
        SessionId = sessionId ?? string.Empty;
        Revision = revision;
        ServerBehaviorTypes = serverBehaviorTypes?.ToArray() ?? Array.Empty<string>();
        Settlements = settlements?.ToArray() ?? Array.Empty<RdSettlementPopulationState>();
        Plague = plague ?? new RdPlagueState(string.Empty, 0, string.Empty);
        ActivePrompts = activePrompts?.Select(RdPromptLeaseState.FromLease).OrderBy(prompt => prompt.LeaseId, StringComparer.Ordinal).ToArray()
            ?? Array.Empty<RdPromptLeaseState>();
        PromptTombstones = promptTombstones?.OrderBy(tombstone => tombstone.LeaseId, StringComparer.Ordinal).ToArray()
            ?? Array.Empty<RdPromptTombstone>();
        InterventionWatermarks = interventionWatermarks?.OrderBy(watermark => watermark.AuthorityRequestId).ToArray()
            ?? Array.Empty<RdInterventionWatermark>();
        Fingerprint = ComputeFingerprint(Settlements, Plague, ActivePrompts, PromptTombstones, InterventionWatermarks);
    }

    private static string ComputeFingerprint(IEnumerable<RdSettlementPopulationState> settlements, RdPlagueState plague,
        IEnumerable<RdPromptLeaseState> activePrompts, IEnumerable<RdPromptTombstone> promptTombstones,
        IEnumerable<RdInterventionWatermark> interventionWatermarks)
    {
        string canonical = string.Join("|", settlements.Select(value => value.Fingerprint)) + "|" + plague.Fingerprint + "|" +
            string.Join("|", activePrompts.Select(value => value.Fingerprint)) + "|" + string.Join("|", promptTombstones.Select(value => value.Fingerprint)) +
            "|" + string.Join("|", interventionWatermarks.Select(value => value.Fingerprint));
        using var sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", string.Empty).ToLowerInvariant();
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class RdSettlementPopulationState
{
    [ProtoMember(1)] public string SettlementId { get; private set; }
    [ProtoMember(2)] public int TotalPopulation { get; private set; }
    [ProtoMember(3)] public int Manpower { get; private set; }
    [ProtoMember(4)] public int StarvationDays { get; private set; }
    [ProtoMember(5)] public int LastDailyMigration { get; private set; }
    [ProtoMember(6)] public RdCulturePopulationState[] Cultures { get; private set; }
    private RdSettlementPopulationState() { }
    internal RdSettlementPopulationState(string settlementId, int totalPopulation, int manpower, int starvationDays,
        int lastDailyMigration, IEnumerable<RdCulturePopulationState> cultures)
    {
        SettlementId = settlementId ?? string.Empty; TotalPopulation = totalPopulation; Manpower = manpower;
        StarvationDays = starvationDays; LastDailyMigration = lastDailyMigration; Cultures = cultures?.ToArray() ?? Array.Empty<RdCulturePopulationState>();
    }
    internal string Fingerprint => SettlementId + ":" + TotalPopulation + ":" + Manpower + ":" + StarvationDays + ":" +
        LastDailyMigration + ":" + string.Join(",", Cultures.Select(culture => culture.Fingerprint));
}

[ProtoContract(SkipConstructor = true)]
internal sealed class RdCulturePopulationState
{
    [ProtoMember(1)] public string CultureId { get; private set; }
    [ProtoMember(2)] public int Population { get; private set; }
    private RdCulturePopulationState() { }
    internal RdCulturePopulationState(string cultureId, int population) { CultureId = cultureId; Population = population; }
    internal string Fingerprint => CultureId + "=" + Population;
}

[ProtoContract(SkipConstructor = true)]
internal sealed class RdPlagueState
{
    [ProtoMember(1)] public string SettlementId { get; private set; }
    [ProtoMember(2)] public int DaysLeft { get; private set; }
    [ProtoMember(3)] public string NextTick { get; private set; }
    private RdPlagueState() { }
    internal RdPlagueState(string settlementId, int daysLeft, string nextTick)
    { SettlementId = settlementId ?? string.Empty; DaysLeft = daysLeft; NextTick = nextTick ?? string.Empty; }
    internal string Fingerprint => SettlementId + ":" + DaysLeft + ":" + NextTick;
}

internal enum RdPromptKind { Ultimatum = 1, Defeat = 2 }

internal sealed class RdPromptLease
{
    internal RdPromptLease(string leaseId, RdPromptKind kind, string sessionId, string ownerHeroId, string kingdomId, IReadOnlyList<string> clanIds,
        int goldCost, long revision, float expiresAtDays)
    { LeaseId = leaseId; Kind = kind; SessionId = sessionId; OwnerHeroId = ownerHeroId; KingdomId = kingdomId; ClanIds = clanIds; GoldCost = goldCost; Revision = revision; ExpiresAtDays = expiresAtDays; }
    internal string LeaseId { get; }
    internal RdPromptKind Kind { get; }
    internal string SessionId { get; }
    internal string OwnerHeroId { get; }
    internal string KingdomId { get; }
    internal IReadOnlyList<string> ClanIds { get; }
    internal int GoldCost { get; }
    internal long Revision { get; }
    internal float ExpiresAtDays { get; }
    internal long AuthorityRequestId { get; set; }
    internal bool Completed { get; set; }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class RdPromptLeaseState
{
    [ProtoMember(1)] public string LeaseId { get; private set; }
    [ProtoMember(2)] public RdPromptKind Kind { get; private set; }
    [ProtoMember(3)] public string OwnerHeroId { get; private set; }
    [ProtoMember(4)] public string KingdomId { get; private set; }
    [ProtoMember(5)] public long Revision { get; private set; }
    [ProtoMember(6)] public long AuthorityRequestId { get; private set; }
    [ProtoMember(7)] public string SessionId { get; private set; }
    [ProtoMember(8)] public string[] ClanIds { get; private set; }
    [ProtoMember(9)] public int GoldCost { get; private set; }
    [ProtoMember(10)] public float ExpiresAtDays { get; private set; }
    private RdPromptLeaseState() { }
    private RdPromptLeaseState(RdPromptLease lease)
    { LeaseId = lease.LeaseId; Kind = lease.Kind; OwnerHeroId = lease.OwnerHeroId; KingdomId = lease.KingdomId; Revision = lease.Revision; AuthorityRequestId = lease.AuthorityRequestId;
        SessionId = lease.SessionId; ClanIds = lease.ClanIds?.ToArray() ?? Array.Empty<string>(); GoldCost = lease.GoldCost; ExpiresAtDays = lease.ExpiresAtDays; }
    internal static RdPromptLeaseState FromLease(RdPromptLease lease) => new(lease);
    internal string Fingerprint => LeaseId + ":" + (int)Kind + ":" + OwnerHeroId + ":" + KingdomId + ":" + Revision + ":" + AuthorityRequestId + ":" +
        SessionId + ":" + string.Join(",", ClanIds) + ":" + GoldCost + ":" + ExpiresAtDays;
}

[ProtoContract(SkipConstructor = true)]
internal sealed class RdPromptTombstone
{
    [ProtoMember(1)] public string LeaseId { get; private set; }
    [ProtoMember(2)] public RdPromptKind Kind { get; private set; }
    [ProtoMember(3)] public long AuthorityRequestId { get; private set; }
    [ProtoMember(4)] public bool Accepted { get; private set; }
    [ProtoMember(5)] public bool Completed { get; private set; }
    private RdPromptTombstone() { }
    internal RdPromptTombstone(RdPromptLease lease, long authorityRequestId, bool accepted, bool completed)
    { LeaseId = lease.LeaseId; Kind = lease.Kind; AuthorityRequestId = authorityRequestId; Accepted = accepted; Completed = completed; }
    internal string Fingerprint => LeaseId + ":" + (int)Kind + ":" + AuthorityRequestId + ":" + Accepted + ":" + Completed;
}

[ProtoContract(SkipConstructor = true)]
internal sealed class RdInterventionWatermark
{
    [ProtoMember(1)] public long AuthorityRequestId { get; private set; }
    [ProtoMember(2)] public string ActorHeroId { get; private set; }
    [ProtoMember(3)] public string ActorClanId { get; private set; }
    [ProtoMember(4)] public string TargetHeroId { get; private set; }
    [ProtoMember(5)] public string TargetClanId { get; private set; }
    [ProtoMember(6)] public string OldKingdomId { get; private set; }
    [ProtoMember(7)] public string NewKingdomId { get; private set; }
    [ProtoMember(8)] public int PostActorGold { get; private set; }
    [ProtoMember(9)] public float PostActorInfluence { get; private set; }
    [ProtoMember(10)] public int AllyCount { get; private set; }
    private RdInterventionWatermark() { }
    internal RdInterventionWatermark(long authorityRequestId, string actorHeroId, string actorClanId, string targetHeroId,
        string targetClanId, string oldKingdomId, string newKingdomId, int postActorGold, float postActorInfluence, int allyCount)
    { AuthorityRequestId = authorityRequestId; ActorHeroId = actorHeroId ?? string.Empty; ActorClanId = actorClanId ?? string.Empty;
        TargetHeroId = targetHeroId ?? string.Empty; TargetClanId = targetClanId ?? string.Empty; OldKingdomId = oldKingdomId ?? string.Empty;
        NewKingdomId = newKingdomId ?? string.Empty; PostActorGold = postActorGold; PostActorInfluence = postActorInfluence; AllyCount = allyCount; }
    internal string Fingerprint => AuthorityRequestId + ":" + ActorHeroId + ":" + ActorClanId + ":" + TargetHeroId + ":" +
        TargetClanId + ":" + OldKingdomId + ":" + NewKingdomId + ":" + PostActorGold + ":" + PostActorInfluence + ":" + AllyCount;
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkRebellionsAndDemographicsPrompt : IEvent
{
    [ProtoMember(1)] public string LeaseId { get; private set; }
    [ProtoMember(2)] public RdPromptKind Kind { get; private set; }
    [ProtoMember(3)] public string OwnerHeroId { get; private set; }
    [ProtoMember(4)] public string KingdomId { get; private set; }
    [ProtoMember(5)] public string[] ClanIds { get; private set; }
    [ProtoMember(6)] public int GoldCost { get; private set; }
    [ProtoMember(7)] public long Revision { get; private set; }
    private NetworkRebellionsAndDemographicsPrompt() { }
    [ProtoMember(8)] public string SessionId { get; private set; }
    [ProtoMember(9)] public long AuthorityRequestId { get; private set; }
    internal NetworkRebellionsAndDemographicsPrompt(string leaseId, RdPromptKind kind, string sessionId, string ownerHeroId, string kingdomId,
        string[] clanIds, int goldCost, long revision, long authorityRequestId)
    { LeaseId = leaseId; Kind = kind; SessionId = sessionId; OwnerHeroId = ownerHeroId; KingdomId = kingdomId; ClanIds = clanIds; GoldCost = goldCost; Revision = revision; AuthorityRequestId = authorityRequestId; }
}

[AuthorityRoute(RebellionsAndDemographicsCompatibilityHandler.ChoiceRouteId, AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkRequestRebellionsAndDemographicsChoice : ICommand
{
    [ProtoMember(1)] public int ProtocolVersion { get; private set; }
    [ProtoMember(2)] public string SessionId { get; private set; }
    [ProtoMember(3)] public long AuthorityRequestId { get; private set; }
    [ProtoMember(4)] public long ExpectedRevision { get; private set; }
    [ProtoMember(5)] public string LeaseId { get; private set; }
    [ProtoMember(6)] public RdPromptKind Kind { get; private set; }
    [ProtoMember(7)] public bool Accept { get; private set; }
    [ProtoMember(8)] public long ExpectedStateRevision { get; private set; }
    private NetworkRequestRebellionsAndDemographicsChoice() { }
    internal NetworkRequestRebellionsAndDemographicsChoice(AuthorityRequestHeader header, string leaseId, RdPromptKind kind, bool accept, long expectedStateRevision)
    { ProtocolVersion = header.ProtocolVersion; SessionId = header.SessionId; AuthorityRequestId = header.RequestId; ExpectedRevision = header.ExpectedRevision;
        LeaseId = leaseId ?? string.Empty; Kind = kind; Accept = accept; ExpectedStateRevision = expectedStateRevision; }
    internal bool IsValid => Header.TryValidate(out _) && LeaseId?.Length is > 0 and <= 64 && (Kind == RdPromptKind.Ultimatum || Kind == RdPromptKind.Defeat);
    internal AuthorityRequestHeader Header => new(ProtocolVersion, SessionId, AuthorityRequestId, ExpectedRevision);
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkRebellionsAndDemographicsChoiceResult : IMessage
{
    [ProtoMember(1)] public string SessionId { get; private set; }
    [ProtoMember(2)] public long AuthorityRequestId { get; private set; }
    [ProtoMember(3)] public AuthorityResultStatus Status { get; private set; }
    [ProtoMember(4)] public long CommittedRevision { get; private set; }
    [ProtoMember(5)] public string ReasonCode { get; private set; }
    [ProtoMember(6)] public string LeaseId { get; private set; }
    [ProtoMember(7)] public RdPromptKind Kind { get; private set; }
    [ProtoMember(8)] public bool Accept { get; private set; }
    private NetworkRebellionsAndDemographicsChoiceResult() { }
    internal NetworkRebellionsAndDemographicsChoiceResult(AuthorityRequestHeader header, AuthorityResultStatus status,
        string leaseId, RdPromptKind kind, bool accept, long committedRevision, string reason)
    { SessionId = header.SessionId; AuthorityRequestId = header.RequestId; Status = status; CommittedRevision = committedRevision;
        ReasonCode = reason ?? string.Empty; LeaseId = leaseId ?? string.Empty; Kind = kind; Accept = accept; }
    internal AuthorityResultHeader Header => new(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[AuthorityRoute(RebellionsAndDemographicsCompatibilityHandler.SnapshotRouteId, AuthorityRouteKind.BootstrapQuery)]
[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkRequestRebellionsAndDemographicsState : ICommand
{
    [ProtoMember(1)] public int ProtocolVersion { get; private set; }
    [ProtoMember(2)] public string SessionId { get; private set; }
    [ProtoMember(3)] public long AuthorityRequestId { get; private set; }
    [ProtoMember(4)] public long ExpectedRevision { get; private set; }
    private NetworkRequestRebellionsAndDemographicsState() { }
    internal NetworkRequestRebellionsAndDemographicsState(AuthorityRequestHeader header)
    {
        ProtocolVersion = header.ProtocolVersion; SessionId = header.SessionId;
        AuthorityRequestId = header.RequestId; ExpectedRevision = header.ExpectedRevision;
    }
    internal AuthorityRequestHeader Header => new(ProtocolVersion, SessionId, AuthorityRequestId, ExpectedRevision);
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkRebellionsAndDemographicsStateQueryResult : IMessage
{
    [ProtoMember(1)] public string SessionId { get; private set; }
    [ProtoMember(2)] public long AuthorityRequestId { get; private set; }
    [ProtoMember(3)] public AuthorityResultStatus Status { get; private set; }
    [ProtoMember(4)] public long CommittedRevision { get; private set; }
    [ProtoMember(5)] public string ReasonCode { get; private set; }
    [ProtoMember(6)] public RebellionsAndDemographicsState State { get; private set; }
    private NetworkRebellionsAndDemographicsStateQueryResult() { }
    internal NetworkRebellionsAndDemographicsStateQueryResult(AuthorityRequestHeader header,
        AuthorityResultStatus status, RebellionsAndDemographicsState state, string reason)
    {
        SessionId = header.SessionId; AuthorityRequestId = header.RequestId; Status = status;
        CommittedRevision = state?.Revision ?? header.ExpectedRevision; ReasonCode = reason ?? string.Empty; State = state;
    }
    internal AuthorityResultHeader Header => new(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkRebellionsAndDemographicsState : IEvent
{
    [ProtoMember(1)] public RebellionsAndDemographicsState State { get; private set; }
    private NetworkRebellionsAndDemographicsState() { }
    internal NetworkRebellionsAndDemographicsState(RebellionsAndDemographicsState state) => State = state;
}

[AuthorityRoute(RebellionsAndDemographicsCompatibilityHandler.InterventionRouteId, AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkRequestRebellionsAndDemographicsIntervention : ICommand
{
    [ProtoMember(1)] public int ProtocolVersion { get; private set; }
    [ProtoMember(2)] public string SessionId { get; private set; }
    [ProtoMember(3)] public long AuthorityRequestId { get; private set; }
    [ProtoMember(4)] public long ExpectedRevision { get; private set; }
    [ProtoMember(5)] public string TargetHeroId { get; private set; }
    [ProtoMember(6)] public int AllyCount { get; private set; }
    [ProtoMember(7)] public long ExpectedStateRevision { get; private set; }
    private NetworkRequestRebellionsAndDemographicsIntervention() { }
    internal NetworkRequestRebellionsAndDemographicsIntervention(AuthorityRequestHeader header, string targetHeroId, int allyCount, long expectedStateRevision)
    {
        ProtocolVersion = header.ProtocolVersion; SessionId = header.SessionId;
        AuthorityRequestId = header.RequestId; ExpectedRevision = header.ExpectedRevision;
        TargetHeroId = targetHeroId ?? string.Empty; AllyCount = allyCount; ExpectedStateRevision = expectedStateRevision;
    }
    internal bool IsValid => Header.TryValidate(out _) && !string.IsNullOrWhiteSpace(TargetHeroId) && TargetHeroId.Length <= 128 && AllyCount >= 1 && AllyCount <= 5;
    internal AuthorityRequestHeader Header => new(ProtocolVersion, SessionId, AuthorityRequestId, ExpectedRevision);
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkRebellionsAndDemographicsInterventionResult : IMessage
{
    [ProtoMember(1)] public string SessionId { get; private set; }
    [ProtoMember(2)] public long AuthorityRequestId { get; private set; }
    [ProtoMember(3)] public AuthorityResultStatus Status { get; private set; }
    [ProtoMember(4)] public long CommittedRevision { get; private set; }
    [ProtoMember(5)] public string ReasonCode { get; private set; }
    [ProtoMember(6)] public string TargetHeroId { get; private set; }
    [ProtoMember(7)] public int AllyCount { get; private set; }
    [ProtoMember(8)] public string NewKingdomId { get; private set; }
    [ProtoMember(9)] public long GoldCost { get; private set; }
    [ProtoMember(10)] public float InfluenceCost { get; private set; }
    private NetworkRebellionsAndDemographicsInterventionResult() { }
    internal NetworkRebellionsAndDemographicsInterventionResult(AuthorityRequestHeader header,
        AuthorityResultStatus status, string targetHeroId, int allyCount, long committedRevision,
        string newKingdomId, long goldCost, float influenceCost, string reason)
    {
        SessionId = header.SessionId; AuthorityRequestId = header.RequestId; Status = status;
        CommittedRevision = committedRevision; ReasonCode = reason ?? string.Empty; TargetHeroId = targetHeroId ?? string.Empty;
        AllyCount = allyCount; NewKingdomId = newKingdomId ?? string.Empty; GoldCost = goldCost; InfluenceCost = influenceCost;
    }
    internal AuthorityResultHeader Header => new(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}
