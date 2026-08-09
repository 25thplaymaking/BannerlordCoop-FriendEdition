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

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Both halves of the Diplomacy "Donate Gold" route: forwards a client's intent to the server,
/// and applies a peer's request authoritatively. A peer may only donate from its OWN hero.
/// </summary>
internal sealed class DiplomacyDonateGoldHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<DiplomacyDonateGoldHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IDiplomacyDonateGoldInterface donateGoldInterface;

    public DiplomacyDonateGoldHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IDiplomacyDonateGoldInterface donateGoldInterface)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.donateGoldInterface = donateGoldInterface;
        messageBroker.Subscribe<DiplomacyGoldDonationAttempted>(HandleAttempt);
        messageBroker.Subscribe<NetworkRequestDiplomacyDonateGold>(HandleRequest);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<DiplomacyGoldDonationAttempted>(HandleAttempt);
        messageBroker.Unsubscribe<NetworkRequestDiplomacyDonateGold>(HandleRequest);
    }

    /// <summary>Client half: intent → ids-only request. The hosting player never publishes this —
    /// its prefix runs vanilla directly.</summary>
    private void HandleAttempt(MessagePayload<DiplomacyGoldDonationAttempted> payload)
    {
        if (!ModInformation.IsClient) return;

        var obj = payload.What;
        if (!objectManager.TryGetIdWithLogging(obj.Giver, out var giverHeroId)) return;
        if (!objectManager.TryGetIdWithLogging(obj.Clan, out var clanId)) return;

        network.SendAll(new NetworkRequestDiplomacyDonateGold(giverHeroId, clanId, obj.Amount));
    }

    /// <summary>Server half: re-derive the acting hero from the PEER, never from the message.</summary>
    private void HandleRequest(MessagePayload<NetworkRequestDiplomacyDonateGold> payload)
    {
        if (!ModInformation.IsServer) return;
        if (payload.Who is not NetPeer peer) return;

        var obj = payload.What;

        GameThread.RunSafe(() =>
        {
            // Same ownership rule as every routed intent: the peer's registered hero must BE the
            // hero the request claims is giving.
            if (!playerManager.TryGetPlayer(peer, out var player) ||
                !objectManager.TryGetObject<Hero>(player.HeroId, out var owned) ||
                !objectManager.TryGetObjectWithLogging<Hero>(obj.GiverHeroId, out var requested) ||
                !ReferenceEquals(owned, requested))
            {
                Logger.Warning("Peer requested a Diplomacy donation from hero {HeroId} it does not own", obj.GiverHeroId);
                return;
            }

            if (!objectManager.TryGetObjectWithLogging<Clan>(obj.ClanId, out var clan)) return;

            var verdict = donateGoldInterface.TryApplyDonation(requested, clan, obj.Amount);
            if (verdict != DiplomacyDonationVerdict.Applied)
            {
                Logger.Information("Diplomacy donation from {HeroId} refused: {Verdict}", obj.GiverHeroId, verdict);
            }
        });
    }
}
