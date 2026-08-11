using Common.Messaging;
using Common.Network;
using Common.Util;
using Coop.Core.Server.Services.Kingdoms.Messages;
using E2E.Tests.Environment;
using E2E.Tests.Environment.Instance;
using GameInterface.Configuration;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.Kingdoms.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Separatism;
using GameInterface.Services.WorkshopMods.Core;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;
using Xunit.Abstractions;

namespace E2E.Tests.Services.Separatism;

public sealed class SeparatismCampaignFlowTests : IDisposable
{
    private E2ETestEnvironment TestEnvironment { get; }
    private EnvironmentInstance Server => TestEnvironment.Server;
    private IEnumerable<EnvironmentInstance> Clients => TestEnvironment.Clients;

    public SeparatismCampaignFlowTests(ITestOutputHelper output)
    {
        TestEnvironment = new E2ETestEnvironment(output);
    }

    public void Dispose() => TestEnvironment.Dispose();

    [Fact]
    public void FallenClanRecruitment_UsesTheAuthenticatedRuler_AndSynchronizesEveryPeer()
    {
        var fixture = CreateFallenClanRecruitmentFixture();
        var request = CreateRecruitmentRequest(fixture, requestId: 1);

        Server.SimulateMessage(fixture.Client.NetPeer, request);

        var result = Assert.Single(
            Server.NetworkSentMessages.GetMessages<NetworkSeparatismRecruitmentResult>());
        Assert.Equal(SeparatismRecruitmentStatus.Accepted, result.Status);
        Assert.Equal(request.RequestId, result.RequestId);
        Assert.Equal(fixture.Context.RebelClanId, result.TargetClanId);
        Assert.True(result.Revision >= request.ExpectedRevision);

        foreach (var instance in new[] { Server }.Concat(Clients))
        {
            instance.Call(() =>
            {
                var kingdom = Get<Kingdom>(instance, fixture.Context.SourceKingdomId);
                var fallenClan = Get<Clan>(instance, fixture.Context.RebelClanId);
                Assert.Same(kingdom, fallenClan.Kingdom);
                Assert.Contains(fallenClan, kingdom.Clans);
            });
        }
    }

    [Fact]
    public void FallenClanRecruitment_ExactDuplicateReplaysTheResultWithoutASecondTransition()
    {
        var fixture = CreateFallenClanRecruitmentFixture();
        var request = CreateRecruitmentRequest(fixture, requestId: 4);

        Server.SimulateMessage(fixture.Client.NetPeer, request);
        Server.SimulateMessage(fixture.Client.NetPeer, request);

        var results = Server.NetworkSentMessages
            .GetMessages<NetworkSeparatismRecruitmentResult>()
            .ToArray();
        Assert.Equal(2, results.Length);
        Assert.All(results, result =>
        {
            Assert.Equal(SeparatismRecruitmentStatus.Accepted, result.Status);
            Assert.Equal(results[0].Revision, result.Revision);
        });

        Server.Call(() =>
        {
            var kingdom = Get<Kingdom>(Server, fixture.Context.SourceKingdomId);
            var fallenClan = Get<Clan>(Server, fixture.Context.RebelClanId);
            Assert.Single(kingdom.Clans.Where(clan => ReferenceEquals(clan, fallenClan)));
        });
    }

    [Fact]
    public void FallenClanRecruitment_RejectsAStaleMembershipRevisionWithoutMutation()
    {
        var fixture = CreateFallenClanRecruitmentFixture();
        var request = CreateRecruitmentRequest(fixture, requestId: 2);
        request = new NetworkRequestSeparatismRecruitment(
            request.SessionId,
            request.RequestId,
            request.ExpectedRevision + 1,
            request.ExpectedKingdomId,
            request.TargetClanId,
            request.TargetHeroId);

        Server.SimulateMessage(fixture.Client.NetPeer, request);

        var result = Assert.Single(
            Server.NetworkSentMessages.GetMessages<NetworkSeparatismRecruitmentResult>());
        Assert.Equal(SeparatismRecruitmentStatus.StaleState, result.Status);
        Server.Call(() => Assert.Null(Get<Clan>(Server, fixture.Context.RebelClanId).Kingdom));
    }

    [Fact]
    public void FallenClanRecruitment_UnmappedPeerCannotChooseAnActorOrMutateTheTarget()
    {
        var fixture = CreateFallenClanRecruitmentFixture(connectPlayer: false);
        var request = CreateRecruitmentRequest(fixture, requestId: 3);

        Server.SimulateMessage(fixture.Client.NetPeer, request);

        var result = Assert.Single(
            Server.NetworkSentMessages.GetMessages<NetworkSeparatismRecruitmentResult>());
        Assert.Equal(SeparatismRecruitmentStatus.Unauthorized, result.Status);
        Server.Call(() => Assert.Null(Get<Clan>(Server, fixture.Context.RebelClanId).Kingdom));
    }

    [Fact]
    public void FallenClanRecruitment_FailedMembershipCommitRestoresTheIndependentClan()
    {
        var fixture = CreateFallenClanRecruitmentFixture();

        Server.Call(() =>
        {
            var membership = new ThrowOnceAfterMoveMembershipState(
                Server.Resolve<IKingdomMembershipState>());
            var service = new SeparatismCampaignService(
                Server.Resolve<IMessageBroker>(),
                Server.Resolve<INetwork>(),
                Server.Resolve<IObjectManager>(),
                membership,
                Server.Resolve<IPlayerManager>(),
                Server.Resolve<IModConfig>());
            var actor = Get<Hero>(Server, fixture.Context.RulerHeroId);
            var target = Get<Clan>(Server, fixture.Context.RebelClanId);
            long originalRevision = target.LastFactionChangeTime.NumTicks;

            Assert.False(service.TryRecruitFallenClan(actor, target));
            Assert.True(membership.Threw);
            Assert.Null(target.Kingdom);
            Assert.Equal(originalRevision, target.LastFactionChangeTime.NumTicks);
            Assert.DoesNotContain(
                target,
                Get<Kingdom>(Server, fixture.Context.SourceKingdomId).Clans);
        });
    }

    [Fact]
    public void ChaosStart_IsServerAuthoritative_AndSynchronizesTheCreatedKingdom()
    {
        var context = CreateChaosContext();
        var client = Clients.First();

        // The behavior is installed on clients for model/UI compatibility, but campaign decisions
        // themselves must never originate there.
        int clientKingdomCount = GetCampaignKingdomCount(client);
        client.Call(() => client.Resolve<ISeparatismCampaignService>().OnNewGameCreated());

        Assert.Equal(clientKingdomCount, GetCampaignKingdomCount(client));
        Assert.Empty(Server.InternalMessages.GetMessages<PlayerKingdomCreated>());
        Assert.Empty(client.InternalMessages.GetMessages<PlayerKingdomCreated>());

        Server.Call(() => Server.Resolve<ISeparatismCampaignService>().OnNewGameCreated());

        var created = Assert.Single(Server.InternalMessages.GetMessages<PlayerKingdomCreated>());
        Assert.Equal(context.RebelClanId, created.ClanId);
        Assert.Equal(context.DormantKingdomId, created.KingdomId);

        var notification = Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkPlayerKingdomCreated>());
        Assert.Equal(created.KingdomId, notification.KingdomId);
        Assert.Equal(created.ClanId, notification.ClanId);

        AssertCreatedKingdomState(Server, context, created.KingdomId);
        foreach (var environmentClient in Clients)
        {
            AssertCreatedKingdomState(environmentClient, context, created.KingdomId);
            Assert.Contains(
                environmentClient.InternalMessages.GetMessages<PlayerKingdomCreated>(),
                message => message.KingdomId == created.KingdomId && message.ClanId == context.RebelClanId);
        }
    }

    [Fact]
    public void ChaosStart_FailedMembershipCommit_RestoresTheOriginalClanAndDormantKingdom()
    {
        var context = CreateChaosContext();

        Server.Call(() =>
        {
            var membership = new ThrowOnceAfterMoveMembershipState(
                Server.Resolve<IKingdomMembershipState>());
            var service = new SeparatismCampaignService(
                Server.Resolve<IMessageBroker>(),
                Server.Resolve<INetwork>(),
                Server.Resolve<IObjectManager>(),
                membership,
                Server.Resolve<IPlayerManager>(),
                Server.Resolve<IModConfig>());

            service.OnNewGameCreated();

            var sourceKingdom = Get<Kingdom>(Server, context.SourceKingdomId);
            var rebelClan = Get<Clan>(Server, context.RebelClanId);
            var dormantKingdom = Get<Kingdom>(Server, context.DormantKingdomId);
            var settlement = Get<Settlement>(Server, context.SettlementId);

            Assert.True(membership.Threw);
            Assert.Same(sourceKingdom, rebelClan.Kingdom);
            Assert.Contains(rebelClan, sourceKingdom.Clans);
            Assert.Contains(settlement, sourceKingdom.Settlements);
            Assert.DoesNotContain(rebelClan, dormantKingdom.Clans);
            Assert.True(dormantKingdom.IsEliminated);
            Assert.Equal(0xFF202020u, rebelClan.Color);
            Assert.Equal(0xFFE0E0E0u, rebelClan.Color2);
        });

        Assert.Empty(Server.InternalMessages.GetMessages<PlayerKingdomCreated>());
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkPlayerKingdomCreated>());
    }

    [Fact]
    public void LordRebellion_IsServerOwned_AndCommitsExactlyOneKingdomTransition()
    {
        var context = CreateChaosContext();
        SetRulerRelation(context, -100);
        var client = Clients.First();

        client.Call(() => client.Resolve<ISeparatismCampaignService>()
            .OnDailyTickClan(Get<Clan>(client, context.RebelClanId)));
        Assert.Empty(Server.InternalMessages.GetMessages<PlayerKingdomCreated>());

        Server.Call(() => Server.Resolve<ISeparatismCampaignService>()
            .OnDailyTickClan(Get<Clan>(Server, context.RebelClanId)));

        var created = Assert.Single(Server.InternalMessages.GetMessages<PlayerKingdomCreated>());
        Assert.Equal(context.RebelClanId, created.ClanId);
        Assert.Equal(context.DormantKingdomId, created.KingdomId);
        Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkPlayerKingdomCreated>());
        AssertCreatedKingdomState(Server, context, created.KingdomId);
        foreach (var environmentClient in Clients)
        {
            AssertCreatedKingdomState(environmentClient, context, created.KingdomId);
        }
    }

    [Fact]
    public void NationalRebellion_UsesTheHostCulturePolicy_AndSynchronizesTheResult()
    {
        var context = CreateChaosContext();
        string nativeCultureId = TestEnvironment.CreateRegisteredObject<CultureObject>();

        foreach (var instance in new[] { Server }.Concat(Clients))
        {
            instance.Call(() =>
            {
                var nativeCulture = Get<CultureObject>(instance, nativeCultureId);
                nativeCulture.Name = new TextObject("Native Culture");
                Get<Clan>(instance, context.RebelClanId).Culture = nativeCulture;
                Get<Settlement>(instance, context.SettlementId).Culture = nativeCulture;
            });
        }

        Server.Call(() =>
        {
            Server.Resolve<IModConfig>().Data.ModOptions.Separatism.MinimalRequiredNumberOfNativeLords = 1;
            Server.Resolve<ISeparatismCampaignService>().OnDailyTick();
        });

        var created = Assert.Single(Server.InternalMessages.GetMessages<PlayerKingdomCreated>());
        Assert.Equal(context.RebelClanId, created.ClanId);
        Assert.Equal(context.DormantKingdomId, created.KingdomId);
        AssertCreatedKingdomState(Server, context, created.KingdomId, nativeCultureId);
        foreach (var environmentClient in Clients)
        {
            AssertCreatedKingdomState(environmentClient, context, created.KingdomId, nativeCultureId);
        }
    }

    [Fact]
    public void AnarchyRebellion_TransfersOnlyTheNeglectedFief_ToOneServerCreatedKingdom()
    {
        var context = CreateChaosContext();

        foreach (var instance in new[] { Server }.Concat(Clients))
        {
            instance.Call(() =>
            {
                var rulerClan = Get<Clan>(instance, context.RulerClanId);
                var rebelClan = Get<Clan>(instance, context.RebelClanId);
                var settlement = Get<Settlement>(instance, context.SettlementId);
                var town = Get<Town>(instance, context.TownId);

                town._ownerClan = rulerClan;
                rulerClan._fiefsCache = new MBList<Town> { town };
                rulerClan._settlementsCache = new MBList<Settlement> { settlement };
                rebelClan._fiefsCache = new MBList<Town>();
                rebelClan._settlementsCache = new MBList<Settlement>();
                settlement.LastVisitTimeOfOwner = -100000f;
            });
        }

        Server.Call(() =>
        {
            var options = Server.Resolve<IModConfig>().Data.ModOptions.Separatism;
            options.CriticalAmountOfFiefsPerSingleClan = 1;
            options.NumberOfDaysAfterOwnerVisitToKeepOrder = 1;
            options.BonusRebelFiefForHighTierClan = false;
            Server.Resolve<ISeparatismCampaignService>()
                .OnDailyTickClan(Get<Clan>(Server, context.RulerClanId));
        });

        var created = Assert.Single(Server.InternalMessages.GetMessages<PlayerKingdomCreated>());
        Assert.Equal(context.RebelClanId, created.ClanId);
        Assert.Equal(context.DormantKingdomId, created.KingdomId);
        Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkPlayerKingdomCreated>());

        foreach (var instance in new[] { Server }.Concat(Clients))
        {
            instance.Call(() =>
            {
                var sourceKingdom = Get<Kingdom>(instance, context.SourceKingdomId);
                var rebelKingdom = Get<Kingdom>(instance, created.KingdomId);
                var rulerClan = Get<Clan>(instance, context.RulerClanId);
                var rebelClan = Get<Clan>(instance, context.RebelClanId);
                var settlement = Get<Settlement>(instance, context.SettlementId);

                Assert.Same(sourceKingdom, rulerClan.Kingdom);
                Assert.Same(rebelKingdom, rebelClan.Kingdom);
                Assert.Same(rebelClan, settlement.OwnerClan);
                Assert.Contains(settlement, rebelKingdom.Settlements);
                Assert.DoesNotContain(settlement, sourceKingdom.Settlements);
            });
        }
    }

    [Fact]
    public void GameLoad_ReconcilesNativeSeparatistState_WithoutCreatingAnotherKingdom()
    {
        var context = CreateChaosContext();
        Server.Call(() => Server.Resolve<ISeparatismCampaignService>().OnNewGameCreated());
        var created = Assert.Single(Server.InternalMessages.GetMessages<PlayerKingdomCreated>());
        Server.InternalMessages.Clear();
        Server.NetworkSentMessages.Clear();

        Server.Call(() =>
        {
            var kingdom = Get<Kingdom>(Server, created.KingdomId);
            kingdom.EncyclopediaText = null;
            kingdom.EncyclopediaTitle = null;
            kingdom.EncyclopediaRulerTitle = null;
            Server.Resolve<ISeparatismCampaignService>().OnGameLoaded();

            Assert.NotNull(kingdom.EncyclopediaText);
            Assert.NotNull(kingdom.EncyclopediaTitle);
            Assert.NotNull(kingdom.EncyclopediaRulerTitle);
            Assert.Same(Get<Clan>(Server, context.RebelClanId), kingdom.RulingClan);
        });

        Assert.Empty(Server.InternalMessages.GetMessages<PlayerKingdomCreated>());
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkPlayerKingdomCreated>());
    }

    [Fact]
    public void DisconnectedControlledClan_RemainsExcludedAcrossLoadReconciliation()
    {
        var context = CreateChaosContext();

        Server.Call(() =>
        {
            var players = Server.Resolve<IPlayerManager>();
            Assert.True(players.AddPlayer(new Player(
                "disconnected-controller",
                context.RebelHeroId,
                "disconnected-party",
                context.RebelClanId,
                "disconnected-character")));
            Assert.True(players.Contains(Get<Clan>(Server, context.RebelClanId)));

            var service = Server.Resolve<ISeparatismCampaignService>();
            service.OnNewGameCreated();
            service.OnGameLoaded();
            service.OnNewGameCreated();

            Assert.Same(
                Get<Kingdom>(Server, context.SourceKingdomId),
                Get<Clan>(Server, context.RebelClanId).Kingdom);
            Assert.True(Get<Kingdom>(Server, context.DormantKingdomId).IsEliminated);
        });

        Assert.Empty(Server.InternalMessages.GetMessages<PlayerKingdomCreated>());
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkPlayerKingdomCreated>());
    }

    [Fact]
    public void DailyCleanup_EliminatesAnEmptyKingdomOnce_AndSynchronizesClients()
    {
        string emptyKingdomId = TestEnvironment.CreateRegisteredObject<Kingdom>();

        foreach (var instance in new[] { Server }.Concat(Clients))
        {
            instance.Call(() =>
            {
                var kingdom = Get<Kingdom>(instance, emptyKingdomId);
                KingdomRegistry.EnsureRuntimeCollections(kingdom);
                kingdom.Name = new TextObject("Empty Kingdom");
                kingdom._isEliminated = false;
                kingdom._clans.Clear();
                if (!Campaign.Current.CampaignObjectManager.Kingdoms.Contains(kingdom))
                {
                    Campaign.Current.CampaignObjectManager.AddKingdom(kingdom);
                }
            });
        }

        Server.Call(() => Server.Resolve<ISeparatismCampaignService>().OnDailyTick());

        var destroyed = Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkDestroyKingdom>());
        Assert.Equal(emptyKingdomId, destroyed.KingdomId);
        foreach (var instance in new[] { Server }.Concat(Clients))
        {
            instance.Call(() => Assert.True(Get<Kingdom>(instance, emptyKingdomId).IsEliminated));
        }

        Server.NetworkSentMessages.Clear();
        Server.Call(() => Server.Resolve<ISeparatismCampaignService>().OnDailyTick());
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkDestroyKingdom>());
    }

    [Fact]
    public void Union_MovesTheSoleRulingClanToTheNearbyAlly_AndEliminatesItsOldKingdom()
    {
        var context = CreateChaosContext();
        var ally = CreateIndependentKingdom("Ally Kingdom", context.CultureId, 1f);
        var enemy = CreateIndependentKingdom("Common Enemy", context.CultureId, 10f);

        foreach (var instance in new[] { Server }.Concat(Clients))
        {
            instance.Call(() =>
            {
                var source = Get<Kingdom>(instance, context.SourceKingdomId);
                var sourceClan = Get<Clan>(instance, context.RulerClanId);
                var unusedClan = Get<Clan>(instance, context.RebelClanId);
                var sourceSettlement = Get<Settlement>(instance, context.SettlementId);
                var sourceTown = Get<Town>(instance, context.TownId);
                var allyKingdom = Get<Kingdom>(instance, ally.KingdomId);
                var enemyKingdom = Get<Kingdom>(instance, enemy.KingdomId);

                source._clans = new MBList<Clan> { sourceClan };
                unusedClan._kingdom = null;
                sourceTown._ownerClan = sourceClan;
                sourceClan._fiefsCache = new MBList<Town> { sourceTown };
                sourceClan._settlementsCache = new MBList<Settlement> { sourceSettlement };
                unusedClan._fiefsCache = new MBList<Town>();
                unusedClan._settlementsCache = new MBList<Settlement>();
                sourceSettlement._position = new CampaignVec2(new Vec2(0f, 0f), true);
                sourceClan._midSettlement = sourceSettlement;
                source._midSettlement = sourceSettlement;

                source._factionsAtWarWith = new MBList<IFaction> { enemyKingdom };
                allyKingdom._factionsAtWarWith = new MBList<IFaction> { enemyKingdom };
                enemyKingdom._factionsAtWarWith = new MBList<IFaction> { source, allyKingdom };
                CharacterRelationManager.SetHeroRelation(sourceClan.Leader, allyKingdom.Leader, 100);
            });
        }

        Server.Call(() =>
        {
            var source = Get<Kingdom>(Server, context.SourceKingdomId);
            var sourceClan = Get<Clan>(Server, context.RulerClanId);
            var allyKingdom = Get<Kingdom>(Server, ally.KingdomId);
            var enemyKingdom = Get<Kingdom>(Server, enemy.KingdomId);

            Assert.Contains(enemyKingdom, FactionHelper.GetEnemyKingdoms(source));
            Assert.Contains(source, FactionHelper.GetEnemyKingdoms(enemyKingdom));
            Assert.Contains(allyKingdom, FactionHelper.GetEnemyKingdoms(enemyKingdom));
            Assert.Contains(enemyKingdom, FactionHelper.GetEnemyKingdoms(allyKingdom));
            Assert.Contains(allyKingdom, SeparatismCampaignService.GetCloseKingdoms(sourceClan));
            Assert.True(sourceClan.Leader.HasGoodRelationWith(allyKingdom.Leader));

            Server.Resolve<ISeparatismCampaignService>().OnDailyTickClan(sourceClan);
        });

        Assert.Empty(Server.InternalMessages.GetMessages<PlayerKingdomCreated>());
        foreach (var instance in new[] { Server }.Concat(Clients))
        {
            instance.Call(() =>
            {
                var source = Get<Kingdom>(instance, context.SourceKingdomId);
                var sourceClan = Get<Clan>(instance, context.RulerClanId);
                var allyKingdom = Get<Kingdom>(instance, ally.KingdomId);

                Assert.True(source.IsEliminated);
                Assert.Same(allyKingdom, sourceClan.Kingdom);
                Assert.Contains(sourceClan, allyKingdom.Clans);
                Assert.DoesNotContain(sourceClan, source.Clans);
            });
        }
    }

    private ChaosContext CreateChaosContext()
    {
        var sourceKingdomId = TestEnvironment.CreateRegisteredObject<Kingdom>();
        var rulerClanId = TestEnvironment.CreateRegisteredObject<Clan>();
        var rebelClanId = TestEnvironment.CreateRegisteredObject<Clan>();
        var rulerHeroId = TestEnvironment.CreateRegisteredObject<Hero>();
        var rebelHeroId = TestEnvironment.CreateRegisteredObject<Hero>();
        var cultureId = TestEnvironment.CreateRegisteredObject<CultureObject>();
        var settlementId = TestEnvironment.CreateRegisteredObject<Settlement>();
        var townId = TestEnvironment.CreateRegisteredObject<Town>();
        var dormantKingdomId = GetNativeStringId<Clan>(Server, rebelClanId) + "_separatist_kingdom";

        var context = new ChaosContext(
            sourceKingdomId,
            rulerClanId,
            rebelClanId,
            rulerHeroId,
            rebelHeroId,
            cultureId,
            settlementId,
            townId,
            dormantKingdomId);

        var instances = new[] { Server }.Concat(Clients).ToArray();
        foreach (var instance in instances)
        {
            instance.CreateRegisteredObject<Kingdom>(dormantKingdomId);
        }

        foreach (var instance in instances)
        {
            ConfigureChaosContext(instance, context);
        }

        return context;
    }

    private FallenClanRecruitmentFixture CreateFallenClanRecruitmentFixture(bool connectPlayer = true)
    {
        var context = CreateChaosContext();
        var client = Clients.First();

        foreach (var instance in new[] { Server }.Concat(Clients))
        {
            instance.Call(() =>
            {
                var kingdom = Get<Kingdom>(instance, context.SourceKingdomId);
                var rulerClan = Get<Clan>(instance, context.RulerClanId);
                var fallenClan = Get<Clan>(instance, context.RebelClanId);

                kingdom._clans = new MBList<Clan> { rulerClan };
                fallenClan._kingdom = null;
                fallenClan._fiefsCache = new MBList<Town>();
                fallenClan._settlementsCache = new MBList<Settlement>();
                fallenClan.LastFactionChangeTime = CampaignTime.Zero;
            });
        }

        string sessionId = null;
        Server.Call(() =>
        {
            var authority = Server.Resolve<IModConfigAuthority>();
            var snapshot = authority.InitializeHost(Server.Resolve<IModConfig>().Data);
            sessionId = snapshot.SessionId;

            var capability = new WorkshopCapability(
                SeparatismRecruitmentHandler.ModuleId,
                SeparatismRecruitmentHandler.Operation,
                enabled: true,
                reason: string.Empty);
            Assert.Equal(
                WorkshopCapabilityApplyResult.Applied,
                Server.Resolve<IWorkshopCapabilityRegistry>().Apply(
                    new WorkshopCapabilitySnapshot(sessionId, revision: 0, new[] { capability })));

            if (!connectPlayer) return;
            var players = Server.Resolve<IPlayerManager>();
            Assert.True(players.AddPlayer(new Player(
                "separatism-ruler",
                context.RulerHeroId,
                string.Empty,
                context.RulerClanId,
                string.Empty)));
            players.SetPeer("separatism-ruler", client.NetPeer);
        });

        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        return new FallenClanRecruitmentFixture(context, client, sessionId);
    }

    private NetworkRequestSeparatismRecruitment CreateRecruitmentRequest(
        FallenClanRecruitmentFixture fixture,
        long requestId)
    {
        long revision = 0;
        Server.Call(() => revision = Get<Clan>(
            Server,
            fixture.Context.RebelClanId).LastFactionChangeTime.NumTicks);

        return new NetworkRequestSeparatismRecruitment(
            fixture.SessionId,
            requestId,
            revision,
            fixture.Context.SourceKingdomId,
            fixture.Context.RebelClanId,
            fixture.Context.RebelHeroId);
    }

    private KingdomFixture CreateIndependentKingdom(string name, string cultureId, float x)
    {
        var fixture = new KingdomFixture(
            TestEnvironment.CreateRegisteredObject<Kingdom>(),
            TestEnvironment.CreateRegisteredObject<Clan>(),
            TestEnvironment.CreateRegisteredObject<Hero>(),
            TestEnvironment.CreateRegisteredObject<Settlement>(),
            TestEnvironment.CreateRegisteredObject<Town>());

        foreach (var instance in new[] { Server }.Concat(Clients))
        {
            instance.Call(() =>
            {
                var kingdom = Get<Kingdom>(instance, fixture.KingdomId);
                var clan = Get<Clan>(instance, fixture.ClanId);
                var hero = Get<Hero>(instance, fixture.HeroId);
                var culture = Get<CultureObject>(instance, cultureId);
                var settlement = Get<Settlement>(instance, fixture.SettlementId);
                var town = Get<Town>(instance, fixture.TownId);

                using (new AllowedThread())
                {
                    KingdomRegistry.EnsureRuntimeCollections(kingdom);
                    kingdom.Name = new TextObject(name);
                    kingdom.InformalName = kingdom.Name;
                    kingdom.Culture = culture;
                    kingdom.Banner = new Banner();
                    kingdom._clans = new MBList<Clan> { clan };
                    kingdom._rulingClan = clan;
                    kingdom._fiefsCache = new MBList<Town> { town };
                    kingdom._townsCache = new MBList<Town> { town };
                    kingdom._settlementsCache = new MBList<Settlement> { settlement };
                    kingdom._midSettlement = settlement;
                    kingdom._isEliminated = false;

                    clan.Name = new TextObject(name + " Clan");
                    clan.Culture = culture;
                    clan.Banner = new Banner();
                    clan._kingdom = kingdom;
                    clan.SetLeader(hero);
                    clan._fiefsCache = new MBList<Town> { town };
                    clan._settlementsCache = new MBList<Settlement> { settlement };
                    clan._midSettlement = settlement;
                    hero.Clan = clan;
                    hero.ChangeState(Hero.CharacterStates.Active);

                    settlement.Party.Settlement = settlement;
                    settlement.Town = town;
                    settlement.Culture = culture;
                    settlement._position = new CampaignVec2(new Vec2(x, 0f), true);
                    town.Owner = settlement.Party;
                    town._ownerClan = clan;
                    town._isCastle = false;
                    town._prosperity = 3000f;

                    if (!Campaign.Current.CampaignObjectManager.Clans.Contains(clan))
                    {
                        Campaign.Current.CampaignObjectManager.AddClan(clan);
                    }
                    if (!Campaign.Current.CampaignObjectManager.Kingdoms.Contains(kingdom))
                    {
                        Campaign.Current.CampaignObjectManager.AddKingdom(kingdom);
                    }
                }
            });
        }

        return fixture;
    }

    private static void ConfigureChaosContext(EnvironmentInstance instance, ChaosContext context)
    {
        instance.Call(() =>
        {
            var sourceKingdom = Get<Kingdom>(instance, context.SourceKingdomId);
            var rulerClan = Get<Clan>(instance, context.RulerClanId);
            var rebelClan = Get<Clan>(instance, context.RebelClanId);
            var rulerHero = Get<Hero>(instance, context.RulerHeroId);
            var rebelHero = Get<Hero>(instance, context.RebelHeroId);
            var culture = Get<CultureObject>(instance, context.CultureId);
            var settlement = Get<Settlement>(instance, context.SettlementId);
            var town = Get<Town>(instance, context.TownId);
            var dormantKingdom = Get<Kingdom>(instance, context.DormantKingdomId);

            using (new AllowedThread())
            {
                BannerManager.Initialize();

                dormantKingdom.StringId = context.DormantKingdomId;
                KingdomRegistry.EnsureRuntimeCollections(dormantKingdom);
                dormantKingdom.Name = new TextObject("Former Rebel Kingdom");
                dormantKingdom._isEliminated = true;

                sourceKingdom.Name = new TextObject("Source Kingdom");
                sourceKingdom.InformalName = sourceKingdom.Name;
                sourceKingdom.Culture = culture;
                sourceKingdom.Banner = new Banner();
                sourceKingdom._activePolicies ??= new MBList<PolicyObject>();
                sourceKingdom._armies ??= new MBList<Army>();
                sourceKingdom._clans = new MBList<Clan> { rulerClan, rebelClan };
                sourceKingdom._unresolvedDecisions ??= new MBList<KingdomDecision>();
                sourceKingdom._factionsAtWarWith ??= new MBList<IFaction>();
                sourceKingdom._alliedKingdoms ??= new MBList<Kingdom>();
                sourceKingdom._rulingClan = rulerClan;

                rulerClan.Name = new TextObject("Ruling Clan");
                rulerClan.Culture = culture;
                rulerClan.Banner = new Banner();
                rulerClan._kingdom = sourceKingdom;
                rulerClan.SetLeader(rulerHero);
                rulerHero.Clan = rulerClan;
                rulerHero.ChangeState(Hero.CharacterStates.Active);

                rebelClan.Name = new TextObject("Rebel Clan");
                rebelClan.Culture = culture;
                rebelClan.Banner = new Banner();
                rebelClan.Color = 0xFF202020;
                rebelClan.Color2 = 0xFFE0E0E0;
                rebelClan._kingdom = sourceKingdom;
                rebelClan.SetLeader(rebelHero);
                rebelHero.Clan = rebelClan;
                rebelHero.ChangeState(Hero.CharacterStates.Active);

                Assert.NotNull(settlement.Party);
                settlement.Party.Settlement = settlement;
                town.Owner = settlement.Party;
                town._ownerClan = rebelClan;
                town._isCastle = false;
                town._prosperity = 5000f;
                settlement.Town = town;
                settlement.Culture = culture;

                rebelClan._fiefsCache = new MBList<Town> { town };
                rebelClan._settlementsCache = new MBList<Settlement> { settlement };
                sourceKingdom._fiefsCache = new MBList<Town> { town };
                sourceKingdom._townsCache = new MBList<Town> { town };
                sourceKingdom._settlementsCache = new MBList<Settlement> { settlement };

                if (!Campaign.Current.CampaignObjectManager.Clans.Contains(rulerClan))
                {
                    Campaign.Current.CampaignObjectManager.AddClan(rulerClan);
                }
                if (!Campaign.Current.CampaignObjectManager.Clans.Contains(rebelClan))
                {
                    Campaign.Current.CampaignObjectManager.AddClan(rebelClan);
                }
                if (!Campaign.Current.CampaignObjectManager.Kingdoms.Contains(sourceKingdom))
                {
                    Campaign.Current.CampaignObjectManager.AddKingdom(sourceKingdom);
                }
                if (!Campaign.Current.CampaignObjectManager.Kingdoms.Contains(dormantKingdom))
                {
                    Campaign.Current.CampaignObjectManager.AddKingdom(dormantKingdom);
                }
            }

            Assert.True(rebelHero.IsAlive);
            Assert.Same(sourceKingdom, rebelClan.Kingdom);
            Assert.Contains(settlement, rebelClan.Settlements);
            Assert.True(dormantKingdom.IsEliminated);
        });
    }

    private static void AssertCreatedKingdomState(
        EnvironmentInstance instance,
        ChaosContext context,
        string createdKingdomId,
        string expectedCultureId = null)
    {
        instance.Call(() =>
        {
            var sourceKingdom = Get<Kingdom>(instance, context.SourceKingdomId);
            var rebelClan = Get<Clan>(instance, context.RebelClanId);
            var culture = Get<CultureObject>(instance, expectedCultureId ?? context.CultureId);
            var settlement = Get<Settlement>(instance, context.SettlementId);
            var createdKingdom = Get<Kingdom>(instance, createdKingdomId);

            Assert.Same(createdKingdom, rebelClan.Kingdom);
            Assert.Same(rebelClan, createdKingdom.RulingClan);
            Assert.Same(culture, createdKingdom.Culture);
            Assert.False(createdKingdom.IsEliminated);
            Assert.Equal(context.DormantKingdomId, createdKingdom.StringId);
            Assert.Equal("Kingdom of Rebel Clan", createdKingdom.Name.ToString());
            Assert.NotNull(createdKingdom.Banner);
            Assert.NotNull(createdKingdom.EncyclopediaText);
            Assert.NotNull(createdKingdom.EncyclopediaTitle);
            Assert.NotNull(createdKingdom.EncyclopediaRulerTitle);
            Assert.Contains(rebelClan, createdKingdom.Clans);
            Assert.Contains(settlement, createdKingdom.Settlements);
            Assert.DoesNotContain(rebelClan, sourceKingdom.Clans);
            Assert.DoesNotContain(settlement, sourceKingdom.Settlements);
            Assert.Contains(createdKingdom, Campaign.Current.CampaignObjectManager.Kingdoms);
        });
    }

    private static int GetCampaignKingdomCount(EnvironmentInstance instance)
    {
        int count = 0;
        instance.Call(() => count = Campaign.Current.CampaignObjectManager.Kingdoms.Count);
        return count;
    }

    private static T Get<T>(EnvironmentInstance instance, string id) where T : class
    {
        Assert.True(instance.ObjectManager.TryGetObject<T>(id, out var value));
        return value;
    }

    private static string GetNativeStringId<T>(EnvironmentInstance instance, string id) where T : MBObjectBase
    {
        string? stringId = null;
        instance.Call(() => stringId = Get<T>(instance, id).StringId);
        Assert.False(string.IsNullOrWhiteSpace(stringId));
        return stringId!;
    }

    private sealed record ChaosContext(
        string SourceKingdomId,
        string RulerClanId,
        string RebelClanId,
        string RulerHeroId,
        string RebelHeroId,
        string CultureId,
        string SettlementId,
        string TownId,
        string DormantKingdomId);

    private sealed record KingdomFixture(
        string KingdomId,
        string ClanId,
        string HeroId,
        string SettlementId,
        string TownId);

    private sealed record FallenClanRecruitmentFixture(
        ChaosContext Context,
        EnvironmentInstance Client,
        string SessionId);

    private sealed class ThrowOnceAfterMoveMembershipState : IKingdomMembershipState
    {
        private readonly IKingdomMembershipState inner;

        public bool Threw { get; private set; }

        public ThrowOnceAfterMoveMembershipState(IKingdomMembershipState inner)
        {
            this.inner = inner;
        }

        public void EnsureClanInKingdom(Kingdom kingdom, Clan clan, bool publishCollectionChanges) =>
            inner.EnsureClanInKingdom(kingdom, clan, publishCollectionChanges);

        public void MoveClanToKingdom(
            Kingdom previousKingdom,
            Kingdom kingdom,
            Clan clan,
            bool publishCollectionChanges,
            bool republishExistingCollections = false)
        {
            inner.MoveClanToKingdom(
                previousKingdom,
                kingdom,
                clan,
                publishCollectionChanges,
                republishExistingCollections);

            bool isSeparatistKingdom = kingdom?.StringId?.EndsWith(
                "_separatist_kingdom",
                StringComparison.Ordinal) == true;
            bool isRecruitmentKingdom = kingdom != null && previousKingdom == null;
            if (Threw || (!isSeparatistKingdom && !isRecruitmentKingdom))
                return;

            Threw = true;
            throw new InvalidOperationException("Injected failure after the authoritative clan move.");
        }
    }

    private void SetRulerRelation(ChaosContext context, int relation)
    {
        foreach (var instance in new[] { Server }.Concat(Clients))
        {
            instance.Call(() => CharacterRelationManager.SetHeroRelation(
                Get<Hero>(instance, context.RebelHeroId),
                Get<Hero>(instance, context.RulerHeroId),
                relation));
        }
    }
}
