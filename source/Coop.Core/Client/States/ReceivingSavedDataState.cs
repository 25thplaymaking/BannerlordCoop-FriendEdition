using Common.Messaging;
using System.Diagnostics;
using Serilog;
using Common.Logging;
using Coop.Core.Client.Messages;
using GameInterface.Services.GameState.Interfaces;
using GameInterface.Services.UI.Interfaces;
using System.Globalization;

namespace Coop.Core.Client.States;

/// <summary>
/// State Logic Controller for the Receiving Saved Data State
/// </summary>
public class ReceivingSavedDataState : ClientStateBase
{
    private static readonly ILogger Logger = LogManager.GetLogger<ReceivingSavedDataState>();

    private readonly IMessageBroker messageBroker;
    private readonly ILoadingInterface loadingInterface;
    private readonly IGameStateInterface gameStateInterface;

    public ReceivingSavedDataState(
        IClientLogic logic,
        IMessageBroker messageBroker,
        ILoadingInterface loadingInterface,
        IGameStateInterface gameStateInterface) : base(logic)
    {
        this.messageBroker = messageBroker;
        this.loadingInterface = loadingInterface;
        this.gameStateInterface = gameStateInterface;
        messageBroker.Subscribe<NetworkGameSaveDataReceived>(Handle_NetworkGameSaveDataReceived);
        messageBroker.Subscribe<NetworkGameSaveDataProgress>(Handle_NetworkGameSaveDataProgress);

        loadingInterface.ShowLoadingScreen(
            "Joining Coop Campaign",
            "Waiting for host save data...");
    }

    public override void Dispose()
    {
        messageBroker.Unsubscribe<NetworkGameSaveDataReceived>(Handle_NetworkGameSaveDataReceived);
        messageBroker.Unsubscribe<NetworkGameSaveDataProgress>(Handle_NetworkGameSaveDataProgress);
    }

    internal void Handle_NetworkGameSaveDataProgress(MessagePayload<NetworkGameSaveDataProgress> obj)
    {
        int remaining = obj.What.PacketsRemaining;
        string description = remaining > 0
            ? "Waiting for host save data... " +
              remaining.ToString("N0", CultureInfo.InvariantCulture) +
              " save packets remaining"
            : "Host save data received.";

        loadingInterface.SetLoadingMessage(
            "Joining Coop Campaign",
            description);
    }

    internal void Handle_NetworkGameSaveDataReceived(MessagePayload<NetworkGameSaveDataReceived> obj)
    {
        loadingInterface.SetLoadingMessage(
            "Joining Coop Campaign",
            "Preparing host save data...");

        gameStateInterface.GoToMainMenu();

        var saveData = obj.What.GameSaveData;

        if (saveData == null) return;
        if (saveData.Length == 0) return;

        // Loading blocks the game thread, so this is the last thing the player sees until the world
        // is up: nothing repaints, no spinner moves. On a conversion-sized world that is a minute of
        // apparent freeze, and it has already been reported as a softlock. Say what is happening and
        // how big it is, so an honest wait cannot be mistaken for a hang.
        double megabytes = saveData.Length / (1024d * 1024d);
        loadingInterface.SetLoadingMessage(
            "Loading Host Campaign",
            string.Format(
                CultureInfo.InvariantCulture,
                "Loading the host's world ({0:N0} MB). Large campaigns can take a minute — the game " +
                "will not respond until it finishes.",
                megabytes));

        Logger.Information(
            "Loading host save data: {Megabytes:N0} MB. The game thread blocks until this completes.",
            megabytes);

        var loadTimer = Stopwatch.StartNew();
        gameStateInterface.LoadSaveData(saveData);
        Logger.Information(
            "Host save data loaded in {ElapsedMs} ms ({Megabytes:N0} MB)",
            loadTimer.ElapsedMilliseconds, megabytes);

        Logic.LoadSavedData();
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

        Logic.SetState<MainMenuState>();
    }

    public override void ExitGame()
    {
    }

    public override void LoadSavedData()
    {
        Logic.SetState<LoadingState>();
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
