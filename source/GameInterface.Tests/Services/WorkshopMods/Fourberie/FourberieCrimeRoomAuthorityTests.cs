using Common;
using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Collections;
using System.Reflection;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

[Collection(FourberieRuntimeCollection.Name)]
public sealed class FourberieCrimeRoomAuthorityTests
{
    private sealed class CaptureRuntime : IFourberiePatchRuntime
    {
        public FourberieLocalOperation LastOperation { get; private set; }

        public void PublishIfChanged()
        {
        }

        public bool TrySubmit(FourberieLocalOperation operation)
        {
            LastOperation = operation;
            return true;
        }

        public void RunContractTick()
        {
        }
    }

    private sealed class SliderFixture
    {
        public int UpgradeSlideBar { set { } }
        public int LadsDutySlideBar { set { } }
        public int SlavesDutySlideBar { set { } }
    }

    [Theory]
    [InlineData((int)FourberieOperation.SetCorruptionLevel, 10, 5, true)]
    [InlineData((int)FourberieOperation.SetCorruptionLevel, 4, 0, false)]
    [InlineData((int)FourberieOperation.SetAutoInvestment, 5, 61, true)]
    [InlineData((int)FourberieOperation.SetAutoInvestment, 6, 0, false)]
    [InlineData((int)FourberieOperation.SetLadsDuty, 100, 1000, true)]
    [InlineData((int)FourberieOperation.SetLadsDuty, 101, 0, false)]
    [InlineData((int)FourberieOperation.SetSlavesDuty, 0, 1001, true)]
    public void SettingAuthority_AcceptsOnlyPinnedValuesAndKeys(
        int operation,
        int value,
        int expectedKey,
        bool expected)
    {
        IDictionary crime = new Hashtable { [99] = 7 };

        bool actual = FourberieCrimeRoomAuthority.TrySet(
            crime, (FourberieOperation)operation, value, out var failure);

        Assert.Equal(expected, actual);
        Assert.Equal(7, crime[99]);
        if (expected)
            Assert.Equal(value, crime[expectedKey]);
        else
            Assert.Single(crime);
        if (expected) Assert.Null(failure); else Assert.NotNull(failure);
    }

    [Theory]
    [InlineData((int)FourberieOperation.SetCorruptionLevel, 1, true)]
    [InlineData((int)FourberieOperation.SetCorruptionLevel, 2, true)]
    [InlineData((int)FourberieOperation.SetCorruptionLevel, 3, true)]
    [InlineData((int)FourberieOperation.SetCorruptionLevel, 10, true)]
    [InlineData((int)FourberieOperation.SetCorruptionLevel, 0, false)]
    [InlineData((int)FourberieOperation.SetAutoInvestment, 0, true)]
    [InlineData((int)FourberieOperation.SetAutoInvestment, 5, true)]
    [InlineData((int)FourberieOperation.SetAutoInvestment, 6, false)]
    [InlineData((int)FourberieOperation.SetLadsDuty, 100, true)]
    [InlineData((int)FourberieOperation.SetSlavesDuty, 101, false)]
    public void Protocol_AcceptsOnlyExactCrimeRoomCommandShapes(int operation, int value, bool expected)
    {
        var request = new NetworkRequestFourberieOperation(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 7, 3,
            (FourberieOperation)operation,
            string.Empty, string.Empty, value, Array.Empty<FourberieTroopSelection>());

        Assert.Equal(expected, FourberieOperationProtocol.IsRequestShapeValid(request));
        Assert.False(FourberieOperationProtocol.IsRequestShapeValid(new NetworkRequestFourberieOperation(
            request.SessionId, 8, request.ExpectedRevision, request.Operation,
            "town_a", request.TargetId, request.IntValue, request.Troops)));
    }

    [Theory]
    [InlineData((int)FourberieOperation.SetCorruptionLevel, 2, 5, true)]
    [InlineData((int)FourberieOperation.SetAutoInvestment, 5, 5, true)]
    [InlineData((int)FourberieOperation.SetLadsDuty, 6, 5, false)]
    [InlineData((int)FourberieOperation.StartScheme, 2, 5, false)]
    [InlineData((int)FourberieOperation.StartScheme, 5, 5, true)]
    public void AbsoluteSettings_AcceptOrderedSliderUpdatesWithoutWeakeningOtherCommands(
        int operation,
        long expectedRevision,
        long currentRevision,
        bool expected) =>
        Assert.Equal(expected, FourberieOperationProtocol.CanApplyAtRevision(
            (FourberieOperation)operation, expectedRevision, currentRevision));

    [Theory]
    [InlineData("UpgradeSlideBar", (int)FourberieOperation.SetAutoInvestment)]
    [InlineData("LadsDutySlideBar", (int)FourberieOperation.SetLadsDuty)]
    [InlineData("SlavesDutySlideBar", (int)FourberieOperation.SetSlavesDuty)]
    public void SliderPrefix_SubmitsTypedClientIntentAndNeverRunsOriginal(
        string propertyName,
        int expectedOperation)
    {
        bool previousServer = ModInformation.IsServer;
        IFourberiePatchRuntime previousRuntime = FourberiePatchRuntime.Current;
        var runtime = new CaptureRuntime();
        try
        {
            ModInformation.IsServer = false;
            FourberiePatchRuntime.Current = runtime;
            MethodInfo setter = typeof(SliderFixture).GetProperty(propertyName)?.SetMethod;

            Assert.False(FourberieAuthorityPatches.CrimeRoomSliderConsequencePrefix(
                setter, new object[] { 3 }));
            Assert.Equal((FourberieOperation)expectedOperation, runtime.LastOperation.Operation);
            Assert.Equal(3, runtime.LastOperation.IntValue);
            Assert.Null(runtime.LastOperation.Settlement);
            Assert.Null(runtime.LastOperation.TargetHero);
        }
        finally
        {
            FourberiePatchRuntime.Current = previousRuntime;
            ModInformation.IsServer = previousServer;
        }
    }

    [Theory]
    [InlineData("10", 10)]
    [InlineData("2", 2)]
    [InlineData("4", 0)]
    [InlineData(null, 0)]
    public void CorruptionSelection_MapsOnlyPinnedIdentifiers(string selection, int expected) =>
        Assert.Equal(expected, FourberieAuthorityPatches.CorruptionLevelForSelection(selection));

    [Fact]
    public void ClientReadSnapshot_RestoresMissingOrExistingAutoInvestmentState()
    {
        IDictionary missing = new Hashtable();
        FourberieCrimeRoomSnapshot missingSnapshot = FourberieCrimeRoomAuthority.CaptureReadState(missing);
        missing[61] = 0;
        missingSnapshot.Restore(missing);
        Assert.False(missing.Contains(61));

        IDictionary existing = new Hashtable { [61] = 4 };
        FourberieCrimeRoomSnapshot existingSnapshot = FourberieCrimeRoomAuthority.CaptureReadState(existing);
        existing[61] = 1;
        existingSnapshot.Restore(existing);
        Assert.Equal(4, existing[61]);
    }
}
