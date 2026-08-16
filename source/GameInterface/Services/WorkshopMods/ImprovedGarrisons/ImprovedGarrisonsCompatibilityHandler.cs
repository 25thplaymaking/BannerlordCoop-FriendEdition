using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.CampaignService.Messages;
using GameInterface.Registry.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using Helpers;
using LiteNetLib;
using Serilog;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Core.ImageIdentifiers;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace GameInterface.Services.WorkshopMods.ImprovedGarrisons;

internal readonly struct ImprovedGarrisonsSnapshotIntent
{
}

internal sealed class ImprovedGarrisonsSettingIntent
{
    public ImprovedGarrisonsSettingIntent(string managerType, string method, string townId, string value)
    {
        ManagerType = managerType;
        Method = method;
        TownId = townId;
        Value = value;
    }

    public string ManagerType { get; }
    public string Method { get; }
    public string TownId { get; }
    public string Value { get; }
}

internal sealed class ImprovedGarrisonsManagementIntent
{
    public ImprovedGarrisonsManagementIntent(NetworkRequestImprovedGarrisonsOperation request) => Request = request;
    public NetworkRequestImprovedGarrisonsOperation Request { get; }
}

/// <summary>
/// Reflection-only compatibility boundary for Improved Garrisons 4.2.0.7. Native campaign
/// mutations, AI ticks, party creation and external persistence are server-authoritative. The
/// supported UI settings and stable selection payloads are validated and routed client -&gt; server.
/// </summary>
internal sealed class ImprovedGarrisonsCompatibilityHandler : IHandler, IImprovedGarrisonsPatchRuntime
{
    internal delegate bool CanonicalStateApplier(
        IReadOnlyCollection<ImprovedGarrisonsStateValue> values,
        out string failure);

    internal const int MaxRequestManagerLength = 192;
    internal const int MaxRequestMethodLength = 96;
    internal const int MaxObjectIdLength = 256;
    internal const int MaxRequestValueLength = 256;
    internal const int MaxSnapshotValues = 16384;
    internal const int MaxSnapshotPropertyLength = 768;
    internal const int MaxSnapshotValueLength = 4096;
    internal const int MaxSnapshotCharacters = 4 * 1024 * 1024;

    private static readonly ILogger Logger = LogManager.GetLogger<ImprovedGarrisonsCompatibilityHandler>();
    private static readonly object PatchSync = new object();
    private static Assembly patchedAssembly;

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;
    private readonly IPlayerManager playerManager;
    private readonly IModConfigAuthority configAuthority;
    private readonly IWorkshopCapabilityRegistry capabilityRegistry;
    private readonly IAuthorityRequestRouter authorityRequestRouter;
    private readonly IAuthorityRouteHandle<ImprovedGarrisonsSnapshotIntent, NetworkImprovedGarrisonsStateQueryResult> snapshotRoute;
    private readonly IAuthorityRouteHandle<ImprovedGarrisonsSettingIntent, NetworkImprovedGarrisonsSettingResult> settingRoute;
    private readonly IAuthorityRouteHandle<ImprovedGarrisonsManagementIntent, NetworkImprovedGarrisonsOperationResult> managementRoute;
    private readonly Harmony harmony;
    private readonly HashSet<string> deniedNotifications = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> pendingPartyScreens =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, (ImprovedGarrisonsMethodSpec Spec, MethodInfo Method)> routedMethods =
        new Dictionary<string, (ImprovedGarrisonsMethodSpec, MethodInfo)>(StringComparer.Ordinal);

    private Assembly assembly;
    private long revision;
    private string lastPublishedHash;
    private string lastAppliedHash;
    private bool compatible;
    private bool stateReady;
    private bool objectsRegistered;
    internal WorkshopSnapshotReadiness SnapshotReadiness { get; private set; }
    internal string SnapshotSessionId { get; private set; }
    internal long SnapshotRevision { get; private set; } = -1;

    public ImprovedGarrisonsCompatibilityHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IModConfigAuthority configAuthority,
        IWorkshopCapabilityRegistry capabilityRegistry,
        Harmony harmonyDependency,
        IAuthorityRequestRouter authorityRequestRouter)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.configAuthority = configAuthority;
        this.capabilityRegistry = capabilityRegistry;
        this.authorityRequestRouter = authorityRequestRouter;
        this.harmony = new Harmony(ImprovedGarrisonsCompatibilityManifest.AdapterHarmonyId);

        compatible = TryInstall();
        if (compatible) ImprovedGarrisonsPatchRuntime.Current = this;

        snapshotRoute = authorityRequestRouter.Register(
            AuthorityRoute<ImprovedGarrisonsSnapshotIntent, NetworkRequestImprovedGarrisonsState,
                NetworkImprovedGarrisonsStateQueryResult>.Define(
                "workshop.improved-garrisons.snapshot", AuthorityRouteKind.BootstrapQuery,
                CreateSnapshotHeader,
                (_, header) => new NetworkRequestImprovedGarrisonsState(header),
                request => request.Header,
                result => result.Header,
                request => request.Header.TryValidate(out _) ? null : "invalid-improved-garrisons-snapshot-query",
                request => "snapshot:" + request.Header.SessionId + ":" + request.Header.ExpectedRevision,
                ValidateSnapshotHeader,
                ExecuteSnapshotQuery,
                CreateSnapshotTerminal,
                ProbeSnapshotApplied,
                _ => { },
                PresentSnapshotTerminal,
                configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.BootstrapQuery,
                requireAuthenticatedPlayer: false));

        settingRoute = authorityRequestRouter.Register(
            AuthorityRoute<ImprovedGarrisonsSettingIntent, NetworkRequestImprovedGarrisonsSettingChange,
                NetworkImprovedGarrisonsSettingResult>.Define(
                "workshop.improved-garrisons.setting", AuthorityRouteKind.Command,
                CreateManagementHeader,
                (intent, header) => new NetworkRequestImprovedGarrisonsSettingChange(
                    header, intent.ManagerType, intent.Method, intent.TownId, intent.Value),
                request => request.Header,
                result => result.Header,
                request => IsRequestShapeValid(request) ? null : "invalid-improved-garrisons-setting",
                SettingCommandKey,
                ValidateManagementHeader,
                ExecuteSettingRoute,
                CreateSettingTerminal,
                ProbeSettingApplied,
                _ => StartSnapshotBootstrap(),
                PresentSettingOutcome,
                configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true,
                isExpectedClientResult: IsExpectedSettingResult));

        managementRoute = authorityRequestRouter.Register(
            AuthorityRoute<ImprovedGarrisonsManagementIntent, NetworkRequestImprovedGarrisonsOperation,
                NetworkImprovedGarrisonsOperationResult>.Define(
                "workshop.improved-garrisons.management", AuthorityRouteKind.Command,
                CreateManagementHeader,
                (intent, header) => BuildManagementRequest(intent, header),
                request => request.Header,
                result => result.Header,
                request => ImprovedGarrisonsOperationProtocol.IsRequestShapeValid(request) ? null :
                    "invalid-improved-garrisons-management",
                ImprovedGarrisonsOperationProtocol.CommandKey,
                ValidateManagementHeader,
                ExecuteManagementRoute,
                CreateManagementTerminal,
                ProbeManagementApplied,
                _ => StartSnapshotBootstrap(),
                PresentManagementOutcome,
                configAuthority.IsTrustedServer,
                AuthorityTimeoutPolicy.CampaignMutation,
                failClosedOnApplyFailure: true,
                isExpectedClientResult: IsExpectedManagementResult));

        messageBroker.Subscribe<AllGameObjectsRegistered>(Handle_AllGameObjectsRegistered);
        messageBroker.Subscribe<NetworkImprovedGarrisonsState>(Handle_State);
        messageBroker.Subscribe<NetworkImprovedGarrisonsStateQueryResult>(Handle_StateQueryResult);
        messageBroker.Subscribe<HostModConfigAccepted>(HandleHostModConfigAccepted);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<AllGameObjectsRegistered>(Handle_AllGameObjectsRegistered);
        messageBroker.Unsubscribe<NetworkImprovedGarrisonsState>(Handle_State);
        messageBroker.Unsubscribe<NetworkImprovedGarrisonsStateQueryResult>(Handle_StateQueryResult);
        messageBroker.Unsubscribe<HostModConfigAccepted>(HandleHostModConfigAccepted);
        snapshotRoute.Dispose();
        settingRoute.Dispose();
        managementRoute.Dispose();
        if (ReferenceEquals(ImprovedGarrisonsPatchRuntime.Current, this)) ImprovedGarrisonsPatchRuntime.Current = null;
        pendingPartyScreens.Clear();
    }

    public bool TryRouteSetting(object manager, MethodBase method, object[] arguments)
    {
        if (!CanUseManagementRoute(out var config) || method?.DeclaringType == null ||
            arguments == null || arguments.Length == 0 ||
            arguments[0] is not Town town || !objectManager.TryGetId(town, out var townId))
            return false;

        var key = RoutedKey(method.DeclaringType.FullName, method.Name);
        if (!routedMethods.ContainsKey(key)) return false;

        var value = arguments.Length == 1 ? string.Empty : ImprovedGarrisonsCanonicalState.FormatValue(arguments[1]);
        if (!ModInformation.IsClient) return false;
        settingRoute.Submit(new ImprovedGarrisonsSettingIntent(
            method.DeclaringType.FullName, method.Name, townId, value));
        return true;
    }

    public bool TryPreparePresentation(
        object manager,
        MethodBase method,
        object[] arguments,
        out bool runOriginal)
    {
        runOriginal = false;
        if (method == null || !CanUseManagementRoute(out _)) return false;
        runOriginal = method.Name != "PromptGarrisonSelector" || !TryShowOwnedGarrisonSelector(arguments);
        return true;
    }

    private bool TryShowOwnedGarrisonSelector(object[] arguments)
    {
        if (arguments == null || arguments.Length != 5 ||
            arguments[0] is not string title || arguments[1] is not string description ||
            arguments[2] is not int maximum || arguments[3] is not Town currentTown ||
            arguments[4] is not Action<List<InquiryElement>> positiveAction ||
            Hero.MainHero?.Clan == null || !TryGetSettingsDictionary(out var settings))
            return false;

        var choices = Settlement.All
            .Where(settlement => settlement?.Town != null && settlement.Town != currentTown &&
                                 (settlement.IsTown || settlement.IsCastle) &&
                                 settlement.Town.OwnerClan == Hero.MainHero.Clan &&
                                 settings.Contains(settlement.Town.Name?.ToString()))
            .Select(settlement => new InquiryElement(
                settlement.Town,
                settlement.Name?.ToString() ?? settlement.StringId,
                new EmptyImageIdentifier()))
            .ToList();
        var inquiry = new MultiSelectionInquiryData(
            title,
            description,
            choices,
            true,
            0,
            maximum,
            new TextObject("{=menu_ok}Okay").ToString(),
            new TextObject("{=menu_back}Back").ToString(),
            positiveAction,
            null,
            string.Empty,
            false);
        MBInformationManager.ShowMultiSelectionInquiry(inquiry, false, false);
        return true;
    }

    public bool TryRouteOperation(object manager, MethodBase method, object[] arguments)
    {
        if (!CanUseManagementRoute(out var config) || manager == null || method?.DeclaringType == null ||
            !TryBuildOperation(manager, method, arguments ?? Array.Empty<object>(), config, out var request))
            return false;

        if (!ModInformation.IsClient) return false;
        managementRoute.Submit(new ImprovedGarrisonsManagementIntent(request));
        return true;
    }

    private bool TryBuildOperation(
        object manager,
        MethodBase method,
        object[] arguments,
        ModConfigSnapshot config,
        out NetworkRequestImprovedGarrisonsOperation request)
    {
        request = null;
        string type = method.DeclaringType.FullName;
        string name = method.Name;
        if (type.EndsWith(".GarrisonPartyBehavior", StringComparison.Ordinal))
            return TryBuildPartyConversationOperation(manager, name, arguments, config, out request);

        if (!TryFindTown(manager, arguments, out var town) || !objectManager.TryGetId(town, out var townId))
            return false;
        var operation = default(ImprovedGarrisonsOperation);
        string[] targets = Array.Empty<string>();
        string value = string.Empty;
        ImprovedGarrisonsTroopSelection[] troops = Array.Empty<ImprovedGarrisonsTroopSelection>();

        if (type.EndsWith(".BuildingVM+<>c", StringComparison.Ordinal) &&
            name == "<PromptReserveWindow>b__48_0")
        {
            operation = ImprovedGarrisonsOperation.BoostBuildingReserve;
            value = arguments.FirstOrDefault() as string;
        }
        else if (type.EndsWith(".ManagementSettings", StringComparison.Ordinal))
        {
            if (name == "Inquirydata_TranferGarrison")
            {
                operation = ImprovedGarrisonsOperation.CreateTransferParty;
                targets = StableIdsFromInquiry(arguments).Take(1).ToArray();
            }
            else
            {
                operation = ImprovedGarrisonsOperation.CopySettings;
                if (name == "Inquirydata_CopySpecific")
                    targets = StableIdsFromInquiry(arguments).ToArray();
                else
                    targets = Settlement.All
                        .Where(settlement => settlement?.Town != null && settlement.Town != town &&
                                             settlement.Town.OwnerClan == town.OwnerClan &&
                                             (name == "CopyToAllTowns" ? settlement.IsTown : settlement.IsCastle))
                        .Select(settlement => objectManager.TryGetId(settlement.Town, out var id) ? id : null)
                        .Where(id => id != null)
                        .ToArray();
            }
        }
        else if (type.EndsWith(".RecruitmentSettings", StringComparison.Ordinal))
        {
            if (name == "InquiryData_CultureToRecruitFrom")
            {
                operation = ImprovedGarrisonsOperation.ChangeRecruitmentCulture;
                targets = StableIdsFromInquiry(arguments).Take(1).ToArray();
            }
            else if (name == "ReturnRecruiter")
            {
                operation = ImprovedGarrisonsOperation.ReturnRecruiter;
            }
            else
            {
                operation = ImprovedGarrisonsOperation.CreateRecruiter;
                targets = StableIdsFromInquiry(arguments).Take(1).ToArray();
                if (targets.Length == 0 && TryGetMemberValue(manager, "_cultureToRecruitFromForNewRecruiter") is object culture &&
                    TryGetStableId(culture, out var cultureId))
                    targets = new[] { cultureId };
                value = Convert.ToString(
                    TryGetMemberValue(manager, "_amountToRecruitForNewRecruiter") ?? 50,
                    CultureInfo.InvariantCulture);
            }
        }
        else if (type.EndsWith(".TrainingSettings", StringComparison.Ordinal))
        {
            if (name == "SetSpecifiedUpgradeTargets")
            {
                operation = ImprovedGarrisonsOperation.ReplaceTownTemplate;
                value = GetTownTemplateName(town) ?? "Custom";
                troops = TroopsFromRoster(arguments).ToArray();
            }
            else if (name == "Inquirydata_SetUpgradePath")
            {
                operation = ImprovedGarrisonsOperation.SetUpgradePaths;
                targets = IdentifiersFromInquiry(arguments)
                    .Select(identifier => Convert.ToInt32(identifier, CultureInfo.InvariantCulture)
                        .ToString(CultureInfo.InvariantCulture))
                    .ToArray();
            }
            else
            {
                operation = ImprovedGarrisonsOperation.RemoveTownTemplateTroop;
                object character = arguments.Length > 1 ? arguments[1] : null;
                if (!TryGetStableId(character, out value)) return false;
            }
        }
        else if (type.EndsWith(".MobileGarrisonSettings", StringComparison.Ordinal))
        {
            if (name == "PromptCreateMobileGarrison")
                operation = ImprovedGarrisonsOperation.CreateMobileGarrison;
            else if (name == "OrderMobileGarrisonToPatrol")
                operation = ImprovedGarrisonsOperation.OrderMobileGarrisonPatrol;
            else if (name == "OrderMobileGarrisonReturn" || name == "OrderMobileGarrisonAttackOrDefend")
                operation = ImprovedGarrisonsOperation.OrderMobileGarrisonReturn;
            else
            {
                operation = ImprovedGarrisonsOperation.SetMobileGarrisonEscort;
                targets = StableIdsFromInquiry(arguments).Take(1).ToArray();
            }
        }
        else if (type.EndsWith(".TemplateManager", StringComparison.Ordinal))
        {
            if (name == "ApplyTemplate")
            {
                operation = ImprovedGarrisonsOperation.ApplyGlobalTemplate;
                value = GetTemplateName(arguments.FirstOrDefault());
            }
            else if (name == "RenameCurrentTemplate")
            {
                operation = ImprovedGarrisonsOperation.RenameGlobalTemplate;
                value = arguments.FirstOrDefault() as string;
                targets = new[] { GetTemplateName(TryGetMemberValue(manager, "_currentTrainingTemplate")) };
            }
            else if (name == "RemoveTemplate")
            {
                operation = ImprovedGarrisonsOperation.RemoveGlobalTemplate;
                value = GetTemplateName(arguments.FirstOrDefault());
            }
            else
            {
                operation = ImprovedGarrisonsOperation.CreateGlobalTemplate;
                value = arguments.FirstOrDefault() as string;
            }
        }
        else
        {
            return false;
        }

        request = new NetworkRequestImprovedGarrisonsOperation(
            config.SessionId,
            1,
            revision,
            operation,
            townId,
            targets,
            value,
            troops);
        return ImprovedGarrisonsOperationProtocol.IsRequestShapeValid(request);
    }

    private bool TryBuildPartyConversationOperation(
        object behavior,
        string methodName,
        object[] arguments,
        ModConfigSnapshot config,
        out NetworkRequestImprovedGarrisonsOperation request)
    {
        request = null;
        MobileParty sourceParty = MobileParty.ConversationParty;
        if (methodName == "Inquirydata_FortifyGarrison")
        {
            object mobileGarrison = TryGetMemberValue(behavior, "_currentMobileGarrisonForFortification");
            sourceParty = InvokeOptional(mobileGarrison, "getMobileParty") as MobileParty;
        }
        if (sourceParty == null || !TryGetStableId(sourceParty, out string sourcePartyId)) return false;

        ImprovedGarrisonsOperation operation;
        string townId;
        string[] targets = Array.Empty<string>();
        if (methodName == "conversation_fight_on_consequence")
        {
            operation = ImprovedGarrisonsOperation.StartHostileEncounter;
            townId = sourcePartyId;
            targets = new[] { sourcePartyId };
        }
        else
        {
            Town homeTown = sourceParty.HomeSettlement?.Town;
            if (homeTown == null || !objectManager.TryGetId(homeTown, out townId)) return false;
            switch (methodName)
            {
                case "Conversation_improvedgarrison_mobilegarrison_escort_on_consequence":
                    operation = ImprovedGarrisonsOperation.SetMobileGarrisonEscort;
                    if (!TryGetStableId(MobileParty.MainParty, out string mainPartyId)) return false;
                    targets = new[] { mainPartyId };
                    break;
                case "Conversation_improvedgarrison_mobilegarrison_patrol_on_consequence":
                    operation = ImprovedGarrisonsOperation.OrderMobileGarrisonPatrol;
                    break;
                case "Conversation_improvedgarrison_mobilegarrison_return_on_consequence":
                    operation = ImprovedGarrisonsOperation.OrderMobileGarrisonReturn;
                    break;
                case "Conversation_improvedgarrison_recruiter_return_on_consequence":
                    operation = ImprovedGarrisonsOperation.ReturnRecruiter;
                    break;
                case "Inquirydata_FortifyGarrison":
                    operation = ImprovedGarrisonsOperation.FortifyMobileGarrison;
                    targets = StableIdsFromInquiry(arguments).Take(1).ToArray();
                    break;
                default:
                    return false;
            }
        }

        request = new NetworkRequestImprovedGarrisonsOperation(
            config.SessionId,
            1,
            revision,
            operation,
            townId,
            targets,
            string.Empty,
            null);
        return ImprovedGarrisonsOperationProtocol.IsRequestShapeValid(request);
    }

    private bool CanUseManagementRoute(out ModConfigSnapshot config)
    {
        config = null;
        return compatible && stateReady && configAuthority.TryGetCurrent(out config) &&
               capabilityRegistry.IsEnabled(
                   ImprovedGarrisonsCapabilitySource.ModuleId,
                   ImprovedGarrisonsCapabilitySource.Operation) &&
               authorityRequestRouter.IsRegistered("workshop.improved-garrisons.setting", AuthorityRouteKind.Command) &&
               authorityRequestRouter.IsRegistered("workshop.improved-garrisons.management", AuthorityRouteKind.Command) &&
               authorityRequestRouter.IsRegistered("workshop.improved-garrisons.snapshot", AuthorityRouteKind.BootstrapQuery) &&
               (!ModInformation.IsClient || (SnapshotReadiness == WorkshopSnapshotReadiness.Ready &&
                   string.Equals(SnapshotSessionId, config.SessionId, StringComparison.Ordinal)));
    }

    private bool TryFindTown(object manager, object[] arguments, out Town town)
    {
        town = arguments.OfType<Town>().FirstOrDefault();
        if (town != null) return true;
        town = TryGetMemberValue(manager, "_currentTown") as Town ??
               TryGetMemberValue(manager, "_recruiterTown") as Town;
        if (town != null) return true;
        object behavior = TryGetMemberValue(manager, "garrisonBehavior");
        town = TryGetMemberValue(behavior, "CurrentTownForSettings") as Town;
        if (town != null) return true;
        object mainBehavior = GetStaticMember(
            assembly?.GetType("ImprovedGarrisons.Main", false),
            "GarrisonBehavior");
        town = TryGetMemberValue(mainBehavior, "CurrentTownForSettings") as Town;
        return town != null;
    }

    private IEnumerable<string> StableIdsFromInquiry(object[] arguments)
    {
        foreach (object identifier in IdentifiersFromInquiry(arguments))
            if (TryGetStableId(identifier, out string id)) yield return id;
    }

    private static IEnumerable<object> IdentifiersFromInquiry(object[] arguments)
    {
        if (arguments.FirstOrDefault() is not IEnumerable selected) yield break;
        foreach (object item in selected)
        {
            object identifier = TryGetMemberValue(item, "Identifier");
            if (identifier != null) yield return identifier;
        }
    }

    private IEnumerable<ImprovedGarrisonsTroopSelection> TroopsFromRoster(object[] arguments)
    {
        if (arguments.FirstOrDefault() is not IEnumerable roster) yield break;
        foreach (object element in roster)
        {
            object character = TryGetMemberValue(element, "Character");
            object rawCount = TryGetMemberValue(element, "Number");
            if (!TryGetStableId(character, out string id) || rawCount == null) continue;
            int count = Convert.ToInt32(rawCount, CultureInfo.InvariantCulture);
            if (count > 0) yield return new ImprovedGarrisonsTroopSelection(id, count);
        }
    }

    private bool TryGetStableId(object value, out string id)
    {
        id = null;
        if (value == null) return false;
        if (value is Town town) return objectManager.TryGetId(town, out id);
        if (value is MobileParty party) return objectManager.TryGetId(party, out id);
        if (objectManager.TryGetId(value, out id)) return true;
        id = TryGetMemberValue(value, "StringId") as string;
        return !string.IsNullOrEmpty(id);
    }

    private string GetTownTemplateName(Town town)
    {
        if (!TryGetTownSettings(town, out object settings)) return null;
        return GetTemplateName(TryGetMemberValue(settings, "Template"));
    }

    private static string GetTemplateName(object template) =>
        TryGetMemberValue(template, "Name") as string;

    private static object TryGetMemberValue(object instance, string name)
    {
        if (instance == null || string.IsNullOrEmpty(name)) return null;
        for (Type type = instance.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null) return property.GetValue(instance);
            var field = type.GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(instance);
        }
        return null;
    }

    public void NotifyDenied(string method)
    {
        if (!deniedNotifications.Add(method ?? "unknown")) return;
        var message = $"Improved Garrisons action '{method}' is disabled in co-op until its selection payload is server-authoritative.";
        Logger.Warning(message);
        if (ModInformation.IsClient) InformationManager.DisplayMessage(new InformationMessage(message));
    }

    public void OnAuthoritativeTickCompleted()
    {
        if (compatible && stateReady && ModInformation.IsServer) PublishStateIfChanged();
    }

    public bool TryGetTownSettings(Town town, out object settings)
    {
        settings = null;
        if (!compatible || town == null || !TryGetSettingsDictionary(out var dictionary)) return false;

        var key = town.Name?.ToString();
        if (string.IsNullOrEmpty(key)) return false;

        // A client may receive the canonical dictionary before its player ownership registry is
        // fully populated. Existing server-provided entries remain authoritative in that window.
        if ((ModInformation.IsClient && dictionary.Contains(key)) || playerManager.Contains(town.OwnerClan))
        {
            settings = ImprovedGarrisonsCanonicalState.GetOrCreateTownSettings(assembly, dictionary, town);
            return settings != null;
        }

        var npcType = assembly.GetType(
            "ImprovedGarrisons.SaveSystem.SaveData.DataTypes.NPCGarrisonSettings", false);
        settings = npcType == null ? null : Activator.CreateInstance(npcType);
        return settings != null;
    }

    public void ReconcileSettlements()
    {
        if (!compatible || !TryGetSettingsDictionary(out var dictionary)) return;

        var knownTownNames = new HashSet<string>(StringComparer.Ordinal);
        var controlledTownNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var settlement in Settlement.All.Where(item => item?.Town != null && (item.IsTown || item.IsCastle)))
        {
            var name = settlement.Town.Name?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            knownTownNames.Add(name);
            if (!playerManager.Contains(settlement.Town.OwnerClan)) continue;
            controlledTownNames.Add(name);
            ImprovedGarrisonsCanonicalState.GetOrCreateTownSettings(assembly, dictionary, settlement.Town);
        }

        // Do not erase a server save before the player registry has loaded. Once it has entries,
        // remove only keys that can be tied to a known, currently non-player-owned settlement.
        if (ModInformation.IsServer && playerManager.Players.Count > 0)
        {
            var staleKeys = dictionary.Keys.Cast<object>()
                .OfType<string>()
                .Where(key => knownTownNames.Contains(key) && !controlledTownNames.Contains(key))
                .ToArray();
            foreach (var key in staleKeys) dictionary.Remove(key);
            PublishStateIfChanged();
        }
    }

    public void OnSettlementOwnerChanged(Settlement settlement)
    {
        if (!compatible || !ModInformation.IsServer || settlement?.Town == null ||
            (!settlement.IsTown && !settlement.IsCastle) || !TryGetSettingsDictionary(out var dictionary))
            return;

        var key = settlement.Town.Name?.ToString();
        if (string.IsNullOrEmpty(key)) return;
        if (playerManager.Contains(settlement.Town.OwnerClan))
            ImprovedGarrisonsCanonicalState.GetOrCreateTownSettings(assembly, dictionary, settlement.Town);
        else
            dictionary.Remove(key);

        PublishStateIfChanged();
    }

    private bool TryInstall()
    {
        assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(
                candidate.GetName().Name,
                ImprovedGarrisonsCompatibilityManifest.AssemblyName,
                StringComparison.Ordinal));
        if (assembly == null)
        {
            Logger.Debug("Improved Garrisons is not loaded; compatibility adapter is inactive");
            return false;
        }

        int removedModulePatches;
        try
        {
            // Improved Garrisons can patch TaleWorlds methods from OnSubModuleLoad, before Coop's
            // container exists. Purge by exact implementing assembly even before fingerprint/API
            // validation, so an unsupported binary cannot retain its early detours.
            removedModulePatches = ImprovedGarrisonsHarmonyIsolation.RemoveModulePatches(
                assembly,
                harmony,
                ImprovedGarrisonsCompatibilityManifest.AdapterHarmonyId);
        }
        catch (Exception ex)
        {
            Logger.Fatal(ex, "Improved Garrisons pre-Coop Harmony isolation failed");
            throw new InvalidOperationException(
                "Improved Garrisons installed an unverified Harmony patch before Coop startup. The campaign was aborted.",
                ex);
        }

        if (!ImprovedGarrisonsCompatibilityManifest.TryValidate(assembly, out var methods, out var failure))
        {
            Logger.Fatal("Improved Garrisons co-op adapter failed closed: {Failure}", failure);
            throw new InvalidOperationException(
                "Unsupported Improved Garrisons binary/API. Coop startup was aborted so the unguarded mod cannot mutate the campaign: " +
                failure);
        }

        foreach (var pair in methods.Where(pair => pair.Key.Kind == ImprovedGarrisonsPatchKind.RoutedSetting))
            routedMethods[RoutedKey(pair.Key.TypeName, pair.Key.MethodName)] = (pair.Key, pair.Value);

        lock (PatchSync)
        {
            var tickPostfix = AccessTools.Method(
                typeof(ImprovedGarrisonsAuthorityPatches),
                nameof(ImprovedGarrisonsAuthorityPatches.ServerTickPostfix));
            var expected = methods.Select(pair => (
                Original: pair.Value,
                Prefix: PrefixMethodFor(pair.Key.Kind),
                Postfix: pair.Key.Kind == ImprovedGarrisonsPatchKind.ServerTick ? tickPostfix : null))
                .ToArray();

            if (ReferenceEquals(patchedAssembly, assembly) ||
                ImprovedGarrisonsHarmonyIsolation.HasOwnerPatchesTargetingAssembly(
                    assembly,
                    ImprovedGarrisonsCompatibilityManifest.AdapterHarmonyId))
            {
                ImprovedGarrisonsHarmonyIsolation.AssertOnlyAdapterGuards(
                    expected,
                    ImprovedGarrisonsCompatibilityManifest.AdapterHarmonyId);
                ImprovedGarrisonsHarmonyIsolation.AssertNoUnexpectedPatchTargets(
                    assembly,
                    expected.Select(guard => guard.Original),
                    ImprovedGarrisonsCompatibilityManifest.AdapterHarmonyId);
                patchedAssembly = assembly;
                return true;
            }

            var applied = new List<(MethodInfo Original, MethodInfo Prefix, MethodInfo Postfix)>();
            try
            {
                foreach (var guard in expected)
                {
                    harmony.Patch(
                        guard.Original,
                        prefix: new HarmonyMethod(guard.Prefix),
                        postfix: guard.Postfix == null ? null : new HarmonyMethod(guard.Postfix));
                    applied.Add(guard);
                }

                ImprovedGarrisonsHarmonyIsolation.AssertOnlyAdapterGuards(
                    expected,
                    ImprovedGarrisonsCompatibilityManifest.AdapterHarmonyId);
                ImprovedGarrisonsHarmonyIsolation.AssertNoUnexpectedPatchTargets(
                    assembly,
                    expected.Select(guard => guard.Original),
                    ImprovedGarrisonsCompatibilityManifest.AdapterHarmonyId);
                patchedAssembly = assembly;
            }
            catch (Exception ex)
            {
                foreach (var patch in applied)
                {
                    harmony.Unpatch(patch.Original, patch.Prefix);
                    if (patch.Postfix != null) harmony.Unpatch(patch.Original, patch.Postfix);
                }
                Logger.Fatal(ex, "Improved Garrisons co-op adapter patching failed; rolled back all adapter detours");
                throw new InvalidOperationException(
                    "Improved Garrisons co-op guard installation failed. Coop startup was aborted after rolling back partial detours.",
                    ex);
            }
        }

        Logger.Information(
            "Improved Garrisons {Version} co-op adapter enabled with {Count} guarded methods; removed {Removed} pre-Coop module patches",
            ImprovedGarrisonsCompatibilityManifest.SupportedModuleVersion,
            methods.Count,
            removedModulePatches);
        return true;
    }

    private static HarmonyMethod PrefixFor(ImprovedGarrisonsPatchKind kind)
        => new HarmonyMethod(PrefixMethodFor(kind));

    private static MethodInfo PrefixMethodFor(ImprovedGarrisonsPatchKind kind)
    {
        string name;
        switch (kind)
        {
            case ImprovedGarrisonsPatchKind.ServerTick:
            case ImprovedGarrisonsPatchKind.ServerTickNoPublication:
                name = nameof(ImprovedGarrisonsAuthorityPatches.ServerTickPrefix);
                break;
            case ImprovedGarrisonsPatchKind.ServerLifecycle:
                name = nameof(ImprovedGarrisonsAuthorityPatches.ServerOnlyPrefix);
                break;
            case ImprovedGarrisonsPatchKind.DeterministicInitialization:
                name = nameof(ImprovedGarrisonsAuthorityPatches.DeterministicInitializationPrefix);
                break;
            case ImprovedGarrisonsPatchKind.RoutedSetting:
                name = nameof(ImprovedGarrisonsAuthorityPatches.RoutedSettingPrefix);
                break;
            case ImprovedGarrisonsPatchKind.ClientPresentation:
                name = nameof(ImprovedGarrisonsAuthorityPatches.ClientPresentationPrefix);
                break;
            case ImprovedGarrisonsPatchKind.RoutedOperation:
                name = nameof(ImprovedGarrisonsAuthorityPatches.RoutedOperationPrefix);
                break;
            case ImprovedGarrisonsPatchKind.StablePartyIdentity:
                name = nameof(ImprovedGarrisonsAuthorityPatches.StablePartyIdentityPrefix);
                break;
            case ImprovedGarrisonsPatchKind.FinanceRead:
                name = nameof(ImprovedGarrisonsAuthorityPatches.FinanceReadPrefix);
                break;
            case ImprovedGarrisonsPatchKind.ClientDefaultConfig:
                name = nameof(ImprovedGarrisonsAuthorityPatches.ClientDefaultConfigPrefix);
                break;
            case ImprovedGarrisonsPatchKind.PlayerTownSettings:
                name = nameof(ImprovedGarrisonsAuthorityPatches.PlayerTownSettingsPrefix);
                break;
            case ImprovedGarrisonsPatchKind.PlayerSettlementInitialization:
                name = nameof(ImprovedGarrisonsAuthorityPatches.PlayerSettlementInitializationPrefix);
                break;
            case ImprovedGarrisonsPatchKind.PlayerSettlementOwnerChanged:
                name = nameof(ImprovedGarrisonsAuthorityPatches.PlayerSettlementOwnerChangedPrefix);
                break;
            case ImprovedGarrisonsPatchKind.PartyHomeResolver:
                name = nameof(ImprovedGarrisonsAuthorityPatches.PartyHomeResolverPrefix);
                break;
            case ImprovedGarrisonsPatchKind.VillagePartySpawnResolver:
                name = nameof(ImprovedGarrisonsAuthorityPatches.VillagePartySpawnResolverPrefix);
                break;
            case ImprovedGarrisonsPatchKind.VillagePartyLookup:
                name = nameof(ImprovedGarrisonsAuthorityPatches.VillagePartyLookupPrefix);
                break;
            default:
                name = nameof(ImprovedGarrisonsAuthorityPatches.ServerOnlyPrefix);
                break;
        }
        return AccessTools.Method(typeof(ImprovedGarrisonsAuthorityPatches), name);
    }

    private void Handle_AllGameObjectsRegistered(MessagePayload<AllGameObjectsRegistered> payload)
    {
        if (!compatible) return;
        ImprovedGarrisonsAuthorityPatches.ResetTickLedger();
        revision = 0;
        lastPublishedHash = null;
        lastAppliedHash = null;
        objectsRegistered = true;
        stateReady = !ModInformation.IsClient;
        SnapshotReadiness = ModInformation.IsClient ? WorkshopSnapshotReadiness.Unknown : WorkshopSnapshotReadiness.Ready;
        SnapshotSessionId = null;
        SnapshotRevision = -1;
        if (ModInformation.IsClient)
            StartSnapshotBootstrap();
        else
            PublishStateIfChanged();
    }

    private void HandleHostModConfigAccepted(MessagePayload<HostModConfigAccepted> payload)
    {
        if (payload?.What.Snapshot == null || !configAuthority.IsCurrent(payload.What.Snapshot)) return;
        if (ModInformation.IsClient && !string.Equals(SnapshotSessionId, payload.What.Snapshot.SessionId, StringComparison.Ordinal))
        {
            stateReady = false;
            SnapshotReadiness = WorkshopSnapshotReadiness.Unknown;
            SnapshotRevision = -1;
            revision = 0;
            lastAppliedHash = null;
        }
        StartSnapshotBootstrap();
    }

    private void Handle_State(MessagePayload<NetworkImprovedGarrisonsState> payload)
    {
        if (!compatible || !ModInformation.IsClient || payload.Who is not NetPeer serverPeer ||
            !ImprovedGarrisonsSnapshotOriginGuard.IsTrustedServerTransport(serverPeer, localIsClient: true))
            return;

        GameThread.RunSafe(() =>
        {
            if (TryApplyState(payload.What, out var rejection))
            {
                stateReady = true;
                SnapshotReadiness = WorkshopSnapshotReadiness.Ready;
                if (configAuthority.TryGetCurrent(out var config)) SnapshotSessionId = config.SessionId;
                SnapshotRevision = payload.What.Revision;
                TryOpenPendingPartyScreens();
                return;
            }

            stateReady = false;
            Logger.Fatal("Disconnecting from the Coop server because Improved Garrisons state could not be accepted: {Failure}",
                rejection);
            try
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "Improved Garrisons compatibility validation failed. The co-op connection was closed to prevent a divergent campaign. " +
                    rejection));
            }
            catch (Exception displayException)
            {
                Logger.Error(displayException, "Could not display the Improved Garrisons compatibility failure");
            }
            finally
            {
                serverPeer.Disconnect();
            }
        }, context: nameof(ImprovedGarrisonsCompatibilityHandler));
    }

    private void Handle_StateQueryResult(MessagePayload<NetworkImprovedGarrisonsStateQueryResult> payload)
    {
        if (!compatible || !ModInformation.IsClient || payload?.Who is not NetPeer serverPeer ||
            !configAuthority.IsTrustedServer(serverPeer) || payload.What.Header.Status != AuthorityResultStatus.Accepted ||
            payload.What.Snapshot == null)
            return;

        GameThread.RunSafe(() =>
        {
            if (TryApplyState(payload.What.Snapshot, out var failure))
            {
                stateReady = true;
                SnapshotReadiness = WorkshopSnapshotReadiness.Ready;
                if (configAuthority.TryGetCurrent(out var config)) SnapshotSessionId = config.SessionId;
                SnapshotRevision = payload.What.Snapshot.Revision;
                TryOpenPendingPartyScreens();
                return;
            }
            stateReady = false;
            Logger.Fatal("Disconnecting from the Coop server because Improved Garrisons state could not be accepted: {Failure}",
                failure);
            serverPeer.Disconnect();
        }, context: nameof(ImprovedGarrisonsCompatibilityHandler));
    }

    private void StartSnapshotBootstrap()
    {
        if (!compatible || !objectsRegistered || !ModInformation.IsClient || stateReady ||
            !configAuthority.TryGetCurrent(out _)) return;
        SnapshotReadiness = WorkshopSnapshotReadiness.Loading;
        snapshotRoute.Submit(default);
    }

    private AuthorityRequestHeader CreateSnapshotHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out var config)) return default;
        return new AuthorityRequestHeader(config.ProtocolVersion, config.SessionId, requestId, config.Revision);
    }

    private AuthorityHeaderValidation ValidateSnapshotHeader(AuthorityRequestHeader header)
    {
        if (!compatible || !stateReady || !configAuthority.TryGetCurrent(out var config))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable, "improved-garrisons-snapshot-unavailable");
        if (header.ProtocolVersion != config.ProtocolVersion || !string.Equals(header.SessionId, config.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-config-session");
        return header.ExpectedRevision == config.Revision
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-config-revision");
    }

    private AuthorityServerReply<NetworkImprovedGarrisonsStateQueryResult> ExecuteSnapshotQuery(
        AuthorityServerContext context, NetworkRequestImprovedGarrisonsState _)
    {
        if (!TryCaptureState(out var snapshot, out var failure))
        {
            Logger.Warning("Improved Garrisons snapshot query is unavailable: {Failure}", failure);
            return new AuthorityServerReply<NetworkImprovedGarrisonsStateQueryResult>(
                CreateSnapshotTerminal(context.Header, AuthorityResultStatus.Unavailable,
                    "improved-garrisons-snapshot-unavailable"), false);
        }
        return new AuthorityServerReply<NetworkImprovedGarrisonsStateQueryResult>(
            new NetworkImprovedGarrisonsStateQueryResult(context.Header, AuthorityResultStatus.Accepted, snapshot, null), true);
    }

    private static NetworkImprovedGarrisonsStateQueryResult CreateSnapshotTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reasonCode) =>
        new NetworkImprovedGarrisonsStateQueryResult(header, status, null, reasonCode);

    private AuthorityCommitProbeResult ProbeSnapshotApplied(NetworkImprovedGarrisonsStateQueryResult result) =>
        stateReady && result.Snapshot != null && revision == result.Header.CommittedRevision
            ? AuthorityCommitProbeResult.Applied
            : AuthorityCommitProbeResult.Pending;

    private AuthorityRequestHeader CreateManagementHeader(long requestId)
    {
        if (!configAuthority.TryGetCurrent(out var config)) return default;
        return new AuthorityRequestHeader(config.ProtocolVersion, config.SessionId, requestId, revision);
    }

    private AuthorityHeaderValidation ValidateManagementHeader(AuthorityRequestHeader header)
    {
        if (!CanUseManagementRoute(out var config))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.Unavailable,
                "improved-garrisons-route-unavailable");
        if (header.ProtocolVersion != config.ProtocolVersion ||
            !string.Equals(header.SessionId, config.SessionId, StringComparison.Ordinal))
            return AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleSession, "stale-config-session");
        return header.ExpectedRevision == revision
            ? AuthorityHeaderValidation.Valid
            : AuthorityHeaderValidation.Reject(AuthorityResultStatus.StaleState, "stale-canonical-revision");
    }

    private static NetworkRequestImprovedGarrisonsOperation BuildManagementRequest(
        ImprovedGarrisonsManagementIntent intent, AuthorityRequestHeader header)
    {
        NetworkRequestImprovedGarrisonsOperation request = intent?.Request;
        return request == null
            ? new NetworkRequestImprovedGarrisonsOperation(header, (ImprovedGarrisonsOperation)0, string.Empty,
                Array.Empty<string>(), string.Empty, Array.Empty<ImprovedGarrisonsTroopSelection>())
            : new NetworkRequestImprovedGarrisonsOperation(header, request.Operation, request.TownId,
                request.TargetIds, request.Value, request.Troops);
    }

    private AuthorityServerReply<NetworkImprovedGarrisonsSettingResult> ExecuteSettingRoute(
        AuthorityServerContext context, NetworkRequestImprovedGarrisonsSettingChange request)
    {
        if (!TryResolveOwnedTown(context.Peer, request.TownId, out var town) ||
            !routedMethods.TryGetValue(RoutedKey(request.ManagerType, request.Method), out var route))
            return SettingReply(context.Header, request, AuthorityResultStatus.Unauthorized, "town-or-setting-not-authorized", false);

        MethodInfo method = route.Method;
        ParameterInfo[] parameters = method.GetParameters();
        object[] arguments = new object[parameters.Length];
        arguments[0] = town;
        if (parameters.Length == 2)
        {
            if (!ImprovedGarrisonsCanonicalState.TryParseValue(request.Value, parameters[1].ParameterType, out var value) ||
                !IsValueAllowed(method.Name, value))
                return SettingReply(context.Header, request, AuthorityResultStatus.InvalidRequest, "invalid-setting-value", false);
            arguments[1] = value;
        }
        object manager = ResolveManager(method.DeclaringType);
        if (manager == null) return SettingReply(context.Header, request, AuthorityResultStatus.Unavailable, "setting-manager-unavailable", false);
        if (!ImprovedGarrisonsCanonicalState.TryBuild(assembly, objectManager, out var rollback, out var rollbackHash, out var captureFailure))
            return SettingReply(context.Header, request, AuthorityResultStatus.Unavailable, "rollback-capture-failed", false);

        try
        {
            method.Invoke(manager, arguments);
            if (!TryCaptureState(out var post, out var postFailure) ||
                !SettingPostStateMatches(request, post))
                throw new InvalidOperationException("setting postcondition failed: " + postFailure);
            if (!PublishStateIfChanged())
                return SettingReply(context.Header, request, AuthorityResultStatus.ExecutionFailed, "snapshot-publication-failed", false);
            return SettingReply(context.Header, request, AuthorityResultStatus.Accepted, null, true);
        }
        catch (Exception exception)
        {
            if (!TryRestoreCanonicalState(rollback, rollbackHash, out var rollbackFailure))
            {
                DisconnectAllCampaignPeers("setting mutation ambiguity: " + rollbackFailure);
                return new AuthorityServerReply<NetworkImprovedGarrisonsSettingResult>(
                    CreateSettingTerminal(context.Header, AuthorityResultStatus.ExecutionFailed, "ambiguous-setting-mutation"), false, true);
            }
            Logger.Error(exception, "Improved Garrisons setting failed. Route={Route} Request={Request}",
                context.RouteId, context.Header.RequestId);
            return SettingReply(context.Header, request, AuthorityResultStatus.ExecutionFailed, "setting-execution-failed", false);
        }
    }

    private AuthorityServerReply<NetworkImprovedGarrisonsOperationResult> ExecuteManagementRoute(
        AuthorityServerContext context, NetworkRequestImprovedGarrisonsOperation request)
    {
        if (request.Operation == ImprovedGarrisonsOperation.StartHostileEncounter)
            return ExecuteHostileEncounterRoute(context, request);
        if (!TryResolveOwnedTown(context.Peer, request.TownId, out var town))
            return ManagementReply(context.Header, request, AuthorityResultStatus.Unauthorized, "town-not-authorized", false);

        Clan clan = town.OwnerClan;
        string[] ownedTownIds = Settlement.All.Where(settlement => settlement?.Town?.OwnerClan == clan &&
                (settlement.IsTown || settlement.IsCastle)).Select(settlement =>
                objectManager.TryGetId(settlement.Town, out string id) ? id : null).Where(id => id != null).ToArray();
        if (!ImprovedGarrisonsCanonicalState.TryBuild(assembly, objectManager, out var rollback, out var rollbackHash,
                out var captureFailure))
            return ManagementReply(context.Header, request, AuthorityResultStatus.Unavailable, "rollback-capture-failed", false);

        bool canonical = IsCanonicalOperation(request.Operation);
        ImprovedGarrisonsStateValue[] transformed = rollback;
        if (canonical && !ImprovedGarrisonsCanonicalOperations.TryTransform(rollback, request, ownedTownIds,
                out transformed, out var transformFailure))
            return ManagementReply(context.Header, request, AuthorityResultStatus.InvalidRequest, "canonical-transform-rejected", false);
        if (!ValidateNativeOperation(context.Peer, request, town, clan, ownedTownIds, out var nativeFailure))
            return ManagementReply(context.Header, request, AuthorityResultStatus.Unavailable, nativeFailure, false);

        bool nativeStarted = false;
        string partyId = string.Empty;
        try
        {
            if (canonical && !ImprovedGarrisonsCanonicalState.TryApply(assembly, objectManager, transformed, out var applyFailure))
                throw new InvalidOperationException("canonical apply failed: " + applyFailure);
            nativeStarted = IsNativeOperation(request.Operation);
            if (nativeStarted && !TryExecuteNativeOperation(context.Peer, request, town, out partyId, out var executionFailure))
                throw new InvalidOperationException("native execution failed: " + executionFailure);
            if (!TryCaptureState(out var post, out var postFailure))
                throw new InvalidOperationException(postFailure ?? "native poststate capture failed");
            if (!ManagementPostStateMatches(context.Peer, request, town, partyId, post, out var proofFailure))
                throw new InvalidOperationException(proofFailure ?? "unverifiable-native-poststate");
            if (!PublishStateIfChanged())
                throw new InvalidOperationException("snapshot publication failed");
            return ManagementReply(context.Header, request, AuthorityResultStatus.Accepted, null, true, partyId);
        }
        catch (Exception exception)
        {
            if (!nativeStarted && TryRestoreCanonicalState(rollback, rollbackHash, out _))
                return ManagementReply(context.Header, request, AuthorityResultStatus.ExecutionFailed,
                    "canonical-execution-failed", false);
            DisconnectAllCampaignPeers("management mutation ambiguity after native execution: " + exception.Message);
            Logger.Fatal(exception, "Ambiguous Improved Garrisons mutation. Route={Route} Request={Request}",
                context.RouteId, context.Header.RequestId);
            return new AuthorityServerReply<NetworkImprovedGarrisonsOperationResult>(
                CreateManagementTerminal(context.Header, AuthorityResultStatus.ExecutionFailed, "ambiguous-native-mutation"), false, true);
        }
    }

    private AuthorityServerReply<NetworkImprovedGarrisonsOperationResult> ExecuteHostileEncounterRoute(
        AuthorityServerContext context, NetworkRequestImprovedGarrisonsOperation request)
    {
        if (request.TargetIds.Length != 1 || request.TownId != request.TargetIds[0] ||
            !objectManager.TryGetObject(context.Player.MobilePartyId, out MobileParty actorParty) ||
            !objectManager.TryGetObject(request.TargetIds[0], out MobileParty targetParty) || actorParty == null ||
            targetParty == null || !targetParty.IsActive || ReferenceEquals(actorParty, targetParty) ||
            actorParty.MapFaction == null || targetParty.MapFaction == null ||
            FactionManager.IsAtWarAgainstFaction(actorParty.MapFaction, targetParty.MapFaction) ||
            actorParty.Position.ToVec2().DistanceSquared(targetParty.Position.ToVec2()) > 4f ||
            !TryGetMobileGarrison(targetParty, out var targetGarrison) ||
            TryGetMemberValue(targetGarrison, "isNPC") is not bool isNpc || !isNpc)
            return ManagementReply(context.Header, request, AuthorityResultStatus.Unauthorized,
                "hostile-encounter-not-authorized", false);
        try
        {
            // BeHostileAction is the existing campaign MapEvent publisher/coordinator entrypoint.
            // This route deliberately does not introduce a second request or participant protocol.
            BeHostileAction.ApplyEncounterHostileAction(actorParty.Party, targetParty.Party);
            var mapEvent = actorParty.MapEvent;
            if (mapEvent == null || targetParty.MapEvent != mapEvent ||
                !objectManager.TryGetId(mapEvent, out var mapEventId))
                throw new InvalidOperationException("hostile encounter did not publish a shared registered map event");
            return ManagementReply(context.Header, request, AuthorityResultStatus.Accepted, null, true, mapEventId);
        }
        catch (Exception exception)
        {
            DisconnectAllCampaignPeers("hostile encounter publication ambiguity: " + exception.Message);
            return new AuthorityServerReply<NetworkImprovedGarrisonsOperationResult>(
                CreateManagementTerminal(context.Header, AuthorityResultStatus.ExecutionFailed,
                    "ambiguous-hostile-encounter"), false, true);
        }
    }

    private AuthorityServerReply<NetworkImprovedGarrisonsSettingResult> SettingReply(
        AuthorityRequestHeader header, NetworkRequestImprovedGarrisonsSettingChange request,
        AuthorityResultStatus status, string reason, bool published)
    {
        string hash = status == AuthorityResultStatus.Accepted ? lastPublishedHash : string.Empty;
        long committed = status == AuthorityResultStatus.Accepted ? revision : header.ExpectedRevision;
        return new AuthorityServerReply<NetworkImprovedGarrisonsSettingResult>(
            new NetworkImprovedGarrisonsSettingResult(header, status, reason, SettingCommandKey(request), hash,
                request.ManagerType, request.Method, request.TownId, request.Value, committed), published);
    }

    private AuthorityServerReply<NetworkImprovedGarrisonsOperationResult> ManagementReply(
        AuthorityRequestHeader header, NetworkRequestImprovedGarrisonsOperation request,
        AuthorityResultStatus status, string reason, bool published, string partyId = "")
    {
        var legacy = status switch
        {
            AuthorityResultStatus.Accepted => ImprovedGarrisonsOperationStatus.Accepted,
            AuthorityResultStatus.StaleSession => ImprovedGarrisonsOperationStatus.StaleSession,
            AuthorityResultStatus.StaleState => ImprovedGarrisonsOperationStatus.StaleState,
            AuthorityResultStatus.ExecutionFailed => ImprovedGarrisonsOperationStatus.Failed,
            _ => ImprovedGarrisonsOperationStatus.Rejected,
        };
        string hash = status == AuthorityResultStatus.Accepted ? lastPublishedHash : string.Empty;
        long committed = status == AuthorityResultStatus.Accepted ? revision : header.ExpectedRevision;
        return new AuthorityServerReply<NetworkImprovedGarrisonsOperationResult>(
            new NetworkImprovedGarrisonsOperationResult(header, request.Operation, legacy, status, reason,
                ImprovedGarrisonsOperationProtocol.CommandKey(request), request.TownId, partyId, committed, hash,
                ManagementSemanticTuple(request, partyId)), published);
    }

    private static NetworkImprovedGarrisonsSettingResult CreateSettingTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new NetworkImprovedGarrisonsSettingResult(header, status, reason, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty);

    private static NetworkImprovedGarrisonsOperationResult CreateManagementTerminal(
        AuthorityRequestHeader header, AuthorityResultStatus status, string reason) =>
        new NetworkImprovedGarrisonsOperationResult(header, ImprovedGarrisonsOperation.CopySettings,
            status == AuthorityResultStatus.StaleState ? ImprovedGarrisonsOperationStatus.StaleState :
            status == AuthorityResultStatus.StaleSession ? ImprovedGarrisonsOperationStatus.StaleSession :
            ImprovedGarrisonsOperationStatus.Rejected, status, reason, string.Empty, string.Empty, string.Empty,
            header.ExpectedRevision, string.Empty, string.Empty);

    private bool IsExpectedSettingResult(NetworkRequestImprovedGarrisonsSettingChange request,
        NetworkImprovedGarrisonsSettingResult result) =>
        result.Header.RequestId == request.Header.RequestId && result.Header.SessionId == request.Header.SessionId &&
        result.CommandDigest == SettingCommandKey(request) && result.ManagerType == request.ManagerType &&
        result.Method == request.Method && result.TownId == request.TownId && result.Value == request.Value;

    private bool IsExpectedManagementResult(NetworkRequestImprovedGarrisonsOperation request,
        NetworkImprovedGarrisonsOperationResult result) =>
        ImprovedGarrisonsOperationProtocol.IsResultShapeValid(result) &&
        result.Header.RequestId == request.Header.RequestId && result.Header.SessionId == request.Header.SessionId &&
        result.Operation == request.Operation && result.TownId == request.TownId &&
        result.CommandDigest == ImprovedGarrisonsOperationProtocol.CommandKey(request);

    private AuthorityCommitProbeResult ProbeSettingApplied(NetworkImprovedGarrisonsSettingResult result) =>
        result.Header.Status == AuthorityResultStatus.Accepted && stateReady && SnapshotReadiness == WorkshopSnapshotReadiness.Ready &&
        SnapshotRevision == result.CommittedRevision && string.Equals(lastAppliedHash, result.CanonicalHash,
            StringComparison.OrdinalIgnoreCase) ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;

    private AuthorityCommitProbeResult ProbeManagementApplied(NetworkImprovedGarrisonsOperationResult result)
    {
        if (result.Header.Status != AuthorityResultStatus.Accepted || !stateReady ||
            SnapshotReadiness != WorkshopSnapshotReadiness.Ready || SnapshotRevision != result.CommittedRevision ||
            !string.Equals(lastAppliedHash, result.CanonicalHash, StringComparison.OrdinalIgnoreCase))
            return AuthorityCommitProbeResult.Pending;
        return ManagementPostStateMatches(null, new NetworkRequestImprovedGarrisonsOperation(
            result.SessionId, result.RequestId, result.CommittedRevision, result.Operation, result.TownId,
            Array.Empty<string>(), string.Empty, Array.Empty<ImprovedGarrisonsTroopSelection>()), null,
            result.PartyId, null, out _) ? AuthorityCommitProbeResult.Applied : AuthorityCommitProbeResult.Pending;
    }

    private void PresentSettingOutcome(AuthorityClientOutcome<NetworkImprovedGarrisonsSettingResult> outcome)
    {
        if (outcome.Applied) InformationManager.DisplayMessage(new InformationMessage(
            "Improved Garrisons setting accepted by the co-op server."));
    }

    private void PresentManagementOutcome(AuthorityClientOutcome<NetworkImprovedGarrisonsOperationResult> outcome)
    {
        if (!outcome.Applied) return;
        var result = outcome.Result;
        if (!string.IsNullOrEmpty(result.PartyId))
        {
            pendingPartyScreens[result.PartyId] = result.TownId;
            TryOpenPendingPartyScreens();
        }
        InformationManager.DisplayMessage(new InformationMessage("Improved Garrisons management action accepted."));
    }

    private bool SettingPostStateMatches(NetworkRequestImprovedGarrisonsSettingChange request,
        NetworkImprovedGarrisonsState snapshot) => snapshot?.Values.Any(value => value.Scope == "town" &&
            value.TargetId == request.TownId && value.Property != null) == true;

    private bool ManagementPostStateMatches(NetPeer peer, NetworkRequestImprovedGarrisonsOperation request,
        Town town, string partyId, NetworkImprovedGarrisonsState snapshot, out string failure)
    {
        failure = null;
        // The route has already bound the client reply to the exact request digest and the
        // canonical snapshot hash/revision.  Client replicas cannot re-derive server-only actor
        // ownership, so their commit barrier is that authenticated snapshot rather than a local
        // reflection echo.
        if (peer == null) return true;
        if (IsCanonicalOperation(request.Operation))
            return snapshot != null && string.Equals(snapshot.CanonicalHash, lastPublishedHash ?? snapshot.CanonicalHash,
                StringComparison.OrdinalIgnoreCase);
        if (request.Operation == ImprovedGarrisonsOperation.BoostBuildingReserve)
        {
            if (!playerManager.TryGetPlayer(peer, out var player) || !objectManager.TryGetObject(player.HeroId, out Hero actor) ||
                !int.TryParse(request.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int boost) ||
                town.BoostBuildingProcess < boost || actor.Gold < 0)
            { failure = "building reserve or actor gold postcondition failed"; return false; }
            return true;
        }
        if (request.Operation == ImprovedGarrisonsOperation.CreateTransferParty ||
            request.Operation == ImprovedGarrisonsOperation.CreateRecruiter ||
            request.Operation == ImprovedGarrisonsOperation.CreateMobileGarrison)
        {
            if (string.IsNullOrEmpty(partyId) || !objectManager.TryGetObject(partyId, out MobileParty party) || party == null)
            { failure = "created party was not registered"; return false; }
            object partyManagement = GetStaticMember(assembly?.GetType("ImprovedGarrisons.Main", false), "PartyManagement");
            object manager = request.Operation == ImprovedGarrisonsOperation.CreateTransferParty
                ? TryGetMemberValue(partyManagement, "transferPartyManagement")
                : request.Operation == ImprovedGarrisonsOperation.CreateRecruiter
                    ? TryGetMemberValue(partyManagement, "garrisonRecruiterPartyManagement")
                    : TryGetMemberValue(partyManagement, "mobileGarrisonManagement");
            object mapped = request.Operation == ImprovedGarrisonsOperation.CreateRecruiter
                ? InvokeOptional(manager, "GetRecruiterOfSettlement", town.Settlement)
                : request.Operation == ImprovedGarrisonsOperation.CreateMobileGarrison
                    ? InvokeOptional(manager, "GetMobileGarrisonPartyOfSettlement", town.Settlement)
                    : null;
            if (request.Operation != ImprovedGarrisonsOperation.CreateTransferParty && mapped == null)
            { failure = "created party has no manager mapping"; return false; }
            if (party.HomeSettlement != town.Settlement && request.Operation != ImprovedGarrisonsOperation.CreateTransferParty)
            { failure = "created party home town does not match source"; return false; }
            return true;
        }
        if (request.Operation == ImprovedGarrisonsOperation.SetMobileGarrisonEscort ||
            request.Operation == ImprovedGarrisonsOperation.OrderMobileGarrisonPatrol ||
            request.Operation == ImprovedGarrisonsOperation.OrderMobileGarrisonReturn ||
            request.Operation == ImprovedGarrisonsOperation.FortifyMobileGarrison ||
            request.Operation == ImprovedGarrisonsOperation.ReturnRecruiter)
        {
            string observed = ReadNativeOrderProof(request.Operation, town);
            string expected = request.Operation == ImprovedGarrisonsOperation.SetMobileGarrisonEscort ||
                              request.Operation == ImprovedGarrisonsOperation.FortifyMobileGarrison
                ? request.TargetIds[0] : town.StringId;
            if (!string.IsNullOrEmpty(observed) && observed.IndexOf(expected, StringComparison.Ordinal) >= 0)
                return true;
            failure = "native order/mode postcondition did not expose exact target";
            return false;
        }
        failure = "native postcondition is unavailable for " + request.Operation;
        return false;
    }

    private string ReadNativeOrderProof(ImprovedGarrisonsOperation operation, Town town)
    {
        object partyManagement = GetStaticMember(assembly?.GetType("ImprovedGarrisons.Main", false), "PartyManagement");
        object manager = operation == ImprovedGarrisonsOperation.ReturnRecruiter
            ? TryGetMemberValue(partyManagement, "garrisonRecruiterPartyManagement")
            : TryGetMemberValue(partyManagement, "mobileGarrisonManagement");
        object wrapper = operation == ImprovedGarrisonsOperation.ReturnRecruiter
            ? InvokeOptional(manager, "GetRecruiterOfSettlement", town.Settlement)
            : InvokeOptional(manager, "GetMobileGarrisonPartyOfSettlement", town.Settlement);
        if (wrapper == null) return null;
        object proof = TryGetMemberValue(wrapper, "CurrentOrder") ?? TryGetMemberValue(wrapper, "Order") ??
            TryGetMemberValue(wrapper, "Mode") ?? TryGetMemberValue(wrapper, "ReturnMode") ??
            TryGetMemberValue(wrapper, "FortifySettlement");
        return proof?.ToString();
    }

    private static string ManagementSemanticTuple(NetworkRequestImprovedGarrisonsOperation request, string partyId) =>
        string.Join("|", ((int)request.Operation).ToString(CultureInfo.InvariantCulture), request.TownId,
            string.Join(",", request.TargetIds), request.Value, partyId ?? string.Empty);

    private void DisconnectAllCampaignPeers(string failure)
    {
        Logger.Fatal("Disconnecting campaign peers after Improved Garrisons authority ambiguity: {Failure}", failure);
        foreach (var player in playerManager.Players)
            if (playerManager.TryGetPeer(player.ControllerId, out var peer)) peer.Disconnect();
    }

    private void PresentSnapshotTerminal(AuthorityClientOutcome<NetworkImprovedGarrisonsStateQueryResult> outcome)
    {
        if (outcome.Completion == AuthorityClientCompletion.Applied) return;
        stateReady = false;
        SnapshotReadiness = WorkshopSnapshotReadiness.Unavailable;
        Logger.Warning("Improved Garrisons snapshot bootstrap ended without readiness. Completion={Completion} Reason={Reason}",
            outcome.Completion, outcome.ReasonCode);
    }

    private static bool IsCanonicalOperation(ImprovedGarrisonsOperation operation) =>
        operation == ImprovedGarrisonsOperation.CopySettings ||
        operation == ImprovedGarrisonsOperation.CreateRecruiter ||
        operation == ImprovedGarrisonsOperation.ChangeRecruitmentCulture ||
        operation == ImprovedGarrisonsOperation.ReturnRecruiter ||
        operation == ImprovedGarrisonsOperation.ReplaceTownTemplate ||
        operation == ImprovedGarrisonsOperation.RemoveTownTemplateTroop ||
        operation == ImprovedGarrisonsOperation.SetUpgradePaths ||
        operation == ImprovedGarrisonsOperation.ApplyGlobalTemplate ||
        operation == ImprovedGarrisonsOperation.CreateGlobalTemplate ||
        operation == ImprovedGarrisonsOperation.RenameGlobalTemplate ||
        operation == ImprovedGarrisonsOperation.RemoveGlobalTemplate;

    private static bool IsNativeOperation(ImprovedGarrisonsOperation operation) =>
        operation == ImprovedGarrisonsOperation.CreateTransferParty ||
        operation == ImprovedGarrisonsOperation.CreateRecruiter ||
        operation == ImprovedGarrisonsOperation.ChangeRecruitmentCulture ||
        operation == ImprovedGarrisonsOperation.ReturnRecruiter ||
        operation == ImprovedGarrisonsOperation.CreateMobileGarrison ||
        operation == ImprovedGarrisonsOperation.SetMobileGarrisonEscort ||
        operation == ImprovedGarrisonsOperation.OrderMobileGarrisonPatrol ||
        operation == ImprovedGarrisonsOperation.OrderMobileGarrisonReturn ||
        operation == ImprovedGarrisonsOperation.FortifyMobileGarrison ||
        operation == ImprovedGarrisonsOperation.BoostBuildingReserve;

    private bool ValidateNativeOperation(
        NetPeer peer,
        NetworkRequestImprovedGarrisonsOperation request,
        Town town,
        Clan clan,
        IReadOnlyCollection<string> ownedTownIds,
        out string failure)
    {
        failure = null;
        if (!IsNativeOperation(request.Operation)) return true;
        if (request.Operation == ImprovedGarrisonsOperation.BoostBuildingReserve)
        {
            if (!playerManager.TryGetPlayer(peer, out var player) ||
                !objectManager.TryGetObject(player.HeroId, out Hero actor) ||
                actor?.Clan != clan ||
                !int.TryParse(request.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int increase) ||
                !ImprovedGarrisonsBuildingAuthority.TryPlan(
                    town.BoostBuildingProcess,
                    actor.Gold,
                    increase,
                    out _,
                    out _))
            {
                failure = "the building-reserve increase is unaffordable, stale, or invalid";
                return false;
            }
            return true;
        }
        if ((request.Operation == ImprovedGarrisonsOperation.CreateTransferParty ||
             request.Operation == ImprovedGarrisonsOperation.CreateRecruiter ||
             request.Operation == ImprovedGarrisonsOperation.CreateMobileGarrison) &&
            town.IsUnderSiege)
        {
            failure = "the source town is under siege";
            return false;
        }
        if ((request.Operation == ImprovedGarrisonsOperation.CreateTransferParty ||
             request.Operation == ImprovedGarrisonsOperation.CreateRecruiter ||
             request.Operation == ImprovedGarrisonsOperation.CreateMobileGarrison) &&
            (town.GarrisonParty == null || town.GarrisonParty.MemberRoster.TotalManCount < 1))
        {
            failure = "the source garrison is empty";
            return false;
        }
        if (request.Operation == ImprovedGarrisonsOperation.CreateTransferParty &&
            !ownedTownIds.Contains(request.TargetIds[0]))
        {
            failure = "the transfer target is not owned by the requesting clan";
            return false;
        }
        if (request.Operation == ImprovedGarrisonsOperation.SetMobileGarrisonEscort)
        {
            if (!objectManager.TryGetObject(request.TargetIds[0], out MobileParty target) ||
                target == null || !target.IsActive || target.MapFaction == null || clan?.MapFaction == null ||
                FactionManager.IsAtWarAgainstFaction(clan.MapFaction, target.MapFaction))
            {
                failure = "the escort target is missing, inactive, or hostile";
                return false;
            }
        }
        if (request.Operation == ImprovedGarrisonsOperation.FortifyMobileGarrison)
        {
            if (!objectManager.TryGetObject(request.TargetIds[0], out Settlement target) || target?.Town == null ||
                target.MapFaction == null || clan?.MapFaction == null ||
                FactionManager.IsAtWarAgainstFaction(clan.MapFaction, target.MapFaction))
            {
                failure = "the fortification target is missing or hostile";
                return false;
            }
        }
        if (request.Operation == ImprovedGarrisonsOperation.SetMobileGarrisonEscort ||
            request.Operation == ImprovedGarrisonsOperation.OrderMobileGarrisonPatrol ||
            request.Operation == ImprovedGarrisonsOperation.OrderMobileGarrisonReturn ||
            request.Operation == ImprovedGarrisonsOperation.FortifyMobileGarrison ||
            request.Operation == ImprovedGarrisonsOperation.ReturnRecruiter)
        {
            if (!HasStableNativePostState(request.Operation, town))
            {
                failure = "native-poststate-unavailable-" + request.Operation;
                return false;
            }
        }
        return true;
    }

    private bool HasStableNativePostState(ImprovedGarrisonsOperation operation, Town town)
    {
        object partyManagement = GetStaticMember(assembly?.GetType("ImprovedGarrisons.Main", false), "PartyManagement");
        object manager = operation == ImprovedGarrisonsOperation.ReturnRecruiter
            ? TryGetMemberValue(partyManagement, "garrisonRecruiterPartyManagement")
            : TryGetMemberValue(partyManagement, "mobileGarrisonManagement");
        object wrapper = operation == ImprovedGarrisonsOperation.ReturnRecruiter
            ? InvokeOptional(manager, "GetRecruiterOfSettlement", town.Settlement)
            : InvokeOptional(manager, "GetMobileGarrisonPartyOfSettlement", town.Settlement);
        if (wrapper == null) return false;
        Type type = wrapper.GetType();
        return new[] { "CurrentOrder", "Order", "Mode", "IsReturning", "ReturnMode", "FortifySettlement" }
            .Any(name => type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null ||
                         type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null ||
                         type.GetMethod("get" + name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null);
    }

    private bool TryGetMobileGarrison(MobileParty party, out object mobileGarrison)
    {
        mobileGarrison = null;
        object partyManagement = GetStaticMember(assembly?.GetType("ImprovedGarrisons.Main", false), "PartyManagement");
        object manager = TryGetMemberValue(partyManagement, "mobileGarrisonManagement");
        mobileGarrison = InvokeOptional(manager, "GetMobileGarrisonForParty", party);
        return mobileGarrison != null;
    }

    private bool TryExecuteNativeOperation(
        NetPeer peer,
        NetworkRequestImprovedGarrisonsOperation request,
        Town town,
        out string partyId,
        out string failure)
    {
        partyId = string.Empty;
        failure = null;
        try
        {
            object partyManagement = GetStaticMember(assembly.GetType("ImprovedGarrisons.Main", false), "PartyManagement");
            if (partyManagement == null)
            {
                failure = "Improved Garrisons party management is unavailable";
                return false;
            }

            object manager;
            object result;
            switch (request.Operation)
            {
                case ImprovedGarrisonsOperation.BoostBuildingReserve:
                    return TryBoostBuildingReserve(peer, request, town, out failure);
                case ImprovedGarrisonsOperation.CreateTransferParty:
                    if (!objectManager.TryGetObject(request.TargetIds[0], out Town targetTown))
                    {
                        failure = "transfer target is not registered";
                        return false;
                    }
                    manager = TryGetMemberValue(partyManagement, "transferPartyManagement");
                    result = InvokeRequired(manager, "CreateNewTransferParty", town.Settlement, targetTown.Settlement);
                    if (result == null) failure = "Improved Garrisons refused to create the transfer party";
                    if (result != null) TryGetStableId(result, out partyId);
                    return result != null;
                case ImprovedGarrisonsOperation.CreateRecruiter:
                    manager = TryGetMemberValue(partyManagement, "garrisonRecruiterPartyManagement");
                    result = InvokeRequired(manager, "CreateGarrisonRecruiterParty", town.Settlement, town.Settlement);
                    if (result == null) failure = "Improved Garrisons refused to create the recruiter party";
                    if (result != null) TryGetStableId(result, out partyId);
                    return result != null;
                case ImprovedGarrisonsOperation.ChangeRecruitmentCulture:
                    manager = TryGetMemberValue(partyManagement, "garrisonRecruiterPartyManagement");
                    object recruiter = InvokeRequired(manager, "GetRecruiterOfSettlement", town.Settlement);
                    if (recruiter != null) InvokeRequired(recruiter, "ResetTradeTarget");
                    return true;
                case ImprovedGarrisonsOperation.CreateMobileGarrison:
                    manager = TryGetMemberValue(partyManagement, "mobileGarrisonManagement");
                    result = InvokeRequired(manager, "CreateMobileGarrison", town.Settlement, town.Settlement);
                    if (result == null) failure = "Improved Garrisons refused to create the mobile garrison";
                    if (result != null) TryGetStableId(result, out partyId);
                    return result != null;
                case ImprovedGarrisonsOperation.SetMobileGarrisonEscort:
                    manager = TryGetMemberValue(partyManagement, "mobileGarrisonManagement");
                    object mobileGarrison = InvokeRequired(manager, "GetMobileGarrisonPartyOfSettlement", town.Settlement);
                    if (!objectManager.TryGetObject(request.TargetIds[0], out MobileParty escortTarget) || mobileGarrison == null)
                    {
                        failure = "mobile garrison or escort target is unavailable";
                        return false;
                    }
                    Type orderType = assembly.GetTypes().SingleOrDefault(type => type.Name == "OrderEscort");
                    object order = orderType?.GetConstructor(new[] { typeof(MobileParty) })?.Invoke(new object[] { escortTarget });
                    if (order == null)
                    {
                        failure = "Improved Garrisons escort order is unavailable";
                        return false;
                    }
                    InvokeRequired(mobileGarrison, "GiveAndExecuteOrder", order);
                    return true;
                case ImprovedGarrisonsOperation.OrderMobileGarrisonPatrol:
                    manager = TryGetMemberValue(partyManagement, "mobileGarrisonManagement");
                    mobileGarrison = InvokeRequired(manager, "GetMobileGarrisonPartyOfSettlement", town.Settlement);
                    if (mobileGarrison == null)
                    {
                        failure = "mobile garrison is unavailable";
                        return false;
                    }
                    orderType = assembly.GetTypes().SingleOrDefault(type => type.Name == "OrderPatrol");
                    order = orderType?.GetConstructor(new[] { typeof(Settlement) })
                        ?.Invoke(new object[] { town.Settlement });
                    if (order == null)
                    {
                        failure = "Improved Garrisons patrol order is unavailable";
                        return false;
                    }
                    InvokeRequired(mobileGarrison, "GiveAndExecuteOrder", order);
                    return true;
                case ImprovedGarrisonsOperation.OrderMobileGarrisonReturn:
                    manager = TryGetMemberValue(partyManagement, "mobileGarrisonManagement");
                    mobileGarrison = InvokeRequired(manager, "GetMobileGarrisonPartyOfSettlement", town.Settlement);
                    if (mobileGarrison == null)
                    {
                        failure = "mobile garrison is unavailable";
                        return false;
                    }
                    InvokeRequired(mobileGarrison, "SetReturnMode");
                    return true;
                case ImprovedGarrisonsOperation.FortifyMobileGarrison:
                    manager = TryGetMemberValue(partyManagement, "mobileGarrisonManagement");
                    mobileGarrison = InvokeRequired(manager, "GetMobileGarrisonPartyOfSettlement", town.Settlement);
                    if (mobileGarrison == null ||
                        !objectManager.TryGetObject(request.TargetIds[0], out Settlement fortificationTarget))
                    {
                        failure = "mobile garrison or fortification target is unavailable";
                        return false;
                    }
                    object sourceMobileParty = InvokeRequired(mobileGarrison, "getMobileParty");
                    InvokeRequired(sourceMobileParty, "SetCustomHomeSettlement", fortificationTarget);
                    InvokeRequired(mobileGarrison, "SetFortifyMode", fortificationTarget);
                    return true;
                case ImprovedGarrisonsOperation.ReturnRecruiter:
                    manager = TryGetMemberValue(partyManagement, "garrisonRecruiterPartyManagement");
                    recruiter = InvokeRequired(manager, "GetRecruiterOfSettlement", town.Settlement);
                    if (recruiter == null)
                    {
                        failure = "Improved Garrisons recruiter is unavailable";
                        return false;
                    }
                    InvokeRequired(recruiter, "SetReturnMode");
                    return true;
                default:
                    return true;
            }
        }
        catch (Exception ex)
        {
            var reported = ex is TargetInvocationException invocation
                ? invocation.InnerException ?? invocation
                : ex;
            failure = reported.GetType().Name + ": " + reported.Message;
            return false;
        }
    }

    private static object GetStaticMember(Type type, string name)
    {
        if (type == null) return null;
        return type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) ??
               type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
    }

    private bool TryBoostBuildingReserve(
        NetPeer peer,
        NetworkRequestImprovedGarrisonsOperation request,
        Town town,
        out string failure)
    {
        failure = null;
        if (!playerManager.TryGetPlayer(peer, out var player) ||
            !objectManager.TryGetObject(player.HeroId, out Hero actor) ||
            actor?.Clan != town.OwnerClan ||
            !int.TryParse(request.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int increase) ||
            !ImprovedGarrisonsBuildingAuthority.TryPlan(
                town.BoostBuildingProcess,
                actor.Gold,
                increase,
                out int newReserve,
                out int newGold))
        {
            failure = "the building-reserve actor or amount is no longer valid";
            return false;
        }

        Hero host = Hero.MainHero;
        int previousReserve = town.BoostBuildingProcess;
        int previousActorGold = actor.Gold;
        int previousHostGold = host?.Gold ?? 0;
        try
        {
            using (new AllowedThread())
            {
                BuildingHelper.BoostBuildingProcessWithGold(newReserve, town);
                if (!ReferenceEquals(actor, host))
                {
                    RestoreGold(host, previousHostGold);
                    GiveGoldAction.ApplyBetweenCharacters(actor, null, increase, false);
                }
            }

            if (town.BoostBuildingProcess != newReserve || actor.Gold != newGold ||
                (!ReferenceEquals(actor, host) && host?.Gold != previousHostGold))
                throw new InvalidOperationException("building-reserve mutation did not reach the planned state");
            return true;
        }
        catch (Exception exception)
        {
            try
            {
                using (new AllowedThread())
                {
                    BuildingHelper.BoostBuildingProcessWithGold(previousReserve, town);
                    RestoreGold(host, previousHostGold);
                    RestoreGold(actor, previousActorGold);
                }
            }
            catch (Exception rollbackException)
            {
                DenyPeerOrAbortSession(
                    peer: null,
                    "building-reserve request " + request.RequestId +
                    " failed and native rollback failed: " + rollbackException.Message);
            }

            failure = exception.GetType().Name + ": " + exception.Message;
            return false;
        }
    }

    private static void RestoreGold(Hero hero, int expectedGold)
    {
        if (hero == null) return;
        int difference = expectedGold - hero.Gold;
        if (difference > 0) GiveGoldAction.ApplyBetweenCharacters(null, hero, difference, false);
        else if (difference < 0) GiveGoldAction.ApplyBetweenCharacters(hero, null, -difference, false);
    }

    private static object InvokeRequired(object instance, string methodName, params object[] arguments)
    {
        if (instance == null) throw new InvalidOperationException(methodName + " manager is unavailable");
        MethodInfo method = instance.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name == methodName &&
                                          candidate.GetParameters().Length == arguments.Length);
        if (method == null) throw new MissingMethodException(instance.GetType().FullName, methodName);
        return method.Invoke(instance, arguments);
    }

    private static object InvokeOptional(object instance, string methodName, params object[] arguments)
    {
        if (instance == null) return null;
        MethodInfo method = instance.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name == methodName &&
                                          candidate.GetParameters().Length == arguments.Length);
        return method?.Invoke(instance, arguments);
    }

    private void TryOpenPendingPartyScreens()
    {
        if (!ModInformation.IsClient || pendingPartyScreens.Count == 0) return;
        object partyManagement = GetStaticMember(assembly?.GetType("ImprovedGarrisons.Main", false), "PartyManagement");
        if (partyManagement == null) return;

        foreach (var pending in pendingPartyScreens.ToArray())
        {
            PartyBase party = null;
            if (!objectManager.TryGetObject(pending.Key, out party) &&
                objectManager.TryGetObject(pending.Key, out MobileParty mobileParty))
                party = mobileParty?.Party;
            if (party == null || !objectManager.TryGetObject(pending.Value, out Town town) || town?.GarrisonParty == null)
                continue;

            try
            {
                InvokeRequired(partyManagement, "PromptPartyManagementMenu", party, town.GarrisonParty);
                pendingPartyScreens.Remove(pending.Key);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Could not open the Improved Garrisons party selection screen for {PartyId}", pending.Key);
                pendingPartyScreens.Remove(pending.Key);
            }
        }
    }

    private bool TryRestoreCanonicalState(
        IReadOnlyCollection<ImprovedGarrisonsStateValue> rollbackValues,
        string expectedHash,
        out string failure)
    {
        if (!ImprovedGarrisonsCanonicalState.TryApply(
                assembly,
                objectManager,
                rollbackValues,
                out failure))
            return false;

        if (!ImprovedGarrisonsCanonicalState.TryBuild(
                assembly,
                objectManager,
                out _,
                out var restoredHash,
                out failure))
            return false;

        if (!string.Equals(restoredHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            failure = $"rollback hash {restoredHash ?? "missing"} did not restore {expectedHash ?? "missing"}";
            return false;
        }

        failure = null;
        return true;
    }

    private bool TryResolveOwnedTown(NetPeer peer, string townId, out Town town)
    {
        town = null;
        if (!playerManager.TryGetPlayer(peer, out var player) ||
            !objectManager.TryGetObject<Clan>(player.ClanId, out var clan) ||
            !objectManager.TryGetObject(townId, out town) ||
            town?.OwnerClan != clan)
        {
            Logger.Warning("Rejected Improved Garrisons request from peer {Peer}: town {TownId} is not owned by its clan", peer.Id, townId);
            return false;
        }
        return true;
    }

    private object ResolveManager(Type type)
    {
        var instance = type?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null);
        if (instance != null) return instance;

        if (type?.FullName == "ImprovedGarrisons.SaveSystem.GarrisonBehavior")
            return assembly.GetType("ImprovedGarrisons.Main", false)
                ?.GetProperty("GarrisonBehavior", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(null);
        return null;
    }

    private bool TryCaptureState(out NetworkImprovedGarrisonsState snapshot, out string failure)
    {
        snapshot = null;
        failure = null;
        if (!stateReady || !ImprovedGarrisonsCanonicalState.TryBuild(
                assembly, objectManager, out var values, out var hash, out failure))
        {
            failure = stateReady ? "could not capture Improved Garrisons server state: " + failure
                : "authoritative state is not ready";
            return false;
        }
        bool changed = lastPublishedHash != null &&
            !string.Equals(lastPublishedHash, hash, StringComparison.OrdinalIgnoreCase);
        long publishedRevision = changed ? revision + 1 : revision;
        snapshot = new NetworkImprovedGarrisonsState(
            ImprovedGarrisonsCompatibilityManifest.AdapterVersion, publishedRevision, hash, values);
        if (!IsSnapshotShapeValid(snapshot, out failure))
        {
            failure = "captured Improved Garrisons server state was invalid: " + failure;
            return false;
        }
        failure = null;
        return true;
    }

    private bool HasStableTransferProof()
    {
        object partyManagement = GetStaticMember(assembly?.GetType("ImprovedGarrisons.Main", false), "PartyManagement");
        object manager = TryGetMemberValue(partyManagement, "transferPartyManagement");
        if (manager == null) return false;
        return manager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(method => method.Name.IndexOf("Transfer", StringComparison.OrdinalIgnoreCase) >= 0 &&
                           method.Name.IndexOf("Settlement", StringComparison.OrdinalIgnoreCase) >= 0 &&
                           method.Name.IndexOf("Get", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private bool PublishStateIfChanged(NetPeer peer = null)
    {
        if (!ModInformation.IsServer) return false;
        if (!stateReady)
        {
            // Campaign callbacks can run while object registration is still assembling the
            // authoritative graph. A connected requester may never proceed without a snapshot;
            // incidental pre-registration callbacks simply wait for the initial publication.
            if (peer != null)
                DenyPeerOrAbortSession(peer, "authoritative state is not ready");
            return false;
        }
        if (!ImprovedGarrisonsCanonicalState.TryBuild(
                assembly, objectManager, out var values, out var hash, out var failure))
        {
            DenyPeerOrAbortSession(peer, "could not capture Improved Garrisons server state: " + failure);
            return false;
        }

        var initialPublication = lastPublishedHash == null;
        var changed = !initialPublication &&
                      !string.Equals(lastPublishedHash, hash, StringComparison.OrdinalIgnoreCase);
        var publishedRevision = changed ? revision + 1 : revision;

        var message = new NetworkImprovedGarrisonsState(
            ImprovedGarrisonsCompatibilityManifest.AdapterVersion,
            publishedRevision,
            hash,
            values);
        if (!IsSnapshotShapeValid(message, out failure))
        {
            DenyPeerOrAbortSession(peer, "captured Improved Garrisons server state was invalid: " + failure);
            return false;
        }

        try
        {
            if (initialPublication || changed) network.SendAll(message);
            else if (peer != null) network.Send(peer, message);
        }
        catch (Exception ex)
        {
            DenyPeerOrAbortSession(peer, "authoritative snapshot publication failed: " + ex.Message);
            return false;
        }

        revision = publishedRevision;
        lastPublishedHash = hash;
        return true;
    }

    internal bool ApplyState(NetworkImprovedGarrisonsState state)
        => TryApplyState(state, out _);

    internal bool TryApplyState(NetworkImprovedGarrisonsState state, out string rejection)
    {
        if (!TryAcceptState(
                revision,
                lastAppliedHash,
                state,
                ApplyCanonicalValues,
                out var acceptedRevision,
                out var acceptedHash,
                out rejection))
            return false;

        revision = acceptedRevision;
        lastAppliedHash = acceptedHash;
        return true;
    }

    private bool ApplyCanonicalValues(
        IReadOnlyCollection<ImprovedGarrisonsStateValue> values,
        out string failure) =>
        ImprovedGarrisonsCanonicalState.TryApply(assembly, objectManager, values, out failure);

    internal static bool TryAcceptState(
        long currentRevision,
        string currentHash,
        NetworkImprovedGarrisonsState state,
        CanonicalStateApplier apply,
        out long acceptedRevision,
        out string acceptedHash,
        out string rejection)
    {
        acceptedRevision = currentRevision;
        acceptedHash = currentHash;
        rejection = null;

        if (!IsSnapshotShapeValid(state, out var failure))
        {
            rejection = "malformed authoritative state: " + failure;
            Logger.Error("Rejected Improved Garrisons state: {Failure}", rejection);
            return false;
        }

        var values = state.Values ?? Array.Empty<ImprovedGarrisonsStateValue>();
        var wireHash = ImprovedGarrisonsCanonicalState.ComputeHash(values);
        if (!string.Equals(wireHash, state.CanonicalHash, StringComparison.OrdinalIgnoreCase))
        {
            rejection = $"corrupt state revision {state.Revision}: hash {wireHash} != {state.CanonicalHash}";
            Logger.Error("Rejected Improved Garrisons {Failure}", rejection);
            return false;
        }

        if (!CanApplyState(currentRevision, currentHash, state.Revision, wireHash))
        {
            rejection = $"stale or conflicting state {state.Revision}/{wireHash}; current is {currentRevision}/{currentHash}";
            Logger.Warning("Rejected Improved Garrisons {Failure}", rejection);
            return false;
        }

        if (state.Revision == currentRevision &&
            string.Equals(currentHash, wireHash, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (apply == null)
        {
            rejection = "canonical state applier is unavailable";
            Logger.Error("Rejected Improved Garrisons state: {Failure}", rejection);
            return false;
        }

        if (!apply(values, out failure))
        {
            rejection = "canonical state could not be applied: " + (failure ?? "unknown semantic failure");
            Logger.Error("Rejected Improved Garrisons state revision {Revision}: {Failure}", state.Revision, rejection);
            return false;
        }

        // Revision/hash are committed only after the detached canonical transaction succeeds.
        acceptedRevision = state.Revision;
        acceptedHash = wireHash;
        return true;
    }

    internal static bool CanApplyState(
        long currentRevision,
        string currentHash,
        long incomingRevision,
        string incomingHash)
    {
        if (incomingRevision < currentRevision) return false;
        if (incomingRevision > currentRevision) return true;
        return currentHash == null || string.Equals(currentHash, incomingHash, StringComparison.OrdinalIgnoreCase);
    }

    internal static void DenyPeerOrAbortSession(NetPeer peer, string failure)
    {
        failure = string.IsNullOrWhiteSpace(failure) ? "unknown authoritative-state failure" : failure;
        if (peer != null)
        {
            Logger.Fatal(
                "Disconnecting peer {Peer} because authoritative Improved Garrisons state is unavailable: {Failure}",
                peer.Id,
                failure);
            peer.Disconnect();
            return;
        }

        Logger.Fatal(
            "Aborting the Coop session because authoritative Improved Garrisons state is unavailable: {Failure}",
            failure);
        throw new InvalidOperationException(
            "The Coop session cannot continue without authoritative Improved Garrisons state: " + failure);
    }

    internal static bool IsRequestShapeValid(NetworkRequestImprovedGarrisonsSettingChange request) =>
        request.SessionId != null && request.SessionId.Length == ModConfigSnapshot.SessionIdLength &&
        Guid.TryParseExact(request.SessionId, "N", out _) && request.RequestId > 0 && request.ExpectedRevision >= 0 &&
        HasProtocolText(request.ManagerType, 1, MaxRequestManagerLength) &&
        HasProtocolText(request.Method, 1, MaxRequestMethodLength) &&
        HasProtocolText(request.TownId, 1, MaxObjectIdLength) &&
        HasProtocolText(request.Value, 0, MaxRequestValueLength);

    internal static string SettingCommandKey(NetworkRequestImprovedGarrisonsSettingChange request) =>
        BuildCommandKey(
            "setting",
            request.SessionId,
            request.ExpectedRevision.ToString(CultureInfo.InvariantCulture),
            request.ManagerType,
            request.Method,
            request.TownId,
            request.Value);

    internal static bool IsSnapshotShapeValid(NetworkImprovedGarrisonsState state, out string failure)
    {
        failure = null;
        if (state == null) { failure = "snapshot is null"; return false; }
        if (!string.Equals(state.AdapterVersion, ImprovedGarrisonsCompatibilityManifest.AdapterVersion, StringComparison.Ordinal))
        {
            failure = $"adapter version {state.AdapterVersion ?? "missing"} is unsupported";
            return false;
        }
        if (state.Revision < 0) { failure = "revision is negative"; return false; }
        if (!HasLength(state.CanonicalHash, 64, 64) || state.CanonicalHash.Any(character => !Uri.IsHexDigit(character)))
        {
            failure = "canonical hash is not a 64-character hexadecimal SHA-256";
            return false;
        }

        var values = state.Values;
        if (values == null) { failure = "values are null"; return false; }
        if (values.Length > MaxSnapshotValues)
        {
            failure = $"value count {values.Length} exceeds {MaxSnapshotValues}";
            return false;
        }

        long characters = 0;
        var canonicalKeys = new HashSet<Tuple<string, string, string>>();
        foreach (var value in values)
        {
            if (value == null || !HasLength(value.Scope, 1, 32) ||
                !HasLength(value.TargetId, 0, MaxObjectIdLength) ||
                !HasLength(value.Property, 1, MaxSnapshotPropertyLength) ||
                !HasLength(value.Value, 0, MaxSnapshotValueLength))
            {
                failure = "a canonical value has a null or oversized field";
                return false;
            }
            if (!canonicalKeys.Add(Tuple.Create(value.Scope, value.TargetId, value.Property)))
            {
                failure = $"duplicate canonical property {value.Scope}/{value.TargetId}/{value.Property}";
                return false;
            }
            characters += value.Scope.Length + value.TargetId.Length + value.Property.Length + value.Value.Length;
            if (characters > MaxSnapshotCharacters)
            {
                failure = $"canonical payload exceeds {MaxSnapshotCharacters} characters";
                return false;
            }
        }

        return true;
    }

    internal static bool IsValueAllowed(string method, object value)
    {
        if (value is bool) return true;
        if (value is float percentage) return percentage >= 0f && percentage <= 1f;
        if (value is not int integer) return value == null;

        if (method == "SetTownMaxUpgradeTier") return integer >= 1 && integer <= 10;
        if (method == "SetRecruiterAmountToRecruit") return integer >= 1 && integer <= 150;
        return integer >= 0 && integer <= 10000;
    }

    private bool TryGetSettingsDictionary(out IDictionary dictionary)
    {
        dictionary = null;
        var behavior = assembly?.GetType("ImprovedGarrisons.Main", false)
            ?.GetProperty("GarrisonBehavior", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null);
        dictionary = behavior?.GetType()
            .GetProperty("SettlementSettingsData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(behavior) as IDictionary;
        return dictionary != null;
    }

    private static bool HasLength(string value, int minimum, int maximum) =>
        value != null && value.Length >= minimum && value.Length <= maximum;

    private static bool HasProtocolText(string value, int minimum, int maximum) =>
        HasLength(value, minimum, maximum) && value.All(character => !char.IsControl(character));

    internal static string BuildCommandKey(string prefix, params string[] fields)
    {
        var builder = new StringBuilder(prefix ?? string.Empty);
        foreach (string field in fields ?? Array.Empty<string>())
        {
            string value = field ?? string.Empty;
            builder.Append('|').Append(value.Length).Append(':').Append(value);
        }
        return builder.ToString();
    }

    private static string RoutedKey(string type, string method) => (type ?? string.Empty) + "::" + (method ?? string.Empty);
}
