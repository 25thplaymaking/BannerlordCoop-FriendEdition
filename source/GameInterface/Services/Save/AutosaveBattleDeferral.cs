using Common.Logging;
using GameInterface.Services.MapEvents;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using Serilog;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.Save;

/// <summary>
/// Holds the host's periodic autosave back while a player is in a battle, and only for as long as
/// that stays safe.
/// </summary>
/// <remarks>
/// The host's save blocks the game thread for 4-6 seconds because it reads a live 205 MB object graph
/// that has to be consistent. That cost is not removable from here: both dominant SaveContext blocks
/// are already TWParallel, the write is already async, and the read has to stop the world. What IS
/// removable is WHEN it lands. Between fights a 5 second stall is an annoyance; in the middle of one
/// it gets people killed, because they cannot act while the host is frozen.
/// <para>
/// This declines the save at <see cref="Patches.SavePatches"/>'s prefix and lets the caller's timer
/// re-arm normally. That distinction matters: an earlier attempt HELD CoopServerHost's autosave tick
/// instead, and the held tick's own re-arm threw ArgumentOutOfRangeException ("un-representable
/// DateTime"). CoopServerHost.Tick treats any exception as fatal and calls Environment.Exit(3), so the
/// host died and restarted 29 times in eleven hours. Nothing here touches the tick, its schedule or its
/// arithmetic - the save is simply skipped this cycle and the next one is considered on its merits.
/// </para>
/// <para>
/// Deferral is bounded twice over, because "never saved" is a far worse outcome than one badly-timed
/// stall. A join save is never deferred (a joining peer needs that exact snapshot), and once
/// <see cref="MaximumDeferral"/> has passed since the last completed save the next one proceeds
/// regardless of what anyone is doing.
/// </para>
/// </remarks>
internal static class AutosaveBattleDeferral
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(AutosaveBattleDeferral));

    /// <summary>The join snapshot, which must always be taken on demand.</summary>
    internal const string JoinSaveName = "TransferSave";

    /// <summary>
    /// The longest the world may go unsaved because of deferral. A siege or a long field battle must
    /// not be able to hold the save off indefinitely.
    /// </summary>
    internal static TimeSpan MaximumDeferral = TimeSpan.FromMinutes(30);

    private static DateTime lastCompletedSaveUtc = DateTime.UtcNow;

    internal static void NoteSaveCompleted() => lastCompletedSaveUtc = DateTime.UtcNow;

    /// <summary>
    /// Whether this save should be skipped this cycle. Pure of side effects apart from its own log line.
    /// </summary>
    internal static bool ShouldDefer(
        string saveName,
        IPlayerManager playerManager,
        IObjectManager objectManager)
    {
        try
        {
            // A joining peer is waiting on this exact snapshot; never make them wait for a battle.
            if (string.Equals(saveName, JoinSaveName, StringComparison.Ordinal)) return false;

            if (playerManager == null || objectManager == null) return false;

            TimeSpan sinceLastSave = DateTime.UtcNow - lastCompletedSaveUtc;
            if (sinceLastSave >= MaximumDeferral)
            {
                Logger.Information(
                    "Autosave '{SaveName}' is going ahead despite a battle: {Minutes:F0} minutes since the " +
                    "last completed save, past the {Limit:F0} minute deferral limit.",
                    saveName, sinceLastSave.TotalMinutes, MaximumDeferral.TotalMinutes);
                return false;
            }

            MobileParty fighting = FindPlayerInBattle(playerManager, objectManager);
            if (fighting == null) return false;

            Logger.Information(
                "Deferring autosave '{SaveName}': {Party} is in map event {MapEvent}. {Minutes:F0} minutes " +
                "since the last completed save; the next tick will reconsider.",
                saveName, fighting.StringId, fighting.MapEvent?.StringId ?? "none", sinceLastSave.TotalMinutes);
            return true;
        }
        catch (Exception error)
        {
            // A save that happens at a bad moment beats a save that does not happen at all.
            Logger.Error(error, "Could not evaluate autosave deferral; saving normally.");
            return false;
        }
    }

    /// <summary>
    /// The first connected player standing in a real battle, or null.
    /// </summary>
    /// <remarks>
    /// A slow village raid is excluded for the same reason
    /// <c>PlayerOccupancyPauseHandler.IsPlayerOccupied</c> excludes it: it is a map event that runs for
    /// campaign-hours while the player is free to act, so treating it as a battle would suppress saving
    /// for the whole raid. Being inside a settlement is deliberately NOT counted either - the stall is
    /// only dangerous where the player is fighting.
    /// </remarks>
    private static MobileParty FindPlayerInBattle(IPlayerManager playerManager, IObjectManager objectManager)
    {
        foreach (var player in playerManager.Players.Where(playerManager.IsConnected))
        {
            if (!objectManager.TryGetObject<MobileParty>(player.MobilePartyId, out var party) || party == null)
                continue;

            var mapEvent = party.MapEvent;
            if (mapEvent == null) continue;
            if (mapEvent.IsActiveSlowVillageRaid()) continue;

            return party;
        }

        return null;
    }
}
