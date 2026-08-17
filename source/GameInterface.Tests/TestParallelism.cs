using Xunit;

// These tests drive production code that reads process-wide state — ModInformation's server/client
// role, Campaign.Current, MBObjectManager.Instance, the global Harmony patch set, MessageBroker's
// singleton — and several of them assign that state for the duration of a test. xUnit gives each
// test class its own collection and runs collections in parallel, so one test flipping
// ModInformation.IsServer is observed by every other test running at that instant.
//
// That is not hypothetical. Running the three role-assigning classes alongside the client-side
// authority tests reproduced it in five runs out of eight, sixteen failures at a time, all of the
// form "Authority route <x> cannot submit a client request on the server" — AuthorityRequestRouter
// gates Submit, Poll and HandleResult on the role. In the full suite the same races surface rarely
// and land on a different unrelated test each time (Registry, Tournaments, Fourberie), which is how
// a nightly release build failed on a test that passes locally.
//
// Per-collection [CollectionDefinition(DisableParallelization = true)] does NOT fix this, and five
// such collections had already accumulated here trying to. It only serializes the classes *inside*
// that collection against each other; collections outside it still run concurrently, which is why
// AuthorityRequestRouterTests kept failing despite being in ModInformationRoleCollection.
//
// Serialize the assembly instead, matching what Coop.IntegrationTests and E2E.Tests already do for
// the same reason. Measured cost on the full suite: 83s -> 118s. The existing collections are kept
// so the intent survives if this attribute is ever revisited.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
