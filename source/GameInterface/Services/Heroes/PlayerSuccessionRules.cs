using GameInterface.Services.Heroes.Extensions;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace GameInterface.Services.Heroes;

/// <summary>
/// Succession policy for co-op player heroes now that the birth-and-death cycle is enabled.
///
/// A player hero may die only when their bloodline survives them: they must have at least one
/// living child, and their clan must hold an eligible successor the player can continue as.
/// Otherwise every <see cref="KillCharacterAction"/> path stays blocked, exactly as before.
/// </summary>
public static class PlayerSuccessionRules
{
    /// <summary>The host-configurable death gate: a living child keeps the bloodline alive.</summary>
    public static bool HasLivingChild(Hero hero)
    {
        if (hero?.Children == null) return false;

        foreach (var child in hero.Children)
        {
            if (child != null && child.IsAlive) return true;
        }

        return false;
    }

    /// <summary>
    /// Picks the successor the dying player's controller will continue as. Mirrors the native
    /// heir-apparent criteria (<c>Clan.GetHeirApparents</c>) but centers the scoring on the dying
    /// hero instead of the clan leader, and excludes heroes already controlled by another player.
    /// Direct children of the victim outrank other clan candidates outright; within a tier the
    /// native heir-selection score decides, with the string id as the deterministic tiebreak.
    /// </summary>
    public static bool TryGetSuccessor(Hero victim, out Hero successor)
    {
        successor = null;
        if (victim?.Clan == null || Campaign.Current == null) return false;
        if (!HasLivingChild(victim)) return false;

        int comesOfAge = Campaign.Current.Models.AgeModel.HeroComesOfAge;
        var model = Campaign.Current.Models.HeirSelectionCalculationModel;
        var children = new HashSet<Hero>(victim.Children);

        Hero maxSkillHero = victim;
        Hero best = null;
        bool bestIsChild = false;
        int bestScore = int.MinValue;

        foreach (var candidate in victim.Clan.Heroes)
        {
            if (candidate == victim || !candidate.IsAlive) continue;
            if (candidate.DeathMark != KillCharacterAction.KillCharacterActionDetail.None) continue;
            if (candidate.IsNotSpawned || candidate.IsDisabled || candidate.IsWanderer || candidate.IsNotable) continue;
            if (candidate.Age < comesOfAge) continue;
            if (candidate.IsPlayerHero()) continue;

            int score = model.CalculateHeirSelectionPoint(candidate, victim, ref maxSkillHero);
            bool isChild = children.Contains(candidate);

            bool better =
                best == null ||
                (isChild && !bestIsChild) ||
                (isChild == bestIsChild &&
                 (score > bestScore ||
                  (score == bestScore &&
                   string.CompareOrdinal(candidate.StringId, best.StringId) < 0)));

            if (better)
            {
                best = candidate;
                bestIsChild = isChild;
                bestScore = score;
            }
        }

        successor = best;
        return best != null;
    }
}
