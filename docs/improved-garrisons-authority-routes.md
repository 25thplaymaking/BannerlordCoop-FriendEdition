# Improved Garrisons authority routes

REQUIREMENTS
- Route setting mutations through `workshop.improved-garrisons.setting` and management mutations through `workshop.improved-garrisons.management` with authenticated, session/revision-bound authority headers.
- Retain the existing snapshot bootstrap and use its canonical revision/hash as the commit barrier.
- Derive actor, town ownership, party and target identities on the server; publish only verified canonical mutations.
- Preserve rollback before publication; isolate campaign peers after ambiguous native mutation or publication failure.

MINIMUM COMPONENTS NEEDED
- The existing compatibility handler, canonical snapshot codec, authority router, and two typed route handles.
- Typed request/result envelopes carrying the existing operation/setting payload plus authority headers and canonical digest/post-state data.

REJECTED/NEEDS CLARIFICATION BEFORE ACTION
- No new service, framework, persistent replay store, generic watermark, additional hostile handshake, deployment, push, or runtime client.
- A native operation with no stable reflected postcondition remains unavailable instead of returning an unverified success.

PRIMARY RISKS
- The pinned mod's reflected party/order APIs may not expose a stable getter. Native changes after that point are ambiguous and must isolate rather than retry.
- Snapshot publication is the cross-client transaction boundary; its hash/revision must be captured and validated before acceptance.

OVERALL REQUEST AS INTERPRETED BY AGENT/CHAT
- Replace the legacy Improved Garrisons setting and management request flow with exactly two typed authority command routes while preserving bootstrap snapshot behavior and the pinned reflection contract.
