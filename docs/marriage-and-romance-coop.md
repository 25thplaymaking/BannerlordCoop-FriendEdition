# Marriage, romance, and spouse behavior in Friend Edition co-op

Full review of the native marriage/romance mechanics, the co-op architecture on top of them,
and the root-cause diagnosis of the live complaints (2026-08-14): "marriage is so buggy" and
"wives/husbands aren't controllable — they do whatever they want."

## Native mechanics (reviewed)

- **Courtship** (`RomanceCampaignBehavior`, 1,439 loc): conversation-driven state machine on
  `Romance.RomanticStateList` — Untested → CourtshipStarted → CoupleDecidedThatTheyAreCompatible
  → CoupleAgreedOnMarriage (persuasion mini-games), or MatchMadeByFamily for arranged matches.
  NPC↔NPC marriages happen in `DailyTickClan`.
- **The wedding is a BARTER**: after agreement, the proposal opens the barter screen; the
  `MarriageBarterable` inside `BarterData` applies `MarriageAction.Apply` with any gold terms.
- **`MarriageAction`** (89 loc): sets both `Spouse` links, relation bump,
  `MarriageModel.GetClanAfterMarriage` decides which clan absorbs the couple; the hero changing
  clans is stripped of governorship, pulled out of their party, **made a FUGITIVE**
  (`MakeHeroFugitiveAction`), their lord party disbanded — native then re-places them later via
  teleports. Courtships end; romantic state becomes Marriage.
- **Post-marriage spouse life**: the spouse is an ordinary clan hero — placed by
  `TeleportationCampaignBehavior`/`HeroSpawnCampaignBehavior` decisions, potentially given
  parties by clan logic, assignable as governor/party member through UI and conversations.

## Co-op architecture (what exists and works by design)

- **Server-authoritative romance**: `RomanceCampaignBehavior` ticks server-only; clients request
  state changes (`RomanceHandler`), the server validates transitions (`RomanceAuthority` +
  `RomanceTransitionRules` — hardened vanilla order) and rebroadcasts full snapshots. Persuasion
  runs client-side; scores ride the request.
- **Server-authoritative wedding**: `MarriageBarterHandler` (786 loc) — authorize (conversation
  presence gate, 15-min lifetime) → request → validate participants/clans/eligibility → apply
  barterables → no-rollback-after-mutation discipline. `MarriageAction` client-side is blocked;
  E2E-covered (`RomanceMarriageBarterSyncTests`, `LordBarterSyncTests`).
- **Protections**: NPC marriage cannot poach player-clan heroes (`RomanceNpcMarriagePatches`);
  player↔player romance rejected; `MarriageOfferCampaignBehavior` (random offers) disabled.

## Why marriage was "so buggy" — two root causes, proven from the server journal

**58 marriage-barter rejections since 2026-08-10. Zero successful marriages.** Every attempt by
every player failed. Two rejection loops account for all of them:

### Bug A — settlement-menu talks are invisible to the conversation gate (fixed)
`Aug 13 02:30 (peer 0), 03:41 (peer 1): "The marriage conversation is no longer active." ×~30`

The marriage authorization in Location context required a tracked location-conversation
engagement. Engagements are only recorded by the agent-interaction acquire patch
(`MissionConversationLogic.OnAgentInteraction`) — walking up to the lord in the scene. The
settlement menu's "Talk" shortcut starts the conversation **without** that interaction, so no
engagement ever existed and every authorization was refused, forever, with the player stuck
clicking a dead propose button. **Fix:** with no tracked engagement, the server now verifies
presence directly — the player's party and the counterparty must be in the same settlement and
the claimed location must belong to that settlement's `LocationComplex`. (The engagement check
still wins when present; the tracker's real job is NPC contention, not marriage security.)

### Bug B — arranged marriages could never be agreed (fixed)
`Aug 14 03:09 (peer 0): "The arranged marriage has not been agreed by both clans." ×~10`

The barter requires the couple's romance state to be `MatchMadeByFamily` server-side. That
state is set by `ChangeRomanticStateAction.Apply(clanMember, otherClanHero, …)` — between two
NON-player heroes. The client patch only routed changes where the player hero was one of the
couple and **silently dropped everything else**, so the server never learned the clans agreed:
guaranteed rejection loop for every marriage arranged for a brother/son/daughter. **Fix:** the
client now routes couple = (own non-player clan member, outside hero) with the clan member's id;
the server validates it through the new `RomanceAuthority.TryValidateArrangedStateChange`
(restricted to `MatchMadeByFamily` only, full eligibility checks) and applies it.

Also fixed: authorization refusals were silent toward the client (only the later request bounced
with a reason). The server now sends the rejection reason immediately.

## Why spouses "do whatever they want"

Four stacked causes, in order of impact:

1. **Until now, nobody was actually married through co-op** (zero successes above). Spouses
   people do have came from the save's history or NPC↔NPC server marriages — heroes the players
   never gained routed control surfaces for.
2. **Post-marriage placement is native server AI.** `MarriageAction` makes the clan-switching
   spouse a fugitive; the server's `TeleportationCampaignBehavior` then walks them to
   settlements/parties over days. From a client this looks like the spouse wandering on its own
   — it is native behavior, invisible-but-authoritative on the server.
3. **The "join my party" conversation surface has no route.** `AddHeroToPartyAction` is blocked
   client-side (correctly), with typed routes existing only for companion flows
   (`CompanionRolesHandler`, `HireCompanionHandler`) and the settlement-menu "take hero to
   party" (`ExecuteTroopActionHandler`). Family/clan-member dialog consequences that call it die
   silently — the spouse says yes and nothing happens.
4. **Clan-management routes exist but are partial.** Routed and working: clan-screen party
   create/change-leader/disband (`ClanPartiesVMPatches`), governor assignment
   (`ChangeGovernorAction` sync), party-screen roster Done (`PartyScreenLogicPatches`), menu
   take-to-party. Neutered: `PartyRolesCampaignBehavior` bookkeeping for player clans (roles
   like surgeon/scout stay assignable via synced party fields but the native bookkeeping events
   are stripped). Server-only: settlement re-placement decisions
   (`CanHeroMoveToAnotherSettlement` is forced false for player clans, so spouses are NOT
   shuffled between settlements by that behavior — but teleport-based travel still moves them).

**Practical control today:** clan screen (create a party for the spouse, change leaders,
disband), settlement menu take-to-party, governor assignment. **Known gap (future route):**
conversation-driven "join me"/"wait here" for clan members — needs a typed request like the
companion flows. That is the remaining piece of "controllable spouses."

## Verification

- Build green; new E2E tests: `MarriageBarterAuthorization_MenuTalkLocationContext_…`
  (menu-talk presence fallback accepts a valid proposal) and
  `ArrangedRomanceStateChange_OwnClanMember_RoutesToServer` (arranged promise reaches the
  server). CI is the test gate.
- **Owed live smoke before stable:** one real in-game marriage — personal (via settlement-menu
  talk, the previously-broken path) and one arranged for a clan member — plus the wire is
  version-locked (the romance request message gained a field), so client and server must deploy
  in lockstep as usual.
