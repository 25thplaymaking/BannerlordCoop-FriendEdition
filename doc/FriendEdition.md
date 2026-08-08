# Bannerlord Coop Friend Edition

## Objective and distribution scope

This branch carries private-community fixes on top of upstream BannerlordCoop while keeping the changes
close enough to upstream to adopt and test existing pull requests directly.

The repository is source-available, not OSI open source. The current `LICENSE` restricts copying,
modifying, and distribution without prior written permission. On 2026-08-07 the private-server operator
reported that the maintainers granted permission for this derivative and its three-member private test
distribution. That authorization is not treated as permission to publish the fork or binaries publicly.

## Current candidate

The current local candidate, `2026-08-08-prfix1` at commit `ace3edd4a84951a84e67d566a41868493cec8689`,
is newer than the live `2026-08-07-bd2` server build. It integrates upstream
PRs #2751, #2755, #2756, #2757, and #2823 plus the Friend Edition captivity and village-flow fixes below.
It has been built and tested locally, but has deliberately not been copied to, restarted on, or otherwise
applied to the live server while players are using it. Client and server assemblies must be upgraded as a
matched set when a maintenance window is available, and the upgraded server should start on a new save.

The private candidate kit is `BannerlordCoop-FriendEdition-2026-08-08-prfix1-TestKit-v3.0.0.zip`,
SHA-256 `f9e2c46bc6acdb478914c5d58b37cf835ca1e3b6b50148bc04c71bb94e806e76`. Its bundled Coop archive is
7,467,619 bytes compressed and 29,411,921 bytes installed. The obsolete Workshop Coop item's reported
6.08 GB is not required Friend Edition content and is excluded.

## Request status

| Request | Candidate resolution | Remaining live validation |
| --- | --- | --- |
| Players travel as one party | A consensual player-to-player proposal creates a synchronized, kingdom-free army attachment led by the proposer. It has no cohesion decay and is rediscovered after save/load. PR #2755's gathering-army join replication is also included. | Two-player movement, leave, reconnect, and save/restart. |
| Surrender/captivity loop | PR #2756 keeps Surrender retryable until a map event exists. PR #2755 releases the conversation hold when capture begins. The server now resolves the correct captor, starts captivity, validates and charges ransom only after a successful release, and persists a 48-hour party-scoped safe-conduct period in both directions. Safe conduct prevents an immediate recapture loop without declaring global kingdom peace. | Lose and surrender in a live field battle, verify the captivity screen, pay ransom, and verify the captor cannot immediately re-engage either party for 48 in-game hours. |
| Join an ongoing conflict / nearby reinforcements | PR #2756 opens the server-authoritative AI join window before announcing a battle and adds eligible nearby AI parties to the map event. A rejected raid join now restores the encounter menu and remains retryable. | Join both sides of an existing field battle and repeat while another player is in a menu or conversation. Confirm only eligible nearby parties join during the configured window. |
| Comrade army or roster missing after deployment | PR #2757 is included: battle sizing uses whole replicated sides, allocations add up across owners, each player receives a troop reservation, empty-team deployment can finish, joining clients do not simulate the server-owned battle locally, and the deployment recovery restores mission flags and agent wake-up. | Enter field and siege battles with two independent parties and with a travel group; verify both players and both rosters appear. |
| Retreat from battle | PR #2751 is included. Retreat is server-authoritative, removes the retreating party and attached parties from the event, replicates the removals, and tears down each affected player's menu state. | Retreat as an independent party and as a travel-group leader/member. |
| Village raid attacks first and then requires a second raid command | Resistance victory now finalizes the combat event, preserves partial village damage, creates a new authoritative raid event, rejoins the attackers, and moves clients directly into the raid flow. The headless server no longer depends on a campaign visual when creating that raid. | Win village resistance with all three clients and confirm raiding continues automatically without a second combat. |
| Demand goods / force recruits stops at Continue | The client now runs the vanilla UI/finalization tail while the authoritative server suppresses duplicate reward application. | Exercise both successful paths with Coop-only, then with RBM, Improved Garrisons, and Diplomacy enabled. |
| Duplicate bandit spawns | The earlier deployment had two Bannerlord processes loading and autosaving one world. The obsolete service was disabled, leaving one server process. Upstream PR #2787 in the baseline also makes delayed bandit attack references safe. | Observe party counts for several in-game days and record party IDs/timestamps if duplication recurs. |
| Missing hideouts | Upstream PR #2657 is present in the baseline. | Confirm visibility and interaction on the new save; upstream issue #2576 still tracks incomplete hideout interactions. |
| Birth & Death | Aging and pregnancy campaign behaviors register only on the authoritative server, while existing hero/family synchronization carries their results to clients. Player-controlled heroes are protected from natural old-age death because vanilla heir selection assumes a single local `Hero.MainHero`. The optional TaleWorlds `BirthAndDeath` module stays disabled because it provides UI/options and is not dedicated-server compatible. | A long campaign-time soak for NPC aging/death and a three-client conception, birth, reconnect, and restart observation. Player education and heir succession remain intentionally unsupported. |

## Upstream code integrated

- PR #2751: six retreat commits through `e345bb912`.
- PR #2755: captivity hold release `39775a083` and gathering-army replication `3f70e4662`.
- PR #2756: surrender retry `f26f43dac` and reinforcement commits `e47dde772`, `cdb9c2d43`.
- PR #2757: all twelve deployment/troop-supply commits from `14849395c` through `106c4a5c5`.
- PR #2823: exact test-only flake fix `86f5aa2b6`; it relaxes an over-specific surrender assertion and connects the slow-raid test peer through the normal test environment.

Merge-only synchronization commits at the tips of #2751, #2755, #2756, and #2757 were not required because
this branch already starts from the newer `60bf5cd` development baseline.

## Automated verification of the current candidate

- Full Release solution build: 0 errors. The 1,055 warnings are existing analyzer/compiler warnings.
- Full `GameInterface.Tests`: 816 passed, 11 intentional “Need regeneration” skips, 0 failed.
- Broad village and captivity E2E sweep: 96 passed, 0 failed.
- PR-focused retreat, surrender, deployment, and troop-supply E2E sweep: 67 passed, 0 failed.
- Complete surrender E2E class after adding the missing-map-event retry: 11 passed, 0 failed.
- Focused Birth & Death and army-registry tests: 9 passed, 0 failed.
- Diff whitespace validation: passed.

The complete E2E corpus was not run for this candidate. The relevant feature classes and their broader
village/captivity dependencies were run using the standalone in-process xUnit runner because this Windows
machine's testhost loopback connection is broken. Docker was unavailable because WSL2 virtualization is
disabled. The preceding `bd2` build previously passed the full deterministic sharded E2E run and isolated
fresh-save/restart canaries; those historical results are not being presented as results for this newer
candidate.

## Live server state

The community server remains on Friend Edition build `2026-08-07-bd2`, based on
`60bf5cd2e0b6557112713eb78398650db79634f8` plus the earlier Friend Edition working tree. It is serving the
fresh save `friendeditionbd1` with `difficulty.birthAndDeath=true`. Nothing in the current candidate work
stopped or restarted that server.

The immediately pre-`bd2` rollback snapshot is
`/home/bishop/bannerlord-coop/backups/pre-friend-bd2-20260807T225821Z`. The paired historical client archive
is `BannerlordCoop-FriendEdition-2026-08-07-bd2.7z`, SHA-256
`92b4dc0df70b6ed85202cc235fa800e2188f534ddea720839df1d6d948a0f719`.

## Player travel-group specification

The proposer sees `Let us travel together. Follow my banner.` in the existing player-party conversation.
The responder receives the normal accept/decline proposal. The option is disabled for hostile parties and
unavailable when either party is inactive, in an encounter, besieging, inside a settlement, already
attached, already in an army, missing a leader, or using a different navigation mode.

Acceptance is server-authoritative and revalidates both parties. It creates a Bannerlord `Army` without a
kingdom, assigns the proposer as leader, adds the responder as an attached party, and uses the existing army
network messages to replicate the graph. Declining changes no campaign state, and existing army membership
is never replaced implicitly.

## Candidate live-session checklist

1. Schedule a maintenance window, take checksummed module/save backups, and confirm exactly one Bannerlord
   server process owns the save and UDP port.
2. Install the matched candidate assemblies on server and clients and start a new save. Do not reuse the
   `bd2` save as the candidate acceptance save.
3. Verify all three clients report identical candidate DLL hashes and load order before connecting.
4. Surrender a battle, confirm captivity starts, pay ransom, and verify party-scoped safe conduct prevents
   immediate recapture without changing kingdom diplomacy.
5. Win village resistance and confirm the same action advances directly into raiding. Complete demand-goods
   and force-recruit flows without a stuck Continue button.
6. Join an ongoing battle from each side. Repeat with a nearby eligible AI party and while another player is
   in a menu/conversation; verify reinforcement membership is consistent on all peers.
7. Enter field and siege battles as independent parties and as a travel group. Confirm all player agents and
   rosters survive deployment, and test leader/member retreat.
8. Save, restart, and reconnect all players. Confirm travel-group state, captivity safe conduct, family data,
   and ongoing campaign state persist.
9. Run a longer Birth & Death soak and observe hideout/bandit-party behavior for several in-game days.

## Dedicated-server deployment constraints

The separately distributed server verifies exact hashes for its paired Coop assemblies. The guarded,
source-only compatibility tool in `tools/DedicatedServerCompatibilityPatcher` makes the authorized
server-side compatibility change reproducible. Never commit or redistribute a patched
`DedicatedServer.Core.dll`.

For the later maintenance-window deployment:

- overlay module assemblies into both client and server module-bin directories without deleting server-only
  files;
- preserve the server's empty `SubModules` manifest instead of replacing it with the client manifest;
- retain the official server's `0Harmony.dll`;
- patch both physical copies of `DedicatedServer.Core.dll` only after resolving symlinks;
- take checksummed module and save backups before restart;
- roll back unless the service stays active, UDP 4200 binds, and multiple pulses appear.

Private-fork hash warnings are expected. Process exit, restart growth, a missing port bind, or missing pulses
are not.

## Upstream watch list

- PRs #2751, #2755, #2756, and #2757 are integrated locally and feature-tested; their upstream branches
  remain the provenance for these changes.
- PR #2823 is integrated exactly and is test-only; it does not change runtime behavior.
- PR #2787 and PR #2657 are already part of the `60bf5cd` baseline.
- PR #2758 (party-state robustness) and draft PR #2768 (party disbanding) are not included because their
  upstream validation is not yet clean.
- Issues #2243, #2385, #2415, #2473, #2576, #2766, #2771, #2783, #2812, and #2817 remain useful
  reproduction anchors.
