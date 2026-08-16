REQUIREMENTS
- Own Player Settlement's five final construction commits with the typed `workshop.player-settlement.construction` command route.
- Admit only configured, registered, current-session snapshot-ready clients; authenticate and derive the server actor and party.
- Capture canonical graph identities before and after the native bridge; publish only an exact, registered graph result with revision, fingerprint, and actor/settlement post-state.
- Treat any failure after the native bridge can mutate campaign state as a session-wide ambiguity and suppress the reply.

MINIMUM COMPONENTS NEEDED
- One typed request/result envelope and one existing `IAuthorityRequestRouter` registration in the Player Settlement handler.
- The existing canonical snapshot publication and object manager for graph and registration validation.
- Focused protocol/diff tests and the existing capability registry/disposition inventory.

REJECTED/NEEDS CLARIFICATION BEFORE ACTION
- No new Player Settlement authority service, generic replay ledger, secondary handshake, runtime client, or deployment work.
- No inferred idempotency for a new construction request: only the router's exact request replay is reused.

PRIMARY RISKS
- The pinned mod can mutate native campaign state before a postcondition is observable. Post-bridge capture, registration, or publication failure therefore aborts the session rather than returning a potentially divergent reply.
- A graph diff can contain valid-looking but unrelated metadata. New roots/parents and target-owned changes are checked structurally before publication.

OVERALL REQUEST AS INTERPRETED BY AGENT/CHAT
- Replace the former direct construction broker/ledger flow with a single authority route while retaining the existing snapshot bootstrap and fail-closed Player Settlement compatibility boundary.
