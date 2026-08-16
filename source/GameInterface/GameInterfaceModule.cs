using Autofac;
using Autofac.Core;
using Autofac.Core.Registration;
using Autofac.Core.Resolving.Pipeline;
using Common.Logging;
using Common.PacketHandlers;
using GameInterface.AutoSync;
using GameInterface.Configuration;
using GameInterface.Registry;
using GameInterface.Serialization;
using GameInterface.Services;
using GameInterface.Services.Armies;
using GameInterface.Services.Bandits;
using GameInterface.Services.Barters;
using GameInterface.Services.Chat;
using GameInterface.Services.Entity;
using GameInterface.Services.GameDebug.Metrics;
using GameInterface.Services.Heroes;
using GameInterface.Services.Kingdoms;
using GameInterface.Services.LiveTesting;
using GameInterface.Services.MapEventParties;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Initialization;
using GameInterface.Services.MapEvents.Logging;
using GameInterface.Services.MobileParties;
using GameInterface.Services.MobileParties.Data;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Party;
using GameInterface.Services.Players;
using GameInterface.Services.Separatism;
using GameInterface.Services.Stances;
using GameInterface.Services.TroopRosters.Logging;
using GameInterface.Services.Time;
using GameInterface.Services.WorkshopMods.Core;
using GameInterface.Services.WorkshopMods.Diplomacy;
using GameInterface.Services.WorkshopMods.Fourberie;
using GameInterface.Services.WorkshopMods.ImprovedGarrisons;
using GameInterface.Services.WorkshopMods.PlayerSettlement;
using GameInterface.Services.Workshops;
using GameInterface.Surrogates;
using HarmonyLib;
using Serilog;
using System.Linq;

namespace GameInterface;

public class GameInterfaceModule : Module
{
    // TODO move to config
    public const string HarmonyId = "Bannerlord.Coop";

    private static readonly Harmony harmony = new Harmony(HarmonyId);

    /// <summary>
    /// The Workshop modules this assembly declares through the module contract. Adding a mod is one
    /// entry here plus its <see cref="IWorkshopModule"/> implementation — the registrar takes care of
    /// presence, fingerprinting, patch application and sync registration from there.
    /// </summary>
    /// <remarks>
    /// Combat mods (DismembermentPlus and UnblockableThrust) declare themselves in MissionModule
    /// instead, next to the Missions-assembly adapters they gate, and go through the same registrar.
    /// A module's category is applied against the assembly that declares the module, so each side
    /// owns its own list.
    /// <para>
    /// The four campaign adapters are declared here. Improved Garrisons, Fourberie, and Player
    /// Settlement patch imperatively from their compatibility handlers, so their declarations use a
    /// null category while still participating in the shared catalog, fingerprint, config, and sync
    /// registration gates.
    /// </para>
    /// </remarks>
    private static readonly IWorkshopModule[] DeclaredWorkshopModules =
    {
        new ImprovedGarrisonsModule(),
        new FourberieModule(),
        new DiplomacyModule(),
        new PlayerSettlementModule(),
    };

    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterInstance(harmony).As<Harmony>().SingleInstance();

        builder.RegisterType<SurrogateCollection>().As<ISurrogateCollection>().InstancePerLifetimeScope().AutoActivate();

        builder.RegisterType<GameInterface>().As<IGameInterface>().InstancePerLifetimeScope().AutoActivate();
        // mod-config.json: one lazy read per session container (see IModConfig).
        builder.RegisterType<ModConfig>().As<IModConfig>().InstancePerLifetimeScope();
        builder.RegisterType<ModConfigAuthority>().As<IModConfigAuthority>().InstancePerLifetimeScope();
        builder.RegisterType<BinaryPackageFactory>().As<IBinaryPackageFactory>().InstancePerLifetimeScope();
        builder.RegisterType<ControllerIdProvider>().As<IControllerIdProvider>().InstancePerLifetimeScope();
        builder.RegisterType<TimeControlModeConverter>().As<ITimeControlModeConverter>().InstancePerLifetimeScope();
        builder.RegisterType<PlayerManager>().As<IPlayerManager>().InstancePerLifetimeScope();
        builder.RegisterType<ChatPlayerName>().As<IChatPlayerNameResolver>().InstancePerDependency();
        builder.RegisterType<PlayerPartyRestorer>().As<IPlayerPartyRestorer>().InstancePerDependency();
        builder.RegisterType<MobilePartyBehaviorSnapshot>().As<IMobilePartyBehaviorSnapshot>().InstancePerDependency();
        builder.RegisterType<BarterClientPresentation>().As<IBarterClientPresentation>().InstancePerDependency();
        builder.RegisterType<SafePassagePartyResolver>().AsSelf().As<ISafePassagePartyResolver>().InstancePerDependency();
        builder.RegisterType<PeacePursuitCleaner>().As<IPeacePursuitCleaner>().InstancePerDependency();
        builder.RegisterType<PartyVisibilitySweep>().As<IPartyVisibilitySweep>().InstancePerDependency();
        builder.RegisterType<ConversationRestartContextTracker>().As<IConversationRestartContextTracker>().InstancePerLifetimeScope();
        builder.RegisterType<BattleHostRegistry>().As<IBattleHostRegistry>().InstancePerLifetimeScope();
        builder.RegisterType<BattleAgentBudget>().As<IBattleAgentBudget>().InstancePerDependency();
        builder.RegisterType<MapEventContributionBarrier>().As<IMapEventContributionBarrier>().InstancePerDependency();
        builder.RegisterType<ArmyDisbander>().As<IArmyDisbander>().InstancePerDependency();
        builder.RegisterType<MapEventLoadCleaner>().As<IMapEventLoadCleaner>().InstancePerDependency();
        builder.RegisterType<EncounterMenuConditionRefresher>().As<IEncounterMenuConditionRefresher>().InstancePerDependency();
        builder.RegisterType<PartyScreenRosterRefresher>().As<IPartyScreenRosterRefresher>().InstancePerDependency();
        builder.RegisterType<PrisonerSaleValidator>().As<IPrisonerSaleValidator>().InstancePerDependency();
        builder.RegisterType<PlayerRansomReleaseSettlementProvider>().As<IPlayerRansomReleaseSettlementProvider>().InstancePerDependency();
        builder.RegisterType<PrisonerSaleProcessor>().As<IPrisonerSaleProcessor>().InstancePerDependency();
        builder.RegisterType<PartyScreenRosterBaselineProvider>().As<IPartyScreenRosterBaselineProvider>().InstancePerDependency();
        builder.RegisterType<BanditPartyHomeSettlementRepairer>().As<IBanditPartyHomeSettlementRepairer>().InstancePerDependency();
        builder.RegisterType<DeadHeroCaptivityRepairer>().As<IDeadHeroCaptivityRepairer>().InstancePerDependency();
        builder.RegisterType<WorkshopRepairer>().As<IWorkshopRepairer>().InstancePerDependency();
        builder.RegisterType<MapEventLogger>().As<IMapEventLogger>().InstancePerLifetimeScope();
        builder.RegisterType<TroopRosterLogger>().As<ITroopRosterLogger>().InstancePerLifetimeScope();
        builder.RegisterType<PartySyncPerformanceClock>().As<IPartySyncPerformanceClock>().InstancePerLifetimeScope();
        builder.RegisterType<PartySyncPerformanceFileWriter>().As<IPartySyncPerformanceFileWriter>().InstancePerLifetimeScope();
        builder.RegisterType<PartySyncPerformancePartyProvider>().As<IPartySyncPerformancePartyProvider>().InstancePerLifetimeScope();
        builder.RegisterType<LiveTestCommandDispatcher>().As<ILiveTestCommandDispatcher>().InstancePerDependency();
        builder.RegisterType<KingdomCreationSettlementTracker>().AsSelf().As<IKingdomCreationSettlementTracker>().InstancePerLifetimeScope();
        builder.RegisterType<KingdomCreator>().AsSelf().As<IKingdomCreator>().InstancePerLifetimeScope();
        builder.RegisterType<KingdomDecisionOutcomeResolver>().AsSelf().As<IKingdomDecisionOutcomeResolver>().InstancePerLifetimeScope();
        builder.RegisterType<KingdomDecisionVoteManager>().AsSelf().As<IKingdomDecisionVoteManager>().InstancePerLifetimeScope();
        builder.RegisterType<KingdomMembershipState>().AsSelf().As<IKingdomMembershipState>().InstancePerLifetimeScope();
        builder.RegisterType<SeparatismCampaignService>().As<ISeparatismCampaignService>().InstancePerLifetimeScope();
        builder.RegisterType<SeparatismCapabilitySource>()
            .As<IWorkshopCapabilitySource>()
            .InstancePerLifetimeScope();
        builder.RegisterType<ImprovedGarrisonsCapabilitySource>()
            .As<IWorkshopCapabilitySource>()
            .InstancePerLifetimeScope();
        builder.RegisterType<PlayerSettlementCapabilitySource>()
            .As<IWorkshopCapabilitySource>()
            .InstancePerLifetimeScope();
        builder.RegisterType<FourberieCapabilitySource>()
            .As<IWorkshopCapabilitySource>()
            .InstancePerLifetimeScope();
        builder.RegisterType<DiplomacyCapabilitySource>()
            .As<IWorkshopCapabilitySource>()
            .InstancePerLifetimeScope();
        builder.RegisterType<MainPartyBattleRewardsCache>().As<IMainPartyBattleRewardsCache>().InstancePerLifetimeScope();
        builder.RegisterType<PacketManager>().As<IPacketManager>().InstancePerLifetimeScope();
        builder.RegisterType<MapEventInitializationBarrierBinding>().InstancePerLifetimeScope().AutoActivate();

        RegisterWorkshopModules(builder);

        builder.RegisterModule<ServiceModule>();
        builder.RegisterModule<ObjectManagerModule>();
        builder.RegisterModule<RegistryModule>();
        builder.RegisterModule<AutoSyncModule>();


        base.Load(builder);
    }

    /// <summary>
    /// Publishes the declared modules for DI (WorkshopModuleAutoSync consumes them) and registers a
    /// Harmony category for each one whose pinned build is actually loaded.
    /// </summary>
    /// <remarks>
    /// The category is registered ONLY when the module resolves. Applying a category whose patch
    /// classes have no resolvable targets throws exactly like the uncategorised path did, and takes
    /// every remaining Coop patch with it.
    /// <para>
    /// Presence is the only term consulted here, not <c>ResolveLiveModules</c>: this runs while the
    /// container is built, and the operator's configuration does not exist yet — ModConfigAuthority
    /// installs it at CampaignReady, well after <c>GameInterface.PatchAll</c>. Gating patch
    /// application on an unread config would disable every Workshop adapter unconditionally. Presence
    /// is already peer-symmetric: WorkshopManifestValidator refuses a session whose members do not
    /// carry the same components at the same versions.
    /// </para>
    /// </remarks>
    private static void RegisterWorkshopModules(ContainerBuilder builder)
    {
        foreach (IWorkshopModule module in DeclaredWorkshopModules)
        {
            builder.RegisterInstance(module).As<IWorkshopModule>();
        }

        foreach (IWorkshopModule module in
                 WorkshopModuleRegistrar.ResolveInstalledModules(DeclaredWorkshopModules))
        {
            // A null category means the module owns no presence-gated adapters; see IWorkshopModule.
            if (module.PatchCategory == null) continue;

            builder.RegisterInstance(new HarmonyPatchCategoryRegistration(
                module.GetType().Assembly,
                module.PatchCategory));
        }
    }

    // Log injector
    protected override void AttachToComponentRegistration(IComponentRegistryBuilder componentRegistry, IComponentRegistration registration)
    {
        registration.PipelineBuilding += (sender, pipeline) =>
        {
            pipeline.Use(PipelinePhase.Activation, MiddlewareInsertionMode.StartOfPhase, (c, next) =>
            {
                var forType = c.Registration.Activator.LimitType;

                var logParameter = new ResolvedParameter(
                    (p, c) => p.ParameterType == typeof(ILogger),
                    (p, c) => AccessTools.Method(typeof(LogManager), nameof(LogManager.GetLogger)).MakeGenericMethod(forType).Invoke(null, null) as ILogger);

                c.GetType().Property(nameof(c.Parameters)).SetValue(c, c.Parameters.Union(new[] { logParameter }));

                next(c);
            });
        };
    }
}
