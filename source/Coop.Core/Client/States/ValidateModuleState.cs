using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Coop.Core.Common;
using Coop.Core.Server.Connections.Messages;
using GameInterface.Configuration;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Services.CharacterCreation.Messages;
using GameInterface.Services.Entity;
using GameInterface.Services.GameDebug.Messages;
using GameInterface.Services.GameState.Interfaces;
using GameInterface.Services.Modules;
using GameInterface.Services.WorkshopMods.Core;
using Serilog;
using System;
using System.Diagnostics;
using System.Threading;

namespace Coop.Core.Client.States;

/// <summary>
/// State Logic Controller for the Validate Module Client State
/// </summary>
public class ValidateModuleState : ClientStateBase
{
    private const string UnsupportedCoopModuleReason = "Server does not support module 'Coop'.";

    private static readonly ILogger Logger = LogManager.GetLogger<ValidateModuleState>();

    /// <summary>
    /// How long the client waits for the server's validation responses before giving up. The
    /// exchange is two immediate request/response roundtrips, so anything beyond this means the
    /// server never answered (validation crashed server-side, or the builds are so different the
    /// request could not even be deserialized) — without a deadline the player would sit on the
    /// "Validating modules..." loading screen forever.
    /// </summary>
    internal static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ManifestPreparationTimeout = TimeSpan.FromMinutes(2);

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IControllerIdProvider controllerIdProvider;
    private readonly ICoopFinalizer coopFinalizer;
    private readonly IGameStateInterface gameStateInterface;
    private readonly IWorkshopManifestProvider workshopManifestProvider;
    private readonly IWorkshopManifestValidator workshopManifestValidator;
    private readonly IModConfigAuthority modConfigAuthority;
    private readonly IModuleInfoProvider moduleInfoProvider;
    private volatile WorkshopCompatibilityManifest localWorkshopManifest;
    private volatile string localWorkshopManifestError;
    private readonly Timer validationTimeoutTimer;

    private volatile bool disposed;

    // The validation exchange has exactly one outcome: the server's terminal response transitions the
    // state forward (LoadSavedData / character creation), or a failure/timeout/disconnect tears coop
    // down. Both race across the poller thread (validation responses) and the game thread (the timeout
    // timer), so every terminal path claims this latch via Interlocked before touching Logic — the
    // first caller wins and runs, all others no-op. That keeps the timeout from destroying the
    // container under an in-flight forward transition (between Handle_NetworkClientValidated starting
    // and reaching LoadSavedData) and runs teardown exactly once. Dispose claims it too, so a timeout
    // firing as the state cleanly transitions finds completion handled instead of tearing the next
    // state down.
    private int completionClaimed;
    private int validationRequestSent;
    private string disconnectReason;

    // Claims this state's single completion for the calling terminal path; returns false if another
    // terminal path (forward transition, teardown, or Dispose) already claimed it.
    private bool TryClaimCompletion() => Interlocked.Exchange(ref completionClaimed, 1) == 0;

    public ValidateModuleState(
        IClientLogic logic,
        IMessageBroker messageBroker,
        INetwork network,
        IControllerIdProvider controllerIdProvider,
        ICoopFinalizer coopFinalizer,
        IGameStateInterface gameStateInterface,
        IModuleInfoProvider moduleInfoProvider,
        IWorkshopManifestProvider workshopManifestProvider,
        IModConfigAuthority modConfigAuthority) : base(logic)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.controllerIdProvider = controllerIdProvider;
        this.coopFinalizer = coopFinalizer;
        this.gameStateInterface = gameStateInterface;
        this.moduleInfoProvider = moduleInfoProvider ?? throw new ArgumentNullException(nameof(moduleInfoProvider));
        this.workshopManifestProvider = workshopManifestProvider ?? throw new ArgumentNullException(nameof(workshopManifestProvider));
        this.modConfigAuthority = modConfigAuthority ?? throw new ArgumentNullException(nameof(modConfigAuthority));
        workshopManifestValidator = new WorkshopManifestValidator();
        messageBroker.Subscribe<NetworkModuleVersionsValidated>(Handle_NetworkModuleVersionsValidated);
        messageBroker.Subscribe<NetworkClientValidated>(Handle_NetworkClientValidated);
        messageBroker.Subscribe<CharacterCreationStarted>(Handle_CharacterCreationStarted);

        localWorkshopManifest = null;
        localWorkshopManifestError = null;

#if DEBUG
        controllerIdProvider.SetControllerFromProgramArgs();
#else
        controllerIdProvider.SetControllerAsPlatformId();
#endif

        // Preparation has its own bounded deadline. Once the request is sent, SendValidationRequest
        // switches this same one-shot timer to the shorter network-response deadline.
        validationTimeoutTimer = new Timer(
            _ => GameThread.RunSafe(TimeoutValidation),
            null,
            ManifestPreparationTimeout,
            Timeout.InfiniteTimeSpan);

        // Runtime module metadata/path discovery reads TaleWorlds ModuleHelper state and therefore
        // runs on the game thread. Only the frozen snapshot's potentially multi-gigabyte file hash
        // moves to a worker. The non-enforcing narrow-test provider stays synchronous because those
        // test compositions do not have a game-loop pump.
        if (workshopManifestProvider.EnforceHandshake)
        {
            GameThread.RunSafe(CaptureAndBuildValidationRequest,
                context: nameof(CaptureAndBuildValidationRequest));
        }
        else
        {
            CaptureAndBuildValidationRequest();
        }
    }

    private void CaptureAndBuildValidationRequest()
    {
        if (disposed || Volatile.Read(ref completionClaimed) != 0) return;

        WorkshopManifestPreparation preparation;
        try
        {
            preparation = workshopManifestProvider.PrepareManifest(WorkshopPeerRole.Client);
        }
        catch (Exception exception)
        {
            localWorkshopManifestError =
                $"Unable to inspect the Friend Edition Workshop installation ({exception.GetType().Name}).";
            Logger.Error(exception, "Capturing the client Workshop compatibility metadata failed");
            SendValidationRequest();
            return;
        }

        if (workshopManifestProvider.EnforceHandshake)
        {
            ThreadPool.QueueUserWorkItem(_ => BuildValidationRequest(preparation));
        }
        else
        {
            BuildValidationRequest(preparation);
        }
    }

    private void BuildValidationRequest(WorkshopManifestPreparation preparation)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            localWorkshopManifest = workshopManifestProvider.BuildPreparedManifest(preparation);
        }
        catch (Exception exception)
        {
            localWorkshopManifestError =
                $"Unable to build the Friend Edition Workshop manifest ({exception.GetType().Name}).";
            Logger.Error(exception, "Building the client Workshop compatibility manifest failed");
        }

        stopwatch.Stop();
        Logger.Information(
            "Built Friend Edition Workshop manifest in {ElapsedMilliseconds} ms",
            stopwatch.ElapsedMilliseconds);

        if (disposed || Volatile.Read(ref completionClaimed) != 0) return;

        if (workshopManifestProvider.EnforceHandshake)
        {
            GameThread.RunSafe(SendValidationRequest, context: nameof(SendValidationRequest));
        }
        else
        {
            // Legacy unit-test compositions have no game-loop pump and use a deterministic
            // non-enforcing provider, so their request remains immediate.
            SendValidationRequest();
        }
    }

    private void SendValidationRequest()
    {
        if (disposed || Volatile.Read(ref completionClaimed) != 0) return;

        if (localWorkshopManifestError != null || localWorkshopManifest == null)
        {
            DenyValidation(localWorkshopManifestError ??
                "Unable to build the required Friend Edition Workshop manifest.");
            return;
        }

        try
        {
            // Arm the response deadline before sending. A loopback/very fast server can otherwise
            // complete the state and dispose the timer between SendAll and Timer.Change.
            Interlocked.Exchange(ref validationRequestSent, 1);
            validationTimeoutTimer.Change(ValidationTimeout, Timeout.InfiniteTimeSpan);
            network.SendAll(new NetworkModuleVersionsValidate(
                moduleInfoProvider.GetModuleInfos(),
                localWorkshopManifest));
        }
        catch (Exception exception)
        {
            localWorkshopManifestError =
                $"Unable to send the Friend Edition Workshop manifest ({exception.GetType().Name}).";
            Logger.Error(exception, "Sending the client Workshop compatibility manifest failed");
            DenyValidation(localWorkshopManifestError);
        }
    }

    public override void Dispose()
    {
        disposed = true;
        // Claim completion so an in-flight timeout callback that already passed its guard finds it
        // handled and no-ops (see TimeoutValidation / Disconnect).
        Interlocked.Exchange(ref completionClaimed, 1);
        validationTimeoutTimer?.Dispose();
        messageBroker.Unsubscribe<NetworkModuleVersionsValidated>(Handle_NetworkModuleVersionsValidated);
        messageBroker.Unsubscribe<NetworkClientValidated>(Handle_NetworkClientValidated);
        messageBroker.Unsubscribe<CharacterCreationStarted>(Handle_CharacterCreationStarted);
    }

    internal void TimeoutValidation()
    {
        // Marshaled onto the game thread by the timer callback. The guard cheaply skips the common
        // case where the state was already left; claiming completion below — before logging or tearing
        // down — is what actually closes the race with a validation response landing at the same
        // instant. If that response (running on the poller thread) already claimed completion, this
        // no-ops entirely: no spurious teardown of the container it is mid-transition into, no
        // spurious error log.
        if (disposed || Logic.State != this) return;
        if (!TryClaimCompletion()) return;

        bool requestSent = Volatile.Read(ref validationRequestSent) != 0;
        TimeSpan deadline = requestSent ? ValidationTimeout : ManifestPreparationTimeout;
        Logger.Error(
            "Timed out after {Timeout}s during {Phase}",
            deadline.TotalSeconds,
            requestSent ? "server module validation" : "Workshop manifest preparation");

        disconnectReason = requestSent
            ? "Timed out waiting for the server to validate the connection.\n" +
              "The server may be running an incompatible version of the mod."
            : "Timed out preparing the Friend Edition Workshop compatibility manifest.\n" +
              "Verify the private suite installation and disk health, then try again.";
        TearDown();
    }

    internal void Handle_NetworkModuleVersionsValidated(MessagePayload<NetworkModuleVersionsValidated> obj)
    {
        // The compatibility manifest is checked in both directions: the server already compared
        // our manifest before setting Matches, and the client independently checks the server's
        // returned manifest before it is allowed to request character/save transfer.
        if (obj.What.Matches)
        {
            if (workshopManifestProvider.EnforceHandshake)
            {
                if (obj?.Who is not LiteNetLib.NetPeer serverPeer)
                {
                    DenyValidation("Rejected module/config validation from an untrusted local source.");
                    return;
                }
                if (!modConfigAuthority.TryBindTrustedServer(serverPeer, out string trustFailure))
                {
                    DenyValidation("Rejected module/config validation transport.\n" + trustFailure);
                    return;
                }

                if (localWorkshopManifestError != null)
                {
                    DenyValidation(localWorkshopManifestError);
                    return;
                }

                WorkshopManifestValidationResult workshopResult = workshopManifestValidator.Validate(
                    obj.What.ServerWorkshopManifest,
                    localWorkshopManifest);
                foreach (string warning in workshopResult.Warnings)
                    Logger.Warning("Workshop compatibility warning: {Warning}", warning);
                if (!workshopResult.Matches)
                {
                    DenyValidation("Workshop compatibility validation failed!\n" +
                                   workshopResult.ToNetworkReason());
                    return;
                }
                if (workshopResult.Warnings.Count > 0)
                {
                    messageBroker.Publish(this, new SendInformationMessage(
                        "Friend Edition package validation passed with feature warnings:\n" +
                        workshopResult.ToNetworkWarning()));
                }

                // This is the mandatory configuration barrier. The host envelope is validated and
                // atomically committed before the character/save request can leave this process.
                ModConfigAcceptanceResult configResult =
                    modConfigAuthority.AcceptClientSnapshot(obj.What.HostModConfig);
                if (!configResult.Succeeded)
                {
                    DenyValidation(
                        $"Host mod-config validation failed ({configResult.Status}).\n" +
                        configResult.Reason);
                    return;
                }
                messageBroker.Publish(this, new HostModConfigAccepted(obj.What.HostModConfig));
                Logger.Information(
                    "Accepted initial host mod-config barrier: session={Session}, revision={Revision}, " +
                    "sha256={Sha256}, difficulty.birthAndDeath={BirthAndDeath}",
                    obj.What.HostModConfig.SessionId,
                    obj.What.HostModConfig.Revision,
                    obj.What.HostModConfig.Sha256,
                    obj.What.HostModConfig.BirthAndDeathEnabled);
            }

            network.SendAll(new NetworkClientValidate(
                controllerIdProvider.ControllerId,
                obj.What.HostModConfig));
            return;
        }

        // Retain the historical narrow-test fallback without weakening the real private suite.
        // Production providers always enforce the manifest and therefore never take this path.
        if (!workshopManifestProvider.EnforceHandshake &&
            string.Equals(obj.What.Reason, UnsupportedCoopModuleReason, StringComparison.Ordinal))
        {
            network.SendAll(new NetworkClientValidate(controllerIdProvider.ControllerId));
            return;
        }

        DenyValidation("Module validation failed!\nReason: " + obj.What.Reason);
    }

    private void DenyValidation(string reason)
    {
        messageBroker.Publish(this, new SendInformationMessage(reason));

        // Carry the reason into the teardown pop-up: the information message above lands in the
        // chat log, which is invisible behind the forced loading screen the player is watching.
        disconnectReason = reason;
        Logic.Disconnect();
    }

    internal void Handle_NetworkClientValidated(MessagePayload<NetworkClientValidated> obj)
    {
        // The server's terminal validation response. Claim completion before touching Logic so a
        // timeout firing at the same instant loses the race and no-ops, rather than tearing the
        // container down in the window between here and the state transition below (which would leave
        // this handler resolving the next state from a disposed container).
        if (!TryClaimCompletion()) return;

        if (obj.What.HeroExists)
        {
            Logic.Player = obj.What.Player;
            Logic.LoadSavedData();
        }
        else
        {
            Logic.StartCharacterCreation();
        }
    }

    internal void Handle_CharacterCreationStarted(MessagePayload<CharacterCreationStarted> obj)
    {
        Logic.SetState<CharacterCreationState>();
    }

    public override void EnterMainMenu()
    {
    }

    public override void LoadSavedData()
    {
        Logic.SetState<ReceivingSavedDataState>();
    }

    public override void Connect()
    {
    }

    public override void Disconnect()
    {
        // Teardown can be initiated from the game thread (validation-timeout timer) and the poller
        // thread (a denied or late validation response) at the same instant, and it is mutually
        // exclusive with a successful forward transition; the shared completion latch runs
        // CoopFinalizer exactly once.
        if (!TryClaimCompletion()) return;

        TearDown();
    }

    private void TearDown()
    {
        validationTimeoutTimer?.Dispose();

        // Finalize tears down coop (EndCoopMode -> DestroyContainer), which disposes the container the
        // state machine resolves from — so do NOT SetState afterwards (it would resolve from a disposed
        // container and throw). This matches the teardown in the other states (e.g. CampaignState).
        coopFinalizer.Finalize(disconnectReason ?? "Client has been stopped");
    }

    public override void EnterCampaignState()
    {
    }

    public override void EnterMissionState()
    {
    }

    public override void ExitGame()
    {
    }

    public override void StartCharacterCreation()
    {
        messageBroker.Publish(this, new StartCharacterCreation());
    }

    public override void ValidateModules()
    {
    }
}
