using Common.Logging;
using Common.Messaging;
using Common.Network;
using Coop.Core.Client.Messages;
using Coop.Core.Common;
using GameInterface;
using GameInterface.Services.GameState.Interfaces;
using GameInterface.Services.UI.Interfaces;
using Serilog;
using System;

namespace Coop.Core.Client.States;

/// <summary>
/// State Logic Controller for the Main Menu Client State
/// </summary>
public class MainMenuState : ClientStateBase
{
    private static readonly ILogger Logger = LogManager.GetLogger<MainMenuState>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IGameInterface gameInterface;
    private readonly IGameStateInterface gameStateInterface;
    private readonly ILoadingInterface loadingInterface;
    private readonly ICoopFinalizer coopFinalizer;

    public MainMenuState(
        IClientLogic logic,
        IMessageBroker messageBroker,
        INetwork network,
        IGameInterface gameInterface,
        IGameStateInterface gameStateInterface,
        ILoadingInterface loadingInterface,
        ICoopFinalizer coopFinalizer) : base(logic)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.gameInterface = gameInterface;
        this.gameStateInterface = gameStateInterface;
        this.loadingInterface = loadingInterface;
        this.coopFinalizer = coopFinalizer;
        loadingInterface.HideLoadingScreen();
        messageBroker.Subscribe<NetworkConnected>(Handle_NetworkConnected);
    }

    public override void Dispose() 
    {
        messageBroker.Unsubscribe<NetworkConnected>(Handle_NetworkConnected);
    }

    public override void Connect()
    {
        network.Start();
    }

    internal void Handle_NetworkConnected(MessagePayload<NetworkConnected> obj)
    {
        loadingInterface.ShowLoadingScreen(
            "Connecting to Coop Server",
            "Applying patches...");
        // A patch-application failure must abort the join loudly. This handler runs inside the
        // message broker, which catches and logs the exception but continues — before this guard,
        // a single failed Harmony job (2026-08-13: a target declared on a constructed generic
        // type, unpatchable on the client CLR) left the player soft-frozen on the "Applying
        // patches..." loading screen with no error, while the server parked the peer in handshake.
        try
        {
            gameInterface.PatchAll();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Patch application failed during the join handshake; aborting the join");
            coopFinalizer.Finalize(
                "Co-op could not apply its game patches, so the join was cancelled.\n" +
                "Send your client log to the host; the first error there names the failing patch.");
            return;
        }
        loadingInterface.SetLoadingMessage(
            "Connecting to Coop Server",
            "Validating modules...");
        Logic.ValidateModules();
    }

    public override void Disconnect()
    {
        gameStateInterface.GoToMainMenu();
    }

    public override void EnterMainMenu()
    {
    }

    public override void ExitGame()
    {
    }

    public override void LoadSavedData()
    {
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
        Logic.SetState<ValidateModuleState>();
    }
}
