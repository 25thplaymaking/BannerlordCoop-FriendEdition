using Common.Util;
using GameInterface.Services.Party.Patches;
using Xunit;

namespace GameInterface.Tests.Services.Party;

public class PartyScreenDataPatchesTests
{
    [Fact]
    public void ResetUsingScope_AllowsRosterResetThenRevokesThread()
    {
        Assert.False(AllowedThread.IsThisThreadAllowed());

        try
        {
            PartyScreenDataPatches.ResetUsingPrefix();

            Assert.True(AllowedThread.IsThisThreadAllowed());
        }
        finally
        {
            PartyScreenDataPatches.ResetUsingFinalizer();
        }

        Assert.False(AllowedThread.IsThisThreadAllowed());
    }
}
