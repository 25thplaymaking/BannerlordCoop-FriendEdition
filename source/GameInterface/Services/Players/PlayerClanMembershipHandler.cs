using Common;
using Common.Messaging;
using Common.Network;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players.Messages;
using LiteNetLib;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace GameInterface.Services.Players;

internal sealed class PlayerClanMembershipHandler : IHandler
{
    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IPlayerManager playerManager;
    private readonly IPlayerClanMembershipService membershipService;
    private readonly IObjectManager objectManager;
    private readonly Dictionary<string, PendingRequest> pending = new();

    public PlayerClanMembershipHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IPlayerManager playerManager,
        IPlayerClanMembershipService membershipService,
        IObjectManager objectManager)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.playerManager = playerManager;
        this.membershipService = membershipService;
        this.objectManager = objectManager;
        messageBroker.Subscribe<IndependentPlayerPartySelected>(HandleSelected);
        messageBroker.Subscribe<LeavePlayerClanSelected>(HandleSelected);
        messageBroker.Subscribe<RequestIndependentPlayerParty>(HandleRequest);
        messageBroker.Subscribe<RequestLeavePlayerClan>(HandleLeave);
        messageBroker.Subscribe<IndependentPlayerPartyApprovalRequested>(HandleApprovalPrompt);
        messageBroker.Subscribe<RespondIndependentPlayerParty>(HandleResponse);
        messageBroker.Subscribe<ClanLeaderReturnedNotification>(HandleLeaderReturned);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<IndependentPlayerPartySelected>(HandleSelected);
        messageBroker.Unsubscribe<LeavePlayerClanSelected>(HandleSelected);
        messageBroker.Unsubscribe<RequestIndependentPlayerParty>(HandleRequest);
        messageBroker.Unsubscribe<RequestLeavePlayerClan>(HandleLeave);
        messageBroker.Unsubscribe<IndependentPlayerPartyApprovalRequested>(HandleApprovalPrompt);
        messageBroker.Unsubscribe<RespondIndependentPlayerParty>(HandleResponse);
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
        network.SendAll(new RequestIndependentPlayerParty(true));
    }

    private void HandleSelected(MessagePayload<LeavePlayerClanSelected> _)
    {
        if (ModInformation.IsServer) return;
        network.SendAll(new RequestLeavePlayerClan(true));
    }

    private void HandleRequest(MessagePayload<RequestIndependentPlayerParty> payload)
    {
        if (ModInformation.IsClient || !payload.What.Requested || payload.Who is not NetPeer peer ||
            !playerManager.TryGetPlayer(peer, out var member) ||
            !membershipService.TryGetClanLeader(member, out var leader) ||
            !playerManager.TryGetPeer(leader.ControllerId, out var leaderPeer))
            return;

        var requestId = Guid.NewGuid().ToString("N");
        pending[requestId] = new PendingRequest(member.ControllerId, leader.ControllerId);
        var playerName = member.ControllerId;
        if (objectManager.TryGetObject(member.HeroId, out Hero hero))
            playerName = hero.Name?.ToString() ?? playerName;
        network.Send(leaderPeer, new IndependentPlayerPartyApprovalRequested(requestId, playerName));
    }

    private void HandleLeave(MessagePayload<RequestLeavePlayerClan> payload)
    {
        if (ModInformation.IsClient || !payload.What.Requested || payload.Who is not NetPeer peer ||
            !playerManager.TryGetPlayer(peer, out var member))
            return;

        GameThread.RunSafe(() => membershipService.TryLeave(member, out _), context: "Leave player clan");
    }

    private void HandleApprovalPrompt(MessagePayload<IndependentPlayerPartyApprovalRequested> payload)
    {
        if (ModInformation.IsServer) return;
        var request = payload.What;
        GameThread.RunSafe(() => InformationManager.ShowInquiry(new InquiryData(
            "Clan party request",
            $"{request.PlayerName} wants to form an independent party in your clan.",
            true,
            true,
            "Approve",
            "Decline",
            () => network.SendAll(new RespondIndependentPlayerParty(request.RequestId, true)),
            () => network.SendAll(new RespondIndependentPlayerParty(request.RequestId, false)))),
            context: "Show clan party approval");
    }

    private void HandleResponse(MessagePayload<RespondIndependentPlayerParty> payload)
    {
        if (ModInformation.IsClient || payload.Who is not NetPeer peer) return;
        var response = payload.What;
        if (!pending.TryGetValue(response.RequestId, out var request)) return;
        pending.Remove(response.RequestId);
        if (
            !playerManager.TryGetPlayer(peer, out var responder) ||
            responder.ControllerId != request.LeaderControllerId ||
            !response.Approved ||
            !playerManager.TryGetPlayer(request.MemberControllerId, out var member))
            return;

        GameThread.RunSafe(
            () => membershipService.TrySeparate(member, emergency: false, out _),
            context: "Create independent player clan party");
    }

    private readonly struct PendingRequest
    {
        public readonly string MemberControllerId;
        public readonly string LeaderControllerId;

        public PendingRequest(string memberControllerId, string leaderControllerId)
        {
            MemberControllerId = memberControllerId;
            LeaderControllerId = leaderControllerId;
        }
    }
}
