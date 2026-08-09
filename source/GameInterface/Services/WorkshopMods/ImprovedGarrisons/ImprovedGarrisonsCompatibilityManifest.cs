using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml;

namespace GameInterface.Services.WorkshopMods.ImprovedGarrisons;

internal enum ImprovedGarrisonsPatchKind
{
    ServerTick,
    ServerTickNoPublication,
    ServerLifecycle,
    DeterministicInitialization,
    ServerMutation,
    ServerPersistence,
    RoutedSetting,
    DeniedClientUi,
    StablePartyIdentity,
    FinanceRead,
    ClientDefaultConfig,
    PlayerTownSettings,
    PlayerSettlementInitialization,
    PlayerSettlementOwnerChanged,
    PartyHomeResolver,
    VillagePartySpawnResolver,
    VillagePartyLookup
}

internal sealed class ImprovedGarrisonsMethodSpec
{
    public string TypeName { get; }
    public string MethodName { get; }
    public ImprovedGarrisonsPatchKind Kind { get; }
    public string[] ParameterTypeNames { get; }

    public ImprovedGarrisonsMethodSpec(
        string typeName,
        string methodName,
        ImprovedGarrisonsPatchKind kind,
        params string[] parameterTypeNames)
    {
        TypeName = typeName;
        MethodName = methodName;
        Kind = kind;
        ParameterTypeNames = parameterTypeNames ?? Array.Empty<string>();
    }

    public string Key => $"{TypeName}::{MethodName}({string.Join(",", ParameterTypeNames)})";

    public MethodInfo Resolve(Assembly assembly)
    {
        var type = assembly.GetType(TypeName, throwOnError: false, ignoreCase: false);
        if (type == null) return null;

        return type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(method => method.Name == MethodName && ParametersMatch(method));
    }

    private bool ParametersMatch(MethodInfo method)
    {
        var parameters = method.GetParameters();
        if (parameters.Length != ParameterTypeNames.Length) return false;

        for (var i = 0; i < parameters.Length; i++)
        {
            var actual = parameters[i].ParameterType;
            if (actual.IsByRef) actual = actual.GetElementType();
            var expected = ParameterTypeNames[i];
            var actualName = actual?.FullName;
            if (actual?.IsGenericType == true && expected.IndexOf("[[", StringComparison.Ordinal) < 0)
                actualName = actual.GetGenericTypeDefinition().FullName;
            if (!string.Equals(actualName, expected, StringComparison.Ordinal)) return false;
        }

        return true;
    }
}

/// <summary>
/// Exact compatibility contract for the creator-approved Improved Garrisons 4.2.0.7 binary.
/// No patch is installed unless the manifest version, binary hash, and every audited method all match.
/// This makes an upstream update fail closed instead of silently running a stale set of detours.
/// </summary>
internal static class ImprovedGarrisonsCompatibilityManifest
{
    public const string AssemblyName = "ImprovedGarrisons";
    public const string SupportedModuleVersion = "v4.2.0.7";
    public const string SupportedSha256 = "FEDAB4041748951282634101871A9C41219BF3F2FA90D4E9CD6A4CBEC082CE15";
    public const string AdapterVersion = "2";
    public const string AdapterHarmonyId = "Bannerlord.Coop.Workshop.ImprovedGarrisons";

    private const string Town = "TaleWorlds.CampaignSystem.Settlements.Town";
    private const string Settlement = "TaleWorlds.CampaignSystem.Settlements.Settlement";
    private const string MobileParty = "TaleWorlds.CampaignSystem.Party.MobileParty";
    private const string PartyBase = "TaleWorlds.CampaignSystem.Party.PartyBase";
    private const string Hero = "TaleWorlds.CampaignSystem.Hero";
    private const string MapEvent = "TaleWorlds.CampaignSystem.MapEvents.MapEvent";
    private const string CharacterObject = "TaleWorlds.CampaignSystem.CharacterObject";
    private const string TroopRoster = "TaleWorlds.CampaignSystem.Roster.TroopRoster";
    private const string Village = "TaleWorlds.CampaignSystem.Settlements.Village";

    public static readonly IReadOnlyList<ImprovedGarrisonsMethodSpec> Methods = BuildMethods();

    public static bool TryValidate(
        Assembly assembly,
        out IReadOnlyDictionary<ImprovedGarrisonsMethodSpec, MethodInfo> resolved,
        out string failure)
    {
        resolved = null;
        failure = null;

        if (assembly == null || !string.Equals(assembly.GetName().Name, AssemblyName, StringComparison.Ordinal))
        {
            failure = "ImprovedGarrisons assembly is not loaded";
            return false;
        }

        if (!TryReadIdentity(assembly.Location, out var moduleVersion, out var sha256, out failure)) return false;
        if (!IsSupportedIdentity(moduleVersion, sha256))
        {
            failure = $"unsupported Improved Garrisons identity (module={moduleVersion ?? "missing"}, sha256={sha256 ?? "missing"})";
            return false;
        }

        var methods = new Dictionary<ImprovedGarrisonsMethodSpec, MethodInfo>();
        foreach (var spec in Methods)
        {
            MethodInfo method;
            try
            {
                method = spec.Resolve(assembly);
            }
            catch (InvalidOperationException)
            {
                failure = $"ambiguous audited method {spec.Key}";
                return false;
            }

            if (method == null)
            {
                failure = $"missing audited method {spec.Key}";
                return false;
            }

            methods.Add(spec, method);
        }

        resolved = methods;
        return true;
    }

    internal static bool IsSupportedIdentity(string moduleVersion, string sha256) =>
        string.Equals(moduleVersion, SupportedModuleVersion, StringComparison.Ordinal) &&
        string.Equals(sha256, SupportedSha256, StringComparison.OrdinalIgnoreCase);

    internal static bool TryReadIdentity(
        string assemblyPath,
        out string moduleVersion,
        out string sha256,
        out string failure)
    {
        moduleVersion = null;
        sha256 = null;
        failure = null;

        try
        {
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            {
                failure = "ImprovedGarrisons.dll has no readable on-disk location";
                return false;
            }

            using (var stream = File.OpenRead(assemblyPath))
            using (var hash = SHA256.Create())
            {
                sha256 = ToHex(hash.ComputeHash(stream));
            }

            var moduleRoot = Directory.GetParent(assemblyPath)?.Parent?.Parent?.FullName;
            var manifestPath = moduleRoot == null ? null : Path.Combine(moduleRoot, "SubModule.xml");
            if (manifestPath == null || !File.Exists(manifestPath))
            {
                // Friend Edition's private integrated distribution copies the approved binary into
                // its own module. The original SubModule.xml is therefore not necessarily beside
                // Assembly.Location. The cryptographic identity still pins one exact 4.2.0.7 file;
                // never infer a version for any other hash.
                if (string.Equals(sha256, SupportedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    moduleVersion = SupportedModuleVersion;
                    return true;
                }

                failure = "Improved Garrisons SubModule.xml was not found and the binary hash is not approved";
                return false;
            }

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(manifestPath, settings)) document.Load(reader);

            var id = document.SelectSingleNode("/Module/Id")?.Attributes?["value"]?.Value;
            moduleVersion = document.SelectSingleNode("/Module/Version")?.Attributes?["value"]?.Value;
            if (!string.Equals(id, AssemblyName, StringComparison.Ordinal))
            {
                failure = $"unexpected module id {id ?? "missing"}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            failure = $"could not verify Improved Garrisons: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static string ToHex(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        const string digits = "0123456789ABCDEF";
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = digits[bytes[i] >> 4];
            chars[(i * 2) + 1] = digits[bytes[i] & 15];
        }
        return new string(chars);
    }

    private static IReadOnlyList<ImprovedGarrisonsMethodSpec> BuildMethods()
    {
        var result = new List<ImprovedGarrisonsMethodSpec>();
        void Add(string type, string method, ImprovedGarrisonsPatchKind kind, params string[] args) =>
            result.Add(new ImprovedGarrisonsMethodSpec(type, method, kind, args));

        const string daily = "ImprovedGarrisons.SaveSystem.GarrisonDailyBehavior";
        const string behavior = "ImprovedGarrisons.SaveSystem.GarrisonBehavior";
        const string partyBehavior = "ImprovedGarrisons.Behaviours.GarrisonPartyBehavior";
        const string main = "ImprovedGarrisons.Main";
        const string partyManager = "ImprovedGarrisons.AI.AIManagers.PartyManager";
        const string mobileManager = "ImprovedGarrisons.AI.AIManagers.MobileGarrisonManager";
        const string recruiterManager = "ImprovedGarrisons.AI.AIManagers.GarrisonRecruiterPartyManager";
        const string villageManager = "ImprovedGarrisons.AI.AIManagers.VillageRecruitPartyManager";
        const string transferManager = "ImprovedGarrisons.AI.AIManagers.TransferPartyManager";
        const string recruitment = "ImprovedGarrisons.Recruitment.GarrisonRecruitmentLogic";
        const string upgrade = "ImprovedGarrisons.Upgrade.GarrisonUpgradeLogic";
        const string activityLog = "ImprovedGarrisons.ActivityLogging.ActivityLogManager";

        // Pin the pre-snapshot initialization surface. The deterministic prefix lets all roles
        // initialize the mod, but forces one identical optional-model composition before AddModels.
        Add(main, "OnSubModuleLoad", ImprovedGarrisonsPatchKind.DeterministicInitialization);
        Add(main, "OnCampaignStart", ImprovedGarrisonsPatchKind.DeterministicInitialization,
            "TaleWorlds.Core.Game", "System.Object");
        Add(main, "OnGameStart", ImprovedGarrisonsPatchKind.DeterministicInitialization,
            "TaleWorlds.Core.Game", "TaleWorlds.Core.IGameStarter");
        Add(main, "InitializeGame", ImprovedGarrisonsPatchKind.DeterministicInitialization,
            "TaleWorlds.Core.Game", "TaleWorlds.Core.IGameStarter");
        Add(main, "AddModels", ImprovedGarrisonsPatchKind.DeterministicInitialization,
            "TaleWorlds.CampaignSystem.CampaignGameStarter");

        Add(daily, "DailyBehavior", ImprovedGarrisonsPatchKind.ServerTick);
        Add(behavior, "HourlyEvent", ImprovedGarrisonsPatchKind.ServerTick);
        Add(behavior, "DailyEvent", ImprovedGarrisonsPatchKind.ServerTick);
        // Per-party callbacks still use the deterministic tick ledger, but do not recapture and
        // hash the entire canonical state once for every party. The containing hourly callbacks
        // publish a single state change after their authoritative work completes.
        Add(partyBehavior, "PartyPartialHourlyAi", ImprovedGarrisonsPatchKind.ServerTickNoPublication, MobileParty);
        Add(partyBehavior, "PartyPartialHourlyAi", ImprovedGarrisonsPatchKind.ServerTickNoPublication);
        Add(partyBehavior, "PartyHourlyAi", ImprovedGarrisonsPatchKind.ServerTick);
        Add(partyBehavior, "PartyDailyAi", ImprovedGarrisonsPatchKind.ServerTickNoPublication, MobileParty);
        // These are nested under the campaign callbacks above. Guarding them with the tick
        // ledger as well would suppress legitimate per-party work within the same hour.
        Add(partyManager, "ExecutePartialHourlyAi", ImprovedGarrisonsPatchKind.ServerMutation, MobileParty);
        Add(partyManager, "ExecutePartialHourlyAi", ImprovedGarrisonsPatchKind.ServerMutation);
        Add(partyManager, "ExecuteHourlyAi", ImprovedGarrisonsPatchKind.ServerMutation);
        Add(mobileManager, "ExecutePartialHourThinkBehavior", ImprovedGarrisonsPatchKind.ServerMutation);
        Add(mobileManager, "ExecuteHourThinkBehavior", ImprovedGarrisonsPatchKind.ServerMutation);
        Add(recruiterManager, "ExecutePartialHourlyBehavior", ImprovedGarrisonsPatchKind.ServerMutation);
        Add(recruiterManager, "ExecuteHourThinkBehaviorForAll", ImprovedGarrisonsPatchKind.ServerMutation);
        Add(villageManager, "ExecuteHourThinkBehavior", ImprovedGarrisonsPatchKind.ServerMutation);
        Add(transferManager, "ExecuteHourThinkBehavior", ImprovedGarrisonsPatchKind.ServerMutation);

        // These lifecycle entry points perform party deletion, party-manager reconstruction, and
        // queued mutations before reaching the already guarded helpers. Guard the outer methods
        // too so a client can never enter the destructive portion of the original mod.
        Add(main, "OnApplicationTick", ImprovedGarrisonsPatchKind.ServerLifecycle, "System.Single");
        Add(partyBehavior, "RegisterEvents", ImprovedGarrisonsPatchKind.ServerLifecycle);
        Add(partyBehavior, "OnGameOpen", ImprovedGarrisonsPatchKind.ServerLifecycle,
            "TaleWorlds.CampaignSystem.CampaignGameStarter");
        Add(partyBehavior, "OnGameStartDeleteAllIGParties", ImprovedGarrisonsPatchKind.ServerLifecycle);
        Add(partyBehavior, "OnGameStartSetAllIGParties", ImprovedGarrisonsPatchKind.ServerLifecycle);
        Add(partyBehavior, "ReturnAllIGParties", ImprovedGarrisonsPatchKind.ServerLifecycle);
        Add(partyBehavior, "SetPartyOwner", ImprovedGarrisonsPatchKind.ServerLifecycle, MobileParty);

        // Activity logs are save-backed custom state. Clients neither register the weekly reset
        // nor append local costs/activities while native roster and party messages are applied.
        Add(activityLog, "RegisterEvents", ImprovedGarrisonsPatchKind.ServerLifecycle);
        Add(activityLog, "WeeklyEvent", ImprovedGarrisonsPatchKind.ServerLifecycle);
        Add(activityLog, "AddUnitRecruitmentCost", ImprovedGarrisonsPatchKind.ServerMutation,
            Town, CharacterObject, "System.Int32", Hero);
        Add(activityLog, "AddUnitRecruitmentCost", ImprovedGarrisonsPatchKind.ServerMutation,
            "ImprovedGarrisons.AI.AITypes.GarrisonRecruiter", CharacterObject, "System.Int32", Hero);
        Add(activityLog, "AddUnitUpgradeCost", ImprovedGarrisonsPatchKind.ServerMutation,
            Town, "System.Int32");
        Add(activityLog, "AddNewRecruits", ImprovedGarrisonsPatchKind.ServerMutation,
            Town, "System.Int32", Settlement, "System.Boolean");
        Add(activityLog, "AddNewUpgrades", ImprovedGarrisonsPatchKind.ServerMutation,
            Town, CharacterObject, CharacterObject, "System.Int32");
        Add(activityLog, "AddNewPrisonerTurnover", ImprovedGarrisonsPatchKind.ServerMutation,
            Town, CharacterObject, "System.Int32");
        Add(activityLog, "AddPartyCreationActivity", ImprovedGarrisonsPatchKind.ServerMutation,
            Town, MobileParty);
        Add(activityLog, "AddPartyMergedWithGarrisonActivity", ImprovedGarrisonsPatchKind.ServerMutation,
            Town, MobileParty);

        Add(partyBehavior, "OnPartyDestroyed", ImprovedGarrisonsPatchKind.ServerMutation, MobileParty, PartyBase);
        Add(partyBehavior, "OnPartyRemoved", ImprovedGarrisonsPatchKind.ServerMutation, PartyBase);
        Add(partyBehavior, "OnPartyEnteredSettlement", ImprovedGarrisonsPatchKind.ServerMutation, MobileParty, Settlement, Hero);
        Add(partyBehavior, "OnSettlementOwnerChanged", ImprovedGarrisonsPatchKind.ServerMutation, Settlement, "System.Boolean", Hero, Hero, Hero,
            "TaleWorlds.CampaignSystem.Actions.ChangeOwnerOfSettlementAction+ChangeOwnerOfSettlementDetail");
        Add(partyBehavior, "OnMapEventStarted", ImprovedGarrisonsPatchKind.ServerMutation, MapEvent, PartyBase, PartyBase);
        Add(partyBehavior, "SellPrisoners", ImprovedGarrisonsPatchKind.ServerMutation, MobileParty, Town);
        Add(partyBehavior, "PutPrisonersIntoDungeon", ImprovedGarrisonsPatchKind.ServerMutation, MobileParty, Town);
        Add(partyBehavior, "TransferTroopsFromPartyToParty", ImprovedGarrisonsPatchKind.ServerMutation,
            MobileParty, "System.Collections.Generic.List`1", PartyBase);
        Add(partyBehavior, "RemovePartyHelper", ImprovedGarrisonsPatchKind.ServerMutation, MobileParty);
        Add(partyManager, "GivePartyFood", ImprovedGarrisonsPatchKind.ServerMutation, MobileParty);
        Add(partyManager, "RecruitMobilePartyToGarrison", ImprovedGarrisonsPatchKind.ServerMutation, MobileParty, Settlement, TroopRoster);

        Add(recruitment, "CheatSpawnUnitInAllGarrisons", ImprovedGarrisonsPatchKind.ServerMutation, "System.Int32");
        Add(recruitment, "CheatSpawnUnitInGarrison", ImprovedGarrisonsPatchKind.ServerMutation, CharacterObject, "System.Int32", Settlement);
        Add(recruitment, "RecruitPrisonerToGarrison", ImprovedGarrisonsPatchKind.ServerMutation, CharacterObject, "System.Int32", Settlement);
        Add(recruitment, "RecruitSurroundingForAllSettlements", ImprovedGarrisonsPatchKind.ServerMutation);
        Add(recruitment, "RecruitFromSurroundingVillages", ImprovedGarrisonsPatchKind.ServerMutation, Settlement);
        Add(recruitment, "TryRecruitAllPrisoners", ImprovedGarrisonsPatchKind.ServerMutation);
        Add(recruitment, "RecruitPrisonersInSettlement", ImprovedGarrisonsPatchKind.ServerMutation, Settlement);
        Add(upgrade, "GiveExpToAllGarrisons", ImprovedGarrisonsPatchKind.ServerMutation);
        Add(upgrade, "GiveGarrisonExp", ImprovedGarrisonsPatchKind.ServerMutation, Settlement);

        Add(partyManager, "InitializeNewParty", ImprovedGarrisonsPatchKind.StablePartyIdentity,
            "System.String", "TaleWorlds.Localization.TextObject", Settlement, Settlement);

        // Improved Garrisons normally derives a party's home by parsing its localized StringId.
        // Stable co-op IDs use the native HomeSettlement instead.
        Add(mobileManager, "GetMobileGarrisonHome", ImprovedGarrisonsPatchKind.PartyHomeResolver, MobileParty);
        Add(recruiterManager, "GetRecruiterPartyHome", ImprovedGarrisonsPatchKind.PartyHomeResolver, MobileParty);
        Add(transferManager, "GetTransferPartyHome", ImprovedGarrisonsPatchKind.PartyHomeResolver, MobileParty);
        Add(villageManager, "GetVillageFromMobileParty", ImprovedGarrisonsPatchKind.VillagePartySpawnResolver, MobileParty);
        Add(villageManager, "GetMobilePartyFromVillage", ImprovedGarrisonsPatchKind.VillagePartyLookup, Village);

        // Replace the single-player MainHero ownership tests with the Coop player registry.
        Add(behavior, "GetTownSettings", ImprovedGarrisonsPatchKind.PlayerTownSettings, Town);
        Add(behavior, "InitializeSettlements", ImprovedGarrisonsPatchKind.PlayerSettlementInitialization);
        Add(behavior, "onSettlementOwnerChanged", ImprovedGarrisonsPatchKind.PlayerSettlementOwnerChanged,
            Settlement, "System.Boolean", Hero, Hero, Hero,
            "TaleWorlds.CampaignSystem.Actions.ChangeOwnerOfSettlementAction+ChangeOwnerOfSettlementDetail");

        Add("ImprovedGarrisons.Models.GarrisonCostModel", "CalculateClanExpenses", ImprovedGarrisonsPatchKind.FinanceRead,
            "TaleWorlds.CampaignSystem.Clan", "System.Boolean", "System.Boolean", "System.Boolean");
        Add("ImprovedGarrisons.Models.GarrisonCostModel", "CalculateClanGoldChange", ImprovedGarrisonsPatchKind.FinanceRead,
            "TaleWorlds.CampaignSystem.Clan", "System.Boolean", "System.Boolean", "System.Boolean");

        AddPersistence(result);
        AddRoutedSettings(result);
        AddDeniedUi(result);
        return result;
    }

    private static void AddPersistence(List<ImprovedGarrisonsMethodSpec> result)
    {
        void Add(string type, string method, params string[] args) => result.Add(
            new ImprovedGarrisonsMethodSpec(type, method, ImprovedGarrisonsPatchKind.ServerPersistence, args));

        Add("ImprovedGarrisons.SaveSystem.SaveBehavior", "OnBeforeSaveEvent");
        Add("ImprovedGarrisons.SaveSystem.SaveBehavior", "OnAfterSaveEvent", "System.Boolean", "System.String");
        Add("ImprovedGarrisons.SaveSystem.SaveBehavior", "OnSaveEvent", "System.Boolean", "System.String");
        Add("ImprovedGarrisons.SaveSystem.SaveBehavior", "OnLoadEvent", "TaleWorlds.CampaignSystem.CampaignGameStarter");
        Add("ImprovedGarrisons.SaveSystem.SaveSystemManager", "SaveSettlementSaveData", "System.String");
        Add("ImprovedGarrisons.SaveSystem.SaveSystemManager", "SaveGlobalSettings");
        Add("ImprovedGarrisons.SaveSystem.SaveSystemManager", "LoadSettlementSaveDataAndGlobalSettings", "TaleWorlds.CampaignSystem.CampaignGameStarter");
        Add("ImprovedGarrisons.SaveSystem.SaveSystemManager", "DeleteUnnecessarySaveAndConfigFiles");
        result.Add(new ImprovedGarrisonsMethodSpec(
            "ImprovedGarrisons.SaveSystem.Configuration.ConfigManager", "ReadConfigForCurrentGame",
            ImprovedGarrisonsPatchKind.ClientDefaultConfig));
        result.Add(new ImprovedGarrisonsMethodSpec(
            "ImprovedGarrisons.SaveSystem.Configuration.ConfigManager", "ReadConfig",
            ImprovedGarrisonsPatchKind.ClientDefaultConfig,
            "ImprovedGarrisons.SaveSystem.FilePaths.ConfigFilePath"));
        Add("ImprovedGarrisons.SaveSystem.Configuration.ConfigManager", "CreateAndUpdateConfigForCurrentGame");
        Add("ImprovedGarrisons.SaveSystem.Configuration.ConfigManager", "CreateAndUpdateConfig", "System.String");
        Add("ImprovedGarrisons.SaveSystem.Configuration.ConfigManager", "CreateAndUpdateConfig",
            "ImprovedGarrisons.SaveSystem.FilePaths.ConfigFilePath");
        Add("ImprovedGarrisons.SaveSystem.Configuration.ConfigManager", "DeleteConfig",
            "ImprovedGarrisons.SaveSystem.FilePaths.ConfigFilePath");

        const string writer = "ImprovedGarrisons.SaveSystem.SaveWriter.SaveWriterManager";
        Add(writer, "CreateAndUpdateSaveFile", "ImprovedGarrisons.SaveSystem.FilePaths.SettlementSaveFilePath");
        Add(writer, "CreateAndUpdateGlobalSettings");
        Add(writer, "DeleteSaveFile", "ImprovedGarrisons.SaveSystem.FilePaths.IGSaveFilePath");
        Add(writer, "LoadSaveFile", "ImprovedGarrisons.SaveSystem.FilePaths.IGSaveFilePath");
        Add(writer, "LoadGlobalSettings");
    }

    private static void AddRoutedSettings(List<ImprovedGarrisonsMethodSpec> result)
    {
        void Add(string type, string method, string valueType = null) => result.Add(
            new ImprovedGarrisonsMethodSpec(type, method, ImprovedGarrisonsPatchKind.RoutedSetting,
                valueType == null ? new[] { Town } : new[] { Town, valueType }));

        const string recruitment = "ImprovedGarrisons.SaveSystem.SaveData.DataManipulationManager.RecruitmentSettings";
        Add(recruitment, "SetRecruiterAmountToRecruit", "System.Int32");
        Add(recruitment, "SetRecruitmentThreshold", "System.Int32");
        Add(recruitment, "ToggleRecruitOnlyElite", "System.Boolean");
        Add(recruitment, "TogglePrisonerRecruitmentAboveThreshold", "System.Boolean");
        Add(recruitment, "TogglePrisonerRecruitment", "System.Boolean");
        Add(recruitment, "ToggleVanillaRecruitment", "System.Boolean");
        Add(recruitment, "ToggleRegionRecruitment", "System.Boolean");
        Add(recruitment, "ToggleRecruiterOnlyElites", "System.Boolean");
        Add(recruitment, "ToggleRecruiterBuyHorses", "System.Boolean");
        Add(recruitment, "TogglePrisonerRecruitmentIgnoresTemplate", "System.Boolean");

        const string training = "ImprovedGarrisons.SaveSystem.SaveData.DataManipulationManager.TrainingSettings";
        Add(training, "SetTownMaxUpgradeTier", "System.Int32");
        Add(training, "ToggleVanillaTraining", "System.Boolean");
        Add(training, "ToggleTraining", "System.Boolean");
        Add(training, "ToggleFollowTemplate", "System.Boolean");
        Add(training, "ToggleRemoveNonTemplateTroops", "System.Boolean");

        result.Add(new ImprovedGarrisonsMethodSpec(
            "ImprovedGarrisons.SaveSystem.GarrisonBehavior", "ResetTownSettings",
            ImprovedGarrisonsPatchKind.RoutedSetting, Town));
    }

    private static void AddDeniedUi(List<ImprovedGarrisonsMethodSpec> result)
    {
        void Add(string type, string method, params string[] args) => result.Add(
            new ImprovedGarrisonsMethodSpec(type, method, ImprovedGarrisonsPatchKind.DeniedClientUi, args));

        const string management = "ImprovedGarrisons.SaveSystem.SaveData.DataManipulationManager.ManagementSettings";
        Add(management, "PromptTransfer", Town);
        Add(management, "PromptCopyToSpecificTowns", Town);
        Add(management, "PromptCopyToAllTowns", Town);
        Add(management, "PromptCopyToAllCastles", Town);

        const string recruitment = "ImprovedGarrisons.SaveSystem.SaveData.DataManipulationManager.RecruitmentSettings";
        Add(recruitment, "PromptCreateRecruiter", Town);
        Add(recruitment, "PromptChangeRecruitmentCulture", Town);
        Add(recruitment, "ToggleRecruiterAutoSpawn", Town, "System.Boolean");
        Add(recruitment, "ReturnRecruiter", Town);

        const string training = "ImprovedGarrisons.SaveSystem.SaveData.DataManipulationManager.TrainingSettings";
        Add(training, "PromptCurrentTemplateManagement", Town, "ImprovedGarrisons.ImprovedGarrisonsUI.SubMenus.TrainingUIVM");
        Add(training, "PromptFilterForNewTroopsToAdd", Town, "ImprovedGarrisons.ImprovedGarrisonsUI.SubMenus.TrainingUIVM");
        Add(training, "RemoveUpgradeTarget", Town, CharacterObject, "ImprovedGarrisons.ImprovedGarrisonsUI.SubMenus.TrainingUIVM");
        Add(training, "ToggleAutoSpawn", Town, "System.Boolean");

        const string mobile = "ImprovedGarrisons.SaveSystem.SaveData.DataManipulationManager.MobileGarrisonSettings";
        Add(mobile, "PromptCreateMobileGarrison", Town);
        Add(mobile, "PromptMobileGarrisonEscort", Town);
        Add(mobile, "OrderMobileGarrisonAttackOrDefend", Town);
        Add(mobile, "OrderMobileGarrisonToPatrol", Town);
        Add(mobile, "OrderMobileGarrisonReturn", Town);
        Add(mobile, "SetReturnPercentage", Town, "System.Single");
        Add(mobile, "SetAutoGarrisonThreshold", Town, "System.Int32");
        Add(mobile, "SetAutoGarrisonSize", Town, "System.Int32");
        Add(mobile, "TogglePrisonerSell", Town, "System.Boolean");
        Add(mobile, "ToggleAutoGuards", Town, "System.Boolean");
        Add(mobile, "ToggleAutoGuardDefend", Town, "System.Boolean");
        Add(mobile, "TogglePrisonerRecruit", Town, "System.Boolean");
        Add(mobile, "ToggleUpgrade", Town, "System.Boolean");
        Add(mobile, "ToggleReplenish", Town, "System.Boolean");
        Add(mobile, "ToggleDestroyHideout", Town, "System.Boolean");
        Add(mobile, "ToggleHorseBuy", Town, "System.Boolean");
        Add("ImprovedGarrisons.SaveSystem.SaveData.DataManipulationManager.TemplateManager",
            "PromptTemplateManager", Town, "ImprovedGarrisons.ImprovedGarrisonsUI.SubMenus.TrainingUIVM");
    }
}
