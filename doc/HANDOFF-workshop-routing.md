# Handoff — Workshop Mod Co-op Routing

**Written:** 2026-08-09
**Branch:** `25vid/workshop-integration` (pushed; [PR #3](https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/pull/3) marked ready 2026-08-09, **CI green** on run 31334171473)
**Worktree:** `C:\Users\Bryce\Documents\ServerWork\workshop-integration`
**For:** whoever picks this up next. Assume no context from the session that produced it.

---

## 1. The actual goal, and how far it is from done

**Goal:** seven third-party Workshop gameplay mods run under co-op authority — the server learns about every mutation, clients can request them, state converges.

**Status: first action routed (2026-08-09).** Diplomacy's **Donate Gold** goes through the full
four-part shape end-to-end: `DiplomacyDonateGoldRoutingPatch` (client prefix, suppress + intent) →
`DiplomacyGoldDonationAttempted` → `DiplomacyDonateGoldHandler` (ids-only
`NetworkRequestDiplomacyDonateGold`; server half re-derives the hero from the peer) →
`IDiplomacyDonateGoldInterface.TryApplyDonation` (amount validated against the server's books, the
mod's own hero-parameterised `GiveGoldToClanAction.ApplyFromHeroToClan` invoked reflectively
against the pinned assembly, relation gain recomputed from native models with explicit heroes;
trait XP documented-skipped as MainHero-bound). Five E2E gates pin the funnel; the apply itself is
live-smoke scope because the pinned mod is absent in tests. **Every other player action of every
mod remains blocked, and the ledger in §4.4 is what "routed" means.**

Everything else this branch did cleared the ground: no crash when a Workshop mod is absent, a
deterministic test suite, the ~13k-line integration committed, a contract for declaring modules —
and, as of today, a green CI as the gate of record. **The remaining work is section 4's
repetition of the donate-gold shape across the action ledger.**

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

**The CI-red findings below were all fixed on 2026-08-09 (commits after `481902edb`); local
verification of every affected class is green.** Re-confirm on the next CI run, then trust these:

| Suite (scoped) | Result after fixes |
|---|---|
| `GameInterface.Tests` → `...Services.WorkshopMods` | 254 tests, **0 failed** |
| `E2E.Tests` → `...Services.WorkshopMods` | 95 tests, **0 failed** |
| `PatchTest` + `ContainerTest` (blanket PatchAll, standalone container) | 8 tests, 0 failed |
| `E2E...SeparatismConfigurationSyncTests` | 8 tests, 0 failed |

What CI run 31333186560 (first ever on this branch) found, and what was done:

1. **`PatchTest.HarmonyPatchesAll`** — blanket `PatchAll(assembly)` hit the categorised Diplomacy
   classes, whose `TargetMethods()` resolve to nothing without the mod. Fixed as recommended: all
   nine classes in `DiplomacyCompatibilityPatches.cs` now carry the `[HarmonyPrepare] =>
   TargetMethods().Any()` guard the RBM/DismembermentPlus adapters already had, and the test
   deliberately stays blanket `PatchAll` so those guards are exercised.
2. **`WorkshopCompatibilityManifestTests.Protobuf_RoundTrip…`** — NOT a serialization bug (an
   earlier note here claimed it was; measurement says otherwise). The test's own wire-shape
   validation recomputes the digest over `Active` and PASSED — so `Active` was false *before*
   serialization. The catalog deliberately declares only Harmony `FeatureActiveExpectedOnClient`;
   the test's blanket `Assert.All(entries, Active)` was stale. It now asserts each entry matches
   the catalog's per-role expectation, plus an explicit Harmony-is-active check so a true still
   provably crosses the wire. The sibling PlayerSettlement failure was the same class of test bug
   (`Assert.Empty` on the null protobuf legitimately deserializes for an omitted empty repeated
   field — the exact shape `PlayerSettlementStateCodec.TryValidate` normalizes).
3. **`NetworkUpdateOtherOptions.TryValidateWireShape`** — real production bug: `Enum.IsDefined`
   throws on an int probe against a non-Int32-backed enum. Fixed underlying-type-agnostically
   (compare against each defined value; no narrowing cast).
4. **All 8 E2E shards** — `ModConfigAuthority.InitializeHost`'s birthAndDeath preflight ran against
   whatever `CoopData/mod-config.json` the hosting machine has: the developer's live file locally
   (tests silently coupled to it), nothing on CI. The E2E environment now registers
   `DeterministicModConfig` — a fixed, preflight-valid config — for every instance.
5. **Standalone GameInterface containers** (`ContainerTest`, `PatchBootstrap`) — this branch's
   `RuntimeWorkshopModuleDiscovery` needs `IModuleInfoProvider`, which production registers from
   Coop.Core's `CommonModule`; the test bootstraps now register a stand-in.

**Lesson, still standing:** namespace-scoped runs were used all session because the full
`E2E.Tests` assembly is order-unstable in one process. That was correct for iteration and wrong as
an acceptance gate. **CI is the only gate of record.**

### What exists

- **`IWorkshopModule`** (`source/GameInterface/Services/WorkshopMods/Core/IWorkshopModule.cs`) — ModuleId, WorkshopId, `ModuleFingerprint`, `PatchCategory` (nullable), `ResolveInstalledSha256()`, `RegisterSync(AutoSyncRegistry)`.
- **`WorkshopModuleRegistrar`** — `ResolveInstalledModules` (presence + byte-exact pin; gates Harmony categories and `IAutoSync`) and `ResolveLiveModules` (adds the operator config switch; **no production caller yet**).
- **`WorkshopModuleAutoSync`** — drives `RegisterSync` for installed modules.
- **`WorkshopModuleTestBase`** — shared gates, subclassed per module.
- **`HarmonyPatchInfoStabilizer`** — retries patch-info reads (see §6).
- Declared modules: **Diplomacy, RBM, DismembermentPlus, UnblockableThrust — 4 of 7.**

### What does NOT exist

- The `IWorkshopModule.Actions` table. ONE inbound intent path now exists (Donate Gold, see §1),
  built directly on the four-part shape; the table abstraction is deliberately deferred until 2–3
  routed actions show what it must express (§9.3).
- Declarations for **ImprovedGarrisons, Fourberie, PlayerSettlement**. They carry zero Harmony attributes and patch imperatively from their handlers, so they have no pin, no config key, and none of the shared gates.
- Any runtime effect from the config switch beyond the routed donation's gate (§5.2).

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

### 4.4 Per-mod definition of done — the action ledger

Coverage is **per action, not per mod**. "Diplomacy is routed" must mean a named, reviewed list of actions. Enumerate each mod's player-initiated surface from its decompiled code and record it, or "done" is unfalsifiable.

**Diplomacy's player-initiated surface** (from the 1.4.7 decompile; this list IS the definition of
"Diplomacy is routed" — every entry either gets the four-part shape or a recorded decision not to):

| Action | Entry point | State |
|---|---|---|
| Donate gold to a clan | `DonateGoldVM.ExecutePropose` | **ROUTED** (2026-08-09) |
| Grant fief to a vassal | `GrantFiefVM.OnGrantFief` | **ROUTED** — authenticated server operation with stable settlement/clan ids |
| Declare war (kingdom screen) | `KingdomWarItemVMMixin.ExecuteDirectAction` | **ROUTED** — authenticated, revision-checked server operation |
| Propose peace (kingdom screen) | `KingdomTruceItemVMMixin.ExecuteDirectAction` | **ROUTED** — authenticated, revision-checked server operation |
| End alliance | `KingdomDiplomacyVMMixin` / Diplomacy action | **ROUTED** — authenticated, revision-checked server operation |
| Propose non-aggression pact | `KingdomTruceItemVMMixin.ProposeNonAggressionPact` + `FormNonAggressionPactAction` | **ROUTED** — server validates and applies the agreement |
| Send messenger | `EncyclopediaHeroPageVMMixin.SendMessenger` | **ROUTED** — persisted controller-scoped server queue; embedded joined-clan members retain this personal action |
| Keep fief after siege | `KeepFiefAfterSiegeBehavior.OnPlayerSettlementTaken` | **ROUTED** — server-owned prompt and decision |
| Civil war actions (create/join/leave faction, start rebellion) | `RebelFactionsVM` / `RebelFactionItemVM` / `CivilWar.Actions.*` | permanently blocked while Friend Separatism owns rebellions (recorded decision, not debt) |

---

## 5. Known gaps that will bite

### 5.1 Three mods can't be declared without a decision
ImprovedGarrisons, Fourberie and PlayerSettlement have no Harmony attributes. When declared they will **all** need `PatchCategory => null`, making the nullable escape hatch the majority case (4 of 7) rather than the single reviewed exception it was approved as. Revisit whether the contract should express "patches imperatively" as a first-class state instead.

### 5.2 The config switch — FIRST CONSUMER LANDED, but only one
`GameInterface.PatchAll()` runs at container-build; `ModConfigAuthority` doesn't install options until `CampaignReady`. So presence-and-pin gates patching, and the operator switch gates **runtime intents**: `DiplomacyDonateGoldInterface` is now `ResolveLiveModules`'s first production consumer — a module the operator disabled refuses the donation (`ModuleDisabled`) before any state is touched, and an E2E gate pins it. Every FUTURE routed action must go through the same gate; the switch still gates nothing else (snapshots, AutoSync, the existing compatibility adapters run on presence alone).
Also: `workshopModules` was deliberately **left out of `deploy/mod-config.default.json`**; revisit once the switch gates enough behaviour to be worth advertising. Note the wire member is a **deny list** (`DisabledWorkshopModules`), because protobuf omits empty repeated fields and an allow list would silently disable every adapter on a zeroed receiver.

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

- **Fourberie and RBM have newer upstream manifests** (Fourberie: installed `4391404683672989722`, latest `1598945672157391038`; RBM: installed `8508128689459287315`, latest `3016800505162011905`). **No longer a packaging blocker:** the suite builder now builds against the installed, pin-verified content and only WARNS about newer upstreams — a Steam download in flight (`NeedsDownload≠0`) stays fatal, and the per-file SHA-256 pins plus the post-staging source re-hash remain the integrity gates. Adopting either update still requires the full re-audit + re-pin of `deploy/workshop-mods.json`; until then the suite deliberately ships the audited builds.
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

## 9. Immediate next actions (updated 2026-08-09, post-CI-green)

Done this session: §6 fix wave verified · scoped gates re-verified · PR #2 **merged** to
`development` · PR #3 marked ready · first CI run's failures (§3) all fixed, **CI green** ·
Diplomacy action surface enumerated (§4.4) · **Donate Gold routed end-to-end** with five E2E gates
· config switch's first runtime consumer landed (§5.2).

1. ~~Merge PR #3~~ — **merged to `development` 2026-08-09** (merge commit `8e389f11e`); the two
   temporary worktrees and their `wi-fixes`/`wi-contract` branches are deleted (junctions removed
   as links, game install verified intact).
1b. **The distributable suite is BUILT and the installer PROVEN (2026-08-09).**
   `Build-PrivateWorkshopSuite` ran clean against the audited pins (11 Workshop modules + the
   freshly built development-head Coop; 965 files verified; sources unchanged after staging):
   staged at `C:\Users\Bryce\Documents\ServerWork\FriendEdition-WorkshopSuite`, archive
   `FriendEdition-WorkshopSuite.zip` (SHA-256
   `a773e1bb555a589cd40ea1a49ca0404225e63c15268bbb5965e4d6582e95bbc4`). The guided client
   installer from that real suite was executed against a synthetic Bannerlord install:
   INSTALLATION PASSED — all 12 managed modules written separately, launcher data selecting
   exactly `Bannerlord.Harmony → Native → SandBoxCore → CustomBattle → Sandbox → StoryMode →
   Coop` with every Workshop gameplay/framework module staged-inactive, the handshake receipt at
   `Coop/WorkshopSuite/MANIFEST.json`, and the installed Diplomacy DLL byte-identical to the
   compatibility pin (`90930a1d…`). The fixture test suite (13 checks incl. injected-failure
   rollback) is green. Give friends the ZIP; they run `Run-ClientSetup.cmd`.
2. **Live smoke the routed donation** — needs Bryce's game + the real Diplomacy 1.4.7 DLL: donate
   from a client, watch the server apply and the gold/relation deltas replicate. E2E cannot cover
   the applied path (mod absent by construction); until this runs, "routed" is proven only up to
   the `ModuleNotInstalled` boundary.
3. **Next action: GrantFief** (`GrantFiefVM.OnGrantFief`) — same shape, discrete, ids-only.
   After 2–3 routed actions, extract the common client-half/server-half plumbing into the
   `IWorkshopModule.Actions` table the design deferred (one instance was too early to abstract;
   three is not).
4. Then the §4.3 mod order: ImprovedGarrisons (declare first, §5.1), the mission-side trio,
   Fourberie (after its Steam update re-pin, §7), PlayerSettlement.
