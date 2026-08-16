using Common.Network;
using E2E.Tests.Services.MapEvents;
using GameInterface.Services.Entity;
using GameInterface.Services.Locations.BoardGames;
using GameInterface.Services.Locations.BoardGames.Messages;
using GameInterface.Services.Players;
using HarmonyLib;
using SandBox.BoardGames;
using SandBox.BoardGames.MissionLogics;
using System.Linq;
using TaleWorlds.MountAndBlade.Source.Missions.Handlers;
using Xunit;
using Xunit.Abstractions;

namespace E2E.Tests.Services.Locations;

public sealed class PlayerBoardGameFlowTests : MapEventTestBase
{
    public PlayerBoardGameFlowTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public void PlayerBoardGame_ServerIgnoresUnsafeClientRelayCommands()
    {
        var clients = Clients.Take(2).ToArray();
        var initiatorClient = clients[0];
        var receiverClient = clients[1];

        initiatorClient.Resolve<IControllerIdProvider>().SetControllerId("BoardGameInitiator");
        receiverClient.Resolve<IControllerIdProvider>().SetControllerId("BoardGameReceiver");

        CreatePlayerHeroParty("BoardGameInitiator");
        CreatePlayerHeroParty("BoardGameReceiver");

        Server.Call(() =>
        {
            var playerManager = Server.Resolve<IPlayerManager>();
            playerManager.SetPeer("BoardGameInitiator", initiatorClient.NetPeer);
            playerManager.SetPeer("BoardGameReceiver", receiverClient.NetPeer);
        }, MapEventDisabledMethods);

        Server.NetworkSentMessages.Clear();
        initiatorClient.Call(() => initiatorClient.Resolve<INetwork>().SendAll(
            new NetworkRequestPlayerBoardGame("BoardGameReceiver", boardGameType: 4)));
        receiverClient.Call(() => receiverClient.Resolve<INetwork>().SendAll(
            new NetworkRespondPlayerBoardGameChallenge("forged", accepted: true)));
        initiatorClient.Call(() => initiatorClient.Resolve<INetwork>().SendAll(
            new NetworkPlayerBoardGameMove("forged", "BoardGameInitiator", fromIndex: 2, toIndex: 17)));
        receiverClient.Call(() => receiverClient.Resolve<INetwork>().SendAll(
            new NetworkPlayerBoardGamePawnCaptured("forged", "BoardGameReceiver", index: 0)));
        initiatorClient.Call(() => initiatorClient.Resolve<INetwork>().SendAll(
            new NetworkPlayerBoardGameFinished("forged", "BoardGameInitiator", gameOver: 0)));
        receiverClient.Call(() => receiverClient.Resolve<INetwork>().SendAll(
            new NetworkPlayerBoardGameCancelled("forged", "BoardGameReceiver")));

        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkPlayerBoardGameChallenge>());
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkPlayerBoardGameStarted>());
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkPlayerBoardGameMove>());
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkPlayerBoardGamePawnCaptured>());
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkPlayerBoardGameFinished>());
        Assert.Empty(Server.NetworkSentMessages.GetMessages<NetworkPlayerBoardGameCancelled>());
    }

    [Fact]
    public void PlayerBoardGameCompletion_UninstallsHandlerAndSuppressesNativePostGameConversation()
    {
        var handler = new ExplicitBoardGameHandler();

        Server.Call(() =>
        {
            var logic = new MissionBoardGameLogic { Handler = handler };
            var completeLocalGame = AccessTools.Method(typeof(PlayerBoardGameCoordinator), "CompleteLocalGame");
            var startConversationAfterGameEnd = AccessTools.Method(
                typeof(PlayerBoardGameCoordinator),
                "StartConversationAfterGameEndInternal");

            Assert.NotNull(completeLocalGame);
            Assert.NotNull(startConversationAfterGameEnd);
            completeLocalGame.Invoke(
                Server.Resolve<PlayerBoardGameCoordinator>(),
                new object[] { logic, GameOverEnum.PlayerOneWon });

            Assert.False((bool)startConversationAfterGameEnd.Invoke(
                Server.Resolve<PlayerBoardGameCoordinator>(),
                new object[] { logic }));
        });

        Assert.True(handler.Uninstalled);
    }

    public static bool SuppressClientBoardGameUi() => false;

    private sealed class ExplicitBoardGameHandler : IBoardGameHandler
    {
        public bool Uninstalled { get; private set; }

        void IBoardGameHandler.SwitchTurns()
        {
        }

        void IBoardGameHandler.DiceRoll(int roll)
        {
        }

        void IBoardGameHandler.Install()
        {
        }

        void IBoardGameHandler.Uninstall() => Uninstalled = true;

        void IBoardGameHandler.Activate()
        {
        }
    }
}
