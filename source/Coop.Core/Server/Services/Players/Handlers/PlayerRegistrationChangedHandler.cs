using Common.Messaging;
using Common.Network;
using Coop.Core.Client.Services.Heroes.Messages;
using GameInterface.Services.Players.Messages;

namespace Coop.Core.Server.Services.Players.Handlers;

internal sealed class PlayerRegistrationChangedHandler : IHandler
{
    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;

    public PlayerRegistrationChangedHandler(IMessageBroker messageBroker, INetwork network)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        messageBroker.Subscribe<PlayerRegistrationChanged>(Handle);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PlayerRegistrationChanged>(Handle);
    }

    private void Handle(MessagePayload<PlayerRegistrationChanged> payload)
    {
        network.SendAll(new NetworkPlayerRegistrationUpdated(payload.What.Player));
    }
}
