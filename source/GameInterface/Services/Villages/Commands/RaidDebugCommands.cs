using Autofac;
using Common;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Handlers;
using System.Collections.Generic;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Villages.Commands;

public class RaidDebugCommands
{
    [CommandLineArgumentFunction("allow_raid_ai_intervention", "coop.debug.mapevent")]
    public static string AllowRaidAiIntervention(List<string> args)
    {
        if (args.Count != 1)
        {
            return "Usage: coop.debug.mapevent.allow_raid_ai_intervention <on|off|toggle|status>";
        }

        var value = args[0].ToLowerInvariant();
        switch (value)
        {
            case "on":
            case "true":
            case "1":
                return ApplyRaidAiInterventionConfig(true);
            case "off":
            case "false":
            case "0":
                return ApplyRaidAiInterventionConfig(false);
            case "toggle":
                return ApplyRaidAiInterventionConfig(!MapEventConfig.AllowRaidAiIntervention);
            case "status":
                return RaidAiInterventionConfigHandler.StatusText;
            default:
                return "Usage: coop.debug.mapevent.allow_raid_ai_intervention <on|off|toggle|status>";
        }
    }

    private static string ApplyRaidAiInterventionConfig(bool allow)
    {
        if (!ModInformation.IsServer)
            return RaidAiInterventionConfigHandler.StatusText + " (server console only)";

        if (!ContainerProvider.TryResolve<RaidAiInterventionConfigHandler>(out var handler))
            return "Raid AI intervention service is unavailable";

        handler.SetAndBroadcast(allow);
        return RaidAiInterventionConfigHandler.StatusText;
    }
}
