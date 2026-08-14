# Birth and Death in Friend Edition co-op

Full-surface review of the native birth-and-death module (decompiled from game 1.4.7,
corpus in `work/campaignsystem-full` + `work/sandbox-birthdeath-decompile`) and how each
piece is owned in co-op. Reviewed 2026-08-14; succession design decision by Bryce:
**player heroes can die as long as they have kids.**

## Native surface (reviewed in entirety)

| Native piece | What it does | Randomness / UI / persistence |
|---|---|---|
| `AgingCampaignBehavior` (349 loc) | Growth-stage events (infancy→child 6, teen 14, of-age 18), old-age death rolls, main-hero illness flow, CheatDeath extra lives | `MBRandom` trait inheritance + death roll; `ShowInquiry` illness UI; 2 saved dicts |
| `PregnancyCampaignBehavior` (209 loc) | Daily conception checks for married women 18–45, delivery on due date | `MBRandom`: conception, twins 3%, stillbirth 1%, sex 51% F, **maternal death 1.5%**; saved pregnancy list |
| `DefaultPregnancyModel` | Probabilities; 36-day term (18 fast mode) | pure |
| `MakePregnantAction` | `IsPregnant=true` + `OnChildConceived` | none |
| `HeroCreator.DeliverOffSpring` (347 loc file) | Clones a **new `CharacterObject`** + `new Hero`, full init (parents, clan, culture, body, traits, skills, equipment), fires `OnHeroCreated(isBornNaturally)` | `MBRandom` culture pick for non-player parents |
| `KillCharacterAction` (365 loc) | Death cascade: clan-leader succession, kingdom ruler decisions, gold to leader, army/party disband, governor removal, captivity end, spouse clear, companion removal, obituary; `MakeDead` state+rosters | player death enters SP heir flow via `OnBeforeMainCharacterDied` |
| `DefaultHeroDeathProbabilityCalculationModel` | Pure age curve (55→128) | pure |
| `DefaultAgeModel` | Age thresholds + location age limits | pure |
| `HeirSelectionCampaignBehavior` (SandBox) | SP player-death flow: heir apparents → heir-selection UI or game over; inherits items/equipment/alleys | full SP UI, `GameOverState` |
| `ApplyHeirSelectionAction` | Clan leader change, caravan/workshop transfer, fugitives, army disband, `ChangePlayerCharacterAction` | SP-main-hero centric |
| `DefaultHeirSelectionCalculationModel` | Deterministic heir scoring (male +10, eldest +5, direct descendant +10, top skill +5) | pure |
| Comment behaviors (`CommentPregnancy/Childbirth/CharacterBorn/OnCharacterKilled`) | Encyclopedia log entries + child-born/death map notices, player-clan/family gated | presentation only |
| SandBox `DefaultNotificationsCampaignBehavior` | Quick-info banners for conceived/birth/killed, locally gated on `Hero.MainHero`/`Clan.PlayerClan` | presentation only |

## Co-op ownership (who owns what)

- **Config**: the handshake attests `BirthAndDeath=true`; `UpdateCampaignOptionsHandler` forces
  `CampaignOptions.IsLifeDeathCycleDisabled=false` on every peer. Debug toggling is refused.
- **Lifecycle behaviors run server-only**: `DisableAgingCampaignBehavior` +
  `DisablePregnancyCampaignBehavior` gate `RegisterEvents` to the server; clients never run
  aging/pregnancy ticks. All rolls therefore happen once, on the authority.
- **Birth replication**: `Hero` ctor lifetime patch + `CharacterObjectRegistry` replicate the
  newborn objects; `InitializeNewHero` carries name + both equipments; every other field
  (parents, clan, culture, body, birthday, traits, skills…) flows through `HeroSync`
  AutoSync (which explicitly targets `MakePregnantAction.ApplyInternal`,
  `CheckOffspringsToDeliver`, `DeliverOffSpring`). `IsPregnant` syncs via `ChangePregnant`.
- **Death replication**: field-level (`ChangeState` transpiler, `_deathDay`, `DeathMark`);
  the cascade's constituent actions each have their own sync owners. Clients never run
  `KillCharacterAction` (server-only prefix).
- **Presentation parity (added 2026-08-14)**: conceived/birth/killed banners were already
  replicated via `DefaultNotificationsCampaignBehaviorPatches`; the client handler now also
  mirrors the comment behaviors — pregnancy/childbirth/born/killed **encyclopedia log
  entries** plus child-born and death **map notices**, gated per-client on that client's own
  main hero/clan/family exactly like native. The killed broadcast gate widened from
  player-clan-only to every non-bandit clan so family-in-other-clans notices and
  encyclopedia entries reach clients.
- **Retired on both roles**: `HeirSelectionCampaignBehavior` (SP UI/game-over flow),
  `EducationCampaignBehavior`, `DynamicBodyCampaignBehavior`, `MarriageOfferCampaignBehavior`.

## Player death and succession (NEW — the one design change)

Rule (Bryce, 2026-08-14): **a player hero may die only when their bloodline survives them.**

- Gate (`KillCharacterActionPatches` + `PlayerSuccessionRules`): a registered player hero's
  death — labor mortality, old-age/death-mark collection, murder, removal — is allowed only
  when the hero has **≥1 living child** AND the clan holds an **eligible successor**: alive,
  of age, spawned, not disabled/wanderer/notable, **not another player's hero**; scored by
  the native heir model centered on the victim, the victim's own children outranking other
  clan heroes, string-id tiebreak for determinism. No successor → the death is blocked
  exactly as before (so all-minor heirs keep the hero protected until one comes of age).
- Succession (`PlayerHeroDied` → server `PlayerSuccessionHandler`): after the death fully
  applies (death-mark deferrals re-enter the gate later), the server rebinds the controller
  to the heir — reusing `PlayerPartyRestorer` (finds the heir's party or creates a recovery
  party, the same repair the rejoin flow trusts) and `PlayerManager.ReplacePlayer` —
  broadcasts `NetworkPlayerRegistrationUpdated` to every client, then sends the owning peer
  `NetworkPlayerHeirSucceeded`. The owner's client switches via the join flow's
  `SwitchToPlayer` plus a succession inquiry popup (`HeroInterface.SwitchToHeir`).
- **Offline owner**: the registration still moves; the normal join flow resolves them into
  the heir on their next connect.
- The old aging-tick exemption stands for the *single-player illness/death flow*
  (`DisableAgingCampaignBehavior`), so player old-age death arrives through the death-mark
  path and the same gate.
- Known edge: if the dedicated server's own resolved `Hero.MainHero` is a registered
  player's hero, native takes the `IsHumanPlayerCharacter` branch (no death, no succession) —
  that hero remains effectively protected.

## Verification

- Unit: `BirthAndDeathCampaignBehaviorPatchesTests` (role gates, bloodline gate,
  no-successor block), `PlayerSuccessionHandlerTests` (rebind + broadcast, offline owner,
  unknown controller), `RemotePlayerHeroHandlerTests` (owner switch, wrong-controller
  ignored). Build green; CI runs the suites (loopback broken locally).
- E2E: existing `BirthAndDeathNotificationTests` exercise the widened notification path.
- **Owed live smoke before stable**: an actual player death with an adult heir on the
  server (console-triggerable via a labor/old-age path or a debug kill) → owning client
  switches to the heir, other clients see the rebind, encyclopedia/map notices appear.
