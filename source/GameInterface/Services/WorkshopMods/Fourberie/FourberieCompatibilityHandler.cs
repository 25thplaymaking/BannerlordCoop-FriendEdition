using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Configuration;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Registry.Messages;
using GameInterface.Services.Barters;
using GameInterface.Services.AuthorityRequests;
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
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace GameInterface.Services.WorkshopMods.Fourberie;

/// <summary>
/// Late-bound safety boundary for Fourberie 1.4.7.6. The original assembly stays a separate
/// runtime module, while this adapter blocks its overlapping campaign/model surface and
/// unrouteable singleton-player actions, fingerprints external configuration, and supplies a
/// revisioned stable-ID snapshot format for its audited persisted fields.
/// </summary>
internal sealed class FourberieCompatibilityHandler : IHandler, IFourberiePatchRuntime
{
    private static readonly ILogger Logger = LogManager.GetLogger<FourberieCompatibilityHandler>();
    private static readonly object PatchSync = new object();
    private static Assembly startupPatchedAssembly;
    private static Assembly patchedAssembly;

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IWorkshopCapabilityRegistry capabilityRegistry;
    private readonly IAuthorityRouteHandle<FourberieSnapshotIntent, NetworkFourberieStateQueryResult> snapshotRoute;
    private readonly Harmony harmony;
    private readonly FourberieRevisionGate revisionGate = new FourberieRevisionGate();
    private readonly object snapshotSync = new object();
    private readonly IAuthorityRouteHandle<FourberieLocalOperation, NetworkFourberieOperationResult> gameplayRoute;
    private NetworkFourberieContractProposal pendingContractProposal;
    private string shownContractProposalKey;

    private Assembly assembly;
    private string configurationFingerprint;
    private string lastPublishedFingerprint;
    private long serverRevision;
    private bool compatible;
    private bool stateReady;
    private bool objectsRegistered;
    internal WorkshopSnapshotReadiness SnapshotReadiness { get; private set; }
    internal string SnapshotSessionId { get; private set; }
    internal long SnapshotRevision { get; private set; } = -1;
    private FourberieOperationExecutor operationExecutor;
    private (FourberieMethodSpec Spec, MethodInfo Original, MethodInfo Prefix, MethodInfo Postfix)[] expectedGuards;

    public FourberieCompatibilityHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IModConfigAuthority configAuthority,
        IWorkshopCapabilityRegistry capabilityRegistry,
        Harmony harmonyDependency,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.configAuthority = configAuthority;
        this.capabilityRegistry = capabilityRegistry;
        this.harmony = new Harmony(FourberieCompatibilityManifest.AdapterHarmonyId);

        compatible = TryInstall();
        if (compatible) FourberiePatchRuntime.Current = this;

        // Register before the result subscriber: the router records an Accepted result first,
        // then this handler applies its snapshot, and the update loop probes readiness.
        snapshotRoute = authorityRequestRouter.Register(
            AuthorityRoute<FourberieSnapshotIntent, NetworkRequestFourberieState,
                NetworkFourberieStateQueryResult>.Define(
                "workshop.fourberie.snapshot", AuthorityRouteKind.BootstrapQuery,
                CreateSnapshotHeader,
                (_, header) => new NetworkRequestFourberieState(header),
                request => request.Header,
                result => result.Header,
                request => request.Header.TryValidate(out _) ? null : "invalid-fourberie-snapshot-query",
                request => "snapshot:" + request.Header.SessionId + ":" + request.Header.ExpectedRevision,
                ValidateSnapshotHeader,
                ExecuteSnapshotQuery,
                CreateSnapshotTerminal,
                ProbeSnapshotApplied,
                _ => { },
                PresentSnapshotTerminal,
                configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.BootstrapQuery,
                requireAuthenticatedPlayer: false));

        gameplayRoute = authorityRequestRouter.Register(
            AuthorityRoute<FourberieLocalOperation, NetworkRequestFourberieOperation,
                NetworkFourberieOperationResult>.Define(
                "workshop.fourberie.gameplay", AuthorityRouteKind.Command,
                CreateOperationHeader,
                BuildOperationRequest,
                request => request.Header,
                result => result.Header,
                request => FourberieOperationProtocol.IsRequestShapeValid(request) ? null : "invalid-fourberie-operation",
                FourberieOperationProtocol.CommandKey,
                ValidateOperationHeader,
                ExecuteOperationRoute,
                CreateOperationTerminal,
                ProbeOperationApplied,
                RequestOperationResync,
                PresentOperationTerminal,
                configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: false,
                isExpectedClientResult: IsExpectedOperationResult));

        messageBroker.Subscribe<AllGameObjectsRegistered>(HandleAllGameObjectsRegistered);
        messageBroker.Subscribe<NetworkFourberieContractProposal>(HandleContractProposal);
        messageBroker.Subscribe<NetworkFourberieState>(HandleState);
        messageBroker.Subscribe<NetworkFourberieStateQueryResult>(HandleStateQueryResult);
        messageBroker.Subscribe<HostModConfigAccepted>(HandleHostModConfigAccepted);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<AllGameObjectsRegistered>(HandleAllGameObjectsRegistered);
        messageBroker.Unsubscribe<NetworkFourberieContractProposal>(HandleContractProposal);
        messageBroker.Unsubscribe<NetworkFourberieState>(HandleState);
        messageBroker.Unsubscribe<NetworkFourberieStateQueryResult>(HandleStateQueryResult);
        messageBroker.Unsubscribe<HostModConfigAccepted>(HandleHostModConfigAccepted);
        snapshotRoute.Dispose();
        gameplayRoute.Dispose();
        if (ReferenceEquals(FourberiePatchRuntime.Current, this)) FourberiePatchRuntime.Current = null;
    }

    public void PublishIfChanged()
    {
        if (!compatible || !stateReady || !ModInformation.IsServer) return;
        SendSnapshotOrAbort(peer: null, onlyIfChanged: true);
    }

    public bool TrySubmit(FourberieLocalOperation operation)
    {
        if (!ModInformation.IsClient || operation == null || !CanUseGameplayRoute(out _) ||
            revisionGate.Revision < 0)
            return false;

        gameplayRoute.Submit(operation);
        return true;
    }

    private NetworkRequestFourberieOperation BuildOperationRequest(
        FourberieLocalOperation operation,
        AuthorityRequestHeader header)
    {
        if (operation == null) return new NetworkRequestFourberieOperation(
            header, 0, string.Empty, string.Empty, string.Empty, 0,
            Array.Empty<FourberieTroopSelection>(), Array.Empty<FourberieItemSelection>(),
            Array.Empty<string>(), Array.Empty<FourberieRosterSelection>());

        string settlementId = string.Empty;
        string targetId = string.Empty;
        string secondaryTargetId = string.Empty;
        if (operation.Settlement != null && !objectManager.TryGetId(operation.Settlement, out settlementId))
            return false;
        int targetKinds = (operation.TargetHero != null ? 1 : 0) +
                          (operation.TargetClan != null ? 1 : 0) +
                          (operation.TargetObject != null ? 1 : 0);
        if (targetKinds > 1) return false;
        if (operation.TargetHero != null && !objectManager.TryGetId(operation.TargetHero, out targetId))
            return false;
        if (operation.TargetClan != null && !objectManager.TryGetId(operation.TargetClan, out targetId))
            return false;
        if (operation.TargetObject != null && !objectManager.TryGetId(operation.TargetObject, out targetId))
            return false;
        if (operation.SecondarySettlement != null &&
            !objectManager.TryGetId(operation.SecondarySettlement, out secondaryTargetId))
            return false;
        if (!string.IsNullOrEmpty(operation.SecondaryId))
        {
            if (!string.IsNullOrEmpty(secondaryTargetId)) return false;
            secondaryTargetId = operation.SecondaryId;
        }

        var objectIds = new List<string>();
        foreach (object target in operation.TargetObjects)
        {
            if (target == null || !objectManager.TryGetId(target, out string objectId)) return false;
            objectIds.Add(objectId);
        }

        var troops = new List<FourberieTroopSelection>();
        foreach (FourberieLocalTroopSelection troop in operation.Troops)
        {
            if (troop?.Troop == null || !objectManager.TryGetId(troop.Troop, out string troopId))
                return false;
            troops.Add(new FourberieTroopSelection(troopId, troop.Count));
        }

        var items = new List<FourberieItemSelection>();
        foreach (FourberieLocalItemSelection item in operation.Items)
        {
            ItemObject itemObject = item?.EquipmentElement.Item;
            ItemModifier modifier = item?.EquipmentElement.ItemModifier;
            if (itemObject == null || !objectManager.TryGetId(itemObject, out string itemId))
                return false;
            string modifierId = string.Empty;
            if (modifier != null && !objectManager.TryGetId(modifier, out modifierId))
                return false;
            items.Add(new FourberieItemSelection(itemId, modifierId, item.DeltaToSafehouse));
        }

        var roster = new List<FourberieRosterSelection>();
        foreach (FourberieLocalRosterSelection selection in operation.Roster)
        {
            if (selection?.Troop == null || !objectManager.TryGetId(selection.Troop, out string troopId))
                return false;
            roster.Add(new FourberieRosterSelection(
                troopId,
                selection.MemberDeltaToActor,
                selection.PrisonerDeltaToActor));
        }

        var request = new NetworkRequestFourberieOperation(
            header,
            operation.Operation,
            settlementId,
            targetId,
            secondaryTargetId,
            operation.IntValue,
            troops.ToArray(),
            items.ToArray(),
            objectIds.ToArray(),
            roster.ToArray());
        return request;
    }

    public void RunContractTick()
    {
        if (!ModInformation.IsServer || !CanUseGameplayRoute(out _) || CampaignTime.Now.GetHourOfDay % 6 != 0 ||
            !TryGetContractState(out IDictionary crime, out IDictionary heroes))
            return;

        bool ready;
        using (new AllowedThread()) ready = FourberieContractAuthority.AdvanceProposalCooldown(crime);
        if (!ready)
        {
            SendSnapshotOrAbort(peer: null, onlyIfChanged: true);
            return;
        }
        if (!TryFindContractController(heroes, out Hero actor, out MobileParty actorParty, out NetPeer peer) ||
            actor.IsPrisoner || actorParty.IsCurrentlyAtSea || actorParty.MapEvent != null ||
            actorParty.CurrentSettlement != null)
            return;

        Type contractType = assembly.GetType("Fourberie.FourbContractBehavior", true, false);
        Hero giver;
        Hero target;
        using (new BarterPlayerContext(actor, actorParty))
        {
            giver = AccessTools.Method(contractType, "GetContractGiver", Type.EmptyTypes)?.Invoke(null, null) as Hero;
            target = giver == null
                ? null
                : AccessTools.Method(contractType, "GetVictimHero", new[] { typeof(Hero) })?.Invoke(null, new object[] { giver }) as Hero;
        }
        if (giver?.Clan == null || target?.Clan == null || giver == target) return;

        int proposalType = MBRandom.RandomInt(2);
        if (!FourberieContractAuthority.TryReward(
                proposalType,
                giver.Clan.Gold,
                MBRandom.RandomInt(
                    FourberieContractAuthority.MinimumRewardRandom,
                    FourberieContractAuthority.MaximumRewardRandom + 1),
                out int reward,
                out string failure))
            throw new InvalidOperationException(failure);
        if (!objectManager.TryGetId(giver, out string giverId) ||
            !objectManager.TryGetId(target, out string targetId))
            return;

        using (new AllowedThread())
            if (!FourberieContractAuthority.TryCommitProposal(
                    crime, heroes, giverId, targetId, proposalType, reward, out failure))
                throw new InvalidOperationException(failure);

        SendSnapshotOrAbort(peer: null, onlyIfChanged: true);
        SendContractProposal(peer, giverId, targetId, proposalType, reward);
    }

    private bool TryInstall()
    {
        assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(
                candidate.GetName().Name,
                FourberieCompatibilityManifest.AssemblyName,
                StringComparison.Ordinal));
        if (assembly == null)
        {
            Logger.Debug("Fourberie is not loaded; compatibility adapter is inactive");
            return false;
        }

        operationExecutor = new FourberieOperationExecutor(assembly, objectManager);

        if (!FourberieCompatibilityManifest.TryValidate(assembly, out var methods, out var failure))
        {
            Logger.Fatal("Fourberie co-op adapter failed closed before patching: {Failure}", failure);
            throw new InvalidOperationException(
                "Unsupported Fourberie binary/API. Coop startup was aborted so the unguarded mod cannot mutate the rendered client campaign: " +
                failure);
        }

        if (!FourberieConfigurationFingerprint.TryComputeForAssembly(
                assembly,
                out configurationFingerprint,
                out var selectedFiles,
                out failure))
        {
            Logger.Fatal("Fourberie co-op adapter failed closed: {Failure}", failure);
            throw new InvalidOperationException(
                "Fourberie configuration could not be canonicalized. Coop startup was aborted before Fourberie campaign code could run: " +
                failure);
        }

        lock (PatchSync)
        {
            FourberieHarmonyIsolation.PurgeAndAssertAuditedSurface(assembly, harmony);

            if (!FourberieRuntimeSurface.TryAssertNoActiveCampaignSurface(assembly, out failure))
            {
                Logger.Fatal("Fourberie co-op adapter found an already-active incompatible runtime: {Failure}", failure);
                throw new InvalidOperationException(failure);
            }

            expectedGuards = methods
                .Select(pair =>
                {
                    var prefix = PrefixFor(pair.Key.Kind);
                    MethodInfo postfix = pair.Key.Kind switch
                    {
                        FourberiePatchKind.ServerTick or FourberiePatchKind.ServerMutation =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.ServerTickPostfix)),
                        FourberiePatchKind.ClientRoleRefresh =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.ClientRoleRefreshPostfix)),
                        FourberiePatchKind.ClientCrimeRoomRead =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.ClientCrimeRoomReadPostfix)),
                        FourberiePatchKind.ClientSchemeFilter =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.ClientSchemeFilterPostfix)),
                        FourberiePatchKind.FightClubAdmission =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.FightClubAdmissionPostfix)),
                        FourberiePatchKind.DominanceCondition =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.DominanceConditionPostfix)),
                        FourberiePatchKind.StealthHit =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.StealthHitPostfix)),
                        FourberiePatchKind.StealthAnswer =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.StealthAnswerPostfix)),
                        FourberiePatchKind.StealthAgentRemoved =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.StealthAgentRemovedPostfix)),
                        FourberiePatchKind.StealthAlarm =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.StealthAlarmPostfix)),
                        FourberiePatchKind.BanditRosterOpen =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.BanditRosterOpenPostfix)),
                        FourberiePatchKind.ClientPresentation =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.ClientPresentationPostfix)),
                        FourberiePatchKind.CampaignConsequence =>
                            AccessTools.Method(typeof(FourberieAuthorityPatches), nameof(FourberieAuthorityPatches.CampaignConsequencePostfix)),
                        _ => null,
                    };
                    return (Spec: pair.Key, Original: pair.Value, Prefix: prefix, Postfix: postfix);
                })
                .ToArray();

            var startupGuards = expectedGuards
                .Where(guard => !FourberieCompatibilityManifest.RequiresCampaignAtPatchTime(guard.Spec))
                .ToArray();

            if (ReferenceEquals(patchedAssembly, assembly))
            {
                AssertOnlyAdapterGuards(expectedGuards);
                return true;
            }

            if (ReferenceEquals(startupPatchedAssembly, assembly))
            {
                AssertOnlyAdapterGuards(startupGuards);
                if (Campaign.Current != null) InstallCampaignReadyGuardsLocked();
                return true;
            }

            var guardsToInstall = Campaign.Current == null ? startupGuards : expectedGuards;

            var applied = new List<(FourberieMethodSpec Spec, MethodInfo Original, MethodInfo Prefix, MethodInfo Postfix)>();
            try
            {
                foreach (var guard in guardsToInstall)
                {
                    harmony.Patch(
                        guard.Original,
                        prefix: new HarmonyMethod(guard.Prefix),
                        postfix: guard.Postfix == null ? null : new HarmonyMethod(guard.Postfix));
                    applied.Add(guard);
                }

                AssertOnlyAdapterGuards(guardsToInstall);
                // See HarmonyPatchInfoStabilizer for why this reads Harmony's inventory more than once
                // before failing closed: an unretried misread here aborts Coop startup outright, and
                // PurgeAndAssertAuditedSurface has already proved this surface empty a few lines above
                // with a retried remove-and-verify cycle inside the same lock.
                if (!HarmonyPatchInfoStabilizer.StabilizeUntilAcceptable(
                        () => !FourberieHarmonyIsolation.DescribeAssemblyPatches(assembly).Any()))
                {
                    throw new InvalidOperationException(
                        "Fourberie assembly-owned Harmony patches appeared while installing Coop guards: " +
                        string.Join("; ", FourberieHarmonyIsolation.DescribeAssemblyPatches(assembly)));
                }

                if (guardsToInstall.Length == expectedGuards.Length)
                    patchedAssembly = assembly;
                else
                    startupPatchedAssembly = assembly;
            }
            catch (Exception exception)
            {
                foreach (var patch in applied)
                {
                    harmony.Unpatch(patch.Original, patch.Prefix);
                    if (patch.Postfix != null) harmony.Unpatch(patch.Original, patch.Postfix);
                }
                Logger.Fatal(exception, "Fourberie co-op patching failed; rolled back adapter detours");
                throw new InvalidOperationException(
                    "Fourberie co-op guard installation failed. Coop startup was aborted after rolling back partial detours.",
                    exception);
            }
        }

        Logger.Information(
            "Fourberie {Version} co-op authority adapter enabled ({Methods} routed methods, {Deferred} campaign-ready guards deferred, config {Fingerprint}, files {Files})",
            FourberieCompatibilityManifest.SupportedModuleVersion,
            methods.Count,
            ReferenceEquals(patchedAssembly, assembly) ? 0 : expectedGuards.Count(guard =>
                FourberieCompatibilityManifest.RequiresCampaignAtPatchTime(guard.Spec)),
            configurationFingerprint,
            string.Join(", ", selectedFiles.Select(System.IO.Path.GetFileName)));
        return true;
    }

    private void InstallCampaignReadyGuards()
    {
        lock (PatchSync)
            InstallCampaignReadyGuardsLocked();
    }

    private void InstallCampaignReadyGuardsLocked()
    {
        if (ReferenceEquals(patchedAssembly, assembly))
        {
            AssertOnlyAdapterGuards(expectedGuards);
            return;
        }
        if (Campaign.Current == null)
            throw new InvalidOperationException(
                "Fourberie campaign-ready guards cannot be installed before Campaign.Current exists.");
        if (!ReferenceEquals(startupPatchedAssembly, assembly))
            throw new InvalidOperationException(
                "Fourberie campaign-ready guards cannot be installed before startup guards are verified.");

        var deferred = expectedGuards
            .Where(guard => FourberieCompatibilityManifest.RequiresCampaignAtPatchTime(guard.Spec))
            .ToArray();
        var applied = new List<(FourberieMethodSpec Spec, MethodInfo Original, MethodInfo Prefix, MethodInfo Postfix)>();
        try
        {
            foreach (var guard in deferred)
            {
                harmony.Patch(
                    guard.Original,
                    prefix: new HarmonyMethod(guard.Prefix),
                    postfix: guard.Postfix == null ? null : new HarmonyMethod(guard.Postfix));
                applied.Add(guard);
            }

            AssertOnlyAdapterGuards(expectedGuards);
            patchedAssembly = assembly;
            Logger.Information(
                "Fourberie campaign-ready authority guards installed ({Methods} routed methods)",
                expectedGuards.Length);
        }
        catch (Exception exception)
        {
            foreach (var patch in applied)
            {
                harmony.Unpatch(patch.Original, patch.Prefix);
                if (patch.Postfix != null) harmony.Unpatch(patch.Original, patch.Postfix);
            }
            Logger.Fatal(exception, "Fourberie campaign-ready guard installation failed");
            throw new InvalidOperationException(
                "Fourberie campaign-ready guard installation failed. Coop startup was aborted after rolling back deferred detours.",
                exception);
        }
    }

    private void AssertOnlyAdapterGuards(
        IEnumerable<(FourberieMethodSpec Spec, MethodInfo Original, MethodInfo Prefix, MethodInfo Postfix)> guards) =>
        FourberieHarmonyIsolation.AssertOnlyAdapterGuards(
            guards.Select(guard => (guard.Original, guard.Prefix, guard.Postfix)),
            harmony.Id);

    private static MethodInfo PrefixFor(FourberiePatchKind kind)
    {
        string method;
        switch (kind)
        {
            case FourberiePatchKind.ServerTick:
            case FourberiePatchKind.ServerMutation:
                method = nameof(FourberieAuthorityPatches.ServerTickPrefix);
                break;
            case FourberiePatchKind.BehaviorsAndModels:
                method = nameof(FourberieAuthorityPatches.InitializeBehaviorsAndModelsPrefix);
                break;
            case FourberiePatchKind.ClientOperationPresentation:
                method = nameof(FourberieAuthorityPatches.ClientOperationPresentationPrefix);
                break;
            case FourberiePatchKind.EnlistPartyConsequence:
                method = nameof(FourberieAuthorityPatches.EnlistPartyConsequencePrefix);
                break;
            case FourberiePatchKind.EnlistLadsConsequence:
                method = nameof(FourberieAuthorityPatches.EnlistLadsConsequencePrefix);
                break;
            case FourberiePatchKind.EnslavePrisonersConsequence:
                method = nameof(FourberieAuthorityPatches.EnslavePrisonersConsequencePrefix);
                break;
            case FourberiePatchKind.RecruitBanditsConsequence:
                method = nameof(FourberieAuthorityPatches.RecruitBanditsConsequencePrefix);
                break;
            case FourberiePatchKind.InsuranceScamConsequence:
                method = nameof(FourberieAuthorityPatches.InsuranceScamConsequencePrefix);
                break;
            case FourberiePatchKind.BusinessStartConsequence:
                method = nameof(FourberieAuthorityPatches.BusinessStartConsequencePrefix);
                break;
            case FourberiePatchKind.BusinessUpgradeConsequence:
                method = nameof(FourberieAuthorityPatches.BusinessUpgradeConsequencePrefix);
                break;
            case FourberiePatchKind.BusinessDowngradeConsequence:
                method = nameof(FourberieAuthorityPatches.BusinessDowngradeConsequencePrefix);
                break;
            case FourberiePatchKind.SchemeBonusUpgradeConsequence:
                method = nameof(FourberieAuthorityPatches.SchemeBonusUpgradeConsequencePrefix);
                break;
            case FourberiePatchKind.SchemeBonusDowngradeConsequence:
                method = nameof(FourberieAuthorityPatches.SchemeBonusDowngradeConsequencePrefix);
                break;
            case FourberiePatchKind.SchemeBonusResetConsequence:
                method = nameof(FourberieAuthorityPatches.SchemeBonusResetConsequencePrefix);
                break;
            case FourberiePatchKind.AgentPartyCreateConsequence:
                method = nameof(FourberieAuthorityPatches.AgentPartyCreateConsequencePrefix);
                break;
            case FourberiePatchKind.AgentPartyDisbandConsequence:
                method = nameof(FourberieAuthorityPatches.AgentPartyDisbandConsequencePrefix);
                break;
            case FourberiePatchKind.AgentPartySelectionConsequence:
                method = nameof(FourberieAuthorityPatches.AgentPartySelectionConsequencePrefix);
                break;
            case FourberiePatchKind.CrimeBaseResetConsequence:
                method = nameof(FourberieAuthorityPatches.CrimeBaseResetConsequencePrefix);
                break;
            case FourberiePatchKind.RoleSelectionPresentation:
                method = nameof(FourberieAuthorityPatches.ClientPresentationPrefix);
                break;
            case FourberiePatchKind.RoleAssignmentConsequence:
                method = nameof(FourberieAuthorityPatches.RoleAssignmentConsequencePrefix);
                break;
            case FourberiePatchKind.RoleRemovalConsequence:
                method = nameof(FourberieAuthorityPatches.RoleRemovalConsequencePrefix);
                break;
            case FourberiePatchKind.SchemeVictimConsequence:
                method = nameof(FourberieAuthorityPatches.SchemeVictimConsequencePrefix);
                break;
            case FourberiePatchKind.SchemeTypeConsequence:
                method = nameof(FourberieAuthorityPatches.SchemeTypeConsequencePrefix);
                break;
            case FourberiePatchKind.SchemeLifecycleConsequence:
                method = nameof(FourberieAuthorityPatches.SchemeLifecycleConsequencePrefix);
                break;
            case FourberiePatchKind.SchemeOwnedReplacement:
                method = nameof(FourberieAuthorityPatches.SchemeOwnedReplacementPrefix);
                break;
            case FourberiePatchKind.SchemeStanceConsequence:
                method = nameof(FourberieAuthorityPatches.SchemeStanceConsequencePrefix);
                break;
            case FourberiePatchKind.ClientRoleRefresh:
                method = nameof(FourberieAuthorityPatches.ClientRoleRefreshPrefix);
                break;
            case FourberiePatchKind.CorruptionLevelConsequence:
                method = nameof(FourberieAuthorityPatches.CorruptionLevelConsequencePrefix);
                break;
            case FourberiePatchKind.CrimeRoomSliderConsequence:
                method = nameof(FourberieAuthorityPatches.CrimeRoomSliderConsequencePrefix);
                break;
            case FourberiePatchKind.ClientCrimeRoomRead:
                method = nameof(FourberieAuthorityPatches.ClientCrimeRoomReadPrefix);
                break;
            case FourberiePatchKind.ContractConsequence:
                method = nameof(FourberieAuthorityPatches.ContractConsequencePrefix);
                break;
            case FourberiePatchKind.ClientSchemeFilter:
                method = nameof(FourberieAuthorityPatches.ClientSchemeFilterPrefix);
                break;
            case FourberiePatchKind.MainBaseConsequence:
                method = nameof(FourberieAuthorityPatches.MainBaseConsequencePrefix);
                break;
            case FourberiePatchKind.TerritorySelectionConsequence:
                method = nameof(FourberieAuthorityPatches.TerritorySelectionConsequencePrefix);
                break;
            case FourberiePatchKind.TerritoryAbandonConsequence:
                method = nameof(FourberieAuthorityPatches.TerritoryAbandonConsequencePrefix);
                break;
            case FourberiePatchKind.SafehouseAbandonConsequence:
                method = nameof(FourberieAuthorityPatches.SafehouseAbandonConsequencePrefix);
                break;
            case FourberiePatchKind.SafehouseEstablishmentConsequence:
                method = nameof(FourberieAuthorityPatches.SafehouseEstablishmentConsequencePrefix);
                break;
            case FourberiePatchKind.SafehouseTraderConsequence:
                method = nameof(FourberieAuthorityPatches.SafehouseTraderConsequencePrefix);
                break;
            case FourberiePatchKind.SafehouseWaitConsequence:
                method = nameof(FourberieAuthorityPatches.SafehouseWaitConsequencePrefix);
                break;
            case FourberiePatchKind.SafehouseReturnLifecycle:
                method = nameof(FourberieAuthorityPatches.SafehouseReturnLifecyclePrefix);
                break;
            case FourberiePatchKind.GrudgeSelectionConsequence:
                method = nameof(FourberieAuthorityPatches.GrudgeSelectionConsequencePrefix);
                break;
            case FourberiePatchKind.GrudgeSettlementConsequence:
                method = nameof(FourberieAuthorityPatches.GrudgeSettlementConsequencePrefix);
                break;
            case FourberiePatchKind.ContractTickReplacement:
                method = nameof(FourberieAuthorityPatches.ContractTickReplacementPrefix);
                break;
            case FourberiePatchKind.ContractProposalLegacyConsequence:
                method = nameof(FourberieAuthorityPatches.ContractProposalLegacyConsequencePrefix);
                break;
            case FourberiePatchKind.InsideMissionOutcome:
                method = nameof(FourberieAuthorityPatches.InsideMissionOutcomePrefix);
                break;
            case FourberiePatchKind.FightClubOutcome:
                method = nameof(FourberieAuthorityPatches.FightClubOutcomePrefix);
                break;
            case FourberiePatchKind.FightClubMissionLocal:
                method = nameof(FourberieAuthorityPatches.FightClubMissionLocalPrefix);
                break;
            case FourberiePatchKind.FightClubFame:
                method = nameof(FourberieAuthorityPatches.FightClubFamePrefix);
                break;
            case FourberiePatchKind.FightClubAdmission:
                method = nameof(FourberieAuthorityPatches.FightClubAdmissionPrefix);
                break;
            case FourberiePatchKind.FightClubEnrollment:
                method = nameof(FourberieAuthorityPatches.FightClubEnrollmentPrefix);
                break;
            case FourberiePatchKind.FightClubPatronRefusal:
                method = nameof(FourberieAuthorityPatches.FightClubPatronRefusalPrefix);
                break;
            case FourberiePatchKind.FightClubStableOwnership:
                method = nameof(FourberieAuthorityPatches.FightClubStableOwnershipPrefix);
                break;
            case FourberiePatchKind.FightClubStableRecruitment:
                method = nameof(FourberieAuthorityPatches.FightClubStableRecruitmentPrefix);
                break;
            case FourberiePatchKind.FightClubMenuRefresh:
                method = nameof(FourberieAuthorityPatches.FightClubMenuRefreshPrefix);
                break;
            case FourberiePatchKind.FightClubPatronPayment:
                method = nameof(FourberieAuthorityPatches.FightClubPatronPaymentPrefix);
                break;
            case FourberiePatchKind.AlleyAcquisition:
                method = nameof(FourberieAuthorityPatches.AlleyAcquisitionPrefix);
                break;
            case FourberiePatchKind.AlleyClear:
                method = nameof(FourberieAuthorityPatches.AlleyClearPrefix);
                break;
            case FourberiePatchKind.SchemeRoomOpen:
                method = nameof(FourberieAuthorityPatches.SchemeRoomOpenPrefix);
                break;
            case FourberiePatchKind.DominanceCondition:
                method = nameof(FourberieAuthorityPatches.DominanceConditionPrefix);
                break;
            case FourberiePatchKind.StealthMissionLocal:
                method = nameof(FourberieAuthorityPatches.StealthMissionLocalPrefix);
                break;
            case FourberiePatchKind.StealthHit:
                method = nameof(FourberieAuthorityPatches.StealthHitPrefix);
                break;
            case FourberiePatchKind.StealthMissionEnd:
                method = nameof(FourberieAuthorityPatches.StealthMissionEndPrefix);
                break;
            case FourberiePatchKind.StealthMilitiaPayment:
                method = nameof(FourberieAuthorityPatches.StealthMilitiaPaymentPrefix);
                break;
            case FourberiePatchKind.StealthMilitiaChoice:
                method = nameof(FourberieAuthorityPatches.StealthMilitiaChoicePrefix);
                break;
            case FourberiePatchKind.StealthAbortContract:
                method = nameof(FourberieAuthorityPatches.StealthAbortContractPrefix);
                break;
            case FourberiePatchKind.StealthAlertConsequence:
                method = nameof(FourberieAuthorityPatches.StealthAlertConsequencePrefix);
                break;
            case FourberiePatchKind.StealthAnswer:
                method = nameof(FourberieAuthorityPatches.StealthAnswerPrefix);
                break;
            case FourberiePatchKind.StealthAgentRemoved:
                method = nameof(FourberieAuthorityPatches.StealthAgentRemovedPrefix);
                break;
            case FourberiePatchKind.StealthAlarm:
                method = nameof(FourberieAuthorityPatches.StealthAlarmPrefix);
                break;
            case FourberiePatchKind.StealthScandalSuccess:
                method = nameof(FourberieAuthorityPatches.StealthScandalSuccessPrefix);
                break;
            case FourberiePatchKind.StealthPrisonSuccess:
                method = nameof(FourberieAuthorityPatches.StealthPrisonSuccessPrefix);
                break;
            case FourberiePatchKind.BanditConsequence:
                method = nameof(FourberieAuthorityPatches.BanditConsequencePrefix);
                break;
            case FourberiePatchKind.BanditDonationConsequence:
                method = nameof(FourberieAuthorityPatches.BanditDonationConsequencePrefix);
                break;
            case FourberiePatchKind.BanditRosterOpen:
                method = nameof(FourberieAuthorityPatches.BanditRosterOpenPrefix);
                break;
            case FourberiePatchKind.BanditRosterConsequence:
                method = nameof(FourberieAuthorityPatches.BanditRosterConsequencePrefix);
                break;
            case FourberiePatchKind.BanditPreparation:
                method = nameof(FourberieAuthorityPatches.BanditPreparationPrefix);
                break;
            case FourberiePatchKind.LegacyCallback:
                method = nameof(FourberieAuthorityPatches.LegacyCallbackPrefix);
                break;
            case FourberiePatchKind.ConversationConsequence:
                method = nameof(FourberieAuthorityPatches.ConversationConsequencePrefix);
                break;
            case FourberiePatchKind.CampaignConsequence:
                method = nameof(FourberieAuthorityPatches.CampaignConsequencePrefix);
                break;
            case FourberiePatchKind.MinorRecruitmentConsequence:
                method = nameof(FourberieAuthorityPatches.MinorRecruitmentConsequencePrefix);
                break;
            case FourberiePatchKind.KingdomLeaveConsequence:
                method = nameof(FourberieAuthorityPatches.KingdomLeaveConsequencePrefix);
                break;
            case FourberiePatchKind.GuardKillConsequence:
                method = nameof(FourberieAuthorityPatches.GuardKillConsequencePrefix);
                break;
            case FourberiePatchKind.SafehouseEncounterConsequence:
                method = nameof(FourberieAuthorityPatches.SafehouseEncounterConsequencePrefix);
                break;
            case FourberiePatchKind.CriminalConsequence:
                method = nameof(FourberieAuthorityPatches.CriminalConsequencePrefix);
                break;
            case FourberiePatchKind.MissionLocal:
                method = nameof(FourberieAuthorityPatches.MissionLocalPrefix);
                break;
            case FourberiePatchKind.MissionInitialization:
                method = nameof(FourberieAuthorityPatches.MissionInitializationPrefix);
                break;
            case FourberiePatchKind.SeparatismLoyaltyComposition:
                method = nameof(FourberieAuthorityPatches.SeparatismLoyaltyCompositionPrefix);
                break;
            case FourberiePatchKind.RefreshHeroDicoOnly:
                method = nameof(FourberieAuthorityPatches.RefreshHeroDicoOnlyPrefix);
                break;
            case FourberiePatchKind.ClientPresentation:
                method = nameof(FourberieAuthorityPatches.ClientPresentationPrefix);
                break;
            case FourberiePatchKind.FinanceRead:
                method = nameof(FourberieAuthorityPatches.FinanceReadPrefix);
                break;
            default:
                method = nameof(FourberieAuthorityPatches.ServerOnlyPrefix);
                break;
        }

        return AccessTools.Method(typeof(FourberieAuthorityPatches), method);
    }

    private void HandleAllGameObjectsRegistered(MessagePayload<AllGameObjectsRegistered> _)
    {
        if (!compatible) return;

        InstallCampaignReadyGuards();

        if (!FourberieRuntimeSurface.TryAssertNoActiveCampaignSurface(assembly, out var runtimeFailure))
            throw new InvalidOperationException(runtimeFailure);

        FourberieAuthorityPatches.ResetTickLedger();
        FourberiePartyCommitSuppression.Reset();
        pendingContractProposal = null;
        shownContractProposalKey = null;
        operationExecutor?.Reset();
        lock (snapshotSync) revisionGate.Reset();
        objectsRegistered = true;
        stateReady = !ModInformation.IsClient;
        SnapshotReadiness = ModInformation.IsClient ? WorkshopSnapshotReadiness.Unknown : WorkshopSnapshotReadiness.Ready;
        SnapshotSessionId = null;
        SnapshotRevision = -1;
        if (ModInformation.IsClient)
        {
            StartSnapshotBootstrap();
            return;
        }

        serverRevision = 0;
        lastPublishedFingerprint = null;
        SendSnapshotOrAbort(peer: null, onlyIfChanged: false);
    }

    private void HandleHostModConfigAccepted(MessagePayload<HostModConfigAccepted> payload)
    {
        if (payload?.What.Snapshot == null || !configAuthority.IsCurrent(payload.What.Snapshot)) return;
        if (ModInformation.IsClient && !string.Equals(SnapshotSessionId, payload.What.Snapshot.SessionId, StringComparison.Ordinal))
        {
            stateReady = false;
            SnapshotReadiness = WorkshopSnapshotReadiness.Unknown;
            SnapshotRevision = -1;
            lock (snapshotSync) revisionGate.Reset();
        }
        StartSnapshotBootstrap();
    }

    private void StartSnapshotBootstrap()
    {
        if (!compatible || !objectsRegistered || !ModInformation.IsClient || stateReady ||
            !configAuthority.TryGetCurrent(out _)) return;
        SnapshotReadiness = WorkshopSnapshotReadiness.Loading;
        snapshotRoute.Submit(default);
    }

    private AuthorityRequestHeader CreateSnapshotHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out var config)) return default;
        return new AuthorityRequestHeader(config.ProtocolVersion, config.SessionId, requestId, config.Revision);
    }

    private AuthorityHeaderValidation ValidateSnapshotHeader(AuthorityRequestHeader header)
    {
        if (!compatible || !stateReady || !configAuthority.TryGetCurrent(out var config))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "fourberie-snapshot-unavailable");
        if (header.ProtocolVersion != config.ProtocolVersion || !string.Equals(header.SessionId, config.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-config-session");
        return header.ExpectedRevision == config.Revision
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-config-revision");
    }

    private AuthorityServerReply<NetworkFourberieStateQueryResult> ExecuteSnapshotQuery(
        AuthorityServerContext context, NetworkRequestFourberieState _)
    {
        if (!TryCaptureSnapshot(out var snapshot, out var failure))
        {
            Logger.Warning("Fourberie snapshot query is unavailable: {Failure}", failure);
            return new AuthorityServerReply<NetworkFourberieStateQueryResult>(
                CreateSnapshotTerminal(context.Header, AuthorityResultStatus.Unavailable, "fourberie-snapshot-unavailable"), false);
        }
        return new AuthorityServerReply<NetworkFourberieStateQueryResult>(
            new NetworkFourberieStateQueryResult(context.Header, AuthorityResultStatus.Accepted, snapshot, null), true);
    }

    private static NetworkFourberieStateQueryResult CreateSnapshotTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reasonCode) =>
        new NetworkFourberieStateQueryResult(header, status, null, reasonCode);

    private AuthorityCommitProbeResult ProbeSnapshotApplied(NetworkFourberieStateQueryResult result) =>
        stateReady && result.Snapshot != null && revisionGate.Revision == result.Header.CommittedRevision
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;

    private void PresentSnapshotTerminal(AuthorityClientOutcome<NetworkFourberieStateQueryResult> outcome)
    {
        if (outcome.Completion == AuthorityClientCompletion.Applied) return;
        stateReady = false;
        SnapshotReadiness = WorkshopSnapshotReadiness.Unavailable;
        Logger.Warning("Fourberie snapshot bootstrap ended without readiness. Completion={Completion} Reason={Reason}",
            outcome.Completion, outcome.ReasonCode);
    }

    private bool CanUseGameplayRoute(out ModConfigSnapshot config)
    {
        config = null;
        return compatible && stateReady && configAuthority.TryGetCurrent(out config) &&
               capabilityRegistry.IsEnabled(FourberieCapabilitySource.ModuleId, FourberieCapabilitySource.Operation);
    }

    private AuthorityRequestHeader CreateOperationHeader(long requestId)
    {
        if (!CanUseGameplayRoute(out var config)) return default;
        return new AuthorityRequestHeader(config.ProtocolVersion, config.SessionId, requestId, revisionGate.Revision);
    }

    private AuthorityHeaderValidation ValidateOperationHeader(AuthorityRequestHeader header)
    {
        if (!CanUseGameplayRoute(out var config))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "fourberie-route-unavailable");
        if (header.ProtocolVersion != config.ProtocolVersion || !string.Equals(header.SessionId, config.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-config-session");
        return FourberieOperationProtocol.CanApplyAtRevision(header.ExpectedRevision, serverRevision)
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-fourberie-state");
    }

    private AuthorityServerReply<NetworkFourberieOperationResult> ExecuteOperationRoute(
        AuthorityServerContext context, NetworkRequestFourberieOperation request)
    {
        if (!CanUseGameplayRoute(out _)) return OperationReply(context.Header, request, AuthorityResultStatus.Unavailable, "fourberie-route-unavailable", false, 0, null);
        if (!objectManager.TryGetObject(context.Player.HeroId, out Hero actor) ||
            !objectManager.TryGetObject(context.Player.MobilePartyId, out MobileParty actorParty) || actor == null || actorParty == null ||
            actor.PartyBelongedTo != actorParty || actorParty.LeaderHero != actor)
            return OperationReply(context.Header, request, AuthorityResultStatus.Unauthorized, "actor-party-mismatch", false, 0, null);
        try
        {
            FourberieTouchedState before = CaptureTouchedState(actor, actorParty, request);
            if (!operationExecutor.TryExecute(actor, actorParty, request, out string failure, out int value))
                return OperationReply(context.Header, request, AuthorityResultStatus.Rejected, "operation-rejected", false, value, null);
            if (!TryCaptureSnapshot(out var snapshot, out failure)) return IsolateOperation(context, request, "poststate-capture-failed");
            bool changed = !string.Equals(lastPublishedFingerprint, snapshot.StateFingerprint, StringComparison.OrdinalIgnoreCase);
            FourberieTouchedState after = CaptureTouchedState(actor, actorParty, request);
            if (!changed && !PostStateMatches(request, snapshot) && TouchedStateEquals(before, after))
                return OperationReply(context.Header, request, AuthorityResultStatus.ExecutionFailed,
                    "missing-canonical-poststate", false, value, after);
            if (changed)
            {
                SendSnapshotOrAbort(null, onlyIfChanged: true);
                if (serverRevision != snapshot.Revision) return IsolateOperation(context, request, "snapshot-publication-failed");
            }
            return OperationReply(context.Header, request, AuthorityResultStatus.Accepted, null, changed, value,
                after);
        }
        catch (Exception exception)
        {
            Logger.Fatal(exception, "Fourberie operation rollback/publication failed. Route={Route} Request={Request}", context.RouteId, context.Header.RequestId);
            return IsolateOperation(context, request, "fourberie-isolated");
        }
    }

    private AuthorityServerReply<NetworkFourberieOperationResult> IsolateOperation(AuthorityServerContext context,
        NetworkRequestFourberieOperation request, string stage)
    {
        foreach (var player in playerManager.Players)
            if (playerManager.IsConnected(player) && playerManager.TryGetPeer(player.ControllerId, out var peer)) peer.Disconnect();
        return new AuthorityServerReply<NetworkFourberieOperationResult>(
            CreateOperationResult(context.Header, request, AuthorityResultStatus.ExecutionFailed, stage, false, 0, null), false, true);
    }

    private AuthorityServerReply<NetworkFourberieOperationResult> OperationReply(AuthorityRequestHeader header,
        NetworkRequestFourberieOperation request, AuthorityResultStatus status, string reason, bool changed, int value, FourberieTouchedState touched) =>
        new AuthorityServerReply<NetworkFourberieOperationResult>(CreateOperationResult(header, request, status, reason, changed, value, touched), changed);

    private NetworkFourberieOperationResult CreateOperationResult(AuthorityRequestHeader header,
        NetworkRequestFourberieOperation request, AuthorityResultStatus status, string reason, bool changed, int value, FourberieTouchedState touched)
    {
        string fingerprint = lastPublishedFingerprint ?? string.Empty;
        return new NetworkFourberieOperationResult(header, request?.Operation ?? 0,
            status == AuthorityResultStatus.Accepted ? FourberieOperationStatus.Accepted : FourberieOperationStatus.Rejected,
            status, reason, request == null ? string.Empty : FourberieOperationProtocol.CommandKey(request),
            serverRevision, fingerprint, changed, value, touched);
    }

    private NetworkFourberieOperationResult CreateOperationTerminal(AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        CreateOperationResult(header, null, status, reason, false, 0, null);

    private bool IsExpectedOperationResult(NetworkRequestFourberieOperation request, NetworkFourberieOperationResult result) =>
        result != null && result.Operation == request.Operation &&
        string.Equals(result.CommandDigest, FourberieOperationProtocol.CommandKey(request), StringComparison.Ordinal);

    private AuthorityCommitProbeResult ProbeOperationApplied(NetworkFourberieOperationResult result)
    {
        if (result?.Header.Status != AuthorityResultStatus.Accepted || result.Operation == 0 ||
            !FourberieStateCodec.IsSha256(result.StateFingerprint)) return AuthorityCommitProbeResult.Invalid;
        if (result.Operation == FourberieOperation.RequestGrudgeQuote)
            return result.IntValue is >= 0 and <= FourberieGrudgeAuthority.MaximumPayment ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Invalid;
        if (revisionGate.Revision != result.Header.CommittedRevision) return AuthorityCommitProbeResult.Pending;
        if (!FourberieCanonicalState.TryCapture(assembly, objectManager, out _, out string fingerprint, out _)) return AuthorityCommitProbeResult.Pending;
        if (!string.Equals(fingerprint, result.StateFingerprint, StringComparison.OrdinalIgnoreCase)) return AuthorityCommitProbeResult.Invalid;
        Hero actor = Hero.MainHero;
        MobileParty party = MobileParty.MainParty;
        return actor != null && party != null && TouchedStateEquals(result.TouchedState,
            CaptureTouchedState(actor, party, new NetworkRequestFourberieOperation(result.Header.SessionId,
                result.Header.RequestId, result.Header.CommittedRevision, result.Operation,
                result.TouchedState?.TargetSettlementId, result.TouchedState?.TargetId, string.Empty, result.IntValue,
                Array.Empty<FourberieTroopSelection>())))
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private void RequestOperationResync(NetworkFourberieOperationResult _) => StartSnapshotBootstrap();

    private static bool PostStateMatches(NetworkRequestFourberieOperation request, NetworkFourberieState snapshot)
    {
        if (request.Operation == FourberieOperation.RequestGrudgeQuote) return true;
        int? key = request.Operation switch
        {
            FourberieOperation.SetCorruptionLevel => 5,
            FourberieOperation.SetAutoInvestment => 61,
            FourberieOperation.SetLadsDuty => 1000,
            FourberieOperation.SetSlavesDuty => 1001,
            FourberieOperation.EnsureSchemeRoomDefaults => 500,
            FourberieOperation.ClearDominanceConversation => 92,
            _ => null,
        };
        if (key == null) return false;
        FourberieStateEntry[] entries = snapshot?.Entries ?? Array.Empty<FourberieStateEntry>();
        if (request.Operation == FourberieOperation.ClearDominanceConversation)
            return !entries.Any(entry => entry.Field == "_crimeValue" && entry.Key == "92");
        int expected = request.Operation == FourberieOperation.EnsureSchemeRoomDefaults ? 2 : request.IntValue;
        return entries.Any(entry => entry.Field == "_crimeValue" && entry.Key == key.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            entry.Value == expected.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private FourberieTouchedState CaptureTouchedState(Hero actor, MobileParty actorParty,
        NetworkRequestFourberieOperation request)
    {
        object target = null;
        if (!string.IsNullOrEmpty(request.TargetId)) objectManager.TryGetObject(request.TargetId, out target);
        MobileParty created = AccessTools.Field(assembly.GetType("Fourberie.FourberieBehavior", false), "_agentsParty")?.GetValue(null) as MobileParty;
        MobileParty destroyed = AccessTools.Field(assembly.GetType("Fourberie.FourberieBehavior", false), "_crimeBaseParty")?.GetValue(null) as MobileParty;
        return new FourberieTouchedState(actor.Gold, actor.HitPoints,
            RosterHash(actorParty.MemberRoster), RosterHash(actorParty.PrisonRoster), ItemHash(actorParty.ItemRoster),
            PartyId(created), created?.IsActive == true, PartyId(destroyed), destroyed?.IsActive == true,
            target is Hero hero ? hero.Gold : target is Clan clan ? clan.Gold : 0,
            target is Hero targetHero ? targetHero.MapFaction?.StringId : target is Clan targetClan ? targetClan.Kingdom?.StringId : string.Empty,
            request.SettlementId, request.TargetId);
    }

    private string RosterHash(TroopRoster roster) => FourberieStateCodec.ComputeHash((roster?.GetTroopRoster() ?? Enumerable.Empty<TroopRosterElement>())
        .Select((element, index) => new FourberieStateEntry("roster", FourberieStateValueKind.TroopRosterElement,
            objectManager.TryGetId(element.Character, out string id) ? id : string.Empty,
            element.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), index)));

    private static string ItemHash(ItemRoster roster) => FourberieStateCodec.ComputeHash(Enumerable.Range(0, roster?.Count ?? 0)
        .Select(index => new FourberieStateEntry("items", FourberieStateValueKind.ItemRosterElement,
            roster.GetElementCopyAtIndex(index).EquipmentElement.Item?.StringId ?? string.Empty,
            roster.GetElementCopyAtIndex(index).Amount.ToString(System.Globalization.CultureInfo.InvariantCulture), index)));

    private string PartyId(MobileParty party) => party != null && objectManager.TryGetId(party, out string id) ? id : string.Empty;

    private static bool TouchedStateEquals(FourberieTouchedState left, FourberieTouchedState right) =>
        left != null && right != null && left.ActorGold == right.ActorGold && left.ActorHitPoints == right.ActorHitPoints &&
        left.MemberRosterHash == right.MemberRosterHash && left.PrisonRosterHash == right.PrisonRosterHash &&
        left.ItemRosterHash == right.ItemRosterHash && left.CreatedPartyId == right.CreatedPartyId &&
        left.CreatedPartyActive == right.CreatedPartyActive && left.DestroyedPartyId == right.DestroyedPartyId &&
        left.DestroyedPartyActive == right.DestroyedPartyActive && left.TargetGold == right.TargetGold &&
        left.TargetFactionId == right.TargetFactionId && left.TargetSettlementId == right.TargetSettlementId &&
        left.TargetId == right.TargetId;

    private void PresentOperationTerminal(AuthorityClientOutcome<NetworkFourberieOperationResult> outcome)
    {
        if (outcome.Completion != AuthorityClientCompletion.Applied || outcome.Result == null) return;
        if (outcome.Result.Operation == FourberieOperation.RequestGrudgeQuote &&
            objectManager.TryGetObject(outcome.Result.TouchedState?.TargetId, out Clan clan))
            ShowGrudgeSettlement(clan, outcome.Result.IntValue);
        else if (!FourberieOperationProtocol.IsAbsoluteSetting(outcome.Result.Operation))
            InformationManager.DisplayMessage(new InformationMessage("Fourberie action accepted by the co-op server."));
    }

    private void ShowRiotPoliticalChoice(Settlement settlement, int choice)
    {
        Hero victim = settlement?.Owner;
        Hero leader = Hero.MainHero?.MapFaction?.Leader;
        if (victim == null || leader == null) return;
        int cost = choice == 1 ? 400 : choice == 2 ? 200 : 100;
        FourberieCriminalConsequence negative = choice == 1
            ? FourberieCriminalConsequence.DefectRiotVictim
            : choice == 2 ? FourberieCriminalConsequence.BanishRiotActor
            : FourberieCriminalConsequence.DeclareRiotWar;
        Hero negativeTarget = choice == 2 ? leader : victim;
        InformationManager.ShowInquiry(new InquiryData(
            new TextObject("{=Fov1506x008}Your are uncovered!").ToString(),
            new TextObject("{=Fov1506x009}{VAL} influence is required to limit the repercussions of your underhanded maneuvers.")
                .SetTextVariable("VAL", cost).ToString(),
            true, true,
            new TextObject("{=FoCom39}A close one...").ToString(),
            new TextObject("{=FoCom24}Damn it!").ToString(),
            () => SubmitRiotPoliticalChoice(settlement, victim, FourberieCriminalConsequence.PayRiotInfluence),
            () => SubmitRiotPoliticalChoice(settlement, negativeTarget, negative)), true, false);
    }

    private void SubmitRiotPoliticalChoice(
        Settlement settlement, Hero target, FourberieCriminalConsequence consequence)
    {
        if (!TrySubmit(new FourberieLocalOperation(
                FourberieOperation.CommitCriminalConsequence, settlement, target, null, (int)consequence,
                Array.Empty<FourberieLocalTroopSelection>())))
            FourberieSafehouseTransferContext.ShowUnavailable();
    }

    private void ShowLeaveKingdomChoice(Settlement settlement)
    {
        var choices = new List<InquiryElement>
        {
            new InquiryElement("keep", new TextObject("{=z8h0BRAb}Keep all holdings").ToString(), null, true,
                new TextObject("{=Fov1407x002}Owned settlements remain under your control but the kingdom will declare war on you.").ToString()),
            new InquiryElement("dontkeep", new TextObject("{=JIr3Jc7b}Relinquish all holdings").ToString(), null, true,
                new TextObject("{=Fov1407x003}Owned settlements are returned to the kingdom. This will avert a war.").ToString()),
        };
        MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
            new TextObject("{=3sxtCWPe}Leaving Kingdom").ToString(),
            new TextObject("{=Fov1407x004}Choose how you want to leave the kingdom.").ToString(),
            choices, false, 1, 1, new TextObject("{=FoCom79}Confirm").ToString(), string.Empty,
            selected =>
            {
                string option = selected.FirstOrDefault()?.Identifier as string;
                if (option == null || !TrySubmit(new FourberieLocalOperation(
                        FourberieOperation.LeaveKingdom, settlement, null, null, 0,
                        Array.Empty<FourberieLocalTroopSelection>(), secondaryId: option)))
                    FourberieSafehouseTransferContext.ShowUnavailable();
            }, null, string.Empty, false), true, false);
    }

    private void CompleteSafehouseReturnPresentation()
    {
        Type behavior = assembly.GetType("Fourberie.FourberieBehavior", false, false);
        Type safehouseBehavior = assembly.GetType("Fourberie.FourbSafeHouseBehavior", false, false);
        Settlement crimeBase = behavior == null
            ? null
            : AccessTools.Field(behavior, "_crimeBase")?.GetValue(null) as Settlement;
        object safehouse = safehouseBehavior == null
            ? null
            : AccessTools.Field(safehouseBehavior, "_safehouse")?.GetValue(null);
        if (crimeBase == null || safehouse == null) return;

        using (new AllowedThread())
        {
            // Deferred network result: guard the encounter/party the same way StopSafehouseWait does,
            // so a player who already left the safehouse menu doesn't NRE the tick on finish/re-encounter.
            if (PlayerEncounter.Current != null)
            {
                PlayerEncounter.LeaveSettlement();
                PlayerEncounter.Finish(false);
            }
            AccessTools.Method(safehouse.GetType(), "SetOwnerComplex")?.Invoke(safehouse, new object[] { null });
            if (MobileParty.MainParty != null)
                EncounterManager.StartSettlementEncounter(MobileParty.MainParty, crimeBase);
        }
    }

    private void ShowGrudgeSettlement(Clan clan, int amount)
    {
        string title = new TextObject("{=Fov1311x059}Grudge settlement").ToString();
        string body = new TextObject(
                "{=Fov1311x060}Your informants let you know that the {CLAN} clan is ready to settle the grudge they are holding for {AMOUNT}{GOLD_ICON}.")
            .SetTextVariable("CLAN", clan.Name)
            .SetTextVariable("AMOUNT", amount)
            .ToString();
        InformationManager.ShowInquiry(new InquiryData(
            title,
            body,
            true,
            true,
            new TextObject("{=FoCom24}Damn it!").ToString(),
            new TextObject("{=FoSchRm24}No way!").ToString(),
            () => TrySubmit(new FourberieLocalOperation(
                FourberieOperation.SettleClanGrudge,
                null,
                null,
                null,
                amount,
                Array.Empty<FourberieLocalTroopSelection>(),
                clan)),
            null,
            string.Empty,
            0f,
            null,
            () => GrudgeSettlementAvailability(amount),
            null),
            true,
            false);
    }

    private ValueTuple<bool, string> GrudgeSettlementAvailability(int amount)
    {
        Type behavior = assembly.GetType("Fourberie.FourberieBehavior", false, false);
        var crime = behavior == null
            ? null
            : AccessTools.Field(behavior, "_crimeValue")?.GetValue(null) as System.Collections.IDictionary;
        int spies = crime?.Contains(310) == true ? Convert.ToInt32(crime[310]) : 0;
        if (spies < 1)
            return (false, new TextObject("{=FoAgeOp24}Agent needed for this scheme: {AGT}")
                .SetTextVariable("AGT", new TextObject("{=FoAgeOp04}Spies"))
                .ToString());
        if (Hero.MainHero?.Gold < amount)
            return (false, new TextObject("{=FoCom70}You don't have enough denars to finish the deal!").ToString());
        return (true, new TextObject("{=Fov90xx23}You pay {VAL}{GOLD_ICON}")
            .SetTextVariable("VAL", amount)
            .ToString());
    }

    private bool TryGetContractState(out IDictionary crime, out IDictionary heroes)
    {
        Type behavior = assembly?.GetType("Fourberie.FourberieBehavior", false, false);
        crime = behavior == null ? null : AccessTools.Field(behavior, "_crimeValue")?.GetValue(null) as IDictionary;
        heroes = behavior == null ? null : AccessTools.Field(behavior, "_stringHeroIdDico")?.GetValue(null) as IDictionary;
        return crime != null && heroes != null;
    }

    private bool TryFindContractController(
        IDictionary heroes,
        out Hero actor,
        out MobileParty actorParty,
        out NetPeer peer)
    {
        actor = null;
        actorParty = null;
        peer = null;
        string enforcerId = heroes?.Contains("enforcer") == true ? heroes["enforcer"] as string : null;
        if (string.IsNullOrEmpty(enforcerId) || !objectManager.TryGetObject(enforcerId, out Hero enforcer) ||
            enforcer == null)
            return false;

        foreach (var player in playerManager.Players.OrderBy(value => value.ControllerId, StringComparer.Ordinal))
        {
            if (!objectManager.TryGetObject(player.HeroId, out Hero candidate) || candidate == null ||
                !objectManager.TryGetObject(player.MobilePartyId, out MobileParty candidateParty) || candidateParty == null ||
                candidate.PartyBelongedTo != candidateParty || candidateParty.LeaderHero != candidate ||
                enforcer.Clan != candidate.Clan || enforcer.PartyBelongedTo != candidateParty ||
                candidateParty.MemberRoster.GetTroopCount(enforcer.CharacterObject) <= 0 ||
                !playerManager.TryGetPeer(player.ControllerId, out NetPeer candidatePeer))
                continue;
            actor = candidate;
            actorParty = candidateParty;
            peer = candidatePeer;
            return true;
        }
        return false;
    }

    private void SendContractProposal(NetPeer peer, string giverId, string targetId, int type, int reward)
    {
        if (peer == null || !CanUseGameplayRoute(out var config)) return;
        network.Send(peer, new NetworkFourberieContractProposal(
            config.SessionId, serverRevision, giverId, targetId, type, reward));
    }

    private void TrySendPendingContractProposal(NetPeer requestedPeer)
    {
        if (!TryGetContractState(out IDictionary crime, out IDictionary heroes) ||
            !FourberieContractAuthority.CanRespondToProposal(crime, heroes, out _) ||
            !TryFindContractController(heroes, out _, out _, out NetPeer ownerPeer) || ownerPeer != requestedPeer)
            return;
        string giverId = heroes["contractGiver"] as string;
        string targetId = heroes["contractTarget"] as string;
        SendContractProposal(
            ownerPeer,
            giverId,
            targetId,
            Convert.ToInt32(crime[200]),
            Convert.ToInt32(crime[201]));
    }

    private void HandleContractProposal(MessagePayload<NetworkFourberieContractProposal> payload)
    {
        if (!compatible || !ModInformation.IsClient || !configAuthority.TryGetCurrent(out _) || payload.Who is not NetPeer serverPeer ||
            !FourberieSnapshotOriginGuard.IsTrustedServerTransport(serverPeer, localIsClient: true) ||
            !FourberieOperationProtocol.IsProposalShapeValid(payload.What))
            return;
        pendingContractProposal = payload.What;
        GameThread.RunSafe(TryShowContractProposal, context: nameof(FourberieCompatibilityHandler));
    }

    private void TryShowContractProposal()
    {
        NetworkFourberieContractProposal proposal = pendingContractProposal;
        if (proposal == null || proposal.Revision != revisionGate.Revision ||
            !configAuthority.TryGetCurrent(out var config) ||
            !string.Equals(config.SessionId, proposal.SessionId, StringComparison.Ordinal) ||
            !TryGetContractState(out IDictionary crime, out IDictionary heroes) ||
            !FourberieContractAuthority.CanRespondToProposal(crime, heroes, out _) ||
            !string.Equals(heroes["contractGiver"] as string, proposal.GiverId, StringComparison.Ordinal) ||
            !string.Equals(heroes["contractTarget"] as string, proposal.TargetId, StringComparison.Ordinal) ||
            Convert.ToInt32(crime[200]) != proposal.ContractType || Convert.ToInt32(crime[201]) != proposal.Reward ||
            !objectManager.TryGetObject(proposal.GiverId, out Hero giver) || giver == null ||
            !objectManager.TryGetObject(proposal.TargetId, out Hero target) || target?.Clan == null)
            return;

        string key = proposal.SessionId + "|" + proposal.Revision + "|" + proposal.TargetId;
        if (string.Equals(shownContractProposalKey, key, StringComparison.Ordinal)) return;
        shownContractProposalKey = key;

        string work = proposal.ContractType == 0
            ? new TextObject("{=FoSafHou51}We want you to bring death to the {CLAN} clan of {KING}.")
                .SetTextVariable("CLAN", target.Clan.Name)
                .SetTextVariable("KING", target.MapFaction.Name)
                .ToString()
            : new TextObject("{=FoSafHou52}We want you to destabilize the {CLAN} clan of {KING}. Fabricate a scandal, blackmail them, or even incite a rebellion.")
                .SetTextVariable("CLAN", target.Clan.Name)
                .SetTextVariable("KING", target.MapFaction.Name)
                .ToString();
        string body = new TextObject("{=FoSafHou50}A messenger from {KING} has arrived. His realm is seeking some discreet services...")
            .SetTextVariable("KING", giver.MapFaction.Name)
            .ToString() + "\n\n" + work + "\n\n" +
            new TextObject("{=FoSafHou53}We will pay you {PAY}{GOLD_ICON} upon completion.")
                .SetTextVariable("PAY", proposal.Reward)
                .ToString();

        InformationManager.ShowInquiry(new InquiryData(
            new TextObject("{=FoSafHou49}Dirty business").ToString(),
            body,
            true,
            true,
            new TextObject("{=FoSafHou44}Consider the job done. Get those denars ready.").ToString(),
            new TextObject("{=FoSafHou45}Nah, I'm not interested.").ToString(),
            () => SubmitContractProposalResponse(FourberieOperation.AcceptContractProposal),
            () => SubmitContractProposalResponse(FourberieOperation.DeclineContractProposal),
            string.Empty,
            0f,
            null,
            null,
            null),
            true,
            false);
    }

    private void SubmitContractProposalResponse(FourberieOperation operation) =>
        TrySubmit(new FourberieLocalOperation(
            operation, null, null, null, 0, Array.Empty<FourberieLocalTroopSelection>()));

    private void HandleStateRequest(MessagePayload<NetworkRequestFourberieState> payload)
    {
        if (!compatible || !ModInformation.IsServer || payload.Who is not NetPeer peer) return;
        GameThread.RunSafe(
            () =>
            {
                SendSnapshotOrAbort(peer, onlyIfChanged: false);
                TrySendPendingContractProposal(peer);
            },
            context: nameof(FourberieCompatibilityHandler));
    }

    private void HandleState(MessagePayload<NetworkFourberieState> payload)
    {
        // A network command is published with its transport peer as the broker source. A
        // rendered client has one server connection, so requiring that transport identity
        // rejects locally published/forged snapshots without trusting payload fields.
        if (!compatible || !ModInformation.IsClient || payload.Who is not NetPeer serverPeer ||
            !FourberieSnapshotOriginGuard.IsTrustedServerTransport(serverPeer, localIsClient: true))
            return;
        GameThread.RunSafe(
            () =>
            {
                if (TryApplySnapshot(payload.What, out var failure))
                {
                    TryShowContractProposal();
                    SnapshotReadiness = WorkshopSnapshotReadiness.Ready;
                    if (configAuthority.TryGetCurrent(out var config)) SnapshotSessionId = config.SessionId;
                    SnapshotRevision = payload.What.Revision;
                    return;
                }

                Logger.Fatal(
                    "Disconnecting from the Coop server because Fourberie state could not be accepted: {Failure}",
                    failure);
                InformationManager.DisplayMessage(new InformationMessage(
                    "Fourberie compatibility validation failed. The co-op connection was closed to prevent a divergent campaign. " +
                    failure));
                serverPeer.Disconnect();
            },
            context: nameof(FourberieCompatibilityHandler));
    }

    private void HandleStateQueryResult(MessagePayload<NetworkFourberieStateQueryResult> payload)
    {
        if (!compatible || !ModInformation.IsClient || payload?.Who is not NetPeer serverPeer ||
            !configAuthority.IsTrustedServer(serverPeer) ||
            payload.What.Header.Status != AuthorityResultStatus.Accepted || payload.What.Snapshot == null)
            return;

        GameThread.RunSafe(
            () =>
            {
                if (TryApplySnapshot(payload.What.Snapshot, out var failure))
                {
                    stateReady = true;
                    SnapshotReadiness = WorkshopSnapshotReadiness.Ready;
                    if (configAuthority.TryGetCurrent(out var config)) SnapshotSessionId = config.SessionId;
                    SnapshotRevision = payload.What.Snapshot.Revision;
                    TryShowContractProposal();
                    return;
                }
                stateReady = false;
                Logger.Fatal("Disconnecting from the Coop server because Fourberie state could not be accepted: {Failure}",
                    failure);
                serverPeer.Disconnect();
            },
            context: nameof(FourberieCompatibilityHandler));
    }

    private void SendSnapshotOrAbort(NetPeer peer, bool onlyIfChanged)
    {
        if (!FourberieCanonicalState.TryCapture(
                assembly,
                objectManager,
                out var entries,
                out var stateFingerprint,
                out var failure))
        {
            DenyPeerOrAbortSession(peer, "could not capture Fourberie server state: " + failure);
            return;
        }

        var changed = !string.Equals(
            lastPublishedFingerprint,
            stateFingerprint,
            StringComparison.OrdinalIgnoreCase);
        if (!changed)
        {
            if (onlyIfChanged) return;
        }

        var nextRevision = serverRevision;
        if (changed && lastPublishedFingerprint != null) nextRevision++;

        var snapshot = new NetworkFourberieState(
            FourberieCompatibilityManifest.AdapterVersion,
            nextRevision,
            configurationFingerprint,
            stateFingerprint,
            entries);
        if (!FourberieStateCodec.TryValidate(snapshot, out failure))
        {
            DenyPeerOrAbortSession(peer, "captured Fourberie server state was invalid: " + failure);
            return;
        }
        var broadcast = peer == null || changed;
        try
        {
            // If a late-join request discovers state newer than the last broadcast, publish that
            // revision to every peer. Advancing the global fingerprint after replying only to the
            // requester would make the ordinary change detector suppress the update forever for
            // already-connected clients.
            if (broadcast) network.SendAll(snapshot);
            else network.Send(peer, snapshot);
        }
        catch (Exception exception)
        {
            DenyPeerOrAbortSession(
                broadcast ? null : peer,
                "authoritative snapshot publication failed: " + exception.Message);
            return;
        }

        // Publication and the server watermark commit form one operation: a failed send must not
        // make a later retry look unchanged.
        if (changed)
        {
            serverRevision = nextRevision;
            lastPublishedFingerprint = stateFingerprint;
        }
    }

    private bool TryCaptureSnapshot(out NetworkFourberieState snapshot, out string failure)
    {
        snapshot = null;
        if (!FourberieCanonicalState.TryCapture(
                assembly,
                objectManager,
                out var entries,
                out var stateFingerprint,
                out failure))
        {
            failure = "could not capture Fourberie server state: " + failure;
            return false;
        }

        bool changed = !string.Equals(lastPublishedFingerprint, stateFingerprint, StringComparison.OrdinalIgnoreCase);
        long revision = serverRevision;
        if (changed && lastPublishedFingerprint != null) revision++;
        snapshot = new NetworkFourberieState(
            FourberieCompatibilityManifest.AdapterVersion,
            revision,
            configurationFingerprint,
            stateFingerprint,
            entries);
        if (!FourberieStateCodec.TryValidate(snapshot, out failure))
        {
            failure = "captured Fourberie server state was invalid: " + failure;
            return false;
        }

        failure = null;
        return true;
    }

    private bool TryApplySnapshot(NetworkFourberieState snapshot, out string rejection)
    {
        lock (snapshotSync)
        {
            if (!FourberieStateCodec.TryValidate(snapshot, out var failure))
            {
                Logger.Error("Rejected Fourberie snapshot: {Failure}", failure);
                rejection = failure;
                return false;
            }

            if (!string.Equals(
                    configurationFingerprint,
                    snapshot.ConfigurationFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                var message = $"Fourberie configuration mismatch: local {configurationFingerprint}, server {snapshot.ConfigurationFingerprint}. Reinstall the private bundle; state was not applied.";
                Logger.Fatal(message);
                rejection = message;
                return false;
            }

            var decision = revisionGate.Evaluate(snapshot.Revision, snapshot.StateFingerprint);
            if (decision == FourberieRevisionDecision.AlreadyApplied)
            {
                rejection = null;
                return true;
            }
            if (decision != FourberieRevisionDecision.Apply)
            {
                Logger.Error(
                    "Rejected Fourberie snapshot revision {Revision} ({Decision}); local revision is {LocalRevision}",
                    snapshot.Revision,
                    decision,
                    revisionGate.Revision);
                rejection = $"snapshot revision {snapshot.Revision} was {decision} against local revision {revisionGate.Revision}";
                return false;
            }

            if (!FourberieCanonicalState.TryBeginApply(
                    assembly,
                    objectManager,
                    snapshot.Entries,
                    out var transaction,
                    out failure))
            {
                Logger.Error("Could not apply Fourberie snapshot revision {Revision}: {Failure}", snapshot.Revision, failure);
                rejection = failure;
                return false;
            }

            if (!revisionGate.Commit(snapshot.Revision, snapshot.StateFingerprint))
            {
                transaction.TryRollback(out var rollbackFailure);
                Logger.Error(
                    "Fourberie snapshot revision changed while applying; state rollback result: {Rollback}",
                    rollbackFailure ?? "success");
                rejection = "snapshot revision changed while applying" +
                            (rollbackFailure == null ? string.Empty : "; " + rollbackFailure);
                return false;
            }

            transaction.Commit();
            RefreshClientLayout();
            Logger.Debug(
                "Atomically applied Fourberie state revision {Revision} ({Fingerprint})",
                snapshot.Revision,
                snapshot.StateFingerprint);
            rejection = null;
            return true;
        }
    }

    private void RefreshClientLayout()
    {
        if (!ModInformation.IsClient) return;
        try
        {
            Type behavior = assembly.GetType("Fourberie.FourberieBehavior", throwOnError: false, ignoreCase: false);
            object layer = behavior == null ? null : AccessTools.Field(behavior, "_layer")?.GetValue(null);
            if (layer != null)
                AccessTools.Method(layer.GetType(), "UpdateLayout", Type.EmptyTypes)?.Invoke(layer, null);
        }
        catch (Exception exception)
        {
            Logger.Warning(exception, "Fourberie state applied, but the open criminal-enterprise view could not refresh");
        }
    }

    private static void DenyPeerOrAbortSession(NetPeer peer, string failure)
    {
        if (peer != null)
        {
            Logger.Fatal(
                "Disconnecting peer {Peer} because authoritative Fourberie state is unavailable: {Failure}",
                peer.Id,
                failure);
            peer.Disconnect();
            return;
        }

        Logger.Fatal("Aborting the Coop session because authoritative Fourberie state is unavailable: {Failure}", failure);
        throw new InvalidOperationException(
            "The Coop session cannot continue without authoritative Fourberie state: " + failure);
    }

}

internal readonly struct FourberieSnapshotIntent
{
}
