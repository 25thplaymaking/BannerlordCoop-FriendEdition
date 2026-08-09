using GameInterface.Configuration;
using TaleWorlds.CampaignSystem.GameComponents;

namespace GameInterface.Services.Separatism;

public sealed class SeparatismSettlementLoyaltyModel : DefaultSettlementLoyaltyModel
{
    public override int RebellionStartLoyaltyThreshold =>
        ModConfigProvider.ModOptions.Separatism.Enabled
            ? ModConfigProvider.ModOptions.Separatism.SettlementRebellionStartLoyaltyThreshold
            : base.RebellionStartLoyaltyThreshold;

    // Separatism 1.3.8 exposed an end threshold but never applied it. Bannerlord 1.4.7 uses
    // RebelliousStateStartLoyaltyThreshold for the recovery boundary, so the integrated port
    // finally wires the documented setting to the engine model.
    public override int RebelliousStateStartLoyaltyThreshold =>
        ModConfigProvider.ModOptions.Separatism.Enabled
            ? ModConfigProvider.ModOptions.Separatism.SettlementRebellionEndLoyaltyThreshold
            : base.RebelliousStateStartLoyaltyThreshold;
}
