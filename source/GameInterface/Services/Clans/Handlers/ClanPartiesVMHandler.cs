using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Clans.Messages;
using GameInterface.Services.Heroes.Extensions;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.ObjectManager;
using Helpers;
using LiteNetLib;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using static GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Services.Clans.Handlers;

internal class ClanPartiesVMHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ClanPartiesVMHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly ISendCoalescer sendCoalescer;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<ClanPartyCreateIntent, ClanPartyCreateResult> createRoute;
    private readonly IAuthorityRouteHandle<ClanPartyLeaderIntent, ClanPartyLeaderChangeResult> leaderRoute;

    public ClanPartiesVMHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network,
        IModConfigAuthority configAuthority,
        IAuthorityRequestRouter authorityRequestRouter,
        ISendCoalescer sendCoalescer = null)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.sendCoalescer = sendCoalescer;
        this.configAuthority = configAuthority;
        createRoute = authorityRequestRouter.Register(
            AuthorityRoute<ClanPartyCreateIntent, CreateNewClanParty, ClanPartyCreateResult>.Define(
                "clan.party.create", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new CreateNewClanParty(intent.NewLeaderId, header),
                request => request.Header, result => result.Header,
                request => string.IsNullOrWhiteSpace(request.NewLeaderId) ? "clan-party-leader-missing" : null,
                request => request.NewLeaderId, ValidateHeader, ExecuteCreate, CreateCreateTerminal, ProbeCreate,
                _ => { }, PresentCreate, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                isExpectedClientResult: (request, result) => request.NewLeaderId == result.NewLeaderId));
        leaderRoute = authorityRequestRouter.Register(
            AuthorityRoute<ClanPartyLeaderIntent, ChangeClanPartyLeader, ClanPartyLeaderChangeResult>.Define(
                "clan.party.leader.set", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new ChangeClanPartyLeader(intent.NewLeaderId, intent.SelectedPartyId, header),
                request => request.Header, result => result.Header,
                request => string.IsNullOrWhiteSpace(request.SelectedPartyId) ? "clan-party-missing" : null,
                request => request.SelectedPartyId + ":" + (request.NewLeaderId ?? "disband"), ValidateHeader,
                ExecuteLeaderChange, CreateLeaderTerminal, ProbeLeaderChange, _ => { }, PresentLeaderChange,
                configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation,
                isExpectedClientResult: (request, result) => request.SelectedPartyId == result.SelectedPartyId && request.NewLeaderId == result.NewLeaderId));
        messageBroker.Subscribe<NewClanPartyCreated>(Handle_NewClanPartyCreated);
        messageBroker.Subscribe<ClanPartyLeaderChanged>(Handle_ClanPartyLeaderChanged);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NewClanPartyCreated>(Handle_NewClanPartyCreated);
        messageBroker.Unsubscribe<ClanPartyLeaderChanged>(Handle_ClanPartyLeaderChanged);
        createRoute.Dispose();
        leaderRoute.Dispose();
    }

    private void Handle_NewClanPartyCreated(MessagePayload<NewClanPartyCreated> obj)
    {
        if (ModInformation.IsServer || !objectManager.TryGetIdWithLogging(obj.What.NewLeader, out var newLeaderId)) return;
        createRoute.Submit(new ClanPartyCreateIntent(newLeaderId));
    }

    private void Handle_ClanPartyLeaderChanged(MessagePayload<ClanPartyLeaderChanged> obj)
    {
        string newLeaderId = null;
        if (ModInformation.IsServer ||
            obj.What.NewLeader != null && !objectManager.TryGetIdWithLogging(obj.What.NewLeader, out newLeaderId) ||
            !objectManager.TryGetIdWithLogging(obj.What.SelectedParty, out var selectedPartyId)) return;
        leaderRoute.Submit(new ClanPartyLeaderIntent(newLeaderId, selectedPartyId));
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out var snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out var snapshot)) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        if (header.ProtocolVersion != snapshot.ProtocolVersion) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.InvalidRequest, "unsupported-protocol");
        if (header.SessionId != snapshot.SessionId) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session");
        return header.ExpectedRevision == snapshot.Revision ? AuthorityHeaderValidation.Valid : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
    }

    private AuthorityServerReply<ClanPartyCreateResult> ExecuteCreate(AuthorityServerContext context, CreateNewClanParty request)
    {
        if (!TryGetActorClanLeader(context, out var actor, out var clan, out _, out var reason)) return CreateReply(context.Header, request.NewLeaderId, null, AuthorityResultStatus.Unauthorized, reason);
        if (!objectManager.TryGetObject(request.NewLeaderId, out Hero newLeader) || newLeader.Clan != clan || newLeader.IsPlayerHero())
            return CreateReply(context.Header, request.NewLeaderId, clan.StringId, AuthorityResultStatus.Rejected, "clan-party-leader-invalid");
        if (newLeader.PartyBelongedTo != null)
            return CreateReply(context.Header, request.NewLeaderId, clan.StringId, AuthorityResultStatus.Rejected, "clan-party-leader-already-assigned");

        MobileParty party = MobilePartyHelper.CreateNewClanMobileParty(newLeader, clan);
        int threshold = Campaign.Current.Models.ClanFinanceModel.PartyGoldLowerThreshold;
        if (newLeader.Gold < threshold) GiveGoldAction.ApplyBetweenCharacters(actor, newLeader, threshold - newLeader.Gold, false);
        party.SetMoveModeHold();
        if (objectManager.TryGetId(party.MemberRoster, out var rosterId)) sendCoalescer?.FlushInstance(Compact(rosterId, typeof(TroopRoster)), network);
        network.Send(context.Peer, new RefreshPartiesList());
        return CreateReply(context.Header, request.NewLeaderId, clan.StringId,
            newLeader.PartyBelongedTo == party ? AuthorityResultStatus.Accepted : AuthorityResultStatus.ExecutionFailed,
            newLeader.PartyBelongedTo == party ? null : "clan-party-create-not-committed");
    }

    private AuthorityServerReply<ClanPartyLeaderChangeResult> ExecuteLeaderChange(AuthorityServerContext context, ChangeClanPartyLeader request)
    {
        if (!TryGetActorClanLeader(context, out var actor, out var clan, out var actorParty, out var reason)) return LeaderReply(context.Header, request.SelectedPartyId, request.NewLeaderId, AuthorityResultStatus.Unauthorized, reason);
        if (!objectManager.TryGetObject(request.SelectedPartyId, out MobileParty party) || party.ActualClan != clan || party.IsPlayerParty())
            return LeaderReply(context.Header, request.SelectedPartyId, request.NewLeaderId, AuthorityResultStatus.Unauthorized, "clan-party-owner-mismatch");
        Hero newLeader = null;
        if (request.NewLeaderId != null && (!objectManager.TryGetObject(request.NewLeaderId, out newLeader) || newLeader.Clan != clan || newLeader.IsPlayerHero()))
            return LeaderReply(context.Header, request.SelectedPartyId, request.NewLeaderId, AuthorityResultStatus.Rejected, "clan-party-leader-invalid");
        Hero oldLeader = party.Party?.LeaderHero;
        if (oldLeader != null && (oldLeader.Clan != clan || oldLeader.IsPlayerHero()))
            return LeaderReply(context.Header, request.SelectedPartyId, request.NewLeaderId, AuthorityResultStatus.Unauthorized, "clan-party-current-leader-mismatch");

        if (newLeader == null)
        {
            if (oldLeader != null) { party.RemovePartyLeader(); MakeHeroFugitiveAction.Apply(oldLeader, false); }
            DisbandPartyAction.StartDisband(party);
        }
        else
        {
            if (newLeader.PartyBelongedTo != null) return LeaderReply(context.Header, request.SelectedPartyId, request.NewLeaderId, AuthorityResultStatus.Rejected, "clan-party-new-leader-assigned");
            if (oldLeader != null) TeleportHeroAction.ApplyDelayedTeleportToParty(oldLeader, actorParty);
            TeleportHeroAction.ApplyDelayedTeleportToPartyAsPartyLeader(newLeader, party);
            int threshold = Campaign.Current.Models.ClanFinanceModel.PartyGoldLowerThreshold;
            if (newLeader.Gold < threshold) GiveGoldAction.ApplyBetweenCharacters(actor, newLeader, threshold - newLeader.Gold, false);
        }
        network.Send(context.Peer, new RefreshPartiesList());
        bool committed = newLeader == null ? party.IsDisbanding : party.Party?.LeaderHero == newLeader;
        return LeaderReply(context.Header, request.SelectedPartyId, request.NewLeaderId,
            committed ? AuthorityResultStatus.Accepted : AuthorityResultStatus.ExecutionFailed,
            committed ? null : "clan-party-leader-not-committed");
    }

    private bool TryGetActorClanLeader(AuthorityServerContext context, out Hero actor, out Clan clan, out MobileParty actorParty, out string reason)
    {
        actor = null; clan = null; actorParty = null;
        if (string.IsNullOrWhiteSpace(context.Player.HeroId) || !objectManager.TryGetObject(context.Player.HeroId, out actor) ||
            string.IsNullOrWhiteSpace(context.Player.ClanId) || !objectManager.TryGetObject(context.Player.ClanId, out clan) || actor.Clan != clan)
        { reason = "actor-hero-clan-mismatch"; return false; }
        if (clan.Leader != actor) { reason = "clan-party-leader-permission-denied"; return false; }
        if (string.IsNullOrWhiteSpace(context.Player.MobilePartyId) ||
            !objectManager.TryGetObject(context.Player.MobilePartyId, out actorParty) || actor.PartyBelongedTo != actorParty ||
            actorParty.Party?.LeaderHero != actor)
        { reason = "actor-party-mismatch"; return false; }
        reason = null; return true;
    }

    private static AuthorityServerReply<ClanPartyCreateResult> CreateReply(AuthorityRequestHeader header, string leaderId, string clanId, AuthorityResultStatus status, string reason) =>
        new(new ClanPartyCreateResult(leaderId, clanId, new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason)), status == AuthorityResultStatus.Accepted);
    private static ClanPartyCreateResult CreateCreateTerminal(AuthorityRequestHeader h, AuthorityResultStatus s, string r) => new(null, null, new AuthorityResultHeader(h.SessionId, h.RequestId, s, h.ExpectedRevision, r));
    private static AuthorityServerReply<ClanPartyLeaderChangeResult> LeaderReply(AuthorityRequestHeader h, string partyId, string leaderId, AuthorityResultStatus s, string r) =>
        new(new ClanPartyLeaderChangeResult(partyId, leaderId, new AuthorityResultHeader(h.SessionId, h.RequestId, s, h.ExpectedRevision, r)), s == AuthorityResultStatus.Accepted);
    private static ClanPartyLeaderChangeResult CreateLeaderTerminal(AuthorityRequestHeader h, AuthorityResultStatus s, string r) => new(null, null, new AuthorityResultHeader(h.SessionId, h.RequestId, s, h.ExpectedRevision, r));
    private AuthorityCommitProbeResult ProbeCreate(ClanPartyCreateResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted) return AuthorityCommitProbeResult.Applied;
        return objectManager.TryGetObject(result.NewLeaderId, out Hero leader) && leader.PartyBelongedTo != null && leader.Clan?.StringId == result.ClanId ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }
    private AuthorityCommitProbeResult ProbeLeaderChange(ClanPartyLeaderChangeResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted) return AuthorityCommitProbeResult.Applied;
        if (!objectManager.TryGetObject(result.SelectedPartyId, out MobileParty party)) return result.NewLeaderId == null ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
        if (result.NewLeaderId == null) return party.IsDisbanding ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
        return objectManager.TryGetObject(result.NewLeaderId, out Hero leader) && party.Party?.LeaderHero == leader ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }
    private static void PresentCreate(AuthorityClientOutcome<ClanPartyCreateResult> outcome) { if (!outcome.Applied) Logger.Warning("Clan party creation rejected: {Reason}", outcome.ReasonCode); }
    private static void PresentLeaderChange(AuthorityClientOutcome<ClanPartyLeaderChangeResult> outcome) { if (!outcome.Applied) Logger.Warning("Clan party leader update rejected: {Reason}", outcome.ReasonCode); }

    private readonly struct ClanPartyCreateIntent { public ClanPartyCreateIntent(string newLeaderId) { NewLeaderId = newLeaderId; } public string NewLeaderId { get; } }
    private readonly struct ClanPartyLeaderIntent { public ClanPartyLeaderIntent(string newLeaderId, string selectedPartyId) { NewLeaderId = newLeaderId; SelectedPartyId = selectedPartyId; } public string NewLeaderId { get; } public string SelectedPartyId { get; } }
}
