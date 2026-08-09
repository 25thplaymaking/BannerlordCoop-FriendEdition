# Workshop Module Integration — Design

Date: 2026-08-09
Branch: `25vid/workshop-integration`
Status: approved design, pending implementation plan

## Problem

Seven third-party gameplay mods (plus Harmony and three frameworks) are subscribed and, for three
of them, already loaded by the live dedicated server alongside Coop — RBM, ImprovedGarrisons and
Diplomacy run today with no co-op awareness at all. Their campaign and mission mutations happen
locally on whichever machine executes them, so every one is a latent desync.

The existing `workshop-integration` branch carries ~13k lines that establish *identity* (which
module, pinned to which bytes) and *admission* (a client may not connect with a mismatched suite),
plus per-module compatibility guards. What it does not have is routing: the server never learns
that a module mutated something, and a client has no way to ask it to.

This design makes module integration a repeatable pattern rather than seven bespoke efforts, so
that the eighth mod — and the hundredth — costs a table entry and a validator instead of a project.

## Review findings (2026-08-09)

The 284 test facts written for this work had never been executed, because VSTest cannot reach its
testhost on this machine (IPv4 loopback is broken system-wide). They run fine under xunit's
in-process console runner:

| Suite | Run | Pass | Fail |
|---|---|---|---|
| `GameInterface.Tests` WorkshopMods | 233 | 227 | 6 |
| `E2E.Tests` WorkshopMods | 51 | 46 | 5 |

**All five E2E failures share a single root cause, and it is a live-fire defect, not a test
artifact.** One class — `DiplomacySharedMutationAuthorityPatch` — declares *uncategorised* Harmony
patches whose `TargetMethods()` resolve to nothing when Diplomacy is absent.
`Harmony.PatchAllUncategorized` then throws inside `GameInterface.PatchAll()`, which aborts **Coop's
entire patch application** — not merely that module's. Every E2E test that constructs an
`E2ETestEnvironment` dies in its constructor, which is why the PlayerSettlement failures report the
Diplomacy patch in their stack traces. Any client lacking Diplomacy, or on a different Diplomacy
build, takes the same abort. The branch cannot ship until it is fixed, and the fix is the same
gating this design needs anyway.

Three sibling classes — `ImprovedGarrisonsAuthorityPatches`, `FourberieAuthorityPatches`,
`PlayerSettlementAuthorityPatches` — are built the same way but are **unverified**: Harmony aborted
at the first failure and never reached them. Assume they share the flaw until increment 0 proves
otherwise.

Of the six unit failures, two are protobuf round-trips that fail under the console runner for
environmental reasons (the same way unrelated `Serialization` tests do); four are real and in
scope: suite-receipt runtime discovery, two ImprovedGarrisons adapter-inventory/patch-purge cases,
and Diplomacy's authoritative revision publisher.

Routing shape as built today: outbound only. Each adapter has a snapshot request/response pair
(`NetworkRequestDiplomacySnapshot` → `NetworkDiplomacySnapshot` → apply) plus a mutation
transaction and codec. There is no inbound intent path, which is why every adapter's only safe
behaviour is to block the local mutation.

## Principle

Use Coop's own mechanisms. Do not build a parallel transport, a parallel change-tracker, or a
parallel test harness. Every element below already exists and is used by native Coop services; the
work is extending them to types resolved at runtime instead of at compile time.

## The contract

A mod author — or we, on their behalf — writes one class:

```csharp
interface IWorkshopModule
{
    string ModuleId { get; }              // "Bannerlord.Diplomacy"
    ulong  WorkshopId { get; }            // 2881380744
    ModuleFingerprint Fingerprint { get; }// assembly name, version, SHA-256
    string PatchCategory { get; }         // applied only when the module is live

    void RegisterSync(AutoSyncRegistry registry);      // replicated members
    IEnumerable<ModuleAction> Actions { get; }         // intents to route
}
```

`ModuleAction` names the target method, the validator that runs on the server, and the apply. That
is the whole surface a new mod must implement.

## Architecture

**Registration.** `WorkshopModuleRegistrar` runs at `GameInterface` load. For each registered
module: if config enables it *and* `WorkshopModuleCatalog` finds it present at the pinned
fingerprint, apply its Harmony category, register its `IAutoSync`, and wire its handlers.
Otherwise do nothing at all. A module that is absent, mismatched, or disabled contributes zero
patches — which removes the `PatchAll` abort by construction.

**State tracking → `IAutoSync`.** `AutoSyncRegistry.AddProperty(PropertyInfo)` and
`AddField(FieldInfo)` take reflection objects, so a mod's members register exactly as
`MapEventPartySync` registers `MapEventParty`'s. The generated property-set prefix publishes on the
server and refuses the write on a client: that *is* the change tracking the server needs.

**Intent → Coop's four-part service shape.** Identical to `TryToGetAwayPatches` →
`ClientBattleRetreatHandler` → `ServerBattleRetreatHandler` → `BattleRetreatInterface`:

1. Harmony prefix intercepts the module action on the client and suppresses the local write.
2. It publishes an internal message carrying the acting party/hero explicitly (never `MainParty`,
   which the headless server does not have).
3. The client handler sends a `Network*` request carrying ids only — never outcomes.
4. The server handler re-derives ownership from the peer, validates, and applies with patches live;
   the resulting state replicates through AutoSync and the registries.

**Identity → `AutoRegistryBase<T>`** for module objects needing wire ids, as `ArmyRegistry` does.

**Config.** A per-module block in `mod-config.json`, read through `ModConfigProvider.ModOptions`,
made server-authoritative by the existing `ModConfigAuthority` and delivered in the existing
handshake (`NetworkModuleVersionsValidate`). Enabling a module is a server decision; a client
cannot opt itself in or out.

**Snapshot codecs.** Where AutoSync covers the state, the per-module snapshot pair is deleted. It
is retained only where a module genuinely needs bulk transfer that the campaign/save transfer does
not already provide. This is a net deletion.

## Testing

`WorkshopModuleTestBase` gives every module the same four gates, derived from its `IWorkshopModule`
declaration rather than hand-written per mod:

1. **Absent** — module not installed: Coop loads, patches apply, nothing throws.
2. **Disabled** — installed but off in config: no patches applied, no sync registered, inert.
3. **Authority** — server may perform the mutation; every client is refused.
4. **Round trip** — client intent reaches the server, is applied once, and converges on all peers.

A new mod earns its coverage by filling the table. Per-action behavioural tests are written on top,
per module, in `E2E.Tests/Services/WorkshopMods/<Module>/`.

All suites run under the xunit in-process console runner; `dotnet test` remains unusable on this
machine. CI (`pull_request.yml`, 8 E2E shards) is the gate of record.

## Increments

From increment 1 onward, each increment ends with a module that actually works in co-op. Nothing
lands in a "converted but inert" state — a session that plays vanilla tests nothing. Increment 0 is
the exception and is a crash fix, not a conversion: without it the module does not load at all.

0. **Unblock.** Fix the `PatchAll` abort in `DiplomacySharedMutationAuthorityPatch` and audit the
   three unverified sibling classes for the same flaw; fix the four real unit failures; confirm the
   two protobuf failures are runner-environmental. All 284 facts green.
1. **Contract, delivered by Diplomacy.** Build `IWorkshopModule`, the registrar, config gating and
   `WorkshopModuleTestBase` *by routing Diplomacy end-to-end*. The abstraction is proven by a real
   consumer before six more depend on it. Diplomacy works in co-op at the end of this increment.
2. **ImprovedGarrisons** — the other campaign mod already live on the server.
3. **RBM**, **DismembermentPlus**, **UnblockableThrust** — mission-side, sharing the combat
   authority policy already written.
4. **Fourberie** — blocked on the pending Steam update; re-fingerprint and re-pin first.
5. **PlayerSettlement** — dynamic campaign objects; largest identity surface, hence last.
6. **Merge** to `development`.

## Non-goals

- Framework modules (UIExtenderEx, ButterLib, MCM) stay staged-inactive. They are client-local UI
  and settings infrastructure with no shared state to route.
- No public redistribution. The private-permission basis is unchanged, and the inherited
  `Nightly Release` workflow stays disabled.
- No attempt to make un-routed actions "work anyway". An action not in a module's `Actions` table
  is refused on clients, as today. Coverage is explicit and checkable.

## Risks

- **Coverage is per action, not per mod.** "Diplomacy is routed" means the actions in its table are
  routed. Each module's table must be enumerated from its decompiled surface and reviewed, or
  "done" becomes unfalsifiable.
- **RBM touches combat every frame.** It is the one mod whose mutations may not fit the
  intent/apply shape; if per-frame combat state proves unroutable, the honest outcome is that RBM
  stays server-side-authoritative with client visuals only, and that is recorded rather than faked.
- **Fourberie's own surface is hostile.** Its initializer replaces fourteen campaign models, several
  overlapping Coop-owned authority. Routing may be rejected in favour of permanent blocking; that
  decision belongs in increment 4, on evidence.
