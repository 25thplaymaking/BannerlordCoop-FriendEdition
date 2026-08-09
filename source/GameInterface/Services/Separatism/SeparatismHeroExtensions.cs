using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.Separatism;

internal static class SeparatismHeroExtensions
{
    internal static bool HasGoodRelationWith(this Hero hero, Hero otherHero)
    {
        if (hero.IsFriend(otherHero)) return true;
        if (hero.IsEnemy(otherHero)) return false;
        return hero.Culture == otherHero.Culture;
    }
}
