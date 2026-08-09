using Common.Messaging;
using Common.Network;
using E2E.Tests.Environment.Instance;
using E2E.Tests.Services.MapEvents;
using GameInterface.Configuration;
using GameInterface.Services.Entity;
using GameInterface.Services.Players;
using GameInterface.Services.WorkshopMods.Diplomacy;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using Xunit.Abstractions;

namespace E2E.Tests.Services.WorkshopMods.Diplomacy;

/// <summary>
/// The routed "Donate Gold" funnel: intent → ids-only request → peer-derived ownership → verdict.
/// The apply itself cannot run here — the pinned Diplomacy assembly is not loaded in this
/// environment, and <see cref="DiplomacyDonationVerdict.ModuleNotInstalled"/> IS the correct
/// production behaviour for that state — so applied-state convergence is live-smoke scope, and
/// these tests pin everything up to that boundary.
/// </summary>
public sealed class DiplomacyDonateGoldRoutingTests : MapEventTestBase
{
    public DiplomacyDonateGoldRoutingTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public void ClientAttempt_SendsIdsAndAmountOnly()
    {
        var client = Clients.First();
        var player = CreatePartyWithRegisteredLeader();
        var clanId = TestEnvironment.CreateRegisteredObject<Clan>();
        RegisterPlayer(client, player.HeroId, player.MobilePartyId);

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<Hero>(player.HeroId, out var hero));
            Assert.True(client.ObjectManager.TryGetObject<Clan>(clanId, out var clan));
            client.Resolve<IMessageBroker>().Publish(this, new DiplomacyGoldDonationAttempted(hero, clan, 321));
        });

        var sent = Assert.Single(client.NetworkSentMessages.GetMessages<NetworkRequestDiplomacyDonateGold>());
        Assert.Equal(player.HeroId, sent.GiverHeroId);
        Assert.Equal(clanId, sent.ClanId);
        Assert.Equal(321, sent.Amount);
    }

    [Fact]
    public void Server_IgnoresDonationFromHeroThePeerDoesNotOwn()
    {
        var client = Clients.First();
        var owned = CreatePartyWithRegisteredLeader();
        var other = CreatePartyWithRegisteredLeader();
        var clanId = TestEnvironment.CreateRegisteredObject<Clan>();
        RegisterPlayer(client, owned.HeroId, owned.MobilePartyId);

        client.Call(() => client.Resolve<INetwork>().SendAll(
            new NetworkRequestDiplomacyDonateGold(other.HeroId, clanId, 100)));

        // The ownership check fails before the interface is ever consulted.
        Assert.Null(ResolveServerInterface().LastVerdict);
    }

    [Fact]
    public void Server_ValidatesAmountAgainstItsOwnBooksFirst()
    {
        var client = Clients.First();
        var player = CreatePartyWithRegisteredLeader();
        var clanId = TestEnvironment.CreateRegisteredObject<Clan>();
        RegisterPlayer(client, player.HeroId, player.MobilePartyId);
        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(player.HeroId, out var hero));
            hero.Gold = 100;
        });

        client.Call(() => client.Resolve<INetwork>().SendAll(
            new NetworkRequestDiplomacyDonateGold(player.HeroId, clanId, 500)));

        Assert.Equal(DiplomacyDonationVerdict.InvalidAmount, ResolveServerInterface().LastVerdict);
    }

    [Fact]
    public void Server_RefusesWhenOperatorDisabledTheModule()
    {
        var client = Clients.First();
        var player = CreatePartyWithRegisteredLeader();
        var clanId = TestEnvironment.CreateRegisteredObject<Clan>();
        RegisterPlayer(client, player.HeroId, player.MobilePartyId);
        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(player.HeroId, out var hero));
            hero.Gold = 1_000;
        });

        ModOptions saved = ModConfigProvider.ModOptions;
        try
        {
            ModConfigProvider.ModOptions = new ModOptions(new ModOptionsData
            {
                WorkshopModules = new Dictionary<string, bool> { ["Bannerlord.Diplomacy"] = false },
            });

            client.Call(() => client.Resolve<INetwork>().SendAll(
                new NetworkRequestDiplomacyDonateGold(player.HeroId, clanId, 500)));

            Assert.Equal(DiplomacyDonationVerdict.ModuleDisabled, ResolveServerInterface().LastVerdict);
        }
        finally
        {
            ModConfigProvider.ModOptions = saved;
        }
    }

    [Fact]
    public void Server_RefusesOwnedValidRequest_WhilePinnedModIsNotLoaded()
    {
        var client = Clients.First();
        var player = CreatePartyWithRegisteredLeader();
        var clanId = TestEnvironment.CreateRegisteredObject<Clan>();
        RegisterPlayer(client, player.HeroId, player.MobilePartyId);
        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(player.HeroId, out var hero));
            hero.Gold = 1_000;
        });

        client.Call(() => client.Resolve<INetwork>().SendAll(
            new NetworkRequestDiplomacyDonateGold(player.HeroId, clanId, 500)));

        Assert.Equal(DiplomacyDonationVerdict.ModuleNotInstalled, ResolveServerInterface().LastVerdict);
    }

    private DiplomacyDonateGoldInterface ResolveServerInterface() =>
        Assert.IsType<DiplomacyDonateGoldInterface>(Server.Resolve<IDiplomacyDonateGoldInterface>());

    private (string HeroId, string MobilePartyId) CreatePartyWithRegisteredLeader()
    {
        var mobilePartyId = TestEnvironment.CreateRegisteredObject<MobileParty>();
        string heroId = null;
        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var party));
            Assert.NotNull(party.LeaderHero);
            Assert.True(Server.ObjectManager.TryGetId(party.LeaderHero, out heroId));
        });
        return (heroId, mobilePartyId);
    }

    private void RegisterPlayer(EnvironmentInstance client, string heroId, string mobilePartyId)
    {
        const string controllerId = "PlayerOne";
        client.Resolve<IControllerIdProvider>().SetControllerId(controllerId);
        RegisterAsPlayerParty(controllerId, heroId, mobilePartyId);
        Server.Resolve<IPlayerManager>().SetPeer(controllerId, client.NetPeer);
    }
}
