using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Services.Caravans.Interfaces;
using GameInterface.Services.Caravans.Messages;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.ObjectManager;
using Serilog;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.Caravans.Handlers;

internal class CaravansCampaignBehaviorInitializationHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<CaravansCampaignBehaviorInitializationHandler>();
    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly ISessionCaravansPlayerDataInterface sessionCaravansPlayerDataInterface;

    private CaravansPlayerData caravansPlayerData;

    public CaravansCampaignBehaviorInitializationHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network,
        ISessionCaravansPlayerDataInterface sessionCaravansPlayerDataInterface)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.sessionCaravansPlayerDataInterface = sessionCaravansPlayerDataInterface;
        messageBroker.Subscribe<InitializeClientCaravansData>(Handle);
        messageBroker.Subscribe<PlayerHeroChanged>(Handle);
        messageBroker.Subscribe<NetworkInitializeServerCaravansDataKeys>(Handle);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<InitializeClientCaravansData>(Handle);
        messageBroker.Unsubscribe<PlayerHeroChanged>(Handle);
        messageBroker.Unsubscribe<NetworkInitializeServerCaravansDataKeys>(Handle);
    }

    private void Handle(MessagePayload<InitializeClientCaravansData> obj)
    {
        caravansPlayerData = obj.What.CaravansPlayerData;
    }

    // Need to load caravan data when the hero changes for the player
    private void Handle(MessagePayload<PlayerHeroChanged> obj)
    {
        if (!objectManager.TryGetIdWithLogging(obj.What.NewHero, out string playerHeroId)) return;

        CaravansCampaignBehavior caravansCampaignBehavior =
            Campaign.Current?.GetCampaignBehavior<CaravansCampaignBehavior>();
        if (caravansCampaignBehavior == null)
        {
            Logger.Debug("Skipping caravan player-data initialization because the campaign behavior is unavailable");
            return;
        }

        caravansCampaignBehavior._prohibitedKingdomsForPlayerCaravans = GetProhibitedKingdoms(playerHeroId);
        caravansCampaignBehavior._tradeRumorTakenCaravans = GetTradeRumorTakenCaravans(playerHeroId);

        network.SendAll(new NetworkInitializeServerCaravansDataKeys(playerHeroId));
    }

    private void Handle(MessagePayload<NetworkInitializeServerCaravansDataKeys> obj)
    {
        sessionCaravansPlayerDataInterface.AddPlayerKeys(obj.What.PlayerHeroId);
    }

    private List<Kingdom> GetProhibitedKingdoms(string playerHeroId)
    {
        var prohibitedKingdoms = new List<Kingdom>();

        // Null and key check for players without existing caravans data
        if (caravansPlayerData?.PlayerProhibitedKingdomsForPlayerCaravans?.ContainsKey(playerHeroId) != true) return prohibitedKingdoms;

        foreach (var kingdomId in caravansPlayerData.PlayerProhibitedKingdomsForPlayerCaravans[playerHeroId])
        {
            if (!objectManager.TryGetObjectWithLogging<Kingdom>(kingdomId, out var kingdom)) continue;

            prohibitedKingdoms.Add(kingdom);
        }

        return prohibitedKingdoms;
    }

    private Dictionary<MobileParty, CampaignTime> GetTradeRumorTakenCaravans(string playerHeroId)
    {
        var tradeRumorTakenCaravans = new Dictionary<MobileParty, CampaignTime>();

        // Null and key check for players without existing caravans data
        if (caravansPlayerData?.PlayerTradeRumorTakenCaravans?.ContainsKey(playerHeroId) != true) return tradeRumorTakenCaravans;

        // A caravan the player once took a trade rumour from is ordinary campaign furniture: it gets
        // destroyed by bandits, disbanded, or replaced, and Bannerlord names each one after the template
        // it was built from ("caravan_template_sturgia_738"). The rumour record outlives the party, so a
        // key that no longer resolves is EXPECTED, not a fault - it was being reported through
        // TryGetObjectWithLogging, which logged an [Error] per stale entry on every join.
        //
        // Prune them instead. The record is replicated to each joining client inside the join payload,
        // so leaving dead keys in it means the list only ever grows and the same errors are re-logged
        // every session.
        var stale = new List<string>();
        foreach (KeyValuePair<string, long> tradeRumorTakenCaravan in caravansPlayerData.PlayerTradeRumorTakenCaravans[playerHeroId])
        {
            if (!objectManager.TryGetObject<MobileParty>(tradeRumorTakenCaravan.Key, out var caravan) || caravan == null)
            {
                stale.Add(tradeRumorTakenCaravan.Key);
                continue;
            }

            tradeRumorTakenCaravans[caravan] = new CampaignTime(tradeRumorTakenCaravan.Value);
        }

        if (stale.Count > 0)
        {
            foreach (string staleKey in stale)
                caravansPlayerData.PlayerTradeRumorTakenCaravans[playerHeroId].Remove(staleKey);

            Logger.Debug(
                "Dropped {Count} trade-rumour caravan records for {HeroId} whose parties no longer exist.",
                stale.Count, playerHeroId);
        }

        return tradeRumorTakenCaravans;
    }
}
