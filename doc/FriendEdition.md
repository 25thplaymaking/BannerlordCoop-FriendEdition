# Bannerlord Coop Friend Edition

## Objective and distribution scope

This branch carries private-community fixes on top of upstream BannerlordCoop while keeping the changes
close enough to upstream to adopt and test existing pull requests directly.

The repository is source-available, not OSI open source. The current `LICENSE` restricts copying,
modifying, and distribution without prior written permission. On 2026-08-07 the private-server operator
reported that the maintainers granted permission for this derivative and its three-member private test
distribution. That authorization is not treated as permission to publish the fork or binaries publicly.

## Current deployed build

Friend Edition `2026-08-08-mountfix1`, built from source commit
`3c130aacf06d845dd8b79c44979a38ed6bf8026f`, was deployed to the private server at
`205.209.116.114:4200` on 2026-08-08. It advances the fork to official nightly commit
`91af3abe21bc5ffd66662a97e12e56676ff1ff4c`, retains the previously integrated gameplay PRs and Friend
Edition fixes, retains the narrowly validated crash-path changes from PRs #2758 and #2846, and closes the
remaining riderless-mount transitions exposed by the latest client dump. The server loaded the existing
`friendeditionprfix1` campaign; no new world was created. `birthAndDeath=true` and the 24-hour player
battle-AI join window remain effective.

The matched three-player test kit is
`BannerlordCoop-FriendEdition-2026-08-08-mountfix1-TestKit-v3.2.0.zip`, 7,314,985 bytes, SHA-256
`e7b5cfd0ae244e70f8e2e55d3cc488692b4057a0003c08d44c34c762b5e61fc6`. Its bundled Coop archive is
7,294,773 bytes compressed and 29,423,697 bytes installed. The obsolete Workshop Coop item's reported
6.08 GB is not required Friend Edition content and is excluded.

## Request status

| Request | Deployed resolution | Remaining live validation |
| --- | --- | --- |
| Players travel as one party | A consensual player-to-player proposal creates a synchronized, kingdom-free army attachment led by the proposer. It has no cohesion decay and is rediscovered after save/load. PR #2755's gathering-army join replication is also included. | Two-player movement, leave, reconnect, and save/restart. |
| Surrender/captivity loop | PR #2756 keeps Surrender retryable until a map event exists. PR #2755 releases the conversation hold when capture begins. The server now resolves the correct captor, starts captivity, validates and charges ransom only after a successful release, and persists a 48-hour party-scoped safe-conduct period in both directions. Safe conduct prevents an immediate recapture loop without declaring global kingdom peace. | Lose and surrender in a live field battle, verify the captivity screen, pay ransom, and verify the captor cannot immediately re-engage either party for 48 in-game hours. |
| Join an ongoing conflict / nearby reinforcements | PR #2756 opens the server-authoritative AI join window before announcing a battle. Crashfix1 replaces its dedicated-server-invalid vanilla selector, which dereferenced `MobileParty.MainParty`/`PlayerEncounter`, with authoritative map-event selection. Scans are rate-limited and one malformed battle can no longer generate an exception every campaign tick. A rejected raid join restores the encounter menu and remains retryable. | Join both sides of an existing field battle and repeat while another player is in a menu or conversation. Confirm only eligible nearby parties join during the configured window. |
| Client crashes during battle | The isolated first commit from PR #2758 discards unchanged synchronized writes before the client error/log path, eliminating the earlier `Settlement.IsVisible` flood. WinDbg resolved the latest dump's managed exception to `HumanAIComponent.FindClosestMountAvailable()`: vanilla Bannerlord dereferenced a missing `CommonAIComponent` on an active riderless horse. The PR #2389 rider-death repair is retained and mountfix1 extends the invariant across synthetic turns, remote dismounts, horse switches, and a final prefix guard before every vanilla mount search. The server-only encounter-close guard from draft PR #2846 is also retained. | Exercise cavalry battles with remote riders, dismounts, horse changes, rider deaths, and loose horses. Preserve a new dump if a crash recurs so its stack can be compared with this fixed path. |
| Comrade army or roster missing after deployment | PR #2757 is included: battle sizing uses whole replicated sides, allocations add up across owners, each player receives a troop reservation, empty-team deployment can finish, joining clients do not simulate the server-owned battle locally, and the deployment recovery restores mission flags and agent wake-up. | Enter field and siege battles with two independent parties and with a travel group; verify both players and both rosters appear. |
| Retreat from battle | PR #2751 is included. Retreat is server-authoritative, removes the retreating party and attached parties from the event, replicates the removals, and tears down each affected player's menu state. | Retreat as an independent party and as a travel-group leader/member. |
| Village raid attacks first and then requires a second raid command | Resistance victory now finalizes the combat event, preserves partial village damage, creates a new authoritative raid event, rejoins the attackers, and moves clients directly into the raid flow. The headless server no longer depends on a campaign visual when creating that raid. | Win village resistance with all three clients and confirm raiding continues automatically without a second combat. |
| Demand goods / force recruits stops at Continue | The client now runs the vanilla UI/finalization tail while the authoritative server suppresses duplicate reward application. | Exercise both successful paths with Coop-only, then with RBM, Improved Garrisons, and Diplomacy enabled. |
| Duplicate bandit spawns | The earlier deployment had two Bannerlord processes loading and autosaving one world. The obsolete service was disabled, leaving one server process. Upstream PR #2787 in the baseline also makes delayed bandit attack references safe. | Observe party counts for several in-game days and record party IDs/timestamps if duplication recurs. |
| Missing hideouts | Upstream PR #2657 is present in the baseline. | Confirm visibility and interaction on the preserved campaign; upstream issue #2576 still tracks incomplete hideout interactions. |
| Birth & Death | Aging and pregnancy campaign behaviors register only on the authoritative server, while existing hero/family synchronization carries their results to clients. Player-controlled heroes are protected from natural old-age death because vanilla heir selection assumes a single local `Hero.MainHero`. The optional TaleWorlds `BirthAndDeath` module stays disabled because it provides UI/options and is not dedicated-server compatible. | A long campaign-time soak for NPC aging/death and a three-client conception, birth, reconnect, and restart observation. Player education and heir succession remain intentionally unsupported. |

## Upstream code integrated

- PR #2751: six retreat commits through `e345bb912`.
- PR #2755: captivity hold release `39775a083` and gathering-army replication `3f70e4662`.
- PR #2756: surrender retry `f26f43dac` and reinforcement commits `e47dde772`, `cdb9c2d43`.
- PR #2757: all twelve deployment/troop-supply commits from `14849395c` through `106c4a5c5`.
- PR #2823: exact test-only flake fix `86f5aa2b6`; it relaxes an over-specific surrender assertion and connects the slow-raid test peer through the normal test environment.
- PR #2389: mounted-puppet death repair through merge `da6712823`; mountfix1 extends its riderless-horse AI
  invariant to controller transitions not covered by the original rider-death path.
- Official 2026-08-08 nightly `91af3abe2`, containing merged PRs #2823, #2824, and #2674.
- PR #2758: only its first runtime commit `7c48695b` was taken. It moves the generated equality/no-op guard
  ahead of the client error log. The rest of the open PR was excluded because its validation is not clean.
- Draft PR #2846: only the dedicated-server guard in `Handle_NetworkClosePvpEncounter` was reproduced. Its
  unrelated deletion of 58 tests was not taken.

Merge-only synchronization commits at the tips of #2751, #2755, #2756, and #2757 were originally omitted
because the branch already contained their changes through the newer `60bf5cd` development baseline. The
branch now also includes the official 2026-08-08 nightly baseline listed above.

## Automated verification of the deployed build

- Full Release solution build from commit `3c130aacf`: 0 errors. The 1,058 output warnings are the
  repository's existing compiler/analyzer and target-framework warnings.
- Complete `MountedPuppetMovementTests`: 69 passed, 0 failed, including patch installation, idempotent
  invariant repair, synthetic-turn, authoritative dismount, and old-horse switch regressions.
- Adjacent `BattleMountIdentityTests`, `BattleDeathMirrorTests`, and `MovementTrafficTests`: 35 passed,
  0 failed.
- The earlier crashfix1 `GameInterface.Tests`, map-event, captivity, surrender, and battle-finalization
  verification remains valid because mountfix1 changes only mission mount handling.
- Client archive, outer ZIP, all 14 archive checksums, six project-DLL hashes, JSON manifest, archive
  integrity, and all four PowerShell scripts: passed.
- Live canary: the preserved campaign loaded at Spring 15, 1086 with 1,791 heroes, 1,521 parties, and 493
  settlements; it completed its first post-upgrade autosave at 20:36:04 UTC. The service stayed active with
  zero restarts, repeated pulses, and UDP 4200 bound.
- Diff whitespace validation: passed.

The complete E2E corpus was not rerun for mountfix1. Its full mounted-puppet class and adjacent
mount/death/traffic classes were run using the standalone in-process xUnit runner because this Windows
machine's testhost loopback connection is broken. The earlier broader crashfix1 and prfix1 sweeps remain
useful regression evidence but are distinguished from mountfix1's direct results above.

## Live server state

The community server is running Friend Edition build `2026-08-08-mountfix1` on the preserved save
`friendeditionprfix1`. It loaded the same Spring 15, 1086 campaign state with 1,791 heroes, 1,521 parties,
and 493 settlements, then wrote its first post-upgrade autosave successfully. Post-deployment checks found
`bannerlord-coop-seven.service` active with zero restarts, exactly one server process tree, UDP 4200 bound
on IPv4 and IPv6, repeated server pulses, and no fatal startup or mount-search exceptions.
Effective configuration logs report Birth & Death on and the 24-hour battle-AI join window. The TaleWorlds
`BirthAndDeath` module itself remains disabled as intended; live births/deaths were not observed during the
previous play session and remain a next-session soak item.

The complete pre-deployment rollback snapshot is
`/home/bishop/bannerlord-coop/backups/pre-mountfix1-20260808T202913Z`; all 994 entries in its
`SHA256SUMS.txt` were verified. It contains the exact pre-upgrade `friendeditionprfix1` save and sidecar,
module, configs, service unit, both server-core binaries, previous release metadata, and previous client
distribution. The superseded crashfix1 build was removed from active release/distribution locations only
after mountfix1 passed health checks. Private-fork module-hash warnings remain expected because both physical
server-core copies retain the authorized compatibility patch. The immutable deployment checksum manifests
and the save's cutover hash are stored under release `2026-08-08-mountfix1-3c130aa`.
The live save is intentionally mutable and changed from its verified cutover hash when that autosave
completed.

## Player travel-group specification

The proposer sees `Let us travel together. Follow my banner.` in the existing player-party conversation.
The responder receives the normal accept/decline proposal. The option is disabled for hostile parties and
unavailable when either party is inactive, in an encounter, besieging, inside a settlement, already
attached, already in an army, missing a leader, or using a different navigation mode.

Acceptance is server-authoritative and revalidates both parties. It creates a Bannerlord `Army` without a
kingdom, assigns the proposer as leader, adds the responder as an attached party, and uses the existing army
network messages to replicate the graph. Declining changes no campaign state, and existing army membership
is never replaced implicitly.

## Live-session acceptance checklist

1. All three players install the v3.2.0 test kit with `Run-Setup.cmd`, run `Run-Verify.cmd`, and confirm the
   expected DLL hashes and load order before reconnecting to the preserved `friendeditionprfix1` campaign.
2. Surrender a battle, confirm captivity starts, pay ransom, and verify party-scoped safe conduct prevents
   immediate recapture without changing kingdom diplomacy.
3. Win village resistance and confirm the same action advances directly into raiding. Complete demand-goods
   and force-recruit flows without a stuck Continue button.
4. Join an ongoing battle from each side. Repeat with a nearby eligible AI party and while another player is
   in a menu/conversation; verify reinforcement membership is consistent on all peers.
5. Enter field and siege battles as independent parties and as a travel group. Confirm all player agents and
   rosters survive deployment, and test leader/member retreat.
6. Save, restart, and reconnect all players. Confirm travel-group state, captivity safe conduct, family data,
   and ongoing campaign state persist.
7. Run a longer Birth & Death soak and observe hideout/bandit-party behavior for several in-game days.
8. In cavalry battles, test remote dismounts, horse changes, rider deaths, and loose riderless horses. Confirm
   client logs do not rapidly repeat unchanged `Settlement.IsVisible` writes. If a crash recurs, preserve the
   TaleWorlds crash report/dump instead of cancelling its generation.

## Dedicated-server deployment constraints

The separately distributed server verifies exact hashes for its paired Coop assemblies. The guarded,
source-only compatibility tool in `tools/DedicatedServerCompatibilityPatcher` makes the authorized
server-side compatibility change reproducible. Never commit or redistribute a patched
`DedicatedServer.Core.dll`.

The mountfix1 deployment followed these constraints:

- overlay module assemblies into both client and server module-bin directories without deleting server-only
  files;
- preserve the server's empty `SubModules` manifest instead of replacing it with the client manifest;
- retain the official server's `0Harmony.dll`;
- resolve and verify both physical copies of `DedicatedServer.Core.dll`; mountfix1 retained their already
  compatibility-patched bytes and backed both up rather than patching an in-use or already-patched file;
- take checksummed module and save backups before restart;
- roll back unless the service stays active, UDP 4200 binds, and multiple pulses appear.

Private-fork hash warnings are expected. Process exit, restart growth, a missing port bind, or missing pulses
are not.

## Upstream watch list

- PRs #2751, #2755, #2756, and #2757 are integrated locally and feature-tested; their upstream branches
  remain the provenance for these changes.
- PR #2823 is integrated exactly and is test-only; it does not change runtime behavior.
- PR #2389 is integrated. Mountfix1 extends its original mounted-puppet death safeguard to every observed
  transition that can leave an active riderless horse without `CommonAIComponent`.
- PR #2787 and PR #2657 are already part of the `60bf5cd` baseline.
- PR #2758's first no-op/log-flood commit is included; the remainder of that open party-state PR is not
  included because its upstream validation is not yet clean.
- Draft PR #2846 contributed only its dedicated-server encounter-close guard; its test deletions remain
  excluded and the PR should continue to be watched for a reviewed form.
- Draft PR #2768 (party disbanding) is not included.
- Issues #2243, #2385, #2415, #2473, #2576, #2766, #2771, #2783, #2812, and #2817 remain useful
  reproduction anchors.
