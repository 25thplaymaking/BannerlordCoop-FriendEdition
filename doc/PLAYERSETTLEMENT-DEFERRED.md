# PlayerSettlement construction — deferred (and why)

**Status (2026-08-10):** PlayerSettlement **loads and displays** in co-op — existing settlements,
its save type definitions, and read-only UI work. **Founding a new settlement is intentionally
deferred**, and its construction entry points stay feature-blocked
(`PlayerSettlementBehaviour.BuildTown/BuildCastle/BuildVillage/Overwrite/Rebuild`,
`PlayerSettlementBuildVM.ExecuteCreatePlayerSettlement`, `SaveHandler.Save*`).

## Why it can't be a simple action-route

Every other routed action (Diplomacy Donate Gold, Fourberie recruiting) is
*client intent → server applies → result syncs through existing funnels*. PlayerSettlement
construction can't fit that shape. From the mod's own flow (adapter comment in
`PlayerSettlementAuthority.cs`):

> "Player Settlement performs construction as a single local UI operation. It chooses a random
> template and ID, reads MainHero/MainParty, **loads generated XML into MBObjectManager**, mutates
> many campaign behaviors, and **finally saves/reloads the game**."

A new settlement is an `MBObjectManager` object **registered at load time**. The mod materialises it
by generating XML and then **restarting the campaign**. You cannot restart the campaign for one
player in a live co-op session, and replaying only the tail of that sequence on the host is not
atomic. So PlayerSettlement's own construction path is fundamentally incompatible with live co-op.

## The plan when we build it (our own create-settlement, LAST in the program)

Do **not** try to route PlayerSettlement's `BuildTown`. Instead build a **Frontir-owned,
co-op-native settlement creation** that avoids the save+reload entirely:

1. **Runtime object creation, not XML+reload.** Create the `Settlement` (an `MBObjectBase`) and its
   component graph directly at runtime on the **server**, so it flows through Coop's existing
   `MBObjectBase` create funnel (`AutoRegistryHandler` → `NetworkCreateInstance`) and registers on
   every client under the same id — the same mechanism that already syncs parties/rosters. No
   `MBObjectManager` load-time registration, no restart.
2. **Vote-to-pause coordination.** Founding a settlement is a heavy, world-shaping act. Gate it
   behind a group **vote-to-pause**: a player proposes it, peers vote, and on pass the server does
   the authoritative create + broadcast while time is paused, then resumes. (This also gives us a
   reusable "pause the session for a big operation" primitive.)
3. **Ownership + validation** server-side exactly like the other routed actions (peer→hero).

## Why it's deferred to last

- It needs a **vote/coordination flow across multiple live clients** to build and verify — and
  there were no co-op testers available the day this was scoped.
- It's the single largest remaining mod feature; the **launcher and Discord bot** are self-contained,
  higher-value, and unblock actually distributing/playing the working session now.

Tracked in `STATUS.md` as the final program item. Until then, PlayerSettlement is present and
loads; players just can't found new settlements.
