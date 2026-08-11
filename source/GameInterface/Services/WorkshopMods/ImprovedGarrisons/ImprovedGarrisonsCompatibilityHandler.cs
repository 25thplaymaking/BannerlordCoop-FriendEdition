using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Configuration;
using GameInterface.Registry.Messages;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
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
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Core.ImageIdentifiers;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace GameInterface.Services.WorkshopMods.ImprovedGarrisons;

internal enum ImprovedGarrisonsReplayDecision
{
    New,
    Replay,
    Conflict,
}

internal sealed class ImprovedGarrisonsRequestLedger<TKey>
{
    private sealed class Entry
    {
        public readonly string CommandKey;
        public object Result;

        public Entry(string commandKey, object result)
        {
            CommandKey = commandKey;
            Result = result;
        }
    }

    private sealed class PeerRequests
    {
        public readonly Dictionary<long, Entry> Entries = new Dictionary<long, Entry>();
        public readonly Queue<long> Order = new Queue<long>();
    }

    private readonly object sync = new object();
    private readonly Dictionary<TKey, PeerRequests> requests = new Dictionary<TKey, PeerRequests>();
    private readonly int capacityPerPeer;

    public ImprovedGarrisonsRequestLedger(int capacityPerPeer)
    {
        if (capacityPerPeer < 1) throw new ArgumentOutOfRangeException(nameof(capacityPerPeer));
        this.capacityPerPeer = capacityPerPeer;
    }

    public bool HasSeen(TKey peer, long requestId)
    {
        lock (sync)
            return requests.TryGetValue(peer, out var peerRequests) && peerRequests.Entries.ContainsKey(requestId);
    }

    public ImprovedGarrisonsReplayDecision Inspect<TResult>(
        TKey peer,
        long requestId,
        string commandKey,
        out TResult result) where TResult : class
    {
        lock (sync)
        {
            if (!requests.TryGetValue(peer, out var peerRequests) ||
                !peerRequests.Entries.TryGetValue(requestId, out var entry))
            {
                result = null;
                return ImprovedGarrisonsReplayDecision.New;
            }

            result = entry.Result as TResult;
            return string.Equals(entry.CommandKey, commandKey, StringComparison.Ordinal)
                ? ImprovedGarrisonsReplayDecision.Replay
                : ImprovedGarrisonsReplayDecision.Conflict;
        }
    }

    public void Record(TKey peer, long requestId, string commandKey, object result = null)
    {
        if (commandKey == null) throw new ArgumentNullException(nameof(commandKey));
        lock (sync)
        {
            if (!requests.TryGetValue(peer, out var peerRequests))
            {
                peerRequests = new PeerRequests();
                requests.Add(peer, peerRequests);
            }

            if (peerRequests.Entries.TryGetValue(requestId, out var existing))
            {
                if (string.Equals(existing.CommandKey, commandKey, StringComparison.Ordinal) && result != null)
                    existing.Result = result;
                return;
            }

            peerRequests.Entries.Add(requestId, new Entry(commandKey, result));
            peerRequests.Order.Enqueue(requestId);
            while (peerRequests.Order.Count > capacityPerPeer)
            {
                long evicted = peerRequests.Order.Dequeue();
                peerRequests.Entries.Remove(evicted);
            }
        }
    }

    public void Reset()
    {
        lock (sync) requests.Clear();
    }
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
    private readonly Harmony harmony;
    private readonly HashSet<string> deniedNotifications = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> pendingPartyScreens =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, (ImprovedGarrisonsMethodSpec Spec, MethodInfo Method)> routedMethods =
        new Dictionary<string, (ImprovedGarrisonsMethodSpec, MethodInfo)>(StringComparer.Ordinal);
    private readonly ImprovedGarrisonsRequestLedger<NetPeer> requestLedger =
        new ImprovedGarrisonsRequestLedger<NetPeer>(256);

    private Assembly assembly;
    private long revision;
    private long nextRequestId;
    private string lastPublishedHash;
    private string lastAppliedHash;
    private bool compatible;
    private bool stateReady;

    public ImprovedGarrisonsCompatibilityHandler(
        IMessageBroker messageBroker,
        INetwork network,
        IObjectManager objectManager,
        IPlayerManager playerManager,
        IModConfigAuthority configAuthority,
        IWorkshopCapabilityRegistry capabilityRegistry,
        Harmony _)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;
        this.playerManager = playerManager;
        this.configAuthority = configAuthority;
        this.capabilityRegistry = capabilityRegistry;
        this.harmony = new Harmony(ImprovedGarrisonsCompatibilityManifest.AdapterHarmonyId);

        compatible = TryInstall();
        if (compatible) ImprovedGarrisonsPatchRuntime.Current = this;

        messageBroker.Subscribe<AllGameObjectsRegistered>(Handle_AllGameObjectsRegistered);
        messageBroker.Subscribe<NetworkRequestImprovedGarrisonsState>(Handle_StateRequest);
        messageBroker.Subscribe<NetworkRequestImprovedGarrisonsSettingChange>(Handle_SettingRequest);
        messageBroker.Subscribe<NetworkRequestImprovedGarrisonsOperation>(Handle_OperationRequest);
        messageBroker.Subscribe<NetworkImprovedGarrisonsOperationResult>(Handle_OperationResult);
        messageBroker.Subscribe<NetworkImprovedGarrisonsState>(Handle_State);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<AllGameObjectsRegistered>(Handle_AllGameObjectsRegistered);
        messageBroker.Unsubscribe<NetworkRequestImprovedGarrisonsState>(Handle_StateRequest);
        messageBroker.Unsubscribe<NetworkRequestImprovedGarrisonsSettingChange>(Handle_SettingRequest);
        messageBroker.Unsubscribe<NetworkRequestImprovedGarrisonsOperation>(Handle_OperationRequest);
        messageBroker.Unsubscribe<NetworkImprovedGarrisonsOperationResult>(Handle_OperationResult);
        messageBroker.Unsubscribe<NetworkImprovedGarrisonsState>(Handle_State);
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
        network.SendAll(new NetworkRequestImprovedGarrisonsSettingChange(
            config.SessionId,
            Interlocked.Increment(ref nextRequestId),
            revision,
            method.DeclaringType.FullName,
            method.Name,
            townId,
            value));
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

        network.SendAll(request);
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

        if (type.EndsWith(".ManagementSettings", StringComparison.Ordinal))
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
            Interlocked.Increment(ref nextRequestId),
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
            Interlocked.Increment(ref nextRequestId),
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
                   ImprovedGarrisonsCapabilitySource.Operation);
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
        requestLedger.Reset();
        revision = 0;
        nextRequestId = 0;
        lastPublishedHash = null;
        lastAppliedHash = null;
        stateReady = !ModInformation.IsClient;
        if (ModInformation.IsClient)
            network.SendAll(new NetworkRequestImprovedGarrisonsState());
        else
            PublishStateIfChanged();
    }

    private void Handle_StateRequest(MessagePayload<NetworkRequestImprovedGarrisonsState> payload)
    {
        if (!compatible || ModInformation.IsClient || payload.Who is not NetPeer peer) return;
        if (!stateReady)
        {
            DenyPeerOrAbortSession(peer, "authoritative state is not ready");
            return;
        }
        GameThread.RunSafe(() => SendState(peer), context: nameof(ImprovedGarrisonsCompatibilityHandler));
    }

    private void Handle_SettingRequest(MessagePayload<NetworkRequestImprovedGarrisonsSettingChange> payload)
    {
        if (!compatible || ModInformation.IsClient || payload.Who is not NetPeer peer) return;
        if (!stateReady)
        {
            DenyPeerOrAbortSession(peer, "authoritative state is not ready for setting requests");
            return;
        }
        var request = payload.What;
        GameThread.RunSafe(
            () => ApplySettingRequest(peer, request),
            context: nameof(ImprovedGarrisonsCompatibilityHandler));
    }

    private void Handle_OperationRequest(MessagePayload<NetworkRequestImprovedGarrisonsOperation> payload)
    {
        if (!compatible || ModInformation.IsClient || payload.Who is not NetPeer peer) return;
        if (!stateReady)
        {
            DenyPeerOrAbortSession(peer, "authoritative state is not ready for management requests");
            return;
        }
        var request = payload.What;
        GameThread.RunSafe(
            () => ApplyOperationRequest(peer, request),
            context: nameof(ImprovedGarrisonsCompatibilityHandler));
    }

    private void Handle_OperationResult(MessagePayload<NetworkImprovedGarrisonsOperationResult> payload)
    {
        if (!compatible || !ModInformation.IsClient || payload.Who is not NetPeer serverPeer ||
            !configAuthority.IsTrustedServer(serverPeer) ||
            !ImprovedGarrisonsOperationProtocol.IsResultShapeValid(payload.What) ||
            !configAuthority.TryGetCurrent(out var config) ||
            !string.Equals(config.SessionId, payload.What.SessionId, StringComparison.Ordinal))
            return;

        NetworkImprovedGarrisonsOperationResult result = payload.What;
        GameThread.RunSafe(() =>
        {
            if (result.Status == ImprovedGarrisonsOperationStatus.Accepted)
            {
                if (!string.IsNullOrEmpty(result.PartyId))
                {
                    pendingPartyScreens[result.PartyId] = result.TownId;
                    TryOpenPendingPartyScreens();
                }
                if (result.Operation == ImprovedGarrisonsOperation.SetMobileGarrisonEscort ||
                    result.Operation == ImprovedGarrisonsOperation.OrderMobileGarrisonPatrol ||
                    result.Operation == ImprovedGarrisonsOperation.OrderMobileGarrisonReturn ||
                    result.Operation == ImprovedGarrisonsOperation.FortifyMobileGarrison ||
                    result.Operation == ImprovedGarrisonsOperation.ReturnRecruiter ||
                    result.Operation == ImprovedGarrisonsOperation.StartHostileEncounter)
                    TaleWorlds.CampaignSystem.Encounters.PlayerEncounter.LeaveEncounter = true;
                InformationManager.DisplayMessage(new InformationMessage(
                    "Improved Garrisons accepted the co-op management action."));
                return;
            }

            InformationManager.DisplayMessage(new InformationMessage(
                result.Status == ImprovedGarrisonsOperationStatus.StaleState
                    ? "Improved Garrisons changed before the action completed; its menu has been refreshed."
                    : "The Coop server rejected the Improved Garrisons management action."));
        }, context: nameof(ImprovedGarrisonsCompatibilityHandler));
    }

    private void Handle_State(MessagePayload<NetworkImprovedGarrisonsState> payload)
    {
        // Network messages are published with their transport peer as source. On a client the
        // only transport peer is its server connection; reject locally published/forged broker
        // messages that have no transport identity.
        if (!compatible || !ModInformation.IsClient || payload.Who is not NetPeer serverPeer ||
            !ImprovedGarrisonsSnapshotOriginGuard.IsTrustedServerTransport(
                serverPeer,
                localIsClient: ModInformation.IsClient))
            return;

        GameThread.RunSafe(
            () =>
            {
                if (TryApplyState(payload.What, out var rejection))
                {
                    stateReady = true;
                    TryOpenPendingPartyScreens();
                    return;
                }

                stateReady = false;
                Logger.Fatal(
                    "Disconnecting from the Coop server because Improved Garrisons state could not be accepted: {Failure}",
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
            },
            context: nameof(ImprovedGarrisonsCompatibilityHandler));
    }

    private void ApplySettingRequest(NetPeer peer, NetworkRequestImprovedGarrisonsSettingChange request)
    {
        if (!IsRequestShapeValid(request) || !CanUseManagementRoute(out var config) ||
            !string.Equals(config.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            Logger.Warning("Rejected malformed Improved Garrisons request {RequestId} from peer {Peer}",
                request.RequestId, peer.Id);
            SendState(peer);
            return;
        }

        string commandKey = SettingCommandKey(request);
        ImprovedGarrisonsReplayDecision replay = requestLedger.Inspect(
            peer,
            request.RequestId,
            commandKey,
            out object _);
        if (replay == ImprovedGarrisonsReplayDecision.Conflict)
        {
            DenyPeerOrAbortSession(peer,
                "reused Improved Garrisons request ID " + request.RequestId + " with a different setting command");
            return;
        }
        if (replay == ImprovedGarrisonsReplayDecision.Replay)
        {
            SendState(peer);
            return;
        }

        // Claim the request ID before any validation that depends on mutable campaign state. An
        // exact retry receives the resulting snapshot; a changed payload using the same ID is a
        // protocol conflict and can never be interpreted as a new command.
        requestLedger.Record(peer, request.RequestId, commandKey);

        if (request.ExpectedRevision != revision)
        {
            Logger.Warning(
                "Rejected stale Improved Garrisons request {RequestId} from peer {Peer}: expected revision {Expected}, server is {Actual}",
                request.RequestId, peer.Id, request.ExpectedRevision, revision);
            SendState(peer);
            return;
        }

        if (!TryResolveOwnedTown(peer, request.TownId, out var town)) return;
        if (!routedMethods.TryGetValue(RoutedKey(request.ManagerType, request.Method), out var route))
        {
            Logger.Warning("Rejected unknown Improved Garrisons UI operation {Type}.{Method}", request.ManagerType, request.Method);
            return;
        }

        var parameters = route.Method.GetParameters();
        var arguments = new object[parameters.Length];
        arguments[0] = town;
        if (parameters.Length == 2)
        {
            if (!ImprovedGarrisonsCanonicalState.TryParseValue(request.Value, parameters[1].ParameterType, out var parsed) ||
                !IsValueAllowed(route.Method.Name, parsed))
            {
                Logger.Warning("Rejected invalid Improved Garrisons value for {Method}: {Value}", route.Method.Name, request.Value);
                return;
            }
            arguments[1] = parsed;
        }

        var manager = ResolveManager(route.Method.DeclaringType);
        if (manager == null)
        {
            Logger.Error("Cannot resolve Improved Garrisons manager {Type}", route.Method.DeclaringType?.FullName);
            return;
        }

        // Primitive setters are expected to touch only canonical Improved Garrisons state, but
        // reflection targets can still throw after a partial assignment. Capture a detached
        // rollback image before invoking so a failed request cannot strand the server between
        // revisions or become repeatable.
        if (!ImprovedGarrisonsCanonicalState.TryBuild(
                assembly,
                objectManager,
                out var rollbackValues,
                out var rollbackHash,
                out var captureFailure))
        {
            DenyPeerOrAbortSession(
                peer,
                "could not capture rollback state for setting request " + request.RequestId + ": " + captureFailure);
            return;
        }

        // Record immediately before invoking. If an upstream method partially mutates and throws,
        // retrying the same request ID must still be unable to duplicate that mutation.
        try
        {
            route.Method.Invoke(manager, arguments);
        }
        catch (Exception ex)
        {
            var reported = ex is TargetInvocationException invocation
                ? invocation.InnerException ?? invocation
                : ex;
            if (!TryRestoreCanonicalState(rollbackValues, rollbackHash, out var rollbackFailure))
            {
                DenyPeerOrAbortSession(
                    peer: null,
                    "setting request " + request.RequestId + " failed after a partial mutation and rollback failed: " +
                    rollbackFailure);
            }

            Logger.Error(reported,
                "Improved Garrisons operation {Type}.{Method} failed for request {RequestId}",
                request.ManagerType, request.Method, request.RequestId);
            PublishStateIfChanged(peer);
            return;
        }

        PublishStateIfChanged(peer);
    }

    private void ApplyOperationRequest(NetPeer peer, NetworkRequestImprovedGarrisonsOperation request)
    {
        if (!ImprovedGarrisonsOperationProtocol.IsRequestShapeValid(request) ||
            !CanUseManagementRoute(out var config))
        {
            Logger.Warning("Rejected malformed, stale-session, or disabled Improved Garrisons management request from peer {Peer}",
                peer.Id);
            SendState(peer);
            return;
        }
        if (!string.Equals(config.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            SendOperationResult(peer, request, ImprovedGarrisonsOperationStatus.StaleSession, config.SessionId);
            SendState(peer);
            return;
        }
        string commandKey = ImprovedGarrisonsOperationProtocol.CommandKey(request);
        ImprovedGarrisonsReplayDecision replay = requestLedger.Inspect(
            peer,
            request.RequestId,
            commandKey,
            out NetworkImprovedGarrisonsOperationResult cached);
        if (replay == ImprovedGarrisonsReplayDecision.Conflict)
        {
            DenyPeerOrAbortSession(peer,
                "reused Improved Garrisons request ID " + request.RequestId + " with a different management command");
            return;
        }
        if (replay == ImprovedGarrisonsReplayDecision.Replay)
        {
            if (cached != null)
                network.Send(peer, cached);
            SendState(peer);
            return;
        }
        if (request.ExpectedRevision != revision)
        {
            Logger.Warning(
                "Rejected stale Improved Garrisons management request {RequestId} from peer {Peer}: expected {Expected}, server is {Actual}",
                request.RequestId, peer.Id, request.ExpectedRevision, revision);
            SendOperationResult(peer, request, ImprovedGarrisonsOperationStatus.StaleState, config.SessionId);
            SendState(peer);
            return;
        }
        if (request.Operation == ImprovedGarrisonsOperation.StartHostileEncounter)
        {
            ApplyHostileEncounterRequest(peer, request, config.SessionId);
            return;
        }
        if (!TryResolveOwnedTown(peer, request.TownId, out var town))
        {
            SendOperationResult(peer, request, ImprovedGarrisonsOperationStatus.Rejected, config.SessionId);
            SendState(peer);
            return;
        }

        Clan clan = town.OwnerClan;
        string[] ownedTownIds = Settlement.All
            .Where(settlement => settlement?.Town?.OwnerClan == clan && (settlement.IsTown || settlement.IsCastle))
            .Select(settlement => objectManager.TryGetId(settlement.Town, out string id) ? id : null)
            .Where(id => id != null)
            .ToArray();

        if (!ImprovedGarrisonsCanonicalState.TryBuild(
                assembly, objectManager, out var rollbackValues, out var rollbackHash, out var captureFailure))
        {
            DenyPeerOrAbortSession(peer,
                "could not capture rollback state for management request " + request.RequestId + ": " + captureFailure);
            return;
        }

        bool canonicalOperation = IsCanonicalOperation(request.Operation);
        ImprovedGarrisonsStateValue[] transformed = rollbackValues;
        if (canonicalOperation && !ImprovedGarrisonsCanonicalOperations.TryTransform(
                rollbackValues,
                request,
                ownedTownIds,
                out transformed,
                out var transformFailure))
        {
            Logger.Warning(
                "Rejected Improved Garrisons management request {RequestId}: {Failure}",
                request.RequestId,
                transformFailure);
            SendOperationResult(peer, request, ImprovedGarrisonsOperationStatus.Rejected, config.SessionId);
            SendState(peer);
            return;
        }

        if (!ValidateNativeOperation(request, town, clan, ownedTownIds, out var nativeFailure))
        {
            Logger.Warning(
                "Rejected Improved Garrisons management request {RequestId}: {Failure}",
                request.RequestId,
                nativeFailure);
            SendOperationResult(peer, request, ImprovedGarrisonsOperationStatus.Rejected, config.SessionId);
            SendState(peer);
            return;
        }

        requestLedger.Record(peer, request.RequestId, commandKey);
        string partyId = string.Empty;
        bool succeeded = true;
        try
        {
            if (canonicalOperation && !ImprovedGarrisonsCanonicalState.TryApply(
                    assembly, objectManager, transformed, out var applyFailure))
                throw new InvalidOperationException("canonical operation could not be applied: " + applyFailure);

            if (IsNativeOperation(request.Operation) &&
                !TryExecuteNativeOperation(request, town, out partyId, out var executionFailure))
                throw new InvalidOperationException(executionFailure);
        }
        catch (Exception ex)
        {
            succeeded = false;
            if (!TryRestoreCanonicalState(rollbackValues, rollbackHash, out var rollbackFailure))
                DenyPeerOrAbortSession(
                    peer: null,
                    "management request " + request.RequestId +
                    " failed after mutation and rollback failed: " + rollbackFailure);
            Logger.Error(ex,
                "Improved Garrisons management operation {Operation} failed for request {RequestId}",
                request.Operation,
                request.RequestId);
        }

        PublishStateIfChanged(peer);
        SendOperationResult(
            peer,
            request,
            succeeded ? ImprovedGarrisonsOperationStatus.Accepted : ImprovedGarrisonsOperationStatus.Failed,
            config.SessionId,
            succeeded ? partyId : string.Empty);
    }

    private void ApplyHostileEncounterRequest(
        NetPeer peer,
        NetworkRequestImprovedGarrisonsOperation request,
        string sessionId)
    {
        if (request.TargetIds.Length != 1 ||
            !string.Equals(request.TownId, request.TargetIds[0], StringComparison.Ordinal) ||
            !playerManager.TryGetPlayer(peer, out var player) ||
            !objectManager.TryGetObject(player.MobilePartyId, out MobileParty playerParty) ||
            !objectManager.TryGetObject(request.TargetIds[0], out MobileParty targetParty) ||
            playerParty == null || targetParty == null || !targetParty.IsActive ||
            ReferenceEquals(playerParty, targetParty) ||
            playerParty.MapFaction == null || targetParty.MapFaction == null ||
            FactionManager.IsAtWarAgainstFaction(playerParty.MapFaction, targetParty.MapFaction) ||
            playerParty.Position.ToVec2().DistanceSquared(targetParty.Position.ToVec2()) > 4f ||
            !TryGetMobileGarrison(targetParty, out object targetGarrison) ||
            TryGetMemberValue(targetGarrison, "isNPC") is not bool isNpc || !isNpc)
        {
            SendOperationResult(peer, request, ImprovedGarrisonsOperationStatus.Rejected, sessionId);
            return;
        }

        requestLedger.Record(
            peer,
            request.RequestId,
            ImprovedGarrisonsOperationProtocol.CommandKey(request));
        try
        {
            TaleWorlds.CampaignSystem.Actions.BeHostileAction.ApplyEncounterHostileAction(
                playerParty.Party,
                targetParty.Party);
            SendOperationResult(peer, request, ImprovedGarrisonsOperationStatus.Accepted, sessionId);
        }
        catch (Exception ex)
        {
            Logger.Error(ex,
                "Improved Garrisons hostile encounter operation failed for request {RequestId}",
                request.RequestId);
            SendOperationResult(peer, request, ImprovedGarrisonsOperationStatus.Failed, sessionId);
        }
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
        operation == ImprovedGarrisonsOperation.FortifyMobileGarrison;

    private bool ValidateNativeOperation(
        NetworkRequestImprovedGarrisonsOperation request,
        Town town,
        Clan clan,
        IReadOnlyCollection<string> ownedTownIds,
        out string failure)
    {
        failure = null;
        if (!IsNativeOperation(request.Operation)) return true;
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
        return true;
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

    private void SendOperationResult(
        NetPeer peer,
        NetworkRequestImprovedGarrisonsOperation request,
        ImprovedGarrisonsOperationStatus status,
        string sessionId,
        string partyId = null)
    {
        if (peer == null || request == null || string.IsNullOrEmpty(sessionId)) return;
        var result = new NetworkImprovedGarrisonsOperationResult(
            sessionId,
            request.RequestId,
            request.Operation,
            status,
            request.TownId,
            partyId);
        if (ImprovedGarrisonsOperationProtocol.IsResultShapeValid(result))
        {
            requestLedger.Record(
                peer,
                request.RequestId,
                ImprovedGarrisonsOperationProtocol.CommandKey(request),
                result);
            network.Send(peer, result);
        }
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

    private void SendState(NetPeer peer)
    {
        PublishStateIfChanged(peer);
    }

    private void PublishStateIfChanged(NetPeer peer = null)
    {
        if (!ModInformation.IsServer) return;
        if (!stateReady)
        {
            // Campaign callbacks can run while object registration is still assembling the
            // authoritative graph. A connected requester may never proceed without a snapshot;
            // incidental pre-registration callbacks simply wait for the initial publication.
            if (peer != null)
                DenyPeerOrAbortSession(peer, "authoritative state is not ready");
            return;
        }
        if (!ImprovedGarrisonsCanonicalState.TryBuild(
                assembly, objectManager, out var values, out var hash, out var failure))
        {
            DenyPeerOrAbortSession(peer, "could not capture Improved Garrisons server state: " + failure);
            return;
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
            return;
        }

        try
        {
            if (initialPublication || changed) network.SendAll(message);
            else if (peer != null) network.Send(peer, message);
        }
        catch (Exception ex)
        {
            DenyPeerOrAbortSession(peer, "authoritative snapshot publication failed: " + ex.Message);
            return;
        }

        revision = publishedRevision;
        lastPublishedHash = hash;
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
        Guid.TryParseExact(request.SessionId, "N", out _) &&
        request.RequestId > 0 && request.ExpectedRevision >= 0 &&
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
