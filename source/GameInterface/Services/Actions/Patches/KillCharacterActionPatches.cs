using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.Heroes;
using GameInterface.Services.Heroes.Extensions;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.Players;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace GameInterface.Services.Actions.Patches;

[HarmonyPatch(typeof(KillCharacterAction))]
internal class KillCharacterActionPatches
{
    private static readonly ILogger Logger = LogManager.GetLogger<KillCharacterActionPatches>();

    internal class SuccessionContext
    {
        public Hero Victim;
        public Hero Successor;
        public string ControllerId;
    }

    /// <summary>
    /// The single death-authority boundary:
    /// <para>
    /// 1. Only the server applies deaths; clients receive the outcome through hero field
    ///    replication (state transpiler, death day, death mark), never by running the action.
    /// </para>
    /// <para>
    /// 2. A registered co-op player hero may die only when their bloodline survives them - at
    ///    least one living child AND an eligible clan successor the controller can continue as
    ///    (<see cref="PlayerSuccessionRules"/>). Without one, every lethal path stays blocked:
    ///    the maternal-mortality roll after childbirth, death marks collected after battles,
    ///    old age, murder, and cleanup removals. Execution UI is separately fail-closed by
    ///    <c>HeroExecutionRules</c>; this boundary backstops the direct action call as well.
    /// </para>
    /// </summary>
    [HarmonyPatch(nameof(KillCharacterAction.ApplyInternal))]
    internal static bool Prefix(
        Hero victim,
        KillCharacterAction.KillCharacterActionDetail actionDetail,
        ref SuccessionContext __state)
    {
        if (!ModInformation.IsServer) return false;

        if (victim == null || !victim.IsPlayerHero()) return true;

        if (!PlayerSuccessionRules.TryGetSuccessor(victim, out var successor))
        {
            Logger.Warning(
                "Blocked {Detail} death of co-op player hero {Hero}: no living child or no eligible clan successor",
                actionDetail, victim.Name);
            return false;
        }

        if (!PlayerManager.TryGetControlledObjectInfo(victim, out var info))
        {
            Logger.Error(
                "Blocked {Detail} death of co-op player hero {Hero}: controller lookup failed",
                actionDetail, victim.Name);
            return false;
        }

        Logger.Information(
            "Allowing {Detail} death of player hero {Hero}; controller {Controller} succeeds to {Successor}",
            actionDetail, victim.Name, info.ObjectControllerId, successor.Name);

        __state = new SuccessionContext
        {
            Victim = victim,
            Successor = successor,
            ControllerId = info.ObjectControllerId,
        };
        return true;
    }

    /// <summary>
    /// Publishes succession only when the death actually landed this call. ApplyInternal can
    /// instead defer by writing a death mark (victim inside a map/siege event) - the hero is
    /// still alive then, and the deferred <c>ApplyByDeathMark</c> re-enters this boundary later.
    /// </summary>
    [HarmonyPatch(nameof(KillCharacterAction.ApplyInternal))]
    [HarmonyPostfix]
    internal static void Postfix(SuccessionContext __state)
    {
        if (__state == null) return;
        if (__state.Victim.IsAlive) return;

        MessageBroker.Instance.Publish(null,
            new PlayerHeroDied(__state.Victim, __state.Successor, __state.ControllerId));
    }
}
