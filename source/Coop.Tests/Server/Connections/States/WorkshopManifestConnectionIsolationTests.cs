using Autofac;
using Common.Messaging;
using Coop.Core.Server.Connections;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Connections.States;
using Coop.Tests.Mocks;
using GameInterface.Configuration;
using GameInterface.Services.Modules;
using GameInterface.Services.Modules.Validators;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.WorkshopMods.Core;
using LiteNetLib;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Library;
using Xunit;
using Xunit.Abstractions;

namespace Coop.Tests.Server.Connections.States;

public sealed class WorkshopManifestConnectionIsolationTests
{
    private readonly ServerTestComponent component;

    public WorkshopManifestConnectionIsolationTests(ITestOutputHelper output) =>
        component = new ServerTestComponent(output);

    [Fact]
    public void MismatchedPeer_DoesNotPoisonCompatibleLateJoinerGate()
    {
        TestNetwork network = component.TestNetwork;
        NetPeer mismatchedPeer = network.CreatePeer();
        NetPeer lateJoinPeer = network.CreatePeer();
        ModuleInfo[] modules =
        {
            new("Native", isOfficial: true, isDlc: false, version: ApplicationVersion.FromString("v1.4.3")),
            new("Coop", isOfficial: false, isDlc: false, version: ApplicationVersion.FromString("v0.1.0")),
        };
        var moduleProvider = new StaticModuleProvider(modules);
        var manifestProvider = new StaticManifestProvider(CreateManifest(WorkshopPeerRole.Server));

        ResolveCharacterState mismatch = CreateState(mismatchedPeer, moduleProvider, manifestProvider);
        ResolveCharacterState lateJoin = CreateState(lateJoinPeer, moduleProvider, manifestProvider);

        mismatch.Handle_ModuleVersionsValidate(new MessagePayload<NetworkModuleVersionsValidate>(
            mismatchedPeer,
            new NetworkModuleVersionsValidate(modules, CreateManifest(
                WorkshopPeerRole.Client,
                mismatchModuleId: "Bannerlord.Diplomacy"))));
        lateJoin.Handle_ModuleVersionsValidate(new MessagePayload<NetworkModuleVersionsValidate>(
            lateJoinPeer,
            new NetworkModuleVersionsValidate(modules, CreateManifest(WorkshopPeerRole.Client))));

        Assert.False(mismatch.WorkshopHandshakeValidated);
        Assert.True(lateJoin.WorkshopHandshakeValidated);
        Assert.False(Assert.Single(network.GetPeerMessages(mismatchedPeer)
            .OfType<NetworkModuleVersionsValidated>()).Matches);
        Assert.True(Assert.Single(network.GetPeerMessages(lateJoinPeer)
            .OfType<NetworkModuleVersionsValidated>()).Matches);

        mismatch.Dispose();
        lateJoin.Dispose();
    }

    [Fact]
    public void UnpreparedServerManifest_DeniesJoin_WithoutDiscoveryOrHashingOnConnectionThread()
    {
        TestNetwork network = component.TestNetwork;
        NetPeer peer = network.CreatePeer();
        ModuleInfo[] modules =
        {
            new("Native", isOfficial: true, isDlc: false, version: ApplicationVersion.FromString("v1.4.3")),
            new("Coop", isOfficial: false, isDlc: false, version: ApplicationVersion.FromString("v0.1.0")),
        };
        var provider = new UnavailableManifestProvider();
        ResolveCharacterState state = CreateState(peer, new StaticModuleProvider(modules), provider);

        state.Handle_ModuleVersionsValidate(new MessagePayload<NetworkModuleVersionsValidate>(
            peer,
            new NetworkModuleVersionsValidate(modules, CreateManifest(WorkshopPeerRole.Client))));

        NetworkModuleVersionsValidated response = Assert.Single(
            network.GetPeerMessages(peer).OfType<NetworkModuleVersionsValidated>());
        Assert.False(response.Matches);
        Assert.Contains("still in progress", response.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(state.WorkshopHandshakeValidated);
        Assert.Equal(1, provider.LookupCount);

        state.Dispose();
    }

    [Fact]
    public void CharacterResolution_RequiresEchoOfAcceptedHostConfig()
    {
        TestNetwork network = component.TestNetwork;
        NetPeer missingAckPeer = network.CreatePeer();
        NetPeer acknowledgedPeer = network.CreatePeer();
        ModuleInfo[] modules =
        {
            new("Native", true, false, ApplicationVersion.FromString("v1.4.3")),
            new("Coop", false, false, ApplicationVersion.FromString("v0.1.0")),
        };
        var moduleProvider = new StaticModuleProvider(modules);
        var manifestProvider = new StaticManifestProvider(CreateManifest(WorkshopPeerRole.Server));
        ResolveCharacterState missingAck = CreateState(missingAckPeer, moduleProvider, manifestProvider);
        ResolveCharacterState acknowledged = CreateState(acknowledgedPeer, moduleProvider, manifestProvider);

        missingAck.Handle_ModuleVersionsValidate(new MessagePayload<NetworkModuleVersionsValidate>(
            missingAckPeer,
            new NetworkModuleVersionsValidate(modules, CreateManifest(WorkshopPeerRole.Client))));
        acknowledged.Handle_ModuleVersionsValidate(new MessagePayload<NetworkModuleVersionsValidate>(
            acknowledgedPeer,
            new NetworkModuleVersionsValidate(modules, CreateManifest(WorkshopPeerRole.Client))));
        ModConfigSnapshot accepted = Assert.Single(network.GetPeerMessages(acknowledgedPeer)
            .OfType<NetworkModuleVersionsValidated>()).HostModConfig;

        missingAck.Handle_ClientValidate(new MessagePayload<NetworkClientValidate>(
            missingAckPeer,
            new NetworkClientValidate("controller")));
        acknowledged.Handle_ClientValidate(new MessagePayload<NetworkClientValidate>(
            acknowledgedPeer,
            new NetworkClientValidate("controller", accepted)));

        Assert.Equal(ConnectionState.ShutdownRequested, missingAckPeer.ConnectionState);
        Assert.NotEqual(ConnectionState.ShutdownRequested, acknowledgedPeer.ConnectionState);

        missingAck.Dispose();
        acknowledged.Dispose();
    }

    private ResolveCharacterState CreateState(
        NetPeer peer,
        IModuleInfoProvider moduleProvider,
        IWorkshopManifestProvider manifestProvider)
    {
        var logic = new Mock<IConnectionLogic>();
        logic.SetupGet(value => value.Peer).Returns(peer);
        var hostConfig = new ModConfigSnapshot(
            Guid.NewGuid().ToString("N"),
            1,
            new ModOptions(new ModOptionsData()),
            birthAndDeathEnabled: true);
        var configAuthority = new Mock<IModConfigAuthority>();
        configAuthority.Setup(value => value.TryGetCurrent(out hostConfig)).Returns(true);
        return new ResolveCharacterState(
            logic.Object,
            component.Container.Resolve<IMessageBroker>(),
            component.TestNetwork,
            component.Container.Resolve<IModuleValidator>(),
            component.Container.Resolve<IPlayerManager>(),
            component.Container.Resolve<IPlayerPartyRestorer>(),
            component.Container.Resolve<IObjectManager>(),
            moduleProvider,
            new Mock<IExistingPlayerSender>().Object,
            manifestProvider,
            configAuthority.Object);
    }

    private static WorkshopCompatibilityManifest CreateManifest(
        WorkshopPeerRole role,
        string mismatchModuleId = null)
    {
        var catalog = new FriendEditionWorkshopModuleCatalog();
        return new WorkshopCompatibilityManifest(role, catalog.Modules.Select(module =>
            new WorkshopCompatibilityManifestEntry(
                module.ModuleId,
                module.WorkshopId,
                module.Version,
                module.Role,
                module.Profile,
                module.ModuleId == mismatchModuleId ? new string('f', 64) : new string('a', 64),
                new string('b', 64),
                managedDistributionComponent: true,
                loadOrder: module.LoadOrder,
                active: role == WorkshopPeerRole.Server
                    ? module.FeatureActiveExpectedOnServer
                    : module.FeatureActiveExpectedOnClient)));
    }

    private sealed class StaticModuleProvider : IModuleInfoProvider
    {
        private readonly IReadOnlyList<ModuleInfo> modules;
        public StaticModuleProvider(IReadOnlyList<ModuleInfo> modules) => this.modules = modules;
        public IEnumerable<ModuleInfo> GetModuleInfos() => modules;
    }

    private sealed class StaticManifestProvider : IWorkshopManifestProvider
    {
        private readonly WorkshopCompatibilityManifest serverManifest;
        public StaticManifestProvider(WorkshopCompatibilityManifest serverManifest) =>
            this.serverManifest = serverManifest;
        public bool EnforceHandshake => true;
        public WorkshopManifestPreparation PrepareManifest(WorkshopPeerRole peerRole) =>
            throw new InvalidOperationException("This connection test uses an already prepared manifest.");
        public WorkshopCompatibilityManifest BuildPreparedManifest(WorkshopManifestPreparation preparation) =>
            throw new InvalidOperationException("This connection test uses an already prepared manifest.");
        public bool TryGetPreparedManifest(
            WorkshopPeerRole peerRole,
            out WorkshopCompatibilityManifest manifest,
            out string unavailableReason)
        {
            manifest = serverManifest;
            unavailableReason = null;
            return true;
        }
    }

    private sealed class UnavailableManifestProvider : IWorkshopManifestProvider
    {
        public bool EnforceHandshake => true;
        public int LookupCount { get; private set; }

        public WorkshopManifestPreparation PrepareManifest(WorkshopPeerRole peerRole) =>
            throw new InvalidOperationException("Connection handler must not run discovery.");

        public WorkshopCompatibilityManifest BuildPreparedManifest(WorkshopManifestPreparation preparation) =>
            throw new InvalidOperationException("Connection handler must not run hashing.");

        public bool TryGetPreparedManifest(
            WorkshopPeerRole peerRole,
            out WorkshopCompatibilityManifest manifest,
            out string unavailableReason)
        {
            LookupCount++;
            manifest = null;
            unavailableReason = "preparation is still in progress";
            return false;
        }
    }
}
