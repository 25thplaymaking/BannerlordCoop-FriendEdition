using Common;
using Common.Messaging;
using Common.Util;
using GameInterface.Services.Actions.Patches;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.PartyComponents.Patches;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Players.Messages;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;

namespace GameInterface.Services.Players;

public interface IPlayerClanMembershipService : IGameAbstraction
{
    bool CanJoin(PartyBase applicantParty, PartyBase leaderParty);
    bool CanMarry(PartyBase proposingParty, PartyBase respondingParty);
    bool TryJoin(PartyBase applicantParty, PartyBase leaderParty);
    bool TryMarry(PartyBase proposingParty, PartyBase respondingParty);
    bool TrySeparate(Player member, bool emergency, out Player separated);
    bool TryLeave(Player member, out Player returned);
    bool TryGetClanLeader(Player member, out Player leader);
}

internal sealed class PlayerClanMembershipService : IPlayerClanMembershipService
{
    private readonly IPlayerManager playerManager;
    private readonly IObjectManager objectManager;
    private readonly IMessageBroker messageBroker;
    private readonly IPlayerPartyRestorer playerPartyRestorer;

    public PlayerClanMembershipService(
        IPlayerManager playerManager,
        IObjectManager objectManager,
        IMessageBroker messageBroker,
        IPlayerPartyRestorer playerPartyRestorer)
    {
        this.playerManager = playerManager;
        this.objectManager = objectManager;
        this.messageBroker = messageBroker;
        this.playerPartyRestorer = playerPartyRestorer;
    }

    public bool CanJoin(PartyBase applicantParty, PartyBase leaderParty)
    {
        var applicant = applicantParty?.LeaderHero;
        var leader = leaderParty?.LeaderHero;
        if (applicant == null || leader == null || applicant == leader) return false;
        if (leader.Clan == null || leader.Clan.Leader != leader || leader.Clan.Tier < 2) return false;
        if (applicant.Clan?.Kingdom?.Leader == applicant) return false;

        if (!TryGetPlayer(applicant, out var applicantPlayer) || !TryGetPlayer(leader, out _)) return false;

        return applicantPlayer.ClanMembershipMode == PlayerClanMembershipMode.PersonalClan ||
               objectManager.TryGetId(leader.Clan, out var leaderClanId) && applicantPlayer.ClanId == leaderClanId;
    }

    public bool CanMarry(PartyBase proposingParty, PartyBase respondingParty)
    {
        var proposer = proposingParty?.LeaderHero;
        var responder = respondingParty?.LeaderHero;
        if (proposer == null || responder == null || proposer == responder) return false;
        if (!TryGetPlayer(proposer, out _) || !TryGetPlayer(responder, out _)) return false;
        if (!proposer.IsAlive || !responder.IsAlive || proposer.Spouse != null || responder.Spouse != null) return false;

        return Campaign.Current?.Models?.MarriageModel?.IsCoupleSuitableForMarriage(proposer, responder) == true;
    }

    public bool TryJoin(PartyBase applicantParty, PartyBase leaderParty)
    {
        if (!CanJoin(applicantParty, leaderParty)) return false;

        var applicantHero = applicantParty.LeaderHero;
        var leaderHero = leaderParty.LeaderHero;
        var targetParty = leaderParty.MobileParty;
        var targetClan = leaderHero.Clan;
        if (!TryGetPlayer(applicantHero, out var applicant)) return false;
        if (!objectManager.TryGetIdWithLogging(targetParty, out var targetPartyId)) return false;
        if (!objectManager.TryGetIdWithLogging(targetClan, out var targetClanId)) return false;

        var firstJoin = applicant.ClanMembershipMode == PlayerClanMembershipMode.PersonalClan;
        if (firstJoin)
            TransferPermanentAssets(applicantHero, leaderHero);
        TransferPartyContents(applicantParty.MobileParty, targetParty, applicantHero);

        using (new AllowedThread())
        {
            applicantHero.Clan = targetClan;
            applicantParty.MobileParty.ActualClan = targetClan;
        }

        var replacement = new Player(
            applicant.ControllerId,
            applicant.HeroId,
            targetPartyId,
            targetClanId,
            applicant.CharacterObjectId,
            applicant.PersonalClanId,
            PlayerClanMembershipMode.Embedded,
            emergencyDetached: false);

        if (!playerManager.ReplacePlayer(applicant, replacement)) return false;

        AddHeroToPartyAction.Apply(applicantHero, targetParty);
        using (new AllowedThread())
            applicantHero.Gold = leaderHero.Gold;
        messageBroker.Publish(this, new PlayerRegistrationChanged(replacement));
        DestroyPartyAction.Apply(null, applicantParty.MobileParty);
        return true;
    }

    public bool TrySeparate(Player member, bool emergency, out Player separated)
    {
        separated = member;
        if (member == null || member.ClanMembershipMode != PlayerClanMembershipMode.Embedded ||
            !playerManager.TryGetPlayer(member.ControllerId, out var current) || !ReferenceEquals(member, current))
            return false;
        if (!objectManager.TryGetObjectWithLogging(member.HeroId, out Hero hero)) return false;
        if (!objectManager.TryGetObjectWithLogging(member.MobilePartyId, out MobileParty sharedParty)) return false;
        if (!objectManager.TryGetObjectWithLogging(member.ClanId, out Clan clan)) return false;
        if (!PlayerClanMembershipRules.CanCreateIndependentParty(
                clan.WarPartyComponents.Count,
                clan.WarPartyLimit,
                emergency))
            return false;

        var candidate = new Player(
            member.ControllerId,
            member.HeroId,
            mobilePartyId: null,
            member.ClanId,
            member.CharacterObjectId,
            member.PersonalClanId,
            PlayerClanMembershipMode.IndependentParty,
            emergency);
        var heroCount = sharedParty.MemberRoster.GetTroopCount(hero.CharacterObject);
        if (heroCount > 0)
            sharedParty.MemberRoster.AddToCounts(hero.CharacterObject, -heroCount);
        hero.PartyBelongedTo = sharedParty;

        if (!playerPartyRestorer.TryRestore(candidate, out separated))
        {
            if (heroCount > 0)
                sharedParty.MemberRoster.AddToCounts(hero.CharacterObject, heroCount);
            hero.PartyBelongedTo = sharedParty;
            return false;
        }

        if (!playerManager.ReplacePlayer(member, separated)) return false;
        messageBroker.Publish(this, new PlayerRegistrationChanged(separated));
        return true;
    }

    public bool TryLeave(Player member, out Player returned)
    {
        returned = member;
        if (member == null || member.ClanMembershipMode == PlayerClanMembershipMode.PersonalClan) return false;
        if (member.ClanMembershipMode == PlayerClanMembershipMode.Embedded &&
            !TrySeparate(member, emergency: true, out member))
            return false;

        if (!objectManager.TryGetObjectWithLogging(member.HeroId, out Hero hero)) return false;
        if (!objectManager.TryGetObjectWithLogging(member.MobilePartyId, out MobileParty party)) return false;
        if (!objectManager.TryGetObjectWithLogging(member.PersonalClanId, out Clan personalClan)) return false;

        using (new AllowedThread())
        {
            hero.Clan = personalClan;
            party.ActualClan = personalClan;
            personalClan.SetLeader(hero);
        }

        returned = new Player(
            member.ControllerId,
            member.HeroId,
            member.MobilePartyId,
            member.PersonalClanId,
            member.CharacterObjectId,
            member.PersonalClanId,
            PlayerClanMembershipMode.PersonalClan,
            emergencyDetached: false);
        if (!playerManager.ReplacePlayer(member, returned)) return false;

        using (new AllowedThread())
            hero.Gold = 0;
        messageBroker.Publish(this, new PlayerRegistrationChanged(returned));
        return true;
    }

    public bool TryGetClanLeader(Player member, out Player leader)
    {
        leader = null;
        if (member == null || !objectManager.TryGetObject(member.ClanId, out Clan clan) || clan.Leader == null)
            return false;
        return TryGetPlayer(clan.Leader, out leader);
    }

    public bool TryMarry(PartyBase proposingParty, PartyBase respondingParty)
    {
        if (!CanMarry(proposingParty, respondingParty)) return false;

        var proposer = proposingParty.LeaderHero;
        var responder = respondingParty.LeaderHero;
        using (new AllowedThread())
        {
            proposer.Spouse = responder;
            responder.Spouse = proposer;
        }

        ChangeRomanticStateAction.Apply(proposer, responder, Romance.RomanceLevelEnum.Marriage);
        CampaignEventDispatcher.Instance.OnBeforeHeroesMarried(proposer, responder, showNotification: true);
        return true;
    }

    private void TransferPermanentAssets(Hero applicant, Hero leader)
    {
        foreach (var settlement in applicant.Clan.Settlements.ToArray())
            ChangeOwnerOfSettlementAction.ApplyByBarter(leader, settlement);

        foreach (var workshop in applicant.OwnedWorkshops.ToArray())
            ChangeOwnerOfWorkshopActionPatches.ApplyInternalOverride(
                workshop,
                leader,
                workshop.WorkshopType,
                workshop.Capital,
                0);

        foreach (var caravan in applicant.OwnedCaravans.ToArray())
        {
            using (new AllowedThread())
            {
                applicant.OwnedCaravans.Remove(caravan);
                if (!leader.OwnedCaravans.Contains(caravan)) leader.OwnedCaravans.Add(caravan);
            }
            CaravanPartyComponentTranspilers.OwnerSetIntercept(caravan, leader);
        }

        foreach (var alley in applicant.OwnedAlleys.ToArray())
            alley.SetOwner(leader);

        if (applicant.Gold > 0)
            GiveGoldAction.ApplyBetweenCharacters(applicant, leader, applicant.Gold, false);
    }

    private static void TransferPartyContents(MobileParty sourceParty, MobileParty targetParty, Hero applicant)
    {
        TransferTroops(sourceParty.MemberRoster, targetParty.MemberRoster, applicant.CharacterObject);
        TransferTroops(sourceParty.PrisonRoster, targetParty.PrisonRoster, excluded: null);
        targetParty.ItemRoster.Add(sourceParty.ItemRoster);
        sourceParty.ItemRoster.Clear();
    }

    private static void TransferTroops(
        TaleWorlds.CampaignSystem.Roster.TroopRoster source,
        TaleWorlds.CampaignSystem.Roster.TroopRoster target,
        TaleWorlds.CampaignSystem.CharacterObject excluded)
    {
        foreach (var element in source.GetTroopRoster().ToArray())
        {
            if (element.Character == excluded) continue;
            target.AddToCounts(
                element.Character,
                element.Number,
                false,
                element.WoundedNumber,
                element.Xp,
                true,
                -1);
            source.AddToCounts(
                element.Character,
                -element.Number,
                false,
                -element.WoundedNumber,
                0,
                true,
                -1);
        }
    }

    private bool TryGetPlayer(Hero hero, out Player player)
    {
        player = null;
        if (hero == null || !objectManager.TryGetId(hero, out var heroId)) return false;
        player = playerManager.Players.SingleOrDefault(candidate => candidate.HeroId == heroId);
        return player != null;
    }
}
