using E2E.Tests.Environment;
using E2E.Tests.Environment.Instance;
using GameInterface.Services.WorkshopMods.PlayerSettlement;
using Xunit.Abstractions;

namespace E2E.Tests.Services.WorkshopMods.PlayerSettlement;

/// <summary>
/// Role-isolated policy checks. These do not claim a physical Bannerlord UI construction test:
/// adapter v1 intentionally denies construction and proves that denial is identical in the host
/// and every client process while empty late-join state converges by revision/fingerprint.
/// </summary>
public sealed class PlayerSettlementAuthorityE2ETests : IDisposable
{
    private E2ETestEnvironment TestEnvironment { get; }
    private EnvironmentInstance Server => TestEnvironment.Server;
    private IEnumerable<EnvironmentInstance> Clients => TestEnvironment.Clients;

    public PlayerSettlementAuthorityE2ETests(ITestOutputHelper output)
    {
        TestEnvironment = new E2ETestEnvironment(output);
    }

    public void Dispose() => TestEnvironment.Dispose();

    [Fact]
    public void CampaignLifecycle_IsHostOnly_WhileConstructionIsDeniedEverywhere()
    {
        Server.Call(() =>
        {
            Assert.True(PlayerSettlementAuthorityPatches.ServerPersistencePrefix());
            Assert.False(PlayerSettlementAuthorityPatches.BlockedPrefix(null));
        });

        foreach (var client in Clients)
        {
            client.Call(() =>
            {
                Assert.False(PlayerSettlementAuthorityPatches.ServerPersistencePrefix());
                Assert.False(PlayerSettlementAuthorityPatches.BlockedPrefix(null));
            });
        }
    }

    [Fact]
    public void EmptyLateJoinState_ConvergesAtEveryClient()
    {
        var fingerprint = PlayerSettlementStateCodec.ComputeHash(
            Array.Empty<PlayerSettlementStateEntry>());

        foreach (var client in Clients)
        {
            client.Call(() =>
            {
                var gate = new PlayerSettlementRevisionGate();
                Assert.Equal(PlayerSettlementRevisionDecision.Apply, gate.Evaluate(0, fingerprint));
                Assert.True(gate.Commit(0, fingerprint));
                Assert.Equal(PlayerSettlementRevisionDecision.AlreadyApplied,
                    gate.Evaluate(0, fingerprint));
            });
        }
    }
}
