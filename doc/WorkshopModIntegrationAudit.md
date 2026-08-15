# Workshop Mod Integration Audit

Audit date: 2026-08-08  
Target: Bannerlord Coop Friend Edition, game/nightly line 1.4.7  
Workshop source: `P:\SteamLibrary\steamapps\workshop\content\261550`  
Decompiler evidence: `C:\Users\Bryce\Documents\ServerWork\workshop-decompile-audit`

> **Release-baseline notice (2026-08-09):** Steam currently reports Fourberie Workshop item `2875710877` at a newer manifest than the installed audited copy. The Fourberie manifest ID and fingerprint below are therefore historical audit evidence, not releasable package pins. Packaging must remain fail-closed until Steam installs the new bytes and the fingerprint/method-surface review is repeated.

## Executive decision

The eleven subscribed modules cannot safely be copied into Friend Edition and enabled as one block. The framework modules and every gameplay module assume a normal rendered, single-local-player Bannerlord process. Several mutate the same campaign or mission methods that Coop already owns. A successful headless boot would therefore prove only that an exception was avoided; it would not prove that the feature is synchronized, authoritative, persistent, or safe for late join.

This audit uses three distinct readiness states:

- **Safe/enabled**: the code is allowed to execute in its declared process role, has one clear patch owner, and cannot create authoritative state on an untrusted peer.
- **Guarded but feature-blocked**: startup/load is protected, but the gameplay entry points return an explicit unavailable response and do not mutate campaign or mission state. This is a compatibility milestone, not feature completion.
- **Fully co-op routed**: intent is validated by the designated authority, the result is idempotently replicated to both clients, persistence and late join are implemented, configuration/content hashes match, and the module-specific E2E gate passes.

### Audit-time disposition

| Module | Safe/enabled scope | Guarded but feature-blocked scope | Fully co-op routed at audit time |
|---|---|---|---|
| Harmony | Exact pinned `Bannerlord.Harmony` v2.4.2.248 provider, loaded before Native and Coop | Every embedded or second `0Harmony.dll` is omitted | Framework; not a gameplay feature |
| UIExtenderEx | None (package staged inactive) | Full original runtime on every role; deliberate activation is detected, reversible state is contained, and hardened Coop aborts with disable/restart guidance | No |
| ButterLib | None (package staged inactive) | Full original runtime, including loaders, crash/UI hooks, save injection, lifecycle patches, and non-disableable subsystems; deliberate activation always aborts | No |
| MCM v5 | None (package staged inactive) | Full original runtime, UI, migrations, local providers, and per-save behavior; deliberate activation always aborts | No |
| RBM | None of the original gameplay entry points | XML, Combat, AI, Tournament, and UI slices independently | No |
| Improved Garrisons | None of the original campaign behaviors | Entire feature | No |
| DismembermentPlus | None of the original `OnRegisterBlow` implementation | Cosmetic feature pending an authoritative sever event | No |
| Fourberie | None of the original campaign/mission behaviors | Feature domains independently | No |
| Diplomacy | Client UI may render an authoritative snapshot only | All campaign managers/actions | No |
| Unblockable Thrust | None of the original collision postfix | Combat rule pending integration with Coop's blow authority | No |
| Player Settlement | Client-only placement preview after it becomes read-only | Creation, rebuilding, persistence, AI, and siege behavior | No |

**Release implication:** the safe baseline is one active Harmony runtime plus explicit feature guards. UIExtenderEx, ButterLib, and MCM are receipt-pinned package content only and remain staged inactive on every role; no safe/enabled optional-framework scope is claimed. Do not label any of the seven gameplay modules “integrated” merely because the server and two clients reach the campaign map. At audit time, none of them met the fully-routed definition.

### Implementation status (2026-08-09)

The current Friend Edition branch implements the guarded baseline, not the complete gameplay integrations described later in this document. Its package and connection handshake require all eleven managed Workshop components to match the pinned receipt, load order, version, content hash, and configuration hash. The exact Harmony provider must be active; the other ten Workshop components must be present but inactive. A client or server with an optional original enabled is rejected before campaign/save transfer.

The branch also carries default-off compatibility adapters for RBM, Improved Garrisons, DismembermentPlus, Fourberie, Diplomacy, Unblockable Thrust, and Player Settlement. These adapters add exact binary/method-shape gates, patch-owner isolation, authority checks, and bounded snapshot scaffolding, but they do not make the original modules production-ready. Deliberately blocked menus, actions, mission callbacks, persistence paths, and framework-dependent features remain unavailable.

Carrying an adapter is not the same as being declared through the `IWorkshopModule` contract, and only four of the seven are: `Bannerlord.Diplomacy` (in `GameInterfaceModule`), RBM, DismembermentPlus and UnblockableThrust (in `MissionModule`). **ImprovedGarrisons, Fourberie and Player Settlement are not declared** — a statement that "all seven mods declared through the contract" (the wording on the merge commit that landed the contract) is wrong about. Those three carry no Harmony attributes at all; their adapters patch imperatively from `*CompatibilityHandler.TryInstall`, so they have no Harmony category to gate, and correspondingly no `IWorkshopModule`, no catalog-reconciled fingerprint pin, no `mod-config.json` per-module switch, and none of the shared `WorkshopModuleTestBase` gates (absent / disabled / declaration). Declaring them is tracked follow-on work, not part of this pass.

Server gameplay configuration is now a revisioned, hashed authority snapshot accepted before save transfer. It explicitly includes `difficulty.birthAndDeath`, rejects client-originated configuration and campaign-option changes, and verifies after `CampaignReady` that the engine has `CampaignOptions.IsLifeDeathCycleDisabled=false`. The optional TaleWorlds `BirthAndDeath` module remains disabled.

All affected production and test projects compile in Release with zero errors. The normal VSTest host repeatedly fails to connect after discovery in this environment (IPv4 loopback is broken machine-wide; `dotnet test`/vstest never reaches its testhost), so runtime tests are executed with the xunit 2.9.3 in-process console runner (`xunit.console.dll ... -noshadow -parallel none`) run directly against each test assembly's build output instead. Under that runner, measured on the merged branch at `782169c82` and again after the review fix wave, the `GameInterface.Tests.Services.WorkshopMods` namespace executes **254 tests with 2 failures** and `E2E.Tests.Services.WorkshopMods` executes **95 tests with 0 failures**, both identical across 3 consecutive runs each. `Coop.IntegrationTests` (whole assembly, same runner) executes 148 tests with 0 failures and 2 skips, the skips being the two example template tests. Never run the whole `E2E.Tests` assembly as a signal: it is order-unstable by harness design, and only namespace- or class-scoped runs are trustworthy.

*(An earlier revision of this paragraph recorded 233 and 63 with "5+ runs" stability. Those figures were measured before the two worktree merges landed and are superseded by the counts above; the 20/20 and 5/5 stability runs cited in the next paragraph likewise predate the merges and were on the smaller suites.)*

The 2 stable GameInterface failures are `WorkshopCompatibilityManifestTests.Protobuf_RoundTrip_PreservesAndValidatesManifest` and `PlayerSettlementCompatibilityTests.EmptyLateJoinSnapshot_ProtobufRoundTripsAndValidates`, both `Assert.IsType`-style protobuf round-trip assertions whose "Expected"/"Actual" print identically (same type, same assembly, same version) yet fail. A control run of `Common.Tests` — an assembly this branch does not touch — reproduces the identical failure shape on its own unrelated serialization round-trip test (`AggregateMessagePacketTests.Envelope_RoundTripsInnerMessagesInOrder`) under the same runner. Since the failure appears on code this branch never modified, using the same "type looks identical but fails an identity/type check" symptom, the cause is the xunit console runner's assembly-loading behavior (most likely a duplicate load of the same assembly into two contexts, so `Type.Equals` fails for what prints as the same type), not a defect in the Workshop protobuf contracts. This has not yet been confirmed by CI (the gate of record per this plan), since no PR was pushed/opened during this pass; that confirmation remains open before either protobuf test can be trusted as fully proven environmental rather than merely runner-suspected.

Beyond that pair, the Workshop test suites are otherwise green and non-flaky as of this pass. A prior investigation of the same suites found genuine run-to-run instability (failure counts varying 6→8→7 in GameInterface's WorkshopMods namespace and 1→0→2, always inside `CombatModCompatibilityTests`, in E2E's) traced to a real HarmonyLib defect: `Harmony.GetPatchInfo` persists patches by serializing them into `HarmonySharedState` and re-deserializing on every read, and that round trip has been observed, under GC pressure from a busy test process, to intermittently reconstruct the wrong `MethodInfo` for a patch's `PatchMethod` — confirmed by hash mismatch against the original object, not merely reference inequality. Every Workshop module's Harmony isolation guard (ImprovedGarrisons, Fourberie, Diplomacy, Player Settlement, the shared Frameworks cohort, and Missions' CombatModCompatibilityGuard) now retries its patch-info read via a shared `HarmonyPatchInfoStabilizer` before failing closed, plus a module initializer disabling HarmonyLib's legacy BinaryFormatter serialization path. Verified stable at the time: 20/20 consecutive runs of the GameInterface namespace and 5/5 of the E2E namespace, identical failure counts every time — measured on the pre-merge suites (233/63), and re-confirmed at 3/3 each on the merged counts above. Note the BinaryFormatter initializer is a test-only mitigation: HarmonyLib reads that AppContext switch only in its net5.0-and-newer builds, and the net472 `0Harmony.dll` the game loads has no such path, so the stabilizer's retry budget is the sole production mitigation and is sized accordingly.

The exact Fourberie `v1.4.7.6` payload is now installed, fingerprinted, and in
the `v1.4.8` candidate. Publication remains blocked by its 420 open authority
routes plus the rendered/same-save release gates; acquiring the newer bytes did
not make the still-unadapted mission/menu behavior safe for co-op.

## Evidence and scope

The inventory below comes from each local `SubModule.xml`, the Win64 assemblies actually present in the Workshop folders, decompiled type/method bodies, and the Friend Edition source tree. Exact binaries are fingerprinted in the appendix. Decompiled output is evidence for behavior and integration design, not a substitute for creator source where source is available.

The review covers the eleven active Workshop items named below. It does not claim compatibility for a different Workshop update, a different game build, the War Sails RBM add-on, or any transitive mod not present in this set.

## Exact inventory

| Workshop ID | Module ID / module version | Primary Win64 assembly | Entry point(s) | Classification / metadata |
|---|---|---|---|---|
| `2859188632` | `Bannerlord.Harmony` / `v2.4.2.248` | `Bannerlord.Harmony.dll` 2.4.2.248; contains `0Harmony.dll` 2.4.2.0 | `Bannerlord.Harmony.SubModule` | Patch-runtime wrapper/framework |
| `2859222409` | `Bannerlord.UIExtenderEx` / `v2.13.3` | `Bannerlord.UIExtenderEx.dll` 2.13.3.0 | `Bannerlord.UIExtenderEx.SubModule` | Client Gauntlet/UI framework |
| `2859232415` | `Bannerlord.ButterLib` / `v2.11.1` | `Bannerlord.ButterLib.dll` 2.11.1.0; version implementation `Bannerlord.ButterLib.Implementation.1.4.7.dll` | `Bannerlord.ButterLib.ButterLibSubModule`; `Bannerlord.ButterLib.ImplementationLoaderSubModule`; dynamically loaded `Bannerlord.ButterLib.Implementation.SubModule` | Mixed lifecycle/save/crash/UI framework |
| `2859238197` | `Bannerlord.MBOptionScreen` / `v5.12.2` | `MCMv5.dll` 5.12.2.0; implementation `Bannerlord.MBOptionScreen.v1.4.7.dll`; loader 1.0.1.50 | `MCM.MCMSubModule`; `MCM.Internal.MCMImplementationSubModule`; `Bannerlord.ModuleLoader.Bannerlord_MBOptionScreen` | Local settings framework plus client UI |
| `2859251492` | `RBM` / `v4.3.4` | `RBM.dll` 1.0.0.0 plus `RBMAI.dll`, `RBMCombat.dll`, `RBMConfig.dll`, `RBMTournament.dll` | `RBM.SubModule` | Combat, AI, tournament, UI, and XML gameplay; `DedicatedServerType=none`, render required; Native dependency v1.4.6, BirthAndDeath optional |
| `2859265386` | `ImprovedGarrisons` / `v4.2.0.7` | `ImprovedGarrisons.dll` 1.0.0.0 | `ImprovedGarrisons.Main` | Campaign AI/state/UI; `DedicatedServerType=none`, render required |
| `2875093027` | `DismembermentPlus` / `v2.0.8.7` | `DismembermentPlus.dll` 2.0.8.7 | `DismembermentPlus.Main` | Mission combat visual/assets; `DedicatedServerType=none`, render required |
| `2875710877` | `Fourberie` / `v1.4.7.6` | `Fourberie.dll` 1.4.7.6 | `Fourberie.Main` | Campaign, mission, UI, and content gameplay; single-player only; `DedicatedServerType=none`, render required |
| `2881380744` | `Bannerlord.Diplomacy` / `v1.4.7` | loader `Bannerlord.ModuleLoader.Bannerlord.Diplomacy.dll` 1.0.1.50; implementation `Bannerlord.Diplomacy.1.4.7.dll` 1.4.7.0 (`e6bfda7...`) | `Bannerlord.ModuleLoader.Bannerlord_Diplomacy`, then `Diplomacy.SubModule` | Campaign/UI gameplay; single-player only |
| `3614435151` | `UnblockableThrust` / `v1.1.3.1` | `UnblockableThrust.dll` 1.1.3.1 | `UnblockableThrust.UnblockableThrustSubmodule` | Mission combat rule; single-player only; `DedicatedServerType=none`, render required |
| `3720376888` | `PlayerSettlement` / `v7.5.0` | `PlayerSettlement.dll` 7.5.0.0 and `PlayerSettlementFixes.dll` 1.0.0.0 | `BannerlordPlayerSettlement.Main`; `PlayerSettlementFixes.PlayerSettlementFixesSubModule` | Dynamic campaign objects, map/siege UI/content; single-player only |

`DedicatedServerType=none` is affirmative evidence that the original author did not declare that submodule as a dedicated-server element. It must not be overridden by simply forcing the original DLL into the server load list.

## Attribution, source, licenses, and permission record

On 2026-08-08, the server operator reported that every subscribed mod creator gave explicit permission in the main Discord to decompile, modify, and privately redistribute the derivative to this three-person group. That reported authorization is the permission basis for the non-open payloads in this private integration. Before distributing a build, archive the creator, date, message link/ID, allowed scope, attribution request, and any revocation/contact terms in the private release provenance. This audit cannot independently verify Discord messages that were not supplied as artifacts, and it does not extend the reported private permission to a public release.

| Module | Source and authorship evidence | License/permission disposition |
|---|---|---|
| Harmony | Assembly metadata and Workshop description point to [BUTR/Bannerlord.Harmony](https://github.com/BUTR/Bannerlord.Harmony); wrapper credits BUTR and upstream Harmony by Andreas Pardeike | Upstream repository reports MIT. Retain both wrapper and upstream notices. The private package pins this wrapper as the sole provider and excludes Coop's embedded duplicate. |
| UIExtenderEx | Assembly metadata points to [BUTR/Bannerlord.UIExtenderEx](https://github.com/BUTR/Bannerlord.UIExtenderEx), credited to BUTR/shdwp | Upstream repository reports LGPL-3.0. Preserve notices and the required source/relinking terms for any distributed derivative. |
| ButterLib | Assembly metadata points to [BUTR/Bannerlord.ButterLib](https://github.com/BUTR/Bannerlord.ButterLib), credited to BUTR Team | Upstream repository reports MIT. Prefer narrow source adapters with attribution rather than the binary bundle. |
| MCM v5 | Assembly metadata points to [Aragas/Bannerlord.MBOptionScreen](https://github.com/Aragas/Bannerlord.MBOptionScreen), credited to Aragas/mipen | Upstream repository reports MIT. Preserve the MCM/ModLib lineage notices. |
| RBM | Public source identified at [Fellow93/RealisticBattleProject](https://github.com/Fellow93/RealisticBattleProject); Workshop/Nexus identify the RBM Team and Nexus mod 791 | No `LICENSE` file or GitHub license metadata was found in the inspected repository. Use the reported creator permission, record its scope, and retain RBM Team/Fellow93/Philozoraptor attribution. |
| Improved Garrisons | Local payload has no repository metadata; Workshop points to [Nexus mod 688](https://www.nexusmods.com/mountandblade2bannerlord/mods/688/) and publisher profile `76561198044510491` | No license file was present in the Workshop payload. Use and preserve the reported creator permission and creator-requested attribution. |
| DismembermentPlus | Local assembly has no repository metadata; Workshop points to [Nexus mod 2190](https://www.nexusmods.com/mountandblade2bannerlord/mods/2190) and publisher profile `76561197995939660` | No license file was present in the Workshop payload. Preserve the reported permission, the DismembermentPlus creator credit, and credits for any third-party meshes/textures retained. |
| Fourberie | Assembly company is `Spinozart1`; Workshop points to [Nexus mod 2969](https://www.nexusmods.com/mountandblade2bannerlord/mods/2969) and publisher profile `76561199126354534` | No source/license metadata was found in the payload. Use the reported creator permission and retain Spinozart1 attribution. |
| Diplomacy | Assembly copyright is `2020-2025 Diplomacy Team`; source identified at [DiplomacyTeam/Bannerlord.Diplomacy](https://github.com/DiplomacyTeam/Bannerlord.Diplomacy) | Repository `LICENSE` is CC BY-NC-SA 4.0. Mark modifications, attribute the Diplomacy Team, keep the use noncommercial, and satisfy ShareAlike for distributed adapted material unless the creator permission grants separate terms. |
| Unblockable Thrust | Assembly company/copyright is `DDragoonz`; Workshop links [DDragoonz/Bannerlord.UnblockableThrust](https://github.com/DDragoonz/Bannerlord.UnblockableThrust) | No license file was found in that repository. Use the reported creator permission and retain DDragoonz attribution. |
| Player Settlement | Workshop calls this a community-maintained update and expressly credits original author BOTLANNER; publisher profile is `76561198255522595` | No source/license metadata was found in the payload. Preserve both maintainer and BOTLANNER credits and the reported permissions. Asset rights must be tracked separately from C# permission. |

Every private package should include an artifact manifest, this attribution table, modification notes, and the archived permission references. Do not infer that permission to modify C# automatically covers music, voice, fonts, meshes, textures, or other third-party assets; record those asset sources separately.

## Required integration boundary

The following rules apply to every module before its feature guard may be removed:

1. **One patch runtime.** Use the exact pinned `Bannerlord.Harmony` v2.4.2.248 wrapper and its `0Harmony` 2.4.2.0 payload, loaded before Native and Coop. Exclude Coop's embedded copy and every optional module's duplicate. Permit only owner-specific unpatches, never blanket `Harmony.UnpatchAll()`.
2. **One authority per mutation.** Campaign ticks and save mutations run on the server. Mission combat runs on the single authority selected by the existing Coop battle/agent-ownership pipeline. Clients send intent and render accepted results; they do not independently award, spawn, declare war, create a settlement, or decide damage.
3. **One configuration snapshot.** Gameplay settings are loaded by the server, assigned a revision and SHA-256, persisted with the Coop save/database, sent at handshake and late join, and rejected on mismatch. No original MCM screen/provider is active; any future Friend Edition admin UI must submit an authenticated request and cannot directly change gameplay state.
4. **One patch owner per target.** Maintain a runtime patch manifest containing target, owner ID, role, priority, before/after constraints, and expected original signature. Fail closed when a target or owner differs on nightly 1.4.7.
5. **Headless means no UI assembly touch.** Dedicated-server code must not load or statically reference Gauntlet, `ScreenManager`, Input, USER32, WinForms `MessageBox`, rendered scene entities, or crash-upload UI. A runtime `if` after type loading is too late; split assemblies/projects or use reflection behind a proven load gate.
6. **Stable object identity.** Messages and persistence use Coop object IDs/player IDs/clan IDs, never localized display names, `Hero.MainHero`, `MobileParty.MainParty`, `Clan.PlayerClan`, `Agent.Main`, or process-local entity indices as authority keys.
7. **Idempotent commands.** Every state-changing request carries player ID, campaign/session ID, request ID, expected revision, and subject object ID. Retried or duplicated requests return the original result without a second charge, reward, party, war, or settlement.
8. **Late-join snapshot plus delta.** Register all dynamic objects before applying references, then replay ordered deltas. A client that joins during a battle, siege, criminal mission, garrison transfer, or settlement build must converge without replaying the initiating action.
9. **Content agreement.** XML, scenes, sprites, prefabs, meshes, and data files are in a versioned client/server manifest. Reject a peer before campaign load when content or object-registration hashes differ.
10. **Explicit blocked UX.** An unavailable feature exits its menu/dialog cleanly with a reason. It must never leave only “Continue,” trap `PlayerEncounter`, or partially charge/apply hostility.

## Per-module audit

### 1. Harmony — Workshop 2859188632

**Entry and state.** `Bannerlord.Harmony.SubModule.OnSubModuleLoad` bootstraps the wrapper and patches Harmony operations. The wrapper owns global patch/debug/load-order state and an application-tick debug UI, but no campaign `SyncData`.

**Conflict evidence.** Friend Edition references `Lib.Harmony` 2.4.2 in `Coop.Core`, `GameInterface`, `Missions`, and tests. Its historical module also carried a second `0Harmony` binary. The selected nightly's live dedicated-server process instead proved a single working topology in which `Bannerlord.Harmony` v2.4.2.248 loads before Native and Coop. The wrapper's `Bannerlord.Harmony.SubModule.UnpatchAllPrefix(string? harmonyID)` rejects null/blanket unpatches, so Friend Edition adapters use owner-specific cleanup only.

**Disposition.** Stage and activate the exact pinned Workshop wrapper as the sole Harmony provider. Exclude Coop's embedded runtime and every optional module's duplicate, enforce exact wrapper/runtime hashes and load order before startup, and test that the composed package contains exactly one active `0Harmony.dll`. This supersedes the audit-time assumption that the wrapper should be omitted.

### 2. UIExtenderEx — Workshop 2859222409

**Entry and state.** `Bannerlord.UIExtenderEx.SubModule` loads the Gauntlet extension service. State is process-local: `UIExtender.Instances`, VM-mixin `ConditionalWeakTable` entries, prefab/brush/widget caches, and Harmony registrations. There is no save or network state.

**Patch/entry evidence.** Decompiled code touches `TaleWorlds.Engine.GauntletUI` during startup, changes `UIConfig.DoNotUseGeneratedPrefabs`, validates load order through a USER32 message box, and can call `Environment.Exit(1)`. Its patch surface includes `ConstantDefinition.GetValue`, ViewModel construction/refresh/finalization and `ExecuteCommand`, `WidgetPrefab.LoadFrom`, `GauntletMovie.Load`, and widget/brush factories.

**Release disposition.** Stage the exact binary for the private receipt/content manifest only; do not activate it on a client or server. Friend Edition claims no safe rendered-client UIExtenderEx scope. If the optional framework cohort is loaded, the compatibility boundary validates exact fingerprints and method shapes, deregisters extension instances, resets the global prefab flag, purges original framework patches where reversible, and then aborts startup with disable/restart guidance. Any future UI feature must be ported into a reviewed Friend Edition presentation slice rather than enabling this original runtime.

### 3. ButterLib — Workshop 2859232415

**Entry and state.** `ButterLibSubModule` and `ImplementationLoaderSubModule` choose a version implementation; `Bannerlord.ButterLib.Implementation.SubModule` then starts services and subsystems. `ImplementationLoaderSubModule.LoadAllImplementations` can fall back to the newest implementation when an exact game version is unavailable instead of failing closed.

**Persistence.** `MBObjectExtensionDataStore` is injected by `CampaignBehaviorManagerPatch`, is saveable, and serializes extension variables/flags. `ButterLibSaveableTypeDefiner` uses base ID `2002018000`. `BehaviourNamePatch` changes unofficial behavior names. `DistanceMatrix.GeopoliticsBehavior.SyncData` is empty and reconstructs state from campaign events. Local settings live under `Configs/ModSettings/ButterLib/Options.json` and absent keys can enable subsystems by default.

**Patch/entry conflicts.** ButterLib patches broad engine lifecycle surfaces: `MBSubModuleBase` methods including application, game, mission, save and `OnNetworkTick`; `DelayedSubModuleManager` patches base/submodule lifecycle again; `BEWPatch` finalizes ManagedApplication, Module, ScreenManager, ManagedScriptHolder, and Mission ticks; save-system patches touch `CampaignBehaviorBase` construction and definition/container logic. Its UI/crash paths use Input, WinForms/load-order dialogs, screen ticks, and optional upload infrastructure.

**Binary conflict.** The Workshop folder bundles Microsoft.Extensions.* 2.0.0, Serilog file version 3.0.1/Extensions.Logging 3.1/Sinks.File 5, and Newtonsoft.Json 13.0.1-era dependencies. Friend Edition uses Microsoft.Extensions 9.0.x, Serilog 4.2/Extensions.Logging 9/Sinks 6, and Newtonsoft.Json 13.0.3. Copying both sets into one AppDomain risks exact-binding startup failures.

**Release disposition.** Stage the exact binary for the private receipt/content manifest only; do not activate or merge the full runtime. ButterLib detaches the TaleWorlds watchdog during its load hook and exposes delayed-submodule and wrapper subsystems that report `CanBeDisabled=false`, so a complete in-process rollback cannot be proven after load. The compatibility boundary restores the exact DebugManager wrapper and trace state, disables disableable subsystems, purges/asserts original framework patches, guards later lifecycle entry points, and then always aborts with disable/restart guidance. Any needed helper must instead be source-adapted into Friend Edition, with a unique save schema/ID and old-save tests; crash UI, uploads, lifecycle wrappers, distance matrices, and object-extension injection remain inactive.

### 4. Mod Configuration Menu v5 — Workshop 2859238197

**Entry and state.** `MCM.MCMSubModule`, `MCM.Internal.MCMImplementationSubModule`, and the version loader register Global, PerCampaign, and PerSave setting containers plus client UI adapters (`MCM.UI.MCMUIAdapterSubModule`/`MCMUISubModule`). `GlobalSettings<T>.Instance` resolves a process-static provider.

**Persistence/local assumptions.** Global settings are local files under `Configs/ModSettings`; per-campaign keys are derived from `Campaign.Current.UniqueGameId`; per-save settings are held by `PerSaveCampaignBehavior` and serialized by its `SyncData`. `SettingsProviderCampaignBehavior.SyncData` itself is empty. UI actions such as `ModOptionsVM.ExecuteDoneInternal` save locally and can request game exit. Migration code copies/moves/deletes legacy setting folders locally.

**Release disposition.** Original MCM has no peer, host, authorization, revision, or transport concept. Conflicting client JSON could cause different damage, collision, dismemberment, and Diplomacy behavior, so the exact MCM/MCM UI binaries are staged inactive on every role and no original settings presentation is enabled. The compatibility boundary blocks its filesystem migrations before the load pass, guards local providers/per-save/UI mutators, and rejects any deliberate optional-framework activation. Friend Edition's independent authoritative configuration snapshot is the only admitted gameplay settings source; any future admin UI must be a reviewed authenticated Friend Edition surface. Servers must never activate or load the MCM UI/loader.

### 5. Realistic Battle Mod — Workshop 2859251492

**Entry and composition.** `RBM.SubModule` loads `RBMConfig`, applies four Harmony owner IDs (`com.rbmmain`, `com.rbmai`, `com.rbmcombat`, `com.rbmt`), and adds mission behaviors. `OnMissionBehaviorInitialize` can add `UnitStatusMissionView`, `HitStopLogic`, `BattleStatsLogic`, `PlayerArmorStatus`, `AgentPanicFix`, `RBMAIPatchLogic`, `StanceVisualLogic`, `SiegeArcherPoints`, and `StanceLogic`. `OnApplicationTick` touches `ScreenManager`, `MissionScreen`, and Input. `SubModule.xml` also merges extensive item, troop, crafting, ranged, armor, horse, and siege XML.

**Persistence/state.** There is no campaign `SyncData` for posture/AI. `RBMConfig.RBMConfig` reads/writes `Documents\Mount and Blade II Bannerlord\Configs\RBM\config.xml`; `SiegeArcherPoints` reads/writes per-scene XML. Mission state is process-local, including `AgentStances.values`, posture/stamina, drop/change queues, timers, tactics, and `MBRandom` decisions. The XML object mutations become save-sensitive when RBM items/equipment enter campaign state.

**Local-player assumptions.** `RBMTournament` uses `Clan.PlayerClan`, `MobileParty.MainParty`, and `CharacterObject.PlayerCharacter` to choose participants/rewards. AI/posture/UI paths use `Agent.Main`, `IsPlayerControlled`, `Mission.Current.PlayerTeam`, and process-local visual state. Those predicates differ between the host and each client.

**High-risk Harmony targets.** Exact decompiled targets include:

- combat: `Mission.RegisterBlow`, `Mission.CreateMeleeBlow`, `Mission.GetAttackCollisionResults`, `MissionCombatMechanicsHelper.CalculateBaseMeleeBlowMagnitude`, `ComputeBlowDamage`, `ComputeBlowDamageOnShield`, `ComputeBlowMagnitudeMissile`, and `GetAttackCollisionResults`;
- AI/spawn: `Mission.SpawnTroop`, `Mission.OnAgentHit`, `Mission.MeleeHitCallback`, `MissionCombatantsLogic.EarlyStart`, `CampaignMissionComponent.EarlyStart`, sandbox/custom battle spawn-handler `AfterStart`, and `PlayerEncounter.CheckIfBattleShouldContinueAfterBattleMission`;
- tournament: `TournamentFightMissionController.Simulate`/`PrepareForMatch`, `FightTournamentGame.GetParticipantCharacters`/`GetTournamentPrize`, `TournamentGame.UpdateTournamentPrize`, and `TournamentManager.GivePrizeToWinner`;
- object/value loading: `MBObjectManager.CreateMergedXmlFile`/`MergeTwoXmls`, `CraftingOrder.InitializeCraftingOrderOnLoad`, and `DefaultItemValueModel` value/tier methods.

**Direct Coop collisions.** Friend Edition owns `Mission.RegisterBlow` in `Missions.Agents.Patches.AgentDamagePatch`, routes `Agent.RegisterBlow` through battle/tournament/location interceptors, replaces sandbox battle spawning with `CoopBattleMissionSpawnHandler`, clamps `MissionBattleSideSpawnContext.SpawnTroops`, and owns tournament state/reward application. It also patches `TournamentGame.UpdateTournamentPrize`. Letting the original RBM patch set run makes damage order, spawn quotas, battle completion, and prizes dependent on Harmony order.

**Required co-op strategy.** Split RBM into five independently gated slices:

1. XML/content: normalize and source-control the selected XML/assets; calculate an object/content manifest before campaign load; require identical peers. Do not allow configuration to conditionally create a different object graph per client.
2. Combat formulas: call reviewed RBM formula code from the one existing Coop blow authority and replicate the final accepted blow/damage/posture delta. Do not patch `Mission.RegisterBlow` twice.
3. Posture/stamina: key by network agent ID and battle epoch; authority owns values, clients render snapshots/deltas; clear state on mission disposal/reconnect.
4. AI/formations/spawn: only the designated formation/agent authority runs RBM tactics and random choice; orders and reinforcement manifests are replicated. RBM may not call native spawn handlers outside Coop quotas.
5. Tournament: server builds roster, seed, equipment, prize, modifier roll, renown, and reward transaction; clients only simulate/render from the frozen session.

RBM must remain feature-blocked until all enabled slices pass separately and as a combined high-troop battle. Enabling only its UI or successfully merging XML is not full RBM integration.

### 6. Improved Garrisons — Workshop 2859265386

**Entry and state.** `ImprovedGarrisons.Main.InitializeGame` creates static singletons for `GarrisonPartyBehavior`, `GarrisonBehavior`, `SaveBehavior`, `ActivityLogManager`, `GarrisonRecruitmentLogic`, `GarrisonUpgradeLogic`, `GarrisonCostModel`, `GarrisonFoodModel`, and `PartyManager`. `AddBehaviours` adds daily, garrison, party, UI, save, and activity-log behaviors. It also replaces clan-finance, party-size, party-speed, and settlement-food models according to configuration.

`GarrisonPartyBehavior.RegisterEvents` subscribes to session, settlement entry, partial-hourly AI, hourly, daily-party, owner change, party removal/destruction, map-event start, and AI-hourly events. `GarrisonBehavior` handles owner change, hourly, and daily ticks. `Main.OnApplicationTick` mixes UI/Input work with an authoritative mutation: removal of empty mobile garrisons.

**Persistence.** Most campaign `SyncData` methods are empty. `SaveBehavior.OnSaveEvent` delegates to `SaveSystemManager`, and `SaveWriterManager` binary-serializes `IGSaveData.Instance` and `GlobalSettings.Instance` into machine-local sidecars such as `Documents/.../Configs/ImprovedGarrisons/Saves/IGSave_<save>_<uniqueid>.bin`, `GlobalSettings.bin`, and `IGConfiguration_<save>_<uniqueid>.xml`. `IGSaveData` stores a settlement-settings dictionary and activity logs in a static singleton.

**Local-player assumptions and risk.** Settlement settings are keyed in places by localized `Settlement.Name.ToString()` and ownership checks use `Hero.MainHero`, `Hero.MainHero.Clan`, `MobileParty.MainParty`, and Campaign `MainParty`. If each peer subscribes to hourly/daily AI, all three can recruit, upgrade, pay, remove, or create the same garrison party. Sidecar saves then diverge by machine and display-name keys can collide or change with localization.

**Required strategy.** Run garrison AI, recruitment, upgrades, costs, food effects, party creation/removal, and persistence on the server only. Move state into a versioned Coop save/database schema keyed by stable settlement/clan/player IDs. Client UI sends validated order/settings requests and renders authoritative parties, rosters, orders, logs, and costs. Reuse Coop mobile-party, roster, finance, owner-change, and map-event services. No campaign mutation may remain in `OnApplicationTick` on a client.

### 7. DismembermentPlus — Workshop 2875093027

**Entry and state.** `DismembermentPlus.Main.OnMissionBehaviorInitialize` adds `DismembermentPlusMissionLogic`. `DismembermentPlusMissionLogic.OnRegisterBlow` builds a static/current `Dismemberment` with `new Random().Next`, a new `Guid`, process-local agent indices, and mission time. It validates the blow, clears/rebuilds skeleton meshes, creates a dynamic `GameEntity`, adds blood/ragdoll effects, and optionally requests local slow motion.

Settings come from local `GlobalSettings<Settings>` or a JSON path that is hard-coded relative to either `Modules\DismembermentPlus` or Workshop ID `2875093027`. There is no save/network state. Error paths call WinForms `MessageBox.Show`.

**Local-player assumptions.** `DismembermentValidator` and slow-motion code use `Agent.Main`, `IsPlayerControlled`, `IsMainAgent`, campaign battle-death options, and local scene names. Independent `Random` instances mean peers can choose different limbs/outcomes even for the same replicated killing blow.

**Required strategy.** Never initialize this module on the headless server. The designated battle authority validates a killing blow once and emits an idempotent cosmetic event containing battle epoch, event ID, victim network-agent ID, bone/limb enum, direction, and visual seed. Rendered clients apply the event once; missing/late agents queue it until spawn or expire it at mission end. Slow motion is a local preference and cannot change authoritative time. Remove all `MessageBox` paths from runtime code. Mesh/material/assets must be in the client manifest.

### 8. Fourberie — Workshop 2875710877

**Entry and composition.** `Fourberie.Main.OnSubModuleLoad` calls `ListHelper.PopulateTroopList`; `ListHelper` hard-codes Workshop ID `2875710877` paths with optional global XML overrides. `InitializeCampaignBehaviors` adds `FourberieBehavior`, `FourbSafeHouseBehavior`, `FourbEscapeBehavior`, `FourbFightClubBehavior`, `FourbBanditBehavior`, `FourbRecruitableBehavior`, `FourbContactMenu`, `FourbContractBehavior`, and optional add-ons. Mission initialization can add crime, safe-house, fight-club, stealth, and banditry controllers.

It replaces fourteen models: `AgentApplyDamageModel`, `PartyHealingModel` (`FModelDeath`), `ClanFinanceModel`, `CrimeModel`, settlement loyalty/security, party food, settlement access, item discard, diplomacy, military power, party speed, trade price, and party transition.

**Persistence/state.** `FourberieBehavior.SyncData` serializes large dictionaries/lists and object references including town cooldowns, supported bandits, territories/partnerships, player troop backup, gang leader, Fourberie party, criminal base, followers, assigned roles, and mission/crime state. Other behaviors have empty `SyncData`. `FourbSaveDefiner` uses save type ID `4649298` and defines `MapNotifGrudgeData`. Events cover hourly/daily/weekly ticks, settlement entry/exit, map-event start/end, raids, forced supplies/volunteers, hero killed/prisoner, hideouts, war/peace, clan/kingdom destruction, alleys, missions, and contracts.

**Local-player assumptions and conflicts.** The decompiled code contains pervasive `Hero.MainHero`, `MobileParty.MainParty`, `Clan.PlayerClan`, `PlayerEncounter`, `Settlement.CurrentSettlement`, inquiry, Input, and mission-scene assumptions. There are no Harmony patch attributes in the main payload, but model replacement is a larger semantic collision: `FModelDamage` overlaps RBM combat, `FModelDeath` overlaps Friend Edition healing/BirthAndDeath work, `FModelClanFinance` overlaps Improved Garrisons and Coop finance, and its diplomacy/war/raid/forced-goods/recruit flows overlap Coop's server-authoritative menus and Diplomacy.

**Required strategy.** Integrate feature domains rather than the monolith. Server owns criminal standing, grudges, cooldowns, relationships, safehouses, parties, followers, contracts, rewards, crime, and world effects. Replace single `MainHero` fields with player-ID keyed state. Mission/UI code is client-only and submits requests; server validates location, target, cost, cooldown, hostility, and mission epoch, then returns a result that always closes or advances the menu. Random targets/rewards are server-seeded. Reuse Coop raid, goods, recruits, hideout, encounter, hero-death, healing, party, and war services. Unsupported scene missions stay explicitly blocked.

### 9. Diplomacy — Workshop 2881380744

**Entry and state.** The loader selects `Bannerlord.Diplomacy.1.4.7.dll`. `Diplomacy.SubModule.OnSubModuleLoad` creates/enables UIExtender, adds Serilog, applies main patches, and registers widgets. `OnGameStart` applies campaign patches and adds `DiplomaticAgreementBehavior`, `CooldownBehavior`, `MessengerBehavior`, optional `WarExhaustionBehavior`, optional `KeepFiefAfterSiegeBehavior`, `MaintainInfluenceBehavior`, `ExpansionismBehavior`, `CivilWarBehavior`, `UIBehavior`, and `DiplomacyKingdomDecisionPermissionModel`.

**Persistence.** Behavior `SyncData` stores manager objects for cooldowns, rebel factions, agreements, expansionism, messengers, and war exhaustion. `CustomSavedTypeDefiner` base `1984110150` defines managers, messengers, agreements, rebel factions/types, war-exhaustion records, and containers, including `CaravanRaidRecord`. Events include hourly/daily clan, settlements, raids, map-event end, hero prisoner/killed, war/peace, decisions, kingdom/clan destruction, and clan kingdom changes.

**Local-player assumptions.** Campaign/UI paths repeatedly use `Hero.MainHero`, `Clan.PlayerClan`, `PlayerEncounter`, local inquiries, and local MCM `GlobalSettings<Settings>`. Original clients can therefore pay, propose, declare, rebel, or record exhaustion independently.

**Exact patch collisions.** Diplomacy targets `DefaultClanPoliticsModel.CalculateInfluenceChange`, `DiplomaticBartersBehavior.ConsiderWar`, `KingdomDecisionProposalBehavior.ConsiderWar`, `ConsiderPeace`, and `DailyTickClan`, `GauntletBannerEditorScreen.OnDone`, `MakePeaceKingdomDecision.ApplyChosenOutcome`, and `KingdomManager.AbdicateTheThrone`. It also patches caravan/villager loot conditions and village hostile-action menus in `RebelKingdomPatches`. Friend Edition directly owns `KingdomDecisionProposalBehavior.DailyTickClan`, `GauntletBannerEditorScreen.OnDone`, and `KingdomManager.AbdicateTheThrone`; it disables `DiplomaticBartersBehavior.RegisterEvents`, routes influence through `DefaultClanPoliticsModel.CalculateInfluenceChangeInternal`, and already has Separatism clan-join/leave/defection compatibility.

**Required strategy.** Server alone owns agreement, cooldown, messenger, influence, war exhaustion, expansionism, peace/war, and civil-war managers. Use Coop Kingdom/Clan/War/Influence services and immutable revisioned DTOs. Client UI sends proposal/response intent and renders the accepted state. Remove original authoritative Harmony patches rather than trying to order them around Coop. Choose exactly one rebellion engine: map Diplomacy civil wars into the existing Separatism service/IDs or disable Diplomacy civil wars. Never run both. Caravan-raid exhaustion is recorded from the authoritative map-event result once, not from each client observer.

### 10. Unblockable Thrust — Workshop 3614435151

**Entry and state.** `UnblockableThrustSubmodule.OnSubModuleLoad` calls `new Harmony("mod.bannerlord.unblockablethrust").PatchAll()`. The sole gameplay patch is a postfix on `MissionCombatMechanicsHelper.GetDefendCollisionResults`; it changes the `ref bool crushedThrough` result according to strike type, shield, mount, relative velocity, and local `GlobalSettings<UnblockableThrustConfig>`. There is no save data.

**Local-player assumptions/conflict.** `PlayerOnlyAsAttacker` and `PlayerOnlyAsDefender` depend on process-local `Agent.IsPlayerControlled`. All peers may disagree about who is “the player.” The target is in the same melee-resolution pipeline that RBM posture/combat changes through `Mission.CreateMeleeBlow`, `MeleeHitCallback`, and damage helpers. The Workshop description's “load after RBM” is a single-player patch-order convention, not a co-op authority model.

**Required strategy.** Port the calculation as a pure rule called inside the designated Coop hit authority before the accepted blow is serialized. Resolve player identity from Coop ownership, not `IsPlayerControlled`; read the server configuration snapshot; include the crush-through result in the replicated collision/blow record. Do not let every peer recompute from interpolated velocity. Unit-test vanilla, shield, parry/chamber, mounted thresholds, RBM posture-break interaction, and malformed settings before removing the guard.

### 11. Player Settlement — Workshop 3720376888

**Entry and state.** `BannerlordPlayerSettlement.Main.OnSubModuleLoad` loads templates/blacklists and applies Harmony patches. `OnGameStart` adds `PlayerSettlementBehaviour`. `RegisterSubModuleObjects(bool isSavedCampaign)` dynamically re-creates XML-backed objects before campaign load from saved `MetaV3`. `PlayerSettlementFixesSubModule` applies an additional set of null, AI, siege, map, and visual patches.

**Persistence.** `PlayerSettlementBehaviour.SyncData` stores `PlayerSettlement_PlayerSettlementInfo` and `PlayerSettlement_MetaV3`. `CustomSaveableTypeDefiner` covers `MetaV3`, `SettlementMetaV3`, `PlayerSettlementItem`, `OverwriteSettlementItem`, `TransformSaveable`, vectors/matrices, and related document/object data. The Workshop author states that it is safe to add mid-campaign but unsafe to remove after a settlement is built because the save requires its dynamic objects.

**Local-player assumptions.** Construction, rebuild, payment, placement, ownership, encounter, pathing, and UI code repeatedly uses `Hero.MainHero`, `MobileParty.MainParty`, `Clan.PlayerClan`, `PlayerEncounter`, `Settlement.CurrentSettlement`, `MapScreen`, and local inquiries. The original implementation lets the local client choose coordinates/name/culture, deduct gold, register objects, and alter encounter state.

**Exact patch collisions.** The primary/fix assemblies target `BuildingsCampaignBehavior.DailyTickSettlement`, `Building.CurrentLevel`, `MapCameraView.OnBeforeTick`, `MapScreen.HandleLeftMouseButtonClick`, `SettlementVisual.OnMapHoverSiegeEngine`, `SiegeEnginesContainer.DeploySiegeEngineAtIndex`/`RemoveDeployedSiegeEngine`, and `PlayerTownVisitCampaignBehavior.game_menu_town_on_init`. Friend Edition directly patches each of those domains. Additional fixes target `Army.FindBestGatheringSettlementAndMoveTheLeader`, `Army.IsAnotherEnemyBesiegingTarget`, `DisbandArmyAction.ApplyByCohesionDepleted`, `AiMilitaryBehavior.AiHourlyTick`, `BesiegerCamp` assault/position methods, `DefaultMapDistanceModel`, `Town.GetWallLevel`, settlement visual/siege UI, and issue behaviors. Those overlap Coop armies, travel groups, buildings, towns, settlement visuals, and siege services.

The Workshop folder also embeds its own `0Harmony.dll` 2.4.2.0 and an older `MCMv5.dll` 5.11.3. Neither may enter the Friend Edition package.

**Required strategy.** Treat this as the highest-complexity integration. The server validates player/clan authority, placement surface/navmesh, distance/collision rules, culture/template, unique name, cost, and expected campaign revision. It allocates stable object IDs, registers the complete `Settlement`/`Town`/`Village`/building object graph, persists `MetaV3` in a versioned Coop schema, then broadcasts an ordered object snapshot and client visual descriptor. Clients may preview locally but cannot register campaign objects or deduct gold. Late join must register dynamic objects before any party, owner, siege, workshop, or village reference. Split fixes into server-safe campaign patches and client-render patches, removing every target already owned by Coop. Save/restart and siege certification are mandatory before construction is enabled.

## Patch and semantic conflict ownership

The table lists the minimum known conflicts that must have an explicit single owner. “Pipeline” means the exact method may differ, but the result is consumed/changed in the same authoritative transaction.

| Target/domain | Workshop owner(s) | Existing Friend Edition owner/evidence | Required resolution |
|---|---|---|---|
| Harmony runtime and `Harmony.UnpatchAll` | Pinned Harmony wrapper; RBM and Player Settlement embed duplicates | `Lib.Harmony` 2.4.2 throughout Coop | Activate the exact pinned wrapper before Native/Coop; omit every embedded duplicate; owner-ID unpatch only |
| `Mission.RegisterBlow` / blow pipeline | RBMCombat | `Missions.Agents.Patches.AgentDamagePatch`; `BattleBlowInterceptPatch`; tournament/location interceptors | Invoke RBM pure formulas inside Coop authority; one register/replay path |
| `MissionCombatMechanicsHelper.GetDefendCollisionResults` pipeline | Unblockable Thrust; semantic interaction with RBM posture | Coop battle damage/guarded-hit authority | Pure rule inside authority; replicate final flags |
| Battle spawn/reinforcement and completion | RBMAI `Mission.SpawnTroop`, spawn-handler `AfterStart`, `PlayerEncounter.CheckIfBattleShouldContinueAfterBattleMission` | `CoopBattleMissionSpawnHandler`, `MissionSpawnCapacityPatch`, Coop ready/end state | Coop owns manifests/quotas/end; adapt RBM AI around them |
| `TournamentGame.UpdateTournamentPrize` | RBMTournament | `TournamentNativeStatePatches`, `TournamentLifetimeGuardPatches` | Server tournament service is sole state/reward owner |
| `TournamentManager.GivePrizeToWinner` pipeline | RBMTournament | `TournamentSessionHandler` invokes authoritative reward | Port modifier/roll logic into server transaction; never patch original on clients |
| Damage/healing/finance game models | Fourberie `FModelDamage`, `FModelDeath`, `FModelClanFinance`; RBM combat; Improved Garrisons cost/finance | Coop damage, hero health/healing, finance sync | Compose one server/battle model explicitly; no load-order winner |
| `KingdomDecisionProposalBehavior.DailyTickClan` | Diplomacy | `CoopKingdomDecisionProposalBehaviorPatch` | Coop proposal service owns it; call reviewed Diplomacy conditions as policy |
| `GauntletBannerEditorScreen.OnDone` | Diplomacy | `BannerEditorDonePatch` | Coop patch owns network action; Diplomacy only decorates UI |
| `KingdomManager.AbdicateTheThrone` | Diplomacy `RebelKingdomPatches` | `KingdomManagerPatches` | Server Kingdom service owns action; fold rebel validation into request policy |
| Influence calculation pipeline | Diplomacy `DefaultClanPoliticsModel.CalculateInfluenceChange` | `DefaultClanPoliticsModelPatches.CalculateInfluenceChangeInternal` | One server calculation; add Diplomacy terms to the authoritative explained number |
| Barter/war/civil-war lifecycle | Diplomacy managers/patches | `DisableDiplomaticBartersBehavior`, Separatism, Coop war/peace | Coop service owns lifecycle; choose one rebellion engine |
| `BuildingsCampaignBehavior.DailyTickSettlement` | Player Settlement and PlayerSettlementFixes | `BuildingsCampaignBehaviorPatches` | Coop server tick is sole owner; integrate null/custom-settlement handling there |
| `MapCameraView.OnBeforeTick` | Player Settlement | `GameStateManagerPatches` | One client patch with composed read-only placement/time behavior |
| `MapScreen.HandleLeftMouseButtonClick` | Player Settlement | `PlayerPartyTeleportPatches`, `DisableMapClickTimeChange` | One dispatcher ordered by explicit UI mode; no campaign mutation in preview |
| `SettlementVisual.OnMapHoverSiegeEngine` | Player Settlement | `SettlementVisualSiegePatches` | Coop visual patch owns target; add custom-settlement descriptor handling |
| `SiegeEnginesContainer.DeploySiegeEngineAtIndex` / `RemoveDeployedSiegeEngine` | Player Settlement | `SiegeEnginesContainerPatches` and handlers | Coop server request/delta flow is sole owner |
| `PlayerTownVisitCampaignBehavior.game_menu_town_on_init` | Player Settlement | `DisablePlayerTownVisitCampaignBehavior` | Route custom-settlement menu through Coop menu service |
| Army gathering/target/disband/AI | PlayerSettlementFixes | Coop Army/travel-group services | Port only proven null guards into existing server paths |
| Campaign party creation/ticks | Improved Garrisons, Fourberie | Coop mobile-party/map-event/roster services | Server service creates once; clients receive object snapshots/deltas |

At startup, enumerate Harmony patch info for these targets and fail the feature closed if an unexpected owner, duplicate prefix, missing original, or nightly signature drift is detected.

## Persistence and migration requirements

The integrated save/database schema should not serialize decompiler-era static singletons or rely on external per-machine files. At minimum it needs namespaced, versioned records for:

- authoritative mod configuration revision/hash;
- Improved Garrisons settings, orders, mobile-garrison IDs, rosters, logs, and timers;
- Fourberie per-player criminal state, cooldowns, roles, safehouses, followers, contracts, grudges, and spawned-party IDs;
- Diplomacy agreements, cooldowns, messengers, war exhaustion, expansionism, and the selected civil-war mapping;
- Player Settlement dynamic object metadata, transform/template, owner, bound villages, buildings, siege references, and schema/object-registration version;
- any RBM campaign-visible content/version marker and tournament transaction state.

Mission-only data such as RBM posture, Dismemberment events, and Unblockable Thrust collision flags should carry battle/session epochs and be discarded at mission end. They must not leak across the “second battle ready” lifecycle.

Migration rules:

1. Import a legacy mod sidecar only on the server, once, from an explicitly selected path; record its hash and migration ID.
2. Never let each client import its own Improved Garrisons/MCM/RBM JSON/XML.
3. Preserve unknown future fields and reject a newer unsupported schema instead of silently resetting it.
4. Back up the campaign before first migration. Use a new save for the first all-mod E2E campaign because RBM changes the object graph and Player Settlement adds dynamic objects.
5. Once a Player Settlement object has been persisted, block disabling/removing that feature for that save unless a tested destructive migration exists.

### Existing-save Birth & Death assessment

The native nightly 1.4.7 `AgingCampaignBehavior` and `PregnancyCampaignBehavior` register their campaign listeners independently of the life/death option; their daily handlers check `CampaignOptions.IsLifeDeathCycleDisabled` at execution time. On save load, aging also rebuilds its young-hero tracking, while pregnancy persists its in-progress list through `SyncData`. Therefore the guarded Friend Edition baseline can enable NPC aging, pregnancy, births, and natural deaths on an existing standard Sandbox save by applying `difficulty.birthAndDeath=true`; the optional TaleWorlds `BirthAndDeath` module must remain disabled. This is code-level compatibility, not live certification: W-018 still requires a long-running server soak and save/reload proof. Registered co-op player heroes remain protected from the native single-player natural-death/heir flow until that transition has a multiplayer authority design.

## E2E certification matrix

Topology for all network cases is one dedicated server plus the three-person production shape (host/client A/client B where applicable). Run each case from a fresh process, then repeat after server save/restart and after a client late-joins. Assertions include zero unhandled exceptions, zero menu traps, no duplicate mutation, identical authoritative object IDs/revisions, and expected client-visible convergence.

| ID | Area | Scenario | Required assertions |
|---|---|---|---|
| W-001 | Package/bootstrap | Scan server and client packages, start headless, connect two clients | Exactly one `0Harmony.dll`; no obsolete ButterLib dependency DLLs; server never loads Gauntlet/WinForms/USER32/MCM UI; all feature guards report role/status |
| W-002 | Patch ownership | Enumerate all targets in the conflict table on nightly 1.4.7 | Expected signature and one authoritative owner; no unknown patch; per-owner unpatch preserves Coop owners |
| W-003 | Configuration | Give server and both clients conflicting/malformed RBM/MCM/Dismemberment/Unblockable/Diplomacy files | Server snapshot wins; unauthorized edits are rejected; all peers report same revision/hash; late join and restart retain it |
| W-004 | Content | Alter one client's RBM XML or Dismemberment/Player Settlement asset | Handshake rejects before campaign/object load with an actionable manifest mismatch |
| W-005 | Save compatibility | Load an old Friend save, a fresh integrated save, save/reload twice | Old save migrates once or feature stays blocked; no duplicate save IDs/behaviors; state/object counts and hashes remain stable |
| W-006 | Battle lifecycle | Fight two consecutive field battles; all clients press Ready in both | No second-battle crash; RBM mission/static state is disposed; battle epochs differ; no stale agent IDs/events |
| W-007 | High-troop battle | Maximum intended troop cap, reinforcement waves, player casualty/reconnect | Coop quota is never exceeded; RBM AI cannot double-spawn; stutter metrics stay within agreed budget; late/rejoining peer converges |
| W-008 | Blow authority | Matrix of cut/pierce/blunt/missile/horse/shield/parry/chamber/thrust with RBM + Unblockable | Exactly one accepted damage transaction; same health/posture/stamina/crush flags on all peers; no replay double damage |
| W-009 | Dismemberment | Player and AI killing blows for every supported limb; late event and reconnect | Authority emits at most one event; both rendered clients show the same limb/seed; server creates no render entity; no slow-motion time authority change |
| W-010 | Tournament | Join, fight, spectate, simulate, disconnect/rejoin, finish twice defensively | Frozen roster/equipment/prize match; one winner/reward/modifier/renown transaction; RBM and Coop prize patches do not stack |
| W-011 | Ongoing conflict | Client joins an active map conflict; comrade army is sent through pre-battle screens | Eligible parties appear once in manifest and battle; ineligible reasons are returned; RBMAI cannot override Coop membership/end logic |
| W-012 | Siege | Large siege with reinforcements and engine deploy/remove | RBM spawn obeys quotas; engine commands are server-authoritative/idempotent; visuals converge; second siege starts cleanly |
| W-013 | Improved Garrisons | Configure/recruit/upgrade/transfer, allow hourly/daily ticks, capture settlement | One charge and roster mutation; one mobile garrison; stable settlement-ID key; both clients show same orders/log; save/restart continues timers |
| W-014 | Fourberie menus | Raid, demand goods, demand recruits, bandit interaction, safehouse/contract success and rejection | Every branch exits/advances menu; one hostility/reward/crime mutation; no duplicate bandit/follower party; invalid request rolls back cleanly |
| W-015 | Caravan | Attack/loot/resolve caravan with Fourberie and Diplomacy war exhaustion enabled | No crash; one map-event result and loot transaction; one `CaravanRaidRecord`; exhaustion agrees on clients after reconnect |
| W-016 | Diplomacy | Propose/accept/reject war, peace, alliance/agreement, messenger, abdication | Only authorized server action applies; costs/cooldowns/influence exactly once; UI reflects authoritative result; no client prompt on headless path |
| W-017 | Rebellion | Trigger Diplomacy civil-war thresholds with Separatism enabled | The configured single engine owns the rebellion; no duplicate kingdom/clan transfer; stable IDs and save/restart state |
| W-018 | Birth/death/healing compatibility | Leave the optional TaleWorlds `BirthAndDeath` module disabled; require the resolved host `CoopData/mod-config.json` to contain `difficulty.birthAndDeath=true`; advance sufficient campaign time, wound a companion, and observe a battle death | Server logs effective life/death-cycle enabled after CampaignReady, then emits/persists births, pregnancies/deaths and notifications once; clients converge; Fourberie healing remains inactive and cannot stop recovery or double-process death |
| W-019 | Player Settlement creation | Two clients concurrently preview/name/build at same/near positions | Server accepts at most one valid request; unique name/placement/cost enforced; complete object graph and visuals converge; no local orphan |
| W-020 | Player Settlement persistence | Build town/castle/village, bind village, start project, save/restart, late join | Dynamic objects register before references; owner/buildings/projects/parties persist; object IDs are unchanged; disable is blocked for this save |
| W-021 | Player Settlement siege/AI | AI chooses, gathers, besieges, deploys engines, assaults, captures custom fortification | No null/path/circle/engine crash; Coop Army/Siege services remain owners; capture and visuals converge after restart |
| W-022 | Idempotency/faults | Duplicate, reorder, delay, and drop each state-changing request/result; disconnect requester mid-action | No second charge/reward/spawn/war/build; retry returns stored result; transaction either commits fully or leaves no mutation |
| W-023 | UI/framework lifecycle | Attempt to activate the original UIExtenderEx/ButterLib/MCM cohort, then exercise only approved Friend Edition/Diplomacy/Fourberie/Improved Garrisons/placement presentation across reconnect | Optional-framework activation is fingerprint-checked, contained where reversible, and aborts before campaign start with disable/restart guidance; approved UI has one injection/handler, no stale callbacks/mixins, and cannot change server gameplay settings locally |

For campaign-time features, compare an authoritative state digest after each operation and after restart. The digest should cover gold/influence, clan/kingdom/war state, hero health/life/death, parties/rosters, settlements/buildings/sieges, and all integrated mod records.

## Recommended implementation order and release gates

1. **Safety spine:** single Harmony, package manifest, feature-role guards, patch-owner audit, server-backed configuration facade, idempotent request envelope, and headless UI exclusion.
2. **Low-persistence combat slices:** Unblockable Thrust as a pure authoritative rule, then Dismemberment as a replicated cosmetic event.
3. **RBM in slices:** content manifest first, then combat formulas, posture, AI/spawn, tournament, and client UI. Certify each slice before combining.
4. **Diplomacy:** build on the existing Coop Kingdom/Clan/War/Influence/Separatism services; choose the rebellion engine before importing state.
5. **Improved Garrisons:** migrate sidecar state and reuse party/roster/finance authority.
6. **Fourberie by feature domain:** start with server data/actions and ordinary menus; leave scene-heavy missions blocked until mission ownership is specified.
7. **Player Settlement last:** dynamic object registration, persistence, map visuals, armies, and sieges create the largest save and patch surface.
8. **All-mod soak:** new save, server plus both clients, at least two battle/siege/tournament cycles, campaign-time advancement, save/restart, late join, and fault injection. Only after W-001 through W-023 pass should the combined package be called fully co-op routed.

No original gameplay DLL should be enabled in the production module list as an interim shortcut. During development, source slices should compile into Friend Edition behind default-off guards. A guard may move from blocked to enabled only when its corresponding E2E IDs are automated and passing on the exact distributable.

## Artifact fingerprints

These SHA-256 values identify the audited Win64 inputs. A Workshop update changes the audit baseline and requires at least signature/patch/content revalidation.

| Workshop ID | File | SHA-256 |
|---|---|---|
| `2859188632` | `Bannerlord.Harmony.dll` | `4bc2f22e63cfcd677b9de2a9834b103eaacfd8ec0ea4735f4edf66342a6fd01b` |
| `2859222409` | `Bannerlord.UIExtenderEx.dll` | `4f2782840e51391d66b7d701962cb6202969d34c0949b8ec1591964f9c479f99` |
| `2859232415` | `Bannerlord.ButterLib.dll` | `d820692e0c02377f53804e7ec14bb35bd524644613dcdd013c5873729b262172` |
| `2859238197` | `MCMv5.dll` | `7aef3e20ce73eb4409a670f9c2778e2894ed5897a0433bdbe9475e13cac67638` |
| `2859251492` | `RBM.dll` | `1dce47879190c09ac85f097da91abe2b4915455b72031e75917c963bab3f99c2` |
| `2859265386` | `ImprovedGarrisons.dll` | `fedab4041748951282634101871a9c41219bf3f2fa90d4e9cd6a4cbec082ce15` |
| `2875093027` | `DismembermentPlus.dll` | `17abfcc4eba59c15caca1bce194c0791663ed692f21ffe22675da49418a4e79b` |
| `2875710877` | `Fourberie.dll` | `29f6644bcca8d5a3834ee51c72ec75d94214beb76cb1fdcbc2027da0eb544e92` |
| `2881380744` | `Bannerlord.ModuleLoader.Bannerlord.Diplomacy.dll` | `444af0df8f00feb1c3860f0786fa483b9297c99d9f3a51deb22ba0d7855df959` |
| `2881380744` | `Bannerlord.Diplomacy.1.4.7.dll` | `90930a1dfb48c8cf040b8bd2c89156a69838a8dc86b8ed97e0cd8475f2081257` |
| `3614435151` | `UnblockableThrust.dll` | `ff73b80a598bce31e8d620fae84e21c7f633f767f03e5f192e05169425dc83df` |
| `3720376888` | `PlayerSettlement.dll` | `74f9ab2ebc82bdc755886c6ad0802500c2df89015d7c65cd5018c543dbf18119` |
| `3720376888` | `PlayerSettlementFixes.dll` | `a74dd0ed13470240074dfe2a52295a33cb6235a1b6a0ae739fcd0b0d8dd8d1ac` |
