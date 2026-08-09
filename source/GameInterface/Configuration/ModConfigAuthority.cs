using ProtoBuf;
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GameInterface.Configuration;

/// <summary>
/// Immutable, integrity-checked host gameplay configuration. Difficulty is normally transferred
/// in the save; BirthAndDeath is repeated here because it gates campaign lifecycle behavior before
/// a joining client receives that save and is a required Friend Edition release invariant.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public sealed class ModConfigSnapshot
{
    public const int CurrentProtocolVersion = 1;
    public const int SessionIdLength = 32;
    public const int Sha256Length = 64;

    [ProtoMember(1)] public int ProtocolVersion { get; }
    [ProtoMember(2)] public string SessionId { get; }
    [ProtoMember(3)] public long Revision { get; }
    [ProtoMember(4)] public ModOptions ModOptions { get; }
    [ProtoMember(5)] public bool BirthAndDeathEnabled { get; }
    [ProtoMember(6)] public string Sha256 { get; }

    public ModConfigSnapshot(
        string sessionId,
        long revision,
        ModOptions modOptions,
        bool birthAndDeathEnabled)
        : this(
            CurrentProtocolVersion,
            sessionId,
            revision,
            modOptions,
            birthAndDeathEnabled,
            ModConfigSnapshotCodec.ComputeSha256(
                CurrentProtocolVersion,
                sessionId,
                revision,
                modOptions,
                birthAndDeathEnabled))
    {
    }

    public ModConfigSnapshot(
        int protocolVersion,
        string sessionId,
        long revision,
        ModOptions modOptions,
        bool birthAndDeathEnabled,
        string sha256)
    {
        ProtocolVersion = protocolVersion;
        SessionId = sessionId;
        Revision = revision;
        ModOptions = modOptions;
        BirthAndDeathEnabled = birthAndDeathEnabled;
        Sha256 = sha256;
    }
}

public enum ModConfigAcceptanceStatus
{
    Accepted,
    AlreadyCurrent,
    Malformed,
    Stale,
    Conflict,
}

public readonly struct ModConfigAcceptanceResult
{
    public ModConfigAcceptanceResult(ModConfigAcceptanceStatus status, string reason = null)
    {
        Status = status;
        Reason = reason;
    }

    public ModConfigAcceptanceStatus Status { get; }
    public string Reason { get; }
    public bool Succeeded =>
        Status == ModConfigAcceptanceStatus.Accepted ||
        Status == ModConfigAcceptanceStatus.AlreadyCurrent;
}

/// <summary>Session-scoped authority boundary used by the join handshake and late resync.</summary>
public interface IModConfigAuthority
{
    bool TryGetCurrent(out ModConfigSnapshot snapshot);
    ModConfigSnapshot InitializeHost(ModConfigData data);
    ModConfigAcceptanceResult AcceptClientSnapshot(ModConfigSnapshot snapshot);
    bool IsCurrent(ModConfigSnapshot snapshot);
    bool TryBindTrustedServer(object transportPeer, out string failure);
    bool IsTrustedServer(object transportPeer);
}

internal sealed class ModConfigAuthority : IModConfigAuthority
{
    private readonly object gate = new();
    private ModConfigSnapshot current;
    private object trustedServerPeer;

    public bool TryGetCurrent(out ModConfigSnapshot snapshot)
    {
        lock (gate)
        {
            snapshot = current;
            return snapshot != null;
        }
    }

    public ModConfigSnapshot InitializeHost(ModConfigData data)
    {
        if (data == null)
            throw new InvalidOperationException("The resolved host mod-config is unavailable.");

        ModOptionsData sourceOptions = data.ModOptions ?? new ModOptionsData();
        if (!ModConfigSnapshotCodec.TryValidateData(sourceOptions, out string dataFailure))
            throw new InvalidOperationException("The resolved host mod-config is invalid: " + dataFailure);

        // Friend Edition intentionally requires this explicit positive setting. Null is not treated
        // as true: that would let an unreadable/unmigrated operator file silently pass preflight.
        if (data.Difficulty?.BirthAndDeath != true)
            throw new InvalidOperationException(
                "Friend Edition release preflight requires difficulty.birthAndDeath=true in the resolved CoopData/mod-config.json.");

        ModOptions options = new(sourceOptions);
        if (!ModConfigSnapshotCodec.TryValidateOptions(options, out string optionFailure))
            throw new InvalidOperationException("The resolved host mod-config is invalid: " + optionFailure);

        lock (gate)
        {
            if (current != null)
            {
                // CampaignReady can be observed more than once during state restoration. The host
                // file is immutable for this session; changing it requires a new session identity.
                var candidateDigest = ModConfigSnapshotCodec.ComputeSha256(
                    current.ProtocolVersion,
                    current.SessionId,
                    current.Revision,
                    options,
                    birthAndDeathEnabled: true);
                if (!string.Equals(candidateDigest, current.Sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "The resolved host mod-config changed during the active Coop session.");
                return current;
            }

            current = new ModConfigSnapshot(
                Guid.NewGuid().ToString("N"),
                revision: 1,
                options,
                birthAndDeathEnabled: true);
            ModConfigProvider.ModOptions = options;
            return current;
        }
    }

    public ModConfigAcceptanceResult AcceptClientSnapshot(ModConfigSnapshot snapshot)
    {
        if (!ModConfigSnapshotCodec.TryValidate(snapshot, out string validationFailure))
            return new ModConfigAcceptanceResult(ModConfigAcceptanceStatus.Malformed, validationFailure);

        lock (gate)
        {
            if (current != null)
            {
                if (!string.Equals(current.SessionId, snapshot.SessionId, StringComparison.Ordinal))
                    return new ModConfigAcceptanceResult(
                        ModConfigAcceptanceStatus.Conflict,
                        "The host configuration session identity changed during this connection.");
                if (snapshot.Revision < current.Revision)
                    return new ModConfigAcceptanceResult(
                        ModConfigAcceptanceStatus.Stale,
                        $"Configuration revision {snapshot.Revision} is older than accepted revision {current.Revision}.");
                if (snapshot.Revision == current.Revision)
                {
                    return string.Equals(current.Sha256, snapshot.Sha256, StringComparison.Ordinal)
                        ? new ModConfigAcceptanceResult(ModConfigAcceptanceStatus.AlreadyCurrent)
                        : new ModConfigAcceptanceResult(
                            ModConfigAcceptanceStatus.Conflict,
                            "The host sent different configuration content for an accepted revision.");
                }
            }

            // Validation has completed before either shared reference changes. ModConfigProvider's
            // reference-box swap prevents readers from observing a torn multi-field value.
            ModConfigProvider.ModOptions = snapshot.ModOptions;
            current = snapshot;
            return new ModConfigAcceptanceResult(ModConfigAcceptanceStatus.Accepted);
        }
    }

    public bool IsCurrent(ModConfigSnapshot snapshot)
    {
        if (snapshot == null) return false;
        lock (gate)
        {
            return current != null &&
                current.ProtocolVersion == snapshot.ProtocolVersion &&
                current.Revision == snapshot.Revision &&
                string.Equals(current.SessionId, snapshot.SessionId, StringComparison.Ordinal) &&
                string.Equals(current.Sha256, snapshot.Sha256, StringComparison.Ordinal);
        }
    }

    public bool TryBindTrustedServer(object transportPeer, out string failure)
    {
        if (transportPeer == null)
        {
            failure = "The authoritative server transport is missing.";
            return false;
        }

        lock (gate)
        {
            if (trustedServerPeer == null)
            {
                trustedServerPeer = transportPeer;
                failure = null;
                return true;
            }
            if (ReferenceEquals(trustedServerPeer, transportPeer))
            {
                failure = null;
                return true;
            }

            failure = "A different transport peer attempted to replace the authoritative server.";
            return false;
        }
    }

    public bool IsTrustedServer(object transportPeer)
    {
        if (transportPeer == null) return false;
        lock (gate) return ReferenceEquals(trustedServerPeer, transportPeer);
    }
}

/// <summary>Canonical wire validation and digest computation. No JSON or reflection participates.</summary>
public static class ModConfigSnapshotCodec
{
    public static bool TryValidate(ModConfigSnapshot snapshot, out string failure)
    {
        if (snapshot == null)
        {
            failure = "The host configuration snapshot is missing.";
            return false;
        }
        if (snapshot.ProtocolVersion != ModConfigSnapshot.CurrentProtocolVersion)
        {
            failure = $"Unsupported host configuration protocol {snapshot.ProtocolVersion}.";
            return false;
        }
        if (snapshot.Revision <= 0)
        {
            failure = "The host configuration revision must be positive.";
            return false;
        }
        if (snapshot.SessionId == null || snapshot.SessionId.Length != ModConfigSnapshot.SessionIdLength ||
            !Guid.TryParseExact(snapshot.SessionId, "N", out _))
        {
            failure = "The host configuration session identity is malformed.";
            return false;
        }
        if (snapshot.Sha256 == null || snapshot.Sha256.Length != ModConfigSnapshot.Sha256Length ||
            !IsLowerHex(snapshot.Sha256))
        {
            failure = "The host configuration SHA-256 is malformed.";
            return false;
        }
        if (!snapshot.BirthAndDeathEnabled)
        {
            failure = "The host configuration has Birth & Death disabled; this Friend Edition build requires it.";
            return false;
        }
        if (!TryValidateOptions(snapshot.ModOptions, out failure)) return false;

        string expected = ComputeSha256(
            snapshot.ProtocolVersion,
            snapshot.SessionId,
            snapshot.Revision,
            snapshot.ModOptions,
            snapshot.BirthAndDeathEnabled);
        if (!string.Equals(expected, snapshot.Sha256, StringComparison.Ordinal))
        {
            failure = "The host configuration SHA-256 does not match its canonical payload.";
            return false;
        }

        failure = null;
        return true;
    }

    public static bool TryValidateData(ModOptionsData data, out string failure)
    {
        if (data == null)
        {
            failure = "modOptions is null.";
            return false;
        }
        if (data.GoldFoodInfluenceChangeInBattles.HasValue &&
            !IsGoldFoodMode(data.GoldFoodInfluenceChangeInBattles.Value))
            return Fail("goldFoodInfluenceChangeInBattles is outside its enum domain.", out failure);
        if (data.LordDefectionRetries.HasValue && !IsLordDefectionMode(data.LordDefectionRetries.Value))
            return Fail("lordDefectionRetries is outside its enum domain.", out failure);
        if (!InRange(data.PlayerBattleAiJoinWindowHours, 0, 168))
            return Fail("playerBattleAiJoinWindowHours must be between 0 and 168.", out failure);
        if (!InRange(data.WandererLimit, 0, 512))
            return Fail("wandererLimit must be between 0 and 512.", out failure);
        if (!InRange(data.PlayerKingdomClanTierRequired, 0, 6))
            return Fail("playerKingdomClanTierRequired must be between 0 and 6.", out failure);
        if (!FiniteRange(data.SmithingStaminaRecoveryMultiplier, 0f, 100f))
            return Fail("smithingStaminaRecoveryMultiplier must be finite and between 0 and 100.", out failure);
        if (!FiniteRange(data.MaximumLootersMultiplier, 0f, 100f))
            return Fail("maximumLootersMultiplier must be finite and between 0 and 100.", out failure);

        SeparatismOptionsData separatism = data.Separatism ?? new SeparatismOptionsData();
        if (!InRange(separatism.MinimalNumberOfWarsPerChaosKingdom, 0, 10))
            return Fail("separatism.minimalNumberOfWarsPerChaosKingdom must be between 0 and 10.", out failure);
        if (!InRange(separatism.MinimalAmountOfKingdomFiefsToRebel, 1, 20))
            return Fail("separatism.minimalAmountOfKingdomFiefsToRebel must be between 1 and 20.", out failure);
        if (!FiniteRange(separatism.DailyLordRebellionChance, 0f, 1f))
            return Fail("separatism.dailyLordRebellionChance must be finite and between 0 and 1.", out failure);
        if (!InRange(separatism.MinimalRequiredNumberOfNativeLords, 1, 5))
            return Fail("separatism.minimalRequiredNumberOfNativeLords must be between 1 and 5.", out failure);
        if (!FiniteRange(separatism.DailyNationalRebellionChance, 0f, 1f))
            return Fail("separatism.dailyNationalRebellionChance must be finite and between 0 and 1.", out failure);
        if (!InRange(separatism.CriticalAmountOfFiefsPerSingleClan, 1, 20))
            return Fail("separatism.criticalAmountOfFiefsPerSingleClan must be between 1 and 20.", out failure);
        if (!InRange(separatism.NumberOfDaysAfterOwnerVisitToKeepOrder, 1, 30))
            return Fail("separatism.numberOfDaysAfterOwnerVisitToKeepOrder must be between 1 and 30.", out failure);
        if (!FiniteRange(separatism.DailyAnarchyRebellionChance, 0f, 1f))
            return Fail("separatism.dailyAnarchyRebellionChance must be finite and between 0 and 1.", out failure);
        if (!InRange(separatism.SettlementRebellionStartLoyaltyThreshold, 0, 100) ||
            !InRange(separatism.SettlementRebellionEndLoyaltyThreshold, 0, 100))
            return Fail("Separatism loyalty thresholds must be between 0 and 100.", out failure);
        if (separatism.SettlementRebellionStartLoyaltyThreshold.HasValue &&
            separatism.SettlementRebellionEndLoyaltyThreshold.HasValue &&
            separatism.SettlementRebellionStartLoyaltyThreshold.Value >
            separatism.SettlementRebellionEndLoyaltyThreshold.Value)
            return Fail("The Separatism start loyalty threshold cannot exceed its end threshold.", out failure);
        if (!InRange(separatism.FriendThreshold, -100, 100) ||
            !InRange(separatism.EnemyThreshold, -100, 100))
            return Fail("Separatism relation thresholds must be between -100 and 100.", out failure);
        if (separatism.FriendThreshold.HasValue && separatism.EnemyThreshold.HasValue &&
            separatism.FriendThreshold.Value < separatism.EnemyThreshold.Value)
            return Fail("The Separatism friend threshold cannot be below its enemy threshold.", out failure);
        if (!RelationRange(separatism))
            return Fail("Separatism relation changes must be between -100 and 100.", out failure);

        failure = null;
        return true;
    }

    public static bool TryValidateOptions(ModOptions options, out string failure)
    {
        if (!IsGoldFoodMode(options.GoldFoodInfluenceChangeInBattles))
            return Fail("goldFoodInfluenceChangeInBattles is outside its enum domain.", out failure);
        if (!IsLordDefectionMode(options.LordDefectionRetries))
            return Fail("lordDefectionRetries is outside its enum domain.", out failure);
        if (!InRange(options.PlayerBattleAiJoinWindowHours, 0, 168))
            return Fail("playerBattleAiJoinWindowHours must be between 0 and 168.", out failure);
        if (!InRange(options.WandererLimit, 0, 512))
            return Fail("wandererLimit must be between 0 and 512.", out failure);
        if (!InRange(options.PlayerKingdomClanTierRequired, 0, 6))
            return Fail("playerKingdomClanTierRequired must be between 0 and 6.", out failure);
        if (!FiniteRange(options.SmithingStaminaRecoveryMultiplier, 0f, 100f))
            return Fail("smithingStaminaRecoveryMultiplier must be finite and between 0 and 100.", out failure);
        if (!FiniteRange(options.MaximumLootersMultiplier, 0f, 100f))
            return Fail("maximumLootersMultiplier must be finite and between 0 and 100.", out failure);

        SeparatismOptions s = options.Separatism;
        if (!InRange(s.MinimalNumberOfWarsPerChaosKingdom, 0, 10) ||
            !InRange(s.MinimalAmountOfKingdomFiefsToRebel, 1, 20) ||
            !InRange(s.MinimalRequiredNumberOfNativeLords, 1, 5) ||
            !InRange(s.CriticalAmountOfFiefsPerSingleClan, 1, 20) ||
            !InRange(s.NumberOfDaysAfterOwnerVisitToKeepOrder, 1, 30))
            return Fail("One or more Separatism count/day values are outside their canonical bounds.", out failure);
        if (!FiniteRange(s.DailyLordRebellionChance, 0f, 1f) ||
            !FiniteRange(s.DailyNationalRebellionChance, 0f, 1f) ||
            !FiniteRange(s.DailyAnarchyRebellionChance, 0f, 1f))
            return Fail("Separatism chances must be finite and between 0 and 1.", out failure);
        if (!InRange(s.SettlementRebellionStartLoyaltyThreshold, 0, 100) ||
            !InRange(s.SettlementRebellionEndLoyaltyThreshold, 0, 100) ||
            s.SettlementRebellionStartLoyaltyThreshold > s.SettlementRebellionEndLoyaltyThreshold)
            return Fail("Separatism loyalty thresholds are invalid.", out failure);
        if (!InRange(s.FriendThreshold, -100, 100) || !InRange(s.EnemyThreshold, -100, 100) ||
            s.FriendThreshold < s.EnemyThreshold)
            return Fail("Separatism friend/enemy thresholds are invalid.", out failure);
        if (!RelationRange(s))
            return Fail("Separatism relation changes must be between -100 and 100.", out failure);

        failure = null;
        return true;
    }

    public static string ComputeSha256(
        int protocolVersion,
        string sessionId,
        long revision,
        ModOptions o,
        bool birthAndDeathEnabled)
    {
        var text = new StringBuilder(768);
        Append(text, protocolVersion);
        Append(text, sessionId ?? string.Empty);
        Append(text, revision);
        Append(text, birthAndDeathEnabled);
        Append(text, o.FastForwardEnabled);
        Append(text, o.AutoPauseEnabled);
        Append(text, o.ClientsCanUseCheats);
        Append(text, o.GoldFoodInfluenceChangeInSettlements);
        Append(text, (int)o.GoldFoodInfluenceChangeInBattles);
        Append(text, o.GoldFoodInfluenceChangeForDisconnectedPlayers);
        Append(text, o.PlayerBattleAiJoinWindowHours);
        Append(text, o.SpeedLimitWhilePlayersInBattle);
        Append(text, o.WandererLimit);
        Append(text, o.WandererLimitScalesWithPlayers);
        Append(text, o.PlayerKingdomClanTierRequired);
        Append(text, o.SmithingStaminaRecoveryOutsideSettlements);
        Append(text, o.SmithingStaminaRecoveryMultiplier);
        Append(text, o.MaximumLootersMultiplier);
        Append(text, (int)o.LordDefectionRetries);
        Append(text, o.EnableHeroExecutions);
        Append(text, o.EnablePlayerClanMemberExecutions);

        // Which Workshop modules the host integrates decides which Harmony adapters and sync
        // registrations exist on each peer, so it belongs in the digest exactly like any other
        // option. ModOptions sorts and de-duplicates the list, so this is order-stable.
        string[] disabledModules = o.DisabledWorkshopModules ?? Array.Empty<string>();
        Append(text, disabledModules.Length);
        foreach (string moduleId in disabledModules) Append(text, moduleId);

        SeparatismOptions s = o.Separatism;
        Append(text, s.Enabled);
        Append(text, s.ChaosStartEnabled);
        Append(text, s.MinimalNumberOfWarsPerChaosKingdom);
        Append(text, s.LordRebellionsEnabled);
        Append(text, s.AverageAmountOfKingdomFiefsIsEnoughToRebel);
        Append(text, s.MinimalAmountOfKingdomFiefsToRebel);
        Append(text, s.DailyLordRebellionChance);
        Append(text, s.NationalRebellionsEnabled);
        Append(text, s.MinimalRequiredNumberOfNativeLords);
        Append(text, s.DailyNationalRebellionChance);
        Append(text, s.AnarchyRebellionsEnabled);
        Append(text, s.CriticalAmountOfFiefsPerSingleClan);
        Append(text, s.NumberOfDaysAfterOwnerVisitToKeepOrder);
        Append(text, s.BonusRebelFiefForHighTierClan);
        Append(text, s.DailyAnarchyRebellionChance);
        Append(text, s.SettlementRebellionsEnabled);
        Append(text, s.SettlementRebellionStartLoyaltyThreshold);
        Append(text, s.SettlementRebellionEndLoyaltyThreshold);
        Append(text, s.FriendThreshold);
        Append(text, s.EnemyThreshold);
        Append(text, s.RelationChangeRebelWithRuler);
        Append(text, s.RelationChangeRebelWithRulerFriendVassals);
        Append(text, s.RelationChangeRebelWithRulerEnemyVassals);
        Append(text, s.RelationChangeRebelWithRulerVassals);
        Append(text, s.RelationChangeUnitedRulers);
        Append(text, s.RelationChangeRulerWithSupporter);
        Append(text, s.RelationChangeNationalRebellionClans);
        Append(text, s.KeepEmptyKingdoms);
        Append(text, s.KeepOriginalKingdomWars);
        Append(text, s.AllowUnions);
        Append(text, s.KeepRebelBannerColors);
        Append(text, s.SameColorsForAllRebels);

        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
        var result = new StringBuilder(hash.Length * 2);
        foreach (byte value in hash) result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return result.ToString();
    }

    private static bool IsGoldFoodMode(GoldFoodChangeMode value) =>
        value == GoldFoodChangeMode.Disabled || value == GoldFoodChangeMode.OneDayMax || value == GoldFoodChangeMode.Enabled;

    private static bool IsLordDefectionMode(LordDefectionRetryMode value) =>
        value == LordDefectionRetryMode.Vanilla || value == LordDefectionRetryMode.NeverExpire || value == LordDefectionRetryMode.AlwaysRetry;

    private static bool IsLowerHex(string value)
    {
        foreach (char c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    private static bool InRange(int? value, int min, int max) =>
        !value.HasValue || InRange(value.Value, min, max);
    private static bool InRange(int value, int min, int max) => value >= min && value <= max;
    private static bool FiniteRange(float? value, float min, float max) =>
        !value.HasValue || FiniteRange(value.Value, min, max);
    private static bool FiniteRange(float value, float min, float max) =>
        !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max;

    private static bool RelationRange(SeparatismOptionsData s) =>
        InRange(s.RelationChangeRebelWithRuler, -100, 100) &&
        InRange(s.RelationChangeRebelWithRulerFriendVassals, -100, 100) &&
        InRange(s.RelationChangeRebelWithRulerEnemyVassals, -100, 100) &&
        InRange(s.RelationChangeRebelWithRulerVassals, -100, 100) &&
        InRange(s.RelationChangeUnitedRulers, -100, 100) &&
        InRange(s.RelationChangeRulerWithSupporter, -100, 100) &&
        InRange(s.RelationChangeNationalRebellionClans, -100, 100);

    private static bool RelationRange(SeparatismOptions s) =>
        InRange(s.RelationChangeRebelWithRuler, -100, 100) &&
        InRange(s.RelationChangeRebelWithRulerFriendVassals, -100, 100) &&
        InRange(s.RelationChangeRebelWithRulerEnemyVassals, -100, 100) &&
        InRange(s.RelationChangeRebelWithRulerVassals, -100, 100) &&
        InRange(s.RelationChangeUnitedRulers, -100, 100) &&
        InRange(s.RelationChangeRulerWithSupporter, -100, 100) &&
        InRange(s.RelationChangeNationalRebellionClans, -100, 100);

    private static bool Fail(string message, out string failure)
    {
        failure = message;
        return false;
    }

    private static void Append(StringBuilder text, string value) =>
        text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
    private static void Append(StringBuilder text, bool value) => Append(text, value ? "1" : "0");
    private static void Append(StringBuilder text, int value) => Append(text, value.ToString(CultureInfo.InvariantCulture));
    private static void Append(StringBuilder text, long value) => Append(text, value.ToString(CultureInfo.InvariantCulture));
    private static void Append(StringBuilder text, float value) => Append(text, value.ToString("R", CultureInfo.InvariantCulture));
}
