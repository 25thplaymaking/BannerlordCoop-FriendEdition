using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using TaleWorlds.Library;

namespace GameInterface.Services.UI.Patches;

/// <summary>
/// Stops a modal text prompt from ever opening on the headless host, where nothing could answer it.
/// </summary>
/// <remarks>
/// <c>DedicatedServer.Core</c> guards <c>InformationManager.ShowInquiry</c> — it logs the dialog,
/// invokes the affirmative action and skips the UI — but it guards ONLY that method.
/// <c>ShowTextInquiry</c>, which asks for typed input rather than a yes/no, is guarded nowhere: not by
/// the host runtime and, until now, not by us. A headless process has no keyboard and no dialog
/// system, so raising one is an interaction that can never be completed.
/// <para>
/// That matters because the conversion reaches this API. <c>EOE.CustomBattlePatch</c> calls both
/// <c>ShowInquiry</c> and <c>ShowTextInquiry</c>, and <c>Europe1100CampaignAuthorityGate</c>
/// deliberately confines its campaign behaviour to the host — so the one place a conversion text
/// prompt could be raised is precisely the one place it cannot be answered.
/// </para>
/// <para>
/// The negative action is taken rather than the affirmative, which is the opposite of what the host
/// does for a yes/no dialog, and deliberately so: an affirmative here would have to invent the typed
/// string, committing the caller to a value nobody chose. Declining is the only answer a machine can
/// honestly give to "type something". Where the caller offers no negative action, the prompt is simply
/// dropped.
/// </para>
/// <para>
/// Server-only. On a client this patch stands aside completely and the prompt is shown normally, which
/// is where a player-facing prompt belongs.
/// </para>
/// </remarks>
[HarmonyPatch(typeof(InformationManager))]
internal class HostTextInquiryGuardPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<HostTextInquiryGuardPatch>();

    [HarmonyPatch(nameof(InformationManager.ShowTextInquiry))]
    [HarmonyPrefix]
    private static bool ShowTextInquiryPrefix(TextInquiryData textData)
    {
        if (!ModInformation.IsServer) return true;

        // Logged at Warning, not Debug: reaching here means something tried to hold a conversation
        // with a machine, and whoever added that path needs to see it.
        Logger.Warning(
            "Suppressed a text prompt on the host — nothing here can answer it. Title: {Title} | Text: {Text}",
            textData?.TitleText, textData?.Text);

        try
        {
            textData?.NegativeAction?.Invoke();
        }
        catch (Exception error)
        {
            // A throwing decline must not take the game thread down with it; the prompt is already
            // suppressed, which is the part that matters.
            Logger.Error(error, "The declined host text prompt threw while cancelling.");
        }

        return false;
    }
}
