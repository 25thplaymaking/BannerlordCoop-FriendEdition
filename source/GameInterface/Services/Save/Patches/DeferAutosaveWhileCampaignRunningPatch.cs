using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using System.Reflection;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.Save.Patches;

/// <summary>
/// Holds the dedicated host's autosave until the campaign is already paused, so its unavoidable
/// game-thread stall lands where nobody is moving.
/// </summary>
/// <remarks>
/// The stall cannot be removed, only scheduled. The host already writes the file asynchronously
/// (<c>DedicatedServer.Core</c> installs an <c>AsyncFileSaveDriver</c>), so what remains is the
/// engine collecting the object graph — measured live at 4.5 s for a 205 MB Europe 1100 world, of
/// which 3.7 s is <c>SaveContext::CollectSaveDataForObject</c>. That has to read live campaign
/// state, so it cannot move off the game thread.
/// <para>
/// It can move in TIME. Sampling three hours of a live session, the campaign sat in
/// <c>TimeControlMode.Stop</c> for roughly half of it — every player in a settlement or a battle,
/// or nobody connected — and a 4.5 s freeze taken then is invisible. So this skips the host's
/// autosave tick while the campaign is actually running.
/// </para>
/// <para>
/// Skipping the whole tick is deliberate: the tick resets its own "next autosave" clock BEFORE it
/// saves, so returning false leaves the save still due and it retries on the following tick — it
/// fires the moment the world next pauses, rather than being pushed out by another full interval.
/// </para>
/// <para>
/// <see cref="MaximumDeferral"/> is the safety net. A world that never pauses must still be saved,
/// and losing progress is far worse than a visible stall, so past that point the save goes through
/// regardless.
/// </para>
/// <para>
/// The target lives in <c>DedicatedServer.Core</c>, which is a third-party obfuscated assembly this
/// project does not reference. It is resolved by name and shape at patch time, and when it cannot be
/// resolved unambiguously NOTHING is patched and the host keeps its stock behaviour — a missing
/// optimisation is acceptable, a mis-patched save path is not. The assembly is hash-pinned by the
/// release pairing, so a change to it is caught by the deployment gate rather than here.
/// </para>
/// </remarks>
internal static class DeferAutosaveWhileCampaignRunningPatch
{
    private static readonly ILogger Logger =
        LogManager.GetLogger(typeof(DeferAutosaveWhileCampaignRunningPatch));

    /// <summary>How long the save may be held back before it is taken regardless.</summary>
    internal static readonly TimeSpan MaximumDeferral = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Shortest hold worth reporting. The tick is called far more often than a save is actually due —
    /// <c>c()</c> makes its own due check after this prefix runs — so most holds end almost at once and
    /// mean nothing happened.
    /// </summary>
    private static readonly TimeSpan NotableHold = TimeSpan.FromSeconds(30);

    private static DateTime? deferringSince;
    private static bool loggedDeferral;

    /// <summary>
    /// Applies the deferral, if the host assembly is present. Applied manually rather than through
    /// PatchAll because Harmony treats an unresolved target as an error: a <c>TargetMethods</c> that
    /// yields nothing throws "Undefined target method" and takes every other patch in the assembly
    /// down with it. On a client, where DedicatedServer.Core is never loaded, that is the normal
    /// case rather than an exceptional one.
    /// </summary>
    internal static void Apply(Harmony harmony)
    {
        if (harmony == null) return;

        MethodBase tick = ResolveAutosaveTick();
        if (tick == null)
        {
            Logger.Information(
                "Dedicated-host autosave tick not found; autosave scheduling is left as-is.");
            return;
        }

        try
        {
            harmony.Patch(tick, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(DeferAutosaveWhileCampaignRunningPatch), nameof(Prefix))));
            Logger.Information(
                "Autosave will be held until the campaign pauses, for at most {MaximumMinutes:N0} min.",
                MaximumDeferral.TotalMinutes);
        }
        catch (Exception error)
        {
            // A host that saves on its own schedule is strictly better than one that fails to boot.
            Logger.Error(error, "Could not install the autosave deferral; leaving the host's schedule alone.");
        }
    }

    /// <summary>
    /// Finds the host's autosave tick: a parameterless static void on
    /// <c>DedicatedServer.CoopServerHost</c>. Returns null rather than guessing.
    /// </summary>
    private static MethodBase ResolveAutosaveTick()
    {
        try
        {
            Type host = AccessTools.TypeByName("DedicatedServer.CoopServerHost");
            if (host == null) return null;

            MethodInfo tick = AccessTools.Method(host, "c", Type.EmptyTypes);
            if (tick == null || !tick.IsStatic || tick.ReturnType != typeof(void)) return null;

            return tick;
        }
        catch (Exception error)
        {
            Logger.Warning(error, "Could not resolve the dedicated-host autosave tick; leaving it alone.");
            return null;
        }
    }

    internal static bool Prefix()
    {
        try
        {
            if (!ShouldDefer())
            {
                if (deferringSince.HasValue)
                {
                    // Only a hold long enough to have moved a save is worth a line. The tick runs
                    // constantly and most holds end within a second, so logging every one of them
                    // buries the saves themselves; SavePatches records those, with their real cost.
                    double heldSeconds = (DateTime.UtcNow - deferringSince.Value).TotalSeconds;
                    if (heldSeconds >= NotableHold.TotalSeconds)
                    {
                        Logger.Information(
                            "Released the host autosave tick after {HeldSeconds:N0}s; the campaign is paused.",
                            heldSeconds);
                    }
                }

                deferringSince = null;
                loggedDeferral = false;
                return true;
            }

            deferringSince ??= DateTime.UtcNow;

            if (DateTime.UtcNow - deferringSince.Value >= MaximumDeferral)
            {
                Logger.Warning(
                    "The host autosave tick has been held for {HeldMinutes:N0} min without the campaign " +
                    "pausing; releasing it so a save cannot be starved.",
                    MaximumDeferral.TotalMinutes);
                deferringSince = null;
                loggedDeferral = false;
                return true;
            }

            if (!loggedDeferral)
            {
                loggedDeferral = true;
                Logger.Debug("Holding the host autosave tick while the campaign is running.");
            }

            return false;
        }
        catch (Exception error)
        {
            // Never let this decide against saving. Anything unexpected means take the save.
            Logger.Error(error, "Autosave deferral check failed; allowing the save through.");
            return true;
        }
    }

    private static bool ShouldDefer()
    {
        if (!ModInformation.IsServer) return false;

        Campaign campaign = Campaign.Current;
        if (campaign == null) return false;

        // Stop means no player is moving on the map: everyone is occupied, or nobody is connected.
        // That is precisely when the stall costs nothing.
        return campaign.TimeControlMode != CampaignTimeControlMode.Stop;
    }
}
