using Common;
using System.Collections.Generic;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.MobilePartyAIs.Commands;

/// <summary>
/// Turns the AI-versus-player aggression trace on and off on the host.
/// </summary>
/// <remarks>
/// Server-only: the decision this traces is made by the authority that owns the AI parties, and a
/// client never runs it for anyone else's party.
/// </remarks>
public static class PlayerAggressionDebugCommand
{
    [CommandLineArgumentFunction("player_aggression", "coop.debug.mobileparty")]
    public static string SetPlayerAggressionDiagnostics(List<string> args)
    {
        if (!ModInformation.IsServer)
            return "Run this command on the server; it traces the host's AI decisions.";

        if (args.Count != 1 || !bool.TryParse(args[0], out bool enabled))
            return "Usage: coop.debug.mobileparty.player_aggression <true|false>";

        PlayerAggressionDiagnostics.SetEnabled(enabled);

        return enabled
            ? "Tracing why AI parties do or do not attack players; a summary is logged every 30s while " +
              "there is anything to report. Walk a player past hostile parties to collect a sample."
            : "Player-aggression tracing is off.";
    }
}
