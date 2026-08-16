using Common;
using Common.Messaging;
using Common.Network;
using Common.Network.Messages;
using GameInterface.Services.Entity;
using GameInterface.Services.Locations.BoardGames.Messages;
using GameInterface.Services.Locations.Conversations;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Helpers;
using LiteNetLib;
using SandBox.BoardGames;
using SandBox.BoardGames.MissionLogics;
using SandBox.BoardGames.Pawns;
using SandBox.BoardGames.Tiles;
using SandBox.Conversation.MissionLogics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.MountAndBlade.View.Screens;
using TaleWorlds.ScreenSystem;

namespace GameInterface.Services.Locations.BoardGames;

internal sealed class PlayerBoardGameCoordinator : IHandler
{
    private const int ControlRecoveryFrames = 3;

    private sealed class PendingChallenge
    {
        public readonly NetPeer InitiatorPeer;
        public readonly string InitiatorControllerId;
        public readonly string TargetControllerId;
        public readonly int BoardGameType;

        public PendingChallenge(NetPeer initiatorPeer, string initiatorControllerId, string targetControllerId, int boardGameType)
        {
            InitiatorPeer = initiatorPeer;
            InitiatorControllerId = initiatorControllerId;
            TargetControllerId = targetControllerId;
            BoardGameType = boardGameType;
        }
    }

    private sealed class ServerGame
    {
        public readonly string InitiatorControllerId;
        public readonly string ResponderControllerId;

        public ServerGame(string initiatorControllerId, string responderControllerId)
        {
            InitiatorControllerId = initiatorControllerId;
            ResponderControllerId = responderControllerId;
        }

        public bool Contains(string controllerId)
            => controllerId == InitiatorControllerId || controllerId == ResponderControllerId;
    }

    private sealed class ClientGame
    {
        public readonly string GameId;
        public readonly string LocalControllerId;
        public readonly string OtherControllerId;
        public readonly MissionBoardGameLogic Logic;

        public ClientGame(string gameId, string localControllerId, string otherControllerId, MissionBoardGameLogic logic)
        {
            GameId = gameId;
            LocalControllerId = localControllerId;
            OtherControllerId = otherControllerId;
            Logic = logic;
        }
    }

    private static readonly PropertyInfo OpposingAgentProperty = typeof(MissionBoardGameLogic).GetProperty(nameof(MissionBoardGameLogic.OpposingAgent));
    private static readonly PropertyInfo IsGameInProgressProperty = typeof(MissionBoardGameLogic).GetProperty(nameof(MissionBoardGameLogic.IsGameInProgress));
    private static readonly FieldInfo BoardGameStateField = typeof(MissionBoardGameLogic).GetField("_boardGameState", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly object CompletedGameMarker = new object();
    private static readonly MethodInfo MovePawnToTileMethod = typeof(BoardGameBase).GetMethod(
        "MovePawnToTile",
        BindingFlags.Instance | BindingFlags.NonPublic,
        null,
        new[] { typeof(PawnBase), typeof(TileBase), typeof(bool), typeof(bool) },
        null);

    internal static PlayerBoardGameCoordinator Instance { get; private set; }

    private readonly INetwork network;
    private readonly IMessageBroker messageBroker;
    private readonly IPlayerManager playerManager;
    private readonly IObjectManager objectManager;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly ConcurrentDictionary<string, PendingChallenge> pendingChallenges = new ConcurrentDictionary<string, PendingChallenge>();
    private readonly ConcurrentDictionary<string, ServerGame> serverGames = new ConcurrentDictionary<string, ServerGame>();
    private readonly ConditionalWeakTable<MissionBoardGameLogic, object> completedGames = new ConditionalWeakTable<MissionBoardGameLogic, object>();

    private ClientGame activeGame;
    private bool applyingRemoteResult;
    private Mission controlRecoveryMission;
    private int controlRecoveryFrames;

    public PlayerBoardGameCoordinator(
        IMessageBroker messageBroker,
        INetwork network,
        IPlayerManager playerManager,
        IObjectManager objectManager,
        IControllerIdProvider controllerIdProvider)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.playerManager = playerManager;
        this.objectManager = objectManager;
        this.controllerIdProvider = controllerIdProvider;

        Instance = this;
    }

    public void Dispose()
    {
        if (Instance == this) Instance = null;

        pendingChallenges.Clear();
        serverGames.Clear();
        activeGame = null;
    }

    internal static bool TryRequestGame(MissionBoardGameLogic logic, Agent opposingAgent)
        => Instance?.TryRequestGameInternal(logic, opposingAgent) == true;

    internal static bool ShouldSuppressAiTurn(BoardGameBase board)
        => Instance?.activeGame?.Logic?.Board == board && board.PlayerTurn == PlayerTurn.PlayerTwo;

    internal static void TrySendMove(BoardGameBase board, Move move)
        => Instance?.TrySendMoveInternal(board, move);

    internal static void TrySendCapturedPawn(BoardGameBase board, PawnBase pawn, bool fake)
        => Instance?.TrySendCapturedPawnInternal(board, pawn, fake);

    internal static bool TryCompleteGame(MissionBoardGameLogic logic, GameOverEnum gameOver)
        => Instance?.TryCompleteGameInternal(logic, gameOver) == true;

    internal static bool IsPlayerGame(MissionBoardGameLogic logic)
        => Instance != null && (Instance.activeGame?.Logic == logic || Instance.completedGames.TryGetValue(logic, out _));

    internal static bool StartConversationAfterGameEnd(MissionBoardGameLogic logic)
        => Instance?.StartConversationAfterGameEndInternal(logic) ?? true;

    internal static void ReassertLocationPlayerControl()
        => Instance?.ReassertLocationPlayerControlInternal();

    private bool TryRequestGameInternal(MissionBoardGameLogic logic, Agent opposingAgent)
    {
        if (ModInformation.IsServer) return false;
        if (!(opposingAgent?.Character is CharacterObject character)) return false;

        var hero = character.HeroObject;
        if (hero == null || !PlayerManager.TryGetControlledObjectInfo(hero, out var controlled))
        {
            completedGames.Remove(logic);
            if (opposingAgent.Controller != AgentControllerType.None) return false;

            ShowMessage("That player is not ready for a board game.");
            return true;
        }

        // The server has no canonical Tablut evaluator or board-position state. Do not turn the old
        // client relay into a client-authoritative game; keep native AI/local games available instead.
        ShowMessage("Player-versus-player board games are temporarily unavailable.");
        return true;
    }

    private void TrySendMoveInternal(BoardGameBase board, Move move)
    {
        // PvP board games are disabled until the server owns a deterministic board evaluator.
    }

    private void TrySendCapturedPawnInternal(BoardGameBase board, PawnBase pawn, bool fake)
    {
        // Captures cannot be client-authoritative; there is deliberately no network producer here.
    }

    private bool TryCompleteGameInternal(MissionBoardGameLogic logic, GameOverEnum gameOver)
    {
        if (activeGame?.Logic == logic)
        {
            // Never accept a client-declared winner/cancellation. This only recovers a legacy active UI.
            CompleteLocalGame(logic, gameOver);
            return true;
        }

        return completedGames.TryGetValue(logic, out _);
    }

    private void CompleteLocalGame(MissionBoardGameLogic logic, GameOverEnum gameOver)
    {
        Mission.Current?.MainAgent?.ClearTargetFrame();
        logic.Board?.SetGameOverInfo(gameOver);
        logic.Handler?.Uninstall();

        BoardGameStateField.SetValue(logic, ToBoardGameState(gameOver));
        IsGameInProgressProperty.SetValue(logic, false);
        OpposingAgentProperty.SetValue(logic, null);
        logic.AIOpponent?.OnSetGameOver();

        completedGames.Remove(logic);
        completedGames.Add(logic, CompletedGameMarker);
        activeGame = null;

        RestoreLocationPlayerControl();
        ArmLocationPlayerControlRecovery();
    }

    private bool StartConversationAfterGameEndInternal(MissionBoardGameLogic logic)
    {
        if (activeGame?.Logic != logic && !completedGames.TryGetValue(logic, out _)) return true;

        RestoreLocationPlayerControl();
        ArmLocationPlayerControlRecovery();
        return false;
    }

    private void ArmLocationPlayerControlRecovery()
    {
        controlRecoveryMission = Mission.Current;
        controlRecoveryFrames = controlRecoveryMission == null ? 0 : ControlRecoveryFrames;
    }

    private void ReassertLocationPlayerControlInternal()
    {
        if (controlRecoveryFrames == 0) return;
        if (controlRecoveryMission != Mission.Current)
        {
            controlRecoveryMission = null;
            controlRecoveryFrames = 0;
            return;
        }

        RestoreLocationPlayerControl();
        if (--controlRecoveryFrames == 0)
            controlRecoveryMission = null;
    }

    private static void RestoreLocationPlayerControl()
    {
        var mission = Mission.Current;
        if (!LocationMissionTracker.IsLocationMission(mission)) return;

        var conversation = mission.GetMissionBehavior<MissionConversationLogic>();
        if (conversation?.ConversationManager?.IsConversationInProgress == true)
            conversation.ConversationManager.EndConversation();

        if (mission.Mode == MissionMode.Conversation && !mission.IsMissionEnding)
            mission.SetMissionMode(MissionMode.Battle, false);

        var localAgent = Agent.Main;
        if (localAgent?.Mission == mission)
        {
            if (localAgent.IsUsingGameObject)
                localAgent.StopUsingGameObject();

            localAgent.SetAsConversationAgent(false);
            localAgent.ClearTargetFrame();
            localAgent.Controller = AgentControllerType.Player;
        }

        if (mission.MainAgentServer != null)
            mission.MainAgentServer.Controller = AgentControllerType.Player;

        var mainAgentController = mission.GetMissionBehavior<MissionMainAgentController>();
        if (mainAgentController != null)
        {
            mainAgentController.IsDisabled = false;
            mainAgentController.Enable();
        }

        if (!(ScreenManager.TopScreen is MissionScreen missionScreen) || missionScreen.SceneLayer == null) return;

        var sceneLayer = missionScreen.SceneLayer;
        sceneLayer.InputRestrictions.ResetInputRestrictions();
        sceneLayer.IsFocusLayer = true;
        ScreenManager.TrySetFocus(sceneLayer);
    }

    private static BoardGameHelper.BoardGameState ToBoardGameState(GameOverEnum gameOver)
    {
        return gameOver switch
        {
            GameOverEnum.PlayerOneWon => BoardGameHelper.BoardGameState.Win,
            GameOverEnum.PlayerTwoWon => BoardGameHelper.BoardGameState.Loss,
            GameOverEnum.Draw => BoardGameHelper.BoardGameState.Draw,
            _ => BoardGameHelper.BoardGameState.None
        };
    }

    private static void ShowMessage(string message)
        => InformationManager.DisplayMessage(new InformationMessage(message));
}
