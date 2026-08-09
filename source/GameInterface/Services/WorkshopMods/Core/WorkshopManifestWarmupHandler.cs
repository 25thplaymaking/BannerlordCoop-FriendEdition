using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.GameState.Messages;
using Serilog;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace GameInterface.Services.WorkshopMods.Core;

/// <summary>
/// Captures TaleWorlds module metadata on the game thread, then pre-hashes the server's managed
/// suite on a worker after the campaign is ready. A failed preparation is retained by the provider,
/// so all later joins fail closed without repeating discovery or hashing on the network thread.
/// </summary>
internal sealed class WorkshopManifestWarmupHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<WorkshopManifestWarmupHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IWorkshopManifestProvider manifestProvider;
    private volatile bool disposed;
    private int started;

    public WorkshopManifestWarmupHandler(
        IMessageBroker messageBroker,
        IWorkshopManifestProvider manifestProvider)
    {
        this.messageBroker = messageBroker;
        this.manifestProvider = manifestProvider;
        messageBroker.Subscribe<CampaignReady>(HandleCampaignReady);
    }

    public void Dispose()
    {
        disposed = true;
        messageBroker.Unsubscribe<CampaignReady>(HandleCampaignReady);
    }

    private void HandleCampaignReady(MessagePayload<CampaignReady> payload)
    {
        if (!ModInformation.IsServer || !manifestProvider.EnforceHandshake ||
            Interlocked.Exchange(ref started, 1) != 0)
            return;

        GameThread.RunSafe(CaptureAndWarmManifest, context: nameof(CaptureAndWarmManifest));
    }

    private void CaptureAndWarmManifest()
    {
        if (disposed) return;

        try
        {
            WorkshopManifestPreparation preparation =
                manifestProvider.PrepareManifest(WorkshopPeerRole.Server);
            ThreadPool.QueueUserWorkItem(_ => WarmManifest(preparation));
        }
        catch (Exception exception)
        {
            // The production provider records this discovery failure durably. Connection handlers
            // will subsequently reject joins through TryGetPreparedManifest instead of retrying.
            Logger.Error(exception, "Capturing server Workshop compatibility metadata failed");
        }
    }

    private void WarmManifest(WorkshopManifestPreparation preparation)
    {
        if (disposed) return;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            WorkshopCompatibilityManifest manifest = manifestProvider.BuildPreparedManifest(preparation);
            Logger.Information(
                "Prepared Friend Edition server Workshop manifest in {ElapsedMilliseconds} ms",
                stopwatch.ElapsedMilliseconds);

            string[] inactive = manifest.Entries
                .Where(entry => !entry.Active)
                .Select(entry => entry.ModuleId)
                .ToArray();
            if (inactive.Length > 0)
            {
                Logger.Warning(
                    "Friend Edition packages are staged but these upstream modules are inactive; " +
                    "their integrations remain feature-guarded: {InactiveModules}",
                    string.Join(", ", inactive));
            }
        }
        catch (Exception exception)
        {
            // BuildPreparedManifest retains this failure. Future joins fail closed immediately and
            // cannot trigger another multi-gigabyte hash on the connection/network thread.
            Logger.Error(exception, "Preparing the server Workshop compatibility manifest failed");
        }
    }
}
