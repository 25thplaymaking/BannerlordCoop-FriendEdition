using Common;
using Common.Logging;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using Serilog;
using System;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Supplies the audited Diplomacy settings object when MCM's provider did not register it. The
/// dedicated host intentionally does not initialize MCM's presentation submodule, so it uses the
/// same one-campaign fallback as clients and publishes those canonical values in its snapshot. A
/// real provider result always wins; clients populate the fallback from that host snapshot before
/// any gated Diplomacy UI is enabled.
/// </summary>
internal static class DiplomacyClientSettingsBridge
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(DiplomacyClientSettingsBridge));
    private static readonly object Gate = new();
    private static object fallback;

    internal static void Reset()
    {
        lock (Gate) fallback = null;
    }

    internal static object Resolve(object providerValue, Func<object> fallbackFactory)
    {
        if (providerValue != null) return providerValue;
        if (fallbackFactory == null) throw new ArgumentNullException(nameof(fallbackFactory));

        lock (Gate)
        {
            if (fallback == null)
            {
                fallback = fallbackFactory() ??
                           throw new InvalidOperationException("Diplomacy settings fallback factory returned null.");
                Logger.Information(
                    "Created canonical Diplomacy settings fallback for role {Role} ({SettingsType})",
                    ModInformation.IsServer ? "server" : "client",
                    fallback.GetType().AssemblyQualifiedName);
            }
            return fallback;
        }
    }

    private static object CreateFallback()
    {
        Type settingsType = DiplomacyCompatibilityPolicy.ResolveType(DiplomacyCompatibilityPolicy.SettingsTypeName) ??
                            throw new TypeLoadException(DiplomacyCompatibilityPolicy.SettingsTypeName);
        return Activator.CreateInstance(settingsType, nonPublic: true) ??
               throw new InvalidOperationException("Diplomacy settings fallback could not be constructed.");
    }

    [HarmonyPatch]
    [HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
    private static class GlobalSettingsInstancePatch
    {
        private static MethodBase TargetMethod()
        {
            Type settingsType = DiplomacyCompatibilityPolicy.ResolveType(DiplomacyCompatibilityPolicy.SettingsTypeName);
            for (Type current = settingsType; current != null; current = current.BaseType)
            {
                MethodInfo getter = current.GetProperty(
                    "Instance",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    ?.GetGetMethod(nonPublic: true);
                if (getter == null) continue;

                // Diplomacy.Settings never redeclares Instance; this walk lands on MCM's
                // GlobalSettings<Diplomacy.Settings>.Instance — a constructed generic declaring
                // type the .NET Framework client CLR refuses to patch (the second of the two
                // targets behind the 2026-08-13 join freeze; see HarmonyGenericTargetPolicy).
                // Skipping it on clients is safe: their full MCM registers the real settings
                // object, so the provider result this postfix would defer to is already non-null,
                // and the snapshot-apply boundaries fail loudly if it ever is not. The MCM-less
                // dedicated host, where the fallback is actually needed, runs .NET Core and keeps
                // the patch.
                if (!HarmonyGenericTargetPolicy.CanPatch(getter))
                {
                    Logger.Information(
                        "Diplomacy settings fallback getter patch skipped on this runtime; " +
                        "relying on MCM's registered settings ({Getter} on {DeclaringType})",
                        getter.Name,
                        getter.DeclaringType);
                    return null;
                }

                return getter;
            }

            return null;
        }

        [HarmonyPrepare]
        private static bool Prepare() => TargetMethod() != null;

        [HarmonyPostfix]
        private static void Postfix(ref object __result)
        {
            __result = Resolve(__result, CreateFallback);
        }
    }
}
