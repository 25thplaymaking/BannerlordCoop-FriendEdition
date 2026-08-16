using Common;
using Common.Messaging;
using Common.Network;
using Coop.Core.Client.Services.MobileParties.Messages;
using System.Collections.Generic;
using Coop.Core.Server.Services.ItemRosters.Messages;
using Coop.Core.Server.Services.Stances.Messages;
using Coop.Core.Server.Services.Time.Messages;
using Common.Util;
using Coop.Core.Server.Connections.Messages;
using Coop.Core.Server.Services.MobileParties.Messages;
using E2E.Tests.Environment.Instance;
using E2E.Tests.Services.MapEvents;
using E2E.Tests.Util;
using GameInterface.Services.Entity;
using GameInterface.Configuration;
using GameInterface.Services.CampaignService.Handlers;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.Heroes.Enum;
using GameInterface.Services.Heroes.Interaces;
using GameInterface.Services.MapEventComponents.Messages;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Handlers;
using GameInterface.Services.MapEvents.Messages.Conversation;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.MapEvents.Messages.Start;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.MapEventSides.Messages;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Villages.Commands;
using GameInterface.Services.Villages.Data;
using GameInterface.Services.Villages.Interfaces;
using GameInterface.Services.Villages.Messages;
using HarmonyLib;
using Missions.Messages;
using Moq;
using System.Net;
using System.Reflection;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;
using Xunit.Abstractions;

namespace E2E.Tests.Services.Villages;

public class VillageHostileActionTests : MapEventTestBase
{
    private static int peerPortCounter;

    public VillageHostileActionTests(ITestOutputHelper output) : base(output)
    {
        foreach (var client in Clients)
        {
            client.Call(() => Assert.True(client.Resolve<IModConfigAuthority>().TryBindTrustedServer(
                Server.NetPeer,
                out var failure), failure));
        }
        Server.Call(() => Server.Resolve<LoadModConfigHandler>().Handle_CampaignReady(
            new MessagePayload<CampaignReady>(this, new CampaignReady())));
    }

    [Fact]
    public void ClientRequestRaid_ForControlledVillage_StartsAndApprovesMapEvent()
    {
        var client = Clients.First();
        RegisterPeer(client, "PlayerOne");
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();

        Server.NetworkSentMessages.Clear();

        RequestHostileAction(client, VillageHostileAction.Raid, mobilePartyId, target.SettlementId);

        var started = Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionStarted>().Single();
        Assert.Equal(VillageHostileAction.Raid, started.Action);
        Assert.Equal(mobilePartyId, started.MobilePartyId);
        Assert.Equal(target.SettlementId, started.SettlementId);
        var accepted = Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionResult>().Single();
        Assert.Equal(AuthorityResultStatus.Accepted, accepted.Header.Status);
        Assert.Equal(accepted.Header.SessionId, started.SessionId);
        Assert.Equal(accepted.Header.RequestId, started.AuthorityRequestId);
        Assert.Equal(accepted.Header.CommittedRevision, started.CommittedRevision);

        var result = ConsumeApprovedMapEventStart(mobilePartyId, target.SettlementPartyId, RaidFlags());
        Assert.True(result.Approved);
        Assert.Equal(VillageHostileActionDeniedReason.Invalid, result.Reason);
    }

    [Theory]
    [InlineData(VillageHostileAction.ForceVolunteers)]
    [InlineData(VillageHostileAction.ForceSupplies)]
    public void ClientRequestForceAction_ForControlledVillage_StartsAndApprovesMapEvent(VillageHostileAction action)
    {
        var client = Clients.First();
        RegisterPeer(client, "PlayerOne");
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();

        Server.NetworkSentMessages.Clear();

        RequestHostileAction(client, action, mobilePartyId, target.SettlementId);

        var started = Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionStarted>().Single();
        Assert.Equal(action, started.Action);
        Assert.Equal(mobilePartyId, started.MobilePartyId);
        Assert.Equal(target.SettlementId, started.SettlementId);
        Assert.Equal(AuthorityResultStatus.Accepted,
            Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionResult>().Single().Header.Status);

        var result = ConsumeApprovedMapEventStart(mobilePartyId, target.SettlementPartyId, HostileActionFlags(action));
        Assert.True(result.Approved);
        Assert.Equal(VillageHostileActionDeniedReason.Invalid, result.Reason);
    }

    [Fact]
    public void ClientRequestRaid_ApprovalIsSentOnlyToRequester()
    {
        var requester = Clients.First();
        var otherClient = Clients.Skip(1).First();
        RegisterPeer(requester, "PlayerOne");
        RegisterPeer(otherClient, "PlayerTwo");
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();

        requester.InternalMessages.Clear();
        otherClient.InternalMessages.Clear();

        RequestHostileAction(requester, VillageHostileAction.Raid, mobilePartyId, target.SettlementId);

        var started = requester.InternalMessages.GetMessages<NetworkVillageHostileActionStarted>().Single();
        Assert.Equal(VillageHostileAction.Raid, started.Action);
        Assert.Equal(mobilePartyId, started.MobilePartyId);
        Assert.Equal(target.SettlementId, started.SettlementId);
        Assert.Empty(otherClient.InternalMessages.GetMessages<NetworkVillageHostileActionStarted>());
    }

    [Fact]
    public void ClientRequestRaid_KicksOtherPlayersOutOfVillageBeforeStarting()
    {
        var requester = Clients.First();
        var otherClient = Clients.Skip(1).First();
        RegisterPeer(requester, "PlayerOne");
        RegisterPeer(otherClient, "PlayerTwo");
        var (_, raiderMobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var (_, otherMobilePartyId) = CreatePlayerHeroParty("PlayerTwo");
        var target = CreateVillageTarget();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(raiderMobilePartyId, out var raiderParty));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(otherMobilePartyId, out var otherParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            EnterSettlementAction.ApplyForParty(raiderParty, settlement);
            EnterSettlementAction.ApplyForParty(otherParty, settlement);

            Assert.Same(settlement, raiderParty.CurrentSettlement);
            Assert.Same(settlement, otherParty.CurrentSettlement);
        });

        otherClient.Call(() =>
        {
            Assert.True(otherClient.ObjectManager.TryGetObject<MobileParty>(otherMobilePartyId, out var otherParty));
            Assert.True(otherClient.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            using (new AllowedThread())
            {
                Campaign.Current.MainParty = otherParty;
                EnterSettlementAction.ApplyForParty(otherParty, settlement);
            }

            Assert.Same(otherParty, MobileParty.MainParty);
            Assert.Same(settlement, otherParty.CurrentSettlement);
        });
        SetMockPlayerEncounter(otherClient);
        EnableHeadlessEncounterFinish(otherClient);

        requester.InternalMessages.Clear();
        otherClient.InternalMessages.Clear();
        Server.NetworkSentMessages.Clear();

        RequestHostileAction(requester, VillageHostileAction.Raid, raiderMobilePartyId, target.SettlementId);

        Assert.Empty(requester.InternalMessages.GetMessages<NetworkSettlementEncounterLeaveResult>());
        var leaveResult = Assert.Single(
            otherClient.InternalMessages.GetMessages<NetworkSettlementEncounterLeaveResult>());
        Assert.Equal(otherMobilePartyId, leaveResult.PartyId);
        Assert.Equal(SettlementEncounterLeaveOutcome.Applied, leaveResult.Outcome);
        var compactOtherMobilePartyId = ObjectManager.Compact(otherMobilePartyId, typeof(MobileParty));
        Assert.All(
            Server.NetworkSentMessages.GetMessages<NetworkPartyLeaveSettlement>(),
            message => Assert.Equal(compactOtherMobilePartyId, message.PartyId));
        Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionStarted>());

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(raiderMobilePartyId, out var raiderParty));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(otherMobilePartyId, out var otherParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            Assert.Same(settlement, raiderParty.CurrentSettlement);
            Assert.Null(otherParty.CurrentSettlement);
        });
        AssertHasPlayerEncounter(otherClient, expected: false);
        otherClient.Call(() =>
        {
            Assert.True(otherClient.ObjectManager.TryGetObject<MobileParty>(otherMobilePartyId, out var otherParty));

            Assert.Null(otherParty.CurrentSettlement);
        });
    }

    [Fact]
    public void ClientRequestForceVolunteers_WithZeroHearth_IsRejected()
    {
        var client = Clients.First();
        RegisterPeer(client, "PlayerOne");
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            using (new AllowedThread())
            {
                settlement.Village.Hearth = 0;
            }
        });

        Server.NetworkSentMessages.Clear();

        RequestHostileAction(client, VillageHostileAction.ForceVolunteers, mobilePartyId, target.SettlementId);

        AssertHostileActionRejected("hearth-too-low");
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionStarted>());
    }

    [Theory]
    [InlineData(VillageHostileAction.ForceVolunteers)]
    [InlineData(VillageHostileAction.ForceSupplies)]
    public void ClientRequestForceAction_OnCooldown_IsRejected(VillageHostileAction action)
    {
        var client = Clients.First();
        RegisterPeer(client, "PlayerOne");
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();
        AddServerCooldown(target.SettlementId);

        Server.NetworkSentMessages.Clear();

        RequestHostileAction(client, action, mobilePartyId, target.SettlementId);

        AssertHostileActionRejected("cooldown");
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionStarted>());
    }

    [Fact]
    public void ClientRequestRaid_ForNonVillageSettlement_IsRejected()
    {
        var client = Clients.First();
        RegisterPeer(client, "PlayerOne");
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var settlementId = TestEnvironment.CreateRegisteredObject<Settlement>();

        Server.NetworkSentMessages.Clear();

        RequestHostileAction(client, VillageHostileAction.Raid, mobilePartyId, settlementId);

        AssertHostileActionRejected("non-village-settlement");
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionStarted>());
    }

    [Fact]
    public void ClientRequestRaid_ForNonNormalVillage_IsRejected()
    {
        var client = Clients.First();
        RegisterPeer(client, "PlayerOne");
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Village>(target.VillageId, out var village));
            using (new AllowedThread())
            {
                village.VillageState = Village.VillageStates.Looted;
            }
        });

        Server.NetworkSentMessages.Clear();

        RequestHostileAction(client, VillageHostileAction.Raid, mobilePartyId, target.SettlementId);

        AssertHostileActionRejected("invalid-village-state");
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionStarted>());
    }

    [Fact]
    public void ClientRequestRaid_WithPendingRaidApprovalForSameVillage_IsRejected()
    {
        var firstClient = Clients.First();
        var secondClient = Clients.Skip(1).First();
        RegisterPeer(firstClient, "PlayerOne");
        RegisterPeer(secondClient, "PlayerTwo");
        var (_, firstMobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var (_, secondMobilePartyId) = CreatePlayerHeroParty("PlayerTwo");
        var target = CreateVillageTarget();

        Server.NetworkSentMessages.Clear();

        RequestHostileAction(firstClient, VillageHostileAction.Raid, firstMobilePartyId, target.SettlementId);
        Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionStarted>());

        Server.NetworkSentMessages.Clear();

        RequestHostileAction(secondClient, VillageHostileAction.Raid, secondMobilePartyId, target.SettlementId);

        AssertHostileActionRejected("already-in-map-event");
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionStarted>());
    }

    [Fact]
    public void ClientRequestRaid_ForOwnFactionVillage_IsRejected()
    {
        var client = Clients.First();
        RegisterPeer(client, "PlayerOne");
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();
        var clanId = TestEnvironment.CreateRegisteredObject<Clan>();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetObject<Clan>(clanId, out var clan));

            var boundSettlement = GameObjectCreator.CreateInitializedObject<Settlement>();
            var boundTown = GameObjectCreator.CreateInitializedObject<Town>();

            using (new AllowedThread())
            {
                mobileParty.ActualClan = clan;
                boundSettlement.SetSettlementComponent(boundTown);
                boundTown.OwnerClan = clan;
                settlement.Village.Bound = boundSettlement;
            }

            Assert.Equal(clan, mobileParty.MapFaction);
            Assert.Equal(clan, settlement.MapFaction);
        });

        Server.NetworkSentMessages.Clear();

        RequestHostileAction(client, VillageHostileAction.Raid, mobilePartyId, target.SettlementId);

        AssertHostileActionRejected("own-faction");
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionStarted>());
    }

    [Fact]
    public void ForceVolunteersOutcome_AttackerVictory_AppliesRewardsAndSyncs()
    {
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();
        var troopId = TestEnvironment.CreateRegisteredObject<CharacterObject>();
        var cultureId = TestEnvironment.CreateRegisteredObject<CultureObject>();

        var disabledMethods = MapEventDisabledMethods
            .Append(AccessTools.Method(typeof(SkillLevelingManager), nameof(SkillLevelingManager.OnForceVolunteers)))
            .ToList();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetObject<CharacterObject>(troopId, out var troop));
            Assert.True(Server.ObjectManager.TryGetObject<CultureObject>(cultureId, out var culture));

            culture.BasicTroop = troop;
            settlement.Culture = culture;
            settlement.SettlementHitPoints = 1f;
            settlement.Village.Hearth = 90f;

            var mapEvent = CreateHostileActionMapEvent(mobileParty.Party, settlement.Party, VillageHostileAction.ForceVolunteers);
            mapEvent._battleState = BattleState.AttackerVictory;

            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out var _));

            Server.NetworkSentMessages.Clear();

            Server.Resolve<IVillageHostileActionInterface>().ApplyForceActionOutcome(
                mapEvent,
                VillageHostileAction.ForceVolunteers);
            Server.Resolve<IVillageHostileActionInterface>().ApplyForceActionOutcome(
                mapEvent,
                VillageHostileAction.ForceVolunteers);
        }, disabledMethods);

        AssertCooldownBroadcast(target.SettlementId);
        AssertForceVolunteersOutcome(Server, mobilePartyId, target.SettlementId, target.VillageId, troopId);
        AssertCooldownSynced(Server, target.SettlementId);
        foreach (var client in Clients)
        {
            AssertForceVolunteersOutcome(client, mobilePartyId, target.SettlementId, target.VillageId, troopId);
            AssertCooldownSynced(client, target.SettlementId);
        }
    }

    [Fact]
    public void ForceSuppliesOutcome_WithoutProductions_AppliesGoldAndCooldown()
    {
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();

        var disabledMethods = MapEventDisabledMethods
            .Append(AccessTools.Method(typeof(SkillLevelingManager), nameof(SkillLevelingManager.OnForceSupplies)))
            .ToList();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetObject<Village>(target.VillageId, out var village));

            village.VillageType = null!;
            village.Hearth = 90f;
            settlement.SettlementHitPoints = 1f;
            mobileParty.LeaderHero.Gold = 0;

            var mapEvent = CreateHostileActionMapEvent(mobileParty.Party, settlement.Party, VillageHostileAction.ForceSupplies);
            mapEvent._battleState = BattleState.AttackerVictory;

            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out var _));

            Server.NetworkSentMessages.Clear();

            Server.Resolve<IVillageHostileActionInterface>().ApplyForceActionOutcome(
                mapEvent,
                VillageHostileAction.ForceSupplies);
            Server.Resolve<IVillageHostileActionInterface>().ApplyForceActionOutcome(
                mapEvent,
                VillageHostileAction.ForceSupplies);
        }, disabledMethods);

        AssertCooldownBroadcast(target.SettlementId);
        AssertForceSuppliesGoldAndHitPointsOutcome(Server, mobilePartyId, target.SettlementId);
        AssertCooldownSynced(Server, target.SettlementId);
        foreach (var client in Clients)
        {
            AssertForceSuppliesGoldAndHitPointsOutcome(client, mobilePartyId, target.SettlementId);
            AssertCooldownSynced(client, target.SettlementId);
        }
    }

    [Theory]
    [InlineData(VillageHostileAction.ForceVolunteers)]
    [InlineData(VillageHostileAction.ForceSupplies)]
    public void ForceActionSuccessContinue_ClearsPresentationAndRequestsFinalize(VillageHostileAction action)
    {
        var client = Clients.First();
        var (heroId, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();
        string? mapEventId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            var mapEvent = CreateHostileActionMapEvent(mobileParty.Party, settlement.Party, action);
            mapEvent._battleState = BattleState.AttackerVictory;
            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out mapEventId));
        }, MapEventDisabledMethods);

        Assert.NotNull(mapEventId);
        client.NetworkSentMessages.Clear();
        using var menuSwitchRecorder = new GameMenuSwitchRecorder();

        var disabledMethods = MapEventDisabledMethods
            // Keep the finalize request recorded on the client without synchronously destroying the
            // event while the patched consequence is still unwinding.
            .Append(AccessTools.Method(
                typeof(E2E.Tests.Environment.TestNetworkRouter),
                nameof(E2E.Tests.Environment.TestNetworkRouter.SendAll),
                new[] { typeof(LiteNetLib.NetPeer), typeof(IMessage) }))
            .ToList();

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<Hero>(heroId, out var hero));
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(client.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(client.ObjectManager.TryGetObject<MapEvent>(mapEventId!, out var mapEvent));

            using (new AllowedThread())
            {
                Campaign.Current.MainParty = mobileParty;
                hero.PartyBelongedTo = mobileParty;
                mobileParty.CurrentSettlement = settlement;
            }
            mapEvent._battleState = BattleState.AttackerVictory;

            var encounter = ObjectHelper.SkipConstructor<PlayerEncounter>();
            encounter._mapEvent = mapEvent;
            encounter.ForceVolunteers = action == VillageHostileAction.ForceVolunteers;
            encounter.ForceSupplies = action == VillageHostileAction.ForceSupplies;
            Campaign.Current.PlayerEncounter = encounter;

            var consequenceName = action == VillageHostileAction.ForceVolunteers
                ? "village_force_volunteers_ended_successfully_on_consequence"
                : "village_force_supplies_ended_successfully_on_consequence";
            var consequence = AccessTools.Method(typeof(VillageHostileActionCampaignBehavior), consequenceName);
            var behavior = ObjectHelper.SkipConstructor<VillageHostileActionCampaignBehavior>();
            var args = new MenuCallbackArgs((MenuContext)null, null);

            consequence.Invoke(behavior, new object[] { args });

            Assert.Equal(GameMenuOption.LeaveType.Leave, args.optionLeaveType);
            Assert.False(encounter.ForceVolunteers);
            Assert.False(encounter.ForceSupplies);
        }, disabledMethods);

        Assert.Equal(new[] { "village" }, menuSwitchRecorder.SwitchesFor(client));
        var finalize = client.NetworkSentMessages.GetMessages<NetworkMapEventFinalizeAttempted>().Single();
        Assert.Equal(mapEventId, finalize.MapEventId);
    }

    [Fact]
    public void ClientRequestRaid_WithForgedRequesterParty_IsRejected()
    {
        var client = Clients.First();
        RegisterPeer(client, "PlayerOne");
        CreatePlayerHeroParty("PlayerOne");
        var (_, forgedMobilePartyId) = CreatePlayerHeroParty("PlayerTwo");
        var target = CreateVillageTarget();

        Server.NetworkSentMessages.Clear();

        RequestHostileAction(client, VillageHostileAction.Raid, forgedMobilePartyId, target.SettlementId);

        AssertHostileActionRejected("invalid-requester");
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionStarted>());
    }

    [Fact]
    public void ClientRequestRaid_WithInactiveRequesterParty_IsRejected()
    {
        var client = Clients.First();
        RegisterPeer(client, "PlayerOne");
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            using (new AllowedThread())
            {
                mobileParty.IsActive = false;
            }
        });

        Server.NetworkSentMessages.Clear();

        RequestHostileAction(client, VillageHostileAction.Raid, mobilePartyId, target.SettlementId);

        AssertHostileActionRejected("invalid-requester");
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionStarted>());
    }

    [Fact]
    public void HostileMapEventCreation_WithoutApproval_IsRejected()
    {
        var client = Clients.First();
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();
        var attackerPartyId = GetPartyBaseId(mobilePartyId);

        Server.NetworkSentMessages.Clear();

        var header = CreateMapEventRequestHeader(1);
        client.Call(() => client.Resolve<INetwork>().SendAll(new NetworkRequestCreateMapEvent(
            header,
            attackerPartyId,
            target.SettlementPartyId,
            RaidFlags(),
            null)));

        Assert.Null(Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkMapEventCreated>()).MapEventId);
    }

    [Fact]
    public void HostileMapEventCreation_DoesNotConsumeApprovalBeforeItsRoutePublication()
    {
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();
        var first = false;
        var second = false;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetObject<PartyBase>(target.SettlementPartyId, out var defender));
            var hostileActions = Server.Resolve<IVillageHostileActionInterface>();

            hostileActions.ApproveMapEventStart(mobileParty.Party, settlement, VillageHostileAction.Raid);
            first = hostileActions.TryConsumeApprovedMapEventStart(
                mobileParty.Party, defender, RaidFlags(), out var firstReason);
            Assert.Equal(VillageHostileActionDeniedReason.NotApproved, firstReason);

            Assert.True(hostileActions.MarkApprovedMapEventStartPublished(
                mobileParty.Party, settlement, VillageHostileAction.Raid));
            second = hostileActions.TryConsumeApprovedMapEventStart(
                mobileParty.Party, defender, RaidFlags(), out var secondReason);
            Assert.Equal(VillageHostileActionDeniedReason.Invalid, secondReason);
        });

        Assert.False(first);
        Assert.True(second);
    }

    [Fact]
    public void FieldMapEventCreation_OverlappingRequest_JoinsActiveBattleAndRejectsStaleBattleId()
    {
        var firstClient = Clients.First();
        var secondClient = Clients.Skip(1).First();
        var (_, firstPlayerId) = CreatePlayerHeroParty("PlayerOne");
        var (_, secondPlayerId) = CreatePlayerHeroParty("PlayerTwo");
        var aiId = TestEnvironment.CreateRegisteredObject<MobileParty>();
        firstClient.Resolve<IControllerIdProvider>().SetControllerId("PlayerOne");
        secondClient.Resolve<IControllerIdProvider>().SetControllerId("PlayerTwo");
        Server.Resolve<IPlayerManager>().SetPeer("PlayerOne", firstClient.NetPeer);
        Server.Resolve<IPlayerManager>().SetPeer("PlayerTwo", secondClient.NetPeer);
        var firstPartyId = GetPartyBaseId(firstPlayerId);
        var secondPartyId = GetPartyBaseId(secondPlayerId);
        var aiPartyId = GetPartyBaseId(aiId);
        var playerClanId = TestEnvironment.CreateRegisteredObject<Clan>();
        var aiClanId = TestEnvironment.CreateRegisteredObject<Clan>();
        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(firstPlayerId, out var firstPlayer));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(secondPlayerId, out var secondPlayer));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(aiId, out var ai));
            Assert.True(Server.ObjectManager.TryGetObject<Clan>(playerClanId, out var playerClan));
            Assert.True(Server.ObjectManager.TryGetObject<Clan>(aiClanId, out var aiClan));
            firstPlayer.ActualClan = secondPlayer.ActualClan = playerClan;
            ai.ActualClan = aiClan;
            VillageHostileFactionStanceHelper.ApplyWarStance(playerClan, aiClan);
        });
        Server.NetworkSentMessages.Clear();

        RequestConversation(firstClient, firstPartyId, aiPartyId);
        RequestConversation(secondClient, secondPartyId, aiPartyId);
        Server.NetworkSentMessages.Clear();

        var firstHeader = CreateMapEventRequestHeader(1);
        firstClient.Call(() => firstClient.Resolve<INetwork>().SendAll(
            new NetworkRequestCreateMapEvent(firstHeader, firstPartyId, aiPartyId, default, null)), MapEventDisabledMethods);
        var firstReply = Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkMapEventCreated>());
        Server.NetworkSentMessages.Clear();
        bool? reservationPrecededJoinCommit = null;
        Server.Resolve<IMessageBroker>().Subscribe<BattleJoinAccepted>(payload =>
        {
            if (payload.What.ControllerId != "PlayerTwo" || reservationPrecededJoinCommit != null)
                return;

            Assert.True(Server.ObjectManager.TryGetObject<PartyBase>(secondPartyId, out var secondParty));
            reservationPrecededJoinCommit = secondParty.MapEventSide == null;
        });

        var overlapHeader = CreateMapEventRequestHeader(2);
        secondClient.Call(() => secondClient.Resolve<INetwork>().SendAll(
            new NetworkRequestCreateMapEvent(
                overlapHeader,
                secondPartyId,
                aiPartyId,
                default,
                firstReply.MapEventId)), MapEventDisabledMethods);
        Assert.True(reservationPrecededJoinCommit);
        var secondReply = Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkMapEventCreated>());
        var pending = Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkMapEventPartyPending>());
        Assert.Equal(firstReply.MapEventId, pending.MapEventId);
        Assert.Equal(secondPartyId, pending.PartyId);
        var overlapMessages = Server.NetworkSentMessages.Messages.ToList();
        var pendingIndex = overlapMessages.FindIndex(message => message is NetworkMapEventPartyPending);
        Assert.True(pendingIndex < overlapMessages.FindIndex(message => message is NetworkAddBattleParty) &&
            pendingIndex < overlapMessages.FindIndex(message => message is NetworkMapEventCreated));
        Assert.Equal(firstReply.MapEventId, secondReply.MapEventId);

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(firstReply.MapEventId, out var mapEvent));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(secondPlayerId, out var secondPlayer));
            Assert.Same(mapEvent, secondPlayer.MapEvent);
            Assert.Same(mapEvent.AttackerSide, secondPlayer.MapEventSide);
        });

        DestroyServerMapEvent(firstReply.MapEventId);
        RequestConversation(secondClient, secondPartyId, aiPartyId);
        Server.NetworkSentMessages.Clear();

        var staleHeader = CreateMapEventRequestHeader(3);
        secondClient.Call(() => secondClient.Resolve<INetwork>().SendAll(
            new NetworkRequestCreateMapEvent(
                staleHeader,
                secondPartyId,
                aiPartyId,
                default,
                firstReply.MapEventId)), MapEventDisabledMethods);

        Assert.Null(Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkMapEventCreated>()).MapEventId);
        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(secondPlayerId, out var secondPlayer));
            Assert.Null(secondPlayer.MapEvent);
        });
    }

    [Theory]
    [InlineData(VillageHostileAction.Raid, true, false, false)]
    [InlineData(VillageHostileAction.ForceVolunteers, false, true, false)]
    [InlineData(VillageHostileAction.ForceSupplies, false, false, true)]
    public void BeginHostileActionPresentation_RequestsAuthoritativeMapEvent(
        VillageHostileAction action,
        bool expectedForceRaid,
        bool expectedForceVolunteers,
        bool expectedForceSupplies)
    {
        var client = Clients.First();
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();
        var attackerPartyId = GetPartyBaseId(mobilePartyId);
        SetMapEventCreationTimeout(client, TimeSpan.FromMilliseconds(1));

        var disabledMethods = MapEventDisabledMethods
            .Append(AccessTools.Method(typeof(GameMenu), nameof(GameMenu.SwitchToMenu), new[] { typeof(string) }))
            .Append(AccessTools.Method(
                typeof(E2E.Tests.Environment.TestNetworkRouter),
                nameof(E2E.Tests.Environment.TestNetworkRouter.SendAll),
                new[] { typeof(LiteNetLib.NetPeer), typeof(IMessage) }))
            .ToList();

        client.NetworkSentMessages.Clear();
        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(client.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            var encounter = ObjectHelper.SkipConstructor<PlayerEncounter>();
            encounter._attackerParty = mobileParty.Party;
            encounter._defenderParty = settlement.Party;
            Campaign.Current.PlayerEncounter = encounter;

            client.Resolve<IVillageHostileActionInterface>().BeginHostileActionPresentation(action);
        }, disabledMethods);

        var requests = client.NetworkSentMessages.GetMessages<NetworkRequestCreateMapEvent>().ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(requests[0].AuthorityRequestId, requests[1].AuthorityRequestId);
        var request = requests[0];
        Assert.Equal(attackerPartyId, request.AttackerId);
        Assert.Equal(target.SettlementPartyId, request.DefenderId);
        Assert.Equal(expectedForceRaid, request.ForceRaid);
        Assert.Equal(expectedForceVolunteers, request.ForceVolunteers);
        Assert.Equal(expectedForceSupplies, request.ForceSupplies);
    }

    [Fact]
    public void HostileMapEventCreation_WithMultipleHostileFlags_IsRejectedEvenWhenApproved()
    {
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();

        ApproveMapEventStart(mobilePartyId, target.SettlementId, VillageHostileAction.Raid);

        var flags = new BattleCreationFlags(
            forceRaid: true,
            forceSallyOut: false,
            forceVolunteers: true,
            forceSupplies: false,
            isSallyOutAmbush: false,
            forceBlockadeAttack: false,
            forceBlockadeSallyOutAttack: false,
            forceHideoutSendTroops: false);

        var result = ConsumeApprovedMapEventStart(mobilePartyId, target.SettlementPartyId, flags);
        Assert.False(result.Approved);
        Assert.Equal(VillageHostileActionDeniedReason.Invalid, result.Reason);

        var retry = ConsumeApprovedMapEventStart(mobilePartyId, target.SettlementPartyId, RaidFlags());
        Assert.False(retry.Approved);
        Assert.Equal(VillageHostileActionDeniedReason.NotApproved, retry.Reason);
        AssertCanStartHostileAction(mobilePartyId, target.SettlementId, VillageHostileAction.Raid);
    }

    [Theory]
    [InlineData(VillageHostileAction.Raid, Village.VillageStates.BeingRaided, typeof(RaidEventComponent))]
    [InlineData(VillageHostileAction.ForceVolunteers, Village.VillageStates.ForcedForVolunteers, typeof(ForceVolunteersEventComponent))]
    [InlineData(VillageHostileAction.ForceSupplies, Village.VillageStates.ForcedForSupplies, typeof(ForceSuppliesEventComponent))]
    public void ServerCreatesHostileActionMapEvent_ClientsReceiveComponentAndVillageState(
        VillageHostileAction action,
        Village.VillageStates expectedVillageState,
        Type expectedComponentType)
    {
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();
        string? mapEventId = null;
        string? componentId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            var mapEvent = CreateHostileActionMapEvent(mobileParty.Party, settlement.Party, action);

            Assert.NotNull(mapEvent);
            Assert.NotNull(mapEvent.Component);
            Assert.IsType(expectedComponentType, mapEvent.Component);
            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out mapEventId));
            Assert.True(Server.ObjectManager.TryGetId(mapEvent.Component, out componentId));
        }, MapEventDisabledMethods);

        Assert.NotNull(mapEventId);
        Assert.NotNull(componentId);
        AssertHostileActionMapEvent(Server, mapEventId!, componentId!, expectedComponentType, target.VillageId, expectedVillageState);
        foreach (var client in Clients)
        {
            AssertHostileActionMapEvent(client, mapEventId!, componentId!, expectedComponentType, target.VillageId, expectedVillageState);
        }
    }

    [Fact]
    public void RaidEventUpdate_LootingPhase_AppliesProgressAndSyncs()
    {
        var (heroId, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();
        var nativeItemStringId = "grain";
        string? itemId = null;
        RemoveNativeItemObjectFromObjectManagers(nativeItemStringId);
        var villageTypeId = TestEnvironment.CreateRegisteredObject<VillageType>();
        string? mapEventId = null;
        string? componentId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(heroId, out var hero));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetObject<Village>(target.VillageId, out var village));
            var item = GetNativeItemObject(nativeItemStringId);
            Assert.NotNull(item);
            Assert.False(Server.ObjectManager.TryGetId(item, out _));
            Assert.True(Server.ObjectManager.TryGetObject<VillageType>(villageTypeId, out var villageType));

            using (new AllowedThread())
            {
                mobileParty.MemberRoster.AddToCounts(hero.CharacterObject, 100);
                hero.PartyBelongedTo = mobileParty;
                hero.Gold = 0;
                settlement.ItemRoster.AddToCounts(new EquipmentElement(item), 3);
                villageType._productions = new MBList<(ItemObject, float)>
                {
                    (item, 120f),
                };
                village.VillageType = villageType;
                settlement.SettlementHitPoints = 1f;
                settlement.Village.Hearth = 100f;
                Campaign.Current.MapTimeTracker._deltaTimeInTicks = CampaignTime.Hours(10f).NumTicks;
            }

            var mapEvent = CreateHostileActionMapEvent(mobileParty.Party, settlement.Party, VillageHostileAction.Raid);
            Assert.NotNull(mapEvent);
            Assert.IsType<RaidEventComponent>(mapEvent.Component);
            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out mapEventId));
            Assert.True(Server.ObjectManager.TryGetId(mapEvent.Component, out componentId));

            var component = (RaidEventComponent)mapEvent.Component;
            var wasFinished = false;
            for (int i = 0; i < 25 && component.RaidDamage <= 0; i++)
            {
                component.Update(ref wasFinished);
            }

            component._raidProductionRewards[item] = 1f;
            MessageBroker.Instance.Publish(component, new RaidProductionRewardsUpdated(component));
            Assert.True(Server.ObjectManager.TryGetId(item, out itemId));

            Assert.True(mapEvent.WasEverInLootingPhase);
            Assert.True(component.RaidDamage > 0);
            Assert.True(
                settlement.SettlementHitPoints < 1f,
                $"settlement hit points should replicate raid damage; actual={settlement.SettlementHitPoints:R}");
            Assert.True(settlement.Village.Hearth < 100f);
            Assert.True(GetItemAmount(component._raidProductionRewards, item) > 0);
        }, MapEventDisabledMethods);

        Assert.NotNull(mapEventId);
        Assert.NotNull(componentId);
        Assert.NotNull(itemId);
        AssertRaidProgressOutcome(Server, mapEventId!, componentId!, mobilePartyId, target.SettlementId, target.VillageId, itemId);
        foreach (var client in Clients)
        {
            AssertRaidProgressOutcome(client, mapEventId!, componentId!, mobilePartyId, target.SettlementId, target.VillageId, itemId);
        }
    }

    [Fact]
    public void RaidLootedItemsUpdated_ClientRaisesItemsLootedForVanillaPlunderUi()
    {
        var client = Clients.First();
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var itemId = TestEnvironment.CreateRegisteredObject<ItemObject>();
        var listenerOwner = new object();
        var capturedAmount = 0;

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(client.ObjectManager.TryGetObject<ItemObject>(itemId, out var item));

            CampaignEvents.ItemsLooted.AddNonSerializedListener(listenerOwner, (MobileParty party, ItemRoster lootedItems) =>
            {
                if (party != mobileParty)
                    return;

                capturedAmount = GetItemAmount(lootedItems, item);
            });
        });

        Server.NetworkSentMessages.Clear();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<ItemObject>(itemId, out var item));

            var lootedItems = new List<(ItemObject Item, int Amount)> { (item, 2) };
            MessageBroker.Instance.Publish(this, new RaidLootedItemsUpdated(mobileParty, lootedItems));
        });

        client.Call(() => CampaignEvents.ItemsLooted.ClearListeners(listenerOwner));

        var message = Server.NetworkSentMessages.GetMessages<NetworkRaidLootedItemsUpdated>().Single();
        Assert.Equal(mobilePartyId, message.PartyId);
        Assert.Contains(itemId, message.ItemIds);
        Assert.Contains(2, message.Amounts);
        Assert.Equal(2, capturedAmount);
    }

    [Theory]
    [InlineData(0f, Village.VillageStates.Looted)]
    [InlineData(0.5f, Village.VillageStates.Normal)]
    public void RaidFinalization_SyncsVillageStateAndDestroysMapEvent(
        float settlementHitPoints,
        Village.VillageStates expectedVillageState)
    {
        var (heroId, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();
        string? mapEventId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(heroId, out var hero));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            using (new AllowedThread())
            {
                mobileParty.MemberRoster.AddToCounts(hero.CharacterObject, 1);
                hero.PartyBelongedTo = mobileParty;
            }

            var mapEvent = CreateHostileActionMapEvent(mobileParty.Party, settlement.Party, VillageHostileAction.Raid);
            Assert.NotNull(mapEvent);
            mapEvent.MapEventVisual = MockMapEventVisual();
            settlement.SettlementHitPoints = settlementHitPoints;
            mapEvent.BattleState = expectedVillageState == Village.VillageStates.Looted
                ? BattleState.AttackerVictory
                : BattleState.DefenderVictory;

            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out mapEventId));
            mapEvent.FinalizeEvent();
        }, MapEventDisabledMethods);

        Assert.NotNull(mapEventId);
        AssertRaidFinalizedOutcome(Server, mapEventId!, target.SettlementId, target.VillageId, expectedVillageState, settlementHitPoints);
        foreach (var client in Clients)
        {
            AssertRaidFinalizedOutcome(client, mapEventId!, target.SettlementId, target.VillageId, expectedVillageState, settlementHitPoints);
        }
    }

    [Fact]
    public void RaidResistanceVictory_FinalizesBattleAndAutomaticallyContinuesRaid()
    {
        var client = Clients.First();
        var (heroId, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var attackerPartyId = GetPartyBaseId(mobilePartyId);
        var defenderTroopId = TestEnvironment.CreateRegisteredObject<CharacterObject>();
        var target = CreateVillageTarget();
        string? mapEventId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(heroId, out var hero));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<CharacterObject>(defenderTroopId, out var defenderTroop));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            using (new AllowedThread())
            {
                mobileParty.MemberRoster.AddToCounts(hero.CharacterObject, 1);
                hero.PartyBelongedTo = mobileParty;
                settlement.Party.MemberRoster.AddToCounts(defenderTroop, 1);
                settlement.SettlementHitPoints = 0.5f;
            }

            var mapEvent = CreateHostileActionMapEvent(mobileParty.Party, settlement.Party, VillageHostileAction.Raid);
            var defenderMapEventParty = mapEvent.DefenderSide.Parties.Single(x => x.Party == settlement.Party);
            var defenderDescriptor = defenderMapEventParty.Troops.Single(x => x.Troop == defenderTroop).Descriptor;
            defenderMapEventParty.OnTroopWounded(defenderDescriptor);
            mapEvent._battleState = BattleState.AttackerVictory;

            Assert.True(mapEvent.HasWinner);
            Assert.Equal(0, settlement.Party.NumberOfHealthyMembers);
            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out mapEventId));
        }, MapEventDisabledMethods);

        Assert.NotNull(mapEventId);
        Server.NetworkSentMessages.Clear();

        client.Call(() => client.Resolve<INetwork>().SendAll(new NetworkMapEventFinalizeAttempted(mapEventId!)), MapEventDisabledMethods);

        var transition = Server.NetworkSentMessages.GetMessages<NetworkRaidBattleTransition>().Single();
        Assert.Equal(target.SettlementId, transition.SettlementId);
        Assert.Contains(attackerPartyId, transition.PartyIds);
        Assert.False(string.IsNullOrWhiteSpace(transition.MapEventId));
        Assert.NotEqual(mapEventId, transition.MapEventId);
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkMapEventFinalized>());
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkChangeBattleState>());
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionStarted>());

        Server.Call(() =>
        {
            Assert.False(Server.ObjectManager.TryGetObject<MapEvent>(mapEventId!, out var _));
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(transition.MapEventId, out var continuedRaid));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetObject<Village>(target.VillageId, out var village));

            Assert.Same(continuedRaid, mobileParty.MapEvent);
            Assert.Same(settlement, mobileParty.CurrentSettlement);
            Assert.Equal(Village.VillageStates.BeingRaided, village.VillageState);
            Assert.Equal(0.5f, settlement.SettlementHitPoints, 3);
            Assert.True(
                continuedRaid.IsActiveSlowVillageRaid(),
                $"Component={continuedRaid.Component?.GetType().Name}, HasWinner={continuedRaid.HasWinner}, " +
                $"HealthyDefenders={continuedRaid.DefenderSide?.GetTotalHealthyTroopCountOfSide()}, " +
                $"TotalDefenders={continuedRaid.DefenderSide?.TroopCount}, " +
                $"ContainsPlayer={continuedRaid.ContainsPlayerParty()}, " +
                $"AiInterventionSuppressed={continuedRaid.IsRaidAiInterventionSuppressed()}");
        }, MapEventDisabledMethods);

        foreach (var syncedClient in Clients)
        {
            syncedClient.Call(() =>
            {
                Assert.False(syncedClient.ObjectManager.TryGetObject<MapEvent>(mapEventId!, out var _));
                Assert.True(syncedClient.ObjectManager.TryGetObject<MapEvent>(transition.MapEventId, out var continuedRaid));
                Assert.True(syncedClient.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
                Assert.Same(continuedRaid, mobileParty.MapEvent);
            });
        }
    }

    [Fact]
    public void RaidLootingCompletion_ServerClosesRaidingPlayersEncounter()
    {
        // The slow-raid loot phase concludes NATIVELY on the server: RaidEventComponent.Update sets
        // AttackerVictory and MapEvent.Update calls FinishBattle directly — no mission result, no
        // NetworkChangeBattleState — so nothing published MapEventConcluded and the raiding client's
        // encounter was never closed. The client sat softlocked on the looting menu with a dead
        // "End raid" button (live incident 2026-08-14, village_EW6_4).
        var (heroId, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var attackerPartyId = GetPartyBaseId(mobilePartyId);
        var target = CreateVillageTarget();
        var villageTypeId = TestEnvironment.CreateRegisteredObject<VillageType>();
        string? mapEventId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(heroId, out var hero));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetObject<Village>(target.VillageId, out var village));
            Assert.True(Server.ObjectManager.TryGetObject<VillageType>(villageTypeId, out var villageType));

            using (new AllowedThread())
            {
                mobileParty.MemberRoster.AddToCounts(hero.CharacterObject, 10);
                hero.PartyBelongedTo = mobileParty;
                hero.Gold = 0;
                villageType._productions = new MBList<(ItemObject, float)>();
                village.VillageType = villageType;
                // Nearly looted already: the next loot tick zeroes the hit points and concludes the raid.
                settlement.SettlementHitPoints = 0.02f;
                settlement.Village.Hearth = 100f;
                Campaign.Current.MapTimeTracker._deltaTimeInTicks = CampaignTime.Hours(10f).NumTicks;
            }

            var mapEvent = CreateHostileActionMapEvent(mobileParty.Party, settlement.Party, VillageHostileAction.Raid);
            Assert.True(mapEvent.IsActiveSlowVillageRaid());
            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out mapEventId));
        }, MapEventDisabledMethods);

        Assert.NotNull(mapEventId);
        Server.NetworkSentMessages.Clear();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(mapEventId!, out var mapEvent));
            var component = (RaidEventComponent)mapEvent.Component;

            // Drive loot ticks the way the campaign-map update does until the raid concludes natively.
            var finished = false;
            for (int i = 0; i < 50 && mapEvent.BattleState != BattleState.AttackerVictory; i++)
                component.Update(ref finished);

            Assert.Equal(BattleState.AttackerVictory, mapEvent.BattleState);
            Assert.True(finished);

            // MapEvent.Update reacts to the component's finish by calling the native FinishBattle.
            AccessTools.Method(typeof(MapEvent), "FinishBattle").Invoke(mapEvent, null);
            Assert.True(mapEvent.IsFinalized);
        }, MapEventDisabledMethods);

        // The raiding player's encounter must be closed by the server — this is what was missing live.
        var close = Server.NetworkSentMessages.GetMessages<NetworkClosePvpEncounter>().Single();
        Assert.Contains(attackerPartyId, close.PartyIds);

        Server.Call(() =>
        {
            Assert.False(Server.ObjectManager.TryGetObject<MapEvent>(mapEventId!, out _));
            Assert.True(Server.ObjectManager.TryGetObject<Village>(target.VillageId, out var village));
            Assert.Equal(Village.VillageStates.Looted, village.VillageState);
        }, MapEventDisabledMethods);

        foreach (var syncedClient in Clients)
        {
            syncedClient.Call(() =>
            {
                Assert.False(syncedClient.ObjectManager.TryGetObject<MapEvent>(mapEventId!, out _));
                Assert.True(syncedClient.ObjectManager.TryGetObject<Village>(target.VillageId, out var village));
                Assert.Equal(Village.VillageStates.Looted, village.VillageState);
            });
        }
    }

    [Fact]
    public void RaidEndRequest_UnresolvableLocalMapEvent_ClosesLocalRaidMenu()
    {
        // Client half of the same softlock: if the server's destroy already unregistered the raid
        // MapEvent locally, the End-raid consequence used to publish a finalize request that could
        // never resolve an id — silently eating every click. It must fall back to closing the local
        // menu instead.
        var client = Clients.First();
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");

        EnableHeadlessEncounterFinish(client);
        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));

            using (new AllowedThread())
            {
                Campaign.Current.MainParty = mobileParty;
            }

            // A stale local raid event the object manager no longer knows about (server destroy applied).
            var staleMapEvent = ObjectHelper.SkipConstructor<MapEvent>();
            var encounter = ObjectHelper.SkipConstructor<PlayerEncounter>();
            encounter._mapEvent = staleMapEvent;
            encounter.ForceRaid = true;
            Campaign.Current.PlayerEncounter = encounter;
            Assert.False(client.ObjectManager.TryGetId(staleMapEvent, out _));
        }, MapEventDisabledMethods);

        client.InternalMessages.Clear();
        var disabledMethods = MapEventDisabledMethods
            .Append(AccessTools.Method(typeof(GameMenu), nameof(GameMenu.ExitToLast)))
            .ToList();

        client.Call(() =>
        {
            AccessTools.Method(typeof(VillageHostileActionCampaignBehavior), "wait_menu_end_raiding_on_consequence")
                .Invoke(null, new object?[] { null });
        }, disabledMethods);

        // No finalize request went out for the unresolvable event, and the local menu state was closed.
        Assert.Empty(client.InternalMessages.GetMessages<MapEventFinalizeAttempted>());
        client.Call(() => Assert.Null(PlayerEncounter.Current), MapEventDisabledMethods);
    }

    [Fact]
    public void RaidFinalizeRequest_EndingSlowRaidMovesRaiderToVillageGate()
    {
        var client = Clients.First();
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();
        var gatePosition = new CampaignVec2(new Vec2(42f, 24f), true);
        var insidePosition = new CampaignVec2(new Vec2(40f, 24f), true);
        string? mapEventId = null;

        void PlaceRaiderInsideVillage(EnvironmentInstance instance)
        {
            instance.Call(() =>
            {
                Assert.True(instance.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
                Assert.True(instance.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

                using (new AllowedThread())
                {
                    settlement.GatePosition = gatePosition;
                    mobileParty.Position = insidePosition;
                    mobileParty.CurrentSettlement = settlement;
                    mobileParty.ResetNavigationToHold();
                }
            });
        }

        PlaceRaiderInsideVillage(Server);
        foreach (var syncedClient in Clients)
        {
            PlaceRaiderInsideVillage(syncedClient);
        }

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            var mapEvent = CreateHostileActionMapEvent(mobileParty.Party, settlement.Party, VillageHostileAction.Raid);
            Assert.True(mapEvent.IsActiveSlowVillageRaid());
            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out mapEventId));
        }, MapEventDisabledMethods);

        Assert.NotNull(mapEventId);
        EnableHeadlessEncounterFinish(client);
        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(client.ObjectManager.TryGetObject<MapEvent>(mapEventId!, out var mapEvent));

            using (new AllowedThread())
            {
                Campaign.Current.MainParty = mobileParty;
            }

            var encounter = ObjectHelper.SkipConstructor<PlayerEncounter>();
            encounter._mapEvent = mapEvent;
            encounter.ForceRaid = true;
            Campaign.Current.PlayerEncounter = encounter;
        }, MapEventDisabledMethods);

        Server.NetworkSentMessages.Clear();
        var disabledMethods = MapEventDisabledMethods
            .Append(AccessTools.Method(typeof(GameMenu), nameof(GameMenu.ExitToLast)))
            .ToList();

        client.Call(() => client.Resolve<INetwork>().SendAll(new NetworkMapEventFinalizeAttempted(mapEventId!)), disabledMethods);
        AssertRaidPartyMovedToVillageGate(Server, mobilePartyId, target.SettlementId);
        foreach (var syncedClient in Clients)
        {
            AssertRaidPartyMovedToVillageGate(syncedClient, mobilePartyId, target.SettlementId);
        }
    }

    [Fact]
    public void RaidFinalizeRequest_ForAlreadyDestroyedMapEvent_StillClosesRequesterMenu()
    {
        var client = Clients.First();
        Server.NetworkSentMessages.Clear();

        client.Call(() => client.Resolve<INetwork>().SendAll(new NetworkMapEventFinalizeAttempted("already-finalized-raid")));

        Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkMapEventFinalized>());
    }

    [Fact]
    public void VillageRecovery_DailyTickSettlement_HealsLootedVillageAndSyncs()
    {
        var target = CreateVillageTarget();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            settlement.Village.VillageState = Village.VillageStates.Looted;
            settlement.SettlementHitPoints = 0.99f;
        });

        AssertVillageStateAndHitPoints(Server, target.SettlementId, target.VillageId, Village.VillageStates.Looted, 0.99f);
        foreach (var client in Clients)
        {
            AssertVillageStateAndHitPoints(client, target.SettlementId, target.VillageId, Village.VillageStates.Looted, 0.99f);
        }

        var disabledMethods = MapEventDisabledMethods
            .Append(AccessTools.Method(typeof(Settlement), "TransferReadyMilitiasToMilitiaParty"))
            .Append(AccessTools.Method(typeof(Settlement), "AddMilitiasToParty"))
            .Where(method => method != null)
            .ToList();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            InvokeVillageHealDailyTick(settlement);
        }, disabledMethods);

        AssertVillageStateAndHitPoints(Server, target.SettlementId, target.VillageId, Village.VillageStates.Normal, 1f);
        foreach (var client in Clients)
        {
            AssertVillageStateAndHitPoints(client, target.SettlementId, target.VillageId, Village.VillageStates.Normal, 1f);
        }
    }

    [Fact]
    public void PlayerEnteringCampaign_ReceivesActiveForceActionCooldownSnapshot()
    {
        var client = Clients.First();
        var target = CreateVillageTarget();
        AddServerCooldown(target.SettlementId);

        Server.NetworkSentMessages.Clear();

        Server.SimulateMessage(this, new PlayerCampaignEntered(client.NetPeer));

        var cooldowns = Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionCooldowns>().Single();
        Assert.Contains(cooldowns.Cooldowns, c => c.SettlementId == target.SettlementId);
        AssertCooldownSynced(client, target.SettlementId);
    }

    [Fact]
    public void PlayerEnteringCampaign_ReceivesRaidAiInterventionConfigSnapshot()
    {
        var client = Clients.First();
        var previous = MapEventConfig.AllowRaidAiIntervention;

        try
        {
            Server.Call(() => MapEventConfig.AllowRaidAiIntervention = false);

            Server.NetworkSentMessages.Clear();

            Server.SimulateMessage(this, new PlayerCampaignEntered(client.NetPeer));

            var update = Server.NetworkSentMessages.GetMessages<NetworkRaidAiInterventionConfigChanged>().Single();
            Assert.False(update.Allow);
            client.Call(() => Assert.False(MapEventConfig.AllowRaidAiIntervention));
        }
        finally
        {
            Server.Call(() => MapEventConfig.AllowRaidAiIntervention = previous);
            foreach (var syncedClient in Clients)
            {
                syncedClient.Call(() => MapEventConfig.AllowRaidAiIntervention = previous);
            }
        }
    }

    [Fact]
    public void SlowRaidMapEvent_WithPlayerParty_ServerUpdateIsAllowed()
    {
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            var mapEvent = GameObjectCreator.CreateInitializedObject<MapEvent>();
            mapEvent.MapEventVisual = MockMapEventVisual();
            mapEvent.Initialize(
                mobileParty.Party,
                settlement.Party,
                new RaidEventComponent(mapEvent),
                MapEvent.BattleTypes.Raid);

            Assert.True(mapEvent.IsActiveSlowVillageRaid());
            Assert.True(InvokeMapEventUpdatePrefix(mapEvent));
        }, MapEventDisabledMethods);
    }

    [Fact]
    public void ActiveSlowRaid_WithPlayerInDeployment_ServerUpdateIsBlockedUntilMissionExit()
    {
        var client = Clients.First();
        var (playerHeroId, playerMobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        TestEnvironment.ConnectRegisteredPlayer(client, "PlayerOne");
        var aiRaiderMobilePartyId = TestEnvironment.CreateRegisteredObject<MobileParty>();
        var aiRaiderTroopId = TestEnvironment.CreateRegisteredObject<CharacterObject>();
        var target = CreateVillageTarget();
        MapEvent? mapEvent = null;
        string? mapEventId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(playerHeroId, out var playerHero));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(playerMobilePartyId, out var playerParty));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(aiRaiderMobilePartyId, out var aiRaiderParty));
            Assert.True(Server.ObjectManager.TryGetObject<CharacterObject>(aiRaiderTroopId, out var aiRaiderTroop));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            using (new AllowedThread())
            {
                playerParty.MemberRoster.AddToCounts(playerHero.CharacterObject, 1);
                playerHero.PartyBelongedTo = playerParty;
                aiRaiderParty.MemberRoster.AddToCounts(aiRaiderTroop, 1);
            }

            mapEvent = CreateHostileActionMapEvent(aiRaiderParty.Party, settlement.Party, VillageHostileAction.Raid);
            Assert.True(mapEvent.IsActiveSlowVillageRaid());
            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out mapEventId));
            Assert.True(InvokeMapEventUpdatePrefix(mapEvent));
        }, MapEventDisabledMethods);

        Assert.NotNull(mapEventId);
        var playerPartyId = GetPartyBaseId(playerMobilePartyId);

        client.Call(() => client.Resolve<INetwork>().SendAll(BattleJoinLeaveTestRequest.Join(
            client,
            mapEventId!,
            playerPartyId,
            BattleSideEnum.Attacker)), MapEventDisabledMethods);

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(playerMobilePartyId, out var playerParty));
            Assert.Same(mapEvent, playerParty.MapEvent);
            Assert.True(mapEvent!.IsActiveSlowVillageRaid());
        }, MapEventDisabledMethods);

        Server.NetworkSentMessages.Clear();
        client.Call(() => client.Resolve<INetwork>().SendAll(BattleStartTestRequest.Create(
            client, BattleStartMode.Mission, mapEventId!, playerMobilePartyId)), MapEventDisabledMethods);

        Assert.Equal(mapEventId, Server.NetworkSentMessages.GetMessages<NetworkStartAttackMission>().Single().MapEventId);

        Server.Call(() => Server.Resolve<IPlayerManager>().SetPeer("PlayerOne", client.NetPeer));
        Server.SimulateMessage(client.NetPeer, new NetworkMissionEntered("PlayerOne", mapEventId!));

        Server.Call(() => Assert.False(InvokeMapEventUpdatePrefix(mapEvent!)), MapEventDisabledMethods);

        Server.SimulateMessage(client.NetPeer, new NetworkMissionLeft("PlayerOne", mapEventId!));

        Server.Call(() => Assert.True(InvokeMapEventUpdatePrefix(mapEvent!)), MapEventDisabledMethods);
        ServerBattleModeArbiter.Release(mapEventId!);
    }

    [Fact]
    public void SlowRaidMapEvent_WithDefenderTroopsAfterLootingPhase_ServerUpdateIsBlocked()
    {
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var defenderTroopId = TestEnvironment.CreateRegisteredObject<CharacterObject>();
        var target = CreateVillageTarget();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetObject<CharacterObject>(defenderTroopId, out var defenderTroop));

            var mapEvent = CreateHostileActionMapEvent(mobileParty.Party, settlement.Party, VillageHostileAction.Raid);
            mapEvent.WasEverInLootingPhase = true;

            var defenderParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
            defenderParty.MemberRoster.AddToCounts(defenderTroop, 1);
            AddSyntheticMapEventParty(mapEvent.DefenderSide, defenderParty.Party);

            Assert.False(mapEvent.IsActiveSlowVillageRaid());
            Assert.False(InvokeMapEventUpdatePrefix(mapEvent));
        }, MapEventDisabledMethods);
    }

    [Fact]
    public void SlowRaidMapEvent_WithDefenderTroopsAndAiInterventionDisabled_ServerUpdateIsAllowed()
    {
        var previous = MapEventConfig.AllowRaidAiIntervention;
        MapEventConfig.AllowRaidAiIntervention = false;
        try
        {
            var (heroId, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
            var defenderTroopId = TestEnvironment.CreateRegisteredObject<CharacterObject>();
            var raidItemId = TestEnvironment.CreateRegisteredObject<ItemObject>();
            var villageTypeId = TestEnvironment.CreateRegisteredObject<VillageType>();
            var target = CreateVillageTarget();

            Server.Call(() =>
            {
                Assert.True(Server.ObjectManager.TryGetObject<Hero>(heroId, out var hero));
                Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
                Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
                Assert.True(Server.ObjectManager.TryGetObject<CharacterObject>(defenderTroopId, out var defenderTroop));
                Assert.True(Server.ObjectManager.TryGetObject<ItemObject>(raidItemId, out var raidItem));
                Assert.True(Server.ObjectManager.TryGetObject<VillageType>(villageTypeId, out var villageType));

                using (new AllowedThread())
                {
                    mobileParty.MemberRoster.AddToCounts(hero.CharacterObject, 100);
                    hero.PartyBelongedTo = mobileParty;
                    settlement.ItemRoster.AddToCounts(new EquipmentElement(raidItem), 3);
                    villageType._productions = new MBList<(ItemObject, float)>
                    {
                        (raidItem, 120f),
                    };
                    settlement.Village.VillageType = villageType;
                    settlement.SettlementHitPoints = 1f;
                    settlement.Village.Hearth = 100f;
                    Campaign.Current.MapTimeTracker._deltaTimeInTicks = CampaignTime.Hours(10f).NumTicks;
                }

                var mapEvent = CreateHostileActionMapEvent(mobileParty.Party, settlement.Party, VillageHostileAction.Raid);
                var component = Assert.IsType<RaidEventComponent>(mapEvent.Component);

                var defenderParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
                defenderParty.MemberRoster.AddToCounts(defenderTroop, 1);
                AddSyntheticMapEventParty(mapEvent.DefenderSide, defenderParty.Party);

                Assert.True(mapEvent.IsActiveSlowVillageRaid());
                Assert.True(mapEvent.DefenderSide.TroopCount > 0);
                Assert.True(InvokeMapEventUpdatePrefix(mapEvent));

                Assert.Null(defenderParty.Party.MapEventSide);
                Assert.DoesNotContain(mapEvent.DefenderSide.Parties, party => party.Party == defenderParty.Party);
                Assert.Equal(0, mapEvent.DefenderSide.TroopCount);

                var wasFinished = false;
                for (int i = 0; i < 25 && component.RaidDamage <= 0; i++)
                {
                    component.Update(ref wasFinished);
                }

                Assert.True(component.RaidDamage > 0);
            }, MapEventDisabledMethods);
        }
        finally
        {
            MapEventConfig.AllowRaidAiIntervention = previous;
        }
    }

    [Fact]
    public void SlowRaidMapEvent_WithAiInterventionEnabled_AllowsAiDefenderJoin()
    {
        var previous = MapEventConfig.AllowRaidAiIntervention;
        MapEventConfig.AllowRaidAiIntervention = true;
        try
        {
            var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
            var defenderTroopId = TestEnvironment.CreateRegisteredObject<CharacterObject>();
            var target = CreateVillageTarget();

            Server.Call(() =>
            {
                Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
                Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
                Assert.True(Server.ObjectManager.TryGetObject<CharacterObject>(defenderTroopId, out var defenderTroop));

                var mapEvent = CreateHostileActionMapEvent(mobileParty.Party, settlement.Party, VillageHostileAction.Raid);

                var defenderParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
                defenderParty.MemberRoster.AddToCounts(defenderTroop, 1);

                Assert.True(mapEvent.IsActiveSlowVillageRaid());
                Assert.True(InvokeCanPartyJoinBattlePostfix(mapEvent, defenderParty.Party, initialResult: true));

                defenderParty.Party.MapEventSide = mapEvent.DefenderSide;

                Assert.Same(mapEvent.DefenderSide, defenderParty.Party.MapEventSide);
                Assert.Contains(mapEvent.DefenderSide.Parties, party => party.Party == defenderParty.Party);
                Assert.False(mapEvent.IsActiveSlowVillageRaid());
            }, MapEventDisabledMethods);
        }
        finally
        {
            MapEventConfig.AllowRaidAiIntervention = previous;
        }
    }

    [Fact]
    public void SlowRaidMapEvent_WithAiInterventionDisabled_BlocksAiDefenderJoin()
    {
        var previous = MapEventConfig.AllowRaidAiIntervention;
        MapEventConfig.AllowRaidAiIntervention = false;
        try
        {
            var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
            var defenderTroopId = TestEnvironment.CreateRegisteredObject<CharacterObject>();
            var target = CreateVillageTarget();

            Server.Call(() =>
            {
                Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
                Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
                Assert.True(Server.ObjectManager.TryGetObject<CharacterObject>(defenderTroopId, out var defenderTroop));

                var mapEvent = CreateHostileActionMapEvent(mobileParty.Party, settlement.Party, VillageHostileAction.Raid);

                var defenderParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
                defenderParty.MemberRoster.AddToCounts(defenderTroop, 1);
                defenderParty.Party.MapEventSide = mapEvent.DefenderSide;

                Assert.Null(defenderParty.Party.MapEventSide);
                Assert.DoesNotContain(mapEvent.DefenderSide.Parties, party => party.Party == defenderParty.Party);
                Assert.True(mapEvent.IsActiveSlowVillageRaid());
                Assert.True(InvokeMapEventUpdatePrefix(mapEvent));
            }, MapEventDisabledMethods);
        }
        finally
        {
            MapEventConfig.AllowRaidAiIntervention = previous;
        }
    }

    [Fact]
    public void SlowRaidMapEvent_WithAiInterventionDisabled_BlocksAiEncounterStarts()
    {
        var previous = MapEventConfig.AllowRaidAiIntervention;
        MapEventConfig.AllowRaidAiIntervention = false;
        try
        {
            var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
            var defenderTroopId = TestEnvironment.CreateRegisteredObject<CharacterObject>();
            var target = CreateVillageTarget();

            Server.Call(() =>
            {
                Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
                Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
                Assert.True(Server.ObjectManager.TryGetObject<CharacterObject>(defenderTroopId, out var defenderTroop));

                var mapEvent = CreateHostileActionMapEvent(mobileParty.Party, settlement.Party, VillageHostileAction.Raid);
                AddSyntheticMapEventParty(mapEvent.DefenderSide, settlement.Party);

                var defenderParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
                defenderParty.MemberRoster.AddToCounts(defenderTroop, 1);

                Assert.True(mapEvent.IsActiveSlowVillageRaid());
                Assert.True(settlement.Party.MapEvent.IsRaidAiInterventionSuppressed());
                Assert.False(InvokeStartSettlementEncounterPrefix(defenderParty, settlement));
                Assert.False(InvokeStartPartyEncounterPrefix(defenderParty.Party, mobileParty.Party));
                Assert.False(InvokeRestartPlayerEncounterPrefix(defenderParty.Party, mobileParty.Party));
                Assert.True(InvokeMapEventUpdatePrefix(mapEvent));
                Assert.Null(defenderParty.Party.MapEventSide);
                Assert.Null(defenderParty.Party.MapEvent);
            }, MapEventDisabledMethods);
        }
        finally
        {
            MapEventConfig.AllowRaidAiIntervention = previous;
        }
    }

    [Fact]
    public void RaidAiInterventionDebugCommand_ClientRequestUpdatesServerAndClients()
    {
        var client = Clients.First();
        var previous = MapEventConfig.AllowRaidAiIntervention;
        MapEventConfig.AllowRaidAiIntervention = true;
        try
        {
            Server.NetworkSentMessages.Clear();
            client.NetworkSentMessages.Clear();

            client.Call(() =>
            {
                var result = RaidDebugCommands.AllowRaidAiIntervention(new List<string> { "off" });
                Assert.Contains("server update requested", result);
            });

            var request = client.NetworkSentMessages.GetMessages<NetworkRequestRaidAiInterventionConfigChange>().Single();
            Assert.False(request.Allow);

            var update = Server.NetworkSentMessages.GetMessages<NetworkRaidAiInterventionConfigChanged>().Single();
            Assert.False(update.Allow);

            Server.Call(() => Assert.False(MapEventConfig.AllowRaidAiIntervention));
            foreach (var syncedClient in Clients)
            {
                syncedClient.Call(() => Assert.False(MapEventConfig.AllowRaidAiIntervention));
            }
        }
        finally
        {
            MapEventConfig.AllowRaidAiIntervention = previous;
        }
    }

    [Fact]
    public void SlowRaidMapEvent_DoesNotBlockFastForward()
    {
        var (_, mobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var target = CreateVillageTarget();

        // Simulate atleast one connected player so that unPause policy in ITimeControl for no connected players is not triggered
        TestEnvironment.ConnectRegisteredPlayer(Clients.First(), "PlayerOne");

        Server.Call(() => Server.Resolve<ITimeControlInterface>().ServerSetTimeControl(TimeControlEnum.Play_2x));
        Server.NetworkSentMessages.Clear();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            var mapEvent = GameObjectCreator.CreateInitializedObject<MapEvent>();
            mapEvent.MapEventVisual = MockMapEventVisual();
            mapEvent.Initialize(
                mobileParty.Party,
                settlement.Party,
                new RaidEventComponent(mapEvent),
                MapEvent.BattleTypes.Raid);

            Assert.True(mapEvent.IsUnopposedVillageRaid());
            Assert.Equal(TimeControlEnum.Play_2x, Server.Resolve<ITimeControlInterface>().GetTimeControl());
        }, MapEventDisabledMethods);

        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkMapEventLockChanged>());
    }

    [Fact]
    public void ActiveSlowRaidMapEvent_SecondPlayerConversationIsAllowed()
    {
        var client = Clients.First();
        var (_, raiderMobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var (_, joinerMobilePartyId) = CreatePlayerHeroParty("PlayerTwo");
        var target = CreateVillageTarget();
        var raiderPartyId = GetPartyBaseId(raiderMobilePartyId);
        var joinerPartyId = GetPartyBaseId(joinerMobilePartyId);

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(raiderMobilePartyId, out var raiderParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            var mapEvent = CreateHostileActionMapEvent(raiderParty.Party, settlement.Party, VillageHostileAction.Raid);

            Assert.True(mapEvent.IsActiveSlowVillageRaid());
        }, MapEventDisabledMethods);

        Server.NetworkSentMessages.Clear();

        RequestConversation(client, joinerPartyId, raiderPartyId);

        var allowed = Server.NetworkSentMessages.GetMessages<NetworkAllowConversation>().Single();
        Assert.Equal(raiderPartyId, allowed.DefenderId);
        Assert.Equal(joinerPartyId, allowed.AttackerId);
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkConversationDenied>());
    }

    [Fact]
    public void ActiveSlowRaidMapEvent_StartSettlementEncounterRequestsJoinConversation()
    {
        var client = Clients.First();
        client.Resolve<IControllerIdProvider>().SetControllerId("PlayerTwo");
        var (_, raiderMobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var (_, joinerMobilePartyId) = CreatePlayerHeroParty("PlayerTwo");
        var target = CreateVillageTarget();
        var joinerPartyId = GetPartyBaseId(joinerMobilePartyId);

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(raiderMobilePartyId, out var raiderParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            var mapEvent = CreateHostileActionMapEvent(raiderParty.Party, settlement.Party, VillageHostileAction.Raid);

            Assert.True(mapEvent.IsActiveSlowVillageRaid());
        }, MapEventDisabledMethods);

        client.NetworkSentMessages.Clear();

        client.Call(() =>
        {
            client.Resolve<IControllerIdProvider>().SetControllerId("PlayerTwo");
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));
            Assert.True(client.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(client.ObjectManager.TryGetObject<Village>(target.VillageId, out var village));
            if (!client.ObjectManager.TryGetObject<PartyBase>(target.SettlementPartyId, out var settlementParty))
            {
                settlementParty = new PartyBase(settlement);
                Assert.True(client.ObjectManager.AddExisting(target.SettlementPartyId, settlementParty));
            }

            using (new AllowedThread())
            {
                settlement.Village = village;
                settlement.SetSettlementComponent(village);
                settlement.Party = settlementParty;
            }

            Assert.True(joinerParty.IsControlledByThisInstance());
            Assert.True(village.VillageState == Village.VillageStates.BeingRaided || settlement.Party.MapEvent?.IsActiveSlowVillageRaid() == true);
            var patchType = AccessTools.TypeByName("GameInterface.Services.MapEvents.Patches.EncounterManagerPatches");
            var prefix = AccessTools.Method(patchType, "Prefix");
            Assert.NotNull(prefix);

            var runOriginal = (bool)prefix.Invoke(null, new object[] { joinerParty, settlement })!;

            Assert.False(runOriginal);
        });

        var request = client.NetworkSentMessages.GetMessages<NetworkRequestConversation>().Single();
        Assert.Equal(target.SettlementPartyId, request.DefenderId);
        Assert.Equal(joinerPartyId, request.AttackerId);
        Assert.Equal(ConversationRestartSource.EncounterManager, request.Source);
        Assert.Empty(client.NetworkSentMessages.GetMessages<NetworkRequestStartSettlementEncounter>());
    }

    [Fact]
    public void RaidDefenderJoin_WithResistancePhase_UsesNormalBattleJoinReplication()
    {
        var client = Clients.First();
        var (raiderHeroId, raiderMobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var (joinerHeroId, joinerMobilePartyId) = CreatePlayerHeroParty("PlayerTwo");
        TestEnvironment.ConnectRegisteredPlayer(client, "PlayerTwo");
        var defenderTroopId = TestEnvironment.CreateRegisteredObject<CharacterObject>();
        var target = CreateVillageTarget();
        var joinerPartyId = GetPartyBaseId(joinerMobilePartyId);
        string? raidMapEventId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(raiderHeroId, out var raiderHero));
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(joinerHeroId, out var joinerHero));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(raiderMobilePartyId, out var raiderParty));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetObject<CharacterObject>(defenderTroopId, out var defenderTroop));

            using (new AllowedThread())
            {
                raiderParty.MemberRoster.AddToCounts(raiderHero.CharacterObject, 1);
                joinerParty.MemberRoster.AddToCounts(joinerHero.CharacterObject, 1);
                raiderHero.PartyBelongedTo = raiderParty;
                joinerHero.PartyBelongedTo = joinerParty;
            }

            var raidMapEvent = CreateHostileActionMapEvent(raiderParty.Party, settlement.Party, VillageHostileAction.Raid);
            Assert.NotNull(raidMapEvent);
            raidMapEvent.MapEventVisual = MockMapEventVisual();

            var defenderParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
            defenderParty.MemberRoster.AddToCounts(defenderTroop, 1);
            AddSyntheticMapEventParty(raidMapEvent.DefenderSide, defenderParty.Party);

            Assert.False(raidMapEvent.IsActiveSlowVillageRaid());
            Assert.True(Server.ObjectManager.TryGetId(raidMapEvent, out raidMapEventId));
        }, MapEventDisabledMethods);

        Assert.NotNull(raidMapEventId);

        client.NetworkSentMessages.Clear();
        Server.NetworkSentMessages.Clear();
        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MapEvent>(raidMapEventId!, out var mapEvent));
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));

            joinerParty.Party.MapEventSide = mapEvent.DefenderSide;
        }, MapEventDisabledMethods);

        var request = client.NetworkSentMessages.GetMessages<NetworkRequestJoinBattle>().Single();
        var reply = Server.NetworkSentMessages.GetMessages<NetworkJoinBattleReply>().Single();
        Assert.Equal(request.RequestId, reply.RequestId);
        Assert.True(reply.Accepted);
        var proof = Server.NetworkSentMessages.GetMessages<NetworkAddBattleParty>()
            .Single(message => message.AuthorityRequestId > 0);
        Assert.Equal(request.Header.SessionId, proof.SessionId);
        Assert.Equal(request.Header.RequestId, proof.AuthorityRequestId);
        Assert.Equal(request.MapEventId, proof.MapEventId);
        Assert.Equal(request.PartyId, proof.PartyId);
        Assert.Equal((int)request.Side, proof.Side);

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(raidMapEventId!, out var mapEvent));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(raiderMobilePartyId, out var raiderParty));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));
            Assert.Same(mapEvent, joinerParty.MapEvent);
            Assert.Same(mapEvent, raiderParty.MapEvent);
            Assert.True(mapEvent.IsRaid);
            Assert.IsType<RaidEventComponent>(mapEvent.Component);
            Assert.Same(joinerParty.Party.MapEventSide, mapEvent.DefenderSide);
            Assert.Same(raiderParty.Party.MapEventSide, mapEvent.AttackerSide);
        }, MapEventDisabledMethods);

        foreach (var instance in Clients)
        {
            instance.Call(() =>
            {
                Assert.True(instance.ObjectManager.TryGetObject<MapEvent>(raidMapEventId!, out var mapEvent));
                Assert.True(instance.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));
                Assert.True(instance.ObjectManager.TryGetObject<MobileParty>(raiderMobilePartyId, out var raiderParty));
                Assert.Same(mapEvent, joinerParty.MapEvent);
                Assert.Same(mapEvent, raiderParty.MapEvent);
                Assert.True(mapEvent.IsRaid);
                Assert.IsType<RaidEventComponent>(mapEvent.Component);
                Assert.Same(joinerParty.Party.MapEventSide, mapEvent.DefenderSide);
                Assert.Same(raiderParty.Party.MapEventSide, mapEvent.AttackerSide);
            });
        }

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MapEvent>(raidMapEventId!, out var mapEvent));
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));

            joinerParty.Party.MapEventSide = mapEvent.DefenderSide;
            Assert.Same(mapEvent, joinerParty.MapEvent);
            Assert.Single(mapEvent.DefenderSide.Parties, party => party.Party == joinerParty.Party);
        }, MapEventDisabledMethods);
        Assert.Single(client.NetworkSentMessages.GetMessages<NetworkRequestJoinBattle>());
    }

    [Fact]
    public void RaidJoinEncounter_RejectedJoinRestoresEncounterAndAllowsRetry()
    {
        var client = Clients.First();
        client.Resolve<IControllerIdProvider>().SetControllerId("PlayerTwo");
        var (raiderHeroId, raiderMobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var (joinerHeroId, joinerMobilePartyId) = CreatePlayerHeroParty("PlayerTwo");
        var defenderTroopId = TestEnvironment.CreateRegisteredObject<CharacterObject>();
        var target = CreateVillageTarget();
        var joinerPartyId = GetPartyBaseId(joinerMobilePartyId);
        string? raidMapEventId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(raiderHeroId, out var raiderHero));
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(joinerHeroId, out var joinerHero));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(raiderMobilePartyId, out var raiderParty));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetObject<CharacterObject>(defenderTroopId, out var defenderTroop));

            using (new AllowedThread())
            {
                raiderParty.MemberRoster.AddToCounts(raiderHero.CharacterObject, 1);
                joinerParty.MemberRoster.AddToCounts(joinerHero.CharacterObject, 1);
                raiderHero.PartyBelongedTo = raiderParty;
                joinerHero.PartyBelongedTo = joinerParty;
            }

            var mapEvent = CreateHostileActionMapEvent(raiderParty.Party, settlement.Party, VillageHostileAction.Raid);
            var defenderParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
            defenderParty.MemberRoster.AddToCounts(defenderTroop, 1);
            AddSyntheticMapEventParty(mapEvent.DefenderSide, defenderParty.Party);

            Assert.False(mapEvent.IsActiveSlowVillageRaid());
            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out raidMapEventId));
        }, MapEventDisabledMethods);

        Assert.NotNull(raidMapEventId);

        client.NetworkSentMessages.Clear();
        var disabledMethods = MapEventDisabledMethods
            .Append(AccessTools.Method(typeof(GameMenu), nameof(GameMenu.ActivateGameMenu), new[] { typeof(string) }))
            .Append(AccessTools.Method(typeof(GameMenu), nameof(GameMenu.SwitchToMenu), new[] { typeof(string) }))
            // The E2E router delivers SendAll synchronously. Keep the outgoing request recorded, but avoid
            // server-side map-event mutation while native JoinBattleInternal is still building local state.
            .Append(AccessTools.Method(
                typeof(E2E.Tests.Environment.TestNetworkRouter),
                nameof(E2E.Tests.Environment.TestNetworkRouter.SendAll),
                new[] { typeof(LiteNetLib.NetPeer), typeof(IMessage) }))
            .ToList();

        client.Call(() =>
        {
            client.Resolve<IControllerIdProvider>().SetControllerId("PlayerTwo");
            Assert.True(client.ObjectManager.TryGetObject<MapEvent>(raidMapEventId!, out var mapEvent));
            Assert.True(client.ObjectManager.TryGetObject<Hero>(joinerHeroId, out var joinerHero));
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));

            using (new AllowedThread())
            {
                Campaign.Current.MainParty = joinerParty;
                joinerHero.PartyBelongedTo = joinerParty;
                Game.Current.PlayerTroop = joinerHero.CharacterObject;
            }

            var encounter = ObjectHelper.SkipConstructor<PlayerEncounter>();
            encounter._mapEvent = mapEvent;
            encounter._encounteredParty = mapEvent.AttackerSide.LeaderParty;
            Campaign.Current.PlayerEncounter = encounter;

            Assert.Same(joinerParty, MobileParty.MainParty);
            Assert.Same(mapEvent, PlayerEncounter.EncounteredBattle);
            Assert.True(mapEvent.IsRaidHostileAction());
            Assert.False(
                mapEvent.IsActiveSlowVillageRaid(),
                $"HealthyDefenders={mapEvent.DefenderSide.GetTotalHealthyTroopCountOfSide()}, TotalDefenders={mapEvent.DefenderSide.TroopCount}");

            var runOriginal = InvokeRaidJoinEncounterConsequencePrefix("game_menu_join_encounter_help_attackers_on_consequence");

            Assert.False(runOriginal);
            Assert.NotNull(PlayerEncounter.Current);
            Assert.Same(mapEvent, PlayerEncounter.Battle);
            Assert.Null(MobileParty.MainParty.MapEvent);
            Assert.Null(MobileParty.MainParty.Party.MapEventSide);
            Assert.DoesNotContain(mapEvent.AttackerSide.Parties, party => party.Party == joinerParty.Party);

            // Vanilla can retry the setter while the encounter menu is rebuilding. Keep the first request pending
            // and do not create a local back-reference or another wire request until the server replies.
            joinerParty.Party.MapEventSide = mapEvent.AttackerSide;
            Assert.Null(joinerParty.MapEvent);
            Assert.Null(joinerParty.Party.MapEventSide);
        }, disabledMethods);

        var request = client.NetworkSentMessages.GetMessages<NetworkRequestJoinBattle>().Single();
        Assert.False(string.IsNullOrWhiteSpace(request.RequestId));
        Assert.Equal(raidMapEventId, request.MapEventId);
        Assert.Equal(joinerPartyId, request.PartyId);
        Assert.Equal(BattleSideEnum.Attacker, request.Side);

        client.Call(() =>
        {
            var gameStateManager = Game.Current.GameStateManager;
            var mapState = gameStateManager.CreateState<MapState>();
            var menuContext = ObjectHelper.SkipConstructor<MenuContext>();
            menuContext.GameMenu = new GameMenu("encounter");
            mapState._menuContext = menuContext;
            gameStateManager._gameStates.Add(mapState);
            Assert.Same(menuContext, Campaign.Current.CurrentMenuContext);
        });

        using var menuSwitchRecorder = new GameMenuSwitchRecorder();

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<PartyBase>(request.PartyId, out var requestedParty));
            Assert.Same(PartyBase.MainParty, requestedParty);
            Assert.True(PlayerEncounter.Current.IsJoinedBattle);
            Assert.Same(PlayerEncounter.Current._mapEvent, PlayerEncounter.Battle);
        });

        client.SimulateMessage(Server.NetPeer, new NetworkJoinBattleReply(
            request.RequestId,
            request.MapEventId,
            request.PartyId,
            accepted: false));

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MapEvent>(raidMapEventId!, out var mapEvent));
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));

            Assert.NotNull(PlayerEncounter.Current);
            Assert.False(PlayerEncounter.Current.IsJoinedBattle);
            Assert.Null(PlayerEncounter.Battle);
            Assert.Same(mapEvent, PlayerEncounter.EncounteredBattle);
            Assert.Null(joinerParty.MapEvent);
            Assert.Null(joinerParty.Party.MapEventSide);
            Assert.NotNull(Campaign.Current.CurrentMenuContext);

            var runOriginal = InvokeRaidJoinEncounterConsequencePrefix("game_menu_join_encounter_help_attackers_on_consequence");
            Assert.False(runOriginal);
            Assert.True(PlayerEncounter.Current.IsJoinedBattle);
            Assert.Same(mapEvent, PlayerEncounter.Battle);
            Assert.Null(joinerParty.MapEvent);
            Assert.Null(joinerParty.Party.MapEventSide);
        }, disabledMethods);

        Assert.Equal(new[] { "join_encounter", "encounter" }, menuSwitchRecorder.SwitchesFor(client));
        menuSwitchRecorder.Clear();

        var requests = client.NetworkSentMessages.GetMessages<NetworkRequestJoinBattle>().ToArray();
        Assert.Equal(2, requests.Length);
        Assert.False(string.IsNullOrWhiteSpace(requests[1].RequestId));
        Assert.NotEqual(requests[0].RequestId, requests[1].RequestId);

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MapEvent>(raidMapEventId!, out var mapEvent));
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));

            using (new AllowedThread())
            {
                // Reproduce the already-applied authoritative add without driving the constructor-skipped
                // client's AttachedParties collection through PartyBase.MapEventSide's vanilla setter.
                joinerParty.Party._mapEventSide = mapEvent.AttackerSide;
                mapEvent.AttackerSide._battleParties.Add(new MapEventParty(joinerParty.Party));
            }

            Assert.Same(mapEvent, joinerParty.MapEvent);
            Assert.Contains(mapEvent.AttackerSide.Parties, party => party.Party == joinerParty.Party);
        }, MapEventDisabledMethods);

        client.SimulateMessage(Server.NetPeer, new NetworkJoinBattleReply(
            requests[1].RequestId,
            requests[1].MapEventId,
            requests[1].PartyId,
            accepted: false));

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MapEvent>(raidMapEventId!, out var mapEvent));
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));

            Assert.True(PlayerEncounter.Current.IsJoinedBattle);
            Assert.Same(mapEvent, PlayerEncounter.Battle);
            Assert.Same(mapEvent, joinerParty.MapEvent);
            Assert.Contains(mapEvent.AttackerSide.Parties, party => party.Party == joinerParty.Party);
        }, MapEventDisabledMethods);

        Assert.Empty(menuSwitchRecorder.SwitchesFor(client));
    }

    [Fact]
    public void RaidResistance_MissionClaimedBeforeAttackerJoin_ReplaysModeAndAcceptsMissionEntry()
    {
        var client = Clients.First();
        var (raiderHeroId, raiderMobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var (joinerHeroId, joinerMobilePartyId) = CreatePlayerHeroParty("PlayerTwo");
        TestEnvironment.ConnectRegisteredPlayer(client, "PlayerTwo");
        var defenderTroopId = TestEnvironment.CreateRegisteredObject<CharacterObject>();
        var target = CreateVillageTarget();
        var joinerPartyId = GetPartyBaseId(joinerMobilePartyId);
        string? raidMapEventId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(raiderHeroId, out var raiderHero));
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(joinerHeroId, out var joinerHero));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(raiderMobilePartyId, out var raiderParty));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetObject<CharacterObject>(defenderTroopId, out var defenderTroop));

            using (new AllowedThread())
            {
                raiderParty.MemberRoster.AddToCounts(raiderHero.CharacterObject, 1);
                joinerParty.MemberRoster.AddToCounts(joinerHero.CharacterObject, 1);
                raiderHero.PartyBelongedTo = raiderParty;
                joinerHero.PartyBelongedTo = joinerParty;
            }

            var mapEvent = CreateHostileActionMapEvent(raiderParty.Party, settlement.Party, VillageHostileAction.Raid);
            var defenderParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
            defenderParty.MemberRoster.AddToCounts(defenderTroop, 1);
            AddSyntheticMapEventParty(mapEvent.DefenderSide, defenderParty.Party);

            Assert.False(mapEvent.IsActiveSlowVillageRaid());
            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out raidMapEventId));
        }, MapEventDisabledMethods);

        Assert.NotNull(raidMapEventId);

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<Hero>(joinerHeroId, out var joinerHero));
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));

            using (new AllowedThread())
            {
                Campaign.Current.MainParty = joinerParty;
                joinerHero.PartyBelongedTo = joinerParty;
                Game.Current.PlayerTroop = joinerHero.CharacterObject;
            }

            Assert.False(BattleModeRegistry.IsMission(raidMapEventId!));
        });

        Server.Call(() => Assert.True(ServerBattleModeArbiter.TryClaimMission(raidMapEventId!)));

        try
        {
            Server.NetworkSentMessages.Clear();

            client.Call(() => client.Resolve<INetwork>().SendAll(BattleJoinLeaveTestRequest.Join(
                client,
                raidMapEventId!,
                joinerPartyId,
                BattleSideEnum.Attacker)), MapEventDisabledMethods);

            var replayedMode = Server.NetworkSentMessages.GetMessages<NetworkBattleModeSet>().Single();
            Assert.Equal(raidMapEventId, replayedMode.MapEventId);
            Assert.Equal((int)BattleStartMode.Mission, replayedMode.Mode);

            client.Call(() => Assert.True(BattleModeRegistry.IsMission(raidMapEventId!)));
            AssertHostileActionJoinerPresent(Server, raidMapEventId!, joinerPartyId);
            foreach (var instance in Clients)
            {
                AssertHostileActionJoinerPresent(instance, raidMapEventId!, joinerPartyId);
            }

            Server.NetworkSentMessages.Clear();

            client.Call(() => client.Resolve<INetwork>().SendAll(BattleStartTestRequest.Create(
                client, BattleStartMode.Mission, raidMapEventId!, joinerMobilePartyId)), MapEventDisabledMethods);

            Assert.True(Server.NetworkSentMessages.GetMessages<NetworkBattleStartReply>().Single().Accepted);
            Assert.Equal(raidMapEventId, Server.NetworkSentMessages.GetMessages<NetworkStartAttackMission>().Single().MapEventId);
        }
        finally
        {
            Server.Call(() => ServerBattleModeArbiter.Release(raidMapEventId!));
            client.Call(BattleModeRegistry.End);
        }
    }

    [Fact]
    public void SlowRaidMapEvent_AttackerJoinRequest_AddsJoiner()
    {
        var client = Clients.First();
        var (_, raiderMobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var (_, joinerMobilePartyId) = CreatePlayerHeroParty("PlayerTwo");
        TestEnvironment.ConnectRegisteredPlayer(client, "PlayerTwo");
        var target = CreateVillageTarget();
        var joinerPartyId = GetPartyBaseId(joinerMobilePartyId);
        string? raidMapEventId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(raiderMobilePartyId, out var raiderParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            var mapEvent = CreateHostileActionMapEvent(raiderParty.Party, settlement.Party, VillageHostileAction.Raid);

            Assert.True(mapEvent.IsActiveSlowVillageRaid());
            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out raidMapEventId));
        }, MapEventDisabledMethods);

        Assert.NotNull(raidMapEventId);

        Server.NetworkSentMessages.Clear();

        client.Call(() => client.Resolve<INetwork>().SendAll(BattleJoinLeaveTestRequest.Join(
            client,
            raidMapEventId!,
            joinerPartyId,
            BattleSideEnum.Attacker)), MapEventDisabledMethods);

        Assert.Contains(
            Server.NetworkSentMessages.GetMessages<NetworkAddInvolvedParties>(),
            message => message.MapEventId == raidMapEventId && message.MapEventPartyIds.Length > 0);
        Assert.Contains(
            Server.NetworkSentMessages.GetMessages<NetworkHidePvpPopup>(),
            message => message.PartyIds.Contains(joinerPartyId));
        AssertHostileActionJoinerPresent(Server, raidMapEventId!, joinerPartyId);
        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(raidMapEventId!, out var mapEvent));
            Assert.True(mapEvent.IsActiveSlowVillageRaid());
        }, MapEventDisabledMethods);

        foreach (var instance in Clients)
        {
            AssertHostileActionJoinerPresent(instance, raidMapEventId!, joinerPartyId);
        }
    }

    [Fact]
    public void SlowRaidMapEvent_DefenderJoinRequest_StartsResistance()
    {
        var client = Clients.First();
        var context = CreateSlowRaidDefenderJoinContext();

        Server.NetworkSentMessages.Clear();
        client.Call(() => client.Resolve<INetwork>().SendAll(BattleJoinLeaveTestRequest.Join(
            client,
            context.MapEventId,
            context.JoinerPartyId,
            BattleSideEnum.Defender)), MapEventDisabledMethods);

        Assert.True(Server.NetworkSentMessages.GetMessages<NetworkJoinBattleReply>().Single().Accepted);

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(context.MapEventId, out var mapEvent));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(context.JoinerMobilePartyId, out var joinerParty));
            Assert.False(mapEvent.IsActiveSlowVillageRaid());
            Assert.Same(mapEvent, joinerParty.MapEvent);
            Assert.Same(mapEvent.DefenderSide, joinerParty.Party.MapEventSide);
        }, MapEventDisabledMethods);

        foreach (var instance in Clients)
        {
            instance.Call(() =>
            {
                Assert.True(instance.ObjectManager.TryGetObject<MapEvent>(context.MapEventId, out var mapEvent));
                Assert.True(instance.ObjectManager.TryGetObject<MobileParty>(context.JoinerMobilePartyId, out var joinerParty));
                Assert.Same(mapEvent, joinerParty.MapEvent);
                Assert.Same(mapEvent.DefenderSide, joinerParty.Party.MapEventSide);
            });
        }
    }

    [Fact]
    public void ActiveSlowRaid_DefenderJoinOption_RequestsBattleJoin()
    {
        var client = Clients.First();
        var (_, raiderMobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var (joinerHeroId, joinerMobilePartyId) = CreatePlayerHeroParty("PlayerTwo");
        var target = CreateVillageTarget();
        string? raidMapEventId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(raiderMobilePartyId, out var raiderParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            var mapEvent = CreateHostileActionMapEvent(raiderParty.Party, settlement.Party, VillageHostileAction.Raid);
            Assert.True(mapEvent.IsActiveSlowVillageRaid());
            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out raidMapEventId));
        }, MapEventDisabledMethods);

        Assert.NotNull(raidMapEventId);
        client.NetworkSentMessages.Clear();

        var disabledMethods = MapEventDisabledMethods
            .Append(AccessTools.Method(typeof(GameMenu), nameof(GameMenu.ActivateGameMenu), new[] { typeof(string) }))
            .Append(AccessTools.Method(typeof(GameMenu), nameof(GameMenu.SwitchToMenu), new[] { typeof(string) }))
            .Append(AccessTools.Method(
                typeof(E2E.Tests.Environment.TestNetworkRouter),
                nameof(E2E.Tests.Environment.TestNetworkRouter.SendAll),
                new[] { typeof(LiteNetLib.NetPeer), typeof(IMessage) }))
            .ToList();

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MapEvent>(raidMapEventId!, out var mapEvent));
            Assert.True(client.ObjectManager.TryGetObject<Hero>(joinerHeroId, out var joinerHero));
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));

            using (new AllowedThread())
            {
                Campaign.Current.MainParty = joinerParty;
                joinerHero.PartyBelongedTo = joinerParty;
                Game.Current.PlayerTroop = joinerHero.CharacterObject;
            }

            var encounter = ObjectHelper.SkipConstructor<PlayerEncounter>();
            encounter._mapEvent = mapEvent;
            encounter._encounteredParty = mapEvent.AttackerSide.LeaderParty;
            Campaign.Current.PlayerEncounter = encounter;

            var condition = InvokeRaidJoinEncounterConditionPostfix(
                "game_menu_join_encounter_help_defenders_on_condition",
                initialResult: true);
            Assert.True(condition.Result);
            Assert.True(condition.IsEnabled);

            var runOriginal = InvokeRaidJoinEncounterConsequencePrefix(
                "game_menu_join_encounter_help_defenders_on_consequence");
            Assert.False(runOriginal);
        }, disabledMethods);

        var request = client.NetworkSentMessages.GetMessages<NetworkRequestJoinBattle>().Single();
        Assert.Equal(raidMapEventId, request.MapEventId);
        Assert.Equal(GetPartyBaseId(joinerMobilePartyId), request.PartyId);
        Assert.Equal(BattleSideEnum.Defender, request.Side);
    }

    [Theory]
    [InlineData(VillageHostileAction.Raid)]
    [InlineData(VillageHostileAction.ForceVolunteers)]
    [InlineData(VillageHostileAction.ForceSupplies)]
    public void SinglePlayerHostileAction_AttackMissionStart_IsAllowed(VillageHostileAction action)
    {
        var client = Clients.First();
        var hostileAction = CreateHostileActionWithOnePlayerParty(action);
        TestEnvironment.ConnectRegisteredPlayer(client, "PlayerOne");

        Server.NetworkSentMessages.Clear();

        client.Call(() => client.Resolve<INetwork>().SendAll(BattleStartTestRequest.Create(
            client, BattleStartMode.Mission, hostileAction.MapEventId, hostileAction.AttackerMobilePartyId)), MapEventDisabledMethods);

        var start = Server.NetworkSentMessages.GetMessages<NetworkStartAttackMission>().Single();
        Assert.Equal(hostileAction.MapEventId, start.MapEventId);

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(hostileAction.MapEventId, out var mapEvent));
            Assert.True(mapEvent.IsVillageHostileAction());
            Assert.False(mapEvent.IsVillageHostileActionWithMultiplePlayerParties());
        }, MapEventDisabledMethods);

        ServerBattleModeArbiter.Release(hostileAction.MapEventId);
    }

    [Fact]
    public void MultiPlayerRaid_AttackMissionStart_IsAllowed()
    {
        var client = Clients.First();
        var hostileAction = CreateHostileActionWithTwoPlayerParties(VillageHostileAction.Raid);

        Server.NetworkSentMessages.Clear();

        client.Call(() => client.Resolve<INetwork>().SendAll(BattleStartTestRequest.Create(
            client, BattleStartMode.Mission, hostileAction.MapEventId, hostileAction.AttackerMobilePartyId)), MapEventDisabledMethods);

        var starts = Server.NetworkSentMessages.GetMessages<NetworkStartAttackMission>().ToArray();
        Assert.Equal(2, starts.Length);
        Assert.All(starts, start =>
        {
            Assert.Equal(hostileAction.MapEventId, start.MapEventId);
            Assert.Equal(hostileAction.AttackerMobilePartyId, start.InitiatingPartyId);
        });

        var mode = Server.NetworkSentMessages.GetMessages<NetworkBattleModeSet>().Single();
        Assert.Equal(hostileAction.MapEventId, mode.MapEventId);
        Assert.Equal((int)BattleStartMode.Mission, mode.Mode);

        var reply = Server.NetworkSentMessages.GetMessages<NetworkBattleStartReply>().Single();
        Assert.True(reply.Accepted);

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(hostileAction.MapEventId, out var mapEvent));
            Assert.True(mapEvent.IsRaidHostileAction());
            Assert.True(mapEvent.IsVillageHostileActionWithMultiplePlayerParties());
            Assert.False(mapEvent.IsUnsupportedMultiPlayerHostileAction());
        }, MapEventDisabledMethods);

        ServerBattleModeArbiter.Release(hostileAction.MapEventId);
    }

    [Fact]
    public void MultiPlayerRaid_WoundedNonInitiator_MissionStart_LeavesBattle()
    {
        var client = Clients.First();
        var hostileAction = CreateHostileActionWithTwoPlayerParties(VillageHostileAction.Raid);
        string? woundedPartyId = null;

        Server.Call(() =>
        {
            var playerManager = Server.Resolve<IPlayerManager>();
            Assert.True(playerManager.TryGetPlayer("PlayerTwo", out var woundedPlayer));
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(woundedPlayer.HeroId, out var woundedHero));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(woundedPlayer.MobilePartyId, out var woundedParty));

            woundedHero.HitPoints = 1;
            Assert.True(woundedHero.IsWounded);
            Assert.True(Server.ObjectManager.TryGetId(woundedParty.Party, out woundedPartyId));
        }, MapEventDisabledMethods);

        Assert.NotNull(woundedPartyId);
        BattleJoinCancelled? cancelled = null;
        Server.Resolve<IMessageBroker>().Subscribe<BattleJoinCancelled>(payload => cancelled = payload.What);
        Server.NetworkSentMessages.Clear();

        client.Call(() => client.Resolve<INetwork>().SendAll(BattleStartTestRequest.Create(
            client, BattleStartMode.Mission, hostileAction.MapEventId, hostileAction.AttackerMobilePartyId)), MapEventDisabledMethods);

        var left = Server.NetworkSentMessages.GetMessages<NetworkPartyLeftBattle>().Single();
        Assert.Equal(woundedPartyId, left.PartyId);
        Assert.False(left.LeaveSiege);
        Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkStartAttackMission>());
        Assert.True(cancelled.HasValue);
        Assert.Equal(hostileAction.MapEventId, cancelled.Value.InstanceId);
        Assert.Equal("PlayerTwo", cancelled.Value.ControllerId);

        AssertHostileActionJoinerLeft(Server, hostileAction.MapEventId, hostileAction.AttackerPartyId, woundedPartyId!);
        foreach (var syncedClient in Clients)
        {
            AssertHostileActionJoinerLeft(syncedClient, hostileAction.MapEventId, hostileAction.AttackerPartyId, woundedPartyId!);
        }

        ServerBattleModeArbiter.Release(hostileAction.MapEventId);
    }

    [Fact]
    public void MultiPlayerRaid_WoundedParticipant_LateMissionJoin_KeepsExistingPlayerInBattle()
    {
        var firstClient = Clients.First();
        var secondClient = Clients.Last();
        var hostileAction = CreateHostileActionWithTwoPlayerParties(VillageHostileAction.Raid);
        string? firstPlayerMobilePartyId = null;
        string? secondPlayerMobilePartyId = null;
        string? firstPlayerPartyId = null;
        string? secondPlayerPartyId = null;

        Server.Call(() =>
        {
            var playerManager = Server.Resolve<IPlayerManager>();
            Assert.True(playerManager.TryGetPlayer("PlayerOne", out var firstPlayer));
            Assert.True(playerManager.TryGetPlayer("PlayerTwo", out var secondPlayer));
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(firstPlayer.HeroId, out var firstPlayerHero));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(firstPlayer.MobilePartyId, out var firstPlayerParty));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(secondPlayer.MobilePartyId, out var secondPlayerParty));
            Assert.False(firstPlayerHero.IsWounded);

            firstPlayerMobilePartyId = firstPlayer.MobilePartyId;
            secondPlayerMobilePartyId = secondPlayer.MobilePartyId;
            Assert.True(Server.ObjectManager.TryGetId(firstPlayerParty.Party, out firstPlayerPartyId));
            Assert.True(Server.ObjectManager.TryGetId(secondPlayerParty.Party, out secondPlayerPartyId));
        }, MapEventDisabledMethods);

        Assert.NotNull(firstPlayerMobilePartyId);
        Assert.NotNull(secondPlayerMobilePartyId);
        Assert.NotNull(firstPlayerPartyId);
        Assert.NotNull(secondPlayerPartyId);

        try
        {
            Server.NetworkSentMessages.Clear();
            firstClient.Call(() => firstClient.Resolve<INetwork>().SendAll(BattleStartTestRequest.Create(
                firstClient, BattleStartMode.Mission, hostileAction.MapEventId, firstPlayerMobilePartyId!)), MapEventDisabledMethods);
            Assert.True(Server.NetworkSentMessages.GetMessages<NetworkBattleStartReply>().Single().Accepted);
            Assert.Equal(2, Server.NetworkSentMessages.GetMessages<NetworkStartAttackMission>().Count());

            Server.Call(() =>
            {
                var playerManager = Server.Resolve<IPlayerManager>();
                Assert.True(playerManager.TryGetPlayer("PlayerOne", out var firstPlayer));
                Assert.True(Server.ObjectManager.TryGetObject<Hero>(firstPlayer.HeroId, out var firstPlayerHero));

                firstPlayerHero.HitPoints = 1;
                Assert.True(firstPlayerHero.IsWounded);
            }, MapEventDisabledMethods);

            Server.NetworkSentMessages.Clear();

            secondClient.Call(() => secondClient.Resolve<INetwork>().SendAll(BattleStartTestRequest.Create(
                secondClient, BattleStartMode.Mission, hostileAction.MapEventId, secondPlayerMobilePartyId!)), MapEventDisabledMethods);

            Assert.True(Server.NetworkSentMessages.GetMessages<NetworkBattleStartReply>().Single().Accepted);
            Assert.Equal(2, Server.NetworkSentMessages.GetMessages<NetworkStartAttackMission>().Count());
            Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkPartyLeftBattle>());
            AssertHostileActionJoinerPresent(Server, hostileAction.MapEventId, firstPlayerPartyId!);
            AssertHostileActionJoinerPresent(Server, hostileAction.MapEventId, secondPlayerPartyId!);
            foreach (var syncedClient in Clients)
            {
                AssertHostileActionJoinerPresent(syncedClient, hostileAction.MapEventId, firstPlayerPartyId!);
                AssertHostileActionJoinerPresent(syncedClient, hostileAction.MapEventId, secondPlayerPartyId!);
            }
        }
        finally
        {
            ServerBattleModeArbiter.Release(hostileAction.MapEventId);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SlowRaidMapEvent_InvalidDefenderJoinRequest_IsRejected(bool remote, bool safePassage)
    {
        var client = Clients.First();
        var context = CreateSlowRaidDefenderJoinContext(remote, safePassage);

        Server.NetworkSentMessages.Clear();
        client.Call(() => client.Resolve<INetwork>().SendAll(BattleJoinLeaveTestRequest.Join(
            client,
            context.MapEventId,
            context.JoinerPartyId,
            BattleSideEnum.Defender)), MapEventDisabledMethods);

        Assert.False(Server.NetworkSentMessages.GetMessages<NetworkJoinBattleReply>().Single().Accepted);
        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(context.MapEventId, out var mapEvent));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(context.JoinerMobilePartyId, out var joinerParty));
            Assert.True(mapEvent.IsActiveSlowVillageRaid());
            Assert.Null(joinerParty.MapEvent);
        }, MapEventDisabledMethods);
    }

    [Fact]
    public void SlowRaidMapEvent_CoastalDefenderAtPort_StartsResistance()
    {
        var client = Clients.First();
        var context = CreateSlowRaidDefenderJoinContext(coastal: true);

        Server.NetworkSentMessages.Clear();
        client.Call(() => client.Resolve<INetwork>().SendAll(BattleJoinLeaveTestRequest.Join(
            client,
            context.MapEventId,
            context.JoinerPartyId,
            BattleSideEnum.Defender)), MapEventDisabledMethods);

        Assert.True(Server.NetworkSentMessages.GetMessages<NetworkJoinBattleReply>().Single().Accepted);
        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(context.MapEventId, out var mapEvent));
            Assert.False(mapEvent.IsActiveSlowVillageRaid());
        }, MapEventDisabledMethods);
    }

    [Theory]
    [InlineData(VillageHostileAction.ForceVolunteers)]
    [InlineData(VillageHostileAction.ForceSupplies)]
    public void MultiPlayerNonRaidHostileAction_AttackMissionStart_IsRejected(VillageHostileAction action)
    {
        var client = Clients.First();
        var hostileAction = CreateHostileActionWithTwoPlayerParties(action);

        Server.NetworkSentMessages.Clear();

        client.Call(() => client.Resolve<INetwork>().SendAll(BattleStartTestRequest.Create(
            client, BattleStartMode.Mission, hostileAction.MapEventId, hostileAction.AttackerMobilePartyId)), MapEventDisabledMethods);

        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkStartAttackMission>());

        var reply = Server.NetworkSentMessages.GetMessages<NetworkBattleStartReply>().Single();
        Assert.False(reply.Accepted);

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(hostileAction.MapEventId, out var mapEvent));
            Assert.True(mapEvent.IsVillageHostileActionWithMultiplePlayerParties());
            Assert.True(mapEvent.IsUnsupportedMultiPlayerHostileAction());
        }, MapEventDisabledMethods);
    }

    [Fact]
    public void MultiPlayerRaid_BattleSimulationStart_IsAllowed()
    {
        var client = Clients.First();
        var hostileAction = CreateHostileActionWithTwoPlayerParties(VillageHostileAction.Raid);

        Server.NetworkSentMessages.Clear();

        client.Call(() => client.Resolve<INetwork>().SendAll(BattleStartTestRequest.Create(
            client, BattleStartMode.Simulation, hostileAction.MapEventId, hostileAction.AttackerMobilePartyId)), MapEventDisabledMethods);

        var open = Server.NetworkSentMessages.GetMessages<NetworkOpenBattleSimulation>().Single();
        Assert.Equal(hostileAction.MapEventId, open.MapEventId);

        var mode = Server.NetworkSentMessages.GetMessages<NetworkBattleModeSet>().Single();
        Assert.Equal(hostileAction.MapEventId, mode.MapEventId);
        Assert.Equal((int)BattleStartMode.Simulation, mode.Mode);

        var reply = Server.NetworkSentMessages.GetMessages<NetworkBattleStartReply>().Single();
        Assert.True(reply.Accepted);
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkBattleSimulationFinished>());

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(hostileAction.MapEventId, out var mapEvent));
            Assert.True(mapEvent.IsRaid);
            Assert.True(mapEvent.IsVillageHostileActionWithMultiplePlayerParties());
            Assert.True(mapEvent.BattleObserver is ForwardingBattleObserver);
        }, MapEventDisabledMethods);

        ServerBattleModeArbiter.Release(hostileAction.MapEventId);
    }

    [Theory]
    [InlineData(VillageHostileAction.ForceVolunteers)]
    [InlineData(VillageHostileAction.ForceSupplies)]
    public void MultiPlayerNonRaidHostileAction_BattleSimulationStart_IsRejected(VillageHostileAction action)
    {
        var client = Clients.First();
        var hostileAction = CreateHostileActionWithTwoPlayerParties(action);

        Server.NetworkSentMessages.Clear();

        client.Call(() => client.Resolve<INetwork>().SendAll(BattleStartTestRequest.Create(
            client, BattleStartMode.Simulation, hostileAction.MapEventId, hostileAction.AttackerMobilePartyId)), MapEventDisabledMethods);

        var finished = Server.NetworkSentMessages.GetMessages<NetworkBattleSimulationFinished>().Single();
        Assert.Equal(hostileAction.MapEventId, finished.MapEventId);
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkOpenBattleSimulation>());

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(hostileAction.MapEventId, out var mapEvent));
            Assert.True(mapEvent.IsVillageHostileActionWithMultiplePlayerParties());
            Assert.False(mapEvent.BattleObserver is ForwardingBattleObserver);
        }, MapEventDisabledMethods);
    }

    [Fact]
    public void RaidSimulation_WhenSecondPlayerJoins_OpensSimulationForJoiner()
    {
        var client = Clients.First();
        var (raiderHeroId, raiderMobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var (joinerHeroId, joinerMobilePartyId) = CreatePlayerHeroParty("PlayerTwo");
        TestEnvironment.ConnectRegisteredPlayer(client, "PlayerTwo");
        var defenderTroopId = TestEnvironment.CreateRegisteredObject<CharacterObject>();
        var target = CreateVillageTarget();
        var joinerPartyId = GetPartyBaseId(joinerMobilePartyId);
        string? mapEventId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(raiderHeroId, out var raiderHero));
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(joinerHeroId, out var joinerHero));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(raiderMobilePartyId, out var raiderParty));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetObject<CharacterObject>(defenderTroopId, out var defenderTroop));

            using (new AllowedThread())
            {
                raiderParty.MemberRoster.AddToCounts(raiderHero.CharacterObject, 1);
                joinerParty.MemberRoster.AddToCounts(joinerHero.CharacterObject, 1);
                raiderHero.PartyBelongedTo = raiderParty;
                joinerHero.PartyBelongedTo = joinerParty;
            }

            var raidMapEvent = CreateHostileActionMapEvent(raiderParty.Party, settlement.Party, VillageHostileAction.Raid);
            Assert.NotNull(raidMapEvent);
            raidMapEvent.MapEventVisual = MockMapEventVisual();
            raidMapEvent.BattleObserver = new ForwardingBattleObserver(Server.ObjectManager);

            var defenderParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
            defenderParty.MemberRoster.AddToCounts(defenderTroop, 1);
            AddSyntheticMapEventParty(raidMapEvent.DefenderSide, defenderParty.Party);

            Assert.False(raidMapEvent.IsVillageHostileActionWithMultiplePlayerParties());
            Assert.True(Server.ObjectManager.TryGetId(raidMapEvent, out mapEventId));
        }, MapEventDisabledMethods);

        Assert.NotNull(mapEventId);

        Server.NetworkSentMessages.Clear();

        client.Call(() => client.Resolve<INetwork>().SendAll(BattleJoinLeaveTestRequest.Join(
            client,
            mapEventId!,
            joinerPartyId,
            BattleSideEnum.Attacker)), MapEventDisabledMethods);

        var open = Server.NetworkSentMessages.GetMessages<NetworkOpenBattleSimulation>().Single();
        Assert.Equal(mapEventId, open.MapEventId);

        var warDeclared = Server.NetworkSentMessages.GetMessages<NetworkDeclareWar>().Single();
        Assert.Equal(GetMobilePartyMapFactionId(Server, joinerMobilePartyId), warDeclared.Faction1Id);
        Assert.Equal(target.OwnerFactionId, warDeclared.Faction2Id);

        AssertWarDeclared(Server, joinerMobilePartyId, target.OwnerFactionId);
        foreach (var syncedClient in Clients)
        {
            AssertWarDeclared(syncedClient, joinerMobilePartyId, target.OwnerFactionId);
        }

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(mapEventId!, out var mapEvent));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));
            Assert.Same(mapEvent, joinerParty.MapEvent);
            Assert.True(mapEvent.IsVillageHostileActionWithMultiplePlayerParties());
            Assert.True(mapEvent.BattleObserver is ForwardingBattleObserver);
            Assert.False(mapEvent.HasWinner);
        }, MapEventDisabledMethods);
    }

    [Theory]
    [InlineData(VillageHostileAction.Raid)]
    [InlineData(VillageHostileAction.ForceVolunteers)]
    [InlineData(VillageHostileAction.ForceSupplies)]
    public void HostileActionJoinerLeave_RemovesOnlyJoinerAndKeepsEvent(VillageHostileAction action)
    {
        var client = Clients.First();
        var hostileAction = CreateHostileActionWithOnePlayerParty(action);
        var (_, joinerMobilePartyId) = CreatePlayerHeroParty("PlayerTwo");
        TestEnvironment.ConnectRegisteredPlayer(client, "PlayerTwo");
        var joinerPartyId = GetPartyBaseId(joinerMobilePartyId);

        Server.NetworkSentMessages.Clear();

        client.Call(() => client.Resolve<INetwork>().SendAll(BattleJoinLeaveTestRequest.Join(
            client,
            hostileAction.MapEventId,
            joinerPartyId,
            BattleSideEnum.Attacker)), MapEventDisabledMethods);

        var warDeclared = Server.NetworkSentMessages.GetMessages<NetworkDeclareWar>().Single();
        Assert.Equal(GetMobilePartyMapFactionId(Server, joinerMobilePartyId), warDeclared.Faction1Id);
        Assert.Equal(hostileAction.OwnerFactionId, warDeclared.Faction2Id);

        AssertWarDeclared(Server, joinerMobilePartyId, hostileAction.OwnerFactionId);
        foreach (var syncedClient in Clients)
        {
            AssertWarDeclared(syncedClient, joinerMobilePartyId, hostileAction.OwnerFactionId);
        }

        AssertHostileActionJoinerPresent(Server, hostileAction.MapEventId, joinerPartyId);
        foreach (var joinedClient in Clients)
        {
            AssertHostileActionJoinerPresent(joinedClient, hostileAction.MapEventId, joinerPartyId);
        }

        Server.NetworkSentMessages.Clear();
        client.Call(() => client.Resolve<INetwork>().SendAll(BattleJoinLeaveTestRequest.Leave(
            client, joinerPartyId, hostileAction.MapEventId)), MapEventDisabledMethods);

        var left = Server.NetworkSentMessages.GetMessages<NetworkPartyLeftBattle>().Single();
        Assert.Equal(joinerPartyId, left.PartyId);
        Assert.False(left.LeaveSiege);
        var leaveRequest = client.NetworkSentMessages.GetMessages<NetworkRequestLeaveBattle>().Single();
        var leaveResult = Server.NetworkSentMessages.GetMessages<NetworkLeaveBattleResult>().Single();
        Assert.Equal(leaveRequest.Header.SessionId, left.SessionId);
        Assert.Equal(leaveRequest.Header.RequestId, left.AuthorityRequestId);
        Assert.Equal(hostileAction.MapEventId, left.MapEventId);
        Assert.Equal(leaveRequest.Header.RequestId, leaveResult.Header.RequestId);
        Assert.Equal(AuthorityResultStatus.Accepted, leaveResult.Header.Status);

        AssertHostileActionJoinerLeft(Server, hostileAction.MapEventId, hostileAction.AttackerPartyId, joinerPartyId);
        foreach (var leftClient in Clients)
        {
            AssertHostileActionJoinerLeft(leftClient, hostileAction.MapEventId, hostileAction.AttackerPartyId, joinerPartyId);
        }
    }

    [Fact]
    public void VillageRecovery_RegisterEventsRunsOnlyOnServer()
    {
        Server.Call(() => Assert.True(InvokeVillageHealRegisterEventsPrefix()));

        foreach (var client in Clients)
        {
            client.Call(() => Assert.False(InvokeVillageHealRegisterEventsPrefix()));
        }
    }

    private void AssertHostileActionJoinerPresent(
        EnvironmentInstance instance,
        string mapEventId,
        string joinerPartyId)
    {
        instance.Call(() =>
        {
            Assert.True(instance.ObjectManager.TryGetObject<MapEvent>(mapEventId, out var mapEvent));
            Assert.True(instance.ObjectManager.TryGetObject<PartyBase>(joinerPartyId, out var joinerParty));

            Assert.Same(mapEvent, joinerParty.MapEvent);
            Assert.True(mapEvent.IsVillageHostileActionWithMultiplePlayerParties());
        }, MapEventDisabledMethods);
    }

    private void AssertHostileActionJoinerLeft(
        EnvironmentInstance instance,
        string mapEventId,
        string attackerPartyId,
        string joinerPartyId)
    {
        instance.Call(() =>
        {
            Assert.True(instance.ObjectManager.TryGetObject<MapEvent>(mapEventId, out var mapEvent));
            Assert.True(instance.ObjectManager.TryGetObject<PartyBase>(attackerPartyId, out var attackerParty));
            Assert.True(instance.ObjectManager.TryGetObject<PartyBase>(joinerPartyId, out var joinerParty));

            Assert.Same(mapEvent, attackerParty.MapEvent);
            Assert.Null(joinerParty.MapEventSide);
            Assert.True(mapEvent.IsVillageHostileAction());
            Assert.False(mapEvent.IsVillageHostileActionWithMultiplePlayerParties());
        }, MapEventDisabledMethods);
    }

    private void AssertRaidPartyMovedToVillageGate(
        EnvironmentInstance instance,
        string mobilePartyId,
        string settlementId)
    {
        instance.Call(() =>
        {
            Assert.True(instance.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(instance.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));

            Assert.Null(mobileParty.CurrentSettlement);
            Assert.True(mobileParty.Position.Distance(settlement.GatePosition) < 0.001f);
            Assert.True(mobileParty.MoveTargetPoint.Distance(mobileParty.Position) < 0.001f);
            Assert.Equal(MoveModeType.Hold, mobileParty.PartyMoveMode);
        }, MapEventDisabledMethods);
    }

    private void AssertRaidFinalizedOutcome(
        EnvironmentInstance instance,
        string mapEventId,
        string settlementId,
        string villageId,
        Village.VillageStates expectedVillageState,
        float expectedSettlementHitPoints)
    {
        instance.Call(() =>
        {
            Assert.False(instance.ObjectManager.TryGetObject<MapEvent>(mapEventId, out var _));
            Assert.True(instance.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
            Assert.True(instance.ObjectManager.TryGetObject<Village>(villageId, out var village));

            Assert.Equal(expectedVillageState, village.VillageState);
            Assert.Equal(expectedSettlementHitPoints, settlement.SettlementHitPoints, 3);
        });
    }

    private void AssertVillageStateAndHitPoints(
        EnvironmentInstance instance,
        string settlementId,
        string villageId,
        Village.VillageStates expectedVillageState,
        float expectedSettlementHitPoints)
    {
        instance.Call(() =>
        {
            Assert.True(instance.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
            Assert.True(instance.ObjectManager.TryGetObject<Village>(villageId, out var village));

            Assert.Equal(expectedVillageState, village.VillageState);
            Assert.Equal(expectedSettlementHitPoints, settlement.SettlementHitPoints, 3);
        });
    }

    private void AssertHostileActionMapEvent(
        EnvironmentInstance instance,
        string mapEventId,
        string componentId,
        Type expectedComponentType,
        string villageId,
        Village.VillageStates expectedVillageState)
    {
        instance.Call(() =>
        {
            Assert.True(instance.ObjectManager.TryGetObject<MapEvent>(mapEventId, out var mapEvent));
            Assert.True(instance.ObjectManager.TryGetObject<MapEventComponent>(componentId, out var component));
            Assert.True(instance.ObjectManager.TryGetObject<Village>(villageId, out var village));

            Assert.Same(component, mapEvent.Component);
            Assert.IsType(expectedComponentType, component);
            Assert.Equal(expectedVillageState, village.VillageState);
        });
    }

    private void AssertRaidProgressOutcome(
        EnvironmentInstance instance,
        string mapEventId,
        string componentId,
        string mobilePartyId,
        string settlementId,
        string villageId,
        string itemId)
    {
        instance.Call(() =>
        {
            Assert.True(instance.ObjectManager.TryGetObject<MapEvent>(mapEventId, out var mapEvent));
            Assert.True(instance.ObjectManager.TryGetObject<MapEventComponent>(componentId, out var component));
            Assert.True(instance.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(instance.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
            Assert.True(instance.ObjectManager.TryGetObject<Village>(villageId, out var village));
            Assert.True(instance.ObjectManager.TryGetObject<ItemObject>(itemId, out var item));

            var raidComponent = Assert.IsType<RaidEventComponent>(component);
            Assert.Same(component, mapEvent.Component);
            Assert.True(mapEvent.WasEverInLootingPhase);
            Assert.True(raidComponent.RaidDamage > 0);
            Assert.True(
                settlement.SettlementHitPoints < 1f,
                $"settlement hit points should replicate raid damage; actual={settlement.SettlementHitPoints:R}");
            Assert.True(village.Hearth < 100f);
            Assert.NotNull(raidComponent._raidProductionRewards);
            Assert.True(GetItemAmount(raidComponent._raidProductionRewards, item) > 0);
        });
    }

    private void AssertForceSuppliesOutcome(
        EnvironmentInstance instance,
        string mobilePartyId,
        string settlementId,
        string itemId)
    {
        instance.Call(() =>
        {
            Assert.True(instance.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(instance.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
            Assert.True(instance.ObjectManager.TryGetObject<ItemObject>(itemId, out var item));

            Assert.Equal(40, GetItemAmount(mobileParty.Party, item));
            Assert.Equal(80, mobileParty.LeaderHero.Gold);
            Assert.Equal(0.2f, settlement.SettlementHitPoints, 3);
        });
    }

    private void AssertForceSuppliesGoldAndHitPointsOutcome(
        EnvironmentInstance instance,
        string mobilePartyId,
        string settlementId)
    {
        // Lord gold is coalesced; drain the buffer before reading it on a client.
        TestEnvironment.FlushCoalescer();
        instance.Call(() =>
        {
            Assert.True(instance.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(instance.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));

            Assert.Equal(80, mobileParty.LeaderHero.Gold);
            Assert.Equal(0.2f, settlement.SettlementHitPoints, 3);
        });
    }

    private void AssertForceVolunteersOutcome(
        EnvironmentInstance instance,
        string mobilePartyId,
        string settlementId,
        string villageId,
        string troopId)
    {
        // Regular-troop roster changes are coalesced; drain the buffer before reading them on a client.
        TestEnvironment.FlushCoalescer();
        instance.Call(() =>
        {
            Assert.True(instance.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(instance.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
            Assert.True(instance.ObjectManager.TryGetObject<Village>(villageId, out var village));
            Assert.True(instance.ObjectManager.TryGetObject<CharacterObject>(troopId, out var troop));

            Assert.Equal(3, mobileParty.MemberRoster.GetTroopCount(troop));
            Assert.Equal(0.2f, settlement.SettlementHitPoints, 3);
            Assert.Equal(89f, village.Hearth);
        });
    }

    private static int GetItemAmount(PartyBase party, ItemObject itemObject)
    {
        foreach (var item in party.ItemRoster)
        {
            if (item.EquipmentElement.Item == itemObject)
                return item.Amount;
        }

        return 0;
    }

    private static int GetItemAmount(ItemRoster roster, ItemObject itemObject)
    {
        foreach (var item in roster)
        {
            if (item.EquipmentElement.Item == itemObject)
                return item.Amount;
        }

        return 0;
    }

    private static int GetItemAmount(Dictionary<ItemObject, float> rewards, ItemObject itemObject)
    {
        return rewards.TryGetValue(itemObject, out var amount) ? (int)amount : 0;
    }

    private void RemoveNativeItemObjectFromObjectManagers(string stringId)
    {
        RemoveNativeItemObjectFromObjectManager(Server, stringId);

        foreach (var client in Clients)
        {
            RemoveNativeItemObjectFromObjectManager(client, stringId);
        }
    }

    private static void RemoveNativeItemObjectFromObjectManager(EnvironmentInstance instance, string stringId)
    {
        instance.Call(() =>
        {
            var item = GetNativeItemObject(stringId);
            if (item != null)
                instance.ObjectManager.Remove(item);
        });
    }

    private static ItemObject GetNativeItemObject(string stringId)
    {
        return MBObjectManager.Instance.GetObject<ItemObject>(stringId) ??
               MBObjectManager.Instance.GetObjectTypeList<ItemObject>().FirstOrDefault(item => item.StringId == stringId);
    }

    private void AddServerCooldown(string settlementId)
    {
        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));

            Server.Resolve<IVillageHostileActionInterface>().ApplyCooldowns(new[]
            {
                new VillageHostileActionCooldownData(settlementId, CampaignTime.DaysFromNow(10).NumTicks),
            });
        });
    }

    private void AssertCooldownBroadcast(string settlementId)
    {
        var cooldowns = Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionCooldowns>().Single();
        Assert.Contains(cooldowns.Cooldowns, c => c.SettlementId == settlementId);
    }

    private void AssertCooldownSynced(EnvironmentInstance instance, string settlementId)
    {
        instance.Call(() =>
        {
            Assert.True(instance.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
            Assert.True(instance.Resolve<IVillageHostileActionInterface>().TryGetForceActionCooldown(settlement, out var cooldownUntil));
            Assert.False(cooldownUntil.IsPast);
        });
    }

    private static string GetMobilePartyMapFactionId(EnvironmentInstance instance, string mobilePartyId)
    {
        string factionId = null;
        instance.Call(() =>
        {
            Assert.True(instance.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            var mapFaction = mobileParty.MapFaction?.MapFaction;
            Assert.NotNull(mapFaction);
            Assert.True(instance.ObjectManager.TryGetId(mapFaction, out factionId));
        });

        return factionId;
    }

    private void AssertWarDeclared(EnvironmentInstance instance, string mobilePartyId, string defenderFactionId)
    {
        instance.Call(() =>
        {
            Assert.True(instance.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(TryGetFaction(instance, defenderFactionId, out var defenderFaction));
            var attackerFaction = mobileParty.MapFaction?.MapFaction;
            var defenderMapFaction = defenderFaction.MapFaction;
            Assert.NotNull(attackerFaction);
            Assert.NotNull(defenderMapFaction);
            var shallowStance = Campaign.Current.Models.DiplomacyModel.GetShallowDiplomaticStance(attackerFaction, defenderMapFaction);
            var stanceLink = FactionManager.Instance.GetStanceLinkInternal(attackerFaction, defenderMapFaction);
            Assert.True(
                VillageHostileFactionStanceHelper.HasWarStance(attackerFaction, defenderMapFaction),
                $"Expected {GetFactionDebugName(instance, attackerFaction)} to be at war with {GetFactionDebugName(instance, defenderMapFaction)}. AttackerEliminated={attackerFaction.IsEliminated}, DefenderEliminated={defenderMapFaction.IsEliminated}, Shallow={shallowStance?.ToString() ?? "null"}, LinkWar={stanceLink.IsAtWar}, LinkStance={stanceLink.StanceType}, AttackerWarsContainsDefender={attackerFaction.FactionsAtWarWith?.Contains(defenderMapFaction) == true}, DefenderWarsContainsAttacker={defenderMapFaction.FactionsAtWarWith?.Contains(attackerFaction) == true}");
        });
    }

    private static string GetFactionDebugName(EnvironmentInstance instance, IFaction faction)
    {
        if (instance.ObjectManager.TryGetId(faction, out var factionId))
            return $"{faction.GetType().Name}:{factionId}";

        return faction.GetType().Name;
    }

    private static bool TryGetFaction(EnvironmentInstance instance, string factionId, out IFaction faction)
    {
        if (instance.ObjectManager.TryGetObject<Kingdom>(factionId, out var kingdom))
        {
            faction = kingdom;
            return true;
        }

        if (instance.ObjectManager.TryGetObject<Clan>(factionId, out var clan))
        {
            faction = clan;
            return true;
        }

        faction = null;
        return false;
    }

    private static bool InvokeMapEventUpdatePrefix(MapEvent mapEvent)
    {
        var patchType = AccessTools.TypeByName("GameInterface.Services.MapEvents.Patches.MapEventPatches");
        var prefix = AccessTools.Method(patchType, "PrefixUpdate");
        Assert.NotNull(prefix);

        return (bool)prefix.Invoke(null, new object[] { mapEvent })!;
    }

    private static bool InvokeCanPartyJoinBattlePostfix(MapEvent mapEvent, PartyBase party, bool initialResult)
    {
        var patchType = AccessTools.TypeByName("GameInterface.Services.MapEvents.Patches.InteractionPatches");
        var postfix = AccessTools.Method(patchType, "Postfix_CanPartyJoinBattle");
        Assert.NotNull(postfix);

        var args = new object[] { mapEvent, party, initialResult };
        postfix.Invoke(null, args);
        return (bool)args[2];
    }

    private static bool InvokeStartSettlementEncounterPrefix(MobileParty attackerParty, Settlement settlement)
    {
        var patchType = AccessTools.TypeByName("GameInterface.Services.MapEvents.Patches.EncounterManagerPatches");
        var prefix = AccessTools.Method(patchType, "Prefix");
        Assert.NotNull(prefix);

        return (bool)prefix.Invoke(null, new object[] { attackerParty, settlement })!;
    }

    private static bool InvokeStartPartyEncounterPrefix(PartyBase attackerParty, PartyBase defenderParty)
    {
        var patchType = AccessTools.TypeByName("GameInterface.Services.MapEvents.Patches.EncounterManagerPatches");
        var prefix = AccessTools.Method(patchType, "StartPartyEncounterPrefix");
        Assert.NotNull(prefix);

        return (bool)prefix.Invoke(null, new object[] { attackerParty, defenderParty })!;
    }

    private static bool InvokeRestartPlayerEncounterPrefix(PartyBase attackerParty, PartyBase defenderParty)
    {
        var patchType = AccessTools.TypeByName("GameInterface.Services.MapEvents.Patches.EncounterManagerPatches");
        var prefix = AccessTools.Method(patchType, "RestartPlayerEncounterPrefix");
        Assert.NotNull(prefix);

        return (bool)prefix.Invoke(null, new object[] { attackerParty, defenderParty })!;
    }

    private static void InvokeVillageHealDailyTick(Settlement settlement)
    {
        var behavior = new VillageHealCampaignBehavior();
        var method = AccessTools.Method(typeof(VillageHealCampaignBehavior), "DailyTickSettlement");
        Assert.NotNull(method);

        method.Invoke(behavior, new object[] { settlement });
    }

    private static bool InvokeRaidJoinEncounterConsequencePrefix(string methodName)
    {
        var originalMethod = AccessTools.Method(typeof(EncounterGameMenuBehavior), methodName);
        Assert.NotNull(originalMethod);

        var patchType = AccessTools.TypeByName("GameInterface.Services.MapEvents.Patches.RaidJoinEncounterPatch");
        var prefix = AccessTools.Method(patchType, "Prefix");
        Assert.NotNull(prefix);

        return (bool)prefix.Invoke(null, new object[] { originalMethod })!;
    }

    private static (bool Result, bool IsEnabled) InvokeRaidJoinEncounterConditionPostfix(
        string methodName,
        bool initialResult)
    {
        var originalMethod = AccessTools.Method(typeof(EncounterGameMenuBehavior), methodName);
        Assert.NotNull(originalMethod);

        var patchType = AccessTools.TypeByName("GameInterface.Services.MapEvents.Patches.RaidJoinEncounterConditionPatch");
        var postfix = AccessTools.Method(patchType, "Postfix");
        Assert.NotNull(postfix);

        var args = new MenuCallbackArgs((MenuContext)null, null)
        {
            IsEnabled = true,
        };
        object[] invocation = { originalMethod, args, initialResult };
        postfix.Invoke(null, invocation);
        return ((bool)invocation[2], args.IsEnabled);
    }
    private static bool InvokeVillageHealRegisterEventsPrefix()
    {
        var patchType = AccessTools.TypeByName("GameInterface.Services.Villages.Patches.DisableVillageHealCampaignBehavior");
        var prefix = AccessTools.Method(patchType, "Prefix");
        Assert.NotNull(prefix);

        return (bool)prefix.Invoke(null, Array.Empty<object>())!;
    }

    private void RegisterPeer(EnvironmentInstance client, string controllerId)
    {
        EnsurePeerEndpoint(client);
        client.Resolve<IControllerIdProvider>().SetControllerId(controllerId);
        Server.SimulateMessage(this, new PlayerConnected(client.NetPeer));
        Server.SimulateMessage(client.NetPeer, new NetworkClientValidate(controllerId));
    }

    private static void EnsurePeerEndpoint(EnvironmentInstance client)
    {
        var endPoint = (IPEndPoint)client.NetPeer;
        if (endPoint.Address != null)
            return;

        endPoint.Address = IPAddress.Loopback;
        endPoint.Port = 1 + (Interlocked.Increment(ref peerPortCounter) % 60000);
    }

    private RaidDefenderJoinContext CreateSlowRaidDefenderJoinContext(
        bool remote = false,
        bool safePassage = false,
        bool coastal = false)
    {
        var client = Clients.First();
        var (_, raiderMobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var (_, joinerMobilePartyId) = CreatePlayerHeroParty("PlayerTwo");
        TestEnvironment.ConnectRegisteredPlayer(client, "PlayerTwo");
        var target = CreateVillageTarget();
        var joinerPartyId = GetPartyBaseId(joinerMobilePartyId);
        string? mapEventId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(raiderMobilePartyId, out var raiderParty));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetObject<Clan>(target.OwnerClanId, out var ownerClan));

            joinerParty.ActualClan = ownerClan;
            if (coastal)
            {
                var portPosition = new CampaignVec2(new Vec2(100f, 100f), false);
                settlement.SetPortPosition(portPosition);
                joinerParty.SetTargetSettlement(settlement, isTargetingPort: true);
                joinerParty.Position = portPosition;
            }
            else
            {
                joinerParty.Position = remote
                    ? new CampaignVec2(new Vec2(1000f, 1000f), true)
                    : settlement.GatePosition;
            }

            joinerParty.IsActive = true;
            raiderParty.IsActive = true;
            VillageHostileFactionStanceHelper.ApplyWarStance(raiderParty.MapFaction, settlement.MapFaction);
            if (safePassage)
                raiderParty.MapFaction.NotAttackableByPlayerUntilTime = CampaignTime.DaysFromNow(1f);

            var mapEvent = CreateHostileActionMapEvent(raiderParty.Party, settlement.Party, VillageHostileAction.Raid);
            Assert.True(mapEvent.IsActiveSlowVillageRaid());
            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out mapEventId));
        }, MapEventDisabledMethods);

        Assert.NotNull(mapEventId);
        return new RaidDefenderJoinContext(mapEventId!, joinerMobilePartyId, joinerPartyId);
    }

    private VillageTarget CreateVillageTarget()
    {
        var settlementId = TestEnvironment.CreateRegisteredObject<Settlement>();
        var villageId = TestEnvironment.CreateRegisteredObject<Village>();
        var boundSettlementId = TestEnvironment.CreateRegisteredObject<Settlement>();
        var boundTownId = TestEnvironment.CreateRegisteredObject<Town>();
        var boundOwnerClanId = TestEnvironment.CreateRegisteredObject<Clan>();
        var boundOwnerKingdomId = TestEnvironment.CreateRegisteredObject<Kingdom>();
        var boundOwnerHeroId = TestEnvironment.CreateRegisteredObject<Hero>();
        string? settlementPartyId = null;
        string? ownerFactionId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetObject<Village>(villageId, out var village));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(boundSettlementId, out var boundSettlement));
            Assert.True(Server.ObjectManager.TryGetObject<Town>(boundTownId, out var boundTown));
            Assert.True(Server.ObjectManager.TryGetObject<Clan>(boundOwnerClanId, out var boundOwnerClan));
            Assert.True(Server.ObjectManager.TryGetObject<Kingdom>(boundOwnerKingdomId, out var boundOwnerKingdom));
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(boundOwnerHeroId, out var boundOwnerHero));

            using (new AllowedThread())
            {
                boundSettlement.SetSettlementComponent(boundTown);
                boundOwnerHero.Clan = boundOwnerClan;
                boundOwnerClan.Kingdom = boundOwnerKingdom;
                boundOwnerClan.SetLeader(boundOwnerHero);
                boundOwnerKingdom.RulingClan = boundOwnerClan;
                boundTown.OwnerClan = boundOwnerClan;
                boundTown.IsOwnerUnassigned = false;
                settlement.Party = new PartyBase(settlement);
                settlement.Village = village;
                // SettlementComponent.SetOwner reads Settlement.Party. Build the party first so
                // vanilla RaidEventComponent.CreateRaidEvent can resolve village.Settlement.
                settlement.SetSettlementComponent(village);
                village.Owner = settlement.Party;
                village.Bound = boundSettlement;
                village.VillageState = Village.VillageStates.Normal;
                village.Hearth = 100f;
            }

            Assert.True(Server.ObjectManager.AddNewObject(settlement.Party, out settlementPartyId));
            var ownerFaction = settlement.MapFaction?.MapFaction;
            Assert.NotNull(ownerFaction);
            Assert.True(Server.ObjectManager.TryGetId(ownerFaction, out ownerFactionId));
        });

        Assert.NotNull(settlementPartyId);
        foreach (var client in Clients)
        {
            client.Call(() =>
            {
                Assert.True(client.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
                Assert.True(client.ObjectManager.TryGetObject<Village>(villageId, out var village));
                Assert.True(client.ObjectManager.TryGetObject<Clan>(boundOwnerClanId, out var boundOwnerClan));
                Assert.True(client.ObjectManager.TryGetObject<Hero>(boundOwnerHeroId, out var boundOwnerHero));

                using (new AllowedThread())
                {
                    boundOwnerHero.Clan = boundOwnerClan;
                    boundOwnerClan.SetLeader(boundOwnerHero);
                }

                if (!client.ObjectManager.TryGetObject<PartyBase>(settlementPartyId!, out var settlementParty))
                {
                    using (new AllowedThread())
                    {
                        settlementParty = new PartyBase(settlement);
                    }

                    Assert.True(client.ObjectManager.AddExisting(settlementPartyId!, settlementParty));
                }

                using (new AllowedThread())
                {
                    settlement.Party = settlementParty;
                    settlement.Village = village;
                    settlement.SetSettlementComponent(village);
                    village.Owner = settlementParty;
                }
            });
        }

        return new VillageTarget(settlementId, villageId, settlementPartyId!, ownerFactionId!, boundOwnerClanId);
    }

    private void RequestConversation(EnvironmentInstance client, string attackerPartyId, string defenderPartyId)
    {
        client.Call(() => client.Resolve<ConversationRequestHandler>().SubmitConversation(new NetworkRequestConversation(
            defenderPartyId,
            attackerPartyId,
            forcePlayerOutFromSettlement: false,
            ConversationRestartSource.PlayerEncounter,
            false)));
    }

    private void RequestHostileAction(
        EnvironmentInstance client,
        VillageHostileAction action,
        string mobilePartyId,
        string settlementId)
    {
        var disabledMethods = MapEventDisabledMethods
            .Append(AccessTools.Method(typeof(BeHostileAction), nameof(BeHostileAction.ApplyEncounterHostileAction)))
            .Append(AccessTools.Method(typeof(GameMenu), nameof(GameMenu.SwitchToMenu)))
            .ToList();

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(client.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
            client.Resolve<IMessageBroker>().Publish(this,
                new VillageHostileActionAttempted(action, mobileParty, settlement));
        }, disabledMethods);
    }

    private void AssertHostileActionRejected(string reasonCode)
    {
        var result = Server.NetworkSentMessages.GetMessages<NetworkVillageHostileActionResult>().Single();
        Assert.Equal(AuthorityResultStatus.Rejected, result.Header.Status);
        Assert.Equal(reasonCode, result.Header.ReasonCode);
    }

    private static void SetMapEventCreationTimeout(EnvironmentInstance instance, TimeSpan timeout)
    {
        instance.Call(() =>
        {
            var coordinator = instance.Resolve<MapEventCreationCoordinator>();
            var handleField = AccessTools.Field(typeof(MapEventCreationCoordinator), "mapEventRoute");
            Assert.NotNull(handleField);
            object handle = handleField.GetValue(coordinator);
            var routeField = handle.GetType().GetField("route", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(routeField);
            object route = routeField.GetValue(handle);
            object policy = route.GetType().GetProperty("TimeoutPolicy")?.GetValue(route);
            Assert.NotNull(policy);
            var responseTimeout = policy.GetType().GetField("<ResponseTimeout>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var applyTimeout = policy.GetType().GetField("<ApplyTimeout>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(responseTimeout);
            Assert.NotNull(applyTimeout);
            responseTimeout.SetValue(policy, timeout);
            applyTimeout.SetValue(policy, timeout);
        });
    }

    private AuthorityRequestHeader CreateMapEventRequestHeader(long requestId)
    {
        GameInterface.Configuration.ModConfigSnapshot snapshot = null;
        Server.Call(() => Assert.True(Server.Resolve<GameInterface.Configuration.IModConfigAuthority>()
            .TryGetCurrent(out snapshot)));
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private void AssertCanStartHostileAction(string mobilePartyId, string settlementId, VillageHostileAction action)
    {
        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));

            Assert.True(Server.Resolve<IVillageHostileActionInterface>().CanStartHostileAction(
                mobileParty,
                settlement,
                action,
                out var reason));
            Assert.Equal(VillageHostileActionDeniedReason.Invalid, reason);
        });
    }

    private void ApproveMapEventStart(string mobilePartyId, string settlementId, VillageHostileAction action)
    {
        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));

            Server.Resolve<IVillageHostileActionInterface>().ApproveMapEventStart(mobileParty.Party, settlement, action);
            Assert.True(Server.Resolve<IVillageHostileActionInterface>().MarkApprovedMapEventStartPublished(
                mobileParty.Party, settlement, action));
        });
    }

    private (bool Approved, VillageHostileActionDeniedReason Reason) ConsumeApprovedMapEventStart(
        string mobilePartyId,
        string settlementPartyId,
        BattleCreationFlags flags)
    {
        var approved = false;
        var reason = VillageHostileActionDeniedReason.Invalid;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.True(Server.ObjectManager.TryGetObject<PartyBase>(settlementPartyId, out var settlementParty));

            approved = Server.Resolve<IVillageHostileActionInterface>().TryConsumeApprovedMapEventStart(
                mobileParty.Party,
                settlementParty,
                flags,
                out reason);
        });

        return (approved, reason);
    }

    private string GetPartyBaseId(string mobilePartyId)
    {
        string? partyBaseId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var party));
            Assert.True(Server.ObjectManager.TryGetId(party.Party, out partyBaseId));
        });

        Assert.NotNull(partyBaseId);
        return partyBaseId!;
    }

    private string RegisterMobilePartyItemRoster(string mobilePartyId)
    {
        string? itemRosterId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.NotNull(mobileParty.Party?.ItemRoster);

            itemRosterId = Server.ObjectManager.TryGetId(mobileParty.Party.ItemRoster, out var existingItemRosterId)
                ? existingItemRosterId
                : $"{nameof(ItemRoster)}_{mobileParty.StringId}";
        });

        Assert.NotNull(itemRosterId);
        RegisterMobilePartyItemRoster(Server, mobilePartyId, itemRosterId!);
        foreach (var client in Clients)
        {
            RegisterMobilePartyItemRoster(client, mobilePartyId, itemRosterId!);
        }

        return itemRosterId!;
    }

    private static void RegisterMobilePartyItemRoster(
        EnvironmentInstance instance,
        string mobilePartyId,
        string itemRosterId)
    {
        instance.Call(() =>
        {
            Assert.True(instance.ObjectManager.TryGetObject<MobileParty>(mobilePartyId, out var mobileParty));
            Assert.NotNull(mobileParty.Party?.ItemRoster);

            var itemRoster = mobileParty.Party.ItemRoster;
            if (instance.ObjectManager.TryGetId(itemRoster, out var existingItemRosterId))
            {
                if (existingItemRosterId == itemRosterId)
                    return;

                Assert.True(instance.ObjectManager.Remove(itemRoster));
            }

            if (instance.ObjectManager.Contains(itemRosterId))
            {
                Assert.True(instance.ObjectManager.TryGetObject<ItemRoster>(itemRosterId, out var registeredItemRoster));
                if (ReferenceEquals(registeredItemRoster, itemRoster))
                    return;

                Assert.True(instance.ObjectManager.Remove(registeredItemRoster));
            }

            Assert.True(instance.ObjectManager.AddExisting(itemRosterId, itemRoster));
        });
    }

    private MapEvent CreateHostileActionMapEvent(PartyBase attacker, PartyBase defender, VillageHostileAction action)
    {
        var mapEvent = GameObjectCreator.CreateInitializedObject<MapEvent>();
        mapEvent.MapEventVisual = MockMapEventVisual();

        MapEventComponent component;
        MapEvent.BattleTypes battleType;
        switch (action)
        {
            case VillageHostileAction.Raid:
                component = new RaidEventComponent(mapEvent);
                battleType = MapEvent.BattleTypes.Raid;
                break;
            case VillageHostileAction.ForceVolunteers:
                component = new ForceVolunteersEventComponent(mapEvent);
                battleType = (MapEvent.BattleTypes)3;
                break;
            case VillageHostileAction.ForceSupplies:
                component = new ForceSuppliesEventComponent(mapEvent);
                battleType = (MapEvent.BattleTypes)4;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }

        mapEvent.Initialize(attacker, defender, component, battleType);
        mapEvent.MapEventSettlement = defender.Settlement;
        mapEvent.Position = defender.Position;
        mapEvent.State = MapEventState.Wait;

        SetVillageStateForHostileAction(defender.Settlement, action);
        mapEvent.MapEventVisual = null;
        Campaign.Current.MapEventManager.OnMapEventCreated(mapEvent);
        return mapEvent;
    }

    private static void SetVillageStateForHostileAction(Settlement settlement, VillageHostileAction action)
    {
        switch (action)
        {
            case VillageHostileAction.Raid:
                settlement.Village.VillageState = Village.VillageStates.BeingRaided;
                break;
            case VillageHostileAction.ForceVolunteers:
                settlement.Village.VillageState = Village.VillageStates.ForcedForVolunteers;
                break;
            case VillageHostileAction.ForceSupplies:
                settlement.Village.VillageState = Village.VillageStates.ForcedForSupplies;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private static void AddSyntheticMapEventParty(MapEventSide side, PartyBase party)
    {
        party._mapEventSide = side;
        var mapEventParty = new MapEventParty(party);
        side._battleParties.Add(mapEventParty);
        MessageBroker.Instance.Publish(side, new MapEventPartyBattlePartyAdded(side, mapEventParty));
    }

    private RaidMapEventContext CreateHostileActionWithOnePlayerParty(VillageHostileAction action)
    {
        var (raiderHeroId, raiderMobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var defenderTroopId = TestEnvironment.CreateRegisteredObject<CharacterObject>();
        var target = CreateVillageTarget();
        var raiderPartyId = GetPartyBaseId(raiderMobilePartyId);
        string? mapEventId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(raiderHeroId, out var raiderHero));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(raiderMobilePartyId, out var raiderParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetObject<CharacterObject>(defenderTroopId, out var defenderTroop));

            using (new AllowedThread())
            {
                raiderParty.MemberRoster.AddToCounts(raiderHero.CharacterObject, 1);
                raiderHero.PartyBelongedTo = raiderParty;
            }

            var mapEvent = CreateHostileActionMapEvent(raiderParty.Party, settlement.Party, action);
            Assert.NotNull(mapEvent);
            mapEvent.MapEventVisual = MockMapEventVisual();

            var defenderParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
            defenderParty.MemberRoster.AddToCounts(defenderTroop, 1);
            AddSyntheticMapEventParty(mapEvent.DefenderSide, defenderParty.Party);

            Assert.True(mapEvent.IsVillageHostileAction());
            Assert.False(mapEvent.IsVillageHostileActionWithMultiplePlayerParties());
            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out mapEventId));
        }, MapEventDisabledMethods);

        Assert.NotNull(mapEventId);
        return new RaidMapEventContext(
            mapEventId!,
            raiderMobilePartyId,
            raiderPartyId,
            target.OwnerFactionId);
    }

    private RaidMapEventContext CreateHostileActionWithTwoPlayerParties(VillageHostileAction action)
    {
        var (raiderHeroId, raiderMobilePartyId) = CreatePlayerHeroParty("PlayerOne");
        var (joinerHeroId, joinerMobilePartyId) = CreatePlayerHeroParty("PlayerTwo");
        var clients = Clients.ToArray();
        TestEnvironment.ConnectRegisteredPlayer(clients[0], "PlayerOne");
        TestEnvironment.ConnectRegisteredPlayer(clients[1], "PlayerTwo");
        var target = CreateVillageTarget();
        var raiderPartyId = GetPartyBaseId(raiderMobilePartyId);
        string? mapEventId = null;

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(raiderHeroId, out var raiderHero));
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(joinerHeroId, out var joinerHero));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(raiderMobilePartyId, out var raiderParty));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(joinerMobilePartyId, out var joinerParty));
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(target.SettlementId, out var settlement));

            using (new AllowedThread())
            {
                raiderParty.MemberRoster.AddToCounts(raiderHero.CharacterObject, 1);
                joinerParty.MemberRoster.AddToCounts(joinerHero.CharacterObject, 1);
                raiderHero.PartyBelongedTo = raiderParty;
                joinerHero.PartyBelongedTo = joinerParty;
            }

            var mapEvent = CreateHostileActionMapEvent(raiderParty.Party, settlement.Party, action);
            Assert.NotNull(mapEvent);
            mapEvent.MapEventVisual = MockMapEventVisual();
            AddSyntheticMapEventParty(mapEvent.AttackerSide, joinerParty.Party);

            Assert.True(mapEvent.IsVillageHostileActionWithMultiplePlayerParties());
            Assert.True(Server.ObjectManager.TryGetId(mapEvent, out mapEventId));
        }, MapEventDisabledMethods);

        Assert.NotNull(mapEventId);
        return new RaidMapEventContext(
            mapEventId!,
            raiderMobilePartyId,
            raiderPartyId,
            target.OwnerFactionId);
    }

    private static BattleCreationFlags RaidFlags() => HostileActionFlags(VillageHostileAction.Raid);

    private static BattleCreationFlags HostileActionFlags(VillageHostileAction action) => new BattleCreationFlags(
        forceRaid: action == VillageHostileAction.Raid,
        forceSallyOut: false,
        forceVolunteers: action == VillageHostileAction.ForceVolunteers,
        forceSupplies: action == VillageHostileAction.ForceSupplies,
        isSallyOutAmbush: false,
        forceBlockadeAttack: false,
        forceBlockadeSallyOutAttack: false,
        forceHideoutSendTroops: false);

    private sealed class GameMenuSwitchRecorder : IDisposable
    {
        private static readonly System.Reflection.MethodInfo SwitchToMenuMethod =
            AccessTools.Method(typeof(GameMenu), nameof(GameMenu.SwitchToMenu), new[] { typeof(string) });
        private static readonly List<(object Container, string MenuId)> SwitchCalls = new();

        private readonly Harmony harmony = new($"village-join-menu-recorder-{Guid.NewGuid()}");

        public GameMenuSwitchRecorder()
        {
            SwitchCalls.Clear();
            harmony.Patch(
                SwitchToMenuMethod,
                prefix: new HarmonyMethod(typeof(GameMenuSwitchRecorder), nameof(RecordSwitchToMenu))
                {
                    priority = Priority.First,
                });
        }

        public string[] SwitchesFor(EnvironmentInstance instance) =>
            SwitchCalls
                .Where(call => ReferenceEquals(call.Container, instance.Container))
                .Select(call => call.MenuId)
                .ToArray();

        public void Clear() => SwitchCalls.Clear();

        public void Dispose() =>
            harmony.Unpatch(SwitchToMenuMethod, HarmonyPatchType.Prefix, harmony.Id);

        private static bool RecordSwitchToMenu(string menuId)
        {
            if (GameInterface.ContainerProvider.TryGetContainer(out var container))
                SwitchCalls.Add((container, menuId));
            return false;
        }
    }

    private readonly record struct VillageTarget(
        string SettlementId,
        string VillageId,
        string SettlementPartyId,
        string OwnerFactionId,
        string OwnerClanId);
    private readonly record struct RaidDefenderJoinContext(
        string MapEventId,
        string JoinerMobilePartyId,
        string JoinerPartyId);
    private readonly record struct RaidMapEventContext(
        string MapEventId,
        string AttackerMobilePartyId,
        string AttackerPartyId,
        string OwnerFactionId);
}
