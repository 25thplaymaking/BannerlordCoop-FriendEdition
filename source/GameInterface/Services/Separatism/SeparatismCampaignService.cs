using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Registry.Auto;
using GameInterface.Services.Banners.Messages;
using GameInterface.Services.Clans.Messages;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.Kingdoms.Messages;
using Helpers;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace GameInterface.Services.Separatism;

public interface ISeparatismCampaignService
{
    void OnNewGameCreated();
    void OnGameLoaded();
    void OnDailyTick();
    void OnDailyTickClan(Clan clan);
    bool TryRecruitFallenClan(Hero actor, Clan targetClan);
}

/// <summary>
/// Bannerlord 1.4.7/Coop port of Separatism 1.3.8's campaign rules. This service only runs on
/// the authoritative server. It intentionally uses Coop's existing action, lifetime and
/// collection funnels so live clients and join-in-progress clients converge on the same state.
/// </summary>
internal sealed class SeparatismCampaignService : ISeparatismCampaignService
{
    private static readonly ILogger Logger = LogManager.GetLogger<SeparatismCampaignService>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IKingdomMembershipState membershipState;
    private readonly IPlayerManager playerManager;
    private readonly IModConfig modConfig;

    public SeparatismCampaignService(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IKingdomMembershipState membershipState,
        IPlayerManager playerManager,
        IModConfig modConfig)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.membershipState = membershipState;
        this.playerManager = playerManager;
        this.modConfig = modConfig;
    }

    private SeparatismOptions Options
    {
        get
        {
            // The new-game follow-up can precede CampaignReady. Read the host file directly here
            // so chaos-start honors the configured value even before the normal config broadcast.
            var data = modConfig.Data?.ModOptions?.Separatism;
            return data == null ? ModConfigProvider.ModOptions.Separatism : new SeparatismOptions(data);
        }
    }

    public void OnNewGameCreated()
    {
        var options = Options;
        if (!CanMutate(options) || !options.ChaosStartEnabled) return;

        foreach (var clan in Kingdom.All
                     .Where(kingdom => kingdom != null)
                     .SelectMany(kingdom => kingdom.Clans.ToArray())
                     .Distinct()
                     .Where(IsReadyToGoAndNotEmpty)
                     .ToArray())
        {
            var oldKingdom = clan.Kingdom;
            var capital = clan.Settlements
                .Where(settlement => settlement?.Town != null)
                .OrderByDescending(settlement => settlement.Town.Prosperity)
                .FirstOrDefault();
            if (oldKingdom == null || capital == null) continue;

            if (TryCreateRebelKingdom(clan, capital, BuildChaosIntro(clan), oldKingdom, rebellion: false, out var rebelKingdom))
            {
                CopyPolicies(oldKingdom, rebelKingdom);
            }
        }

        foreach (var kingdom in Kingdom.All.Where(HasSettlements).ToArray())
        {
            int currentWars = GetEnemyKingdoms(kingdom).Count();
            foreach (var neighbor in GetCloseKingdoms(kingdom.RulingClan).Where(candidate => candidate != kingdom))
            {
                if (currentWars >= options.MinimalNumberOfWarsPerChaosKingdom) break;
                if (!kingdom.IsAtWarWith(neighbor))
                {
                    DeclareWarAction.ApplyByDefault(neighbor, kingdom);
                    currentWars++;
                }
            }
        }
    }

    public void OnGameLoaded()
    {
        var options = Options;
        if (!CanMutate(options)) return;

        foreach (var kingdom in Kingdom.All.ToArray())
        {
            if (kingdom == null) continue;
            kingdom.EncyclopediaText ??= TextObject.GetEmpty();
            if (kingdom.EncyclopediaTitle == null || kingdom.EncyclopediaRulerTitle == null)
            {
                GetKingdomText(kingdom.RulingClan, out var name, out var rulerTitle);
                kingdom.EncyclopediaTitle ??= name;
                kingdom.EncyclopediaRulerTitle ??= rulerTitle;
            }
        }

        RemoveEmptyKingdoms(options);
    }

    public void OnDailyTick()
    {
        var options = Options;
        if (!CanMutate(options)) return;

        TryNationalRebellion(options);
        RemoveEmptyKingdoms(options);
    }

    public void OnDailyTickClan(Clan clan)
    {
        var options = Options;
        if (!CanMutate(options) || clan == null) return;

        RunDailyClanTransitions(
            () => TryLordRebellionOrFloatingTitle(clan, options),
            () => TryAnarchyRebellion(clan, options));
    }

    public bool TryRecruitFallenClan(Hero actor, Clan targetClan)
    {
        var options = Options;
        Kingdom actorKingdom = actor?.Clan?.Kingdom;
        Hero targetLeader = targetClan?.Leader;
        if (!CanMutate(options) ||
            actorKingdom == null ||
            actorKingdom.Leader != actor ||
            actorKingdom.RulingClan != actor.Clan ||
            targetClan == null ||
            targetLeader == null ||
            targetClan.Kingdom != null ||
            targetClan.IsMinorFaction ||
            targetLeader.MapFaction?.Leader != targetLeader ||
            FactionManager.IsAtWarAgainstFaction(targetLeader.MapFaction, actor.MapFaction))
            return false;

        CampaignTime originalFactionChangeTime = targetClan.LastFactionChangeTime;
        try
        {
            MoveClan(targetClan, oldKingdom: null, actorKingdom, rebellion: false);
            if (targetClan.Kingdom != actorKingdom || !actorKingdom.Clans.Contains(targetClan))
                throw new InvalidOperationException("The fallen clan did not enter the ruler's kingdom.");

            return true;
        }
        catch (Exception exception)
        {
            try
            {
                if (targetClan.Kingdom != null || actorKingdom.Clans.Contains(targetClan))
                {
                    membershipState.MoveClanToKingdom(
                        targetClan.Kingdom,
                        kingdom: null,
                        targetClan,
                        publishCollectionChanges: true,
                        republishExistingCollections: true);
                }
                targetClan.LastFactionChangeTime = originalFactionChangeTime;
            }
            catch (Exception rollbackException)
            {
                throw new InvalidOperationException(
                    "Separatism fallen-clan recruitment failed and rollback was incomplete.",
                    new AggregateException(exception, rollbackException));
            }

            Logger.Error(
                exception,
                "[Separatism] Failed to recruit fallen clan {Clan} into {Kingdom}",
                targetClan.Name,
                actorKingdom.Name);
            return false;
        }
    }

    internal static void RunDailyClanTransitions(
        Func<bool> tryLordOrFloatingTitle,
        Action tryAnarchy)
    {
        if (!tryLordOrFloatingTitle()) tryAnarchy();
    }

    private static bool CanMutate(SeparatismOptions options) =>
        ModInformation.IsServer && options.Enabled && Campaign.Current?.CampaignObjectManager != null;

    private bool TryLordRebellionOrFloatingTitle(Clan clan, SeparatismOptions options)
    {
        if (clan.Kingdom == null || !IsReady(clan)) return false;

        var kingdom = clan.Kingdom;
        var ruler = kingdom.Leader;
        if (ruler == null || clan.Leader == null) return false;

        if (clan.Leader != ruler)
        {
            if (!options.LordRebellionsEnabled || clan.Leader.HasGoodRelationWith(ruler)) return false;

            int kingdomFiefs = GetFiefWeight(kingdom);
            int nonMercenaryClans = Math.Max(1, kingdom.Clans.Count(item => !item.IsUnderMercenaryService));
            int clanFiefs = GetFiefWeight(clan);
            bool hasEnoughFiefs = kingdomFiefs > 0 &&
                ((options.AverageAmountOfKingdomFiefsIsEnoughToRebel && clanFiefs >= (float)kingdomFiefs / nonMercenaryClans)
                 || clanFiefs >= options.MinimalAmountOfKingdomFiefsToRebel);
            if (!hasEnoughFiefs || !Roll(options.DailyLordRebellionChance)) return false;

            var capital = clan.Settlements
                .Where(settlement => settlement?.Town != null)
                .OrderByDescending(settlement => settlement.Town.Prosperity)
                .FirstOrDefault();
            if (capital == null) return false;

            var oldClans = kingdom.Clans.ToArray();
            if (!TryCreateRebelKingdom(clan, capital, BuildLordRebellionIntro(clan, kingdom), kingdom, rebellion: true, out var rebelKingdom)) return false;

            CopyPolicies(kingdom, rebelKingdom);
            ApplyRebellionRelations(clan, kingdom, oldClans, options);
            InheritWars(rebelKingdom, kingdom, options);
            DeclareWarAction.ApplyByRebellion(kingdom, rebelKingdom);
            LogOutcome("Lord rebellion", clan, rebelKingdom);
            return true;
        }

        if (kingdom.Clans.Count(item => item?.Leader?.IsAlive == true) != 1) return false;

        if (!clan.Settlements.Any())
        {
            MoveClan(clan, kingdom, null, rebellion: false);
            DestroyKingdom(kingdom);
            Logger.Information("[Separatism] {Kingdom} was abandoned by {Clan}", kingdom.Name, clan.Name);
            return true;
        }

        if (options.AllowUnions && TryUnion(clan, options)) return true;

        var supporter = Clan.All
            .Where(candidate => IsReadyToGoAndEmpty(candidate)
                                && candidate.Tier <= clan.Tier
                                && candidate.Leader.HasGoodRelationWith(clan.Leader)
                                && (candidate.Kingdom == null
                                    || candidate.Kingdom.Leader == null
                                    || !candidate.Leader.HasGoodRelationWith(candidate.Kingdom.Leader)))
            .OrderByDescending(candidate => candidate.CurrentTotalStrength)
            .FirstOrDefault();
        if (supporter == null) return false;

        var previous = supporter.Kingdom;
        MoveClan(supporter, previous, kingdom, rebellion: false);
        ChangeRelation(supporter.Leader, clan.Leader, options.RelationChangeRulerWithSupporter);
        Logger.Information("[Separatism] {Clan} joined {Kingdom} as a supporting clan", supporter.Name, kingdom.Name);
        return true;
    }

    private void TryNationalRebellion(SeparatismOptions options)
    {
        if (!options.NationalRebellionsEnabled) return;

        var groups = Clan.All
            .Where(clan => IsReadyToGoAndNotEmpty(clan)
                           && clan.Culture != clan.Kingdom?.Culture
                           && clan.Settlements.Any(settlement => settlement.Culture == clan.Culture))
            .GroupBy(clan => new { clan.Kingdom, clan.Culture })
            .OrderByDescending(group => group.Count())
            .ToArray();

        foreach (var group in groups)
        {
            if (group.Count() < options.MinimalRequiredNumberOfNativeLords || !Roll(options.DailyNationalRebellionChance)) continue;

            var participants = group.OrderByDescending(clan => clan.Tier).ThenByDescending(clan => clan.Renown).ToArray();
            var leaderClan = participants.First();
            var oldKingdom = group.Key.Kingdom;
            var oldClans = oldKingdom.Clans.ToArray();
            var capital = leaderClan.Settlements
                .Where(settlement => settlement?.Town != null)
                .OrderByDescending(settlement => settlement.Town.Prosperity)
                .FirstOrDefault();
            if (capital == null) continue;

            if (!TryCreateRebelKingdom(leaderClan, capital, BuildNationalIntro(leaderClan, oldKingdom), oldKingdom, rebellion: true, out var rebelKingdom)) continue;

            CopyPolicies(oldKingdom, rebelKingdom);
            ApplyRebellionRelations(leaderClan, oldKingdom, oldClans, options);

            foreach (var participant in participants.Skip(1))
            {
                MoveClan(participant, oldKingdom, rebelKingdom, rebellion: true);
                ApplyRebellionRelations(participant, oldKingdom, oldClans, options);
            }

            for (int i = 0; i < participants.Length; i++)
            {
                for (int j = i + 1; j < participants.Length; j++)
                {
                    ChangeRelation(participants[i].Leader, participants[j].Leader, options.RelationChangeNationalRebellionClans);
                }
            }

            InheritWars(rebelKingdom, oldKingdom, options);
            DeclareWarAction.ApplyByRebellion(oldKingdom, rebelKingdom);
            LogOutcome("National rebellion", leaderClan, rebelKingdom);
            break;
        }
    }

    private void TryAnarchyRebellion(Clan ownerClan, SeparatismOptions options)
    {
        if (!options.AnarchyRebellionsEnabled || GetFiefWeight(ownerClan) < options.CriticalAmountOfFiefsPerSingleClan) return;

        var neglectedTowns = ownerClan.Settlements
            .Where(settlement => settlement?.IsTown == true
                                 && CampaignTime.Hours(settlement.LastVisitTimeOfOwner)
                                 + CampaignTime.Days(options.NumberOfDaysAfterOwnerVisitToKeepOrder) < CampaignTime.Now)
            .OrderByDescending(settlement => Distance(settlement.Position, ownerClan.FactionMidSettlement?.Position))
            .ToArray();
        if (neglectedTowns.Length == 0) return;

        var candidates = Clan.All.Where(IsReadyToGoAndEmpty).ToArray();
        foreach (var town in neglectedTowns)
        {
            var rebelClan = candidates
                .Where(candidate => candidate.Culture == town.Culture)
                .OrderByDescending(candidate => candidate.CurrentTotalStrength)
                .FirstOrDefault();
            if (rebelClan == null || !Roll(options.DailyAnarchyRebellionChance)) continue;

            var oldKingdom = ownerClan.Kingdom;
            var rebelClanPreviousKingdom = rebelClan.Kingdom;
            if (oldKingdom == null) continue;

            var seized = new List<Settlement> { town };
            if (options.BonusRebelFiefForHighTierClan && rebelClan.Tier > 4)
            {
                var nearbyCastle = FindSettlementsAround(town, 50f)
                    .Where(settlement => settlement?.OwnerClan == ownerClan
                                         && settlement.IsCastle
                                         && settlement.Culture == town.Culture)
                    .OrderBy(settlement => Distance(settlement.Position, town.Position))
                    .FirstOrDefault();
                if (nearbyCastle != null) seized.Add(nearbyCastle);
            }

            if (!TryCreateRebelKingdom(
                    rebelClan,
                    town,
                    BuildAnarchyIntro(rebelClan, ownerClan, town),
                    rebelClanPreviousKingdom,
                    rebellion: false,
                    out var rebelKingdom)) continue;

            CopyPolicies(oldKingdom, rebelKingdom);
            foreach (var settlement in seized)
            {
                ChangeOwnerOfSettlementAction.ApplyByLeaveFaction(rebelClan.Leader, settlement);
            }

            InheritWars(rebelKingdom, oldKingdom, options);
            DeclareWarAction.ApplyByRebellion(oldKingdom, rebelKingdom);
            LogOutcome("Anarchy rebellion", rebelClan, rebelKingdom);
            break;
        }
    }

    private bool TryUnion(Clan clan, SeparatismOptions options)
    {
        var kingdom = clan.Kingdom;
        var enemies = GetEnemyKingdoms(kingdom).ToArray();
        var candidates = enemies
            .SelectMany(GetEnemyKingdoms)
            .Distinct()
            .Except(enemies)
            .Intersect(GetCloseKingdoms(clan))
            .Where(candidate => candidate != kingdom && HasSettlements(candidate) && !ContainsPlayerClan(candidate))
            .ToArray();

        foreach (var ally in candidates)
        {
            if (ally.Leader == null || !kingdom.Leader.HasGoodRelationWith(ally.Leader) || ally.RulingClan.Tier < clan.Tier) continue;

            foreach (var commonEnemy in GetEnemyKingdoms(ally).Intersect(enemies).Where(HasSettlements))
            {
                float sourceToEnemy = Distance(commonEnemy.FactionMidSettlement?.Position, kingdom.FactionMidSettlement?.Position);
                float allyToEnemy = Distance(commonEnemy.FactionMidSettlement?.Position, ally.FactionMidSettlement?.Position);
                float sourceToAlly = Distance(kingdom.FactionMidSettlement?.Position, ally.FactionMidSettlement?.Position);
                if (sourceToAlly > Math.Sqrt(sourceToEnemy * sourceToEnemy + allyToEnemy * allyToEnemy)
                    && !(IsInsideTerritory(kingdom, commonEnemy) && IsInsideTerritory(ally, commonEnemy)))
                {
                    continue;
                }

                MoveClan(clan, kingdom, ally, rebellion: false);
                if (kingdom.RulingClan == clan)
                {
                    kingdom._rulingClan = null;
                }
                ChangeRelation(clan.Leader, ally.Leader, options.RelationChangeUnitedRulers);
                InheritWars(ally, kingdom, options);
                DestroyKingdom(kingdom);
                Logger.Information("[Separatism] {Kingdom} united with {Ally} against {Enemy}", kingdom.Name, ally.Name, commonEnemy.Name);
                return true;
            }
        }

        return false;
    }

    private bool TryCreateRebelKingdom(
        Clan rulingClan,
        Settlement capital,
        TextObject intro,
        Kingdom oldKingdom,
        bool rebellion,
        out Kingdom kingdom)
    {
        kingdom = null;
        if (rulingClan == null || capital == null || rulingClan.Culture == null || rulingClan.Leader == null) return false;
        if (!objectManager.TryGetId(rulingClan, out var clanId)) return false;

        GetKingdomText(rulingClan, out var kingdomName, out var rulerTitle);
        intro.SetTextVariable("RebelKingdom", kingdomName);

        var options = Options;
        var colors = GetRebelColors(rulingClan, options);
        var originalClanBanner = rulingClan.Banner;
        uint originalClanColor = rulingClan.Color;
        uint originalClanColor2 = rulingClan.Color2;
        var banner = originalClanBanner == null ? new Banner() : new Banner(originalClanBanner);
        banner.ChangePrimaryColor(colors.primary);
        banner.ChangeIconColors(colors.secondary);

        string baseId = string.IsNullOrWhiteSpace(rulingClan.StringId)
            ? "separatist_kingdom"
            : rulingClan.StringId + "_separatist_kingdom";

        Kingdom existingKingdom = Kingdom.All.FirstOrDefault(candidate => candidate?.StringId == baseId)
                                   ?? MBObjectManager.Instance?.GetObject<Kingdom>(baseId);
        if (existingKingdom != null && !existingKingdom.IsEliminated)
        {
            Logger.Error(
                "[Separatism] Refusing to overwrite active kingdom {KingdomId} for {Clan}",
                baseId,
                rulingClan.Name);
            return false;
        }

        bool kingdomPreExisted = existingKingdom != null;
        kingdom = existingKingdom ?? Kingdom.CreateKingdom(baseId);
        bool kingdomWasRegistered = objectManager.TryGetId(kingdom, out _);
        bool kingdomWasEliminated = kingdom.IsEliminated;
        var originalKingdomName = kingdom.Name;
        var originalKingdomInformalName = kingdom.InformalName;
        var originalKingdomCulture = kingdom.Culture;
        var originalKingdomBanner = kingdom.Banner;
        uint originalKingdomColor = kingdom.Color;
        uint originalKingdomColor2 = kingdom.Color2;
        var originalKingdomEncyclopediaText = kingdom.EncyclopediaText;
        var originalKingdomEncyclopediaTitle = kingdom.EncyclopediaTitle;
        var originalKingdomEncyclopediaRulerTitle = kingdom.EncyclopediaRulerTitle;
        var originalRulingClan = kingdom.RulingClan;

        try
        {
            // A clan can found, lose and later re-found its separatist kingdom. Reuse the native
            // object when it still exists instead of attempting to register the same MBObject id
            // twice (the original Separatism implementation follows the same rule).
            if (kingdom.IsEliminated)
            {
                // DestroyKingdomAction keeps the native object registered and only deactivates it.
                // Re-found titles therefore need an explicit, AutoSync-visible reactivation.
                kingdom.ReactivateKingdom();
            }
            KingdomRegistry.EnsureRuntimeCollections(kingdom);

            if (!options.KeepRebelBannerColors)
            {
                rulingClan.Banner = new Banner(banner);
                rulingClan.Color = colors.primary;
                rulingClan.Color2 = colors.secondary;
                banner = new Banner(rulingClan.Banner);
            }

            kingdom.InitializeKingdom(
                kingdomName,
                kingdomName,
                rulingClan.Culture,
                banner,
                colors.primary,
                colors.secondary,
                capital,
                intro,
                kingdomName,
                rulerTitle);
            kingdom.RulingClan = rulingClan;
            KingdomCreator.EnsureKingdomRegisteredInCampaign(kingdom, Campaign.Current.CampaignObjectManager);

            if (!objectManager.TryGetId(kingdom, out var kingdomId))
            {
                messageBroker.Publish(this, new InstanceCreated<Kingdom>(kingdom));
            }
            if (!objectManager.TryGetId(kingdom, out kingdomId))
            {
                Logger.Error("[Separatism] Failed to register rebel kingdom for {Clan}", rulingClan.Name);
                return false;
            }

            // Constructor lifetime patches normally register the object before InitializeKingdom.
            // Repeat the presentation assignments after the explicit registration fallback so the
            // AutoSync layer can always resolve the new owner and deliver the complete client state.
            kingdom.Banner = banner;
            kingdom.Color = colors.primary;
            kingdom.Color2 = colors.secondary;
            kingdom.Culture = rulingClan.Culture;
            kingdom.Name = kingdomName;
            kingdom.InformalName = kingdomName;
            kingdom.EncyclopediaText = intro;
            kingdom.EncyclopediaTitle = kingdomName;
            kingdom.EncyclopediaRulerTitle = rulerTitle;
            kingdom._rulingClan = rulingClan;

            MoveClan(rulingClan, oldKingdom, kingdom, rebellion);
            rulingClan.SetInitialHomeSettlement(capital);

            objectManager.TryGetId(rulingClan.Culture, out var cultureId);
            messageBroker.Publish(this, new PlayerKingdomCreated(
                string.Empty,
                kingdomId,
                kingdomName.ToString(),
                clanId,
                cultureId));
            return true;
        }
        catch (Exception ex)
        {
            var rollbackFailures = new List<string>();
            try
            {
                if (rulingClan.Kingdom != oldKingdom)
                {
                    membershipState.MoveClanToKingdom(
                        rulingClan.Kingdom,
                        oldKingdom,
                        rulingClan,
                        publishCollectionChanges: true);
                }

                rulingClan.Banner = originalClanBanner;
                rulingClan.Color = originalClanColor;
                rulingClan.Color2 = originalClanColor2;
            }
            catch (Exception rollbackException)
            {
                rollbackFailures.Add("clan restore failed: " + rollbackException.Message);
            }

            try
            {
                if (!kingdomPreExisted || kingdomWasEliminated)
                {
                    if (!kingdom.IsEliminated) DestroyKingdomAction.Apply(kingdom);
                }

                if (kingdomPreExisted)
                {
                    kingdom.Name = originalKingdomName;
                    kingdom.InformalName = originalKingdomInformalName;
                    kingdom.Culture = originalKingdomCulture;
                    kingdom.Banner = originalKingdomBanner;
                    kingdom.Color = originalKingdomColor;
                    kingdom.Color2 = originalKingdomColor2;
                    kingdom.EncyclopediaText = originalKingdomEncyclopediaText;
                    kingdom.EncyclopediaTitle = originalKingdomEncyclopediaTitle;
                    kingdom.EncyclopediaRulerTitle = originalKingdomEncyclopediaRulerTitle;
                    kingdom._rulingClan = originalRulingClan;
                }

                if (!kingdomWasRegistered && objectManager.Contains(kingdom))
                {
                    objectManager.Remove(kingdom);
                }
            }
            catch (Exception rollbackException)
            {
                rollbackFailures.Add("kingdom restore failed: " + rollbackException.Message);
            }

            if (rollbackFailures.Count != 0)
            {
                throw new InvalidOperationException(
                    "Separatism kingdom creation failed and rollback was incomplete: " +
                    string.Join("; ", rollbackFailures),
                    ex);
            }

            Logger.Error(ex, "[Separatism] Failed to create rebel kingdom for {Clan}", rulingClan.Name);
            kingdom = null;
            return false;
        }
    }

    private void MoveClan(Clan clan, Kingdom oldKingdom, Kingdom newKingdom, bool rebellion)
    {
        FinishStaleHostileActions(clan, newKingdom);
        clan.EndMercenaryService(true);
        membershipState.MoveClanToKingdom(oldKingdom, newKingdom, clan, publishCollectionChanges: true);
        CampaignEventDispatcher.Instance.OnClanChangedKingdom(
            clan,
            oldKingdom,
            newKingdom,
            rebellion
                ? ChangeKingdomAction.ChangeKingdomActionDetail.LeaveWithRebellion
                : newKingdom == null
                    ? ChangeKingdomAction.ChangeKingdomActionDetail.LeaveKingdom
                    : ChangeKingdomAction.ChangeKingdomActionDetail.JoinKingdom,
            true);

        if (clan.Banner != null)
        {
            // The service intentionally bypasses ChangeKingdomAction.ApplyInternal, so its normal
            // banner refresh postfix never runs. Reuse the established banner packet to update
            // client party/nameplate/settlement visuals after the membership transition.
            messageBroker.Publish(this, new PlayerBannerChanged(clan));
        }
    }

    private static void FinishStaleHostileActions(Clan clan, Kingdom newKingdom)
    {
        if (clan == null || newKingdom == null) return;

        foreach (var otherKingdom in Kingdom.All.ToArray())
        {
            if (otherKingdom == null || otherKingdom == newKingdom || newKingdom.IsAtWarWith(otherKingdom)) continue;

            FactionHelper.FinishAllRelatedHostileActionsOfFactionToFaction(clan, otherKingdom);
            FactionHelper.FinishAllRelatedHostileActionsOfFactionToFaction(otherKingdom, clan);
        }

        foreach (var otherClan in Clan.All.ToArray())
        {
            if (otherClan == null || otherClan == clan || otherClan.Kingdom != null || newKingdom.IsAtWarWith(otherClan)) continue;
            FactionHelper.FinishAllRelatedHostileActions(clan, otherClan);
        }
    }

    private void ApplyRebellionRelations(Clan rebel, Kingdom oldKingdom, IEnumerable<Clan> oldClans, SeparatismOptions options)
    {
        foreach (var clan in oldClans.Where(item => item?.Leader != null && item != rebel))
        {
            int change = options.RelationChangeRebelWithRulerVassals;
            if (clan == oldKingdom.RulingClan) change = options.RelationChangeRebelWithRuler;
            else if (clan.Leader.IsFriend(oldKingdom.Leader)) change = options.RelationChangeRebelWithRulerFriendVassals;
            else if (clan.Leader.IsEnemy(oldKingdom.Leader)) change = options.RelationChangeRebelWithRulerEnemyVassals;
            ChangeRelation(rebel.Leader, clan.Leader, change);
        }
    }

    private static void ChangeRelation(Hero first, Hero second, int amount)
    {
        if (first == null || second == null || amount == 0) return;
        ChangeRelationAction.ApplyRelationChangeBetweenHeroes(first, second, amount, true);
    }

    private static void CopyPolicies(Kingdom source, Kingdom destination)
    {
        if (source?._activePolicies == null || destination == null) return;
        foreach (var policy in source.ActivePolicies.ToArray())
        {
            if (!destination.ActivePolicies.Contains(policy)) destination.AddPolicy(policy);
        }
    }

    private static void InheritWars(Kingdom destination, Kingdom source, SeparatismOptions options)
    {
        if (!options.KeepOriginalKingdomWars || destination == null || source == null) return;
        foreach (var enemy in GetEnemyKingdoms(source).Where(enemy => enemy != destination).ToArray())
        {
            if (!destination.IsAtWarWith(enemy)) DeclareWarAction.ApplyByDefault(enemy, destination);
        }
    }

    private void RemoveEmptyKingdoms(SeparatismOptions options)
    {
        foreach (var kingdom in Kingdom.All
                     .Where(kingdom => kingdom != null
                                      && kingdom.Clans.Count(clan => clan?.Leader?.IsAlive == true) == 0
                                      && (!options.KeepEmptyKingdoms || IsSeparatistKingdom(kingdom)))
                     .ToArray())
        {
            DestroyKingdom(kingdom);
        }
    }

    private void DestroyKingdom(Kingdom kingdom)
    {
        if (kingdom == null || kingdom.IsEliminated) return;
        if (!objectManager.TryGetId(kingdom, out var kingdomId)) return;

        // NetworkDestroyKingdom is normally client-requested. Here the server already owns the
        // decision, so broadcast the apply command and perform the same action locally once.
        network.SendAll(new NetworkDestroyKingdom(kingdomId));
        DestroyKingdomAction.Apply(kingdom);
    }

    private bool IsReady(Clan clan)
    {
        if (clan == null || clan.Leader == null || !clan.Leader.IsAlive || clan.Leader.IsPrisoner) return false;
        if (IsPlayerClan(clan) || clan.IsUnderMercenaryService || clan.IsClanTypeMercenary) return false;
        if (clan.IsMinorFaction || clan.IsBanditFaction || clan.IsMafia || clan.IsNomad || clan.IsOutlaw
            || clan.IsRebelClan || clan.IsSect || clan.StringId == "test_clan") return false;
        if (clan.Fiefs.Any(fief => fief?.IsUnderSiege == true)) return false;
        return !clan.WarPartyComponents.Any(component => component?.MobileParty?.MapEvent != null);
    }

    private bool IsReadyToGo(Clan clan) => IsReady(clan) && clan.Kingdom?.RulingClan != clan;
    private bool IsReadyToGoAndEmpty(Clan clan) => IsReadyToGo(clan) && !clan.Settlements.Any();
    private bool IsReadyToGoAndNotEmpty(Clan clan) => IsReadyToGo(clan) && clan.Kingdom != null && clan.Settlements.Any();
    private bool IsPlayerClan(Clan clan) => clan != null && (playerManager.Contains(clan) || clan == Clan.PlayerClan);
    private bool ContainsPlayerClan(Kingdom kingdom) => kingdom?.Clans.Any(IsPlayerClan) == true;

    internal static IReadOnlyList<Kingdom> GetCloseKingdoms(Clan clan)
    {
        if (clan?.FactionMidSettlement == null) return Array.Empty<Kingdom>();
        var distances = Kingdom.All
            .Where(kingdom => kingdom?.FactionMidSettlement != null)
            .Select(kingdom => new { Kingdom = kingdom, Distance = Distance(kingdom.FactionMidSettlement.Position, clan.FactionMidSettlement.Position) })
            .ToArray();
        if (distances.Length == 0) return Array.Empty<Kingdom>();
        float average = distances.Average(item => item.Distance);
        return distances.Where(item => item.Distance <= average).OrderBy(item => item.Distance).Select(item => item.Kingdom).ToArray();
    }

    private static IEnumerable<Kingdom> GetEnemyKingdoms(Kingdom kingdom) =>
        kingdom == null ? Enumerable.Empty<Kingdom>() : FactionHelper.GetEnemyKingdoms(kingdom);

    private static bool HasSettlements(Kingdom kingdom) => kingdom?.Settlements.Any() == true;

    private static bool IsInsideTerritory(Kingdom inner, Kingdom outer)
    {
        var innerPoints = inner?.Settlements.Where(settlement => settlement.IsTown || settlement.IsCastle).Select(settlement => settlement.Position).ToArray();
        var outerPoints = outer?.Settlements.Where(settlement => settlement.IsTown || settlement.IsCastle).Select(settlement => settlement.Position).ToArray();
        if (innerPoints == null || outerPoints == null || innerPoints.Length == 0 || outerPoints.Length == 0) return false;
        return innerPoints.Max(point => point.X) <= outerPoints.Max(point => point.X)
               && innerPoints.Max(point => point.Y) <= outerPoints.Max(point => point.Y)
               && innerPoints.Min(point => point.X) >= outerPoints.Min(point => point.X)
               && innerPoints.Min(point => point.Y) >= outerPoints.Min(point => point.Y);
    }

    private static IEnumerable<Settlement> FindSettlementsAround(Settlement origin, float radius)
    {
        var search = Settlement.StartFindingLocatablesAroundPosition(origin.Position.ToVec2(), radius);
        Settlement next;
        while ((next = Settlement.FindNextLocatable(ref search)) != null)
        {
            yield return next;
        }
    }

    private static float Distance(CampaignVec2? first, CampaignVec2? second)
    {
        if (!first.HasValue || !second.HasValue) return float.MaxValue;
        var value = first.Value;
        return value.Distance(second.Value);
    }

    private static int GetFiefWeight(IFaction faction) =>
        faction?.Settlements?.Sum(settlement => settlement.IsTown ? 2 : settlement.IsCastle ? 1 : 0) ?? 0;

    private static bool Roll(float chance) => chance >= 1f || (chance > 0f && MBRandom.RandomFloat <= chance);

    private static bool IsSeparatistKingdom(Kingdom kingdom) =>
        kingdom?.StringId?.Contains("_separatist_kingdom") == true;

    private static (uint primary, uint secondary) GetRebelColors(Clan clan, SeparatismOptions options)
    {
        if (options.KeepRebelBannerColors) return (clan.Color, clan.Color2);

        var bannerManager = BannerManager.Instance;
        if (bannerManager?.ReadOnlyColorPalette == null) return (clan.Color, clan.Color2);

        var palette = bannerManager.ReadOnlyColorPalette.Values.Select(color => color.Color).Distinct().ToArray();
        if (palette.Length == 0) return (clan.Color, clan.Color2);
        if (options.SameColorsForAllRebels) return (palette.Max(), palette.Min());

        var usedPrimary = new HashSet<uint>(Kingdom.All.Select(kingdom => kingdom.Color));
        var usedSecondary = new HashSet<uint>(Kingdom.All.Select(kingdom => kingdom.Color2));
        var primaryChoices = palette.Where(color => !usedPrimary.Contains(color)).ToList();
        var secondaryChoices = palette.Where(color => !usedSecondary.Contains(color)).ToList();
        if (primaryChoices.Count == 0 || secondaryChoices.Count == 0) return (palette.Max(), palette.Min());

        uint primary = TakeRandom(primaryChoices);
        uint secondary = primary;
        while (secondaryChoices.Count > 0 && ColorDifference(primary, secondary) < 0.3)
        {
            secondary = TakeRandom(secondaryChoices);
        }
        return (primary, secondary);
    }

    private static uint TakeRandom(List<uint> colors)
    {
        int index = MBRandom.RandomInt(colors.Count);
        uint color = colors[index];
        colors.RemoveAt(index);
        return color;
    }

    private static double ColorDifference(uint first, uint second)
    {
        double firstLuminosity = (0.2126 * ((first >> 16) & 0xFF) + 0.7152 * ((first >> 8) & 0xFF) + 0.0722 * (first & 0xFF)) / 255.0;
        double secondLuminosity = (0.2126 * ((second >> 16) & 0xFF) + 0.7152 * ((second >> 8) & 0xFF) + 0.0722 * (second & 0xFF)) / 255.0;
        return Math.Abs(firstLuminosity - secondLuminosity);
    }

    private static void GetKingdomText(Clan clan, out TextObject kingdomName, out TextObject rulerTitle)
    {
        string nameTemplate = "{=Separatism_Kingdom_Name}Kingdom of {ClanName}";
        string titleTemplate = "{=Separatism_Kingdom_Ruler_Title}King";
        switch (clan?.Culture?.StringId)
        {
            case "aserai":
                nameTemplate = "{=Separatism_Sultanate_Name}Sultanate of {ClanName}";
                titleTemplate = "{=Separatism_Sultanate_Ruler_Title}Sultan";
                break;
            case "khuzait":
                nameTemplate = "{=Separatism_Khanate_Name}Khanate of {ClanName}";
                titleTemplate = "{=Separatism_Khanate_Ruler_Title}Khan";
                break;
            case "sturgia":
                nameTemplate = "{=Separatism_Principality_Name}Principality of {ClanName}";
                titleTemplate = "{=Separatism_Principality_Ruler_Title}Knyaz";
                break;
            case "empire":
                nameTemplate = "{=Separatism_Empire_Name}Empire of {ClanName}";
                titleTemplate = "{=Separatism_Empire_Ruler_Title}Emperor";
                break;
        }

        kingdomName = new TextObject(nameTemplate);
        kingdomName.SetTextVariable("ClanName", clan?.Name ?? TextObject.GetEmpty());
        rulerTitle = new TextObject(titleTemplate);
    }

    private static TextObject BuildChaosIntro(Clan clan)
    {
        var text = new TextObject("{=Separatism_Kingdom_Intro_Chaos}{RebelKingdom} was founded in {Year} during the great separation of Calradia.");
        text.SetTextVariable("Year", CampaignTime.Now.GetYear);
        text.SetTextVariable("ClanName", clan.Name);
        return text;
    }

    private static TextObject BuildLordRebellionIntro(Clan clan, Kingdom oldKingdom)
    {
        var text = new TextObject("{=Separatism_Kingdom_Intro}{RebelKingdom} was founded in {Year} when {ClanName} rebelled against {Ruler}, ruler of {Kingdom}.");
        text.SetTextVariable("Year", CampaignTime.Now.GetYear);
        text.SetTextVariable("ClanName", clan.Name);
        text.SetTextVariable("Ruler", oldKingdom.Leader?.Name ?? TextObject.GetEmpty());
        text.SetTextVariable("Kingdom", oldKingdom.Name);
        return text;
    }

    private static TextObject BuildNationalIntro(Clan clan, Kingdom oldKingdom)
    {
        var text = new TextObject("{=Separatism_Kingdom_Intro_National}{RebelKingdom} was founded in {Year} when the {Culture} people of {Kingdom} declared independence.");
        text.SetTextVariable("Year", CampaignTime.Now.GetYear);
        text.SetTextVariable("Culture", clan.Culture.Name);
        text.SetTextVariable("Kingdom", oldKingdom.Name);
        return text;
    }

    private static TextObject BuildAnarchyIntro(Clan rebelClan, Clan ownerClan, Settlement settlement)
    {
        var text = new TextObject("{=Separatism_Kingdom_Intro_Anarchy}{RebelKingdom} was founded in {Year} after the people of {Settlement} called {Ruler} to rule during anarchy in the fiefs of {ClanName}.");
        text.SetTextVariable("Year", CampaignTime.Now.GetYear);
        text.SetTextVariable("ClanName", ownerClan.Name);
        text.SetTextVariable("Settlement", settlement.Name);
        text.SetTextVariable("Ruler", rebelClan.Leader.Name);
        return text;
    }

    private static void LogOutcome(string type, Clan clan, Kingdom kingdom) =>
        Logger.Information("[Separatism] {Type}: {Clan} founded {Kingdom}", type, clan.Name, kingdom.Name);
}
