using Common.Util;
using E2E.Tests.Environment;
using E2E.Tests.Util;
using GameInterface.Services.Armies.Patches;
using GameInterface.Services.Entity;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using HarmonyLib;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using Xunit;
using Xunit.Abstractions;

namespace E2E.Tests.Services.Armies;

public class PlayerArmyWaitBehaviorTests : IDisposable
{
    private E2ETestEnvironment TestEnvironment { get; }

    public PlayerArmyWaitBehaviorTests(ITestOutputHelper output)
    {
        TestEnvironment = new E2ETestEnvironment(output);
    }

    public void Dispose()
    {
        TestEnvironment.Dispose();
    }

    [Fact]
    public void ClientArmyWaitMenuTick_PartialArmyRelationship_DoesNotRunUnsafeVanillaTick()
    {
        var client = TestEnvironment.Clients.First();

        client.Call(() =>
        {
            var mainParty = ObjectHelper.SkipConstructor<MobileParty>();
            var army = ObjectHelper.SkipConstructor<Army>();
            mainParty._army = army;
            Campaign.Current.MainParty = mainParty;

            var behavior = ObjectHelper.SkipConstructor<PlayerArmyWaitBehavior>();
            var method = AccessTools.Method(typeof(PlayerArmyWaitBehavior), "ArmyWaitMenuTick");
            Assert.NotNull(method);

            var exception = Record.Exception(() =>
                method.Invoke(behavior, new object[] { null, default(CampaignTime) }));

            Assert.Null(UnwrapInvocationException(exception));
        });
    }

    [Fact]
    public void ClientLeaveArmyOption_StaleBattleResidue_StaysAvailable()
    {
        // "Join an army but never leave": native hides Leave Army whenever the party holds ANY
        // MapEvent reference. A stale, authoritatively-destroyed event (unregistered locally)
        // must not hide the option; a live registered one must.
        var client = TestEnvironment.Clients.First();

        client.Call(() =>
        {
            var mainParty = ObjectHelper.SkipConstructor<MobileParty>();
            mainParty.Party = ObjectHelper.SkipConstructor<PartyBase>();

            Assert.False(PlayerArmyWaitBehaviorPatches.HasLiveBlockingMapEvent(mainParty));

            var staleEvent = ObjectHelper.SkipConstructor<MapEvent>();
            var staleSide = ObjectHelper.SkipConstructor<MapEventSide>();
            AccessTools.Field(typeof(MapEventSide), "_mapEvent").SetValue(staleSide, staleEvent);
            mainParty.Party._mapEventSide = staleSide;

            // Unregistered event = destroyed server-side; only local residue remains.
            Assert.False(PlayerArmyWaitBehaviorPatches.HasLiveBlockingMapEvent(mainParty));
        });

        var liveEventId = TestEnvironment.CreateRegisteredObject<MapEvent>();
        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MapEvent>(liveEventId, out var liveEvent));
            var mainParty = ObjectHelper.SkipConstructor<MobileParty>();
            mainParty.Party = ObjectHelper.SkipConstructor<PartyBase>();
            var side = ObjectHelper.SkipConstructor<MapEventSide>();
            AccessTools.Field(typeof(MapEventSide), "_mapEvent").SetValue(side, liveEvent);
            mainParty.Party._mapEventSide = side;

            Assert.True(PlayerArmyWaitBehaviorPatches.HasLiveBlockingMapEvent(mainParty));
        });
    }

    [Fact]
    public void ClientWaitMenuLeave_RoutesAuthoritativeRemovalAndMirrorsLocally()
    {
        var server = TestEnvironment.Server;
        var client = TestEnvironment.Clients.First();

        var clientPartyId = TestEnvironment.CreateRegisteredObject<MobileParty>();
        string? armyId = null;

        server.Call(() =>
        {
            var kingdom = GameObjectCreator.CreateInitializedObject<Kingdom>();
            var leaderParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
            var army = new Army(kingdom, leaderParty, Army.ArmyTypes.Patrolling);
            Assert.True(server.ObjectManager.TryGetId(army, out armyId));

            Assert.True(server.ObjectManager.TryGetObject<MobileParty>(clientPartyId, out var clientParty));
            army._parties.Add(clientParty);
            clientParty._army = army;
        });

        // Register the client as the controlling player so the server's removal authority
        // guard recognizes the leaver as removing its own party.
        client.Call(() => client.Resolve<IControllerIdProvider>().SetControllerId("PlayerOne"));
        server.Call(() =>
        {
            var playerManager = server.Resolve<IPlayerManager>();
            Assert.True(playerManager.AddPlayer(
                new Player("PlayerOne", heroId: null, clientPartyId, clanId: null, characterObjectId: null)));
            playerManager.SetPeer("PlayerOne", client.NetPeer);
        });

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(clientPartyId, out var clientParty));
            Assert.True(client.ObjectManager.TryGetObject<Army>(armyId, out var army));
            if (!army._parties.Contains(clientParty)) army._parties.Add(clientParty);
            clientParty._army = army;
            Campaign.Current.MainParty = clientParty;

            // Drive the REAL patched consequence, not a hand-published message.
            var behavior = ObjectHelper.SkipConstructor<PlayerArmyWaitBehavior>();
            AccessTools.Method(
                    typeof(PlayerArmyWaitBehavior),
                    nameof(PlayerArmyWaitBehavior.wait_menu_army_leave_on_consequence))
                .Invoke(behavior, new object[] { null });

            // The local mirror applies immediately; the player is out without waiting for the echo.
            Assert.Null(clientParty._army);
            Assert.DoesNotContain(clientParty, army._parties);
        });

        server.Call(() =>
        {
            Assert.True(server.ObjectManager.TryGetObject<Army>(armyId, out var army));
            Assert.True(server.ObjectManager.TryGetObject<MobileParty>(clientPartyId, out var clientParty));
            Assert.DoesNotContain(clientParty, army._parties);
            Assert.Null(clientParty._army);
        });
    }

    private static Exception? UnwrapInvocationException(Exception? exception)
        => exception is TargetInvocationException invocationException
            ? invocationException.InnerException
            : exception;
}
