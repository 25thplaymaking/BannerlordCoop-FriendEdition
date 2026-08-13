# Raid Replication Accounting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make raid loot notifications equal the authoritative inventory delta and prevent transient roster registration traffic.

**Architecture:** Compute a summed per-item snapshot before and after the server’s native raid update, then publish only positive authoritative deltas. Keep the client notification roster inside `AllowedThread`, but test both the aggregation and the absence of created/managed roster messages through the existing E2E harness.

**Tech Stack:** C# 10, Bannerlord `ItemRoster`, Harmony, xUnit/E2E.

**Spec:** `docs/coop-next-update-bugsweep.md`

## Global Constraints

- The server remains the only peer that runs `RaidEventComponent.Update`.
- Notification totals must equal the change in the attacking party’s authoritative roster, including multiple equipment-element rows for one `ItemObject`.
- Do not create a scratch `ItemRoster` while measuring a raid delta.
- The client may create only the transient event-argument roster under `AllowedThread`.

---

### Task 1: Correct per-item aggregation and prove the sync boundary

**Files:**
- Modify: `source/GameInterface/Services/MapEventComponents/Patches/RaidEventComponentPatches.cs`
- Modify: `source/E2E.Tests/Services/Villages/VillageHostileActionTests.cs`

**Interfaces:**
- Consumes: `RaidLootedItemsUpdated(MobileParty, IReadOnlyList<(ItemObject Item, int Amount)>)`.
- Produces: `CaptureItems(ItemRoster)` that sums all rows by `ItemObject`, and `GetAddedItems(Dictionary<ItemObject,int>, ItemRoster)` that emits one delta per item.

- [ ] **Step 1: Write the failing multi-row aggregation test**

Build a roster containing two `EquipmentElement` rows for the same item with different modifiers, mutate both rows during an allowed authoritative update, and assert one network loot entry whose amount equals the sum of both row deltas.

- [ ] **Step 2: Run the focused test and verify RED**

Expect the current last-row-wins dictionary and per-row comparison to produce a duplicate or incorrect amount.

- [ ] **Step 3: Implement summed snapshots**

In `CaptureItems`, replace assignment with `TryGetValue` plus addition. In `GetAddedItems`, build one summed `after` dictionary, subtract the summed `before` value once per item, and emit only positive results.

- [ ] **Step 4: Add the no-transient-registration assertion and verify GREEN**

Clear network messages before the real update; after it, assert exactly one `NetworkRaidLootedItemsUpdated` for the delta and zero `NetworkCreateItemRoster`/managed update messages attributable to measurement. Run the raid-focused E2E tests.

- [ ] **Step 5: Commit**

```powershell
git add source/GameInterface/Services/MapEventComponents/Patches/RaidEventComponentPatches.cs source/E2E.Tests/Services/Villages/VillageHostileActionTests.cs
git commit -m "fix(raids): aggregate authoritative loot deltas"
```
