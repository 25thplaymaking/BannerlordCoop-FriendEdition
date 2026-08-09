using GameInterface.Services.WorkshopMods.Fourberie;
using ProtoBuf;
using System;
using System.IO;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieStateCodecTests
{
    private const string ConfigHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void Snapshot_ProtobufRoundTrip_PreservesRevisionAndState()
    {
        var entries = new[]
        {
            new FourberieStateEntry("_crimeValue", FourberieStateValueKind.IntIntDictionary, "", ""),
            new FourberieStateEntry("_crimeValue", FourberieStateValueKind.IntIntDictionary, "7", "42", 1),
        };
        var state = new NetworkFourberieState(
            FourberieCompatibilityManifest.AdapterVersion,
            9,
            ConfigHash,
            FourberieStateCodec.ComputeHash(entries),
            entries);

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, state);
        stream.Position = 0;
        var restored = Serializer.Deserialize<NetworkFourberieState>(stream);

        Assert.True(FourberieStateCodec.TryValidate(restored, out var failure), failure);
        Assert.Equal(9, restored.Revision);
        Assert.Equal("42", restored.Entries[1].Value);
    }

    [Fact]
    public void CanonicalHash_IsIndependentOfWireEnumerationOrder()
    {
        var first = new FourberieStateEntry("_crimeValue", FourberieStateValueKind.IntIntDictionary, "1", "2", 1);
        var second = new FourberieStateEntry("_territoryList", FourberieStateValueKind.StringList, "", "town", 1);

        Assert.Equal(
            FourberieStateCodec.ComputeHash(new[] { first, second }),
            FourberieStateCodec.ComputeHash(new[] { second, first }));
    }

    [Fact]
    public void TamperedPayload_IsRejectedBeforeApply()
    {
        var entries = new[]
        {
            new FourberieStateEntry("_crimeValue", FourberieStateValueKind.IntIntDictionary, "1", "2", 1),
        };
        var state = new NetworkFourberieState(
            FourberieCompatibilityManifest.AdapterVersion,
            0,
            ConfigHash,
            FourberieStateCodec.ComputeHash(entries),
            entries);
        state.Entries[0].Value = "999";

        Assert.False(FourberieStateCodec.TryValidate(state, out var failure));
        Assert.Contains("fingerprint mismatch", failure);
    }

    [Fact]
    public void OversizedEntryCount_IsRejected()
    {
        var entries = new FourberieStateEntry[FourberieStateCodec.MaximumEntries + 1];
        Array.Fill(
            entries,
            new FourberieStateEntry("_crimeValue", FourberieStateValueKind.IntIntDictionary, "", ""));
        var state = new NetworkFourberieState(
            FourberieCompatibilityManifest.AdapterVersion,
            0,
            ConfigHash,
            FourberieStateCodec.ComputeHash(entries),
            entries);

        Assert.False(FourberieStateCodec.TryValidate(state, out var failure));
        Assert.Contains("more than", failure);
    }

    [Fact]
    public void OversizedFieldKeyAndValue_AreRejected()
    {
        AssertOversizedEntry(new FourberieStateEntry(
            new string('f', FourberieStateCodec.MaximumFieldLength + 1),
            FourberieStateValueKind.StringStringDictionary,
            "key",
            "value"));
        AssertOversizedEntry(new FourberieStateEntry(
            "_field",
            FourberieStateValueKind.StringStringDictionary,
            new string('k', FourberieStateCodec.MaximumKeyLength + 1),
            "value"));
        AssertOversizedEntry(new FourberieStateEntry(
            "_field",
            FourberieStateValueKind.StringStringDictionary,
            "key",
            new string('v', FourberieStateCodec.MaximumValueLength + 1)));
    }

    [Fact]
    public void LocalOrWrongRoleSnapshotOrigins_AreRejected()
    {
        Assert.False(FourberieSnapshotOriginGuard.IsTrustedServerTransport(new object(), localIsClient: true));
        Assert.False(FourberieSnapshotOriginGuard.IsTrustedServerTransport(null, localIsClient: true));
        Assert.False(FourberieSnapshotOriginGuard.IsTrustedServerTransport(new object(), localIsClient: false));
    }

    private static void AssertOversizedEntry(FourberieStateEntry entry)
    {
        var entries = new[] { entry };
        var state = new NetworkFourberieState(
            FourberieCompatibilityManifest.AdapterVersion,
            0,
            ConfigHash,
            FourberieStateCodec.ComputeHash(entries),
            entries);

        Assert.False(FourberieStateCodec.TryValidate(state, out var failure));
        Assert.Contains("oversized", failure);
    }
}
