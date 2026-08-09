using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Services.CampaignService.Data;
using GameInterface.Services.CampaignService.Interfaces;
using GameInterface.Services.CampaignService.Messages;
using Serilog;
using LiteNetLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.CampaignService.Handlers;

internal class UpdateCampaignOptionsHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<UpdateCampaignOptionsHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IServerOptionsProvider serverOptionsProvider;
    private readonly IModConfigAuthority configAuthority;

    public UpdateCampaignOptionsHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IServerOptionsProvider serverOptionsProvider,
        IModConfigAuthority configAuthority)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.serverOptionsProvider = serverOptionsProvider;
        this.configAuthority = configAuthority;
        messageBroker.Subscribe<UpdateCampaignOptions>(Handle_UpdateCampaignOptions);
        messageBroker.Subscribe<NetworkUpdateCampaignOptions>(Handle_NetworkUpdateCampaignOptions);

        messageBroker.Subscribe<UpdateOtherOptions>(Handle_UpdateOtherOptions);
        messageBroker.Subscribe<NetworkUpdateOtherOptions>(Handle_NetworkUpdateOtherOptions);

        messageBroker.Subscribe<InitializeServerOptionsOnClient>(Handle_InitializeServerOptionsOnClient);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<UpdateCampaignOptions>(Handle_UpdateCampaignOptions);
        messageBroker.Unsubscribe<NetworkUpdateCampaignOptions>(Handle_NetworkUpdateCampaignOptions);

        messageBroker.Unsubscribe<UpdateOtherOptions>(Handle_UpdateOtherOptions);
        messageBroker.Unsubscribe<NetworkUpdateOtherOptions>(Handle_NetworkUpdateOtherOptions);

        messageBroker.Unsubscribe<InitializeServerOptionsOnClient>(Handle_InitializeServerOptionsOnClient);
    }

    private void Handle_UpdateCampaignOptions(MessagePayload<UpdateCampaignOptions> obj)
    {
        if (!ModInformation.IsServer) return;
        if (obj?.Who is NetPeer)
        {
            RejectServerInbound(obj.Who, nameof(UpdateCampaignOptions));
            return;
        }

        GameThread.RunSafe(() =>
        {
            if (!configAuthority.TryGetCurrent(out ModConfigSnapshot authoritative) ||
                !authoritative.BirthAndDeathEnabled)
            {
                Logger.Fatal(
                    "Refusing to publish campaign options because the authoritative Birth & Death snapshot is unavailable");
                throw new System.InvalidOperationException(
                    "Campaign options cannot be published while Birth & Death is unverified.");
            }
            if (CampaignOptions.IsLifeDeathCycleDisabled)
            {
                Logger.Warning(
                    "A host-side option attempted to disable Birth & Death; restoring the attested Friend Edition invariant");
                CampaignOptions.IsLifeDeathCycleDisabled = false;
                if (CampaignOptions.IsLifeDeathCycleDisabled)
                {
                    Logger.Fatal("Failed to restore CampaignOptions.IsLifeDeathCycleDisabled=false");
                    throw new System.InvalidOperationException(
                        "The campaign lifecycle could not be reconciled with the accepted host configuration.");
                }
            }

            var message = new NetworkUpdateCampaignOptions(
                CampaignOptions.AutoAllocateClanMemberPerks,
                CampaignOptions.PlayerTroopsReceivedDamage,
                CampaignOptions.RecruitmentDifficulty,
                CampaignOptions.PlayerMapMovementSpeed,
                CampaignOptions.StealthAndDisguiseDifficulty,
                CampaignOptions.CombatAIDifficulty,
                CampaignOptions.IsLifeDeathCycleDisabled,
                CampaignOptions.PersuasionSuccessChance,
                CampaignOptions.ClanMemberDeathChance,
                CampaignOptions.IsIronmanMode,
                CampaignOptions.BattleDeath
            );
            network.SendAll(message);
        });
    }

    private void Handle_NetworkUpdateCampaignOptions(MessagePayload<NetworkUpdateCampaignOptions> obj)
    {
        if (ModInformation.IsServer)
        {
            RejectServerInbound(obj?.Who, nameof(NetworkUpdateCampaignOptions));
            return;
        }
        if (!TryTrustServer(obj?.Who, nameof(NetworkUpdateCampaignOptions), out NetPeer serverPeer)) return;

        var newOptions = obj.What;
        if (!newOptions.TryValidateWireShape(out string failure) ||
            !configAuthority.TryGetCurrent(out ModConfigSnapshot accepted) ||
            !accepted.BirthAndDeathEnabled)
        {
            Logger.Fatal("Rejected authoritative campaign options: {Failure}",
                failure ?? "the host mod-config barrier has not been accepted");
            serverPeer.Disconnect();
            return;
        }

        GameThread.RunSafe(() =>
        {
            CampaignOptions.AutoAllocateClanMemberPerks = newOptions.AutoAllocateClanMemberPerks;
            CampaignOptions.PlayerTroopsReceivedDamage = newOptions.PlayerTroopsReceivedDamage;
            CampaignOptions.RecruitmentDifficulty = newOptions.RecruitmentDifficulty;
            CampaignOptions.PlayerMapMovementSpeed = newOptions.PlayerMapMovementSpeed;
            CampaignOptions.StealthAndDisguiseDifficulty = newOptions.StealthAndDisguiseDifficulty;
            CampaignOptions.CombatAIDifficulty = newOptions.CombatAIDifficulty;
            // The validated wire shape and accepted config both require this exact engine polarity.
            CampaignOptions.IsLifeDeathCycleDisabled = false;
            CampaignOptions.PersuasionSuccessChance = newOptions.PersuasionSuccessChance;
            CampaignOptions.ClanMemberDeathChance = newOptions.ClanMemberDeathChance;
            CampaignOptions.IsIronmanMode = newOptions.IsIronmanMode;
            CampaignOptions.BattleDeath = newOptions.BattleDeath;
        });
    }

    private void Handle_UpdateOtherOptions(MessagePayload<UpdateOtherOptions> obj)
    {
        if (!ModInformation.IsServer) return;
        if (obj?.Who is NetPeer)
        {
            RejectServerInbound(obj.Who, nameof(UpdateOtherOptions));
            return;
        }

        GameThread.RunSafe(() =>
        {
            var message = new NetworkUpdateOtherOptions(serverOptionsProvider.GetServerOptions());
            network.SendAll(message);
        });  
    }

    private void Handle_NetworkUpdateOtherOptions(MessagePayload<NetworkUpdateOtherOptions> obj)
    {
        if (ModInformation.IsServer)
        {
            RejectServerInbound(obj?.Who, nameof(NetworkUpdateOtherOptions));
            return;
        }
        if (!TryTrustServer(obj?.Who, nameof(NetworkUpdateOtherOptions), out NetPeer serverPeer)) return;
        if (!obj.What.TryValidateWireShape(out string failure))
        {
            Logger.Fatal("Rejected authoritative server options: {Failure}", failure);
            serverPeer.Disconnect();
            return;
        }

        GameThread.RunSafe(() =>
        {
            var newOptions = obj.What.ServerOptions;
            UpdateOtherOptions(newOptions);
        });
    }

    private void Handle_InitializeServerOptionsOnClient(MessagePayload<InitializeServerOptionsOnClient> obj)
    {
        // This is a local save-transfer event, not a wire event. It is meaningful only on a client
        // and a NetPeer source would indicate the concrete event was injected through the network
        // dispatcher instead of produced by the trusted save-data handler.
        string failure = null;
        if (!ModInformation.IsClient || obj == null || obj.What == null || obj.Who is NetPeer ||
            !new NetworkUpdateOtherOptions(obj.What.ServerOptions)
                .TryValidateWireShape(out failure))
        {
            Logger.Warning(
                "Rejected client server-options initialization: {Failure}",
                failure ?? "invalid role or network origin");
            return;
        }

        GameThread.RunSafe(() =>
        {
            var newOptions = obj.What.ServerOptions;
            UpdateOtherOptions(newOptions);
        });
    }

    private void UpdateOtherOptions(ServerOptions newOptions)
    {
        // Add other server options as needed
        BannerlordConfig.PlayerReceivedDamageDifficulty = newOptions.PlayerReceivedDamage;
    }

    private bool TryTrustServer(object source, string messageName, out NetPeer serverPeer)
    {
        serverPeer = source as NetPeer;
        if (serverPeer == null)
        {
            Logger.Warning("Rejected {Message} from a local/null source on the client", messageName);
            return false;
        }
        if (!configAuthority.IsTrustedServer(serverPeer))
        {
            Logger.Fatal(
                "Rejected {Message} from a transport peer not pinned by the initial config handshake",
                messageName);
            serverPeer.Disconnect();
            return false;
        }
        return true;
    }

    private static void RejectServerInbound(object source, string messageName)
    {
        if (source is NetPeer peer)
        {
            Logger.Warning("Disconnecting peer {Peer} that sent server-owned {Message}", peer.Id, messageName);
            peer.Disconnect();
            return;
        }

        Logger.Warning("Rejected locally published server-owned network message {Message}", messageName);
    }
}
