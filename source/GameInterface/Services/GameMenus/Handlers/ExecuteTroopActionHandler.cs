using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Services.GameMenus.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using LiteNetLib;
using Serilog;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.GameMenus.Handlers;

/// <summary>
/// Replicates the settlement game-menu overlay's "take to party" action.
/// </summary>
/// <remarks>
/// The client-side postfix on <c>GameMenuOverlay.ExecuteTroopAction</c> runs after the original, whose
/// mutation is suppressed on clients, so the request has to come back from the server to take effect.
/// Native's TakeToParty branch is exactly LeaveSettlementAction.ApplyForCharacterOnly followed by
/// AddHeroToPartyAction.Apply(hero, MainParty, true) - confirmed in the shipped IL - and the server
/// applies that same pair, so the replicated result is vanilla behaviour rather than an approximation.
///
/// The request names its own target party, which a client could otherwise forge into "add any hero to
/// any party". <see cref="IsAuthorized"/> is what makes it safe: the party must be the requesting
/// player's own, and the hero must be unattached or already belong to that player's clan. Both are
/// checked against server state, never against anything the client asserts.
/// </remarks>
internal class ExecuteTroopActionHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<ExecuteTroopActionHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly IPlayerManager playerManager;

    public ExecuteTroopActionHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network,
        IPlayerManager playerManager)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.playerManager = playerManager;

        messageBroker.Subscribe<MenuHeroTakenToParty>(Handle_MenuHeroTakenToParty);
        messageBroker.Subscribe<MenuTakeHeroToParty>(Handle_MenuTakeHeroToParty);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<MenuHeroTakenToParty>(Handle_MenuHeroTakenToParty);
        messageBroker.Unsubscribe<MenuTakeHeroToParty>(Handle_MenuTakeHeroToParty);
    }

    private void Handle_MenuHeroTakenToParty(MessagePayload<MenuHeroTakenToParty> obj)
    {
        if (ModInformation.IsServer) return;

        if (!objectManager.TryGetIdWithLogging(obj.What.Hero, out var heroId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.MainParty, out var mainPartyId)) return;

        network.SendAll(new MenuTakeHeroToParty(heroId, mainPartyId));
    }

    private void Handle_MenuTakeHeroToParty(MessagePayload<MenuTakeHeroToParty> obj)
    {
        if (!ModInformation.IsServer) return;

        if (obj.Who is not NetPeer requester)
        {
            Logger.Error("Rejected {Message} without a requesting peer", nameof(MenuTakeHeroToParty));
            return;
        }

        if (!objectManager.TryGetObjectWithLogging<Hero>(obj.What.HeroId, out var hero)) return;
        if (!objectManager.TryGetObjectWithLogging<MobileParty>(obj.What.MainPartyId, out var mainParty)) return;

        if (!IsAuthorized(requester, hero, mainParty)) return;

        GameThread.RunSafe(() =>
        {
            if (hero.CurrentSettlement != null && hero.CurrentSettlement.Notables?.Contains(hero) == true)
            {
                LeaveSettlementAction.ApplyForCharacterOnly(hero);
            }
            AddHeroToPartyAction.Apply(hero, mainParty, true);
        }, context: nameof(MenuTakeHeroToParty));
    }

    /// <summary>
    /// Confirms the requesting peer may move this hero into this party. Rejections are logged rather
    /// than answered: a well-behaved client cannot produce one, so it is a forged or stale request.
    /// </summary>
    private bool IsAuthorized(NetPeer requester, Hero hero, MobileParty mainParty)
    {
        if (!playerManager.TryGetPlayer(requester, out var player))
        {
            Logger.Warning("Rejected hero transfer from peer {PeerId}: no registered player", requester.Id);
            return false;
        }

        // Own party only. Without this the request is "add any hero to any party".
        if (!objectManager.TryGetIdWithLogging(mainParty, out var targetPartyId) || targetPartyId != player.MobilePartyId)
        {
            Logger.Warning("Rejected hero transfer from peer {PeerId}: {PartyId} is not their main party",
                requester.Id, targetPartyId);
            return false;
        }

        // Never another player's character.
        if (objectManager.TryGetIdWithLogging(hero, out var heroId) &&
            playerManager.Players.Any(other => other.HeroId == heroId))
        {
            Logger.Warning("Rejected hero transfer from peer {PeerId}: {HeroId} is a player character",
                requester.Id, heroId);
            return false;
        }

        // The overlay only offers this for heroes sitting in the settlement or already in the
        // requester's own clan, so an unattached hero or one of their own parties' heroes is the
        // full legitimate set. Anything else would be pulling a hero out of someone else's party.
        Clan playerClan = objectManager.TryGetObject<Clan>(player.ClanId, out var clan) ? clan : null;
        MobileParty currentParty = hero.PartyBelongedTo;
        if (currentParty != null && !ReferenceEquals(currentParty, mainParty) &&
            (playerClan == null || !ReferenceEquals(currentParty.ActualClan, playerClan)))
        {
            Logger.Warning("Rejected hero transfer from peer {PeerId}: hero belongs to another clan's party",
                requester.Id);
            return false;
        }

        return true;
    }
}
