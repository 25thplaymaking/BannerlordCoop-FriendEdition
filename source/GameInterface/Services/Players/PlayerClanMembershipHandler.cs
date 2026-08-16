using Common;
using Common.Messaging;
using GameInterface.Services.Players.Messages;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace GameInterface.Services.Players;

internal sealed class PlayerClanMembershipHandler : IHandler
{
    private readonly IMessageBroker messageBroker;

    public PlayerClanMembershipHandler(
        IMessageBroker messageBroker,
        Common.Network.INetwork network,
        IPlayerManager playerManager,
        IPlayerClanMembershipService membershipService,
        global::GameInterface.Services.ObjectManager.IObjectManager objectManager)
    {
        this.messageBroker = messageBroker;
        messageBroker.Subscribe<IndependentPlayerPartySelected>(HandleSelected);
        messageBroker.Subscribe<LeavePlayerClanSelected>(HandleSelected);
        messageBroker.Subscribe<ClanLeaderReturnedNotification>(HandleLeaderReturned);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<IndependentPlayerPartySelected>(HandleSelected);
        messageBroker.Unsubscribe<LeavePlayerClanSelected>(HandleSelected);
        messageBroker.Unsubscribe<ClanLeaderReturnedNotification>(HandleLeaderReturned);
    }

    private void HandleLeaderReturned(MessagePayload<ClanLeaderReturnedNotification> _)
    {
        if (ModInformation.IsServer) return;
        GameThread.RunSafe(() => InformationManager.DisplayMessage(new InformationMessage(
            "A carrier pigeon arrives: your clan leader has returned. Rejoin their party when you are ready.")),
            context: "Show clan leader return notification");
    }

    private void HandleSelected(MessagePayload<IndependentPlayerPartySelected> _)
    {
        if (ModInformation.IsServer) return;
        ShowUnavailable("Independent clan parties require a server-owned interaction session and are unavailable in this build.");
    }

    private void HandleSelected(MessagePayload<LeavePlayerClanSelected> _)
    {
        if (ModInformation.IsServer) return;
        ShowUnavailable("Leaving a player clan requires a server-owned interaction session and is unavailable in this build.");
    }

    private static void ShowUnavailable(string message) => GameThread.RunSafe(
        () => InformationManager.DisplayMessage(new InformationMessage(message)), context: "Player clan membership unavailable");
}
