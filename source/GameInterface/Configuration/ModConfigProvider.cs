using ProtoBuf;

namespace GameInterface.Configuration;

public class ModConfigProvider
{
    /// <summary>What the session runs on until a config is loaded (server) or received (client).
    /// Built from an all-absent <see cref="ModOptionsData"/> so every option falls back to its
    /// documented default. It has to go through that constructor: the options struct declares no
    /// parameterless one, so a plain <c>new ModOptions()</c> is just <c>default</c> — the property
    /// initializers below never run and every option reads back false/0.</summary>
    public static ModOptions ModOptions = new(new ModOptionsData());

    public static void LoadModConfig(ModOptionsData modOptionsData)
    {
        ModOptions = new(modOptionsData);
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct ModOptions
{
    [ProtoMember(1)]
    public readonly bool FastForwardEnabled { get; } = true;
    [ProtoMember(2)]
    public readonly bool AutoPauseEnabled { get; } = true;
    [ProtoMember(3)]
    public readonly bool ClientsCanUseCheats { get; } = false;
    [ProtoMember(4)]
    public readonly bool GoldFoodInfluenceChangeInSettlements { get; } = true;
    [ProtoMember(5)]
    public readonly GoldFoodChangeMode GoldFoodInfluenceChangeInBattles { get; } = GoldFoodChangeMode.OneDayMax;
    [ProtoMember(6)]
    public readonly bool GoldFoodInfluenceChangeForDisconnectedPlayers { get; } = false;
    [ProtoMember(7)]
    public readonly int PlayerBattleAiJoinWindowHours { get; } = 24;
    [ProtoMember(8)]
    public readonly bool SpeedLimitWhilePlayersInBattle { get; } = true;
    [ProtoMember(9)]
    public readonly int WandererLimit { get; } = 32;
    [ProtoMember(10)]
    public readonly bool WandererLimitScalesWithPlayers { get; } = false;
    [ProtoMember(11)]
    public readonly int PlayerKingdomClanTierRequired { get; } = 4;
    [ProtoMember(12)]
    public readonly bool SmithingStaminaRecoveryOutsideSettlements { get; } = true;
    [ProtoMember(13)]
    public readonly float SmithingStaminaRecoveryMultiplier { get; } = 0.1f;
    [ProtoMember(14)]
    public readonly float MaximumLootersMultiplier { get; } = 1f;
    [ProtoMember(15)]
    public readonly LordDefectionRetryMode LordDefectionRetries { get; } = LordDefectionRetryMode.Vanilla;
    [ProtoMember(16)]  
    public readonly bool EnableHeroExecutions { get; } = true;
    [ProtoMember(17)]
    public readonly bool EnablePlayerClanMemberExecutions { get; } = false;
    [ProtoMember(18)]
    public readonly SeparatismOptions Separatism { get; } = new SeparatismOptions(new SeparatismOptionsData());

    public ModOptions(ModOptionsData modOptionsData)
    {
        FastForwardEnabled = modOptionsData.FastForwardEnabled ?? FastForwardEnabled;
        AutoPauseEnabled = modOptionsData.AutoPauseEnabled ?? AutoPauseEnabled;
        ClientsCanUseCheats = modOptionsData.ClientsCanUseCheats ?? ClientsCanUseCheats;
        GoldFoodInfluenceChangeInSettlements = modOptionsData.GoldFoodInfluenceChangeInSettlements ?? GoldFoodInfluenceChangeInSettlements;
        GoldFoodInfluenceChangeInBattles = modOptionsData.GoldFoodInfluenceChangeInBattles ?? GoldFoodInfluenceChangeInBattles;
        GoldFoodInfluenceChangeForDisconnectedPlayers = modOptionsData.GoldFoodInfluenceChangeForDisconnectedPlayers ?? GoldFoodInfluenceChangeForDisconnectedPlayers;
        PlayerBattleAiJoinWindowHours = modOptionsData.PlayerBattleAiJoinWindowHours ?? PlayerBattleAiJoinWindowHours;
        SpeedLimitWhilePlayersInBattle = modOptionsData.SpeedLimitWhilePlayersInBattle ?? SpeedLimitWhilePlayersInBattle;
        WandererLimit = modOptionsData.WandererLimit ?? WandererLimit;
        WandererLimitScalesWithPlayers = modOptionsData.WandererLimitScalesWithPlayers ?? WandererLimitScalesWithPlayers;
        PlayerKingdomClanTierRequired = modOptionsData.PlayerKingdomClanTierRequired ?? PlayerKingdomClanTierRequired;
        SmithingStaminaRecoveryOutsideSettlements = modOptionsData.SmithingStaminaRecoveryOutsideSettlements ?? SmithingStaminaRecoveryOutsideSettlements;
        SmithingStaminaRecoveryMultiplier = modOptionsData.SmithingStaminaRecoveryMultiplier ?? SmithingStaminaRecoveryMultiplier;
        MaximumLootersMultiplier = modOptionsData.MaximumLootersMultiplier ?? MaximumLootersMultiplier;
        LordDefectionRetries = modOptionsData.LordDefectionRetries ?? LordDefectionRetries;      
        EnableHeroExecutions = modOptionsData.EnableHeroExecutions ?? EnableHeroExecutions;
        EnablePlayerClanMemberExecutions = modOptionsData.EnablePlayerClanMemberExecutions ?? EnablePlayerClanMemberExecutions;
        Separatism = new SeparatismOptions(modOptionsData.Separatism ?? new SeparatismOptionsData());
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct SeparatismOptions
{
    [ProtoMember(1)] public readonly bool Enabled { get; } = true;
    [ProtoMember(2)] public readonly bool ChaosStartEnabled { get; } = true;
    [ProtoMember(3)] public readonly int MinimalNumberOfWarsPerChaosKingdom { get; } = 1;
    [ProtoMember(4)] public readonly bool LordRebellionsEnabled { get; } = true;
    [ProtoMember(5)] public readonly bool AverageAmountOfKingdomFiefsIsEnoughToRebel { get; } = true;
    [ProtoMember(6)] public readonly int MinimalAmountOfKingdomFiefsToRebel { get; } = 3;
    [ProtoMember(7)] public readonly float DailyLordRebellionChance { get; } = 1f;
    [ProtoMember(8)] public readonly bool NationalRebellionsEnabled { get; } = true;
    [ProtoMember(9)] public readonly int MinimalRequiredNumberOfNativeLords { get; } = 2;
    [ProtoMember(10)] public readonly float DailyNationalRebellionChance { get; } = 1f;
    [ProtoMember(11)] public readonly bool AnarchyRebellionsEnabled { get; } = true;
    [ProtoMember(12)] public readonly int CriticalAmountOfFiefsPerSingleClan { get; } = 10;
    [ProtoMember(13)] public readonly int NumberOfDaysAfterOwnerVisitToKeepOrder { get; } = 14;
    [ProtoMember(14)] public readonly bool BonusRebelFiefForHighTierClan { get; } = true;
    [ProtoMember(15)] public readonly float DailyAnarchyRebellionChance { get; } = 1f;
    [ProtoMember(16)] public readonly bool SettlementRebellionsEnabled { get; } = false;
    [ProtoMember(17)] public readonly int SettlementRebellionStartLoyaltyThreshold { get; } = 25;
    [ProtoMember(18)] public readonly int SettlementRebellionEndLoyaltyThreshold { get; } = 50;
    [ProtoMember(19)] public readonly int FriendThreshold { get; } = 10;
    [ProtoMember(20)] public readonly int EnemyThreshold { get; } = -10;
    [ProtoMember(21)] public readonly int RelationChangeRebelWithRuler { get; } = -20;
    [ProtoMember(22)] public readonly int RelationChangeRebelWithRulerFriendVassals { get; } = -10;
    [ProtoMember(23)] public readonly int RelationChangeRebelWithRulerEnemyVassals { get; } = 10;
    [ProtoMember(24)] public readonly int RelationChangeRebelWithRulerVassals { get; } = 0;
    [ProtoMember(25)] public readonly int RelationChangeUnitedRulers { get; } = 20;
    [ProtoMember(26)] public readonly int RelationChangeRulerWithSupporter { get; } = 20;
    [ProtoMember(27)] public readonly int RelationChangeNationalRebellionClans { get; } = 20;
    [ProtoMember(28)] public readonly bool KeepEmptyKingdoms { get; } = false;
    [ProtoMember(29)] public readonly bool KeepOriginalKingdomWars { get; } = false;
    [ProtoMember(30)] public readonly bool AllowUnions { get; } = true;
    [ProtoMember(31)] public readonly bool KeepRebelBannerColors { get; } = false;
    [ProtoMember(32)] public readonly bool SameColorsForAllRebels { get; } = false;

    public SeparatismOptions(SeparatismOptionsData data)
    {
        Enabled = data.Enabled ?? Enabled;
        ChaosStartEnabled = data.ChaosStartEnabled ?? ChaosStartEnabled;
        MinimalNumberOfWarsPerChaosKingdom = Clamp(data.MinimalNumberOfWarsPerChaosKingdom, 0, 10, MinimalNumberOfWarsPerChaosKingdom);
        LordRebellionsEnabled = data.LordRebellionsEnabled ?? LordRebellionsEnabled;
        AverageAmountOfKingdomFiefsIsEnoughToRebel = data.AverageAmountOfKingdomFiefsIsEnoughToRebel ?? AverageAmountOfKingdomFiefsIsEnoughToRebel;
        MinimalAmountOfKingdomFiefsToRebel = Clamp(data.MinimalAmountOfKingdomFiefsToRebel, 1, 20, MinimalAmountOfKingdomFiefsToRebel);
        DailyLordRebellionChance = Clamp01(data.DailyLordRebellionChance, DailyLordRebellionChance);
        NationalRebellionsEnabled = data.NationalRebellionsEnabled ?? NationalRebellionsEnabled;
        MinimalRequiredNumberOfNativeLords = Clamp(data.MinimalRequiredNumberOfNativeLords, 1, 5, MinimalRequiredNumberOfNativeLords);
        DailyNationalRebellionChance = Clamp01(data.DailyNationalRebellionChance, DailyNationalRebellionChance);
        AnarchyRebellionsEnabled = data.AnarchyRebellionsEnabled ?? AnarchyRebellionsEnabled;
        CriticalAmountOfFiefsPerSingleClan = Clamp(data.CriticalAmountOfFiefsPerSingleClan, 1, 20, CriticalAmountOfFiefsPerSingleClan);
        NumberOfDaysAfterOwnerVisitToKeepOrder = Clamp(data.NumberOfDaysAfterOwnerVisitToKeepOrder, 1, 30, NumberOfDaysAfterOwnerVisitToKeepOrder);
        BonusRebelFiefForHighTierClan = data.BonusRebelFiefForHighTierClan ?? BonusRebelFiefForHighTierClan;
        DailyAnarchyRebellionChance = Clamp01(data.DailyAnarchyRebellionChance, DailyAnarchyRebellionChance);
        SettlementRebellionsEnabled = data.SettlementRebellionsEnabled ?? SettlementRebellionsEnabled;
        int start = Clamp(data.SettlementRebellionStartLoyaltyThreshold, 0, 100, SettlementRebellionStartLoyaltyThreshold);
        int end = Clamp(data.SettlementRebellionEndLoyaltyThreshold, 0, 100, SettlementRebellionEndLoyaltyThreshold);
        SettlementRebellionStartLoyaltyThreshold = System.Math.Min(start, end);
        SettlementRebellionEndLoyaltyThreshold = System.Math.Max(start, end);
        int friend = Clamp(data.FriendThreshold, -100, 100, FriendThreshold);
        int enemy = Clamp(data.EnemyThreshold, -100, 100, EnemyThreshold);
        FriendThreshold = System.Math.Max(friend, enemy);
        EnemyThreshold = System.Math.Min(friend, enemy);
        RelationChangeRebelWithRuler = Clamp(data.RelationChangeRebelWithRuler, -100, 100, RelationChangeRebelWithRuler);
        RelationChangeRebelWithRulerFriendVassals = Clamp(data.RelationChangeRebelWithRulerFriendVassals, -100, 100, RelationChangeRebelWithRulerFriendVassals);
        RelationChangeRebelWithRulerEnemyVassals = Clamp(data.RelationChangeRebelWithRulerEnemyVassals, -100, 100, RelationChangeRebelWithRulerEnemyVassals);
        RelationChangeRebelWithRulerVassals = Clamp(data.RelationChangeRebelWithRulerVassals, -100, 100, RelationChangeRebelWithRulerVassals);
        RelationChangeUnitedRulers = Clamp(data.RelationChangeUnitedRulers, -100, 100, RelationChangeUnitedRulers);
        RelationChangeRulerWithSupporter = Clamp(data.RelationChangeRulerWithSupporter, -100, 100, RelationChangeRulerWithSupporter);
        RelationChangeNationalRebellionClans = Clamp(data.RelationChangeNationalRebellionClans, -100, 100, RelationChangeNationalRebellionClans);
        KeepEmptyKingdoms = data.KeepEmptyKingdoms ?? KeepEmptyKingdoms;
        KeepOriginalKingdomWars = data.KeepOriginalKingdomWars ?? KeepOriginalKingdomWars;
        AllowUnions = data.AllowUnions ?? AllowUnions;
        KeepRebelBannerColors = data.KeepRebelBannerColors ?? KeepRebelBannerColors;
        SameColorsForAllRebels = data.SameColorsForAllRebels ?? SameColorsForAllRebels;
    }

    private static int Clamp(int? value, int min, int max, int fallback) =>
        System.Math.Min(max, System.Math.Max(min, value ?? fallback));

    private static float Clamp01(float? value, float fallback) =>
        System.Math.Min(1f, System.Math.Max(0f, value ?? fallback));
}
