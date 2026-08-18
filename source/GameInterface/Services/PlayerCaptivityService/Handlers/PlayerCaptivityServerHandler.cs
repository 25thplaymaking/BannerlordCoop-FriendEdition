using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.MapEventParties.Messages;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.MobilePartyAIs.Patches;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.Heroes.Extensions;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.PartyBases.Extensions;
using GameInterface.Services.PartyVisuals.Extensions;
using GameInterface.Services.PartyVisuals.Messages;
using GameInterface.Services.PlayerCaptivityService.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.TroopRosters.Messages;
using Helpers;
using LiteNetLib;
using SandBox.View.Map.Managers;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace GameInterface.Services.PlayerCaptivityService.Handlers;

/// <summary>
/// Server side of player captivity. The server is authoritative for when a player hero is
/// captured and released:
/// <list type="bullet">
/// <item><see cref="PrisonerTaken"/> — a player hero is being captured after a lost battle; park the
/// player's party so native post-battle processing cannot scatter it.</item>
/// <item><see cref="NetworkPlayerSurrendered"/> — a client chose to surrender; resolve the battle on
/// the server, which then captures the player through the normal defeat path.</item>
/// <item><see cref="NetworkEndPlayerCaptivityAttempted"/> — a client requests release from captivity;
/// apply it and confirm with <see cref="NetworkPlayerCaptivityEnded"/>.</item>
/// <item><see cref="NetworkPrisonerLiberationAttempted"/> — a client liberated a prisoner through
/// the post-battle conversation; apply the vanilla relation reward for that client hero.</item>
/// <item><see cref="CampaignTick"/> — keep captive players' parties glued to their captor
/// (the server-side replacement for native <see cref="PlayerCaptivity"/>.Update).</item>
/// </list>
/// The client counterpart is <see cref="PlayerCaptivityClientHandler"/>.
/// </summary>
internal class PlayerCaptivityServerHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<PlayerCaptivityServerHandler>();
    internal static PlayerCaptivityServerHandler Instance { get; private set; }
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly IMessageBroker messageBroker;
    private readonly IPlayerManager playerManager;
    private readonly ConversationPartyTracker conversationPartyTracker;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRouteHandle<SurrenderIntent, NetworkPlayerSurrenderResult> surrenderRoute;

    // Server-issued release terms, keyed by offer id. Guarded because offers are issued and consumed
    // from the game thread but discarded from the disconnect path too.
    private readonly Dictionary<string, ReleaseOffer> releaseOffers = new Dictionary<string, ReleaseOffer>();

    private readonly struct SurrenderIntent
    {
        public SurrenderIntent(string mapEventId) => MapEventId = mapEventId;
        public string MapEventId { get; }
    }

    public PlayerCaptivityServerHandler(
        IObjectManager objectManager,
        INetwork network,
        IMessageBroker messageBroker,
        IPlayerManager playerManager,
        ConversationPartyTracker conversationPartyTracker,
        IModConfigAuthority configAuthority,
        INetworkConfig configuration,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.objectManager = objectManager;
        this.network = network;
        this.messageBroker = messageBroker;
        this.playerManager = playerManager;
        this.conversationPartyTracker = conversationPartyTracker;
        this.configAuthority = configAuthority;
        surrenderRoute = authorityRequestRouter.Register(
            AuthorityRoute<SurrenderIntent, NetworkPlayerSurrendered, NetworkPlayerSurrenderResult>.Define(
                "battle.surrender", AuthorityRouteKind.Command, CreateHeader,
                (intent, header) => new NetworkPlayerSurrendered(intent.MapEventId, header),
                request => request.Header, result => result.Header, ValidateSurrenderWire,
                request => request.MapEventId, ValidateHeader, ExecuteSurrender, SurrenderTerminal,
                ProbeSurrenderCommit, _ => { }, PresentSurrenderTerminal, configAuthority.IsTrustedServer,
                new AuthorityTimeoutPolicy(configuration.ObjectCreationTimeout, configuration.ObjectCreationTimeout, 1),
                failClosedOnApplyFailure: true,
                isExpectedClientResult: (request, result) => string.Equals(request.MapEventId, result.MapEventId, StringComparison.Ordinal)));
        Instance = this;

        // ModInformation is evaluated per call (tests flip it per instance), so each handler
        // guards itself instead of gating the subscriptions here.
        messageBroker.Subscribe<PrisonerTaken>(Handle_PrisonerTaken);
        messageBroker.Subscribe<NetworkEndCaptivityAttempted>(Handle_NetworkEndCaptivityAttempted);
        messageBroker.Subscribe<NetworkPlayerCaptivityReleaseRequest>(Handle_NetworkPlayerCaptivityReleaseRequest);
        messageBroker.Subscribe<PlayerCaptivityEndedByServer>(Handle_PlayerCaptivityEndedByServer);
        messageBroker.Subscribe<CampaignTick>(Handle_CampaignTick);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PrisonerTaken>(Handle_PrisonerTaken);
        messageBroker.Unsubscribe<NetworkEndCaptivityAttempted>(Handle_NetworkEndCaptivityAttempted);
        messageBroker.Unsubscribe<NetworkPlayerCaptivityReleaseRequest>(Handle_NetworkPlayerCaptivityReleaseRequest);
        surrenderRoute.Dispose();
        if (Instance == this) Instance = null;
        messageBroker.Unsubscribe<PlayerCaptivityEndedByServer>(Handle_PlayerCaptivityEndedByServer);
        messageBroker.Unsubscribe<CampaignTick>(Handle_CampaignTick);
    }

    /// <summary>Client entry point. The map-event key is a stale-context guard only.</summary>
    internal void RequestSurrender(string mapEventId)
    {
        if (ModInformation.IsServer || string.IsNullOrWhiteSpace(mapEventId)) return;
        surrenderRoute.Submit(new SurrenderIntent(mapEventId));
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot snapshot)) return default;
        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision);
    }

    private static string ValidateSurrenderWire(NetworkPlayerSurrendered request)
    {
        if (string.IsNullOrWhiteSpace(request.MapEventId) || request.MapEventId.Length > 256)
            return "map-event-invalid";
        // Route messages never contain a player-controlled party id. Supplying one is an attempted
        // cross-player capability, including when it happens to name the sender's own party.
        return string.IsNullOrEmpty(request.PlayerParty) ? null : "client-party-not-allowed";
    }

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out ModConfigSnapshot current))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        if (header.ProtocolVersion != current.ProtocolVersion)
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.InvalidRequest, "unsupported-protocol");
        if (!string.Equals(header.SessionId, current.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session");
        return header.ExpectedRevision == current.Revision
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
    }

    private AuthorityServerReply<NetworkPlayerSurrenderResult> ExecuteSurrender(
        AuthorityServerContext context, NetworkPlayerSurrendered request)
    {
        if (!objectManager.TryGetObject<MobileParty>(context.Player.MobilePartyId, out var playerParty) ||
            playerParty?.Party == null)
            return SurrenderReply(context.Header, AuthorityResultStatus.Rejected, request.MapEventId, null,
                "player-party-missing");
        if (!objectManager.TryGetObject<MapEvent>(request.MapEventId, out var mapEvent) || mapEvent == null ||
            playerParty.Party.MapEvent != mapEvent)
            return SurrenderReply(context.Header, AuthorityResultStatus.StaleState, request.MapEventId,
                context.Player.MobilePartyId, "map-event-not-current");
        if (mapEvent.IsFinalized || mapEvent.HasWinner || playerParty.Party.MapEventSide == null)
            return SurrenderReply(context.Header, AuthorityResultStatus.Rejected, request.MapEventId,
                context.Player.MobilePartyId, "battle-not-surrenderable");
        if (ServerBattleModeArbiter.IsClaimed(request.MapEventId))
            return SurrenderReply(context.Header, AuthorityResultStatus.Rejected, request.MapEventId,
                context.Player.MobilePartyId, "battle-claimed");
        if (!objectManager.TryGetId(playerParty.Party, out var surrenderedPartyId))
            return SurrenderReply(context.Header, AuthorityResultStatus.ExecutionFailed, request.MapEventId,
                context.Player.MobilePartyId, "party-id-unavailable");

        bool crossedMutationBoundary = false;
        try
        {
            var side = playerParty.Party.MapEventSide;
            bool hasHealthyAllies = side.Parties.Any(p => p.Party != playerParty.Party &&
                p.Party?.NumberOfHealthyMembers > 0);
            if (hasHealthyAllies)
            {
                if (!TryGetCaptorForPartialSurrender(mapEvent, playerParty.Party.Side, out var captorParty))
                    return SurrenderReply(context.Header, AuthorityResultStatus.Rejected, request.MapEventId,
                        context.Player.MobilePartyId, "captor-not-found");
                if (!TryGetPlayerHeroOfParty(playerParty, out var playerHero))
                    return SurrenderReply(context.Header, AuthorityResultStatus.Unauthorized, request.MapEventId,
                        context.Player.MobilePartyId, "player-hero-missing");

                crossedMutationBoundary = true;
                playerParty.Party.MapEventSide = null;
                network.SendAll(new NetworkPartyLeftBattle(surrenderedPartyId, false));
                TakePrisonerAction.Apply(captorParty, playerHero);
            }
            else
            {
                var playerPartyIds = MapEventPlayerPartyCollector.CollectPartyIds(mapEvent, objectManager);
                crossedMutationBoundary = true;
                PvpEncounterCloseSender.Send(network, playerPartyIds, surrenderedPartyId, request.MapEventId);
                mapEvent.DoSurrender(playerParty.Party.Side);
                messageBroker.Publish(this, new MapEventConcluded(request.MapEventId, playerPartyIds, surrenderedPartyId));
            }

            // Every native mutation ran with patches live and its ordinary state messages were sent before
            // this terminal reply. The client route waits for the party's canonical battle absence.
            return SurrenderReply(context.Header, AuthorityResultStatus.Accepted, request.MapEventId,
                context.Player.MobilePartyId, null, statePublished: true);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Authority surrender failed for {PartyId} in {MapEventId}",
                context.Player.MobilePartyId, request.MapEventId);
            if (crossedMutationBoundary)
            {
                // The requester may have received only part of a roster/battle publication. Preserve
                // replay ownership, suppress a false terminal reply, and force the only safe recovery.
                context.Peer.Disconnect();
                return new AuthorityServerReply<NetworkPlayerSurrenderResult>(
                    SurrenderTerminal(context.Header, AuthorityResultStatus.ExecutionFailed, "surrender-publication-failed"),
                    statePublished: false, suppressReply: true);
            }
            return SurrenderReply(context.Header, AuthorityResultStatus.ExecutionFailed, request.MapEventId,
                context.Player.MobilePartyId, "surrender-failed");
        }
    }

    private AuthorityCommitProbeResult ProbeSurrenderCommit(NetworkPlayerSurrenderResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted) return AuthorityCommitProbeResult.Applied;
        if (!objectManager.TryGetObject<MobileParty>(result.PlayerPartyId, out var party) || party?.Party == null)
            return AuthorityCommitProbeResult.Pending;
        return party.Party.MapEvent == null ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private static AuthorityServerReply<NetworkPlayerSurrenderResult> SurrenderReply(AuthorityRequestHeader header,
        AuthorityResultStatus status, string mapEventId, string partyId, string reason, bool statePublished = false) =>
        new(new NetworkPlayerSurrenderResult(header, status, mapEventId, partyId, reason), statePublished);

    private static NetworkPlayerSurrenderResult SurrenderTerminal(AuthorityRequestHeader header,
        AuthorityResultStatus status, string reason) => new(header, status, null, null, reason);

    private static void PresentSurrenderTerminal(AuthorityClientOutcome<NetworkPlayerSurrenderResult> outcome)
    {
        if (!outcome.Applied)
            Logger.Warning("Battle surrender did not commit. Completion={Completion} Reason={Reason}",
                outcome.Completion, outcome.ReasonCode);
    }

    /// <summary>
    /// Runs from the <see cref="TakePrisonerAction.ApplyInternal"/> postfix, after the native capture
    /// applied with patches live (hero state → Prisoner, member-roster removal, prison-roster add —
    /// each replicating to the clients as its own message, with
    /// <see cref="Hero.PartyBelongedToAsPrisoner"/> auto-synced). Only the coop-specific extras happen
    /// here: the player party's surviving companion heroes and remaining troops are recorded as prisoners
    /// of the captor (BR-061), the emptied rosters keep native
    /// <see cref="MapEvent.CaptureDefeatedPartyMembers"/> from re-processing or scattering them, and the
    /// party is parked until captivity ends. The park happens first — capturing a companion re-enters this
    /// handler (see <see cref="CaptureCompanionHeroes"/>), and the IsActive guard is the re-entrancy stop.
    /// </summary>
    private void Handle_PrisonerTaken(MessagePayload<PrisonerTaken> payload)
    {
        if (ModInformation.IsClient) return;

        var hero = payload.What.PrisonerHero;
        // PrisonerTaken is published from the TakePrisonerAction.ApplyInternal postfix, so native already
        // cleared hero.PartyBelongedTo and set PartyBelongedToAsPrisoner. The party the hero was captured
        // from therefore has to come from the message, not from the (now-null) hero.PartyBelongedTo.
        var playerParty = payload.What.PrisonerParty;

        PlayerCaptivityLogger.Debug("Handle_PrisonerTaken: hero={HeroId} party={PartyId} captor={CaptorId}",
            hero?.StringId, playerParty?.StringId, payload.What.CapturerParty?.MobileParty?.StringId);

        // Only player heroes need coop-specific handling; native TakePrisonerAction covers AI heroes.
        if (playerParty?.IsPlayerParty() != true)
        {
            PlayerCaptivityLogger.Debug("Handle_PrisonerTaken: skipping, captured party is not a player party");
            return;
        }

        // Guard against re-processing an already-parked party (a repeated capture, or one already freed).
        if (!playerParty.IsActive)
        {
            PlayerCaptivityLogger.Debug("Handle_PrisonerTaken: skipping, player party {PartyId} is already parked", playerParty.StringId);
            return;
        }

        // Park the party FIRST: capturing a companion below re-enters this handler through the
        // TakePrisonerAction postfix (a companion's PartyBelongedTo IS this same player party, so the
        // prefix snapshots it and the postfix publishes another PrisonerTaken), and the IsActive guard
        // above is what short-circuits that nested pass — running it before the guard state is set would
        // double the troop transfer and re-empty the rosters. MobileParty.IsActive is a plain flag with
        // no native side effects, so parking before the roster work only changes coop-internal ordering;
        // the captivity-end flow reactivates the party.
        playerParty.IsActive = false;

        // Vanilla ends captivity setup with the army block at the tail of
        // PlayerCaptivity.StartCaptivityInternal (IL_0089-IL_00C5): if the captured player's party is in an
        // army, disband it when the player LED it, then drop the membership. That block is unreachable in
        // coop - native TakePrisonerAction.ApplyInternal gates its whole captivity branch on
        // `prisonerCharacter == Hero.MainHero` (IL_0062) and a captured CLIENT hero never is, while
        // MobileParty.Army is excluded from AutoSync (MobilePartySync.cs:37). Run it here, AFTER the park:
        // Army.DisperseInternal skips repositioning parties with IsActive == false, which is exactly why
        // vanilla deactivates the party first (StartCaptivityInternal IL_0039).
        // The captured player can no longer finish its own PlayerEncounter, and that Finish is the ONLY
        // production trigger that releases a ConversationPartyHold. TryEngage disabled the captor's AI with
        // DisableAi(), which sets _enableAgainAtHour = CampaignTime.Never - so without this the captor sits
        // frozen ("Holding.", never walking the prisoner to a dungeon) for the entire captivity, and resumes
        // only when captivity ends and the client finally calls Finish. Release it here instead.
        ReleaseConversationHoldHeldBy(playerParty);
        // MobileParty.Position is not AutoSynced, so the owning client keeps its stale pre-battle position
        // while the server and every OTHER client have the authoritative one - measured live as the captured
        // party sitting in the wrong place on its own screen. Push the snapshot so all three converge.
        PublishCapturedPartyPosition(playerParty);


        DisbandArmyOfCapturedPlayer(playerParty, hero);

        // BR-061: surviving companion heroes riding in the surrendered party become prisoners of the
        // captor through the same TakePrisonerAction that captured the leader, BEFORE the rosters are
        // emptied below (which would silently discard them). Each capture runs with patches live, so its
        // side effects replicate exactly like the leader's did.
        CaptureCompanionHeroes(playerParty.MemberRoster, payload.What.CapturerParty);

        // BR-061: the surrendered party's remaining troops become prisoners of the captor. Transfer them
        // into the captor's prison roster BEFORE the rosters are emptied below; the additions run with
        // patches live, so they replicate to the clients the same way the removals do.
        TransferTroopsToCaptorPrisonRoster(playerParty.MemberRoster, payload.What.CapturerParty);

        // Empty the parked party's rosters so native post-battle processing cannot scatter or re-process
        // them. Empty by each element's ACTUAL current count rather than TroopRoster.Clear(): the native
        // TakePrisonerAction (run by Prefix_CaptureDefeatedPartyMembers) already removed the captured
        // hero, leaving a depleted element that Clear() subtracts AGAIN, driving the member count to -1
        // (the live "captured party roster goes negative" bug). Removing by the real count can never fall
        // below zero. The removals run with patches live, so each replicates to the clients. Note: heroes
        // the surrendered party itself HELD captive (its prison roster) are discarded here, not
        // transferred — out of BR-061's scope, which covers the surrendered party's own heroes and troops.
        EmptyRoster(playerParty.MemberRoster);
        EmptyRoster(playerParty.PrisonRoster);
        RemoveVisual(playerParty);
        if (playerParty.LeaderHero != null)
            playerParty.ChangePartyLeader(null);

        // Price this captivity now, while the captor is known, and tell the captive's client the terms.
        // Without an offer the player can only wait to be freed by someone else (audit F15).
        IssueReleaseOffer(hero, playerParty, payload.What.CapturerParty);
    }

    /// <summary>
    /// Takes every surviving companion hero still riding in the surrendered party's member roster prisoner
    /// (BR-061 "heroes" clause) through the real <see cref="TakePrisonerAction"/> — the same action that
    /// captured the leader — so each companion gets proper hero captivity state, with the member-roster
    /// removal, the captor's prison-roster add and the auto-synced <see cref="Hero.PartyBelongedToAsPrisoner"/>
    /// each replicating to the clients. Wounded companions are still captured; death-marked companions and
    /// existing prisoners are not captured again.
    /// MUST run after the party is parked: each capture re-publishes <see cref="PrisonerTaken"/> for this
    /// same party (the companion's <see cref="Hero.PartyBelongedTo"/> is the player party), and the IsActive
    /// guard in <see cref="Handle_PrisonerTaken"/> is what short-circuits that nested pass.
    /// </summary>
    private static void CaptureCompanionHeroes(TroopRoster memberRoster, PartyBase captor)
    {
        if (memberRoster == null || captor == null) return;

        // Snapshot first: each TakePrisonerAction removes its hero's element from this same roster.
        var companions = new List<Hero>();
        for (int i = 0; i < memberRoster.Count; i++)
        {
            var element = memberRoster.GetElementCopyAtIndex(i);
            if (element.Character?.IsHero != true) continue;

            // A depleted hero element (e.g. the captured leader's leftover) is not a live member.
            if (element.Number <= 0) continue;

            var companion = element.Character.HeroObject;
            // A death mark can precede the final state transition, so aliveness alone is not enough.
            if (companion == null ||
                !companion.IsAlive ||
                companion.DeathMark != KillCharacterAction.KillCharacterActionDetail.None ||
                companion.IsPrisoner)
                continue;

            companions.Add(companion);
        }

        foreach (var companion in companions)
        {
            PlayerCaptivityLogger.Debug("CaptureCompanionHeroes: capturing companion {HeroId} for captor {CaptorId}",
                companion.StringId, captor.MobileParty?.StringId);
            TakePrisonerAction.Apply(captor, companion);
        }
    }



    /// <summary>
    /// Replicates a captured party's authoritative position to every client, including its owner.
    /// </summary>
    private void PublishCapturedPartyPosition(MobileParty playerParty)
    {
        if (playerParty == null) return;
        if (!ContainerProvider.TryResolve<IMobilePartyBehaviorSnapshot>(out var snapshot)) return;
        if (!snapshot.TryCreate(playerParty, out PartyBehaviorUpdateData data)) return;

        data.ForcePosition = true;
        data.ResetMovementToHold = true;
        messageBroker.Publish(this, new PartyBehaviorUpdated(ref data));
    }

    /// <summary>
    /// Releases any AI party this captured player was holding through a conversation engagement.
    /// </summary>
    /// <remarks>
    /// Acts on the specific captured party's own engagement, never on "the" player, so one capture cannot
    /// free a lord another player is still talking to.
    /// </remarks>
    private void ReleaseConversationHoldHeldBy(MobileParty playerParty)
    {
        if (conversationPartyTracker == null || playerParty?.Party == null) return;
        if (!objectManager.TryGetId(playerParty.Party, out var engagerPartyId)) return;

        ConversationPartyHold.EndEngagementForEngagerParty(conversationPartyTracker, engagerPartyId);
    }

    /// <summary>
    /// Server-side stand-in for the army half of native <c>PlayerCaptivity.StartCaptivityInternal</c>
    /// (IL_0089-IL_00C5). Native gates that block on the captured hero being <see cref="Hero.MainHero"/>; the
    /// coop equivalent is "the hero registered to the player that owns this party", so a companion captured
    /// alongside its leader - which re-enters <see cref="Handle_PrisonerTaken"/> through the TakePrisonerAction
    /// postfix - cannot disband the army a second time.
    /// </summary>
    /// <remarks>
    /// The disband must PRECEDE the membership drop. <c>MobileParty.set_Army</c> calls
    /// <c>Army.OnRemovePartyInternal</c>, which disbands a leaderless army itself through
    /// <c>DisbandArmyAction.ApplyByLeaderPartyRemoved</c> - the wrong dispersion reason. Vanilla's order keeps
    /// the reason PlayerTakenPrisoner.
    ///
    /// Both writes run with patches live, so each party removal replicates on its own: ArmyPatches publishes
    /// MobilePartyInArmyRemoved and lets native run, ArmyHandler broadcasts NetworkRemovePartyInArmy, and the
    /// clients apply it.
    /// </remarks>
    private void DisbandArmyOfCapturedPlayer(MobileParty playerParty, Hero capturedHero)
    {
        var army = playerParty?.Army;
        if (army == null) return;

        if (!TryGetPlayerHeroOfParty(playerParty, out var owningPlayerHero) || owningPlayerHero != capturedHero)
            return;

        PlayerCaptivityLogger.Debug(
            "DisbandArmyOfCapturedPlayer: party={PartyId} army={ArmyName} playerLedIt={PlayerLedIt}",
            playerParty.StringId, army.Name?.ToString(), army.LeaderParty == playerParty);

        if (army.LeaderParty == playerParty)
        {
            DisbandArmyAction.ApplyByPlayerTakenPrisoner(army);
        }

        playerParty.Army = null;
    }

    /// <summary>
    /// Records the surrendered party's remaining regular troops as prisoners of the captor (BR-061): each
    /// non-hero member element is added to the captor's prison roster, wounded staying wounded. Heroes are
    /// excluded — a hero capture must go through <see cref="TakePrisonerAction"/>, which manages the hero
    /// state a raw roster add would bypass (the leader was captured natively; companions go through
    /// <see cref="CaptureCompanionHeroes"/>). The subsequent <see cref="EmptyRoster"/> removes the source
    /// elements, so native post-battle capture finds nothing to double-process.
    /// </summary>
    private static void TransferTroopsToCaptorPrisonRoster(TroopRoster memberRoster, PartyBase captor)
    {
        if (memberRoster == null || captor?.PrisonRoster == null) return;

        for (int i = 0; i < memberRoster.Count; i++)
        {
            var element = memberRoster.GetElementCopyAtIndex(i);
            if (element.Character == null || element.Character.IsHero) continue;

            int number = Math.Max(element.Number, 0);
            if (number == 0) continue;

            captor.PrisonRoster.AddToCounts(element.Character, number, false, Math.Max(element.WoundedNumber, 0), 0, true);
        }
    }

    /// <summary>
    /// Empties a roster to exactly zero by removing each element by its actual current count, then dropping the
    /// depleted entries. Unlike <see cref="TroopRoster.Clear"/>, this can never drive a count negative even when
    /// an earlier roster mutation left a depleted element behind.
    /// </summary>
    private static void EmptyRoster(TroopRoster roster)
    {
        if (roster == null) return;

        for (int i = roster.Count - 1; i >= 0; i--)
        {
            var element = roster.GetElementCopyAtIndex(i);
            int removeNumber = Math.Max(element.Number, 0);
            if (removeNumber > 0 || element.WoundedNumber > 0)
                roster.AddToCounts(element.Character, -removeNumber, false, -element.WoundedNumber, 0, true);
        }

        roster.RemoveZeroCounts();
    }

    /// <summary>
    /// A client surrendered (its <see cref="TaleWorlds.CampaignSystem.Encounters.PlayerEncounter"/>
    /// surrender is blocked locally). While healthy allies remain on the surrenderer's side, only the
    /// surrendering party is captured (<see cref="TakePrisonerAction"/> — its postfix drives
    /// <see cref="Handle_PrisonerTaken"/> for the companions/troops) and removed from the event, and the
    /// battle continues without it. Only when no other party on the side can still fight does the whole
    /// side surrender (<see cref="MapEvent.DoSurrender"/> — native semantics), which resolves the battle
    /// and captures the player hero through the finalize path.
    /// </summary>
    private void Handle_NetworkPlayerSurrendered(MessagePayload<NetworkPlayerSurrendered> payload)
    {
        if (ModInformation.IsClient) return;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging(payload.What.MapEventId, out MapEvent mapEvent)) return;
            if (!objectManager.TryGetObjectWithLogging(payload.What.PlayerParty, out MobileParty playerParty)) return;

            PlayerCaptivityLogger.Debug("Handle_NetworkPlayerSurrendered: applying surrender for party={PartyId} in mapEvent={MapEventId}",
                playerParty.StringId, payload.What.MapEventId);

            // While a live mission or an auto-resolve simulation owns this event, a surrender would
            // conclude the battle under the players resolving it — refuse it until the claim releases
            // (mission instance emptied / event finalized). The client menu already refuses the option
            // while claimed (BattleModeEncounterOptionsPatch); this is the authoritative backstop for
            // the race where the claim lands between the client's menu refresh and its click.
            if (ServerBattleModeArbiter.IsClaimed(payload.What.MapEventId))
            {
                Logger.Information("[PvPEncounterClose] Refused surrender of {PartyId} in {MapEventId}: the event is claimed by an active mission/simulation",
                    playerParty.StringId, payload.What.MapEventId ?? "<none>");
                return;
            }

            if (!objectManager.TryGetIdWithLogging(playerParty.Party, out var surrenderedPartyId)) return;

            // While OTHER parties on the surrenderer's side can still fight, DoSurrender below would end
            // the battle for all of them (it marks the whole side surrendered and hands the other side
            // the win). Capture just the surrendering party instead and let the battle continue.
            var side = playerParty.Party.MapEventSide;
            var hasHealthyAllies = side != null &&
                side.Parties.Any(p => p.Party != playerParty.Party && p.Party?.NumberOfHealthyMembers > 0);
            if (hasHealthyAllies)
            {
                ApplyPartialSurrender(mapEvent, playerParty, surrenderedPartyId);
                return;
            }

            var playerPartyIds = MapEventPlayerPartyCollector.CollectPartyIds(mapEvent, objectManager);

            Logger.Information("[PvPEncounterClose] Server sending immediate surrender close: partyIds=[{PartyIds}] surrenderedPartyId={SurrenderedPartyId} mapEventId={MapEventId}",
                string.Join(",", playerPartyIds),
                surrenderedPartyId ?? "<none>",
                payload.What.MapEventId ?? "<none>");
            PvpEncounterCloseSender.Send(network, playerPartyIds, surrenderedPartyId, payload.What.MapEventId);
            mapEvent.DoSurrender(playerParty.Party.Side);
            messageBroker.Publish(this, new MapEventConcluded(payload.What.MapEventId, playerPartyIds, surrenderedPartyId));
        }, blocking: true, context: nameof(Handle_NetworkPlayerSurrendered));
    }

    /// <summary>
    /// Surrenders ONLY <paramref name="playerParty"/> out of a battle its side keeps fighting: the party is
    /// removed from the event (explicit broadcast — single-party removal does not auto-replicate; applying
    /// it closes the surrenderer's own encounter menu, see <c>BattleJoinLeaveHandler.ApplyNetworkLeave</c>)
    /// and its hero is captured by the opposing side's leader as a plain out-of-battle capture. The capture
    /// runs with patches live, so its side effects replicate and its postfix-published
    /// <see cref="PrisonerTaken"/> drives <see cref="Handle_PrisonerTaken"/> (park, companions, troop
    /// transfer); the owning client then enters captivity from the synced state. No
    /// <see cref="MapEventConcluded"/>: the event lives on for the allies.
    /// </summary>
    private void ApplyPartialSurrender(MapEvent mapEvent, MobileParty playerParty, string surrenderedPartyId)
    {
        if (!TryGetCaptorForPartialSurrender(mapEvent, playerParty.Party.Side, out var captorParty))
        {
            // No enemy party can hold prisoners — the battle is effectively decided and about to resolve
            // through its own paths; a surrender into a spent side has nothing coherent to do.
            Logger.Warning("Refused partial surrender of {PartyId}: no opposing party remains to take prisoners", surrenderedPartyId);
            return;
        }

        if (!TryGetPlayerHeroOfParty(playerParty, out var playerHero))
        {
            Logger.Error("Refused partial surrender of {PartyId}: no registered player hero resolves for it", surrenderedPartyId);
            return;
        }

        Logger.Information("Applying partial surrender: party={PartyId} captor={CaptorId} — healthy allies keep fighting the battle",
            surrenderedPartyId, captorParty.MobileParty?.StringId ?? "<settlement>");

        // Out of the battle first, so the capture below is a plain out-of-battle capture.
        playerParty.Party.MapEventSide = null;
        network.SendAll(new NetworkPartyLeftBattle(surrenderedPartyId, false));

        TakePrisonerAction.Apply(captorParty, playerHero);
    }

    /// <summary>
    /// Enemy party to hold a partial surrender's prisoners: the opposing side's leader while it can still
    /// fight, else the side's first party with healthy members, else the leader regardless. False only when
    /// no opposing party remains at all.
    /// </summary>
    private static bool TryGetCaptorForPartialSurrender(MapEvent mapEvent, BattleSideEnum surrenderingSide, out PartyBase captorParty)
    {
        captorParty = null;

        var enemySide = mapEvent.GetMapEventSide(mapEvent.GetOtherSide(surrenderingSide));
        if (enemySide == null) return false;

        var leader = enemySide.LeaderParty;
        if (leader != null && leader.NumberOfHealthyMembers > 0)
        {
            captorParty = leader;
            return true;
        }

        captorParty = enemySide.Parties.FirstOrDefault(p => p.Party?.NumberOfHealthyMembers > 0)?.Party ?? leader;
        return captorParty != null;
    }

    /// <summary>
    /// Resolves the player hero registered for <paramref name="playerParty"/>. A player party's component
    /// leader is not reliably set on the server, so the player registry is the source of truth (the inverse
    /// of <see cref="TryGetPlayerParty"/>). The registry keys players by the MOBILE party's id, which is
    /// distinct from the <see cref="PartyBase"/> id used on the battle-leave wire.
    /// </summary>
    private bool TryGetPlayerHeroOfParty(MobileParty playerParty, out Hero playerHero)
    {
        playerHero = null;

        if (!objectManager.TryGetId(playerParty, out var mobilePartyId)) return false;

        foreach (var player in playerManager.Players)
        {
            if (player.MobilePartyId == mobilePartyId)
                return objectManager.TryGetObject(player.HeroId, out playerHero);
        }

        return false;
    }

    /// <summary>
    /// A captive player chose to end their own captivity, quoting a server-issued offer.
    /// Re-implements native <see cref="PlayerCaptivity"/>.EndCaptivityInternal for a remote player hero,
    /// then confirms to the requesting client so it can leave the captivity menus.
    /// </summary>
    /// <remarks>
    /// The request carries an offer id and nothing else of consequence. Everything that decides the
    /// outcome — which hero, which captor, the price, where the party reappears — is read from the
    /// server's own record of that offer, so the exploit the previous message allowed (name your own
    /// ransom, name your own reappearance position) has nothing to attach to.
    ///
    /// The offer is consumed before the release runs, so a duplicate or replayed request finds nothing.
    /// </remarks>
    private void Handle_NetworkPlayerCaptivityReleaseRequest(MessagePayload<NetworkPlayerCaptivityReleaseRequest> payload)
    {
        if (ModInformation.IsClient) return;

        var offerId = payload.What.OfferId;
        var detail = payload.What.Detail;

        if (payload.Who is not NetPeer peer)
        {
            Logger.Error("Rejected {Message} without a requesting peer", nameof(NetworkPlayerCaptivityReleaseRequest));
            return;
        }

        if (!playerManager.TryGetPlayer(peer, out var player))
        {
            Logger.Warning("Rejected captivity release from peer {PeerId}: no registered player", peer.Id);
            return;
        }

        GameThread.Run(() =>
        {
            try
            {
                ReleaseOffer offer;
                lock (releaseOffers)
                {
                    if (offerId == null || !releaseOffers.TryGetValue(offerId, out offer))
                    {
                        Logger.Warning("Rejected captivity release from peer {PeerId}: unknown offer", peer.Id);
                        return;
                    }

                    // The offer belongs to one hero, and only that hero's owner may spend it.
                    if (offer.HeroId != player.HeroId)
                    {
                        Logger.Warning("Rejected captivity release from peer {PeerId}: offer belongs to another player",
                            peer.Id);
                        return;
                    }

                    // Consume first: a replay finds nothing rather than a second free release.
                    releaseOffers.Remove(offerId);
                }

                if (!objectManager.TryGetObjectWithLogging<Hero>(offer.HeroId, out var playerHero)) return;
                if (!objectManager.TryGetObjectWithLogging<MobileParty>(player.MobilePartyId, out var playerParty)) return;

                // The world may have moved on since the offer was made — freed already, or handed to a
                // different captor, which would make this price wrong.
                PartyBase captor = playerHero.PartyBelongedToAsPrisoner;
                if (captor == null)
                {
                    PlayerCaptivityLogger.Debug("Captivity release skipped: {HeroId} is no longer captive", playerHero.StringId);
                    return;
                }

                if (!objectManager.TryGetId(captor, out var currentCaptorId) || currentCaptorId != offer.CaptorPartyId)
                {
                    Logger.Warning("Rejected captivity release for {HeroId}: the captor changed since the offer",
                        playerHero.StringId);
                    return;
                }

                var isPaidRansom = detail == EndCaptivityDetail.Ransom;
                if (isPaidRansom && (offer.RansomAmount <= 0 || playerHero.Gold < offer.RansomAmount))
                {
                    Logger.Warning(
                        "Refused ransom release for {HeroId}: price={Amount}, available={Gold}",
                        playerHero.StringId, offer.RansomAmount, playerHero.Gold);
                    return;
                }

                // Derived, never taken from the client: native drops the freed party at the captor.
                CampaignVec2 releasePosition = GetReleasePosition(captor, playerParty.Position);

                Hero facilitator = captor.LeaderHero;
                var capturerFaction = captor.MapFaction;

                if (!ReleasePlayerFromCaptivity(playerHero, playerParty, detail, facilitator, releasePosition))
                    return;

                if (isPaidRansom)
                {
                    GiveGoldAction.ApplyBetweenCharacters(playerHero, null, offer.RansomAmount, false);
                    GrantRansomSafeConduct(playerParty, capturerFaction);
                }

                PlayerCaptivityLogger.Debug("Released {HeroId} on offer {OfferId} ({Detail}, {Amount} denars)",
                    playerHero.StringId, offerId, detail, isPaidRansom ? offer.RansomAmount : 0);

                network.Send(peer, new NetworkPlayerCaptivityEnded());
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to apply {Message}", nameof(NetworkPlayerCaptivityReleaseRequest));
            }
        }, blocking: true);
    }

    /// <summary>
    /// Prices this capture and offers the captive's own client the terms, once.
    /// </summary>
    /// <remarks>
    /// Native prices a captivity once, in <c>PlayerCaptivity.SetRansomAmount</c>, and keeps the number for
    /// its duration. This does the same, on the only machine whose word counts. The client is told the
    /// figure purely so its menu quotes what it will actually be charged.
    /// </remarks>
    private void IssueReleaseOffer(Hero captive, MobileParty capturedParty, PartyBase captor)
    {
        if (captive == null || captor == null) return;
        if (!objectManager.TryGetId(captive, out var heroId)) return;
        if (!objectManager.TryGetId(captor, out var captorPartyId)) return;

        int ransom = PlayerCaptivityRansom.ForCaptive(captive, captor);
        var offerId = Guid.NewGuid().ToString("N");

        lock (releaseOffers)
        {
            // One live offer per hero: a re-capture replaces the stale terms rather than adding to them.
            foreach (var existing in releaseOffers.Where(entry => entry.Value.HeroId == heroId).ToList())
                releaseOffers.Remove(existing.Key);

            releaseOffers[offerId] = new ReleaseOffer(offerId, heroId, captorPartyId, ransom);
        }

        if (!PlayerManager.TryGetControlledObjectInfo(capturedParty, out var controlInfo) ||
            !playerManager.TryGetPeer(controlInfo.ObjectControllerId, out var peer))
        {
            PlayerCaptivityLogger.Debug("Priced captivity of {HeroId} at {Amount} but its owner is not connected",
                heroId, ransom);
            return;
        }

        PlayerCaptivityLogger.Debug("Offering release of {HeroId} at {Amount} denars (offer {OfferId})",
            heroId, ransom, offerId);
        network.Send(peer, new NetworkPlayerCaptivityReleaseOffer(offerId, ransom));
    }

    /// <summary>Drops any live offer for a hero whose captivity ended by some other route.</summary>
    private void DiscardReleaseOffers(string heroId)
    {
        if (heroId == null) return;
        lock (releaseOffers)
        {
            foreach (var existing in releaseOffers.Where(entry => entry.Value.HeroId == heroId).ToList())
                releaseOffers.Remove(existing.Key);
        }
    }

    /// <summary>Terms the server issued for one captivity. Priced once, spent once.</summary>
    /// <summary>
    /// Test seam: the live release terms outstanding for a captive, if any. Tests cannot predict the
    /// figure — <see cref="PlayerCaptivityRansom.ForCaptive"/> rolls <c>MBRandom.RandomFloat</c> — and
    /// the client is never told anything it could compute, so reading the server's own record is the
    /// only honest way to assert what a release should cost.
    /// </summary>
    internal bool TryGetReleaseOffer(string heroId, out string offerId, out int ransomAmount)
    {
        lock (releaseOffers)
        {
            foreach (var entry in releaseOffers)
            {
                if (!string.Equals(entry.Value.HeroId, heroId, StringComparison.Ordinal)) continue;

                offerId = entry.Key;
                ransomAmount = entry.Value.RansomAmount;
                return true;
            }
        }

        offerId = null;
        ransomAmount = 0;
        return false;
    }

    private readonly struct ReleaseOffer
    {
        public ReleaseOffer(string offerId, string heroId, string captorPartyId, int ransomAmount)
        {
            OfferId = offerId;
            HeroId = heroId;
            CaptorPartyId = captorPartyId;
            RansomAmount = ransomAmount;
        }

        public string OfferId { get; }
        public string HeroId { get; }
        public string CaptorPartyId { get; }
        public int RansomAmount { get; }
    }

    /// <summary>
    /// The server itself freed a player (client) hero — typically because the captor party was defeated in
    /// battle (native <see cref="MapEvent.LootDefeatedPartyPrisoners"/> →
    /// <see cref="EndCaptivityAction.ApplyByReleasedAfterBattle"/>), but also AI ransoms and peace releases.
    /// Unlike the client-requested path there is no request to answer; the owning client leaves the
    /// captivity menus on its own when the cleared <see cref="Hero.PartyBelongedToAsPrisoner"/> syncs to it
    /// (<see cref="PlayerCaptivityClientHandler"/>).
    /// </summary>
    private void Handle_PlayerCaptivityEndedByServer(MessagePayload<PlayerCaptivityEndedByServer> payload)
    {
        if (ModInformation.IsClient) return;

        var playerHero = payload.What.PrisonerHero;
        if (playerHero == null) return;

        if (!TryGetPlayerParty(playerHero, out var playerParty))
        {
            Logger.Error("Could not resolve a player party for released hero {HeroId}; cannot restore it", playerHero.StringId);
            return;
        }

        PlayerCaptivityLogger.Debug("Handle_PlayerCaptivityEndedByServer: hero={HeroId} party={PartyId} detail={Detail}",
            playerHero.StringId, playerParty.StringId, payload.What.Detail);

        // Freed by some other route (captor defeated, AI ransom, peace): any live offer is now stale.
        if (objectManager.TryGetId(playerHero, out var releasedHeroId)) DiscardReleaseOffers(releasedHeroId);

        var captorParty = playerHero.PartyBelongedToAsPrisoner;
        var releasePosition = payload.What.HasReleasePosition
            ? payload.What.ReleasePosition
            : GetReleasePosition(captorParty, playerParty.Position);

        var capturerFaction = captorParty?.MapFaction;
        if (ReleasePlayerFromCaptivity(playerHero, playerParty, payload.What.Detail, payload.What.Facilitator, releasePosition) &&
            payload.What.Detail == EndCaptivityDetail.Ransom)
        {
            GrantRansomSafeConduct(playerParty, capturerFaction);
        }
    }

    /// <summary>
    /// Server-authoritative release of a player (client) hero from captivity, shared by the client-requested
    /// and server-initiated paths. Restores the deactivated player party to the map and clears the captivity
    /// state — which auto-syncs to the clients through <see cref="Hero.PartyBelongedToAsPrisoner"/>.
    /// Re-implements native <see cref="PlayerCaptivity"/>.EndCaptivityInternal for a hero that is not this
    /// instance's main hero; the menu/encounter cleanup the native version does happens on the owning client
    /// instead (<see cref="PlayerCaptivityClientHandler"/>).
    /// </summary>
    private bool ReleasePlayerFromCaptivity(Hero playerHero, MobileParty playerParty, EndCaptivityDetail detail, Hero facilitator, CampaignVec2 releasePosition)
    {
        // Guard against re-processing an already-ended captivity: a client release request can race a
        // server-initiated release, and a second pass would re-add the hero to the member roster,
        // doubling the troop count. The captor reference is the captivity's source of truth — it is
        // still set on every legitimate entry (the EndCaptivityAction prefix intercepts before native
        // clears anything, including a death in captivity) and cleared below on the first pass.
        if (playerHero.PartyBelongedToAsPrisoner == null)
        {
            PlayerCaptivityLogger.Debug("ReleasePlayerFromCaptivity: skipping, hero {HeroId} is no longer captive", playerHero.StringId);
            return false;
        }

        // Snapshot the captor before the release: clearing the captivity below nulls
        // PartyBelongedToAsPrisoner, and a captor defeated in battle may already be inactive.
        PartyBase captorParty = playerHero.PartyBelongedToAsPrisoner;
        IFaction capturerFaction = captorParty?.MapFaction;

        if (playerHero.IsAlive)
        {
            playerHero.ChangeState(Hero.CharacterStates.Active);
            playerParty.AddElementToMemberRoster(playerHero.CharacterObject, 1, true);
            playerParty.ChangePartyLeader(playerHero);
        }
        if (playerHero.CurrentSettlement != null)
        {
            if (playerHero.IsAlive)
            {
                LeaveSettlementAction.ApplyForParty(playerParty);
            }
            else
            {
                LeaveSettlementAction.ApplyForCharacterOnly(playerHero);
            }
        }

        // Clear the captivity. Removing the hero from the captor's prison roster clears
        // PartyBelongedToAsPrisoner via the engine hook; do this regardless of whether the captor is still
        // active, since a captor defeated in battle may already be inactive. If the roster no longer holds
        // the hero, null it directly so the cleared state still auto-syncs to the owning client.
        if (captorParty != null)
        {
            var prisonRoster = captorParty.PrisonRoster;
            int prisonerIndex = prisonRoster.FindIndexOfTroop(playerHero.CharacterObject);
            // Apply the authoritative cleanup without letting the roster patches publish a conditional
            // mutation. The explicit identity-keyed tombstone below must be sent even when this element
            // was already absent on the server but remains stale on one or more clients.
            using (new AllowedThread())
            {
                if (prisonerIndex >= 0)
                {
                    if (prisonRoster.GetElementWoundedNumber(prisonerIndex) != 0)
                    {
                        prisonRoster.SetElementWoundedNumber(prisonerIndex, 0);
                    }
                    prisonRoster.SetElementNumber(prisonerIndex, 0);
                }
                // Match the roster-wide cleanup every client applies below, including when the target
                // player element was already absent but another depleted element remains.
                prisonRoster.RemoveZeroCounts();
                prisonRoster.InitializeCachedData();
            }

            // Publish absolute zeroes regardless of authoritative element presence. A normal Party-screen
            // release can arrive after another server path removed the roster element while the captor client
            // still has a stale copy; skipping the tombstone in that state reproduces the ghost prisoner.
            messageBroker.Publish(this, new ElementWoundedNumberSet(prisonRoster, playerHero.CharacterObject, 0));
            messageBroker.Publish(this, new ElementNumberSet(prisonRoster, playerHero.CharacterObject, 0));
            messageBroker.Publish(this, new ZeroCountsRemoved(prisonRoster));
        }
        if (playerHero.PartyBelongedToAsPrisoner != null)
        {
            playerHero.PartyBelongedToAsPrisoner = null;
        }

        // Separate the freed party from a still-active mobile captor (mirrors native). A defeated/inactive
        // captor is skipped — there's nothing to disengage from, and navigating around a destroyed party
        // would be meaningless.
        if (captorParty?.IsActive == true && captorParty.IsMobile && !captorParty.MobileParty.IsCurrentlyAtSea)
        {
            playerParty.TeleportPartyToOutSideOfEncounterRadius();
        }

        if (captorParty != null && captorParty.IsSettlement)
        {
            playerParty.DisembarkToPosition(captorParty.Settlement.GatePosition);
        }
        else if (captorParty != null && captorParty.IsMobile)
        {
            playerParty.IsCurrentlyAtSea = captorParty.MobileParty.IsCurrentlyAtSea;
        }
        if (facilitator != null && detail != EndCaptivityDetail.Death)
        {
            StringHelpers.SetCharacterProperties("FACILITATOR", facilitator.CharacterObject, null, false);
            StringHelpers.SetCharacterProperties("PRISONER", playerHero.CharacterObject, null, false);
            MBInformationManager.AddQuickInformation(new TextObject("{=xPuSASof}{FACILITATOR.NAME} paid a ransom and freed {PRISONER.NAME} from captivity."));
        }
        CampaignEventDispatcher.Instance.OnHeroPrisonerReleased(playerHero, captorParty, capturerFaction, detail, true);

        if (playerHero.IsAlive)
        {
            playerParty.Position = releasePosition;
            playerParty.IsActive = true;
            playerParty.IgnoreForHours(4);
            if (captorParty?.MobileParty?.IsActive == true)
            {
                // Vanilla protects MainParty from its former captor for 12 hours; apply it to this client party.
                DefaultMobilePartyAIModelPatches.PreventAttacksUntil(
                    captorParty.MobileParty,
                    playerParty,
                    CampaignTime.HoursFromNow(12));
            }
            playerParty.Party.SetAsCameraFollowParty();
            playerParty.SetMoveModeHold();
            // SetMoveModeHold only resets the AI behavior, not the navigation mode, so the freed party
            // would keep whatever Party-mode target it had at capture (its old captor, or a party that
            // was destroyed and deserialized to null after a save/reload). Clear it so the released party
            // actually holds and obeys the player's next move order instead of being stuck or throwing.
            playerParty.ResetNavigationToHold();

            // Native grants the released hero captivity XP through Campaign.Current.PlayerCaptivity,
            // which on the server tracks the host hero — using it here would XP the wrong hero.
            // TODO grant the released client hero its captivity XP.
            if (playerHero == Hero.MainHero)
            {
                SkillLevelingManager.OnMainHeroReleasedFromCaptivity(PlayerCaptivity.CaptivityStartTime.ElapsedHoursUntilNow);
            }

            if (!playerParty.IsCurrentlyAtSea)
            {
                playerParty.Party.UpdateVisibilityAndInspected(playerParty.Position);
            }
            SyncReleasePosition(playerParty, releasePosition);

            // Rebuild the map mesh after the roster/leader/position are restored, so the freed party's map
            // figure reflects its (re-mounted) state rather than the stale on-foot captive mesh.
            playerParty.Party.SetVisualAsDirty();
            RecreateVisual(playerParty);
        }

        return true;
    }

    private static void GrantRansomSafeConduct(MobileParty playerParty, IFaction capturerFaction)
    {
        if (playerParty?.IsActive != true || capturerFaction == null) return;

        var disabledUntil = CampaignTime.HoursFromNow(48);

        // Mutual at the encounter layer: the released party cannot immediately attack the captor's
        // faction, and any AI party in that faction is barred from immediately recapturing the player.
        // This is intentionally scoped to the released party instead of making two whole kingdoms
        // globally peaceful on behalf of one co-op player.
        DefaultMobilePartyAIModelPatches.PreventFactionAttacksUntil(
            playerParty,
            capturerFaction,
            disabledUntil);
        DefaultMobilePartyAIModelPatches.PreventAttackerFactionAttacksAgainstPartyUntil(
            capturerFaction,
            playerParty,
            disabledUntil);
    }

    private CampaignVec2 GetReleasePosition(PartyBase captorParty, CampaignVec2 fallbackPosition)
    {
        if (captorParty == null)
            return fallbackPosition;

        if (captorParty.IsSettlement)
            return captorParty.Settlement.GatePosition;

        if (captorParty.IsMobile)
            return captorParty.MobileParty.Position;

        return captorParty.Position;
    }

    private void SyncReleasePosition(MobileParty playerParty, CampaignVec2 releasePosition)
    {
        if (!objectManager.TryGetIdWithLogging(playerParty, out string playerPartyId)) return;

        network.SendAll(new NetworkPlayerCaptivityReleasePositionSet(playerPartyId, releasePosition));
    }

    private void RemoveVisual(MobileParty party)
    {
        var partyVisual = party.Party.GetPartyVisual();
        if (partyVisual == null) return;
        if (!objectManager.TryGetIdWithLogging(partyVisual, out string partyVisualId)) return;
        if (!objectManager.TryGetIdWithLogging(party, out string mobilePartyId)) return;

        objectManager.Remove(partyVisual);

        using (new AllowedThread())
        {
            MobilePartyVisualManager.Current?.RemovePartyVisualForParty(party);
        }

        network.SendAll(new NetworkDestroyPartyVisual(partyVisualId, mobilePartyId));
    }

    private void RecreateVisual(MobileParty party)
    {
        RemoveVisual(party);

        if (!objectManager.TryGetIdWithLogging(party, out string mobilePartyId)) return;

        using (new AllowedThread())
        {
            party.CreateNewPartyVisual();
        }

        var partyVisual = party.Party.GetPartyVisual();
        if (partyVisual == null)
        {
            Logger.Error("CreateNewPartyVisual did not produce a visual for party {PartyId}", party.StringId);
            return;
        }

        if (!objectManager.AddNewObject(partyVisual, out var visualId))
        {
            Logger.Error("Failed to register recreated visual for party {PartyId}", party.StringId);
            return;
        }

        network.SendAll(new NetworkCreatePartyVisual(visualId, mobilePartyId));
    }

    /// <summary>
    /// Resolves the <see cref="MobileParty"/> registered to the player that owns <paramref name="hero"/>.
    /// </summary>
    private bool TryGetPlayerParty(Hero hero, out MobileParty playerParty)
    {
        playerParty = null;

        if (!objectManager.TryGetId(hero, out var heroId))
            return false;

        foreach (var player in playerManager.Players)
        {
            if (player.HeroId == heroId)
                return objectManager.TryGetObject(player.MobilePartyId, out playerParty);
        }

        return false;
    }

    /// <summary>
    /// Keeps captive players' parties at their captor's position; the server-side replacement for
    /// native <see cref="PlayerCaptivity"/>.Update, which only handles the local main hero.
    /// Positions are server-authoritative and replicate to the clients through party movement sync.
    /// </summary>
    private void Handle_CampaignTick(MessagePayload<CampaignTick> payload)
    {
        if (ModInformation.IsClient) return;

        foreach (var (hero, mobileParty) in PlayerHeros())
        {
            if (!hero.IsPrisoner) continue;

            var captorParty = hero.PartyBelongedToAsPrisoner;

            if (captorParty == null) continue;

            mobileParty.Position = captorParty.Position;
        }
    }

    private IEnumerable<(Hero, MobileParty)> PlayerHeros()
    {
        foreach (var player in playerManager.Players)
        {
            if (objectManager.TryGetObject<Hero>(player.HeroId, out var hero) &&
                objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out var mobileParty))
            {
                yield return (hero, mobileParty);
            }
        }
    }

    /// <summary>
    /// A client asks to release a prisoner its own party is holding — the party screen's release action
    /// and the other native flows that reach <c>EndCaptivityAction.ApplyInternal</c> for a non-local hero.
    /// </summary>
    /// <remarks>
    /// Custody is the whole authorization, and the server reads it rather than accepting it: the hero
    /// must actually be a prisoner, and the party holding them must be the requesting player's own. A
    /// client naming somebody else's prisoner is refused, so this cannot be used to empty another
    /// player's or an AI lord's dungeon.
    ///
    /// Releasing a player hero is deliberately excluded. That path restores a deactivated co-op party
    /// and has its own server-driven flow (<see cref="Handle_PlayerCaptivityEndedByServer"/>); routing it
    /// through here would half-free them.
    /// </remarks>
    private void Handle_NetworkEndCaptivityAttempted(MessagePayload<NetworkEndCaptivityAttempted> payload)
    {
        if (ModInformation.IsClient) return;

        var prisonerId = payload.What.PrisonerId;
        var facilitatorId = payload.What.FacilitatorId;
        var detail = payload.What.Detail;

        if (payload.Who is not NetPeer requester)
        {
            Logger.Error("Rejected {Message} without a requesting peer", nameof(NetworkEndCaptivityAttempted));
            return;
        }

        if (!playerManager.TryGetPlayer(requester, out var player))
        {
            Logger.Warning("Rejected captivity release from peer {PeerId}: no registered player", requester.Id);
            return;
        }

        GameThread.Run(() =>
        {
            try
            {
                if (!objectManager.TryGetObjectWithLogging<Hero>(prisonerId, out var prisoner)) return;

                Hero facilitator = null;
                if (facilitatorId != null && !objectManager.TryGetObjectWithLogging(facilitatorId, out facilitator))
                    return;

                if (prisoner.IsPlayerHero())
                {
                    Logger.Warning(
                        "Rejected captivity release from peer {PeerId}: {HeroId} is a player hero and uses the co-op release",
                        requester.Id, prisoner.StringId);
                    return;
                }

                if (!prisoner.IsPrisoner)
                {
                    PlayerCaptivityLogger.Debug("Captivity release skipped: {HeroId} is not a prisoner", prisoner.StringId);
                    return;
                }

                PartyBase captor = prisoner.PartyBelongedToAsPrisoner;
                if (captor?.MobileParty == null ||
                    !objectManager.TryGetId(captor.MobileParty, out var captorPartyId) ||
                    captorPartyId != player.MobilePartyId)
                {
                    Logger.Warning(
                        "Rejected captivity release from peer {PeerId}: {HeroId} is not held by their party",
                        requester.Id, prisoner.StringId);
                    return;
                }

                using (new AllowedThread())
                {
                    EndCaptivityAction.ApplyByReleasedByChoice(prisoner, facilitator);
                }

                PlayerCaptivityLogger.Debug("Released {HeroId} at the request of peer {PeerId} ({Detail})",
                    prisoner.StringId, requester.Id, detail);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to apply {Message}", nameof(NetworkEndCaptivityAttempted));
            }
        });
    }
}
