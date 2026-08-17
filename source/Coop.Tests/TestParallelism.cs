using Xunit;

// Same exposure as GameInterface.Tests: classes here assign ModInformation's process-wide
// server/client role and drive production code that reads it, plus Campaign.Current. Their
// [Collection] attributes only serialize those classes against each other — every other collection
// still runs alongside them — so the role can flip under an unrelated test mid-assertion.
//
// Serialize the assembly, matching Coop.IntegrationTests and E2E.Tests. This suite is small enough
// that the wall-clock cost is a few seconds.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
