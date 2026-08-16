using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using GameInterface.Services.Heroes.Messages.LordConversations;
using GameInterface.Services.ObjectManager;
using Serilog;
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

    private const int PrisonerLiberationRelationReward = 10;
    private const int LetLordGoReward = 4;

    public LordConversationsCampaignBehaviorHandler(
        IObjectManager objectManager,
        INetwork network,
        IMessageBroker messageBroker)
    {
        this.objectManager = objectManager;
        this.network = network;
        this.messageBroker = messageBroker;

        messageBroker.Subscribe<LiberateLordPrisoner>(Handle_LiberateLordPrisoner);

        messageBroker.Subscribe<TakeLordPrisoner>(Handle_TakeLordPrisoner);

        messageBroker.Subscribe<LordHelpedInBattle>(Handle_LordHelpedInBattle);

        messageBroker.Subscribe<LordDefeatToRelease>(Handle_LordDefeatToRelease);

        messageBroker.Subscribe<LordFreedToRelease>(Handle_LordFreedToRelease);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<LiberateLordPrisoner>(Handle_LiberateLordPrisoner);

        messageBroker.Unsubscribe<TakeLordPrisoner>(Handle_TakeLordPrisoner);

        messageBroker.Unsubscribe<LordHelpedInBattle>(Handle_LordHelpedInBattle);

        messageBroker.Unsubscribe<LordDefeatToRelease>(Handle_LordDefeatToRelease);

        messageBroker.Unsubscribe<LordFreedToRelease>(Handle_LordFreedToRelease);
    }

    private void Handle_LiberateLordPrisoner(MessagePayload<LiberateLordPrisoner> obj)
    {
        RejectLegacyConversationMutation("Prisoner liberation is unavailable until the server verifies the conversation.");
        return;
#pragma warning disable CS0162
        if (!objectManager.TryGetIdWithLogging(obj.What.MainHero, out var mainHeroId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.ConversationHero, out var conversationHeroId)) return;

        network.SendAll(new NetworkLiberateLordPrisoner(mainHeroId, conversationHeroId));
#pragma warning restore CS0162
    }

    private void Handle_NetworkLiberateLordPrisoner(MessagePayload<NetworkLiberateLordPrisoner> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.MainHeroId, out var playerHero)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.ConversationHeroId, out var conversationHero)) return;
            if (!conversationHero.IsPrisoner) return;

            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(playerHero, conversationHero, PrisonerLiberationRelationReward);
            EndCaptivityAction.ApplyByReleasedAfterBattle(conversationHero);
        },
        context: nameof(Handle_NetworkLiberateLordPrisoner));
    }

    private void Handle_TakeLordPrisoner(MessagePayload<TakeLordPrisoner> obj)
    {
        RejectLegacyConversationMutation("Taking a prisoner is unavailable until the server verifies the conversation.");
        return;
#pragma warning disable CS0162
        if (!objectManager.TryGetIdWithLogging(obj.What.MainParty, out var mainPartyId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.ConversationHero, out var conversationHeroId)) return;

        network.SendAll(new NetworkTakeLordPrisoner(mainPartyId, conversationHeroId));
#pragma warning restore CS0162
    }

    private void Handle_NetworkTakeLordPrisoner(MessagePayload<NetworkTakeLordPrisoner> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<PartyBase>(data.MainPartyId, out var mainParty)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.ConversationHeroId, out var conversationHero)) return;

            TakePrisonerAction.Apply(mainParty, conversationHero);
        },
        context: nameof(Handle_NetworkTakeLordPrisoner));
    }

    private void Handle_LordHelpedInBattle(MessagePayload<LordHelpedInBattle> obj)
    {
        RejectLegacyConversationMutation("Prisoner release is unavailable until the server verifies the battle state.");
        return;
#pragma warning disable CS0162
        if (!objectManager.TryGetIdWithLogging(obj.What.MainHero, out var mainHeroId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.ConversationHero, out var conversationHeroId)) return;

        network.SendAll(new NetworkLordHelpedInBattle(mainHeroId, conversationHeroId));
#pragma warning restore CS0162
    }

    private void Handle_NetworkLordHelpedInBattle(MessagePayload<NetworkLordHelpedInBattle> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.MainHeroId, out var mainHero)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.ConversationHeroId, out var conversationHero)) return;

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
        RejectLegacyConversationMutation("Prisoner release is unavailable until the server verifies the battle state.");
        return;
#pragma warning disable CS0162
        if (!objectManager.TryGetIdWithLogging(obj.What.MainHero, out var mainHeroId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.ConversationHero, out var conversationHeroId)) return;

        network.SendAll(new NetworkLordDefeatToRelease(mainHeroId, conversationHeroId));
#pragma warning restore CS0162
    }

    private void Handle_NetworkLordDefeatToRelease(MessagePayload<NetworkLordDefeatToRelease> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.MainHeroId, out var mainHero)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.ConversationHeroId, out var conversationHero)) return;

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
        RejectLegacyConversationMutation("Prisoner release is unavailable until the server verifies the conversation.");
        return;
#pragma warning disable CS0162
        if (!objectManager.TryGetIdWithLogging(obj.What.MainHero, out var mainHeroId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.What.ConversationHero, out var conversationHeroId)) return;

        network.SendAll(new NetworkLordFreedToRelease(mainHeroId, conversationHeroId));
#pragma warning restore CS0162
    }

    private void Handle_NetworkLordFreedToRelease(MessagePayload<NetworkLordFreedToRelease> obj)
    {
        var data = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.MainHeroId, out var mainHero)) return;
            if (!objectManager.TryGetObjectWithLogging<Hero>(data.ConversationHeroId, out var conversationHero)) return;
            if (!conversationHero.IsPrisoner) return;

            EndCaptivityAction.ApplyByReleasedByChoice(conversationHero, mainHero);
            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(mainHero, conversationHero, LetLordGoReward);
            
            // TODO
            //TraitLevelingHelper.OnLordFreed(conversationHero);
        },
        context: nameof(Handle_NetworkLordFreedToRelease));
    }

    private static void RejectLegacyConversationMutation(string reason)
    {
        Logger.Warning(reason);
        try { TaleWorlds.Library.InformationManager.DisplayMessage(new TaleWorlds.Library.InformationMessage(reason)); }
        catch (Exception exception) { Logger.Warning(exception, "Could not show conversation authority rejection"); }
    }
}
