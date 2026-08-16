# Rebellions & Demographics v3.0.1 co-op surface inventory

The audit target is `RebellionsAndDemographics.dll`, SHA-256
`115ca5f26eaa50f9ce6fa4ac88dc2b65b94be1eb4ff27a895ea29982983463a8`.
IL inspection found a global `Harmony.PatchAll` owner (`com.rebellions.and.demographics`), 44
campaign behavior registrations from `SubModule.OnGameStart`, and mission behavior registration
from `OnMissionBehaviorInitialize`. The adapter removes that owner and suppresses both entry
points before allowing the server-only list below.

| Upstream reachable family | Disposition | Reason / owner |
| --- | --- | --- |
| `PopulationBehavior`, `PlagueBehavior`, `RebellionCoreBehavior`, `RecruitmentLimiterBehavior`, `DemographicsBehavior` | Server callback + persisted original behavior state | Constructed as one preflighted batch on the authoritative host. The adapter observes population and plague state daily and publishes a canonical fingerprinted snapshot. |
| `RebellionCoreBehavior.ForcePlayerIntervention(Hero,int)` | Typed server command | `workshop.rebellions-demographics.intervention` authenticates the controller, carries the foreign clan-leader target and ally count, recomputes native bribe/influence predicates against an AI-only pool, and requires the target-led new-kingdom postcondition before accepting. |
| `RebellionCoreBehavior.ProcessRebelDefeat`, `TriggerPlayerUltimatum` | Server-issued prompt lease + typed choice command | `workshop.rebellions-demographics.choice` binds session, owner, lease generation, choice, and snapshot revision. It tombstones one-shot answers before the result, takes deterministic negative branches on expiry/no owner, and isolates every campaign peer if a native mutation or publication becomes ambiguous. |
| `SchismCultureBehavior`, `ShadowGarrisonBehavior`, `GovernmentStabilityBehavior` | Omitted host-local player-context behavior | Their implementations read `Clan.PlayerClan`, `Hero.MainHero`, or `Settlement.CurrentSettlement`; a host-only registration would apply a connected player feature to the host character. |
| `CorruptionCampaignBehavior`, `StrikeCampaignBehavior`, `PostStrikeBehavior` | Omitted local menu/input behavior | They are entered primarily through `AddGameMenuOption` or local callback surfaces; no server-safe multi-player action contract is supplied by the original binary. |
| Adapter lifecycle/readiness and host behavior receipt | Bootstrap query + canonical resync | `workshop.rebellions-demographics.snapshot` publishes host behavior identity, structured population cultures, plague state, active prompt leases/tombstones, retained intervention watermarks, and a deterministic SHA-256 fingerprint; clients are incapable of enabling the capability before this trusted receipt applies. |
| `PlagueDialogs`, `SchismMenuBehavior`, `DiplomacyDialogBehavior`, `MainVillageMenuBehavior` | Disabled presentation | Their only trigger is a local game menu/dialogue callback. The upstream entry point is not instantiated; no menu callback may mutate a client campaign. |
| `Council*`, `WarCouncil*`, `LocalCouncil*`, `FieldCommand*`, `Incognito*`, `StrikeConversationBehavior` | Disabled local-only UI / input | These paths require the original local Gauntlet/inquiry/input state and do not expose a host-safe command contract in the audited binary. |
| `MilitiaInspectionMission`, `TrainingMissionLogic`, `PlagueMissionLogic`, `CouncilMissionLogic`, `WarCouncilMissionLogic`, `ScavengerMissionController`, `Views.*` | Disabled local mission UI | Each is entered from a local mission/UI behavior and may create local mission state; the guarded mission lifecycle never injects them. |
| `Pendraic*`, `RumorsOfPendraicBehavior`, `StoryModeKiller`, `VanillaQuestSilencer`, `EmpireTimelineBehavior`, `Olek/Arenicos/EmpireSchism` cheats | Disabled story/cinematic domain | Fixed story/cutscene and cheat paths require local story/cinematic state and are not multiplayer campaign authority. |
| `HybridReality.*`, `GemmaClient`, `OpenSettlementMenuOnExitBehavior` | Disabled no-render/client service | These require local input/render/network client services and cannot exist on a dedicated no-render host. |

The module remains active on both peers. "Disabled" above means that the named local-only feature
is never instantiated after the upstream lifecycle is replaced; it does not mean the package or
the server-authoritative campaign surface is held or inactive.
