using GameInterface.Services.ObjectManager;
using GameInterface.Services.TroopRosters.Interfaces;
using GameInterface.Services.TroopRosters.Logging;
using Moq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;

namespace GameInterface.Tests.Services.TroopRosters;

public class TroopRosterInterfaceDeltaTests
{
    [Fact]
    public void PackTroopRosterDelta_RemovedTroop_NormalizesResidualXpToZero()
    {
        var character = new CharacterObject();
        var current = new TroopRoster
        {
            data = new[]
            {
                new TroopRosterElement(character) { Number = 0, Xp = 125 },
            },
        };
        var initial = new TroopRoster
        {
            data = new[]
            {
                new TroopRosterElement(character) { Number = 1, Xp = 125 },
            },
        };

        var objectManager = new Mock<IObjectManager>();
        string characterId = "removed-troop";
        objectManager
            .Setup(manager => manager.TryGetIdWithLogging(character, out characterId))
            .Returns(true);

        var subject = new TroopRosterInterface(
            objectManager.Object,
            new Mock<ITroopRosterLogger>().Object);

        var delta = subject.PackTroopRosterDelta(current, initial);

        var element = Assert.Single(delta.Data);
        Assert.Equal(characterId, element.CharacterId);
        Assert.Equal(-1, element.Number);
        Assert.Equal(-125, element.Xp);
    }
}
