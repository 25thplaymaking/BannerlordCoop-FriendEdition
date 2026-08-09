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

Green and stable, verified 3× each on the merge commit `782169c82` and again after the §6 fix wave:

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

### 5.4 Dead constants — FIXED
Four of seven `WorkshopPatchCategories` constants were unused, so `WorkshopModuleTestBase.PatchCategory_IsOneOfTheDeclaredWorkshopCategoriesOrNone` validated against a partly aspirational list. ImprovedGarrisons, Fourberie and PlayerSettlement are removed (those mods patch imperatively and would still declare `PatchCategory => null`, so the names were never going to be applied). UnblockableThrust stays, documented as deliberately unapplied — its adapter patches a native method that always resolves and must keep applying when the mod is absent.

---

## 6. Review findings — fix wave landed

The whole-branch review's findings are fixed on this branch. Re-verified after the wave: GameInterface `...Services.WorkshopMods` 254/2 failed and E2E `...Services.WorkshopMods` 95/0, identical across 3 runs each; `Coop.IntegrationTests` 148/0 with 2 template skips.

**FIXED — `FourberieHarmonyIsolation.PurgeAndAssertAuditedSurface` missed the stabilizer, and its failure latched.**
Every other scan→unpatch→verify cycle was wrapped in `HarmonyPatchInfoStabilizer`; this one wasn't, so one transient misread set the static, never-cleared `rejectedSurface` and rejected the mod for the life of the process. The whole cycle is now wrapped and `rejectedSurface` is written only once the retried cycle genuinely fails; the undeclared-patch catalog accumulates across retries so a later clean pass cannot erase a real finding. The same unstabilized check in `FourberieCompatibilityHandler.TryInstall` is fixed too.

**FIXED — `DiplomacyPatchCompatibilityGate.HasForbiddenPatches` failed OPEN.**
`StabilizeUntilAcceptable` returns on the first attempt whose predicate holds, so wrapping `() => !ScanForForbiddenPatches(...)` reported clean if any one of five scans said clean. It now uses `HarmonyPatchInfoStabilizer.RequireAcceptableOnEveryAttempt`: every read must agree before it reports clean, and the first read that sees something forbidden ends the loop — retries can only confirm the fail-closed verdict. That inner short-circuit also removes the secondary cost finding: `RemoveAndAssert`'s failure path is now one `GetAllPatchedMethods()` sweep per outer attempt instead of 5×5 = 25 sweeps and ~120 ms of `Thread.Sleep` on the game thread.

**DECIDED — the flakiness mitigation is test-only, so the production budget was raised.**
Setting the `AppContext` switch in a production assembly cannot work: HarmonyLib reads `System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization` only from its net5.0-and-newer builds. Scanning every per-TFM `0Harmony.dll` in Lib.Harmony 2.4.2 finds the switch literal (with `UseBinaryFormatter` and `JsonSerializer`) in net5.0/net6.0/net8.0 and none of the three in net35/net452/net472/net48/netcoreapp3.x — those carry no System.Text.Json fallback at all. The binary deployed to the game, `Modules/Coop/bin/Win64_Shipping_Client/0Harmony.dll` 2.4.2.0, is stamped `.NETFramework,Version=v4.7.2` and is byte-size-identical to the net472 lib, so production serializes patch info through BinaryFormatter unconditionally and the 5×5 ms calibration does not transfer. `HarmonyPatchInfoStabilizer.Attempts` is therefore doubled to 10 and the finding recorded on the type and on both bootstraps. Retry-until-acceptable returns on the first good read, so the extra attempts cost nothing on a healthy startup and are spent only on a path about to fail closed. Fails-closed behaviour is unchanged.

**FIXED — the record said all seven mods were declared.** Four are. See §5.1 and the design doc's Architecture section.

**FIXED — audit doc figures predated the merges** (233/63 vs the real 254/95).

**Still deferred, agreed not to block merge:** deny-list keys aren't validated against the catalog; `Append()` dereferences an unvalidated digest string.

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

1. ~~Verify whether the §6 fix wave landed.~~ It landed; see §6.
2. `gh pr ready 3 --repo 25thplaymaking/BannerlordCoop-FriendEdition` — CI has never run on this branch (drafts are skipped). Confirm the 8 shards, and that the 2 protobuf tests pass under vstest.
3. Decide whether to merge PR #3. Also outstanding: **PR #2** (battle E2E fixes, CI-green, unmerged).
4. Then start §4 — pick Diplomacy, enumerate its player-initiated action surface from the decompiled assembly, and route one action end-to-end through the four-part shape. Everything after that is repetition.
