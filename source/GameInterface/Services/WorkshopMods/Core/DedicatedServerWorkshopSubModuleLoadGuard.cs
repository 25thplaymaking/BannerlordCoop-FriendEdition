using HarmonyLib;
using System;
using System.Threading;
using TaleWorlds.ModuleManager;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// Enables the audited Workshop entry points on the dedicated campaign host without changing their
/// SubModule.xml files. Keeping those files byte-identical preserves the client/server content handshake.
/// Coop's constructor installs this guard while Bannerlord is still enumerating submodules, after Coop has
/// loaded and before every whitelisted gameplay module in the production activation order.
/// </summary>
public static class DedicatedServerWorkshopSubModuleLoadGuard
{
    private const string HarmonyId = "BannerlordCoop.DedicatedServerWorkshopSubModules";
    private static int installed;

    public static void PrepareBeforeWorkshopSubModuleLoad()
    {
        if (Interlocked.CompareExchange(ref installed, 1, 0) != 0) return;

        try
        {
            new Harmony(HarmonyId).Patch(
                AccessTools.Method(typeof(Module), nameof(Module.CheckIfSubmoduleCanBeLoadable)),
                prefix: new HarmonyMethod(AccessTools.Method(
                    typeof(DedicatedServerWorkshopSubModuleLoadGuard), nameof(CheckIfSubmoduleCanBeLoadablePrefix))));
        }
        catch
        {
            Volatile.Write(ref installed, 0);
            throw;
        }
    }

    private static bool CheckIfSubmoduleCanBeLoadablePrefix(
        Module __instance,
        SubModuleInfo subModuleInfo,
        ref bool __result)
    {
        if (!ShouldForceLoad(
                __instance?.StartupInfo?.DedicatedServerType ?? DedicatedServerType.None,
                subModuleInfo?.SubModuleClassTypeName))
            return true;

        __result = true;
        TaleWorlds.Library.Debug.Print(
            "[Coop] Enabling audited dedicated-server Workshop submodule " +
            subModuleInfo.SubModuleClassTypeName);
        return false;
    }

    internal static bool ShouldForceLoad(DedicatedServerType serverType, string subModuleClassType) =>
        serverType != DedicatedServerType.None && subModuleClassType switch
        {
            "ImprovedGarrisons.Main" => true,
            "DismembermentPlus.Main" => true,
            "Fourberie.Main" => true,
            "UnblockableThrust.UnblockableThrustSubmodule" => true,
            "RebellionsAndDemographics.SubModule" => true,
            _ => false,
        };
}
