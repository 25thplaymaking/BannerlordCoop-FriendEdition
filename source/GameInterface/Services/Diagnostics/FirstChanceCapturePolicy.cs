using System;

namespace GameInterface.Services.Diagnostics;

public static class FirstChanceCapturePolicy
{
    private const int NativeBoundaryEFail = unchecked((int)0x80004005);

    private static readonly string[] RuntimeStackMarkers =
    {
        "GameInterface.",
        "Coop.",
        "BannerlordPlayerSettlement.",
        "PlayerSettlementFixes.",
    };

    public static bool ShouldCapture(string stack)
    {
        if (string.IsNullOrEmpty(stack)) return false;

        foreach (string marker in RuntimeStackMarkers)
        {
            if (stack.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
        }

        return false;
    }

    public static bool ShouldCapture(Exception exception, string stack)
    {
        return exception is InvalidOperationException ||
               exception?.HResult == NativeBoundaryEFail ||
               ShouldCapture(stack);
    }
}
