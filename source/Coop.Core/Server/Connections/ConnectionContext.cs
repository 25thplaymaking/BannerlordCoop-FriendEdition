using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Coop.Core.Server.Services.MobileParties;
using GameInterface.Configuration;
using GameInterface.CoopSessionData;
using GameInterface.Services.CampaignService.Interfaces;
using GameInterface.Services.Heroes.Interfaces;
using GameInterface.Services.Modules;
using GameInterface.Services.Modules.Validators;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.WorkshopMods.Core;

namespace Coop.Core.Server.Connections;

/// <summary>
/// Aggregates the services shared across the connection states so each state can be created with
/// <c>new</c> instead of being resolved from the DI container. Registered once and threaded through
/// the connection state machine by <see cref="ConnectionLogic"/>.
/// </summary>
public class ConnectionContext
{
    public ConnectionContext(
        IMessageBroker messageBroker,
        INetwork network,
        IModuleValidator moduleValidator,
        IModuleInfoProvider moduleInfoProvider,
        IPlayerManager playerManager,
        IPlayerPartyRestorer playerPartyRestorer,
        IObjectManager objectManager,
        IHeroInterface heroInterface,
        ICoopSessionProvider coopSessionProvider,
        ISaveInterface saveInterface,
        IConnectionMessageQueue connectionMessageQueue,
        ISendCoalescer coalescer,
        IAttachmentIdMapper attachmentIdMapper,
        IExistingPlayerSender existingPlayerSender,
        IServerOptionsProvider serverOptionsProvider,
        IJoinCampaignBaselineSender joinCampaignBaselineSender,
        IWorkshopManifestProvider workshopManifestProvider,
        IModConfigAuthority modConfigAuthority)
    {
        MessageBroker = messageBroker;
        Network = network;
        ModuleValidator = moduleValidator;
        ModuleInfoProvider = moduleInfoProvider;
        PlayerManager = playerManager;
        PlayerPartyRestorer = playerPartyRestorer;
        ObjectManager = objectManager;
        HeroInterface = heroInterface;
        CoopSessionProvider = coopSessionProvider;
        SaveInterface = saveInterface;
        ConnectionMessageQueue = connectionMessageQueue;
        Coalescer = coalescer;
        AttachmentIdMapper = attachmentIdMapper;
        ExistingPlayerSender = existingPlayerSender;
        ServerOptionsProvider = serverOptionsProvider;
        JoinCampaignBaselineSender = joinCampaignBaselineSender;
        WorkshopManifestProvider = workshopManifestProvider ??
            throw new System.ArgumentNullException(nameof(workshopManifestProvider));
        ModConfigAuthority = modConfigAuthority ??
            throw new System.ArgumentNullException(nameof(modConfigAuthority));
    }

    public IMessageBroker MessageBroker { get; }
    public INetwork Network { get; }
    public IModuleValidator ModuleValidator { get; }
    public IModuleInfoProvider ModuleInfoProvider { get; }
    public IPlayerManager PlayerManager { get; }
    public IPlayerPartyRestorer PlayerPartyRestorer { get; }
    public IObjectManager ObjectManager { get; }
    public IHeroInterface HeroInterface { get; }
    public ICoopSessionProvider CoopSessionProvider { get; }
    public ISaveInterface SaveInterface { get; }
    public IConnectionMessageQueue ConnectionMessageQueue { get; }
    public ISendCoalescer Coalescer { get; }
    public IAttachmentIdMapper AttachmentIdMapper { get; }
    public IExistingPlayerSender ExistingPlayerSender { get; }
    public IServerOptionsProvider ServerOptionsProvider { get; }
    public IJoinCampaignBaselineSender JoinCampaignBaselineSender { get; }
    public IWorkshopManifestProvider WorkshopManifestProvider { get; }
    public IModConfigAuthority ModConfigAuthority { get; }
}
