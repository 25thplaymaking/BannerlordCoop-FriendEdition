using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.MobileParties.Handlers;
using GameInterface.Services.MobileParties.Messages;
using GameInterface.Services.ObjectManager;
using Moq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using Xunit;

namespace GameInterface.Tests.Services.MobileParties;

/// <summary>
/// Unit tests for the client send-side of mercenary-hire replication in
/// <see cref="MercenaryHireHandler"/>: a client-side hire is relayed to the server as a
/// <see cref="HireMercenaries"/> request carrying the resolved ids, troop count and gold cost.
/// </summary>
public class MercenaryHireHandlerTests
{
    [Fact]
    public void CanApplyMercenaryHire_ValidCurrentStockAndGold_ReturnsTrue()
    {
        int count = 5;
        int unitPrice = 50;
        int goldAmount = MercenaryHireHandler.GetMercenaryHireGoldAmount(count, unitPrice);

        bool canApply = MercenaryHireHandler.CanApplyMercenaryHire(
            count,
            goldAmount,
            serverHeroGold: 1000,
            unitPrice: unitPrice,
            availableTroopMatches: true,
            availableMercenaries: 10);

        Assert.True(canApply);
    }

    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 4)]
    public void CanApplyMercenaryHire_StaleConversationStock_ReturnsFalse(bool availableTroopMatches, int availableMercenaries)
    {
        int count = 5;
        int unitPrice = 50;
        int goldAmount = MercenaryHireHandler.GetMercenaryHireGoldAmount(count, unitPrice);

        bool canApply = MercenaryHireHandler.CanApplyMercenaryHire(
            count,
            goldAmount,
            serverHeroGold: 1000,
            unitPrice: unitPrice,
            availableTroopMatches,
            availableMercenaries);

        Assert.False(canApply);
    }

    [Fact]
    public void CanApplyMercenaryHire_InsufficientCurrentServerGold_ReturnsFalse()
    {
        int count = 5;
        int unitPrice = 50;
        int goldAmount = MercenaryHireHandler.GetMercenaryHireGoldAmount(count, unitPrice);

        bool canApply = MercenaryHireHandler.CanApplyMercenaryHire(
            count,
            goldAmount,
            serverHeroGold: goldAmount - 1,
            unitPrice: unitPrice,
            availableTroopMatches: true,
            availableMercenaries: 10);

        Assert.False(canApply);
    }

    [Fact]
    public void GetMercenaryHireGoldAmount_Overflow_ReturnsZero()
    {
        Assert.Equal(0, MercenaryHireHandler.GetMercenaryHireGoldAmount(int.MaxValue, 2));
    }

}
