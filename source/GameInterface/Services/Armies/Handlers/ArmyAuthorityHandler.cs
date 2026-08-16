using Common;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Configuration;
using GameInterface.Services.Armies.Messages;
using GameInterface.Services.Armies.Patches;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.Armies.Handlers;

/// <summary>
/// The only client-to-server mutation boundary for player army actions.  The older Network* army
/// messages remain server replication messages; accepting them as client commands would make every
/// client supplied party, cohesion and objective authoritative.
/// </summary>
internal sealed class ArmyAuthorityHandler : IHandler
{
    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<CreateIntent, ArmyAuthorityResult> createRoute;
    private readonly IAuthorityRouteHandle<InviteIntent, ArmyAuthorityResult> inviteRoute;
    private readonly IAuthorityRouteHandle<InviteResponseIntent, ArmyAuthorityResult> inviteResponseRoute;
    private readonly IAuthorityRouteHandle<LeaveIntent, ArmyAuthorityResult> leaveRoute;
    private readonly IAuthorityRouteHandle<KickIntent, ArmyAuthorityResult> kickRoute;
    private readonly IAuthorityRouteHandle<CohesionIntent, ArmyAuthorityResult> cohesionRoute;
    private readonly IAuthorityRouteHandle<ObjectiveIntent, ArmyAuthorityResult> objectiveRoute;
    private readonly Dictionary<string, InviteLease> invitationLeases = new Dictionary<string, InviteLease>();

    public ArmyAuthorityHandler(IMessageBroker messageBroker, IObjectManager objectManager, INetwork network,
        IModConfigAuthority configAuthority, IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.configAuthority = configAuthority;
        createRoute = authorityRequestRouter.Register(AuthorityRoute<CreateIntent, RequestCreateArmy, ArmyAuthorityResult>.Define(
            "army.create", AuthorityRouteKind.Command, Header, (x,h) => new RequestCreateArmy(x.KingdomId, x.TargetSettlementId, x.ArmyTypeId, x.PartyIds, h), x => x.Header, x => x.Header,
            x => string.IsNullOrWhiteSpace(x.KingdomId) || string.IsNullOrWhiteSpace(x.ArmyTypeId) ? "army-create-malformed" : null,
            x => x.KingdomId + ":" + x.ArmyTypeId + ":" + string.Join(",", x.PartyIds ?? new List<string>()), Validate, ExecuteCreate, Terminal, Probe, _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation));
        inviteRoute = authorityRequestRouter.Register(AuthorityRoute<InviteIntent, RequestArmyInvite, ArmyAuthorityResult>.Define(
            "army.invite", AuthorityRouteKind.Command, Header, (x,h) => new RequestArmyInvite(x.ArmyId, x.PartyId, h), x => x.Header, x => x.Header,
            x => Missing(x.ArmyId, x.PartyId), x => x.ArmyId + ":" + x.PartyId, Validate, ExecuteInvite, Terminal, Probe, _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation));
        inviteResponseRoute = authorityRequestRouter.Register(AuthorityRoute<InviteResponseIntent, RequestArmyInviteResponse, ArmyAuthorityResult>.Define(
            "army.invite.respond", AuthorityRouteKind.Command, Header, (x,h) => new RequestArmyInviteResponse(x.ArmyId, x.Accept, h), x => x.Header, x => x.Header,
            x => string.IsNullOrWhiteSpace(x.ArmyId) ? "army-id-missing" : null, x => x.ArmyId + ":" + x.Accept, Validate, ExecuteInviteResponse, Terminal, Probe, _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation));
        leaveRoute = authorityRequestRouter.Register(AuthorityRoute<LeaveIntent, RequestLeaveArmy, ArmyAuthorityResult>.Define(
            "army.leave", AuthorityRouteKind.Command, Header, (x,h) => new RequestLeaveArmy(x.ArmyId, h), x => x.Header, x => x.Header,
            x => string.IsNullOrWhiteSpace(x.ArmyId) ? "army-id-missing" : null, x => x.ArmyId, Validate, ExecuteLeave, Terminal, Probe, _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation));
        kickRoute = authorityRequestRouter.Register(AuthorityRoute<KickIntent, RequestKickArmyMember, ArmyAuthorityResult>.Define(
            "army.kick", AuthorityRouteKind.Command, Header, (x,h) => new RequestKickArmyMember(x.ArmyId, x.PartyId, h), x => x.Header, x => x.Header,
            x => Missing(x.ArmyId, x.PartyId), x => x.ArmyId + ":" + x.PartyId, Validate, ExecuteKick, Terminal, Probe, _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation));
        cohesionRoute = authorityRequestRouter.Register(AuthorityRoute<CohesionIntent, RequestBoostArmyCohesion, ArmyAuthorityResult>.Define(
            "army.boost-cohesion", AuthorityRouteKind.Command, Header, (x,h) => new RequestBoostArmyCohesion(x.ArmyId, x.Cohesion, h), x => x.Header, x => x.Header,
            x => string.IsNullOrWhiteSpace(x.ArmyId) || x.RequestedCohesion <= 0 ? "army-cohesion-malformed" : null, x => x.ArmyId + ":" + x.RequestedCohesion, Validate, ExecuteCohesion, Terminal, Probe, _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation));
        objectiveRoute = authorityRequestRouter.Register(AuthorityRoute<ObjectiveIntent, RequestChangeArmyObjective, ArmyAuthorityResult>.Define(
            "army.objective.change", AuthorityRouteKind.Command, Header, (x,h) => new RequestChangeArmyObjective(x.ArmyId, x.ObjectiveId, x.IsSettlement, h), x => x.Header, x => x.Header,
            x => Missing(x.ArmyId, x.ObjectiveId), x => x.ArmyId + ":" + x.ObjectiveId + ":" + x.IsSettlement, Validate, ExecuteObjective, Terminal, Probe, _ => { }, _ => { }, configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation));

        messageBroker.Subscribe<PlayerCreatedArmy>(OnCreate);
        messageBroker.Subscribe<MobilePartyInArmyAdded>(OnAdded);
        messageBroker.Subscribe<MobilePartyInArmyRemoved>(OnRemoved);
        messageBroker.Subscribe<PlayerBoostedArmyCohesion>(OnCohesion);
        messageBroker.Subscribe<ArmyAiBehaviorObjectChanged>(OnObjective);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PlayerCreatedArmy>(OnCreate); messageBroker.Unsubscribe<MobilePartyInArmyAdded>(OnAdded);
        messageBroker.Unsubscribe<MobilePartyInArmyRemoved>(OnRemoved); messageBroker.Unsubscribe<PlayerBoostedArmyCohesion>(OnCohesion);
        messageBroker.Unsubscribe<ArmyAiBehaviorObjectChanged>(OnObjective);
        createRoute.Dispose(); inviteRoute.Dispose(); inviteResponseRoute.Dispose(); leaveRoute.Dispose(); kickRoute.Dispose(); cohesionRoute.Dispose(); objectiveRoute.Dispose();
    }

    private void OnCreate(MessagePayload<PlayerCreatedArmy> p)
    {
        if (ModInformation.IsServer || !objectManager.TryGetId(p.What.Kingdom, out var kingdomId) || !objectManager.TryGetId(p.What.TargetSettlement, out var settlementId)) return;
        createRoute.Submit(new CreateIntent(kingdomId, settlementId, p.What.ArmyType.ToString(), Ids(p.What.Parties)));
    }
    private void OnAdded(MessagePayload<MobilePartyInArmyAdded> p)
    {
        if (ModInformation.IsServer || !objectManager.TryGetId(p.What.Army, out var armyId) || !objectManager.TryGetId(p.What.MobileParty, out var partyId)) return;
        // Joining an encountered army is the response path; manager-selected parties are leader invitations.
        if (ReferenceEquals(p.What.MobileParty, MobileParty.MainParty)) inviteResponseRoute.Submit(new InviteResponseIntent(armyId, true));
        else inviteRoute.Submit(new InviteIntent(armyId, partyId));
    }
    private void OnRemoved(MessagePayload<MobilePartyInArmyRemoved> p)
    {
        if (ModInformation.IsServer || !objectManager.TryGetId(p.What.Army, out var armyId) || !objectManager.TryGetId(p.What.MobileParty, out var partyId)) return;
        if (ReferenceEquals(p.What.MobileParty, MobileParty.MainParty)) leaveRoute.Submit(new LeaveIntent(armyId));
        else kickRoute.Submit(new KickIntent(armyId, partyId));
    }
    private void OnCohesion(MessagePayload<PlayerBoostedArmyCohesion> p)
    {
        if (ModInformation.IsServer || !objectManager.TryGetId(p.What.ArmyLeaderParty?.Army, out var armyId)) return;
        cohesionRoute.Submit(new CohesionIntent(armyId, p.What.CohesionToGain));
    }
    private void OnObjective(MessagePayload<ArmyAiBehaviorObjectChanged> p)
    {
        if (ModInformation.IsServer || !objectManager.TryGetId(p.What.Army, out var armyId) || !objectManager.TryGetId(p.What.AiBehaviorObject, out var objectiveId)) return;
        objectiveRoute.Submit(new ObjectiveIntent(armyId, objectiveId, p.What.AiBehaviorObject is Settlement));
    }

    private AuthorityServerReply<ArmyAuthorityResult> ExecuteCreate(AuthorityServerContext c, RequestCreateArmy r)
    {
        if (!Actor(c, out var actor, out var reason) || actor.Army != null || !objectManager.TryGetObject<Kingdom>(r.KingdomId, out var kingdom) ||
            !ReferenceEquals(actor.MapFaction, kingdom) || !objectManager.TryGetObject<Settlement>(r.TargetSettlementId, out var target) ||
            !Enum.TryParse(r.ArmyTypeId, true, out Army.ArmyTypes armyType)) return Reply(c.Header, null, null, AuthorityResultStatus.Rejected, reason ?? "army-create-ineligible");
        kingdom.CreateArmy(actor.LeaderHero, target, armyType);
        var army = actor.Army;
        if (army == null || !objectManager.TryGetId(army, out var armyId)) return Reply(c.Header, null, null, AuthorityResultStatus.ExecutionFailed, "army-create-not-committed");
        foreach (var party in AuthoritativeEligibleParties(actor, r.PartyIds)) Add(army, party, false);
        network.SendAll(new NetworkPlayerCreatedArmy(r.KingdomId, actor.LeaderHero.StringId, r.TargetSettlementId, armyType.ToString(), Ids(army.Parties.Where(x => !ReferenceEquals(x, actor)))));
        return Reply(c.Header, armyId, null, AuthorityResultStatus.Accepted);
    }
    private AuthorityServerReply<ArmyAuthorityResult> ExecuteInvite(AuthorityServerContext c, RequestArmyInvite r)
    {
        if (!Actor(c, out var actor, out var reason) || !Army(r.ArmyId, out var army) || !ReferenceEquals(army.LeaderParty, actor) || !Party(r.PartyId, out var party) || party.Army != null)
            return Reply(c.Header, r.ArmyId, r.PartyId, AuthorityResultStatus.Rejected, reason ?? "army-invite-ineligible");
        if (party.IsPlayerParty()) { invitationLeases[LeaseKey(r.ArmyId, r.PartyId)] = new InviteLease(actor.StringId, DateTime.UtcNow.AddMinutes(1)); return Reply(c.Header, r.ArmyId, r.PartyId, AuthorityResultStatus.Accepted); }
        if (!SameFaction(actor, party)) return Reply(c.Header, r.ArmyId, r.PartyId, AuthorityResultStatus.Rejected, "army-invite-faction-mismatch");
        Add(army, party, false); return Reply(c.Header, r.ArmyId, r.PartyId, AuthorityResultStatus.Accepted);
    }
    private AuthorityServerReply<ArmyAuthorityResult> ExecuteInviteResponse(AuthorityServerContext c, RequestArmyInviteResponse r)
    {
        if (!Actor(c, out var actor, out var reason) || !Army(r.ArmyId, out var army)) return Reply(c.Header, r.ArmyId, null, AuthorityResultStatus.Rejected, reason ?? "army-invite-response-ineligible");
        var key = LeaseKey(r.ArmyId, c.Player.MobilePartyId);
        if (!invitationLeases.TryGetValue(key, out var lease) || lease.ExpiresAt < DateTime.UtcNow || !string.Equals(army.LeaderParty?.StringId, lease.Owner, StringComparison.Ordinal)) return Reply(c.Header, r.ArmyId, null, AuthorityResultStatus.Rejected, "army-invite-expired");
        invitationLeases.Remove(key); // Tombstone by removal: a lease is single-use and cannot be replayed.
        if (!r.Accept) return Reply(c.Header, r.ArmyId, c.Player.MobilePartyId, AuthorityResultStatus.Accepted);
        if (actor.Army != null || !SameFaction(actor, army.LeaderParty)) return Reply(c.Header, r.ArmyId, c.Player.MobilePartyId, AuthorityResultStatus.Rejected, "army-invite-no-longer-eligible");
        Add(army, actor, true); return Reply(c.Header, r.ArmyId, c.Player.MobilePartyId, AuthorityResultStatus.Accepted);
    }
    private AuthorityServerReply<ArmyAuthorityResult> ExecuteLeave(AuthorityServerContext c, RequestLeaveArmy r)
    {
        if (!Actor(c, out var actor, out var reason) || !Army(r.ArmyId, out var army) || !ReferenceEquals(actor.Army, army)) return Reply(c.Header, r.ArmyId, null, AuthorityResultStatus.Rejected, reason ?? "army-leave-ineligible");
        Remove(army, actor, actor); return Reply(c.Header, r.ArmyId, c.Player.MobilePartyId, AuthorityResultStatus.Accepted);
    }
    private AuthorityServerReply<ArmyAuthorityResult> ExecuteKick(AuthorityServerContext c, RequestKickArmyMember r)
    {
        if (!Actor(c, out var actor, out var reason) || !Army(r.ArmyId, out var army) || !ReferenceEquals(army.LeaderParty, actor) || !Party(r.PartyId, out var party) || ReferenceEquals(actor, party) || !ReferenceEquals(party.Army, army))
            return Reply(c.Header, r.ArmyId, r.PartyId, AuthorityResultStatus.Rejected, reason ?? "army-kick-ineligible");
        Remove(army, party, actor); return Reply(c.Header, r.ArmyId, r.PartyId, AuthorityResultStatus.Accepted);
    }
    private AuthorityServerReply<ArmyAuthorityResult> ExecuteCohesion(AuthorityServerContext c, RequestBoostArmyCohesion r)
    {
        if (!Actor(c, out var actor, out var reason) || !Army(r.ArmyId, out var army) || !ReferenceEquals(army.LeaderParty, actor)) return Reply(c.Header, r.ArmyId, null, AuthorityResultStatus.Rejected, reason ?? "army-cohesion-ineligible");
        float gain = Math.Min(10f, Math.Max(1f, r.RequestedCohesion));
        int cost = Math.Max(1, (int)Math.Ceiling(gain / 5f));
        if (actor.LeaderHero?.Clan == null || actor.LeaderHero.Clan.Influence < cost) return Reply(c.Header, r.ArmyId, null, AuthorityResultStatus.Rejected, "army-cohesion-insufficient-influence");
        army.BoostCohesionWithInfluence(gain, cost);
        return Reply(c.Header, r.ArmyId, null, AuthorityResultStatus.Accepted);
    }
    private AuthorityServerReply<ArmyAuthorityResult> ExecuteObjective(AuthorityServerContext c, RequestChangeArmyObjective r)
    {
        if (!Actor(c, out var actor, out var reason) || !Army(r.ArmyId, out var army) || !ReferenceEquals(army.LeaderParty, actor)) return Reply(c.Header, r.ArmyId, null, AuthorityResultStatus.Rejected, reason ?? "army-objective-ineligible");
        TaleWorlds.CampaignSystem.Map.IMapPoint mapPoint;
        if (r.IsSettlement)
        {
            if (!objectManager.TryGetObject<Settlement>(r.ObjectiveId, out var settlement)) return Reply(c.Header, r.ArmyId, null, AuthorityResultStatus.Rejected, "army-objective-missing");
            mapPoint = settlement;
        }
        else
        {
            if (!objectManager.TryGetObject<MobileParty>(r.ObjectiveId, out var party)) return Reply(c.Header, r.ArmyId, null, AuthorityResultStatus.Rejected, "army-objective-missing");
            mapPoint = party;
        }
        ArmyPatches.SetAiBehaviorObject(army, mapPoint); network.SendAll(new NetworkSetArmyAiBehaviorObject(r.ArmyId, r.ObjectiveId, r.IsSettlement));
        return Reply(c.Header, r.ArmyId, null, AuthorityResultStatus.Accepted);
    }

    private void Add(Army army, MobileParty party, bool merged) { ArmyPatches.AddMobilePartyInArmy(party, army); objectManager.TryGetId(army, out var a); objectManager.TryGetId(party, out var p); network.SendAll(new NetworkAddMobilePartyInArmy(a, p, merged)); }
    private void Remove(Army army, MobileParty party, MobileParty client) { ArmyPatches.RemoveMobilePartyInArmy(party, army, client); objectManager.TryGetId(army, out var a); objectManager.TryGetId(party, out var p); objectManager.TryGetId(client, out var cp); network.SendAll(new NetworkRemovePartyInArmy(a, p, cp)); }
    private bool Actor(AuthorityServerContext c, out MobileParty party, out string reason) { party = null; reason = null; if (string.IsNullOrWhiteSpace(c.Player.MobilePartyId) || !objectManager.TryGetObject(c.Player.MobilePartyId, out party) || party.LeaderHero == null || !string.Equals(party.LeaderHero.StringId, c.Player.HeroId, StringComparison.Ordinal)) { reason = "army-actor-missing"; return false; } return true; }
    private bool Army(string id, out Army army) => objectManager.TryGetObject(id, out army);
    private bool Party(string id, out MobileParty party) => objectManager.TryGetObject(id, out party);
    private static bool SameFaction(MobileParty one, MobileParty two) => one?.MapFaction != null && ReferenceEquals(one.MapFaction, two?.MapFaction);
    private IEnumerable<MobileParty> AuthoritativeEligibleParties(MobileParty actor, IEnumerable<string> ids) => (ids ?? Enumerable.Empty<string>()).Distinct().Select(id => { objectManager.TryGetObject(id, out MobileParty p); return p; }).Where(p => p != null && p.Army == null && !p.IsPlayerParty() && SameFaction(actor, p));
    private static List<string> Ids(IEnumerable<MobileParty> parties) => parties.Select(p => p.StringId).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
    private AuthorityRequestHeader Header(long requestId) => configAuthority.TryGetCurrent(out var s) ? new AuthorityRequestHeader(s.ProtocolVersion, s.SessionId, requestId, s.Revision) : default;
    private AuthorityHeaderValidation Validate(AuthorityRequestHeader h) { if (!configAuthority.TryGetCurrent(out var s)) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable"); if (h.ProtocolVersion != s.ProtocolVersion) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.InvalidRequest, "unsupported-protocol"); if (h.SessionId != s.SessionId) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session"); return h.ExpectedRevision == s.Revision ? AuthorityHeaderValidation.Valid : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state"); }
    private static string Missing(params string[] values) => values.Any(string.IsNullOrWhiteSpace) ? "army-request-malformed" : null;
    private static string LeaseKey(string armyId, string partyId) => armyId + ":" + partyId;
    private static AuthorityServerReply<ArmyAuthorityResult> Reply(AuthorityRequestHeader h, string army, string party, AuthorityResultStatus status, string reason = null) => new(new ArmyAuthorityResult(army, party, new AuthorityResultHeader(h.SessionId, h.RequestId, status, h.ExpectedRevision, reason)), status == AuthorityResultStatus.Accepted);
    private static ArmyAuthorityResult Terminal(AuthorityRequestHeader h, AuthorityResultStatus s, string reason) => new(null, null, new AuthorityResultHeader(h.SessionId, h.RequestId, s, h.ExpectedRevision, reason));
    private static AuthorityCommitProbeResult Probe(ArmyAuthorityResult r) => r.Header.Status == AuthorityResultStatus.Accepted ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Applied;
    private readonly struct InviteLease { public InviteLease(string owner, DateTime expiresAt) { Owner = owner; ExpiresAt = expiresAt; } public string Owner { get; } public DateTime ExpiresAt { get; } }
    private readonly struct CreateIntent { public CreateIntent(string kingdomId, string target, string type, List<string> parties) { KingdomId=kingdomId; TargetSettlementId=target; ArmyTypeId=type; PartyIds=parties; } public string KingdomId {get;} public string TargetSettlementId {get;} public string ArmyTypeId {get;} public List<string> PartyIds {get;} }
    private readonly struct InviteIntent { public InviteIntent(string armyId,string partyId){ArmyId=armyId;PartyId=partyId;} public string ArmyId{get;} public string PartyId{get;} }
    private readonly struct InviteResponseIntent { public InviteResponseIntent(string armyId,bool accept){ArmyId=armyId;Accept=accept;} public string ArmyId{get;} public bool Accept{get;} }
    private readonly struct LeaveIntent { public LeaveIntent(string armyId){ArmyId=armyId;} public string ArmyId{get;} }
    private readonly struct KickIntent { public KickIntent(string armyId,string partyId){ArmyId=armyId;PartyId=partyId;} public string ArmyId{get;} public string PartyId{get;} }
    private readonly struct CohesionIntent { public CohesionIntent(string armyId,float cohesion){ArmyId=armyId;Cohesion=cohesion;} public string ArmyId{get;} public float Cohesion{get;} }
    private readonly struct ObjectiveIntent { public ObjectiveIntent(string armyId,string id,bool settlement){ArmyId=armyId;ObjectiveId=id;IsSettlement=settlement;} public string ArmyId{get;} public string ObjectiveId{get;} public bool IsSettlement{get;} }
}
