using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Missions.Agents;

public interface IPuppetMountStateRepairer
{
    void PrepareForAiControl(Agent mount);

    void PreserveRiderlessPuppet(Agent mount);

    void RepairAfterRiderDeath(Agent mount);
}

public class PuppetMountStateRepairer : IPuppetMountStateRepairer
{
    /// <summary>
    /// Restores the invariant assumed by HumanAIComponent.FindClosestMountAvailable: every active,
    /// riderless entry in Mission.MountsWithoutRiders has a CommonAIComponent. Controller transitions
    /// remove that component automatically, so coop must put it back before native mount-search AI runs.
    /// </summary>
    internal static bool EnsureMountSearchInvariant(Agent mount)
    {
        if (mount == null
            || !mount.IsActive()
            || mount.RiderAgent != null
            || mount.CommonAIComponent != null)
        {
            return false;
        }

        mount.AddComponent(new CommonAIComponent(mount));
        return true;
    }

    public void PrepareForAiControl(Agent mount)
    {
        if (mount == null
            || mount.Controller == AgentControllerType.AI
            || mount.CommonAIComponent == null)
        {
            return;
        }

        mount.RemoveComponent(mount.CommonAIComponent);
    }

    public void PreserveRiderlessPuppet(Agent mount)
    {
        if (mount == null
            || !mount.IsActive()
            || mount.RiderAgent != null
            || mount.Controller != AgentControllerType.None)
        {
            return;
        }

        EnsureMountSearchInvariant(mount);
    }

    public void RepairAfterRiderDeath(Agent mount)
    {
        PreserveRiderlessPuppet(mount);
        if (mount?.CommonAIComponent == null
            || !mount.IsActive()
            || mount.RiderAgent != null)
        {
            return;
        }

        mount.CommonAIComponent.OnMountUnreserved();
    }
}
