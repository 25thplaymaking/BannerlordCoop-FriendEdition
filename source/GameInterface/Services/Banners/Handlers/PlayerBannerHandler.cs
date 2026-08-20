using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.Banners.Messages;
using GameInterface.Services.ObjectManager;
using SandBox.GauntletUI.Map;
using SandBox.View.Map;
using SandBox.ViewModelCollection.Nameplate;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;

namespace GameInterface.Services.Banners.Handlers
{
    /// <summary>
    /// Handles propagation of player banner edits across the network.
    /// </summary>
    public class PlayerBannerHandler : IHandler
    {
        private readonly IMessageBroker messageBroker;
        private readonly IObjectManager objectManager;
        private readonly INetwork network;
        private readonly ILogger Logger = LogManager.GetLogger<PlayerBannerHandler>();

        public PlayerBannerHandler(IMessageBroker messageBroker, IObjectManager objectManager, INetwork network)
        {
            this.messageBroker = messageBroker;
            this.objectManager = objectManager;
            this.network = network;
            messageBroker.Subscribe<PlayerBannerChanged>(Handle);
            messageBroker.Subscribe<NetworkUpdatePlayerBanner>(Handle);
        }

        public void Dispose()
        {
            messageBroker.Unsubscribe<PlayerBannerChanged>(Handle);
            messageBroker.Unsubscribe<NetworkUpdatePlayerBanner>(Handle);
        }

        /// <summary>
        /// Local edit by this player: resolve the clan's network id and forward to the network.
        /// </summary>
        private void Handle(MessagePayload<PlayerBannerChanged> obj)
        {
            var clan = obj.What.Clan;

            if (clan == null) return;

            if (objectManager.TryGetId(clan, out string clanId) == false)
            {
                Logger.Error("Unable to resolve network id for clan {clanName}", clan.Name);
                return;
            }

            network.SendAll(new NetworkUpdatePlayerBanner(clan.Banner.Serialize(), clanId, clan.Color, clan.Color2));
        }

        /// <summary>
        /// Incoming banner update from the network: apply it locally, and (if server) relay to all clients.
        /// </summary>
        private void Handle(MessagePayload<NetworkUpdatePlayerBanner> obj)
        {
            var payload = obj.What;

            if (objectManager.TryGetObject<Clan>(payload.ClanId, out var clan) == false)
            {
                Logger.Error("Unable to find clan ({clanId})", payload.ClanId);
                return;
            }

            GameThread.Run(() =>
            {
                using (new AllowedThread())
                {
                    if (clan.Banner == null)
                    {
                        clan.Banner = new Banner(payload.BannerCode);
                    }
                    else
                    {
                        clan.Banner.Deserialize(payload.BannerCode);
                    }

                    // Banner CODE is shared state and always applies. Banner COLOUR is not always the
                    // host's to give: a conversion can own it as presentation. Empires of Europe 1100
                    // ships BannerColorPersistence, whose whole job is recolouring clan heraldry
                    // (ChangeBannerColors -> Banner.ChangePrimaryColor, and a PreventBannerColorUpdates
                    // patch to stop the game undoing it). That module patches
                    // SandBox.View.Map.Visuals.MobilePartyVisual, so it CANNOT run on the headless host
                    // and is tagged DedicatedServerType="none" - the host therefore has no EoE colouring
                    // to send, and assigning Color/Color2 directly here bypasses the module's own guard
                    // and then forces every party visual and nameplate to rebuild in the host's colours.
                    // That is why EoE heraldry stopped showing: SnowballingKingdoms drives frequent
                    // kingdom changes, and each one repainted the clan.
                    //
                    // A player's own banner edit is still fully authoritative - that is a real shared
                    // decision, not presentation - so only AI clans keep their locally-derived colours.
                    if (BannerColourPresentation.ShouldKeepLocalColours(clan))
                    {
                        Logger.Debug(
                            "Kept local banner colours for {ClanName}; a banner-colour presentation module owns them.",
                            clan.Name);
                    }
                    else
                    {
                        clan.Color = payload.Color;
                        clan.Color2 = payload.Color2;
                        clan.UpdateBannerColor(payload.Color, payload.Color2);
                    }

                    // The banner is mutated in place, so the cached party map visuals (the flag rendered
                    // on the campaign map) won't rebuild on their own. Mark every party belonging to the
                    // clan as visually dirty so the visual is regenerated from the new banner code.
                    foreach (var warParty in clan.WarPartyComponents)
                    {
                        warParty?.MobileParty?.Party?.SetVisualAsDirty();
                    }

                    foreach (var settlement in clan.Settlements)
                    {
                        settlement?.Party?.SetVisualAsDirty();
                    }

                    // The small banner flag on each party's map nameplate is a separate UI cache that
                    // only rebuilds when the nameplate is flagged dirty. There is no vanilla event for a
                    // banner edit, so force the affected nameplates to refresh their banner here.
                    RefreshClanNameplateBanners(clan);
                }
            });

            if (ModInformation.IsServer)
            {
                network.SendAll(new NetworkUpdatePlayerBanner(payload.BannerCode, payload.ClanId, payload.Color, payload.Color2));
            }
        }

        /// <summary>
        /// Forces the map nameplates of every party belonging to <paramref name="clan"/> to rebuild
        /// their cached banner image. No-op when the campaign map UI is not currently active
        /// (e.g. on a headless server).
        /// </summary>
        private void RefreshClanNameplateBanners(Clan clan)
        {
            var nameplateView = MapScreen.Instance?.GetMapView<GauntletMapPartyNameplateView>();
            var dataSource = nameplateView?._dataSource;
            if (dataSource == null) return;

            RefreshNameplateIfClanMatches(dataSource.PlayerNameplate, clan);

            foreach (var nameplate in dataSource.Nameplates)
            {
                RefreshNameplateIfClanMatches(nameplate, clan);
            }
        }

        private static void RefreshNameplateIfClanMatches(PartyNameplateVM nameplate, Clan clan)
        {
            if (nameplate?.Party?.LeaderHero?.Clan == clan)
            {
                nameplate.RefreshValues();
            }
        }
    }
}
