using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.MobileParties.Messages;
using GameInterface.Services.MobileParties.Patches;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.UI.Notifications.Messages;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;
using System;

namespace GameInterface.Services.MobileParties.Handlers;

/// <summary>
/// Replicates a tavern mercenary hire from a client to the server, which validates the synced
/// stock and applies the troop add and gold cost authoritatively so they reach every client.
/// </summary>
internal class MercenaryHireHandler : IHandler
{
    private static readonly ILogger logger = LogManager.GetLogger<MercenaryHireHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IPlayerManager playerManager;
    private readonly IAuthorityRouteHandle<MercenaryHireIntent, MercenaryHireResult> hireRoute;

    public MercenaryHireHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        IModConfigAuthority configAuthority,
        IPlayerManager playerManager,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.configAuthority = configAuthority;
        this.playerManager = playerManager;
        hireRoute = authorityRequestRouter.Register(AuthorityRoute<MercenaryHireIntent, HireMercenaries, MercenaryHireResult>.Define(
            "mercenary.hire", AuthorityRouteKind.Command, CreateHeader,
            (intent, header) => new HireMercenaries(intent.TownId, intent.Count, header), request => request.Header, result => result.Header,
            request => string.IsNullOrWhiteSpace(request.TownId) || request.Count <= 0 || request.Count > 1000 ? "hire-shape-invalid" : null,
            request => request.TownId + ":" + request.Count, ValidateHeader, ExecuteHire, Terminal, Probe, _ => { }, Present,
            configAuthority.IsTrustedServer, AuthorityTimeoutPolicy.CampaignMutation, failClosedOnApplyFailure: true,
            isExpectedClientResult: (request, result) => request.TownId == result.TownId && request.Count == result.Count));

        messageBroker.Subscribe<MercenariesHired>(Handle_MercenariesHired);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<MercenariesHired>(Handle_MercenariesHired);
        hireRoute.Dispose();
    }

    internal void Handle_MercenariesHired(MessagePayload<MercenariesHired> obj)
    {
        if (ModInformation.IsServer || !objectManager.TryGetId(obj.What.Town, out var townId)) return;
        hireRoute.Submit(new MercenaryHireIntent(townId, obj.What.Count));
    }

    private AuthorityServerReply<MercenaryHireResult> ExecuteHire(AuthorityServerContext context, HireMercenaries data)
    {
        if (!TryActor(context, out var mainHero, out var mainParty, out var reason) ||
            !objectManager.TryGetObject(data.TownId, out Town town) || town.Settlement == null || !town.Settlement.IsTown ||
            !ReferenceEquals(mainParty.CurrentSettlement, town.Settlement))
            return Reply(context.Header, data.TownId, null, data.Count, 0, 0, 0, AuthorityResultStatus.Unauthorized, reason ?? "hire-town-context-invalid");

        var recruitmentBehavior = Campaign.Current?.GetCampaignBehavior<RecruitmentCampaignBehavior>();
        var mercenaryData = recruitmentBehavior?.GetMercenaryData(town);
        int unitPrice = mercenaryData?.TroopType == null
            ? 0
            : Campaign.Current.Models.PartyWageModel.GetTroopRecruitmentCost(mercenaryData.TroopType, mainHero).RoundedResultNumber;
        int goldAmount = GetMercenaryHireGoldAmount(data.Count, unitPrice);
        int availableCount = mercenaryData?.Number ?? 0;

        // The server stock may tick while a client is still in the tavern conversation. Reject those
        // stale requests against the current server stock, then publish the latest stock back below.
        if (mercenaryData == null ||
            !CanApplyMercenaryHire(
                data.Count, goldAmount, mainHero.Gold, unitPrice, mercenaryData?.TroopType != null, availableCount) ||
            mainParty.MemberRoster.TotalManCount + data.Count > Campaign.Current.Models.PartySizeLimitModel.GetPartyMemberSizeLimit(mainParty.Party).ResultNumber)
        {
            return Reply(context.Header, data.TownId, null, data.Count, 0, mainHero.Gold, availableCount, AuthorityResultStatus.Rejected, "hire-ineligible");
        }

        CharacterObject mercenaryTroop = mercenaryData.TroopType;
        int beforeTroopCount = mainParty.MemberRoster.GetTroopCount(mercenaryTroop);
        int beforeGold = mainHero.Gold;
        int beforeStock = mercenaryData.Number;
        bool mutationStarted = false;
        try
        {
            // Everything below this point can partially replicate. Never retry or emit an ordinary
            // rejection after the first native mutation.
            mutationStarted = true;
            mainParty.AddElementToMemberRoster(mercenaryTroop, data.Count);
            GiveGoldAction.ApplyBetweenCharacters(mainHero, null, goldAmount, false);
            if (mainHero.GetPerkValue(DefaultPerks.Leadership.FamousCommander))
                mainParty.MemberRoster.AddXpToTroop(mercenaryTroop, (int)DefaultPerks.Leadership.FamousCommander.SecondaryBonus * data.Count);
            SkillLevelingManager.OnTroopRecruited(mainHero, data.Count, mercenaryTroop.Tier);
            if (mercenaryTroop.Occupation == Occupation.Bandit)
                SkillLevelingManager.OnBanditsRecruited(mainParty, mercenaryTroop, data.Count);

            mercenaryData.ChangeMercenaryCount(-data.Count);
            RecruitmentCampaignBehaviorPatch.PublishMercenaryStock(recruitmentBehavior, town);
            if (!objectManager.TryGetId(mercenaryTroop, out var troopId) ||
                mainParty.MemberRoster.GetTroopCount(mercenaryTroop) != beforeTroopCount + data.Count ||
                mainHero.Gold != beforeGold - goldAmount || mercenaryData.Number != beforeStock - data.Count)
                throw new InvalidOperationException("hire-postcondition-failed");

            return Reply(context.Header, data.TownId, troopId, data.Count, beforeTroopCount + data.Count, beforeGold - goldAmount,
                beforeStock - data.Count, AuthorityResultStatus.Accepted, null);
        }
        catch (Exception exception) when (mutationStarted)
        {
            logger.Error(exception, "Mercenary hire crossed the mutation boundary and could not prove publication.");
            DisconnectAllCampaignPeers();
            return new AuthorityServerReply<MercenaryHireResult>(Terminal(context.Header, AuthorityResultStatus.ExecutionFailed, "hire-ambiguous"), false, true);
        }
    }

    private AuthorityRequestHeader CreateHeader(long requestId)
    {
        return configAuthority.TryGetCurrent(out var snapshot)
            ? new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId, requestId, snapshot.Revision)
            : default;
    }

    private AuthorityHeaderValidation ValidateHeader(AuthorityRequestHeader header)
    {
        if (!configAuthority.TryGetCurrent(out var snapshot)) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "config-unavailable");
        if (header.ProtocolVersion != snapshot.ProtocolVersion) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.InvalidRequest, "unsupported-protocol");
        if (!string.Equals(header.SessionId, snapshot.SessionId, StringComparison.Ordinal)) return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-session");
        return header.ExpectedRevision == snapshot.Revision ? AuthorityHeaderValidation.Valid : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-state");
    }

    private bool TryActor(AuthorityServerContext context, out Hero hero, out MobileParty party, out string reason)
    {
        hero = null; party = null; reason = null;
        if (string.IsNullOrWhiteSpace(context.Player.HeroId) || string.IsNullOrWhiteSpace(context.Player.MobilePartyId) ||
            !objectManager.TryGetObject(context.Player.HeroId, out hero) || !objectManager.TryGetObject(context.Player.MobilePartyId, out party) ||
            !ReferenceEquals(party.LeaderHero, hero)) { reason = "hire-actor-missing"; return false; }
        return true;
    }

    private static AuthorityServerReply<MercenaryHireResult> Reply(AuthorityRequestHeader header, string townId, string troopId,
        int count, int partyTroopCount, int heroGold, int stock, AuthorityResultStatus status, string reason) =>
        new(new MercenaryHireResult(townId, troopId, count, partyTroopCount, heroGold, stock,
            new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason)), status == AuthorityResultStatus.Accepted);

    private static MercenaryHireResult Terminal(AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new(null, null, 0, 0, 0, 0, new AuthorityResultHeader(header.SessionId, header.RequestId, status, header.ExpectedRevision, reason));

    private static AuthorityCommitProbeResult Probe(MercenaryHireResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted) return AuthorityCommitProbeResult.Applied;
        CharacterObject troop = string.IsNullOrWhiteSpace(result.TroopId) ? null : MBObjectManager.Instance.GetObject<CharacterObject>(result.TroopId);
        if (troop == null) return AuthorityCommitProbeResult.Pending;
        Town town = string.IsNullOrWhiteSpace(result.TownId) ? null : MBObjectManager.Instance.GetObject<Town>(result.TownId);
        var stock = town == null ? null : Campaign.Current?.GetCampaignBehavior<RecruitmentCampaignBehavior>()?.GetMercenaryData(town);
        return MobileParty.MainParty?.MemberRoster.GetTroopCount(troop) == result.ExpectedPartyTroopCount &&
            Hero.MainHero?.Gold == result.ExpectedHeroGold && stock != null && stock.TroopType == troop && stock.Number == result.ExpectedStock
            ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private void DisconnectAllCampaignPeers()
    {
        foreach (var player in playerManager.Players)
        {
            if (playerManager.TryGetPeer(player.ControllerId, out var peer))
                try { peer.Disconnect(); } catch { }
        }
    }

    private static void Present(AuthorityClientOutcome<MercenaryHireResult> outcome)
    {
        if (!outcome.Applied) MBInformationManager.AddQuickInformation(new TaleWorlds.Localization.TextObject("{=coop_mercenary_hire_failed}Unable to hire mercenaries."));
    }

    private readonly struct MercenaryHireIntent
    {
        public MercenaryHireIntent(string townId, int count) { TownId = townId; Count = count; }
        public string TownId { get; }
        public int Count { get; }
    }

    internal static int GetMercenaryHireGoldAmount(int count, int unitPrice)
    {
        if (count <= 0 || unitPrice <= 0)
            return 0;

        if (count > int.MaxValue / unitPrice)
            return 0;

        return count * unitPrice;
    }

    internal static bool CanApplyMercenaryHire(
        int count,
        int goldAmount,
        int serverHeroGold,
        int unitPrice,
        bool availableTroopMatches,
        int availableMercenaries)
    {
        return count > 0 &&
               goldAmount > 0 &&
               serverHeroGold >= goldAmount &&
               unitPrice > 0 &&
               availableTroopMatches &&
               availableMercenaries >= count;
    }
}
