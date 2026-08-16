using Common;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.GameDebug.Messages;
using GameInterface.Services.MapEvents.Messages;

namespace GameInterface.Services.MapEvents.Handlers;

internal class RaidAiInterventionConfigHandler : IHandler
{
    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IModConfigAuthority configAuthority;

    public RaidAiInterventionConfigHandler(IMessageBroker messageBroker, INetwork network,
        IModConfigAuthority configAuthority)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.configAuthority = configAuthority;

        messageBroker.Subscribe<NetworkRaidAiInterventionConfigChanged>(Handle_NetworkRaidAiInterventionConfigChanged);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkRaidAiInterventionConfigChanged>(Handle_NetworkRaidAiInterventionConfigChanged);
    }

    private void Handle_NetworkRaidAiInterventionConfigChanged(MessagePayload<NetworkRaidAiInterventionConfigChanged> payload)
    {
        if (ModInformation.IsServer || !configAuthority.IsTrustedServer(payload.Who))
            return;

        MapEventConfig.AllowRaidAiIntervention = payload.What.Allow;
        messageBroker.Publish(this, new SendInformationMessage(StatusText));
    }

    internal void SetAndBroadcast(bool allow)
    {
        MapEventConfig.AllowRaidAiIntervention = allow;
        network.SendAll(new NetworkRaidAiInterventionConfigChanged(allow));
        messageBroker.Publish(this, new SendInformationMessage(StatusText));
    }

    internal static string StatusText =>
        $"Raid AI intervention is {(MapEventConfig.AllowRaidAiIntervention ? "enabled" : "disabled")}";
}
