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
///
/// <para>
/// <b>The retry budget is calibrated for production, not for the test processes.</b> Both test
/// assemblies carry a <c>HarmonySerializationBootstrap</c> module initializer that sets the
/// <c>System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization</c> AppContext switch to
/// false, which pushes HarmonyLib onto its System.Text.Json fallback and measurably cut the misread
/// rate (~10x, per the run that introduced it). No production assembly sets that switch, and it
/// would not matter if one did: HarmonyLib only reads it from its net5.0-and-newer builds. Verified
/// by scanning every per-TFM <c>0Harmony.dll</c> in Lib.Harmony 2.4.2 for the switch literal — it is
/// present in net5.0/net6.0/net8.0 alongside <c>UseBinaryFormatter</c> and <c>JsonSerializer</c>, and
/// absent from net35/net452/net472/net48/netcoreapp3.x, which carry no JSON path at all. The binary
/// actually deployed to the game (<c>Modules/Coop/bin/Win64_Shipping_Client/0Harmony.dll</c>,
/// 2.4.2.0, stamped <c>.NETFramework,Version=v4.7.2</c>) is one of those: it serializes patch info
/// through BinaryFormatter unconditionally. Bannerlord runs on .NET Framework 4.7.2, so production
/// faces the UN-mitigated misread rate with the retry as its only mitigation, while the numbers
/// below were tuned against the mitigated one.
/// </para>
/// <para>
/// The budget is therefore set well above what the test suites need. Raising it is close to free:
/// <see cref="StabilizeUntilAcceptable"/> returns on the first acceptable read, so extra attempts
/// cost nothing on a healthy startup and are only fully spent on a path that is about to fail closed
/// anyway. Fail-closed behaviour is unchanged — the guards still throw when every attempt disagrees;
/// only the number of transient misreads that can be absorbed before that happens has gone up.
/// </para>
/// </remarks>
internal static class HarmonyPatchInfoStabilizer
{
    /// <summary>
    /// Reads a retry-until-acceptable check will make before failing closed. Doubled from the
    /// original 5 because production runs the un-mitigated BinaryFormatter path (see the type
    /// remarks); 5 was calibrated against the test processes' ~10x-reduced misread rate, and that
    /// calibration does not transfer.
    /// </summary>
    private const int Attempts = 10;

    /// <summary>
    /// How many consecutive agreeing reads <see cref="RequireAcceptableOnEveryAttempt"/> needs before
    /// it will call an existence scan clean. Deliberately smaller than <see cref="Attempts"/>: this
    /// budget is spent on the SUCCESS path (every read must agree), so each extra read is unavoidable
    /// cost on a healthy startup, whereas <see cref="Attempts"/> is only fully spent on a path that is
    /// about to abort anyway. Three agreeing reads put the false-clean probability at the per-read
    /// misread rate cubed, which is far below the residual risk of the scan itself.
    /// </summary>
    private const int ConfirmationAttempts = 3;

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

    /// <summary>
    /// Invokes <paramref name="isAcceptable"/> up to <see cref="ConfirmationAttempts"/> times
    /// (waiting <see cref="RetryDelayMs"/>ms between attempts) and returns true only if EVERY attempt
    /// reports acceptable. Returns false as soon as one attempt reports otherwise.
    /// </summary>
    /// <remarks>
    /// This is the mirror image of <see cref="StabilizeUntilAcceptable"/> and the two are NOT
    /// interchangeable — picking the wrong one silently inverts which way the guard fails.
    /// <para>
    /// <see cref="StabilizeUntilAcceptable"/> is correct when the artifact can only manufacture a
    /// FALSE ALARM: an exact-inventory assert ("this original must carry exactly this patch method")
    /// cannot be fooled into passing, because a misread yields a wrong <c>MethodInfo</c>, never the
    /// expected one. It is also correct when each attempt re-does corrective work — a remove-then-
    /// verify cycle whose retry unpatches again — because a real leftover would have been removed by
    /// the retry rather than merely re-read.
    /// </para>
    /// <para>
    /// It is WRONG for an existence scan ("no forbidden patch is installed"), where a misread can
    /// also manufacture a FALSE CLEAN — a real forbidden patch whose <c>PatchMethod</c> transiently
    /// deserializes as null or as an allow-listed type disappears from that one scan. Retrying until
    /// some scan says clean then shops for the misread instead of absorbing it, multiplying the
    /// false-negative rate by the attempt count. Such scans use this method instead, so a retry can
    /// only ever confirm the fail-closed verdict.
    /// </para>
    /// <para>
    /// Use this nested inside a corrective <see cref="StabilizeUntilAcceptable"/> loop, not on its
    /// own: on its own, one transient false alarm out of the confirmation reads aborts outright,
    /// which is the very flakiness this type exists to absorb. Nested, a false alarm costs one more
    /// removal round and the caller only fails once the outer budget is spent.
    /// </para>
    /// </remarks>
    internal static bool RequireAcceptableOnEveryAttempt(System.Func<bool> isAcceptable)
    {
        for (var attempt = 0; attempt < ConfirmationAttempts; attempt++)
        {
            if (attempt > 0) Thread.Sleep(RetryDelayMs);
            if (!isAcceptable()) return false;
        }

        return true;
    }
}
