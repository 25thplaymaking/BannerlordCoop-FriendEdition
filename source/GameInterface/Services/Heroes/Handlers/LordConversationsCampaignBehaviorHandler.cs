using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using GameInterface.Services.Heroes.Messages.LordConversations;
using GameInterface.Services.MapEvents;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.Heroes.Handlers;

internal class LordConversationsCampaignBehaviorHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<LordConversationsCampaignBehaviorHandler>();

    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly IMessageBroker messageBroker;
    private readonly IPlayerManager playerManager;

    private const int PrisonerLiberationRelationReward = 10;
    private const int LetLordGoReward = 4;

    public LordConversationsCampaignBehaviorHandler(
        IObjectManager objectManager,
        INetwork network,
        IMessageBroker messageBroker,
        IPlayerManager playerManager)
    {
        this.objectManager = objectManager;
        this.network = network;
        this.messageBroker = messageBroker;
        this.playerManager = playerManager;

        messageBroker.Subscribe<LiberateLordPrisoner>(Handle_LiberateLordPrisoner);
        messageBroker.Subscribe<NetworkLiberateLordPrisoner>(Handle_NetworkLiberateLordPrisoner);

        messageBroker.Subscribe<TakeLordPrisoner>(Handle_TakeLordPrisoner);
        messageBroker.Subscribe<NetworkTakeLordPrisoner>(Handle_NetworkTakeLordPrisoner);

        messageBroker.Subscribe<LordHelpedInBattle>(Handle_LordHelpedInBattle);
        messageBroker.Subscribe<NetworkLordHelpedInBattle>(Handle_NetworkLordHelpedInBattle);

        messageBroker.Subscribe<LordDefeatToRelease>(Handle_LordDefeatToRelease);
        messageBroker.Subscribe<NetworkLordDefeatToRelease>(Handle_NetworkLordDefeatToRelease);

        messageBroker.Subscribe<LordFreedToRelease>(Handle_LordFreedToRelease);
        messageBroker.Subscribe<NetworkLordFreedToRelease>(Handle_NetworkLordFreedToRelease);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<LiberateLordPrisoner>(Handle_LiberateLordPrisoner);
        messageBroker.Unsubscribe<NetworkLiberateLordPrisoner>(Handle_NetworkLiberateLordPrisoner);

        messageBroker.Unsubscribe<TakeLordPrisoner>(Handle_TakeLordPrisoner);
        messageBroker.Unsubscribe<NetworkTakeLordPrisoner>(Handle_NetworkTakeLordPrisoner);

        messageBroker.Unsubscribe<LordHelpedInBattle>(Handle_LordHelpedInBattle);
        messageBroker.Unsubscribe<NetworkLordHelpedInBattle>(Handle_NetworkLordHelpedInBattle);

        messageBroker.Unsubscribe<LordDefeatToRelease>(Handle_LordDefeatToRelease);
        messageBroker.Unsubscribe<NetworkLordDefeatToRelease>(Handle_NetworkLordDefeatToRelease);

        messageBroker.Unsubscribe<LordFreedToRelease>(Handle_LordFreedToRelease);
        messageBroker.Unsubscribe<NetworkLordFreedToRelease>(Handle_NetworkLordFreedToRelease);
    }

    private void Handle_LiberateLordPrisoner(MessagePayload<LiberateLordPrisoner> obj)
    {
        if (ModInformation.IsServer) return;

        if (!objectManager.TryGetIdWithLogging(obj.What.MainHero, out var mainHeroId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.ConversationHero, out var conversationHeroId)) return;

        network.SendAll(new NetworkLiberateLordPrisoner(mainHeroId, conversationHeroId));
    }

    private void Handle_NetworkLiberateLordPrisoner(MessagePayload<NetworkLiberateLordPrisoner> obj)
    {
        var data = obj.What;


        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.MainHeroId, out var playerHero)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.ConversationHeroId, out var conversationHero)) return;
            if (!conversationHero.IsPrisoner) return;
            if (!IsConversationAuthorized(obj.Who, conversationHero, actorHeroId: data.MainHeroId)) return;

            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(playerHero, conversationHero, PrisonerLiberationRelationReward);
            EndCaptivityAction.ApplyByReleasedAfterBattle(conversationHero);
        },
        context: nameof(Handle_NetworkLiberateLordPrisoner));
    }

    private void Handle_TakeLordPrisoner(MessagePayload<TakeLordPrisoner> obj)
    {
        if (ModInformation.IsServer) return;

        if (!objectManager.TryGetIdWithLogging(obj.What.MainParty, out var mainPartyId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.ConversationHero, out var conversationHeroId)) return;

        network.SendAll(new NetworkTakeLordPrisoner(mainPartyId, conversationHeroId));
    }

    private void Handle_NetworkTakeLordPrisoner(MessagePayload<NetworkTakeLordPrisoner> obj)
    {
        var data = obj.What;


        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<PartyBase>(data.MainPartyId, out var mainParty)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.ConversationHeroId, out var conversationHero)) return;
            if (!IsConversationAuthorized(obj.Who, conversationHero, actorPartyId: data.MainPartyId)) return;

            TakePrisonerAction.Apply(mainParty, conversationHero);
        },
        context: nameof(Handle_NetworkTakeLordPrisoner));
    }

    private void Handle_LordHelpedInBattle(MessagePayload<LordHelpedInBattle> obj)
    {
        if (ModInformation.IsServer) return;

        if (!objectManager.TryGetIdWithLogging(obj.What.MainHero, out var mainHeroId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.ConversationHero, out var conversationHeroId)) return;

        network.SendAll(new NetworkLordHelpedInBattle(mainHeroId, conversationHeroId));
    }

    private void Handle_NetworkLordHelpedInBattle(MessagePayload<NetworkLordHelpedInBattle> obj)
    {
        var data = obj.What;


        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.MainHeroId, out var mainHero)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.ConversationHeroId, out var conversationHero)) return;
            if (!IsConversationAuthorized(obj.Who, conversationHero, actorHeroId: data.MainHeroId)) return;

            // TODO: PlayerMapEvent will be null. Need to get relation change without it
            //ChangeRelationAction.ApplyRelationChangeBetweenHeroes(mainHero, conversationHero, relationChange);

            if (conversationHero.IsPrisoner)
            {
               EndCaptivityAction.ApplyByReleasedAfterBattle(conversationHero);
            }
        },
        context: nameof(Handle_NetworkLordHelpedInBattle));
    }

    private void Handle_LordDefeatToRelease(MessagePayload<LordDefeatToRelease> obj)
    {
        if (ModInformation.IsServer) return;

        if (!objectManager.TryGetIdWithLogging(obj.What.MainHero, out var mainHeroId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.ConversationHero, out var conversationHeroId)) return;

        network.SendAll(new NetworkLordDefeatToRelease(mainHeroId, conversationHeroId));
    }

    private void Handle_NetworkLordDefeatToRelease(MessagePayload<NetworkLordDefeatToRelease> obj)
    {
        var data = obj.What;


        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.MainHeroId, out var mainHero)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.ConversationHeroId, out var conversationHero)) return;
            if (!IsConversationAuthorized(obj.Who, conversationHero, actorHeroId: data.MainHeroId)) return;

            if (conversationHero.IsPrisoner)
            {
                EndCaptivityAction.ApplyByReleasedAfterBattle(conversationHero);
            }
            else
            {
                MakeHeroFugitiveAction.Apply(conversationHero, false);
            }

            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(mainHero, conversationHero, LetLordGoReward);
        },
        context: nameof(Handle_NetworkLordDefeatToRelease));
    }

    private void Handle_LordFreedToRelease(MessagePayload<LordFreedToRelease> obj)
    {
        if (ModInformation.IsServer) return;

        if (!objectManager.TryGetIdWithLogging(obj.What.MainHero, out var mainHeroId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.ConversationHero, out var conversationHeroId)) return;

        network.SendAll(new NetworkLordFreedToRelease(mainHeroId, conversationHeroId));
    }

    private void Handle_NetworkLordFreedToRelease(MessagePayload<NetworkLordFreedToRelease> obj)
    {
        var data = obj.What;


        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.MainHeroId, out var mainHero)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.ConversationHeroId, out var conversationHero)) return;
            if (!IsConversationAuthorized(obj.Who, conversationHero, actorHeroId: data.MainHeroId)) return;
            if (!conversationHero.IsPrisoner) return;

            EndCaptivityAction.ApplyByReleasedByChoice(conversationHero, mainHero);
            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(mainHero, conversationHero, LetLordGoReward);
            
            // TODO
            //TraitLevelingHelper.OnLordFreed(conversationHero);
        },
        context: nameof(Handle_NetworkLordFreedToRelease));
    }

    /// <summary>
    /// Confirms the requesting peer may drive this conversation outcome. Runs on the game thread: it
    /// reads campaign state.
    /// </summary>
    /// <remarks>
    /// These five actions were shipped disabled with the note "no server-issued conversation lease
    /// verifies this request". Identity is the half that always holds — the actor named in the request
    /// must be the requesting peer's own hero or party, or a client could act as somebody else.
    ///
    /// For the second half, a lease alone is too narrow. <see cref="ConversationPartyTracker"/> issues
    /// one for map conversations, including the server-detected post-battle case, but a player talking
    /// to a prisoner already sitting in their own party has no map conversation to lease. Custody is the
    /// equivalent proof there and the server can check it directly: releasing a prisoner your own party
    /// is holding needs no further permission than holding them.
    ///
    /// Taking a new prisoner has no custody to appeal to and so still requires the lease, which is
    /// exactly the case that must not be forgeable.
    /// </remarks>
    private bool IsConversationAuthorized(object who, Hero conversationHero,
        string actorHeroId = null, string actorPartyId = null)
    {
        if (who is not NetPeer requester)
        {
            Logger.Error("Rejected a lord conversation outcome without a requesting peer");
            return false;
        }

        if (!playerManager.TryGetPlayer(requester, out var player))
        {
            Logger.Warning("Rejected lord conversation outcome from peer {PeerId}: no registered player",
                requester.Id);
            return false;
        }

        if (actorHeroId != null && actorHeroId != player.HeroId)
        {
            Logger.Warning("Rejected lord conversation outcome from peer {PeerId}: {HeroId} is not their hero",
                requester.Id, actorHeroId);
            return false;
        }

        if (actorPartyId != null && actorPartyId != player.MobilePartyId)
        {
            Logger.Warning("Rejected lord conversation outcome from peer {PeerId}: {PartyId} is not their party",
                requester.Id, actorPartyId);
            return false;
        }

        ConversationPartyTracker tracker = ConversationPartyTracker.Instance;
        if (tracker != null && tracker.TryGetActiveLeaseByOwner(requester, out _)) return true;

        if (HoldsAsPrisoner(player, conversationHero)) return true;

        Logger.Warning(
            "Rejected lord conversation outcome from peer {PeerId}: no active conversation lease and no custody of {HeroId}",
            requester.Id, conversationHero?.StringId);
        return false;
    }

    /// <summary>True when the requesting player's own party is the one holding this hero prisoner.</summary>
    private bool HoldsAsPrisoner(Player player, Hero conversationHero)
    {
        if (conversationHero == null || !conversationHero.IsPrisoner) return false;

        PartyBase captor = conversationHero.PartyBelongedToAsPrisoner;
        if (captor?.MobileParty == null) return false;

        return objectManager.TryGetId(captor.MobileParty, out var captorPartyId) &&
               captorPartyId == player.MobilePartyId;
    }
}
