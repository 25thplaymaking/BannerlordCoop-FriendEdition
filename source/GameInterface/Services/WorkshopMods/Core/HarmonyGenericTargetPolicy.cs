using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// Decides whether a resolved Workshop-mod method is a target Harmony can actually rewrite on the
/// running CLR.
/// <para>
/// A method declared on a constructed generic type — e.g. Diplomacy's
/// <c>AbstractDiplomaticAction&lt;T&gt;.TryApply</c>, which concrete actions inherit without
/// overriding — patches fine on the dedicated server's .NET Core runtime, but on the .NET
/// Framework client CLR Harmony's <c>PatchFunctions.UpdateWrapper</c> throws
/// <c>ArgumentException: The given generic instantiation was invalid</c>. Because
/// <c>Harmony.PatchCategory</c> aborts the whole category on its first failed job and
/// <c>GameInterface.PatchAll</c> runs inside the join handshake
/// (<c>MainMenuState.Handle_NetworkConnected</c>), a single such target soft-froze every joining
/// client on the "Applying patches" loading screen (2026-08-13). Open generic definitions never
/// patch on any runtime.
/// </para>
/// Every <c>TargetMethods()</c> that resolves methods out of a Workshop mod assembly must route
/// candidates through <see cref="CanPatch(MethodBase)"/> instead of yielding them raw.
/// </summary>
internal static class HarmonyGenericTargetPolicy
{
    /// <summary>
    /// True on runtimes whose CLR accepts Harmony rewrites of methods with generic declaring
    /// types (observed: .NET Core / modern .NET). False on the .NET Framework CLR the game client
    /// runs, where such rewrites throw at patch time.
    /// </summary>
    internal static readonly bool RuntimePatchesGenericTargets =
        !RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.Ordinal);

    internal static bool CanPatch(MethodBase method) => CanPatch(method, RuntimePatchesGenericTargets);

    internal static bool CanPatch(MethodBase method, bool runtimePatchesGenericTargets)
    {
        if (method == null) return false;

        // Unbound type parameters anywhere in the signature can never be rewritten.
        if (method.ContainsGenericParameters) return false;

        var declaringType = method.DeclaringType;
        if (declaringType == null || declaringType.ContainsGenericParameters) return false;

        bool genericTarget = method.IsGenericMethod || declaringType.IsGenericType;
        return !genericTarget || runtimePatchesGenericTargets;
    }
}
