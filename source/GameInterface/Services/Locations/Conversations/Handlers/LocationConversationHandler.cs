using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Messages;
using GameInterface.Services.Heroes.Extensions;
using GameInterface.Services.Locations.Conversations.Patches;
using GameInterface.Services.Locations.Messages.Conversation;
using GameInterface.Services.MapEvents.Messages.Conversation;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.Players;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Configuration;
using GameInterface.Services.CampaignService.Messages;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Concurrent;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace GameInterface.Services.Locations.Conversations.Handlers;

/// <summary>
/// Bridges the client-side location-conversation acquire/release to the server-authoritative
/// <see cref="LocationConversationTracker"/>.
/// </summary>
/// <remarks>
/// Client: turns a <see cref="LocationConversationRequested"/> into a network request (rate-limited), starts
/// the held-back conversation on approval, and shows a busy message on denial.
/// Server: records the engagement and replies allow/deny; releases the NPC when the client reports the
/// conversation ended or disconnects.
/// </remarks>
internal class LocationConversationHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<LocationConversationHandler>();

    private static readonly TimeSpan BlockedMessageCooldown = TimeSpan.FromSeconds(5);

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly LocationConversationTracker tracker;
    private readonly IPlayerManager playerManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<NetworkRequestLocationConversation, NetworkLocationConversationBeginResult> beginRoute;
    private readonly IAuthorityRouteHandle<string, NetworkLocationConversationEndResult> endRoute;
    private readonly ConcurrentDictionary<NetPeer, string> waitingPartyByInitiator = new ConcurrentDictionary<NetPeer, string>();

    private DateTime lastBlockedMessageUtc = DateTime.MinValue;
    private string activeLeaseId;

    public LocationConversationHandler(
        IMessageBroker messageBroker,
        INetwork network,
        LocationConversationTracker tracker,
        IPlayerManager playerManager, IModConfigAuthority configAuthority, INetworkConfig configuration,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.tracker = tracker;
        this.playerManager = playerManager;
        this.configAuthority = configAuthority;
        if (!ModInformation.IsServer && configAuthority.TryGetCurrent(out var accepted)) tracker.ResetReplicaSession(accepted.SessionId);
        beginRoute = authorityRequestRouter.Register(AuthorityRoute<NetworkRequestLocationConversation,
            NetworkRequestLocationConversation, NetworkLocationConversationBeginResult>.Define(
            "location.conversation.begin", AuthorityRouteKind.Command, CreateHeader,
            (x,h) => new NetworkRequestLocationConversation(x.LocationId,x.CharacterId,x.Generation,h), x=>x.Header, x=>x.Header,
            ValidateBegin, x => $"{x.LocationId}:{x.CharacterId}:{x.Generation}", ValidateHeader, ExecuteBegin, BeginTerminal,
            ProbeBegin, _=>{}, PresentBegin, configAuthority.IsTrustedServer,
            new AuthorityTimeoutPolicy(configuration.ObjectCreationTimeout, configuration.ObjectCreationTimeout, 1), true));
        endRoute = authorityRequestRouter.Register(AuthorityRoute<string, NetworkLocationConversationEnded,
            NetworkLocationConversationEndResult>.Define(
            "location.conversation.end", AuthorityRouteKind.Command, CreateHeader, (x,h)=>new NetworkLocationConversationEnded(x,h), x=>x.Header,x=>x.Header,
            x=>string.IsNullOrEmpty(x.LeaseId)?"location-conversation-lease-missing":null, x=>x.LeaseId, ValidateHeader, ExecuteEnd,
            EndTerminal, ProbeEnd, _=>{}, PresentEnd, configAuthority.IsTrustedServer,
            new AuthorityTimeoutPolicy(configuration.ObjectCreationTimeout, configuration.ObjectCreationTimeout, 1), true));

        messageBroker.Subscribe<LocationConversationRequested>(Handle_LocationConversationRequested);
        messageBroker.Subscribe<HostModConfigAccepted>(Handle_HostModConfigAccepted);
        messageBroker.Subscribe<NetworkLocationConversationLeaseState>(Handle_NetworkLocationConversationLeaseState);
        messageBroker.Subscribe<LocationConversationEnded>(Handle_LocationConversationEnded);
        messageBroker.Subscribe<NetworkAllowLocationConversation>(Handle_NetworkAllowLocationConversation);
        messageBroker.Subscribe<NetworkLocationConversationDenied>(Handle_NetworkLocationConversationDenied);
        messageBroker.Subscribe<PlayerDisconnected>(Handle_PlayerDisconnected);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<LocationConversationRequested>(Handle_LocationConversationRequested);
        messageBroker.Unsubscribe<HostModConfigAccepted>(Handle_HostModConfigAccepted);
        messageBroker.Unsubscribe<NetworkLocationConversationLeaseState>(Handle_NetworkLocationConversationLeaseState);
        messageBroker.Unsubscribe<LocationConversationEnded>(Handle_LocationConversationEnded);
        messageBroker.Unsubscribe<NetworkAllowLocationConversation>(Handle_NetworkAllowLocationConversation);
        messageBroker.Unsubscribe<NetworkLocationConversationDenied>(Handle_NetworkLocationConversationDenied);
        messageBroker.Unsubscribe<PlayerDisconnected>(Handle_PlayerDisconnected);

        waitingPartyByInitiator.Clear();
        beginRoute.Dispose(); endRoute.Dispose();
    }

    internal void SubmitConversation(NetworkRequestLocationConversation request) => beginRoute.Submit(request);
    internal void SubmitConversationEnd(string leaseId) { if (!string.IsNullOrEmpty(leaseId)) endRoute.Submit(leaseId); }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out var snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }
    private static string ValidateBegin(NetworkRequestLocationConversation x) => string.IsNullOrWhiteSpace(x.LocationId) ||
        string.IsNullOrWhiteSpace(x.CharacterId) || x.Generation <= 0 ? "location-conversation-target-invalid" : null;
    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader h)
    {
        if (!configAuthority.TryGetCurrent(out var c)) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable,"config-unavailable");
        if (h.ProtocolVersion != c.ProtocolVersion) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.InvalidRequest,"unsupported-protocol");
        if (h.SessionId != c.SessionId) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession,"stale-session");
        if (h.ExpectedRevision != c.Revision) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState,"stale-state");
        return AuthorityHeaderValidation.Valid;
    }
    private AuthorityServerReply<NetworkLocationConversationBeginResult> ExecuteBegin(AuthorityServerContext context, NetworkRequestLocationConversation request)
    {
        if (!playerManager.TryGetPlayer(context.Peer,out var player) || string.IsNullOrEmpty(player.CharacterObjectId))
            return BeginReply(context.Header,request,AuthorityResultStatus.Unauthorized,"location-conversation-actor-missing",default);
        if (!tracker.ObjectManager.TryGetObject<CharacterObject>(request.CharacterId,out var target) || !target.IsHero)
            return BeginReply(context.Header,request,AuthorityResultStatus.Rejected,"location-conversation-target-missing",default);
        if (request.CharacterId == player.CharacterObjectId)
            return BeginReply(context.Header,request,AuthorityResultStatus.InvalidRequest,"location-conversation-self-target",default);
        var engager = LocationConversationTracker.ComposeKey(request.LocationId,player.CharacterObjectId);
        var targetKey = LocationConversationTracker.ComposeKey(request.LocationId,request.CharacterId);
        if (!tracker.TryBeginEngagement(context.Peer,engager,targetKey))
            return BeginReply(context.Header,request,AuthorityResultStatus.Rejected,"location-conversation-busy",default);
        var lease=tracker.BeginLease(context.Peer,context.Header.SessionId,request.LocationId,player.CharacterObjectId,request.CharacterId);
        PublishLease(context.Peer,lease); StartPlayerWaitingInteraction(context.Peer,request.CharacterId);
        return BeginReply(context.Header,request,AuthorityResultStatus.Accepted,null,lease);
    }
    private AuthorityServerReply<NetworkLocationConversationEndResult> ExecuteEnd(AuthorityServerContext context, NetworkLocationConversationEnded request)
    {
        if (!tracker.TryGetLease(request.LeaseId,out var lease)) return EndReply(context.Header,request.LeaseId,AuthorityResultStatus.Rejected,"location-conversation-not-active",default);
        if (!ReferenceEquals(lease.Owner,context.Peer)) return EndReply(context.Header,request.LeaseId,AuthorityResultStatus.Unauthorized,"location-conversation-lease-not-owned",default);
        tracker.TryEndLease(context.Peer,request.LeaseId,out var ended);
        if (ended.Active == false) { tracker.TryEndEngagement(context.Peer,out _); EndPlayerWaitingInteraction(context.Peer); }
        PublishLease(context.Peer,ended); return EndReply(context.Header,request.LeaseId,AuthorityResultStatus.Accepted,null,ended);
    }
    private void PublishLease(NetPeer peer, LocationConversationTracker.Lease x) => network.Send(peer,
        new NetworkLocationConversationLeaseState(x.SessionId,x.Id,x.Revision,x.Active,x.LocationId,x.OwnerCharacterId,x.TargetCharacterId));
    private static AuthorityServerReply<NetworkLocationConversationBeginResult> BeginReply(AuthorityRequestHeader h,NetworkRequestLocationConversation x,AuthorityResultStatus s,string reason,LocationConversationTracker.Lease lease) =>
        new(new NetworkLocationConversationBeginResult(new AuthorityResultHeader(h.SessionId,h.RequestId,s,lease.Revision,reason),lease.Id,lease.Revision,x.LocationId,lease.OwnerCharacterId,lease.TargetCharacterId,x.Generation),s==AuthorityResultStatus.Accepted);
    private static NetworkLocationConversationBeginResult BeginTerminal(AuthorityRequestHeader h,AuthorityResultStatus s,string r) => new(new AuthorityResultHeader(h.SessionId,h.RequestId,s,0,r),null,0,null,null,null,0);
    private static AuthorityServerReply<NetworkLocationConversationEndResult> EndReply(AuthorityRequestHeader h,string id,AuthorityResultStatus s,string r,LocationConversationTracker.Lease x) => new(new NetworkLocationConversationEndResult(new AuthorityResultHeader(h.SessionId,h.RequestId,s,x.Revision,r),id,x.Revision),s==AuthorityResultStatus.Accepted);
    private static NetworkLocationConversationEndResult EndTerminal(AuthorityRequestHeader h,AuthorityResultStatus s,string r) => new(new AuthorityResultHeader(h.SessionId,h.RequestId,s,0,r),null,0);
    private static AuthorityCommitProbeResult ProbeBegin(NetworkLocationConversationBeginResult x) { if(x.Header.Status!=AuthorityResultStatus.Accepted)return AuthorityCommitProbeResult.Applied; var t=LocationConversationTracker.Instance; return t!=null&&t.IsReplicaLease(x.Header.SessionId,x.LeaseId,x.LeaseRevision,true,x.LocationId,x.OwnerCharacterId,x.TargetCharacterId)?AuthorityCommitProbeResult.Applied:AuthorityCommitProbeResult.Pending; }
    private static AuthorityCommitProbeResult ProbeEnd(NetworkLocationConversationEndResult x) { if(x.Header.Status!=AuthorityResultStatus.Accepted)return AuthorityCommitProbeResult.Applied; var t=LocationConversationTracker.Instance; return t!=null&&t.IsReplicaLease(x.Header.SessionId,x.LeaseId,x.LeaseRevision,false,null,null,null)?AuthorityCommitProbeResult.Applied:AuthorityCommitProbeResult.Pending; }
    private void PresentBegin(AuthorityClientOutcome<NetworkLocationConversationBeginResult> x) { GameThread.Run(()=> { if (x.Applied) { activeLeaseId=x.Result.LeaseId; LocationConversationPatches.StartApprovedConversation(x.Result.Generation); } else { if(!string.IsNullOrEmpty(x.Result.LeaseId)) endRoute.Submit(x.Result.LeaseId); LocationConversationPatches.CancelPending(x.Result.Generation); ShowInteractionBlockedMessage(); } }); }
    private void PresentEnd(AuthorityClientOutcome<NetworkLocationConversationEndResult> x) { if (x.Applied && x.Result.LeaseId == activeLeaseId) activeLeaseId=null; }
    private void Handle_HostModConfigAccepted(MessagePayload<HostModConfigAccepted> x) { if (!ModInformation.IsServer && x?.What.Snapshot != null && configAuthority.IsCurrent(x.What.Snapshot)) tracker.ResetReplicaSession(x.What.Snapshot.SessionId); }
    private void Handle_NetworkLocationConversationLeaseState(MessagePayload<NetworkLocationConversationLeaseState> x) { if (ModInformation.IsServer || !configAuthority.IsTrustedServer(x.Who) || !configAuthority.TryGetCurrent(out var c) || c.SessionId != x.What.SessionId) return; tracker.ApplyLeaseState(x.What); }

    /// <summary>[Client] Forward the request to the server.</summary>
    private void Handle_LocationConversationRequested(MessagePayload<LocationConversationRequested> payload)
    {
        var request = payload.What;

        // The acquire patch arms a single pending request at a time and blocks re-entry until it resolves,
        // so no rate-limiting is needed here - and a cooldown could otherwise swallow a legitimate next
        // request after a fast denial, wedging all further interaction.
        // On a client, SendAll targets the server (its only connected peer).
        beginRoute.Submit(new NetworkRequestLocationConversation(request.LocationId, request.CharacterId, request.Generation));
    }

    /// <summary>[Server] Record the engagement and reply allow, or deny when the NPC is already taken.</summary>
    private void Handle_NetworkRequestLocationConversation(MessagePayload<NetworkRequestLocationConversation> payload)
    {
        if (!ModInformation.IsServer) return;

        if (!(payload.Who is NetPeer peer))
        {
            Logger.Error("Received {Message} with no originating peer", nameof(NetworkRequestLocationConversation));
            return;
        }

        var request = payload.What;
        if (!playerManager.TryGetPlayer(peer, out var player) || string.IsNullOrEmpty(player.CharacterObjectId))
        {
            Logger.Error("Could not resolve the player character for {Message}", nameof(NetworkRequestLocationConversation));
            network.Send(peer, new NetworkLocationConversationDenied(request.Generation));
            return;
        }

        var engagerNpcKey = LocationConversationTracker.ComposeKey(request.LocationId, player.CharacterObjectId);
        var targetNpcKey = LocationConversationTracker.ComposeKey(request.LocationId, request.CharacterId);

        // Reserve both participants so crossed requests cannot approve two conversations at once.
        if (tracker.TryBeginEngagement(peer, engagerNpcKey, targetNpcKey))
        {
            network.Send(peer, new NetworkAllowLocationConversation(request.Generation));
            StartPlayerWaitingInteraction(peer, request.CharacterId);
        }
        else
        {
            network.Send(peer, new NetworkLocationConversationDenied(request.Generation));
        }
    }

    /// <summary>[Client] Server approved: start the held-back conversation on the main thread.</summary>
    private void Handle_NetworkAllowLocationConversation(MessagePayload<NetworkAllowLocationConversation> payload)
    {
        if (ModInformation.IsServer) return;

        var generation = payload.What.Generation;
        GameThread.Run(() => LocationConversationPatches.StartApprovedConversation(generation));
    }

    /// <summary>[Client] Server denied: drop the pending request and tell the player why.</summary>
    private void Handle_NetworkLocationConversationDenied(MessagePayload<NetworkLocationConversationDenied> payload)
    {
        if (ModInformation.IsServer) return;

        var generation = payload.What.Generation;
        GameThread.Run(() =>
        {
            // Only explain the refusal if this denial still matches our current pending request; a stale denial
            // (the player left and started another) neither clears the new pending nor pops a message.
            if (LocationConversationPatches.CancelPending(generation))
            {
                ShowInteractionBlockedMessage();
            }
        });
    }

    /// <summary>[Client] This player's conversation ended; tell the server to release the NPC.</summary>
    private void Handle_LocationConversationEnded(MessagePayload<LocationConversationEnded> payload)
    {
        // On a client, SendAll targets the server (its only connected peer).
        if (!string.IsNullOrEmpty(activeLeaseId)) endRoute.Submit(activeLeaseId);
    }

    /// <summary>[Server] A client's conversation finished: release the NPC held for that player, if any.</summary>
    private void Handle_NetworkLocationConversationEnded(MessagePayload<NetworkLocationConversationEnded> payload)
    {
        if (!ModInformation.IsServer) return;

        if (!(payload.Who is NetPeer peer))
        {
            Logger.Error("Received {Message} with no originating peer", nameof(NetworkLocationConversationEnded));
            return;
        }

        GameThread.RunSafe(() =>
        {
            tracker.TryEndEngagement(peer, out _);
            EndPlayerWaitingInteraction(peer);
        }, context: nameof(NetworkLocationConversationEnded));
    }

    /// <summary>[Server] A player disconnected: release the NPC held for them, if any.</summary>
    private void Handle_PlayerDisconnected(MessagePayload<PlayerDisconnected> payload)
    {
        if (!ModInformation.IsServer) return;

        if (tracker.TryGetActiveLeaseByOwner(payload.What.PlayerId, out var lease))
            tracker.TryEndLease(payload.What.PlayerId, lease.Id, out _);
        tracker.TryEndEngagement(payload.What.PlayerId, out _);
        EndPlayerWaitingInteraction(payload.What.PlayerId);
    }

    private void StartPlayerWaitingInteraction(NetPeer initiatorPeer, string characterId)
    {
        if (!tracker.ObjectManager.TryGetObject<CharacterObject>(characterId, out var character)) return;

        var targetHero = character.HeroObject;
        if (targetHero?.IsPlayerHero() != true) return;

        var targetParty = targetHero.PartyBelongedTo?.Party;
        if (targetParty?.MobileParty?.IsPlayerParty() != true) return;
        if (!tracker.ObjectManager.TryGetId(targetParty, out var targetPartyId)) return;
        if (!waitingPartyByInitiator.TryAdd(initiatorPeer, targetPartyId)) return;

        network.SendAll(new NetworkPlayerInteractionStarted(targetPartyId, GetPlayerName(initiatorPeer), isLocationInteraction: true));
    }

    private void EndPlayerWaitingInteraction(NetPeer initiatorPeer)
    {
        if (initiatorPeer == null) return;
        if (!waitingPartyByInitiator.TryRemove(initiatorPeer, out var targetPartyId)) return;

        network.SendAll(new NetworkPlayerInteractionEnded(targetPartyId, isLocationInteraction: true));
    }

    private string GetPlayerName(NetPeer peer)
    {
        if (!playerManager.TryGetPlayer(peer, out var player)) return "Another player";
        if (!tracker.ObjectManager.TryGetObject<Hero>(player.HeroId, out var hero)) return "Another player";

        return hero.Name?.ToString() ?? "Another player";
    }

    /// <summary>
    /// Shows the local player why their interaction did nothing, at most once per cooldown so a repeatedly
    /// retried click does not flood the log. Must run on the game's main thread.
    /// </summary>
    private void ShowInteractionBlockedMessage()
    {
        var now = DateTime.UtcNow;
        if (now - lastBlockedMessageUtc < BlockedMessageCooldown) return;
        lastBlockedMessageUtc = now;

        InformationManager.DisplayMessage(new InformationMessage(
            "You cannot talk to this character while another player is talking to them"));
    }
}
