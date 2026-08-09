using Common.Logging;
using GameInterface.Configuration;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>Why a routed Diplomacy gold donation was or was not applied.</summary>
public enum DiplomacyDonationVerdict
{
    Applied,

    /// <summary>Giver or clan missing — nothing to donate from or to.</summary>
    InvalidTarget,

    /// <summary>Non-positive, or more than the giver's authoritative gold.</summary>
    InvalidAmount,

    /// <summary>The operator switched Bannerlord.Diplomacy off for this session.</summary>
    ModuleDisabled,

    /// <summary>The pinned Diplomacy build is not loaded, so there is no audited apply to run.</summary>
    ModuleNotInstalled,

    /// <summary>The mod's apply threw; nothing may be assumed about resulting state.</summary>
    ApplyFailed,
}

/// <summary>
/// [Server, game thread] Applies a Diplomacy gold donation authoritatively, for a named hero
/// rather than "the" player.
/// </summary>
public interface IDiplomacyDonateGoldInterface : IGameAbstraction
{
    DiplomacyDonationVerdict TryApplyDonation(Hero giver, Clan clan, int amount);
}

/// <summary>
/// Reproduces <c>DonateGoldVM.ExecutePropose</c>'s consequence with every hero explicit.
/// </summary>
/// <remarks>
/// The gold movement runs the MOD'S OWN <c>GiveGoldToClanAction.ApplyFromHeroToClan</c> (via
/// reflection against the digest-pinned assembly; its body is fully hero-parameterised — no
/// MainHero anywhere), so the distribution across the clan's lords stays whatever Diplomacy
/// ships rather than a copy that drifts on the next mod update. The relation gain is the VM's own
/// deterministic formula over NATIVE campaign models, applied with
/// <see cref="ChangeRelationAction.ApplyRelationChangeBetweenHeroes"/> instead of the
/// MainHero-bound <c>ApplyPlayerRelation</c>.
///
/// <para>
/// Deliberately NOT reproduced: the VM's Generosity/Calculating trait XP. Vanilla's whole trait
/// pipeline (<c>TraitLevelingHelper</c>, <c>Campaign.PlayerTraitDeveloper</c>) is hard-bound to
/// <c>Hero.MainHero</c>, which on this process is not the giver; crediting the XP to the wrong
/// hero is worse than crediting none. Revisit if remote-player trait development ever gets its
/// own routed shape.
/// </para>
///
/// <para>
/// This is also the first production consumer of
/// <see cref="WorkshopModuleRegistrar.ResolveLiveModules"/> — the operator's per-module switch
/// finally gates something at runtime: a disabled module refuses the intent before any state is
/// touched. Nothing here publishes a Diplomacy snapshot because a donation touches no Diplomacy
/// manager state — gold and relation replicate through Coop's native funnels.
/// </para>
/// </remarks>
internal sealed class DiplomacyDonateGoldInterface : IDiplomacyDonateGoldInterface
{
    private static readonly ILogger Logger = LogManager.GetLogger<DiplomacyDonateGoldInterface>();

    private readonly IReadOnlyList<IWorkshopModule> declaredModules;

    /// <summary>Observability seam for the E2E gates; production reads the return value.</summary>
    internal DiplomacyDonationVerdict? LastVerdict { get; private set; }

    public DiplomacyDonateGoldInterface(IEnumerable<IWorkshopModule> declaredModules)
    {
        this.declaredModules = (declaredModules ?? Enumerable.Empty<IWorkshopModule>()).ToArray();
    }

    public DiplomacyDonationVerdict TryApplyDonation(Hero giver, Clan clan, int amount)
    {
        DiplomacyDonationVerdict verdict = Evaluate(giver, clan, amount) ?? DiplomacyDonationVerdict.Applied;
        LastVerdict = verdict;
        return verdict;
    }

    private DiplomacyDonationVerdict? Evaluate(Hero giver, Clan clan, int amount)
    {
        if (giver == null || clan == null) return DiplomacyDonationVerdict.InvalidTarget;

        // The client's slider is capped by whatever gold IT believed the giver had; the server's
        // books are the only ones that count.
        if (amount <= 0 || amount > giver.Gold) return DiplomacyDonationVerdict.InvalidAmount;

        string moduleId = DiplomacyModuleIdOrDefault();
        ModOptions options = ModConfigProvider.ModOptions;
        if (!options.IsWorkshopModuleEnabled(moduleId)) return DiplomacyDonationVerdict.ModuleDisabled;
        if (!WorkshopModuleRegistrar.ResolveLiveModules(declaredModules, options)
                .Any(module => string.Equals(module.ModuleId, moduleId, StringComparison.Ordinal)))
        {
            // Not disabled (checked above), so the remaining reason a declared module is not live
            // is that the pinned build is not loaded byte-for-byte.
            return DiplomacyDonationVerdict.ModuleNotInstalled;
        }

        var applyMethod = ResolveApplyFromHeroToClan();
        if (applyMethod == null) return DiplomacyDonationVerdict.ModuleNotInstalled;

        try
        {
            applyMethod.Invoke(null, new object[] { giver, clan, amount });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Diplomacy donation apply failed for {GiverId} -> {ClanId}", giver.StringId, clan.StringId);
            return DiplomacyDonationVerdict.ApplyFailed;
        }

        ApplyRelationGain(giver, clan, amount);

        Logger.Debug("Applied Diplomacy donation: {Amount} gold from {GiverId} to {ClanId}",
            amount, giver.StringId, clan.StringId);
        return null;
    }

    private string DiplomacyModuleIdOrDefault() =>
        declaredModules.FirstOrDefault(module => module is DiplomacyModule)?.ModuleId
            ?? "Bannerlord.Diplomacy";

    private static System.Reflection.MethodInfo ResolveApplyFromHeroToClan()
    {
        var type = DiplomacyCompatibilityPolicy.ResolveType("Diplomacy.Actions.GiveGoldToClanAction");
        return type == null ? null : AccessTools.Method(type, "ApplyFromHeroToClan");
    }

    /// <summary>
    /// The VM's <c>GetBaseRelationValueOfCurrentGoldCost</c>, verbatim: gold → influence → the
    /// support-a-clan relation/influence exchange rate. Deterministic (native models, no roll), so
    /// recomputing it here from the routed amount cannot diverge from what the client's dialog
    /// previewed — and a forged amount buys exactly the relation the server's own math says.
    /// </summary>
    private static void ApplyRelationGain(Hero giver, Clan clan, int amount)
    {
        if (clan == giver.Clan || clan.Leader == null) return;

        var model = Campaign.Current?.Models?.DiplomacyModel;
        if (model == null) return;

        float influence = amount * model.DenarsToInfluence();
        float exchange = model.GetRelationValueOfSupportingClan() /
                         (float)model.GetInfluenceCostOfSupportingClan();
        int gain = (int)Math.Round(influence * exchange);
        if (gain <= 0) return;

        ChangeRelationAction.ApplyRelationChangeBetweenHeroes(giver, clan.Leader, gain, showQuickNotification: false);
    }
}
