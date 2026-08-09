using GameInterface.Services.ObjectManager;
using GameInterface.Services.WorkshopMods.Fourberie;
using Moq;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieCanonicalStateTests
{
    [Fact]
    public void LateJoinSnapshot_RestoresEveryPrimitiveCollectionAndIsIdempotent()
    {
        global::Fourberie.FourberieBehavior.Reset();
        global::Fourberie.FourberieBehavior._crimeValue[7] = 42;
        global::Fourberie.FourberieBehavior._territoryList.Add("town_A");
        global::Fourberie.FourberieBehavior._assignedGl["gang"] = "hero_A";
        global::Fourberie.FourberieBehavior._townScamTiming["town_A"] = new CampaignTime(123456);
        global::Fourberie.FourberieBehavior._getSomeHelp = true;
        var objectManager = new Mock<IObjectManager>(MockBehavior.Strict).Object;

        Assert.True(FourberieCanonicalState.TryCapture(
            typeof(global::Fourberie.FourberieBehavior).Assembly,
            objectManager,
            out var entries,
            out var fingerprint,
            out var captureFailure), captureFailure);

        global::Fourberie.FourberieBehavior.Reset();
        Assert.True(FourberieCanonicalState.TryApply(
            typeof(global::Fourberie.FourberieBehavior).Assembly,
            objectManager,
            entries,
            out var applyFailure), applyFailure);

        Assert.Equal(42, global::Fourberie.FourberieBehavior._crimeValue[7]);
        Assert.Equal("town_A", Assert.Single(global::Fourberie.FourberieBehavior._territoryList));
        Assert.Equal("hero_A", global::Fourberie.FourberieBehavior._assignedGl["gang"]);
        Assert.Equal(123456, global::Fourberie.FourberieBehavior._townScamTiming["town_A"].NumTicks);
        Assert.True(global::Fourberie.FourberieBehavior._getSomeHelp);

        Assert.True(FourberieCanonicalState.TryCapture(
            typeof(global::Fourberie.FourberieBehavior).Assembly,
            objectManager,
            out _,
            out var restoredFingerprint,
            out var secondFailure), secondFailure);
        Assert.Equal(fingerprint, restoredFingerprint);
    }

    [Fact]
    public void UnknownField_IsRejectedWithoutMutatingExistingState()
    {
        global::Fourberie.FourberieBehavior.Reset();
        global::Fourberie.FourberieBehavior._crimeValue[1] = 5;
        var entries = new[]
        {
            new FourberieStateEntry("_notAudited", FourberieStateValueKind.IntIntDictionary, "", ""),
        };

        Assert.False(FourberieCanonicalState.TryApply(
            typeof(global::Fourberie.FourberieBehavior).Assembly,
            new Mock<IObjectManager>().Object,
            entries,
            out var failure));
        Assert.Contains("unknown field", failure);
        Assert.Equal(5, global::Fourberie.FourberieBehavior._crimeValue[1]);
    }

    [Fact]
    public void ReversibleApply_RestoresPreviousFieldsWhenRevisionCannotCommit()
    {
        global::Fourberie.FourberieBehavior.Reset();
        global::Fourberie.FourberieBehavior._crimeValue[1] = 42;
        var objectManager = new Mock<IObjectManager>(MockBehavior.Strict).Object;
        Assert.True(FourberieCanonicalState.TryCapture(
            typeof(global::Fourberie.FourberieBehavior).Assembly,
            objectManager,
            out var entries,
            out _,
            out var captureFailure), captureFailure);

        global::Fourberie.FourberieBehavior.Reset();
        global::Fourberie.FourberieBehavior._crimeValue[1] = 5;
        Assert.True(FourberieCanonicalState.TryBeginApply(
            typeof(global::Fourberie.FourberieBehavior).Assembly,
            objectManager,
            entries,
            out var transaction,
            out var applyFailure), applyFailure);
        Assert.Equal(42, global::Fourberie.FourberieBehavior._crimeValue[1]);

        Assert.True(transaction.TryRollback(out var rollbackFailure), rollbackFailure);
        Assert.Equal(5, global::Fourberie.FourberieBehavior._crimeValue[1]);
    }
}
