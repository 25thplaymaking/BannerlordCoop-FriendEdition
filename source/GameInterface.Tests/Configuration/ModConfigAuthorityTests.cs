using GameInterface.Configuration;
using System;
using Xunit;

namespace GameInterface.Tests.Configuration;

public sealed class ModConfigAuthorityTests : IDisposable
{
    private readonly ModOptions original = ModConfigProvider.ModOptions;

    public void Dispose() => ModConfigProvider.ModOptions = original;

    [Fact]
    public void InitializeHost_RequiresExplicitBirthAndDeathTrue()
    {
        var authority = new ModConfigAuthority();
        var missing = new ModConfigData
        {
            Difficulty = new DifficultyConfigData { BirthAndDeath = null },
        };
        var disabled = new ModConfigData
        {
            Difficulty = new DifficultyConfigData { BirthAndDeath = false },
        };

        Assert.Throws<InvalidOperationException>(() => authority.InitializeHost(missing));
        Assert.Throws<InvalidOperationException>(() => authority.InitializeHost(disabled));

        var enabled = new ModConfigData
        {
            Difficulty = new DifficultyConfigData { BirthAndDeath = true },
        };
        ModConfigSnapshot snapshot = authority.InitializeHost(enabled);
        Assert.True(snapshot.BirthAndDeathEnabled);
        Assert.True(ModConfigSnapshotCodec.TryValidate(snapshot, out var failure), failure);
        Assert.Equal(snapshot.Sha256, AssertCurrent(authority).Sha256);
    }

    [Fact]
    public void AcceptClientSnapshot_ValidatesBeforeAtomicCommit()
    {
        var authority = new ModConfigAuthority();
        ModOptions before = new(new ModOptionsData { WandererLimit = 27 });
        ModConfigProvider.ModOptions = before;
        var invalidOptions = new ModOptions(new ModOptionsData
        {
            WandererLimit = 91,
            SmithingStaminaRecoveryMultiplier = float.NaN,
        });
        var malformed = Snapshot(1, invalidOptions);

        ModConfigAcceptanceResult result = authority.AcceptClientSnapshot(malformed);

        Assert.Equal(ModConfigAcceptanceStatus.Malformed, result.Status);
        Assert.Equal(27, ModConfigProvider.ModOptions.WandererLimit);
        Assert.False(authority.TryGetCurrent(out _));
    }

    [Fact]
    public void AcceptClientSnapshot_RejectsEnumAndBoundsViolations()
    {
        var invalidEnum = new ModOptions(new ModOptionsData
        {
            GoldFoodInfluenceChangeInBattles = (GoldFoodChangeMode)99,
        });
        var invalidBounds = new ModOptions(new ModOptionsData
        {
            PlayerBattleAiJoinWindowHours = 169,
        });

        Assert.False(ModConfigSnapshotCodec.TryValidate(Snapshot(1, invalidEnum), out var enumFailure));
        Assert.Contains("enum", enumFailure, StringComparison.OrdinalIgnoreCase);
        Assert.False(ModConfigSnapshotCodec.TryValidate(Snapshot(1, invalidBounds), out var boundFailure));
        Assert.Contains("168", boundFailure, StringComparison.Ordinal);
    }

    [Fact]
    public void RawSchemaValidation_RejectsSeparatismNaNAndInvertedInvariants()
    {
        var chance = new ModOptionsData
        {
            Separatism = new SeparatismOptionsData { DailyLordRebellionChance = float.NaN },
        };
        var loyalty = new ModOptionsData
        {
            Separatism = new SeparatismOptionsData
            {
                SettlementRebellionStartLoyaltyThreshold = 80,
                SettlementRebellionEndLoyaltyThreshold = 20,
            },
        };
        var relation = new ModOptionsData
        {
            Separatism = new SeparatismOptionsData { FriendThreshold = -20, EnemyThreshold = 20 },
        };

        Assert.False(ModConfigSnapshotCodec.TryValidateData(chance, out var chanceFailure));
        Assert.Contains("finite", chanceFailure, StringComparison.OrdinalIgnoreCase);
        Assert.False(ModConfigSnapshotCodec.TryValidateData(loyalty, out var loyaltyFailure));
        Assert.Contains("start loyalty", loyaltyFailure, StringComparison.OrdinalIgnoreCase);
        Assert.False(ModConfigSnapshotCodec.TryValidateData(relation, out var relationFailure));
        Assert.Contains("friend threshold", relationFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RevisionGate_RejectsStaleAndSameRevisionConflict()
    {
        var authority = new ModConfigAuthority();
        string session = "0123456789abcdef0123456789abcdef";
        var revisionOne = Snapshot(1, new ModOptions(new ModOptionsData { WandererLimit = 20 }), session);
        var revisionTwo = Snapshot(2, new ModOptions(new ModOptionsData { WandererLimit = 21 }), session);

        Assert.True(authority.AcceptClientSnapshot(revisionOne).Succeeded);
        Assert.True(authority.AcceptClientSnapshot(revisionTwo).Succeeded);
        Assert.Equal(
            ModConfigAcceptanceStatus.Stale,
            authority.AcceptClientSnapshot(revisionOne).Status);

        var conflictingTwo = Snapshot(2, new ModOptions(new ModOptionsData { WandererLimit = 22 }), session);
        Assert.Equal(
            ModConfigAcceptanceStatus.Conflict,
            authority.AcceptClientSnapshot(conflictingTwo).Status);
        Assert.Equal(21, ModConfigProvider.ModOptions.WandererLimit);
    }

    [Fact]
    public void SameRevisionTamperedDigest_IsMalformedNotAlreadyCurrent()
    {
        var authority = new ModConfigAuthority();
        ModConfigSnapshot valid = Snapshot(1, new ModOptions(new ModOptionsData()));
        Assert.True(authority.AcceptClientSnapshot(valid).Succeeded);
        var tampered = new ModConfigSnapshot(
            valid.ProtocolVersion,
            valid.SessionId,
            valid.Revision,
            valid.ModOptions,
            valid.BirthAndDeathEnabled,
            new string('0', ModConfigSnapshot.Sha256Length));

        ModConfigAcceptanceResult result = authority.AcceptClientSnapshot(tampered);

        Assert.Equal(ModConfigAcceptanceStatus.Malformed, result.Status);
        Assert.True(authority.IsCurrent(valid));
    }

    [Fact]
    public void ClientAuthorities_IsolateDifferentServerSessions()
    {
        var firstClient = new ModConfigAuthority();
        var secondClient = new ModConfigAuthority();
        ModConfigSnapshot first = Snapshot(
            1,
            new ModOptions(new ModOptionsData { WandererLimit = 20 }),
            "0123456789abcdef0123456789abcdef");
        ModConfigSnapshot second = Snapshot(
            1,
            new ModOptions(new ModOptionsData { WandererLimit = 30 }),
            "fedcba9876543210fedcba9876543210");

        Assert.True(firstClient.AcceptClientSnapshot(first).Succeeded);
        Assert.True(secondClient.AcceptClientSnapshot(second).Succeeded);
        Assert.True(firstClient.IsCurrent(first));
        Assert.False(firstClient.IsCurrent(second));
        Assert.True(secondClient.IsCurrent(second));
        Assert.False(secondClient.IsCurrent(first));
    }

    [Fact]
    public void TrustedServerTransport_CannotBeRepinned()
    {
        var authority = new ModConfigAuthority();
        var server = new object();
        var forged = new object();

        Assert.True(authority.TryBindTrustedServer(server, out var firstFailure), firstFailure);
        Assert.True(authority.TryBindTrustedServer(server, out var repeatFailure), repeatFailure);
        Assert.False(authority.TryBindTrustedServer(forged, out var forgedFailure));
        Assert.Contains("different transport", forgedFailure, StringComparison.OrdinalIgnoreCase);
        Assert.True(authority.IsTrustedServer(server));
        Assert.False(authority.IsTrustedServer(forged));
    }

    private static ModConfigSnapshot Snapshot(
        long revision,
        ModOptions options,
        string session = "0123456789abcdef0123456789abcdef") =>
        new(session, revision, options, birthAndDeathEnabled: true);

    private static ModConfigSnapshot AssertCurrent(IModConfigAuthority authority)
    {
        Assert.True(authority.TryGetCurrent(out ModConfigSnapshot snapshot));
        return snapshot;
    }
}
