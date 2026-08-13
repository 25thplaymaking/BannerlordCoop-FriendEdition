using Common;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using System;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Supplies the audited Diplomacy settings object on a client when MCM's provider did not register it.
/// A real provider result always wins; the fallback exists for one campaign and is then populated by
/// the authoritative Diplomacy snapshot before any gated UI is enabled.
/// </summary>
internal static class DiplomacyClientSettingsBridge
{
    private static readonly object Gate = new();
    private static object fallback;

    internal static void Reset()
    {
        lock (Gate) fallback = null;
    }

    internal static object Resolve(bool isClient, object providerValue, Func<object> fallbackFactory)
    {
        if (providerValue != null || !isClient) return providerValue;
        if (fallbackFactory == null) throw new ArgumentNullException(nameof(fallbackFactory));

        lock (Gate)
        {
            fallback ??= fallbackFactory() ??
                         throw new InvalidOperationException("Diplomacy client settings fallback factory returned null.");
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
                if (getter != null) return getter;
            }

            return null;
        }

        [HarmonyPrepare]
        private static bool Prepare() => TargetMethod() != null;

        [HarmonyPostfix]
        private static void Postfix(ref object __result)
        {
            __result = Resolve(ModInformation.IsClient, __result, CreateFallback);
        }
    }
}
