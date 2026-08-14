using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Coop.Core.Client.Services.Heroes.Messages;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Serilog;

namespace Coop.Core.Server.Services.Heroes.Handlers;

/// <summary>
/// Applies player succession after a registered player hero died with an eligible heir
/// (the gate in <c>KillCharacterActionPatches</c> guarantees one was resolved):
/// rebinds the controller to the successor - restoring or creating the heir's party through
/// the same <see cref="IPlayerPartyRestorer"/> the join flow uses - broadcasts the replaced
/// registration to every client, and tells the owning peer to switch characters. A
/// disconnected owner's registration is still replaced; the normal join flow then resolves
/// them straight into the heir on their next connect.
/// </summary>
internal class PlayerSuccessionHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<PlayerSuccessionHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IPlayerPartyRestorer playerPartyRestorer;

    public PlayerSuccessionHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IPlayerPartyRestorer playerPartyRestorer)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.playerPartyRestorer = playerPartyRestorer;

        messageBroker.Subscribe<PlayerHeroDied>(Handle_PlayerHeroDied);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PlayerHeroDied>(Handle_PlayerHeroDied);
    }

    private void Handle_PlayerHeroDied(MessagePayload<PlayerHeroDied> obj)
    {
        var payload = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!playerManager.TryGetPlayer(payload.ControllerId, out var registered))
            {
                Logger.Error(
                    "Succession failed: controller {ControllerId} has no registration",
                    payload.ControllerId);
                return;
            }

            if (!objectManager.TryGetIdWithLogging(payload.Successor, out var heirId)) return;
            if (!objectManager.TryGetIdWithLogging(payload.Successor.Clan, out var clanId)) return;
            if (!objectManager.TryGetIdWithLogging(payload.Successor.CharacterObject, out var characterId)) return;

            var replacement = new Player(payload.ControllerId, heirId, mobilePartyId: null, clanId, characterId);

            // Finds the heir's existing party or creates a recovery party at a valid position,
            // registering it either way - the same repair the rejoin flow relies on.
            if (!playerPartyRestorer.TryRestore(replacement, out var restored))
            {
                Logger.Error(
                    "Succession failed: heir {HeirId} for controller {ControllerId} has no restorable party",
                    heirId, payload.ControllerId);
                return;
            }

            if (!playerManager.ReplacePlayer(registered, restored))
            {
                Logger.Error(
                    "Succession failed: could not replace registration for controller {ControllerId}",
                    payload.ControllerId);
                return;
            }

            network.SendAll(new NetworkPlayerRegistrationUpdated(restored));

            if (playerManager.TryGetPeer(payload.ControllerId, out var peer))
            {
                network.Send(peer, new NetworkPlayerHeirSucceeded(restored, payload.VictimName));
            }
            else
            {
                Logger.Information(
                    "Controller {ControllerId} is offline; heir {HeirId} takes effect on next join",
                    payload.ControllerId, heirId);
            }
        }, blocking: true, context: nameof(PlayerSuccessionHandler));
    }
}
