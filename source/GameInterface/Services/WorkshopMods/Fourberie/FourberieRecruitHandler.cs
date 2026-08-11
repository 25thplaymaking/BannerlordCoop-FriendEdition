using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using LiteNetLib;
using Serilog;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.WorkshopMods.Fourberie;

/// <summary>
/// Both halves of Fourberie's routed create-actions: forwards a client's intent to the server, and
/// runs it authoritatively. A peer may only act as its OWN hero.
/// </summary>
internal sealed class FourberieRecruitHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<FourberieRecruitHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IFourberieCreateActionInterface createActionInterface;

    public FourberieRecruitHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IFourberieCreateActionInterface createActionInterface)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.createActionInterface = createActionInterface;
        messageBroker.Subscribe<FourberieCreateActionAttempted>(HandleAttempt);
        messageBroker.Subscribe<NetworkRequestFourberieCreateAction>(HandleRequest);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<FourberieCreateActionAttempted>(HandleAttempt);
        messageBroker.Unsubscribe<NetworkRequestFourberieCreateAction>(HandleRequest);
    }

    /// <summary>Client half: intent → ids-only request. The host player never publishes this — its
    /// prefix runs the routine directly.</summary>
    private void HandleAttempt(MessagePayload<FourberieCreateActionAttempted> payload)
    {
        if (!ModInformation.IsClient) return;

        var obj = payload.What;
        if (!objectManager.TryGetIdWithLogging(obj.Requester, out var requesterHeroId)) return;

        network.SendAll(new NetworkRequestFourberieCreateAction(
            requesterHeroId, obj.DeclaringTypeName, obj.MethodName, obj.Arg));
    }

    /// <summary>Server half: re-derive the acting hero from the PEER, never from the message.</summary>
    private void HandleRequest(MessagePayload<NetworkRequestFourberieCreateAction> payload)
    {
        if (!ModInformation.IsServer) return;
        if (payload.Who is not NetPeer peer) return;

        var obj = payload.What;

        GameThread.RunSafe(() =>
        {
            if (!playerManager.TryGetPlayer(peer, out var player) ||
                !objectManager.TryGetObject<Hero>(player.HeroId, out var owned) ||
                !objectManager.TryGetObjectWithLogging<Hero>(obj.RequesterHeroId, out var requested) ||
                !ReferenceEquals(owned, requested))
            {
                Logger.Warning("Peer requested a Fourberie create-action as hero {HeroId} it does not own", obj.RequesterHeroId);
                return;
            }

            var verdict = createActionInterface.TryRun(obj.DeclaringTypeName, obj.MethodName, obj.Arg);
            if (verdict != FourberieCreateActionVerdict.Applied)
            {
                Logger.Information("Fourberie create-action {Type}.{Method} for {HeroId} refused: {Verdict}",
                    obj.DeclaringTypeName, obj.MethodName, obj.RequesterHeroId, verdict);
            }
        });
    }
}
