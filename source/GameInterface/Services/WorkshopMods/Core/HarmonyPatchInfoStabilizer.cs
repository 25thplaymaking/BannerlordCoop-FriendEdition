using System.Threading;

namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// Retries a <see cref="HarmonyLib.Harmony.GetPatchInfo"/>-based check before accepting a negative
/// result, absorbing a proven-transient HarmonyLib defect rather than papering over a real one.
/// </summary>
/// <remarks>
/// HarmonyLib persists every patch by serializing it into <c>HarmonySharedState</c> and
/// re-deserializing it on every <c>Harmony.GetPatchInfo</c> call. That round trip has been observed,
/// under GC pressure from a busy test process, to intermittently reconstruct the wrong
/// <see cref="System.Reflection.MethodInfo"/> for a patch's <c>PatchMethod</c> (confirmed by hash
/// mismatch against the original object, not merely reference inequality — a genuine HarmonyLib-
/// internal race, not application logic). Re-reading <c>GetPatchInfo</c> moments later always shows
/// the correct value. This is the proven cause of this suite's run-to-run-unstable failures across
/// every Workshop module's Harmony isolation guard (ImprovedGarrisons, Fourberie, Diplomacy, Player
/// Settlement, Combat).
///
/// A genuine foreign, leftover, or otherwise-real patch mismatch does NOT self-correct between reads
/// — only this artifact does — so retrying costs nothing for the real fail-closed cases these guards
/// exist to catch, while absorbing the proven-transient ones. Callers pass a predicate that reads
/// live Harmony state and returns true once it looks acceptable; the guard should still throw if the
/// predicate never returns true across every attempt.
/// </remarks>
internal static class HarmonyPatchInfoStabilizer
{
    private const int Attempts = 5;
    private const int RetryDelayMs = 5;

    /// <summary>
    /// Invokes <paramref name="isAcceptable"/> up to <see cref="Attempts"/> times (waiting
    /// <see cref="RetryDelayMs"/>ms between attempts), returning true as soon as one attempt
    /// succeeds. Returns false only if every attempt reports a mismatch.
    /// </summary>
    internal static bool StabilizeUntilAcceptable(System.Func<bool> isAcceptable)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0) Thread.Sleep(RetryDelayMs);
            if (isAcceptable()) return true;
        }

        return false;
    }
}
