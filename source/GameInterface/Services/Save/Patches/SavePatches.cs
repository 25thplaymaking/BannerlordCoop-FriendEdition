using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.Heroes.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Save.Commands;
using GameInterface.Services.Save.Messages;
using HarmonyLib;
using Serilog;
using System;
using System.Diagnostics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;

namespace GameInterface.Services.Save.Patches;

/// <summary>
/// Announces the host's save to the clients, and records how long it actually took.
/// </summary>
/// <remarks>
/// <see cref="Game.Save"/> is the one entry point every host save goes through, which is the reason
/// the saving state is raised here rather than alongside <see cref="SaveStartedPatch"/>. The
/// dedicated host does not autosave through <see cref="SaveHandler"/> at all: it builds the metadata
/// itself and calls <c>Game.Current.Save(metaData, name, new AsyncFileSaveDriver(), callback)</c>
/// directly, so <c>SaveHandler.OnSaveStarted</c> and <c>OnSaveEnded</c> are never reached and the
/// patches on them never fire. That is why players saw no saving indicator during the freeze — the
/// notification pipeline was complete and simply never triggered on this host.
/// <para>
/// The publish happens before the game thread blocks. Sending is the network thread's job, not the
/// game thread's, so the "saving" packet leaves while the save is still collecting rather than
/// arriving with the backlog afterwards, when it would be useless.
/// </para>
/// <para>
/// Clearing it is a finalizer rather than a postfix on purpose: a postfix is skipped when the
/// original throws, and a failed save that leaves every client showing a saving indicator forever is
/// a worse outcome than the failure itself.
/// </para>
/// </remarks>
[HarmonyPatch(typeof(Game), nameof(Game.Save))]
class SavePatches
{
    private static readonly ILogger Logger = LogManager.GetLogger<SavePatches>();

    static bool Prefix(Game __instance, ref string saveName, out Stopwatch __state)
    {
        __state = null;
        if (!ModInformation.IsServer) return true;

        // Skip a periodic autosave that would land in the middle of someone's battle. Declining here
        // leaves the caller's timer untouched, which is what makes this safe - see
        // AutosaveBattleDeferral. Evaluated before anything is published so a deferred save raises no
        // saving indicator on the clients.
        ContainerProvider.TryResolve<IPlayerManager>(out var playerManager);
        ContainerProvider.TryResolve<IObjectManager>(out var objectManager);
        if (AutosaveBattleDeferral.ShouldDefer(saveName, playerManager, objectManager)) return false;

        __state = Stopwatch.StartNew();
        MessageBroker.Instance.Publish(__instance, new GameSaved(saveName));
        MessageBroker.Instance.Publish(__instance, new GameSaveStateChanged(true));

        return true;
    }

    static Exception Finalizer(Game __instance, string saveName, Stopwatch __state, Exception __exception)
    {
        if (__state == null) return __exception;

        MessageBroker.Instance.Publish(__instance, new GameSaveStateChanged(false));

        __state.Stop();
        if (__exception == null)
        {
            AutosaveBattleDeferral.NoteSaveCompleted();
            Logger.Information(
                "Saved '{SaveName}'; the game thread was blocked for {ElapsedMs} ms.",
                saveName, __state.ElapsedMilliseconds);
        }
        else
        {
            Logger.Error(
                __exception,
                "Save '{SaveName}' failed after {ElapsedMs} ms; cleared the saving state on all clients.",
                saveName, __state.ElapsedMilliseconds);
        }

        // Never swallow: the caller decides what a failed save means.
        return __exception;
    }
}

/// <summary>
/// Raises the saving state for hosts that DO save through <see cref="SaveHandler"/>.
/// </summary>
/// <remarks>
/// Kept alongside the <see cref="Game.Save"/> patch rather than replaced by it. This one fires a tick
/// earlier — <c>OnSaveStarted</c> runs on the tick before the save itself — so where it applies it
/// buys the clients a little more warning. <c>SaveGameHandler</c> reference-counts the sources, so a
/// host where both fire raises the state once and clears it once.
/// </remarks>
[HarmonyPatch(typeof(SaveHandler), "OnSaveStarted")]
internal class SaveStartedPatch
{
    static void Prefix(SaveHandler __instance)
    {
        if (ModInformation.IsServer)
        {
            MessageBroker.Instance.Publish(__instance, new GameSaveStateChanged(true));
            SaveDebugCommand.HoldForEvidenceIfRequested();
        }
    }
}

[HarmonyPatch(typeof(SaveHandler), "OnSaveEnded")]
internal class SaveEndedPatch
{
    static void Postfix(SaveHandler __instance)
    {
        if (ModInformation.IsServer)
        {
            MessageBroker.Instance.Publish(__instance, new GameSaveStateChanged(false));
        }
    }
}
