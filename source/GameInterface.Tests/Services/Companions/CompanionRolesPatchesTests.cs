using Common.Util;
using GameInterface.Services.Companions.Patches;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Services.Companions;

public class CompanionRolesPatchesTests
{
    private static Clan CreateClan()
    {
        return ObjectHelper.SkipConstructor<Clan>();
    }

    [Fact]
    public void SamePlayerClan_AllowsPromotion()
    {
        Clan playerClan = CreateClan();

        Assert.True(CompanionRolesPatches.CanPromoteConversationCompanion(playerClan, playerClan));
    }

    [Fact]
    public void AnotherPlayersClan_BlocksPromotion()
    {
        Assert.False(CompanionRolesPatches.CanPromoteConversationCompanion(CreateClan(), CreateClan()));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void MissingClanContext_BlocksPromotion(bool hasConversationClan, bool hasPlayerClan)
    {
        Clan conversationClan = hasConversationClan ? CreateClan() : null!;
        Clan playerClan = hasPlayerClan ? CreateClan() : null!;

        Assert.False(CompanionRolesPatches.CanPromoteConversationCompanion(conversationClan, playerClan));
    }
}
