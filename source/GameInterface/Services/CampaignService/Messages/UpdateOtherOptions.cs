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
        if (ServerOptions == null || !IsDefinedDifficulty(ServerOptions.PlayerReceivedDamage))
        {
            failure = "Server options contain an invalid playerReceivedDamage difficulty.";
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>
    /// CampaignOptions.Difficulty is not Int32-backed, and Enum.IsDefined THROWS when the boxed
    /// value's type differs from the enum's underlying type instead of answering false — so the
    /// wire int must never reach it directly. Comparing against each defined value is
    /// underlying-type-agnostic and cannot be truncation-fooled by out-of-range input.
    /// </summary>
    private static bool IsDefinedDifficulty(int value)
    {
        foreach (object defined in System.Enum.GetValues(
                     typeof(TaleWorlds.CampaignSystem.CampaignOptions.Difficulty)))
        {
            if (System.Convert.ToInt32(defined, System.Globalization.CultureInfo.InvariantCulture) == value)
                return true;
        }

        return false;
    }
}
