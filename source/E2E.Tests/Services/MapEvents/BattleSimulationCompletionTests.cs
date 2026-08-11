using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Handlers;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.MapEvents.Messages.Start;
using HarmonyLib;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;
using Xunit.Abstractions;

namespace E2E.Tests.Services.MapEvents;

/// <summary>
/// Regression coverage for the shared completion boundary used by client-paced auto-resolve and
/// the server-side fallback after the pacing peer disconnects.
/// </summary>
public sealed class BattleSimulationCompletionTests : MapEventTestBase
{
    public BattleSimulationCompletionTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void PacedWinThenDuplicateDisconnectCompletion_FinalizesMapEventExactlyOnce()
    {
        var ctx = CreateServerMapEvent();
        var disabled = MapEventDisabledMethods
            .Append(AccessTools.Method(typeof(DefaultBattleRewardModel), nameof(DefaultBattleRewardModel.GetCaptureMemberChancesForWinnerParties)))
            .Append(AccessTools.Method(typeof(MapEvent), "LootDefeatedPartyCasualties"))
            .Append(AccessTools.Method(typeof(MapEvent), "LootDefeatedPartyItems"))
            .Append(AccessTools.Method(typeof(MapEvent), "LootDefeatedPartyPrisoners"))
            .Append(AccessTools.Method(typeof(MapEvent), "LootDefeatedPartyShips"))
            .Append(AccessTools.Method(typeof(MapEvent), "CalculateMapEventResults"))
            .Append(AccessTools.Method(typeof(MapEvent), "CommitCalculatedMapEventResults"))
            .Append(AccessTools.Method(typeof(MapEvent), "CaptureDefeatedPartyMembers"))
            .Append(AccessTools.Method(typeof(MapEvent), "MovePartyToSuitablePositionOnMapEventFinalize"))
            .Append(AccessTools.Method(typeof(GameMenu), nameof(GameMenu.ExitToLast)))
            .ToList();

        Server.InternalMessages.Clear();
        Server.NetworkSentMessages.Clear();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(ctx.MapEventId, out var mapEvent));
            mapEvent._battleState = BattleState.AttackerVictory;
            Assert.True(ServerBattleModeArbiter.TryClaimSimulation(ctx.MapEventId));

            var handler = Server.Resolve<BattleSimulationRunHandler>();
            handler.CompleteSimulation(ctx.MapEventId, mapEvent);
            handler.CompleteSimulation(ctx.MapEventId, mapEvent);
        }, disabled);

        Assert.Single(Server.InternalMessages.GetMessages<MapEventFinalized>());
        Assert.False(ServerBattleModeArbiter.IsClaimed(ctx.MapEventId));
        Assert.False(Server.ObjectManager.TryGetObject<MapEvent>(ctx.MapEventId, out _));
    }

    [Fact]
    public void UndecidedCompletion_ReleasesSimulationClaimWithoutFinalizingMapEvent()
    {
        var ctx = CreateServerMapEvent();
        try
        {
            Server.InternalMessages.Clear();
            Server.NetworkSentMessages.Clear();

            Server.Call(() =>
            {
                Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(ctx.MapEventId, out var mapEvent));
                Assert.False(mapEvent.HasWinner);
                Assert.True(ServerBattleModeArbiter.TryClaimSimulation(ctx.MapEventId));

                Server.Resolve<BattleSimulationRunHandler>().CompleteSimulation(ctx.MapEventId, mapEvent);
            });

            Assert.False(ServerBattleModeArbiter.IsClaimed(ctx.MapEventId));
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(ctx.MapEventId, out _));
            Assert.Empty(Server.InternalMessages.GetMessages<MapEventConcluded>());
            Assert.Empty(Server.InternalMessages.GetMessages<MapEventFinalized>());
            Assert.Single(Server.NetworkSentMessages.GetMessages<NetworkBattleSimulationFinished>());
        }
        finally
        {
            if (Server.ObjectManager.TryGetObject<MapEvent>(ctx.MapEventId, out _))
                DestroyServerMapEvent(ctx.MapEventId);
        }
    }

}
