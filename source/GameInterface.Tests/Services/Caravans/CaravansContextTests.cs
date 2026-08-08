using Common.Util;
using GameInterface.Services.Caravans.Patches;
using System;
using TaleWorlds.CampaignSystem.Party;
using Xunit;

namespace GameInterface.Tests.Services.Caravans;

public class CaravansContextTests
{
    [Fact]
    public void NestedContext_FinalizersRestorePreviousParty_WhenInnerCallThrows()
    {
        var outerParty = ObjectHelper.SkipConstructor<MobileParty>();
        var innerParty = ObjectHelper.SkipConstructor<MobileParty>();
        var failure = new InvalidOperationException("fixture");
        CaravansContext.CurrentParty = null;

        CaravansCampaignBehaviorPatches.HourlyTickPartyPrefix(outerParty, out var rootState);
        CaravansCampaignBehaviorPatches.FindNextDestinationForCaravanPrefix(innerParty, out var nestedState);

        Assert.Same(innerParty, CaravansContext.CurrentParty);
        Assert.Same(failure,
            CaravansCampaignBehaviorPatches.FindNextDestinationForCaravanFinalizer(nestedState, failure));
        Assert.Same(outerParty, CaravansContext.CurrentParty);

        Assert.Null(CaravansCampaignBehaviorPatches.HourlyTickPartyFinalizer(rootState, null));
        Assert.Null(CaravansContext.CurrentParty);
    }
}
