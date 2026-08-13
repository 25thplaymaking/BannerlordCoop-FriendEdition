using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Policies;
using GameInterface.Services.MapEvents.Messages;
using HarmonyLib;
using Helpers;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// Replaces the formerly disabled post-battle capture consequence with a typed request. The server
/// authenticates the requesting party, validates the defeated NPC field battle, and then uses the
/// existing authoritative battle-conclusion pipeline to close the encounter and stage rewards.
/// </summary>
[HarmonyPatch(typeof(MenuHelper))]
internal static class EncounterCaptureTheEnemyConsequencePatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(EncounterCaptureTheEnemyConsequencePatch));

    [HarmonyPatch(nameof(MenuHelper.EncounterCaptureTheEnemyOnConsequence))]
    [HarmonyPrefix]
    private static bool Prefix()
    {
        if (CallOriginalPolicy.IsOriginalAllowed() || ModInformation.IsServer)
            return true;

        MapEvent mapEvent = GetEncounterBattle() ?? MobileParty.MainParty?.MapEvent;
        MobileParty playerParty = MobileParty.MainParty;
        if (mapEvent == null || playerParty == null)
        {
            Logger.Warning("Client could not route defeated-enemy capture because the encounter battle or player party was absent");
            return false;
        }

        MessageBroker.Instance.Publish(
            playerParty,
            new CaptureDefeatedEnemyAttempted(mapEvent, playerParty));
        return false;
    }

    private static MapEvent GetEncounterBattle()
    {
        try
        {
            return PlayerEncounter.Battle ?? PlayerEncounter.EncounteredBattle;
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }
}
