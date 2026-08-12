# Upstream Nightly Bugfix Backports Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Selectively backport the upstream nightly bug fixes that remain compatible with the Friend Edition's Bannerlord 1.4.7 runtime, existing save, and launcher-managed client/server release contract.

**Architecture:** Import final pull-request diffs instead of merging upstream `development`. Keep every backport isolated and traceable to its upstream PR, exercise the intended regression before production changes, then gate the combined patch through the repository build/test workflow. Do not import the upstream 1.4.8 project-version change, nightly module identity, save-breaking redesigns, or fixes that overlap Friend Edition's custom mission/auto-resolve authority work without a separate reconciliation pass.

**Tech Stack:** C#/.NET, Harmony, xUnit, Git, GitHub Actions, Bannerlord 1.4.7 assemblies.

## Compatibility decision

Import this batch:

| PR | Fix | Compatibility reason |
| --- | --- | --- |
| #2968 | Party-screen inventory duplication after cancelling prisoner/troop upgrade | `PartyScreenData.ResetUsing` exists in the pinned 1.4.7 assembly; local patch applies cleanly. |
| #2913 | Coalesce non-lord party trade-gold updates | Uses the coalescer already present in this fork; no save or wire-schema change. |
| #2905 | Chat visibility/escape/settings fixes | UI and local message behavior only; final diff applies cleanly. |
| #2897 | Companion/troop duplication when creating clan parties | Existing 1.4.7 APIs and roster format; final diff applies cleanly. |
| #2898 | Companion parties taking troops from player garrisons | One authority predicate change using extensions already in this fork. |
| #2899 | Settlement visibility log spam | Removes a process-local derived property from synchronization; no saved field. |
| #2884 | Player party stops following moving AI party | Client-side AI tick correction; no persistence or message changes. |
| #2855 | Null-party map-event result crash | Repairs invalid participants before reward calculation; upstream regression test included. |
| #2768 | Wrong/player party disbanded during clan-party refresh | Uses the popup's stable party and adds server-side player-party guards; upstream regression tests included. |

Defer from this batch:

- #2919 and #2874: 1.4.8/project identity and upstream-nightly packaging changes are intentionally incompatible with this fork's launcher/release identity.
- #2903: adds persisted session data; requires a dedicated existing-save migration review.
- #2931 and #2912: ownership/garrison lifecycle diffs overlap Friend Edition settlement and mod-authority work and do not apply cleanly.
- #2941, #2773, and #2863: overlap already-backported hideout, companion-health, and caravan hardening.
- #2867: overlaps the custom auto-resolve authority path and needs separate reconciliation rather than a blind patch.
- Feature/content PRs (team NPCs, wanderers, limits): outside this bugfix-only batch.

## Global constraints

- Stay on the current save. Do not create, reset, convert, or deploy a save.
- Keep Bannerlord target `v1.4.7`; do not import the upstream `v1.4.8` project change.
- Do not touch the pre-existing line-ending-only mission changes or untracked `work/` directory.
- Preserve the Friend Edition module ID, launcher channels, Workshop adapters, and custom battle/hideout behavior.
- Import final reviewed PR behavior, not intermediate commits.
- Commit verified increments, then push `development` only after the combined gate passes.

---

### Task 1: Party-screen inventory reset and trade-gold traffic

**Files:**
- Create: `source/GameInterface.Tests/Services/Party/PartyScreenDataPatchesTests.cs`
- Create: `source/GameInterface/Services/Party/Patches/PartyScreenDataPatches.cs`
- Create: `source/E2E.Tests/Services/MobileParties/MobilePartyTradeGoldCoalescingTests.cs`
- Modify: `source/GameInterface/Services/MobileParties/MobilePartyRegistry.cs`
- Modify: `source/GameInterface/Services/MobileParties/MobilePartySync.cs`

- [x] Add focused tests proving `ResetUsing` temporarily allows and always revokes the current thread, and import upstream's trade-gold coalescing regression test.
- [x] Run those tests before production changes and record the expected missing-patch/non-coalescing failures.
- [x] Apply the final reviewed production diffs from #2968 and #2913.
- [x] Re-run focused tests and commit the verified increment.

### Task 2: Party creation, garrison, visibility, and following fixes

**Files:**
- Create: `source/GameInterface/Services/MobileParties/Patches/MobilePartyHelperPatches.cs`
- Modify: `source/GameInterface/Services/TroopRosters/Interfaces/TroopRosterInterface.cs`
- Modify: `source/GameInterface/Services/Settlements/Patches/Disable/DisableGarrisonTroopsCampaignBehavior.cs`
- Modify: `source/GameInterface/Services/Settlements/SettlementSync.cs`
- Modify: `source/GameInterface/Services/MobilePartyAIs/Patches/PartiesThinkPatch.cs`
- Test: focused GameInterface/E2E regression coverage for the imported behavior.

- [x] Add regression coverage for the desired roster XP normalization, player-garrison authority guard, local settlement visibility, and client escort tick behavior.
- [x] Run the focused tests and record the expected failures.
- [x] Apply the final reviewed production diffs from #2897, #2898, #2899, and #2884.
- [x] Re-run focused tests and commit the verified increment.

### Task 3: Chat and map-event crash fixes

**Files:**
- Modify: `source/GameInterface.Tests/Services/Chat/ChatOverlayTests.cs`
- Modify: `source/GameInterface.Tests/Services/Chat/ChatServiceTests.cs`
- Create: `source/GameInterface.Tests/Services/UI/ChatOptionsTests.cs`
- Modify: `source/GameInterface/Services/Chat/ChatOverlay.cs`
- Modify: `source/GameInterface/Services/Chat/ChatService.cs`
- Modify/Create: upstream #2905 chat settings/UI files and generated `UIMovies/CoopOptionsUIMovie.xml`
- Create: `source/GameInterface.Tests/Services/MapEvents/MapEventResultPartyRepairTests.cs`
- Modify: `source/GameInterface/Services/MapEvents/Patches/MapEventPatches.cs`

- [x] Import the upstream regression tests before production changes and record the expected failures.
- [x] Apply the final reviewed production diffs from #2905 and #2855.
- [x] Re-run focused tests, validate the generated UI movie/template pair, and commit the verified increment.

### Task 4: Clan-party disband safety

**Files:**
- Modify: `source/GameInterface.Tests/GameInterface.Tests.csproj`
- Create: `source/GameInterface.Tests/Services/Clans/ClanPartiesVMPatchesTests.cs`
- Modify: #2768 production files under `source/GameInterface/Services/Clans`, `MobileParties/Patches`, and `Party/Handlers`.

- [ ] Import the upstream clan-party regression tests before production changes and record the expected failures.
- [ ] Apply the final reviewed #2768 production diff, preserving existing Friend Edition coalescer integration.
- [ ] Re-run focused tests and commit the verified increment.

### Task 5: Combined verification and nightly handoff

**Files:**
- Modify: `STATUS.md`
- Modify: this plan's checkboxes.

- [ ] Build `source/CoopTests.slnf` in Release using the x64 .NET SDK.
- [ ] Run all focused backport regression classes locally with isolated test-runner processes.
- [ ] Run repository formatting/diff checks and confirm the pre-existing mission/work status is unchanged.
- [ ] Update `STATUS.md` with imported/deferred PRs, save compatibility, and verification evidence.
- [ ] Push the verified commits to `development` and require the complete GitHub test workflow to pass before treating the launcher nightly as published.
- [ ] Do not deploy or create a save as part of this backport pass.
