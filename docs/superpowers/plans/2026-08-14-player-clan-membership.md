# Player Clan Membership Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add native, persisted player clan membership with party embedding, approved separation,
voluntary departure, offline recovery, and consent-based player marriage.

**Architecture:** Extend the existing `Player` registration and unfinished player-party `JoinClan`
proposal. Keep game mutations in one GameInterface service, use existing registration-update
replication, and add thin Coop.Core request/prompt handlers for actions requiring another player's
approval.

**Tech Stack:** C# 10, .NET 6/netstandard2.0, Harmony, protobuf-net, xUnit, Bannerlord campaign
actions and existing BannerlordCoop E2E harness.

**Spec:** `docs/superpowers/specs/2026-08-14-player-clan-membership-design.md`

## Global Constraints

- Target clan tier is at least 2.
- Clan membership and marriage are independent and voluntary.
- Holdings transferred on clan join are never restored on leave.
- Party inventory is shared only while embedded; clan gold is shared in both joined modes.
- XP remains per hero.
- Emergency separation may exceed the party cap; voluntary separation may not.
- Rejoining after the leader returns is voluntary.
- No SHA-256 gate, multi-owner registry, UIExtender surface, or speculative framework.
- Add only behavior-bearing unit tests and the required E2E flows.

---

### Task 1: Persist membership and assign controller ownership

**Files:**
- Create: `source/GameInterface/Services/Players/Data/PlayerClanMembershipMode.cs`
- Modify: `source/GameInterface/Services/Players/Data/Player.cs`
- Modify: `source/GameInterface/Services/Players/PlayerManager.cs`
- Modify: `source/GameInterface/Services/Players/PlayerPartyRestorer.cs`
- Test: `source/GameInterface.Tests/Services/Players/PlayerManagerTests.cs`
- Test: `source/GameInterface.Tests/Services/Players/PlayerPartyRestorerTests.cs`
- Test: `source/Coop.Tests/Server/Services/Save/SaveLoadCoopSessionTests.cs`

**Interfaces:**
- Produces: `PlayerClanMembershipMode`, `Player.PersonalClanId`,
  `Player.ClanMembershipMode`, `Player.EmergencyDetached`, and conditional party/clan claims.
- Preserves: the existing five-argument `Player` constructor behavior for old saves and callers.

- [x] **Step 1: Write failing ownership and round-trip tests**

Add focused assertions equivalent to:

```csharp
var embedded = new Player("member", heroId, leaderPartyId, joinedClanId, characterId,
    personalClanId, PlayerClanMembershipMode.Embedded, false);
Assert.True(manager.AddPlayer(embedded));
Assert.True(manager.Contains(hero));
Assert.False(manager.Contains(leaderParty));
Assert.False(manager.Contains(joinedClan));
```

Round-trip the three new protobuf members and prove an old five-field registration normalizes to
`PersonalClan`, `PersonalClanId == ClanId`, and `EmergencyDetached == false`.

- [x] **Step 2: Run the focused tests and verify the expected failures**

```powershell
& .\source\GameInterface.Tests\bin\Release\net6.0\xunit.console.exe .\source\GameInterface.Tests\bin\Release\net6.0\GameInterface.Tests.dll -noshadow -parallel none -class GameInterface.Tests.Services.Players.PlayerManagerTests
& .\source\Coop.Tests\bin\Release\net6.0\xunit.console.exe .\source\Coop.Tests\bin\Release\net6.0\Coop.Tests.dll -noshadow -parallel none -class Coop.Tests.Server.Services.Save.SaveLoadCoopSessionTests
```

Expected: failures because the membership fields and conditional ownership do not exist.

- [x] **Step 3: Implement the minimum persisted state and ownership rules**

Use protobuf member numbers 6-8 and optional constructor parameters:

```csharp
public Player(string controllerId, string heroId, string mobilePartyId, string clanId,
    string characterObjectId, string personalClanId = null,
    PlayerClanMembershipMode clanMembershipMode = PlayerClanMembershipMode.PersonalClan,
    bool emergencyDetached = false)
```

`PlayerManager` always claims the hero, claims the party unless mode is `Embedded`, and claims the
clan only in `PersonalClan`. `ReplacePlayer` removes/adds claims when either id or claim state
changes. `PlayerPartyRestorer` accepts a valid embedded party containing the hero without promoting
that hero to party leader; if the party is gone it returns an `IndependentParty` recovery
registration preserving `PersonalClanId`.

- [x] **Step 4: Build and rerun the focused tests**

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build source\GameInterface.Tests\GameInterface.Tests.csproj -c Release --no-restore
& 'C:\Program Files\dotnet\dotnet.exe' build source\Coop.Tests\Coop.Tests.csproj -c Release --no-restore
```

Run the two direct xUnit commands from Step 2 plus `PlayerPartyRestorerTests`; require zero failures.

- [x] **Step 5: Commit**

```powershell
git add source/GameInterface/Services/Players source/GameInterface.Tests/Services/Players source/Coop.Tests/Server/Services/Save
git commit -m "feat: persist player clan membership"
```

### Task 2: Complete clan join and player marriage proposals

**Files:**
- Create: `source/GameInterface/Services/Players/PlayerClanMembershipRules.cs`
- Create: `source/GameInterface/Services/Players/PlayerClanMembershipService.cs`
- Create: `source/GameInterface/Services/Players/Messages/PlayerRegistrationChanged.cs`
- Modify: `source/GameInterface/Services/MapEvents/PlayerPartyInteractions/PlayerPartyInteractionTypes.cs`
- Modify: `source/GameInterface/Services/MapEvents/PlayerPartyInteractions/PlayerPartyInteractionSession.cs`
- Modify: `source/GameInterface/Services/MapEvents/PlayerPartyInteractions/PlayerPartyInteractionHandler.cs`
- Modify: `source/GameInterface/Services/MapEvents/PlayerPartyInteractions/PlayerPartyInteractionDialogState.cs`
- Modify: `source/GameInterface/Services/MapEvents/PlayerPartyInteractions/PlayerPartyInteractionCampaignBehavior.cs`
- Modify: `source/GameInterface/Services/MapEvents/PlayerPartyInteractions/PlayerPartyInteractionOutcomeHandler.cs`
- Test: `source/E2E.Tests/Services/Players/PlayerClanMembershipFlowTests.cs`

**Interfaces:**
- Produces: `IPlayerClanMembershipService.TryJoin`, `TryEmbed`, `TrySeparate`, and `TryLeave`.
- Emits: `PlayerRegistrationChanged` after each successful registration replacement.
- Consumes: existing player-party proposal, native campaign actions, object registration, and party
  lifetime replication.

- [x] **Step 1: Write failing E2E tests for join and marriage**

Drive the real player-party dialog messages. Assert that a tier-1 target cannot receive a join
proposal, the applicant must confirm the permanent transfer warning, and acceptance produces:

```csharp
Assert.Equal(targetClan, applicantHero.Clan);
Assert.Equal(targetParty, applicantHero.PartyBelongedTo);
Assert.Equal(PlayerClanMembershipMode.Embedded, updated.ClanMembershipMode);
Assert.False(serverPlayers.Contains(applicantOldParty));
Assert.Equal(targetLeader, workshop.Owner);
Assert.Equal(targetLeader, settlement.OwnerClan.Leader);
```

For marriage, accept the proposal and assert reciprocal spouse links while both original clan
references remain unchanged.

- [x] **Step 2: Run the new E2E class and verify the expected failures**

```powershell
& .\source\E2E.Tests\bin\Release\net6.0\xunit.console.exe .\source\E2E.Tests\bin\Release\net6.0\E2E.Tests.dll -noshadow -parallel none -class E2E.Tests.Services.Players.PlayerClanMembershipFlowTests
```

Expected: failure because join is disabled and player marriage is absent.

- [x] **Step 3: Implement join eligibility, confirmation, transfer, and registration replacement**

The service validates server state again at acceptance, transfers fiefs/workshops/caravans/alleys,
then moves party rosters and the applicant hero before removing the empty party. It replaces the
registration with:

```csharp
new Player(current.ControllerId, current.HeroId, leaderPartyId, targetClanId,
    current.CharacterObjectId, current.PersonalClanId,
    PlayerClanMembershipMode.Embedded, emergencyDetached: false)
```

For a member already in the target clan, the same accepted proposal only re-embeds the member and
does not repeat asset transfer.

- [x] **Step 4: Implement consent-based player marriage without clan movement**

Validate both registered heroes through the native marriage suitability model, set reciprocal
`Spouse` references, publish `OnBeforeHeroesMarried`, end both courtships, and apply native
`RomanceLevelEnum.Marriage`. Do not call native `MarriageAction`, because it moves one spouse's clan.

- [x] **Step 5: Build and rerun the E2E class**

Build `source/E2E.Tests/E2E.Tests.csproj` Release, then run the direct xUnit command from Step 2;
require zero failures.

- [x] **Step 6: Commit**

```powershell
git add source/GameInterface/Services/MapEvents/PlayerPartyInteractions source/GameInterface/Services/Players source/E2E.Tests/Services/Players
git commit -m "feat: let players join clans and marry"
```

### Task 3: Add approved separation, leaving, and shared gold

**Files:**
- Create: `source/GameInterface/Services/Players/Messages/PlayerClanMembershipMessages.cs`
- Create: `source/GameInterface/Services/Players/Patches/PlayerClanGoldPatches.cs`
- Create: `source/GameInterface/Services/Clans/Patches/PersonalClanProtectionPatches.cs`
- Modify: `source/GameInterface/Services/Clans/Patches/ClanPartiesVMPatches.cs`
- Modify: `source/GameInterface/Services/Clans/Handlers/ClanPartiesVMHandler.cs`
- Create: `source/Coop.Core/Common/Players/Messages/PlayerClanMembershipNetworkMessages.cs`
- Create: `source/Coop.Core/Client/Services/Players/Handlers/ClientPlayerClanMembershipHandler.cs`
- Create: `source/Coop.Core/Server/Services/Players/Handlers/ServerPlayerClanMembershipHandler.cs`
- Test: `source/GameInterface.Tests/Services/Players/PlayerClanMembershipRulesTests.cs`
- Test: `source/E2E.Tests/Services/Players/PlayerClanMembershipFlowTests.cs`

**Interfaces:**
- Client request: authenticated originating peer plus `RequestIndependentParty` or `LeaveClan`.
- Leader decision: request id plus approve/decline; server revalidates requester and leader.
- Registration changes continue through `PlayerRegistrationChanged`.

- [x] **Step 1: Write failing rule and E2E tests**

Cover only these breaks: voluntary separation over cap is rejected; approved separation creates a
hero-only party; leave needs no leader response and restores `PersonalClanId`; transferred holdings
stay with the joined clan; joined gold resolves to the clan leader.

- [x] **Step 2: Run the focused tests and verify the expected failures**

Run direct xUnit for `PlayerClanMembershipRulesTests` and `PlayerClanMembershipFlowTests`.

- [x] **Step 3: Implement native Clan → Parties entry points and approval messages**

Allow only the local embedded player hero through `GetNewPartyLeaderCandidates`. Intercept its
creation action with a TaleWorlds inquiry that sends either `RequestIndependentParty` or
`LeaveClan`. Permit the local independent member's own party to invoke disband, but replace that
action with a leave confirmation. The server resolves controller identity from `NetPeer`; client
payloads never choose another controller.

- [x] **Step 4: Implement separate/leave and gold ownership**

`TrySeparate` uses `IPlayerPartyRestorer` with a null party id to create and register a hero-only
party, then replaces the player registration. `TryLeave` separates first when embedded, changes the
hero and party back to `PersonalClanId`, clears shared gold from the leaving hero, and preserves all
transferred assets. Gold changes for joined heroes are redirected to the current clan leader and
mirrored to joined player heroes after the authoritative change.

- [x] **Step 5: Protect dormant personal clans**

Prefix native clan destruction and return `false` only when the target id is the `PersonalClanId`
of a registered player currently away from that clan. Normal clan destruction is unchanged.

- [x] **Step 6: Build, run the focused tests, and commit**

```powershell
git add source/GameInterface/Services/Players source/GameInterface/Services/Clans source/Coop.Core/Common/Players source/Coop.Core/Client/Services/Players source/Coop.Core/Server/Services/Players source/GameInterface.Tests/Services/Players source/E2E.Tests/Services/Players
git commit -m "feat: manage joined player parties"
```

### Task 4: Guarantee offline recovery and voluntary rejoin

**Files:**
- Modify: `source/Coop.Core/Server/Services/Players/Handlers/PlayerPartyVisibilityHandler.cs`
- Modify: `source/Coop.Core/Server/Services/Players/Handlers/ServerPlayerClanMembershipHandler.cs`
- Modify: `source/Coop.Core/Client/Services/Players/Handlers/ClientPlayerClanMembershipHandler.cs`
- Test: `source/Coop.Tests/Server/Services/Players/PlayerPartyVisibilityHandlerTests.cs`
- Test: `source/E2E.Tests/Services/Players/PlayerClanMembershipFlowTests.cs`

**Interfaces:**
- Consumes: `PlayerDisconnected`, `PlayerCampaignEntered`, and membership service operations.
- Produces: `IndependentParty` with `EmergencyDetached == true` and a one-time carrier-pigeon
  notification after leader return.

- [x] **Step 1: Write failing disconnect/reconnect tests**

Assert that an embedded member disconnect does not park the leader party; a leader disconnect
separates embedded members before parking; emergency creation succeeds above the native cap; a
leader reconnect does not re-embed anyone; and the notification is delivered once.

- [x] **Step 2: Run the focused tests and verify the expected failures**

Run direct xUnit for `PlayerPartyVisibilityHandlerTests` and `PlayerClanMembershipFlowTests`.

- [x] **Step 3: Implement disconnect ordering and notification**

On member disconnect, clear only the peer. On leader disconnect, call `TrySeparate(member, true)`
for each embedded registration sharing the leader party, then park the leader party. On campaign
entry, separate an embedded member whose leader is still offline. When the leader is connected,
send the emergency-detached member a quick notification reading:

```text
A carrier pigeon arrives: your clan leader has returned. Rejoin their party when you are ready.
```

Clear `EmergencyDetached` only after delivery; never auto-embed.

- [x] **Step 4: Build, rerun the focused tests, and commit**

```powershell
git add source/Coop.Core/Server/Services/Players source/Coop.Core/Client/Services/Players source/Coop.Tests/Server/Services/Players source/E2E.Tests/Services/Players
git commit -m "fix: prevent joined players from being stranded"
```

### Task 5: Review the complete PR and release only the reviewed result

**Files:**
- Modify: `docs/coop-next-update-bugsweep.md`
- Modify: `STATUS.md`
- Modify: PR #15 description and status.

**Interfaces:**
- Consumes: all commits from `origin/development..HEAD`.
- Produces: reviewed draft PR, required green verification, then merge/deployment/launcher update.

- [x] **Step 1: Run required verification**

Build `source/Coop.sln` Release. Run the focused CrashReporter, Diplomacy, PatchTest, membership
unit, visibility, save, and membership E2E classes through direct xUnit. Do not add unrelated test
suites.

- [x] **Step 2: Review the full PR diff**

Review `origin/development...HEAD` for authority bypass, object-registration conflicts, save
compatibility, roster duplication/loss, clan-leader validation, disconnect ordering, and player
marriage clan movement. Fix every confirmed defect and rerun the affected test.

- [x] **Step 3: Update owned docs and PR metadata**

Record exact behavior and test counts in `STATUS.md` and `docs/coop-next-update-bugsweep.md`. Update
PR #15 with the final scope and validation; mark it ready only after the review is clean.

- [x] **Step 4: Merge only the reviewed PR**

Confirm PR checks and head commit, merge PR #15 into `development`, and verify the merged branch
builds before release operations.

- [x] **Step 5: Update launcher delivery and deploy the server**

Use the repository's existing launcher/client release workflow and documented server deployment
path. Do not change launcher source unless its existing feed cannot deliver this build. Preserve the
configured campaign save, restart headlessly, and verify the server reaches serving state with the
new build and no startup script errors.

**Execution result:** PR #15 merged as `da18f9b95` after workflow `31818854020` passed every
required job. Workflow `31819338262` published stable/nightly client `2026.08.14.1630`. The paired
server deployment preserved `friendallmods1`, reached `SERVING` on UDP 4200, and held
`NRestarts=0`; rollback snapshot is
`/home/bishop/bannerlord-coop/server/_mod_backups/pre-da18f9b95-20260814T163244Z`.
