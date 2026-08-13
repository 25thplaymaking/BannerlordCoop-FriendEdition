using Common.Messaging;
using Common.Logging;
using GameInterface.AutoSync;
using GameInterface.Registry.Auto;
using GameInterface.Registry.Messages;
using GameInterface.Services.ObjectManager;
using Serilog;

namespace GameInterface.Registry;

public interface IRegistryManager
{
    void PatchLifetimes();
    void RegisterAllGameObjects();
    void ClearAllRegistries();
}

internal class RegistryManager : IRegistryManager
{
    private static readonly ILogger Logger = LogManager.GetLogger<RegistryManager>();

    private readonly IObjectManager objectManager;
    private readonly IRegistryCollection registryCollection;
    private readonly IMessageBroker messageBroker;
    private readonly IAutoRegistryFactory autoRegistryFactory;
    private readonly IAutoSyncPatchCollector autoSyncPatchCollector;

    public RegistryManager(
        IObjectManager objectManager,
        IRegistryCollection registryCollection,
        IMessageBroker messageBroker,
        IAutoRegistryFactory autoRegistryFactory,
        IAutoSyncPatchCollector autoSyncPatchCollector)
    {
        this.objectManager = objectManager;
        this.registryCollection = registryCollection;
        this.messageBroker = messageBroker;
        this.autoRegistryFactory = autoRegistryFactory;
        this.autoSyncPatchCollector = autoSyncPatchCollector;
    }

    public void PatchLifetimes()
    {
        autoSyncPatchCollector.PatchAll();
    }

    public void RegisterAllGameObjects()
    {
        autoRegistryFactory.RegisterAll();

        var audit = RegistryCompletenessAudit.Run(objectManager);
        if (audit.Available && audit.Missing > 0)
        {
            Logger.Fatal(
                "[RegistryAudit] FAILED after RegisterAll: {Missing}/{Expected} critical campaign objects " +
                "are unregistered. Missing examples: {Examples}",
                audit.Missing,
                audit.Expected,
                audit.MissingExamples);
            throw new System.InvalidOperationException(
                $"Campaign registry incomplete after RegisterAll: {audit.Missing}/{audit.Expected} critical objects missing");
        }

        if (audit.Available)
        {
            Logger.Information(
                "[RegistryAudit] PASS after RegisterAll: {Expected} critical campaign objects registered; " +
                "parties={Parties} armies={Armies} mapEvents={MapEvents} rosters={Rosters}",
                audit.Expected,
                audit.Parties,
                audit.Armies,
                audit.MapEvents,
                audit.Rosters);
        }

        messageBroker.Publish(this, new AllGameObjectsRegistered());
    }

    public void ClearAllRegistries()
    {
        registryCollection.ClearRegistries();
        objectManager.Clear();
    }
}
