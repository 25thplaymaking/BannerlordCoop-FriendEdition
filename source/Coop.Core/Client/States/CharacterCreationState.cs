// Ignore Spelling: Finalizer

using Common.Logging;
using Common.Messaging;
using Common.Network;
using Coop.Core.Client.Messages;
using Coop.Core.Common;
using Coop.Core.Server.Connections.Messages;
using GameInterface.Registry;
using GameInterface.Services.CharacterCreation.Messages;
using GameInterface.Services.Entity;
using GameInterface.Services.GameState.Interfaces;
using GameInterface.Services.GameState.Messages;
using GameInterface.Services.Heroes.Interfaces;
using GameInterface.Services.Players;
using GameInterface.Services.UI.Interfaces;
using Serilog;
using System.Diagnostics;

namespace Coop.Core.Client.States;

/// <summary>
/// State controller for the character creation client state
/// </summary>
public class CharacterCreationState : ClientStateBase
{
    private static readonly ILogger Logger = LogManager.GetLogger<CharacterCreationState>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IHeroInterface heroInterface;
    private readonly IRegistryManager registryManager;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly ILoadingInterface loadingInterface;
    private readonly IPlayerManager playerManager;
    private readonly IGameStateInterface gameStateInterface;
    private readonly ICoopFinalizer coopFinalizer;

    public CharacterCreationState(
        IClientLogic logic,
        IMessageBroker messageBroker,
        INetwork network,
        IHeroInterface heroInterface,
        IRegistryManager registryManager,
        IControllerIdProvider controllerIdProvider,
        ILoadingInterface loadingInterface,
        IPlayerManager playerManager,
        IGameStateInterface gameStateInterface,
        ICoopFinalizer coopFinalizer) : base(logic)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.heroInterface = heroInterface;
        this.registryManager = registryManager;
        this.controllerIdProvider = controllerIdProvider;
        this.loadingInterface = loadingInterface;
        this.playerManager = playerManager;
        this.gameStateInterface = gameStateInterface;
        this.coopFinalizer = coopFinalizer;

        loadingInterface.HideLoadingScreen();

        messageBroker.Subscribe<CharacterCreationFinished>(Handle_CharacterCreationFinished);
        messageBroker.Subscribe<MainMenuEntered>(Handle_MainMenuEntered);
        messageBroker.Subscribe<NetworkHeroRecieved>(Handle_NetworkHeroRecieved);
    }

    public override void Dispose()
    {
        messageBroker.Unsubscribe<CharacterCreationFinished>(Handle_CharacterCreationFinished);
        messageBroker.Unsubscribe<MainMenuEntered>(Handle_MainMenuEntered);
        messageBroker.Unsubscribe<NetworkHeroRecieved>(Handle_NetworkHeroRecieved);
    }

    internal void Handle_CharacterCreationFinished(MessagePayload<CharacterCreationFinished> obj)
    {
        // Cover the client's own (character-creation) world with a loading screen until the
        // server campaign is ready, so the local world isn't briefly visible while we join.
        Logger.Information("[CC-DIAG] Character creation finished; beginning host handoff");
        loadingInterface.ShowLoadingScreen(
            "Joining Coop Campaign",
            "Sending your character to the host...");

        // This runs on the game thread with a loading screen up and nothing else logging, so a
        // slow step here is indistinguishable from a hang. Time each one.
        var handoff = Stopwatch.StartNew();

        var stepTimer = Stopwatch.StartNew();
        registryManager.RegisterAllGameObjects();
        long registerMs = stepTimer.ElapsedMilliseconds;

        var playerId = controllerIdProvider.ControllerId;
        stepTimer.Restart();
        var data = heroInterface.PackageMainHero();
        long packageMs = stepTimer.ElapsedMilliseconds;

        // Clear all registries so next time the game is loaded, it re-registers loaded save objects
        stepTimer.Restart();
        registryManager.ClearAllRegistries();
        long clearMs = stepTimer.ElapsedMilliseconds;

        stepTimer.Restart();
        network.SendAll(new NetworkTransferNewHero(playerId, data));
        long sendMs = stepTimer.ElapsedMilliseconds;

        Logger.Information(
            "[CC-DIAG] Character creation handoff sent. registerAllMs={RegisterMs} packageHeroMs={PackageMs} " +
            "clearRegistriesMs={ClearMs} sendMs={SendMs} totalMs={TotalMs}",
            registerMs, packageMs, clearMs, sendMs, handoff.ElapsedMilliseconds);
    }

    internal void Handle_NetworkHeroRecieved(MessagePayload<NetworkHeroRecieved> obj)
    {
        Logger.Information("[CC-DIAG] Host acknowledged the hero; requesting the saved campaign");

        Logic.Player = obj.What.Player;

        Logic.LoadSavedData();
    }

    internal void Handle_MainMenuEntered(MessagePayload<MainMenuEntered> obj)
    {
        coopFinalizer.Finalize("Client has been stopped");

        Logic.SetState<MainMenuState>();
    }

    public override void EnterMainMenu()
    {
        gameStateInterface.GoToMainMenu();
    }

    public override void Connect()
    {
    }

    public override void Disconnect()
    {
        gameStateInterface.GoToMainMenu();
    }

    public override void ExitGame()
    {
    }

    public override void LoadSavedData()
    {
        Logic.SetState<ReceivingSavedDataState>();
    }

    public override void StartCharacterCreation()
    {
    }

    public override void EnterCampaignState()
    {
    }

    public override void EnterMissionState()
    {
    }

    public override void ValidateModules()
    {
    }
}
