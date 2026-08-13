using Common;
using Serilog;
using System;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.MapEvents;

/// <summary>
/// Lets the native battle observer latch its result while PlayerEncounter.Battle and the replicated
/// MapEvent are still alive. The registry then destroys the graph, so the scoreboard never has to tick
/// against an encounter that has already been torn out from underneath it.
/// </summary>
internal static class BattlePresentationTeardown
{
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<object, object> Prepared = new();

    internal static bool PrepareBeforeDestroy(
        MapEvent mapEvent,
        bool localPartyWasInvolved,
        ILogger logger)
    {
        Mission mission = Mission.Current;
        bool eligible = ModInformation.IsClient &&
                        localPartyWasInvolved &&
                        mapEvent != null &&
                        mission != null;

        try
        {
            return TryPrepareOnce(
                mapEvent,
                eligible,
                () =>
                {
                    BattleObserverMissionLogic logic = mission.GetMissionBehavior<BattleObserverMissionLogic>();
                    if (logic?.BattleObserver == null)
                        throw new InvalidOperationException("The active battle mission had no battle observer.");

                    logic.BattleObserver.BattleResultsReady();
                });
        }
        catch (Exception ex)
        {
            logger.Warning(
                ex,
                "Could not prepare native battle presentation before MapEvent teardown; teardown will continue");
            return false;
        }
    }

    internal static bool TryPrepareOnce(object identity, bool eligible, Action signalResultsReady)
    {
        if (!eligible || identity == null || signalResultsReady == null) return false;

        lock (Gate)
        {
            if (Prepared.TryGetValue(identity, out _)) return false;
            Prepared.Add(identity, new object());
        }

        try
        {
            signalResultsReady();
            return true;
        }
        catch
        {
            lock (Gate) Prepared.Remove(identity);
            throw;
        }
    }
}
