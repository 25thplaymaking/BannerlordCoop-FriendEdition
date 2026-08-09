# Handoff — Workshop Mod Co-op Routing

**Written:** 2026-08-09
**Branch:** `25vid/workshop-integration` (pushed; open as draft [PR #3](https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/pull/3))
**Worktree:** `C:\Users\Bryce\Documents\ServerWork\workshop-integration`
**For:** whoever picks this up next. Assume no context from the session that produced it.

---

## 1. The actual goal, and how far it is from done

**Goal:** seven third-party Workshop gameplay mods run under co-op authority — the server learns about every mutation, clients can request them, state converges.

**Status: not started.** No gameplay is routed. Three of these mods (RBM, ImprovedGarrisons, Diplomacy) run on the live dedicated server *today* with no co-op awareness at all; every mutation they make is local to whichever machine executed it. That is unchanged by this branch.

What this branch did was clear the ground: the mod no longer crashes when a Workshop mod is absent, the test suite is deterministic, the ~13k-line integration is committed instead of living in a dirty working tree, and there is a contract for declaring modules. Necessary, not sufficient. **The work the user asked for is section 4 onward.**

---

## 2. Environment — read this before running anything

These will waste hours if rediscovered:

| Thing | Reality |
|---|---|
| `dotnet` on PATH | Runtimes-only **x86** tree; falsely reports "no SDKs". Always use `"C:\Program Files\dotnet\dotnet.exe"`. |
| `dotnet test` | **Does not work.** IPv4 loopback is broken system-wide; vstest.console never connects to its testhost. Not a timeout, not slow — unfixable from the test side. |
| Test runner that DOES work | xunit's in-process console runner: `~/.nuget/packages/xunit.runner.console/2.9.3/tools/net6.0/`. Copy `xunit.console.*` and `xunit.runner.*.dll` next to the test assembly, then `DOTNET_TC_CallCounting=0 dotnet.exe xunit.console.dll <Asm>.dll -noshadow -parallel none -namespace "..."`. |
| Building one test csproj | Fails on NuGet audit (NU1903/NU1902, Scriban 7.2.0). Build `source/CoopTests.slnf` instead. |
| New git worktrees | Need a directory link named `mb2` at the worktree root pointing at the game install, or nothing compiles. Create with PowerShell `New-Item -ItemType Junction`. **Never `ln -s`** — in this Git Bash it silently COPIES 12+ GB. |
| Whole `E2E.Tests` assembly in one process | Order-unstable by harness design. A baseline run produced 114 failures where a namespace-scoped run produced 0. **Only trust namespace- or class-scoped runs.** |
| `GameInterface.Tests.Serialization` | ~174 failures under the console runner, pre-existing and environmental (same symptom in `Common.Tests`, which this branch never touched). Not yours. |
| CI | `.github/workflows/pull_request.yml`, 8 E2E shards. **Only runs on `pull_request`, and skips drafts.** A push alone triggers nothing. Mark the PR ready to get a run. CI is the gate of record. |

---

## 3. Current state of the branch

Green and stable, verified 3× each on the merge commit `782169c82`:

| Suite | Result |
|---|---|
| `GameInterface.Tests` → `...Services.WorkshopMods` | 254 tests, **2 failed**, identical every run |
| `E2E.Tests` → `...Services.WorkshopMods` | 95 tests, **0 failed**, identical every run |

The 2 failures are a protobuf round-trip pair, **environmental** — proven by control: `Common.Tests`, untouched by this branch, fails an unrelated round-trip identically under the same runner. Do not "fix" them by changing production serialization. CI under vstest should show them passing; **that confirmation has not happened yet** because the draft PR was skipped.

### What exists

- **`IWorkshopModule`** (`source/GameInterface/Services/WorkshopMods/Core/IWorkshopModule.cs`) — ModuleId, WorkshopId, `ModuleFingerprint`, `PatchCategory` (nullable), `ResolveInstalledSha256()`, `RegisterSync(AutoSyncRegistry)`.
- **`WorkshopModuleRegistrar`** — `ResolveInstalledModules` (presence + byte-exact pin; gates Harmony categories and `IAutoSync`) and `ResolveLiveModules` (adds the operator config switch; **no production caller yet**).
- **`WorkshopModuleAutoSync`** — drives `RegisterSync` for installed modules.
- **`WorkshopModuleTestBase`** — shared gates, subclassed per module.
- **`HarmonyPatchInfoStabilizer`** — retries patch-info reads (see §6).
- Declared modules: **Diplomacy, RBM, DismembermentPlus, UnblockableThrust — 4 of 7.**

### What does NOT exist

- Any inbound intent path. `IWorkshopModule.Actions` is deliberately absent — it cannot be designed before one real action is routed.
- Declarations for **ImprovedGarrisons, Fourberie, PlayerSettlement**. They carry zero Harmony attributes and patch imperatively from their handlers, so they have no pin, no config key, and none of the shared gates.
- Any runtime effect from the per-module config switch.

---

## 4. THE WORK: routing a mod's gameplay

This is what was actually asked for and never started.

### 4.1 The rule that decides your approach — do not skip this

An earlier version of the design claimed per-module snapshot codecs would "largely fold away" in favour of AutoSync. **That is false, and structurally so.** It was corrected in `doc/specs/2026-08-09-workshop-module-integration-design.md`; the corrected rule:

> **AutoSync** where the state is a plain member the mod itself mutates and `ValidateSyncable` accepts it.
> **Snapshot codec** where `ValidateSyncable` rejects the state (record-typed or `List<>`-valued dictionaries), **or** where the state already has revisioned server-authoritative ownership.
> A module can need both.

Worked example — Diplomacy. `DiplomacyModule.RegisterSync` registers **nothing**, on purpose:
- `CooldownManager`'s three dictionaries (`Dictionary<string,CampaignTime>`, `Dictionary<Kingdom,CampaignTime>`) *would* pass `ValidateSyncable` — `CampaignTime` has a surrogate, `Kingdom` is registry-managed. "AutoSync can't do dictionaries" is **false**; don't repeat it.
- But `DiplomacyRuntime` already captures and applies all of that as one revisioned, validated, rollback-protected server-authoritative snapshot. AutoSync would be a **second independent writer** on the same state.
- The genuinely unsyncable state is on `WarExhaustionManager` (`Dictionary<string, WarExhaustionRecord>`, `Dictionary<string, List<WarExhaustionEventRecord>>`) and `DiplomaticAgreementManager` (`Dictionary<FactionPair, List<DiplomaticAgreement>>`).

**Each remaining mod must be assessed this way individually, with the mod decompiled.** Diplomacy's answer does not transfer.

Decompile with: `"C:\Users\Bryce\.dotnet\tools\ilspycmd.exe" -t <TypeName> "<path to mod dll>"`
Mods live under `P:\SteamLibrary\steamapps\workshop\content\261550\<workshopId>\bin\Win64_Shipping_Client\`.

### 4.2 The intent shape to follow

Coop's own four-part service pattern. Reference implementation to copy: `TryToGetAwayPatches` → `ClientBattleRetreatHandler` → `ServerBattleRetreatHandler` → `BattleRetreatInterface`.

1. Harmony prefix intercepts the mod's action on the client and **suppresses the local write**.
2. Publishes an internal message carrying the acting party/hero **explicitly** — never `MobileParty.MainParty`, which the headless server does not have.
3. Client handler sends a `Network*` request carrying **ids only, never outcomes** (the server decides costs; a client must not be able to dictate them).
4. Server handler **re-derives ownership from the peer**, validates, applies with patches live; state replicates via AutoSync/registries/snapshot.

Do not invent a parallel transport. Everything needed exists.

### 4.3 Suggested order

1. **Diplomacy** — already declared, largest adapter, discrete campaign actions, running live. Best first target.
2. **ImprovedGarrisons** — also running live; needs declaring first (§5.1).
3. **RBM / DismembermentPlus / UnblockableThrust** — mission-side, share `CombatModAuthorityPolicy`.
4. **Fourberie** — blocked on a pending Steam update (§7); its initializer replaces ~14 campaign models overlapping Coop authority, so routing may be rejected in favour of permanent blocking. Decide on evidence.
5. **PlayerSettlement** — dynamic campaign objects, largest identity surface. Last.

### 4.4 Per-mod definition of done

Coverage is **per action, not per mod**. "Diplomacy is routed" must mean a named, reviewed list of actions. Enumerate each mod's player-initiated surface from its decompiled code and record it, or "done" is unfalsifiable.

---

## 5. Known gaps that will bite

### 5.1 Three mods can't be declared without a decision
ImprovedGarrisons, Fourberie and PlayerSettlement have no Harmony attributes. When declared they will **all** need `PatchCategory => null`, making the nullable escape hatch the majority case (4 of 7) rather than the single reviewed exception it was approved as. Revisit whether the contract should express "patches imperatively" as a first-class state instead.

### 5.2 The config switch gates nothing
`GameInterface.PatchAll()` runs at container-build; `ModConfigAuthority` doesn't install options until `CampaignReady`. So presence-and-pin gates patching, and the operator switch has **no runtime consumer**. Its natural first consumer is the routing funnel you are about to build — bind it there, or delete `ResolveLiveModules`. Do not let another increment pass with an unreferenced public API.
Also: `workshopModules` was deliberately **left out of `deploy/mod-config.default.json`** — do not advertise a switch that does nothing. Note the wire member is a **deny list** (`DisabledWorkshopModules`), because protobuf omits empty repeated fields and an allow list would silently disable every adapter on a zeroed receiver.

### 5.3 The registrar's fingerprint check is a tautology
`ResolveInstalledSha256()` returns the pinned constant or null, so `Fingerprint.Matches(...)` can never fail for a non-null answer. **All real safety lives inside each module's `ResolveInstalledSha256`**, and nothing in the contract or the shared gates can detect a lazy implementation. The gates only ever assert absent behaviour — they never prove a module works when installed.

### 5.4 Dead constants
Four of seven `WorkshopPatchCategories` constants are unused (ImprovedGarrisons, Fourberie, PlayerSettlement, UnblockableThrust). `WorkshopModuleTestBase.PatchCategory_IsOneOfTheDeclaredWorkshopCategoriesOrNone` therefore validates against a partly aspirational list and would accept a typo matching a dead constant.

---

## 6. Open findings — fix before merging PR #3

A whole-branch review found these; **a fix wave was dispatched but its result was not confirmed before this handoff was written. Verify current state before acting.**

**Important — `FourberieHarmonyIsolation.PurgeAndAssertAuditedSurface` (`:23-72`) misses the stabilizer, and its failure latches.**
Every other scan→unpatch→verify cycle is wrapped in `HarmonyPatchInfoStabilizer`; this one isn't. One transient misread sets the **static, never-cleared** `rejectedSurface` (`:57`) and throws — after which `:27` throws immediately on every later call for the life of the process. The same unstabilized check exists at `FourberieCompatibilityHandler.cs:164-168`.

**Important — `DiplomacyPatchCompatibilityGate.HasForbiddenPatches` (`:50-52`) fails OPEN.**
`StabilizeUntilAcceptable` returns on the **first** attempt whose predicate is true. Wrapping `() => !ScanForForbiddenPatches(...)` means "clean" is reported if **any one of five** scans says clean — a 5× amplified false negative. Failure: a real `Diplomacy.Patches.*` patch survives removal, one re-read deserializes its `PatchMethod` as null or as the allow-listed type, the scan returns clean, and Coop runs with an unremoved Diplomacy mutation funnel installed. The retry shape is correct for the exact-inventory asserts elsewhere and **wrong here** — retries must only confirm a dirty verdict, never shop for a clean one.
Secondary: `RemoveAndAssert:81-85` nests the stabilizer, giving 5×5 = 25 full `GetAllPatchedMethods()` sweeps and ~120 ms of `Thread.Sleep` on the game thread during `PatchAll`.

**Decision needed — the flakiness mitigation is test-only.**
`HarmonySerializationBootstrap` (both test assemblies) disables HarmonyLib's legacy BinaryFormatter path, which cut the misread rate ~10×; the 5×5 ms retry budget was calibrated **with that reduction in place**. No production assembly sets it, and the game is .NET Framework 4.7.2 where BinaryFormatter is on by default — so production faces the un-mitigated rate with only the retry. (The `AppContext` switch is .NET Core-era and may not be honoured on net472.) Either raise the production budget or record why the calibration transfers. It fails closed, so this is robustness, not a safety hole.

**Deferred, agreed not to block merge:** deny-list keys aren't validated against the catalog; `Append()` dereferences an unvalidated digest string; audit doc figures predate the merges (233/63 vs the real 254/95).

---

## 7. External blockers

- **Fourberie is not updated.** Installed manifest `4391404683672989722` (2026-08-02); latest `1598945672157391038` (2026-08-09 13:42 UTC). ACF says `NeedsUpdate 1`; the installed `Fourberie.dll` still hashes to `fd1c02158817fae5b90e3c121da474096caa368cb35495d83ce81ea49d860c71`, identical to the audited copy. Steam must download it, then **re-fingerprint and re-pin** — `deploy/workshop-mods.json` still pins the old manifest.
- **`Nightly Release` workflow is disabled** on the fork, deliberately. It ran nightly against `development`, built this private code successfully, and failed one step before **`Publish latest client to public R2`**. Do not re-enable without deciding whether publishing this derivative publicly is acceptable.

---

## 8. Context that explains decisions

- **Design:** `doc/specs/2026-08-09-workshop-module-integration-design.md`
- **Plan (increments 0–1 only):** `doc/plans/2026-08-09-workshop-module-integration.md`
- **Execution ledger — every ruling, amendment and parked finding:** `.superpowers/sdd/2026-08-09-workshop-module-integration/progress.md`
- **Prior audit of all 11 Workshop items:** `doc/WorkshopModIntegrationAudit.md`

Two temporary worktrees exist and can be deleted once PR #3 is settled:
`ServerWork/wt-fixes` (`25vid/wi-fixes`) and `ServerWork/wt-contract` (`25vid/wi-contract`), both merged into the branch.

---

## 9. Immediate next actions

1. Verify whether the §6 fix wave landed; if not, do those two fixes.
2. `gh pr ready 3 --repo 25thplaymaking/BannerlordCoop-FriendEdition` — CI has never run on this branch (drafts are skipped). Confirm the 8 shards, and that the 2 protobuf tests pass under vstest.
3. Decide whether to merge PR #3. Also outstanding: **PR #2** (battle E2E fixes, CI-green, unmerged).
4. Then start §4 — pick Diplomacy, enumerate its player-initiated action surface from the decompiled assembly, and route one action end-to-end through the four-part shape. Everything after that is repetition.
