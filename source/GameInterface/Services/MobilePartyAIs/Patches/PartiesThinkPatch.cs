using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using System.Threading.Tasks;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MobilePartyAIs.Patches;

[HarmonyPatch(typeof(Campaign))]
internal class PartiesThinkPatch
{
    internal static bool ShouldTickClientMainParty(AiBehavior behavior) => behavior == AiBehavior.EscortParty;

    private const int TICK_DELAY_MS = 100;

    /// <summary>
    /// How many parties get an AI tick per batch. Scaled to the world, not fixed.
    /// </summary>
    /// <remarks>
    /// This used to be a flat 100. At one batch per <see cref="TICK_DELAY_MS"/> that is 1000 party
    /// thinks a second no matter how big the campaign is, so the interval between a given party's
    /// thinks is (party count / 1000) seconds. Native Calradia carries roughly 600 parties, giving
    /// every party a think about every 0.6s — which is what the constant was tuned against.
    /// Europe 1100 runs 4113, which stretches that to over four seconds, and hostile AI that
    /// re-evaluates its target once every four seconds reads as inert.
    /// <para>
    /// Scale the batch so a full sweep of the world completes in <see cref="SWEEP_TICKS"/> batches
    /// regardless of party count, with a floor so small worlds behave exactly as before and a
    /// ceiling so a pathological party count cannot run away with the game thread. Raising this
    /// also raises how often AI decisions are replicated, so the ceiling is deliberately
    /// conservative rather than "as fast as possible".
    /// </para>
    /// </remarks>
    private const int SWEEP_TICKS = 20;
    private const int MIN_UPDATES_PER_TICK = 100;
    private const int MAX_UPDATES_PER_TICK = 400;

    internal static int GetUpdatesPerTick(int partyCount)
    {
        if (partyCount <= 0) return MIN_UPDATES_PER_TICK;

        int scaled = (partyCount + SWEEP_TICKS - 1) / SWEEP_TICKS;
        if (scaled < MIN_UPDATES_PER_TICK) return MIN_UPDATES_PER_TICK;
        if (scaled > MAX_UPDATES_PER_TICK) return MAX_UPDATES_PER_TICK;
        return scaled;
    }

    private static Task delay = Task.CompletedTask;

    private static int CurrentStartIdx = 0;

    private static readonly ILogger Logger = LogManager.GetLogger<PartiesThinkPatch>();

    [HarmonyPatch("PartiesThink")]
    [HarmonyPrefix]
    private static bool PartiesThinkPrefix(Campaign __instance, ref float dt)
    {
        if (ModInformation.IsClient)
        {
            if (ShouldTickClientMainParty(MobileParty.MainParty.DefaultBehavior))
                MobileParty.MainParty.Ai.Tick(dt);

            return false;
        }

        if (delay.IsCompleted == false) return false;

        delay = Task.Delay(TICK_DELAY_MS);

        int partyCount = __instance.MobileParties.Count;
        if (partyCount <= 0) return false;

        int updatesPerTick = GetUpdatesPerTick(partyCount);

        for (int i = 0; i < updatesPerTick; i++)
        {
            var currentIdx = (CurrentStartIdx + i) % partyCount;

            var ai = __instance.MobileParties[currentIdx]?.Ai;

            ai.Tick(dt);
        }

        CurrentStartIdx = (CurrentStartIdx + updatesPerTick) % partyCount;

        return false;
    }
}
