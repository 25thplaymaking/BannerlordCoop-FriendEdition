using System;
using System.Runtime.CompilerServices;

namespace E2E.Tests;

/// <summary>
/// Disables HarmonyLib's legacy <see cref="System.Runtime.Serialization.Formatters.Binary.BinaryFormatter"/>
/// path for <c>PatchInfo</c> persistence before any Harmony instance patches anything in this process.
/// </summary>
/// <remarks>
/// HarmonyLib's <c>HarmonySharedState</c> stores every patch as a serialized byte blob and
/// re-deserializes it on every <c>Harmony.GetPatchInfo</c> call. With BinaryFormatter (the default
/// unless this switch is set), that round-trip resolves each <c>Patch.PatchMethod</c> through
/// BinaryFormatter's <c>MemberInfoSerializationHolder</c>, which re-resolves a <see cref="System.Reflection.MethodInfo"/>
/// via assembly-qualified-name lookup rather than returning the original object. Under GC pressure
/// from a busy test process, that lookup intermittently resolves to the wrong (but similarly-shaped)
/// <c>MethodInfo</c>, so guard code that compares the recorded patch method against the one it just
/// installed spuriously fails closed — a proven source of this suite's run-to-run flakiness in the
/// Workshop module isolation tests (ImprovedGarrisons, Fourberie, Diplomacy, Combat, always inside
/// CombatModCompatibilityTests here). Forcing the System.Text.Json fallback removes that indirection.
/// The switch must be set before <c>HarmonySharedState</c>'s static constructor first runs, which a
/// module initializer guarantees: it executes before any type in this module is used, ahead of every
/// test class' static state.
/// </remarks>
internal static class HarmonySerializationBootstrap
{
    [ModuleInitializer]
    internal static void DisableUnsafeBinaryFormatterForHarmonyPatchInfo()
    {
        AppContext.SetSwitch("System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization", false);
    }
}
