using Common.Messaging;
using GameInterface.Services.CampaignService.Data;
using ProtoBuf;

namespace GameInterface.Services.CampaignService.Messages;

public readonly struct UpdateOtherOptions : IEvent { }

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkUpdateOtherOptions : IEvent
{
    [ProtoMember(1)]
    public readonly ServerOptions ServerOptions;

    public NetworkUpdateOtherOptions(ServerOptions serverOptions)
    {
        ServerOptions = serverOptions;
    }

    public bool TryValidateWireShape(out string failure)
    {
        if (ServerOptions == null ||
            !System.Enum.IsDefined(
                typeof(TaleWorlds.CampaignSystem.CampaignOptions.Difficulty),
                ServerOptions.PlayerReceivedDamage))
        {
            failure = "Server options contain an invalid playerReceivedDamage difficulty.";
            return false;
        }

        failure = null;
        return true;
    }
}
