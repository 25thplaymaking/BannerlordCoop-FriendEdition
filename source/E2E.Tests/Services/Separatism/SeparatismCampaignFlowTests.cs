using Common.Messaging;
using Common.Util;
using Coop.Core.Server.Services.Kingdoms.Messages;
using E2E.Tests.Environment;
using E2E.Tests.Environment.Instance;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.Kingdoms.Messages;
using GameInterface.Services.Separatism;
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
        string createdKingdomId)
    {
        instance.Call(() =>
        {
            var sourceKingdom = Get<Kingdom>(instance, context.SourceKingdomId);
            var rebelClan = Get<Clan>(instance, context.RebelClanId);
            var culture = Get<CultureObject>(instance, context.CultureId);
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
}
