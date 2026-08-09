using E2E.Tests.Environment;
using GameInterface.Services.UI.Notifications.Messages;
using SandBox.CampaignBehaviors;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using Xunit.Abstractions;

namespace E2E.Tests.Services.UI;

public sealed class BirthAndDeathNotificationTests : IDisposable
{
    private readonly E2ETestEnvironment testEnvironment;

    public BirthAndDeathNotificationTests(ITestOutputHelper output)
    {
        testEnvironment = new E2ETestEnvironment(output);
    }

    public void Dispose() => testEnvironment.Dispose();

    [Fact]
    public void Server_NaturalDeathNotification_ReplicatesWithoutKiller()
    {
        var victimId = testEnvironment.CreateRegisteredObject<Hero>();
        Hero victim = null;

        testEnvironment.Server.Call(() =>
        {
            Assert.True(testEnvironment.Server.ObjectManager.TryGetObject(victimId, out victim));
        });

        testEnvironment.Server.SimulateMessage(
            this,
            new NotifyHeroKilled(
                victim,
                null,
                KillCharacterAction.KillCharacterActionDetail.DiedOfOldAge,
                showNotification: true));

        var notification = Assert.Single(
            testEnvironment.Server.NetworkSentMessages.GetMessages<NetworkNotifyHeroKilled>());

        Assert.Equal(victimId, notification.VictimHeroId);
        Assert.Null(notification.KillerId);
        Assert.Equal(KillCharacterAction.KillCharacterActionDetail.DiedOfOldAge, notification.Detail);
        Assert.True(notification.ShowNotification);
    }

    [Fact]
    public void Client_AllStillbornBirthNotification_AcceptsOmittedOffspringList()
    {
        var motherId = testEnvironment.CreateRegisteredObject<Hero>();
        var client = testEnvironment.Clients.First();

        client.Call(() =>
        {
            Campaign.Current.AddCampaignBehaviorManager(
                new CampaignBehaviorManager(new CampaignBehaviorBase[]
                {
                    new DefaultNotificationsCampaignBehavior(),
                }));

            Assert.NotNull(Campaign.Current.GetCampaignBehavior<DefaultNotificationsCampaignBehavior>());
        });

        // Empty protobuf repeated fields are absent on the wire and deserialize as null when the
        // message struct uses SkipConstructor. This is the legitimate all-stillborn payload shape.
        var notification = client.EnsureSerializable(
            new NetworkNotifyGivenBirth(motherId, aliveOffspringsIds: null, stillbornCount: 1));

        Assert.Null(notification.AliveOffspringsIds);
        client.SimulateMessage(testEnvironment.Server.NetPeer, notification);
    }
}
