# Player Clan Membership Design (approved 2026-08-14)

## Goal

Let one player voluntarily join another player's tier-2-or-higher clan as a real clan member,
travel inside the clan leader's party, later request an independent clan party, or leave for their
original personal clan without being soft-locked by an offline leader. Player marriage is a
separate, consent-based option and never implicitly joins a clan.

## Native model

- Finish the existing player-party `JoinClan` proposal instead of introducing a second social UI.
- Persist clan membership on the existing `Player` registration with:
  - original personal clan id;
  - `PersonalClan`, `Embedded`, or `IndependentParty` mode;
  - whether an independent party was created by the offline safeguard.
- A player hero is always controller-owned. A mobile party is controller-owned only by its party
  leader. A clan is controller-owned only by the player in `PersonalClan` mode who leads it.
  Shared clans and embedded parties do not become multi-owner controller objects.
- `Player.MobilePartyId` continues to identify the hero's current party. Embedded registrations
  may therefore share the leader's party id while only the leader claims control of it.

## Joining

- The applicant meets the target player through the existing player-party conversation.
- `Join clan` is enabled only when the target is the clan leader, the target clan is tier 2+, the
  applicant is still in their personal clan, and neither party is hostile or in an invalid state.
- Before the request reaches the target, the applicant sees a confirmation state warning that the
  transfer is permanent even if they later leave.
- The target player accepts or declines through the existing proposal response.
- On acceptance the server:
  - transfers the applicant clan's fiefs to the target clan leader;
  - transfers the applicant hero's workshops, caravans, alleys, and gold to the clan leader;
  - moves the applicant party's troops, prisoners, and item roster into the leader's party;
  - moves the applicant hero into the leader's party and changes the hero to the target clan;
  - removes the emptied former player party;
  - replaces and broadcasts the applicant's registration as `Embedded`.
- Transferred holdings are not restored when the player leaves.

## Party authority and resources

- An embedded member does not control map movement, settlement choices, diplomacy, purchases, or
  the shared party inventory. The party leader owns those decisions.
- Each player still controls their own hero in missions and retains their own skills, perks, and XP.
- Battle results are shared naturally because embedded heroes fight from the same party and
  independent members remain in the same clan; no XP values are copied between heroes.
- While players share a clan, gold mutations resolve to the clan leader and are mirrored to joined
  player heroes for correct UI display. XP is never redirected or pooled.
- An independent clan party has its own party inventory. Clan gold remains shared.

## Independent parties and leaving

- An embedded player can select their own hero from Clan → Parties. A native inquiry offers
  `Request independent party` or `Leave clan`. An independent member can select their own party's
  disband action, which is replaced with a `Leave clan` confirmation.
- An independent-party request is sent to the connected clan leader. The server creates the party
  only after the leader approves and only when the clan has a free native party slot.
- The new party starts with the player hero at the current position. It receives no duplicated
  troops, prisoners, items, or gold.
- Leaving the clan requires no clan-leader approval. The server creates a party first when needed,
  returns the hero and party to the saved personal clan, and leaves transferred holdings behind.
- Personal clans referenced by an away player are protected from native destruction so the return
  target remains valid.
- A detached member can voluntarily rejoin the leader's party through the same player-party clan
  proposal. It never happens automatically.

## Offline safeguard

- When a clan leader disconnects, every member embedded in that leader's party is moved into a
  hero-only independent party before the leader party is parked.
- Emergency creation ignores the clan party-slot cap. Native party creation remains unavailable
  while the clan is at or above its normal cap.
- When the leader returns, connected emergency-detached members receive a carrier-pigeon quick
  notification. Offline members receive it when they next enter the campaign.
- Rejoining the leader party remains voluntary.

## Player marriage

- Add `Propose marriage` to the existing player-party conversation.
- The other player must explicitly accept. Both heroes must be alive, adult, unmarried, and pass
  the native marriage suitability model.
- Acceptance sets the native spouse/romance state and publishes the normal marriage event without
  moving either player between clans. Clan joining remains a separate action.
- Existing server-authoritative pregnancy, birth, death, and succession paths continue to operate
  from the spouse links.

## Tests and release boundary

- Add only focused unit tests required for registration ownership, save compatibility, and pure
  eligibility rules.
- Add E2E coverage for join/asset transfer/embed, approved separation, voluntary leave, leader
  disconnect/reconnect recovery, and consensual player marriage.
- Do not add SHA-256 gates, a multi-owner registry, a new UI framework, or speculative abstractions.
- PR #15 remains draft until the complete diff is reviewed and required tests pass. Merge, server
  deployment, and launcher update happen only after that review.
