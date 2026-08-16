using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.MobileParties.Messages.Unstuck;
using Serilog;
using System;
using TaleWorlds.Library;

namespace GameInterface.Services.MobileParties.Handlers;

/// <summary>
/// Fails closed until the server can prove a non-exploitable stuck state. The former generic
/// network request could force captivity, battle, army, siege, and settlement exits from client
/// supplied identifiers, so no request-shaped packet remains registered or serializable.
/// </summary>
internal sealed class PlayerUnstuckHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<PlayerUnstuckHandler>();
    private readonly IMessageBroker messageBroker;

    public PlayerUnstuckHandler(IMessageBroker messageBroker)
    {
        this.messageBroker = messageBroker;
        messageBroker.Subscribe<PlayerUnstuckRequested>(Handle_PlayerUnstuckRequested);
    }

    public void Dispose() =>
        messageBroker.Unsubscribe<PlayerUnstuckRequested>(Handle_PlayerUnstuckRequested);

    private void Handle_PlayerUnstuckRequested(MessagePayload<PlayerUnstuckRequested> _)
    {
        if (ModInformation.IsServer) return;

        const string reason = "server-stuck-proof-unavailable";
        Logger.Warning("Rejected client unstuck request: {Reason}", reason);
        try
        {
            InformationManager.DisplayMessage(new InformationMessage(
                "Unstuck is unavailable until the server can verify a non-exploitable stuck state."));
        }
        catch (Exception exception)
        {
            Logger.Warning(exception, "Could not show unstuck authority rejection");
        }

        messageBroker.Publish(this, new PlayerUnstuckCompleted(null, new[] { reason }));
    }
}
