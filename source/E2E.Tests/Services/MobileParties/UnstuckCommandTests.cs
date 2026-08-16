using E2E.Tests.Environment.Instance;
using E2E.Tests.Services.MapEvents;
using GameInterface.Services.GameDebug.Commands;
using GameInterface.Services.MobileParties.Messages.Unstuck;
using Xunit.Abstractions;

namespace E2E.Tests.Services.MobileParties;

/// <summary>Verifies that the unproven generic unstuck mutation remains fail-closed.</summary>
public sealed class UnstuckCommandTests : MapEventTestBase
{
    private const string Unavailable =
        "Unstuck is unavailable until the server can verify a non-exploitable stuck state.";

    private EnvironmentInstance Client => TestEnvironment.Clients.First();

    public UnstuckCommandTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void Unstuck_OnServer_IsRejected()
    {
        string output = null;
        Server.Call(() => output = UnstuckCommand.Unstuck(new List<string>()));

        Assert.Equal("Command can only be run on a client.", output);
    }

    [Fact]
    public void Unstuck_OnClient_FailsClosedWithoutNetworkPacket()
    {
        Client.NetworkSentMessages.Clear();
        Client.InternalMessages.Clear();

        string output = null;
        Client.Call(() => output = UnstuckCommand.Unstuck(new List<string>()));

        Assert.Equal(Unavailable, output);
        Assert.Single(Client.InternalMessages.GetMessages<PlayerUnstuckRequested>());
        var completed = Assert.Single(Client.InternalMessages.GetMessages<PlayerUnstuckCompleted>());
        Assert.Contains("server-stuck-proof-unavailable", completed.Actions);
        Assert.Empty(Client.NetworkSentMessages.Messages);
    }
}
