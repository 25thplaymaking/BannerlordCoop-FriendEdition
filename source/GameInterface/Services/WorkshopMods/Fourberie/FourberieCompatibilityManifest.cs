using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml;

namespace GameInterface.Services.WorkshopMods.Fourberie;

internal enum FourberiePatchKind
{
    ServerOnly,
    ServerTick,
    ServerMutation,
    ClientPresentation,
    FinanceRead,

    /// <summary>
    /// Runs Fourberie's pinned initializer, including its eight behaviors and fourteen decorator
    /// models. Behavior ticks and mutations remain gated by their server callbacks; model
    /// calculations are deterministic policies, with Separatism's loyalty thresholds composed.
    /// </summary>
    BehaviorsAndModels,

    ClientOperationPresentation,
    EnlistPartyConsequence,
    EnlistLadsConsequence,
    EnslavePrisonersConsequence,
    RecruitBanditsConsequence,
    InsuranceScamConsequence,
    BusinessStartConsequence,
    BusinessUpgradeConsequence,
    BusinessDowngradeConsequence,
    SchemeBonusUpgradeConsequence,
    SchemeBonusDowngradeConsequence,
    SchemeBonusResetConsequence,
    AgentPartyCreateConsequence,
    AgentPartyDisbandConsequence,
    AgentPartySelectionConsequence,
    CrimeBaseResetConsequence,
    RoleSelectionPresentation,
    RoleAssignmentConsequence,
    RoleRemovalConsequence,
    SchemeVictimConsequence,
    SchemeTypeConsequence,
    SchemeLifecycleConsequence,
    SchemeOwnedReplacement,
    SchemeStanceConsequence,
    ClientRoleRefresh,
    CorruptionLevelConsequence,
    CrimeRoomSliderConsequence,
    ClientCrimeRoomRead,
    ContractConsequence,
    ClientSchemeFilter,
    MainBaseConsequence,
    TerritorySelectionConsequence,
    TerritoryAbandonConsequence,
    SafehouseAbandonConsequence,
    SafehouseEstablishmentConsequence,
    SafehouseTraderConsequence,
    SafehouseWaitConsequence,
    SafehouseReturnLifecycle,
    GrudgeSelectionConsequence,
    GrudgeSettlementConsequence,
    ContractTickReplacement,
    ContractProposalLegacyConsequence,
    InsideMissionOutcome,
    FightClubOutcome,
    FightClubMissionLocal,
    FightClubFame,
    FightClubAdmission,
    FightClubEnrollment,
    FightClubPatronRefusal,
    FightClubStableOwnership,
    FightClubStableRecruitment,
    FightClubMenuRefresh,
    FightClubPatronPayment,
    AlleyAcquisition,
    AlleyClear,
    SchemeRoomOpen,
    DominanceCondition,
    StealthMissionLocal,
    StealthHit,
    StealthMissionEnd,
    StealthMilitiaPayment,
    StealthMilitiaChoice,
    StealthAbortContract,
    StealthAlertConsequence,
    StealthAnswer,
    StealthAgentRemoved,
    StealthAlarm,
    StealthScandalSuccess,
    StealthPrisonSuccess,
    BanditConsequence,
    BanditDonationConsequence,
    BanditRosterOpen,
    BanditRosterConsequence,
    BanditPreparation,
    LegacyCallback,
    ConversationConsequence,
    CampaignConsequence,
    MinorRecruitmentConsequence,
    KingdomLeaveConsequence,
    GuardKillConsequence,
    SafehouseEncounterConsequence,
    CriminalConsequence,
    MissionLocal,
    MissionInitialization,
    SeparatismLoyaltyComposition,

    /// <summary>
    /// Replaces <c>Main.OnGameInitializationFinished</c>: the server runs only its
    /// <c>StringDicoHelper.RefreshHeroDico()</c> call and replicates those dictionaries to clients.
    /// Compatibility validation remains owned by this exact-binary adapter.
    /// </summary>
    RefreshHeroDicoOnly,
}

internal sealed class FourberieMethodSpec
{
    public FourberieMethodSpec(int metadataToken, FourberiePatchKind kind)
    {
        MetadataToken = metadataToken;
        Kind = kind;
        TypeName = string.Empty;
        MethodName = string.Empty;
        ReturnTypeName = string.Empty;
        ParameterTypeNames = Array.Empty<string>();
    }

    public FourberieMethodSpec(
        string typeName,
        string methodName,
        string returnTypeName,
        FourberiePatchKind kind,
        params string[] parameterTypeNames)
    {
        TypeName = typeName;
        MethodName = methodName;
        ReturnTypeName = returnTypeName;
        Kind = kind;
        ParameterTypeNames = parameterTypeNames ?? Array.Empty<string>();
    }

    public string TypeName { get; }
    public string MethodName { get; }
    public string ReturnTypeName { get; }
    public FourberiePatchKind Kind { get; }
    public string[] ParameterTypeNames { get; }
    public int? MetadataToken { get; }

    public string Key => MetadataToken.HasValue
        ? $"token 0x{MetadataToken.Value:X8}"
        : $"{TypeName}::{MethodName}({string.Join(",", ParameterTypeNames)}):{ReturnTypeName}";

    public MethodInfo Resolve(Assembly assembly)
    {
        if (MetadataToken.HasValue)
            return assembly?.ManifestModule.ResolveMethod(MetadataToken.Value) as MethodInfo;
        var type = assembly?.GetType(TypeName, throwOnError: false, ignoreCase: false);
        if (type == null) return null;

        return type
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(method =>
                string.Equals(method.Name, MethodName, StringComparison.Ordinal) &&
                string.Equals(CanonicalTypeName(method.ReturnType), ReturnTypeName, StringComparison.Ordinal) &&
                ParametersMatch(method));
    }

    private bool ParametersMatch(MethodInfo method)
    {
        var parameters = method.GetParameters();
        if (parameters.Length != ParameterTypeNames.Length) return false;

        for (var index = 0; index < parameters.Length; index++)
        {
            if (!string.Equals(
                    CanonicalTypeName(parameters[index].ParameterType),
                    ParameterTypeNames[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal static string CanonicalTypeName(Type type)
    {
        if (type == null) return string.Empty;
        if (type.IsByRef) return CanonicalTypeName(type.GetElementType()) + "&";
        if (type.IsArray) return CanonicalTypeName(type.GetElementType()) + "[]";
        if (!type.IsGenericType) return type.FullName ?? type.Name;

        var definition = type.GetGenericTypeDefinition().FullName;
        var arguments = string.Join(",", type.GetGenericArguments().Select(CanonicalTypeName));
        return definition + "[" + arguments + "]";
    }
}

/// <summary>
/// Compatibility contract for the creator-authorized Fourberie 1.4.7.6 binary. File identity and
/// every method we detour must match before any compatibility patch is installed. An upstream
/// update therefore cannot silently inherit guards written for a different implementation.
/// </summary>
internal static class FourberieCompatibilityManifest
{
    internal const int CampaignReadyWorkshopConsequenceToken = 0x060005DB;

    public const string AssemblyName = "Fourberie";
    public const string ModuleId = "Fourberie";
    public const string WorkshopId = "2875710877";
    public const string SupportedModuleVersion = "v1.4.7.6";
    public const string SupportedAssemblyVersion = "1.4.7.6";
    public const string SupportedSha256 = "29F6644BCCA8D5A3834EE51C72EC75D94214BEB76CB1FDCBC2027DA0EB544E92";
    public const string AdapterVersion = "1";
    public const string AdapterHarmonyId = "Bannerlord.Coop.Workshop.Fourberie";
    public const bool ApprovedBinaryDeclaresHarmonySurface = false;

    internal static readonly IReadOnlyList<string> BehaviorTypeNames = new[]
    {
        "Fourberie.FourberieBehavior",
        "Fourberie.FourbSafeHouseBehavior",
        "Fourberie.FourbEscapeBehavior",
        "Fourberie.FourbFightClubBehavior",
        "Fourberie.FourbBanditBehavior",
        "Fourberie.FourbRecruitableBehavior",
        "Fourberie.FourbContactMenu",
        "Fourberie.FourbContractBehavior",
    };

    private const string Void = "System.Void";
    private const string IDataStore = "TaleWorlds.CampaignSystem.IDataStore";
    private const string Settlement = "TaleWorlds.CampaignSystem.Settlements.Settlement";
    private const string Hero = "TaleWorlds.CampaignSystem.Hero";
    private const string MobileParty = "TaleWorlds.CampaignSystem.Party.MobileParty";
    private const string Clan = "TaleWorlds.CampaignSystem.Clan";
    private const string Kingdom = "TaleWorlds.CampaignSystem.Kingdom";
    private const string PartyBase = "TaleWorlds.CampaignSystem.Party.PartyBase";
    private const string MapEvent = "TaleWorlds.CampaignSystem.MapEvents.MapEvent";
    private const string IFaction = "TaleWorlds.CampaignSystem.IFaction";
    private const string BattleSide = "TaleWorlds.Core.BattleSideEnum";

    public static readonly IReadOnlyList<FourberieMethodSpec> Methods = BuildMethods();

    internal static bool RequiresCampaignAtPatchTime(FourberieMethodSpec spec) =>
        spec?.MetadataToken == CampaignReadyWorkshopConsequenceToken;

    public static bool TryValidate(
        Assembly assembly,
        out IReadOnlyDictionary<FourberieMethodSpec, MethodInfo> resolved,
        out string failure)
    {
        resolved = null;
        failure = null;

        if (assembly == null ||
            !string.Equals(assembly.GetName().Name, AssemblyName, StringComparison.Ordinal))
        {
            failure = "Fourberie assembly is not loaded";
            return false;
        }

        var assemblyVersion = assembly.GetName().Version?.ToString();
        if (!string.Equals(assemblyVersion, SupportedAssemblyVersion, StringComparison.Ordinal))
        {
            failure = $"unsupported Fourberie assembly version {assemblyVersion ?? "missing"}";
            return false;
        }

        if (!TryReadIdentity(assembly.Location, out var moduleVersion, out var sha256, out failure))
            return false;

        if (!IsSupportedIdentity(moduleVersion, sha256))
        {
            failure = $"unsupported Fourberie identity (module={moduleVersion ?? "missing"}, sha256={sha256 ?? "missing"})";
            return false;
        }

        var methods = new Dictionary<FourberieMethodSpec, MethodInfo>();
        var originals = new Dictionary<MethodInfo, FourberieMethodSpec>();
        foreach (var spec in Methods)
        {
            MethodInfo method;
            try
            {
                method = spec.Resolve(assembly);
            }
            catch (InvalidOperationException)
            {
                failure = $"ambiguous audited Fourberie method {spec.Key}";
                return false;
            }

            if (method == null)
            {
                failure = $"missing audited Fourberie method {spec.Key}";
                return false;
            }

            if (originals.TryGetValue(method, out var existing))
            {
                failure = $"duplicate audited Fourberie method {existing.Key} and {spec.Key}";
                return false;
            }

            methods.Add(spec, method);
            originals.Add(method, spec);
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
                failure = "Fourberie.dll has no readable on-disk location";
                return false;
            }

            using (var stream = File.OpenRead(assemblyPath))
            using (var hash = SHA256.Create())
                sha256 = ToHex(hash.ComputeHash(stream));

            var moduleRoot = Directory.GetParent(assemblyPath)?.Parent?.Parent?.FullName;
            var manifestPath = moduleRoot == null ? null : Path.Combine(moduleRoot, "SubModule.xml");
            if (manifestPath == null || !File.Exists(manifestPath))
            {
                failure = "Fourberie SubModule.xml was not found beside the binary";
                return false;
            }

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(manifestPath, settings))
                document.Load(reader);

            var id = document.SelectSingleNode("/Module/Id")?.Attributes?["value"]?.Value;
            moduleVersion = document.SelectSingleNode("/Module/Version")?.Attributes?["value"]?.Value;
            if (!string.Equals(id, ModuleId, StringComparison.Ordinal))
            {
                failure = $"unexpected Fourberie module id {id ?? "missing"}";
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            failure = $"could not verify Fourberie: {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static string ToHex(byte[] bytes)
    {
        const string digits = "0123456789ABCDEF";
        var characters = new char[bytes.Length * 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            characters[index * 2] = digits[bytes[index] >> 4];
            characters[(index * 2) + 1] = digits[bytes[index] & 15];
        }
        return new string(characters);
    }

    private static IReadOnlyList<FourberieMethodSpec> BuildMethods()
    {
        var methods = new List<FourberieMethodSpec>();
        void Add(string type, string method, FourberiePatchKind kind, params string[] parameters) =>
            methods.Add(new FourberieMethodSpec(type, method, Void, kind, parameters));
        void AddReturning(string type, string method, string returnType, FourberiePatchKind kind,
            params string[] parameters) =>
            methods.Add(new FourberieMethodSpec(type, method, returnType, kind, parameters));
        void AddTokens(FourberiePatchKind kind, params int[] tokens)
        {
            foreach (int token in tokens) methods.Add(new FourberieMethodSpec(token, kind));
        }

        foreach (var behavior in BehaviorTypeNames)
        {
            // RegisterEvents runs on BOTH roles so the behaviors wire up their client-facing game
            // menus (added via OnSessionLaunched) — otherwise the mod's content is invisible on a
            // client. This is safe because the actual state-changing callbacks the listeners fire
            // are separately guarded below (HourlyTick/DailyTick/… = ServerTick, settlement events
            // = ServerMutation), so a client registration cannot mutate authoritative state.
            // SyncData stays server-only: the client has no Fourberie models/save graph.
            Add(behavior, "SyncData", FourberiePatchKind.ServerOnly, IDataStore);
        }

        // Fourberie's monolithic initializer installs its eight behaviors and fourteen decorator
        // models. Callback guards below own mutations, and the runtime gate validates the exact
        // decorator set instead of silently dropping the mod's policy formulas.
        Add("Fourberie.Main", "InitializeCampaignBehaviors", FourberiePatchKind.BehaviorsAndModels,
            "TaleWorlds.Core.IGameStarter");

        // These original routines use MainHero/MainParty and private static selection state. The
        // presentation halves remain local, while exact selection consequences become typed
        // requests and server-side explicit-context transactions.
        const string TroopRoster = "TaleWorlds.CampaignSystem.Roster.TroopRoster";
        const string FlattenedTroopRoster = "TaleWorlds.CampaignSystem.Roster.FlattenedTroopRoster";
        // These VM methods only build/switch client UI, browse selections, or open TaleWorlds
        // screens. Their writes target Fourberie's temporary presentation caches rather than the
        // campaign save graph. Keep them available on clients and suppress them on the server.
        Add("Fourberie.CriminalVM", "OpenSub", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM", "FOpenStash", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM", "RespecPerks", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM", "AgentsList", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM", "HintSchemeRoutine", FourberiePatchKind.ClientPresentation,
            "System.Int32");
        Add("Fourberie.CriminalVM", "ListKingTribF", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM", "ListToTownNet", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM", "ListPartnerF", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM", "ListCrimPactF", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM", "ListKCrimPactF", FourberiePatchKind.ClientPresentation);

        Add("Fourberie.CriminalVM", "AgentsEnlistRoutine", FourberiePatchKind.ClientOperationPresentation, "System.Int32");
        AddReturning("Fourberie.CriminalVM", "EnlistFromPartyDone", "System.Boolean",
            FourberiePatchKind.EnlistPartyConsequence,
            TroopRoster, TroopRoster, TroopRoster, TroopRoster,
            FlattenedTroopRoster, FlattenedTroopRoster, "System.Boolean", PartyBase, PartyBase);
        Add("Fourberie.CriminalVM", "EnlistFromLadsDone", FourberiePatchKind.EnlistLadsConsequence,
            PartyBase, TroopRoster, TroopRoster, PartyBase, TroopRoster, TroopRoster, "System.Boolean");
        // Both public enlist completion entry points above submit the complete roster transaction.
        // The original helper mutates the static crime pool and is therefore never allowed to run
        // as a second, implicit consequence on either role.
        Add("Fourberie.CriminalVM", "EnlistAgentsDoneRoutine",
            FourberiePatchKind.SchemeOwnedReplacement, TroopRoster);
        AddReturning("Fourberie.FourberieBehavior", "OnDoneEnslaved", "System.Boolean",
            FourberiePatchKind.EnslavePrisonersConsequence,
            TroopRoster, TroopRoster, TroopRoster, TroopRoster,
            FlattenedTroopRoster, FlattenedTroopRoster, "System.Boolean", PartyBase, PartyBase);
        Add("Fourberie.FourbBanditBehavior", "FourbRecruitBandit",
            FourberiePatchKind.ClientOperationPresentation, "System.Int32");
        AddReturning("Fourberie.FourbBanditBehavior", "RecruitLadsOnDoneClicked", "System.Boolean",
            FourberiePatchKind.RecruitBanditsConsequence,
            TroopRoster, TroopRoster, TroopRoster, TroopRoster,
            FlattenedTroopRoster, FlattenedTroopRoster, "System.Boolean", PartyBase, PartyBase);
        Add("Fourberie.HelperSubInsuScam", "SpawnBandits", FourberiePatchKind.ServerOnly, "System.Int32");
        Add("Fourberie.HelperSubInsuScam+<>c__DisplayClass0_0", "<Menu>b__4",
            FourberiePatchKind.InsuranceScamConsequence);
        Add("Fourberie.CriminalVM+<>c", "<UpSmugglers>b__12_0",
            FourberiePatchKind.BusinessStartConsequence);
        Add("Fourberie.CriminalVM+<>c__DisplayClass16_0", "<UpWorkers>b__0",
            FourberiePatchKind.BusinessStartConsequence);
        Add("Fourberie.CriminalVM+<>c__DisplayClass28_0", "<UpServants>b__0",
            FourberiePatchKind.BusinessStartConsequence);
        Add("Fourberie.FourberieBehavior", "BizUpgrades",
            FourberiePatchKind.BusinessUpgradeConsequence,
            "System.Int32", "System.Int32", "System.Int32");
        Add("Fourberie.FourberieBehavior", "BizDowngrades",
            FourberiePatchKind.BusinessDowngradeConsequence,
            "System.Int32", "System.Int32");
        Add("Fourberie.FourberieBehavior", "BonUpg",
            FourberiePatchKind.SchemeBonusUpgradeConsequence, "System.Int32");
        Add("Fourberie.FourberieBehavior", "BonDowng",
            FourberiePatchKind.SchemeBonusDowngradeConsequence, "System.Int32");
        Add("Fourberie.CriminalVM", "SchBonus1Re",
            FourberiePatchKind.SchemeBonusResetConsequence);
        Add("Fourberie.CriminalVM", "SchBonus2Re",
            FourberiePatchKind.SchemeBonusResetConsequence);
        Add("Fourberie.CriminalVM", "PackAgents",
            FourberiePatchKind.AgentPartyCreateConsequence);
        Add("Fourberie.CriminalVM", "UnpackAgents",
            FourberiePatchKind.AgentPartyDisbandConsequence);
        Add("Fourberie.CriminalVM+<>c__DisplayClass35_0", "<AgentsList>b__0",
            FourberiePatchKind.AgentPartySelectionConsequence,
            "System.Collections.Generic.List`1[TaleWorlds.Core.InquiryElement]");
        Add("Fourberie.CriminalVM+<>c", "<FRefrparty>b__22_0",
            FourberiePatchKind.CrimeBaseResetConsequence);
        Add("Fourberie.CriminalVM", "ButAssign1", FourberiePatchKind.RoleSelectionPresentation);
        Add("Fourberie.CriminalVM", "ButRemove1", FourberiePatchKind.RoleSelectionPresentation);
        Add("Fourberie.CriminalVM", "ButAssign4", FourberiePatchKind.RoleSelectionPresentation);
        Add("Fourberie.CriminalVM", "ButRemove4", FourberiePatchKind.RoleSelectionPresentation);
        Add("Fourberie.FourberieBehavior", "Comparole",
            FourberiePatchKind.RoleSelectionPresentation, "System.String");
        Add("Fourberie.FourberieBehavior", "RemoveCompa",
            FourberiePatchKind.RoleRemovalConsequence, "System.String");
        Add("Fourberie.FourberieBehavior+<>c__DisplayClass84_0", "<Comparole>b__0",
            FourberiePatchKind.RoleAssignmentConsequence,
            "System.Collections.Generic.List`1[TaleWorlds.Core.InquiryElement]");
        Add("Fourberie.CriminalVM", "RefreshValues", FourberiePatchKind.ClientRoleRefresh);
        Add("Fourberie.CriminalVM", "Close2", FourberiePatchKind.ClientRoleRefresh);
        Add("Fourberie.CriminalVM", "CorruptSelect", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM+<>c", "<CorruptSelect>b__33_0",
            FourberiePatchKind.CorruptionLevelConsequence,
            "System.Collections.Generic.List`1[TaleWorlds.Core.InquiryElement]");
        Add("Fourberie.CriminalVM", "set_UpgradeSlideBar",
            FourberiePatchKind.CrimeRoomSliderConsequence, "System.Int32");
        Add("Fourberie.CriminalVM", "set_LadsDutySlideBar",
            FourberiePatchKind.CrimeRoomSliderConsequence, "System.Int32");
        Add("Fourberie.CriminalVM", "set_SlavesDutySlideBar",
            FourberiePatchKind.CrimeRoomSliderConsequence, "System.Int32");
        AddReturning("Fourberie.CriminalVM", "get_UpgradeSlideBar", "System.Int32",
            FourberiePatchKind.ClientCrimeRoomRead);
        Add("Fourberie.CriminalVM", "UpInvestHint", FourberiePatchKind.ClientCrimeRoomRead);
        Add("Fourberie.CriminalVM", "FContractCom", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM+<>c", "<FContractCom>b__151_0",
            FourberiePatchKind.ContractConsequence);
        Add("Fourberie.CriminalVM+<>c", "<FContractCom>b__151_3",
            FourberiePatchKind.ContractConsequence);
        Add("Fourberie.CriminalVM+<>c", "<FContractCom>b__151_5",
            FourberiePatchKind.ContractConsequence);
        Add("Fourberie.FourbContractBehavior", "ContractAborted", FourberiePatchKind.ServerOnly,
            "System.Boolean", "System.Int32");
        Add("Fourberie.FourbContractBehavior", "ContractComplete", FourberiePatchKind.ServerOnly,
            Clan, "System.Int32");
        AddReturning("Fourberie.FourbContractBehavior", "fb_contract_hint", "System.Boolean",
            FourberiePatchKind.ClientPresentation, "TaleWorlds.Localization.TextObject&");
        Add("Fourberie.FourbContractBehavior+<>c", "<AddGameMenus>b__5_0",
            FourberiePatchKind.ContractProposalLegacyConsequence);
        Add("Fourberie.FourbContractBehavior+<>c", "<AddGameMenus>b__5_1",
            FourberiePatchKind.ContractProposalLegacyConsequence);
        const string InquiryElements = "System.Collections.Generic.List`1[TaleWorlds.Core.InquiryElement]";
        const string MenuCallbackArgs = "TaleWorlds.CampaignSystem.GameMenus.MenuCallbackArgs";
        Add("Fourberie.CriminalVM+<>c", "<KingdomFilter>b__148_1",
            FourberiePatchKind.ClientSchemeFilter, InquiryElements);
        Add("Fourberie.CriminalVM+<>c", "<ClanFilter>b__152_0",
            FourberiePatchKind.ClientSchemeFilter, InquiryElements);
        Add("Fourberie.CriminalVM+<>c", "<ClanFilter2>b__153_0",
            FourberiePatchKind.ClientSchemeFilter, InquiryElements);
        Add("Fourberie.CriminalVM+<>c", "<ListArmiesF>b__161_0",
            FourberiePatchKind.ClientSchemeFilter, InquiryElements);
        Add("Fourberie.CriminalVM+<>c", "<ListKPartiesF>b__162_0",
            FourberiePatchKind.ClientSchemeFilter, InquiryElements);
        Add("Fourberie.CriminalVM+<>c", "<KingFiefsF>b__166_0",
            FourberiePatchKind.ClientSchemeFilter, InquiryElements);
        Add("Fourberie.CriminalVM+<>c", "<ClanFiefsF>b__167_0",
            FourberiePatchKind.ClientSchemeFilter, InquiryElements);
        Add("Fourberie.CriminalVM+<>c", "<ClanFiefsF2>b__168_0",
            FourberiePatchKind.ClientSchemeFilter, InquiryElements);
        Add("Fourberie.CriminalVM+<>c", "<ClanPartiesF>b__170_0",
            FourberiePatchKind.ClientSchemeFilter, InquiryElements);
        Add("Fourberie.CriminalVM+<>c", "<ClanPartiesF2>b__171_0",
            FourberiePatchKind.ClientSchemeFilter, InquiryElements);
        Add("Fourberie.CriminalVM+<>c", "<ListKingPoliticsF>b__551_0",
            FourberiePatchKind.ClientSchemeFilter, InquiryElements);
        Add("Fourberie.CriminalVM", "<ListKingDiploF>b__552_2",
            FourberiePatchKind.ClientSchemeFilter, InquiryElements);
        Add("Fourberie.CriminalVM", "TerritoryMakeMainBase", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM+<>c", "<TerritoryMakeMainBase>b__160_0",
            FourberiePatchKind.MainBaseConsequence, InquiryElements);
        Add("Fourberie.CriminalVM", "ListTributeF", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM+<>c", "<ListTributeF>b__157_0",
            FourberiePatchKind.TerritorySelectionConsequence, InquiryElements);
        Add("Fourberie.CriminalVM+<>c__DisplayClass157_0", "<ListTributeF>b__2",
            FourberiePatchKind.TerritoryAbandonConsequence);
        Add("Fourberie.CriminalVM", "AbandonSafeClick", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM+<>c__DisplayClass544_0", "<AbandonSafeClick>b__0",
            FourberiePatchKind.SafehouseAbandonConsequence);
        Add("Fourberie.FourbSafeHouseBehavior", "<AddDialogsSafeHouse>b__9_1",
            FourberiePatchKind.SafehouseEstablishmentConsequence);
        Add("Fourberie.FourbSafeHouseBehavior+<>c", "<AddDialogsSafeHouse>b__9_18",
            FourberiePatchKind.SafehouseTraderConsequence);
        Add("Fourberie.FourbSafeHouseBehavior+<>c", "<AddDialogsSafeHouse>b__9_20",
            FourberiePatchKind.SafehouseTraderConsequence);
        Add("Fourberie.FourbSafeHouseBehavior+<>c", "<AddDialogsSafeHouse>b__9_22",
            FourberiePatchKind.SafehouseTraderConsequence);
        Add("Fourberie.FourbSafeHouseBehavior+<>c", "<AddDialogsSafeHouse>b__9_24",
            FourberiePatchKind.SafehouseTraderConsequence);
        Add("Fourberie.FourbSafeHouseBehavior+<>c", "<MenuSafeHouse>b__13_5",
            FourberiePatchKind.SafehouseWaitConsequence, MenuCallbackArgs);
        Add("Fourberie.FourbSafeHouseBehavior+<>c", "<MenuSafeHouse>b__13_7",
            FourberiePatchKind.SafehouseWaitConsequence, MenuCallbackArgs);
        Add("Fourberie.FourbSafeHouseBehavior", "SHOnGaMenOpened",
            FourberiePatchKind.SafehouseReturnLifecycle, MenuCallbackArgs);
        Add("Fourberie.CriminalVM", "ClanGruFdilter", FourberiePatchKind.ClientPresentation);
        AddReturning("Fourberie.CriminalVM", "CanPlayerPay",
            "System.ValueTuple`2[System.Boolean,System.String]", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM", "<ClanGruFdilter>b__149_0",
            FourberiePatchKind.GrudgeSelectionConsequence, InquiryElements);
        Add("Fourberie.CriminalVM+<>c__DisplayClass149_1", "<ClanGruFdilter>b__4",
            FourberiePatchKind.GrudgeSettlementConsequence);
        Add("Fourberie.CriminalVM", "Schemhero1", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM", "Schemhero2", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM+<>c__DisplayClass137_0", "<Schemhero1>b__0",
            FourberiePatchKind.SchemeVictimConsequence,
            "System.Collections.Generic.List`1[TaleWorlds.Core.InquiryElement]");
        Add("Fourberie.CriminalVM+<>c__DisplayClass138_0", "<Schemhero2>b__0",
            FourberiePatchKind.SchemeVictimConsequence,
            "System.Collections.Generic.List`1[TaleWorlds.Core.InquiryElement]");
        Add("Fourberie.CriminalVM", "Scheme1Sel", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM", "Scheme2Sel", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.FourberieBehavior", "SchemeSel", FourberiePatchKind.ClientPresentation,
            "System.Int32");
        Add("Fourberie.FourberieBehavior+<>c__DisplayClass102_0", "<SchemeSel>b__0",
            FourberiePatchKind.SchemeTypeConsequence,
            "System.Collections.Generic.List`1[TaleWorlds.Core.InquiryElement]");
        Add("Fourberie.CriminalVM", "SchemeAllBut", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM", "Scheme2AllBut", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM", "SchemeAllButtonRoutine",
            FourberiePatchKind.SchemeLifecycleConsequence, "System.Int32");
        Add("Fourberie.FourberieBehavior", "SchemeAction",
            FourberiePatchKind.SchemeOwnedReplacement,
            "System.Int32", "System.Int32", "System.Int32", "System.Int32", "System.Int32", "System.Int32");
        Add("Fourberie.FourberieBehavior", "AbortScheme",
            FourberiePatchKind.SchemeOwnedReplacement, "System.Int32");
        Add("Fourberie.CriminalVM+<>c__DisplayClass77_0", "<SchemeAllButtonRoutine>b__0",
            FourberiePatchKind.SchemeOwnedReplacement);
        Add("Fourberie.CriminalVM", "SchemeRoomStanceList", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM+<>c__DisplayClass562_0", "<SchemeRoomStanceList>b__0",
            FourberiePatchKind.SchemeStanceConsequence,
            "System.Collections.Generic.List`1[TaleWorlds.Core.InquiryElement]");

        // These periodic entry points contain the random and persistent campaign decisions found
        // in the 1.4.7.6 audit. They are separately guarded so a duplicate listener cannot execute
        // the same operation twice at the same campaign tick.
        Add("Fourberie.FourberieBehavior", "HourlyTick", FourberiePatchKind.ServerTick);
        Add("Fourberie.FourberieBehavior", "DailyTick", FourberiePatchKind.ServerTick);
        Add("Fourberie.FourberieBehavior", "WeeklyTick", FourberiePatchKind.ServerTick);
        Add("Fourberie.FourberieBehavior", "DailyTickSet", FourberiePatchKind.ServerTick, Settlement);
        Add("Fourberie.FourberieBehavior", "DailyTickHero", FourberiePatchKind.ServerTick, Hero);

        Add("Fourberie.FourbBanditBehavior", "BanditHourlyTick", FourberiePatchKind.ServerTick);
        Add("Fourberie.FourbBanditBehavior", "BanditWeeklyTick", FourberiePatchKind.ServerTick);
        Add("Fourberie.FourbBanditBehavior", "BanditDailTick", FourberiePatchKind.ServerTick);
        Add("Fourberie.FourbBanditBehavior", "FOnDailyTickParty", FourberiePatchKind.ServerTick, MobileParty);
        Add("Fourberie.FourbBanditBehavior", "FOnDailyTickSettlement", FourberiePatchKind.ServerTick, Settlement);

        Add("Fourberie.FourbContractBehavior", "HourlyTick", FourberiePatchKind.ContractTickReplacement);
        Add("Fourberie.FourbContractBehavior", "DailyTickClan", FourberiePatchKind.ServerTick, Clan);
        Add("Fourberie.FourbFightClubBehavior", "PitWeeklyTick", FourberiePatchKind.ServerTick);
        Add("Fourberie.FourbFightClubBehavior", "PitDailyTickHero", FourberiePatchKind.ServerTick, Hero);
        Add("Fourberie.FourbSafeHouseBehavior", "SHSHourlyTickF", FourberiePatchKind.ServerTick);
        Add("Fourberie.FourbSafeHouseBehavior", "SHDailyTickF", FourberiePatchKind.ServerTick);

        // Event listeners can already have been registered if the external module loads before
        // Coop. Guard the concrete callbacks as well as RegisterEvents, then publish changed static
        // state after the authoritative invocation.
        Add("Fourberie.FourberieBehavior", "OnSettlementEntered", FourberiePatchKind.ServerMutation,
            MobileParty, Settlement, Hero);
        Add("Fourberie.FourberieBehavior", "OnSettlementLeft", FourberiePatchKind.ServerMutation,
            MobileParty, Settlement);
        Add("Fourberie.FourberieBehavior", "FOnMobilePartyDestroyed", FourberiePatchKind.ServerMutation,
            MobileParty, PartyBase);
        Add("Fourberie.FourberieBehavior", "FMapEventEnded", FourberiePatchKind.ServerMutation, MapEvent);
        Add("Fourberie.FourberieBehavior", "DataDelete", FourberiePatchKind.ServerMutation,
            "TaleWorlds.CampaignSystem.CampaignGameStarter");
        Add("Fourberie.FourberieBehavior", "DataDeleteEx", FourberiePatchKind.ServerMutation);
        Add("Fourberie.FourberieBehavior", "FOnheroKilled", FourberiePatchKind.ServerMutation,
            Hero, Hero, "TaleWorlds.CampaignSystem.Actions.KillCharacterAction+KillCharacterActionDetail", "System.Boolean");
        Add("Fourberie.FourberieBehavior", "HeroBecomePrisoner", FourberiePatchKind.ServerMutation,
            PartyBase, Hero);
        Add("Fourberie.FourberieBehavior", "HideoutDeactivated", FourberiePatchKind.ServerMutation, Settlement);
        Add("Fourberie.FourberieBehavior", "FOnClanChanged", FourberiePatchKind.ServerMutation, Hero, Clan);
        Add("Fourberie.FourberieBehavior", "OnClanDestroyed", FourberiePatchKind.ServerMutation, Clan);
        Add("Fourberie.FourberieBehavior", "OnKingdomDestroyed", FourberiePatchKind.ServerMutation, Kingdom);
        Add("Fourberie.FourberieBehavior", "OnAlleyClearedByPlayer", FourberiePatchKind.ServerMutation,
            "TaleWorlds.CampaignSystem.Settlements.Alley");
        Add("Fourberie.FourberieBehavior", "OnAlleyOccupiedByPlayer", FourberiePatchKind.ServerMutation,
            "TaleWorlds.CampaignSystem.Settlements.Alley", "TaleWorlds.CampaignSystem.Roster.TroopRoster");
        Add("Fourberie.FourberieBehavior", "FOnForceSupplies", FourberiePatchKind.ServerMutation,
            BattleSide, "TaleWorlds.CampaignSystem.MapEvents.ForceSuppliesEventComponent");
        Add("Fourberie.FourberieBehavior", "FOnForceVolunteers", FourberiePatchKind.ServerMutation,
            BattleSide, "TaleWorlds.CampaignSystem.MapEvents.ForceVolunteersEventComponent");
        Add("Fourberie.FourberieBehavior", "FOnRaidCompleted", FourberiePatchKind.ServerMutation,
            BattleSide, "TaleWorlds.CampaignSystem.MapEvents.RaidEventComponent");
        Add("Fourberie.FourberieBehavior", "OnCompanionRemoved", FourberiePatchKind.ServerMutation,
            Hero, "TaleWorlds.CampaignSystem.Actions.RemoveCompanionAction+RemoveCompanionDetail");

        const string InventoryExchange = "System.Collections.Generic.List`1[System.ValueTuple`2[TaleWorlds.Core.ItemRosterElement,System.Int32]]";
        Add("Fourberie.FourbBanditBehavior", "OnPlayerInventoryChanged", FourberiePatchKind.ServerMutation,
            InventoryExchange, InventoryExchange, "System.Boolean");
        Add("Fourberie.FourbBanditBehavior", "BanditMapEventStarted", FourberiePatchKind.ServerMutation,
            MapEvent, PartyBase, PartyBase);
        Add("Fourberie.FourbBanditBehavior", "OnWarDeclared", FourberiePatchKind.ServerMutation,
            IFaction, IFaction, "TaleWorlds.CampaignSystem.Actions.DeclareWarAction+DeclareWarDetail");
        Add("Fourberie.FourbBanditBehavior", "OnMakePeace", FourberiePatchKind.ServerMutation,
            IFaction, IFaction, "TaleWorlds.CampaignSystem.Actions.MakePeaceAction+MakePeaceDetail");
        Add("Fourberie.FourbBanditBehavior", "OnClanChangedKingdomEvent", FourberiePatchKind.ServerMutation,
            Clan, Kingdom, Kingdom, "TaleWorlds.CampaignSystem.Actions.ChangeKingdomAction+ChangeKingdomActionDetail", "System.Boolean");
        Add("Fourberie.FourbBanditBehavior", "OnKingdomDestroyed", FourberiePatchKind.ServerMutation, Kingdom);

        Add("Fourberie.FourbContractBehavior", "OnWarDeclared", FourberiePatchKind.ServerMutation,
            IFaction, IFaction, "TaleWorlds.CampaignSystem.Actions.DeclareWarAction+DeclareWarDetail");
        Add("Fourberie.FourbContractBehavior", "OnheroKilled", FourberiePatchKind.ServerMutation,
            Hero, Hero, "TaleWorlds.CampaignSystem.Actions.KillCharacterAction+KillCharacterActionDetail", "System.Boolean");
        Add("Fourberie.FourbContractBehavior", "OnClanDestroyed", FourberiePatchKind.ServerMutation, Clan);

        Add("Fourberie.FourbFightClubBehavior", "PitOnheroKilled", FourberiePatchKind.ServerMutation,
            Hero, Hero, "TaleWorlds.CampaignSystem.Actions.KillCharacterAction+KillCharacterActionDetail", "System.Boolean");
        Add("Fourberie.FourbFightClubBehavior", "PitOnHeroRelationChanged", FourberiePatchKind.ServerMutation,
            Hero, Hero, "System.Int32", "System.Boolean",
            "TaleWorlds.CampaignSystem.Actions.ChangeRelationAction+ChangeRelationDetail", Hero, Hero);
        Add("Fourberie.FourbFightClubBehavior", "PitOnWarDeclared", FourberiePatchKind.ServerMutation,
            IFaction, IFaction, "TaleWorlds.CampaignSystem.Actions.DeclareWarAction+DeclareWarDetail");

        Add("Fourberie.FourbSafeHouseBehavior", "SHOnMissionEnded", FourberiePatchKind.ServerMutation,
            "TaleWorlds.Core.IMission");
        Add("Fourberie.FourbSafeHouseBehavior", "SHOnMissionStarted", FourberiePatchKind.ClientPresentation,
            "TaleWorlds.Core.IMission");
        Add("Fourberie.FourbSafeHouseBehavior", "SHOnSiegeBombardmentWallHit", FourberiePatchKind.ServerMutation,
            MobileParty, Settlement, BattleSide, "TaleWorlds.Core.SiegeEngineType", "System.Boolean");
        Add("Fourberie.FourbSafeHouseBehavior", "OnGameLoadFinished", FourberiePatchKind.ServerMutation);

        // Fourberie registers all player-facing menus, conversations, and mission-spawn hooks
        // through these session callbacks. They are deliberately not exposed on either peer:
        // there is no controller-scoped request, stable target identity, expected revision, or
        // request-id replay ledger for any of their consequences.
        const string CampaignGameStarter = "TaleWorlds.CampaignSystem.CampaignGameStarter";
        const string SpawnTags = "System.Collections.Generic.Dictionary`2[System.String,System.Int32]";
        Add("Fourberie.FourberieBehavior", "AddGameMenus", FourberiePatchKind.ClientPresentation,
            CampaignGameStarter);
        Add("Fourberie.FourberieBehavior", "FourbOnGaMenOpened", FourberiePatchKind.ClientPresentation,
            MenuCallbackArgs);
        Add("Fourberie.FourbSafeHouseBehavior", "SHAddGameMenus", FourberiePatchKind.ClientPresentation,
            CampaignGameStarter);
        Add("Fourberie.FourbSafeHouseBehavior", "SHLocationCharactersAreReadyToSpawn",
            FourberiePatchKind.ClientPresentation, SpawnTags);
        Add("Fourberie.FourbEscapeBehavior", "FourbEscapMenu", FourberiePatchKind.ClientPresentation,
            CampaignGameStarter);
        Add("Fourberie.FourbFightClubBehavior", "PitAddGameMenus", FourberiePatchKind.ClientPresentation,
            CampaignGameStarter);
        Add("Fourberie.FourbFightClubBehavior", "PitLocationCharactersAreReadyToSpawn",
            FourberiePatchKind.ClientPresentation, SpawnTags);
        Add("Fourberie.FourbBanditBehavior", "BanditAddMenu", FourberiePatchKind.ClientPresentation,
            CampaignGameStarter);
        Add("Fourberie.FourbBanditBehavior", "BanditOnGaMenOpened", FourberiePatchKind.ClientPresentation,
            MenuCallbackArgs);
        Add("Fourberie.FourbRecruitableBehavior", "AddGameMenus", FourberiePatchKind.ClientPresentation,
            CampaignGameStarter);
        Add("Fourberie.FourbContactMenu", "AddContactMenusF", FourberiePatchKind.ClientPresentation,
            CampaignGameStarter);
        Add("Fourberie.FourbContactMenu", "FOnGaMenOpened", FourberiePatchKind.ClientPresentation,
            MenuCallbackArgs);
        Add("Fourberie.FourbContractBehavior", "AddGameMenus", FourberiePatchKind.ClientPresentation,
            CampaignGameStarter);
        // OnApplicationTick is Fourberie's hotkey handler: it reads local input
        // (Settings.BaseMenuButton / TacticsMenuButton) and opens the mod's menus for the local
        // player. On a client Hero.MainHero IS that player, so this is correct client-local UI; the
        // state-changing consequences have their own typed operation or callback routes. Runs
        // client-only (headless has no input). Mission initialization has its own
        // role-aware authority boundary below.
        Add("Fourberie.Main", "OnApplicationTick", FourberiePatchKind.ClientPresentation, "System.Single");
        Add("Fourberie.Main", "OnMissionBehaviorInitialize", FourberiePatchKind.MissionInitialization,
            "TaleWorlds.MountAndBlade.Mission");

        // Exact final callbacks for the mission-local fights. Their mission cleanup stays on the
        // controlling client; every persistent consequence is recomputed by the host transaction.
        Add("Fourberie.InsideMissionsHelper", "AfterMathsGrabAndRun",
            FourberiePatchKind.InsideMissionOutcome, "System.Boolean");
        Add("Fourberie.InsideMissionsHelper", "AfterMathsBashing",
            FourberiePatchKind.InsideMissionOutcome, "System.Boolean");
        Add("Fourberie.InsideMissionsHelper", "AfterMathsIsoRob",
            FourberiePatchKind.InsideMissionOutcome, "System.Boolean");
        Add("Fourberie.InsideMissionsHelper", "AfterMathsPickFail",
            FourberiePatchKind.InsideMissionOutcome, "System.Boolean");
        Add("Fourberie.InsideMissionsHelper", "AfterMathsGrudgeAssassin",
            FourberiePatchKind.InsideMissionOutcome, "System.Boolean");
        Add("Fourberie.InsideMissionsHelper", "AfterMathsTavernBrawl",
            FourberiePatchKind.InsideMissionOutcome, "System.Boolean");
        Add("Fourberie.InsideMissionsHelper", "AfterMathsLarceny",
            FourberiePatchKind.InsideMissionOutcome, "System.Boolean");
        Add("Fourberie.InsideMissionsHelper", "AfterMathsEncounterAlley",
            FourberiePatchKind.InsideMissionOutcome, "System.Boolean");
        AddTokens(FourberiePatchKind.AlleyAcquisition, 0x060005FD);
        AddTokens(FourberiePatchKind.AlleyClear, 0x060005FF);
        AddTokens(FourberiePatchKind.SchemeRoomOpen, 0x060007B7, 0x060007B8, 0x060007B9);
        AddTokens(FourberiePatchKind.ConversationConsequence,
            0x060007D7, 0x060007DC, 0x060007DE, 0x060007E1, 0x060007E2, 0x060007E3);
        AddTokens(FourberiePatchKind.CampaignConsequence,
            0x060002DF, 0x06000462, 0x0600032F, 0x06000336, 0x060004BB);
        AddTokens(FourberiePatchKind.MinorRecruitmentConsequence, 0x06000621);
        AddTokens(FourberiePatchKind.KingdomLeaveConsequence, 0x06000375);
        AddTokens(FourberiePatchKind.GuardKillConsequence, 0x06000313);
        AddTokens(FourberiePatchKind.SafehouseEncounterConsequence, 0x060004A3);
        AddTokens(FourberiePatchKind.CriminalConsequence,
            0x0600032E, 0x06000810, 0x06000824, 0x06000825,
            0x0600084D, 0x06000850, 0x06000866, 0x0600086E, 0x06000877,
            0x06000A3D, 0x06000A3F, 0x06000A41, CampaignReadyWorkshopConsequenceToken, 0x06000A52,
            0x0600031A, 0x0600046E, 0x0600055B, 0x06000563, 0x060005A2, 0x060005B5,
            0x0600095B, 0x06000991, 0x060009BB, 0x060009F4,
            0x0600082E, 0x0600082F, 0x06000830, 0x06000831, 0x06000833, 0x06000835);
        AddTokens(FourberiePatchKind.MissionLocal,
            0x060003E2, 0x06000464, 0x0600046A, 0x060008C8, 0x060004A2,
            0x06000487, 0x0600048C, 0x060004CC, 0x060004DD);
        AddTokens(FourberiePatchKind.ClientPresentation,
            0x06000342, 0x0600035F, 0x060003C2, 0x060003CF,
            0x06000624, 0x06000625, 0x060004C7,
            0x060007B2, 0x060007BB, 0x060007BD, 0x060007BE,
            0x060008EF);
        AddTokens(FourberiePatchKind.DominanceCondition, 0x06000314);
        AddTokens(FourberiePatchKind.StealthMissionLocal, 0x06000504, 0x06000528);
        AddTokens(FourberiePatchKind.StealthHit, 0x060004FD);
        AddTokens(FourberiePatchKind.StealthMissionEnd, 0x06000503);
        AddTokens(FourberiePatchKind.StealthMilitiaPayment, 0x0600050C);
        AddTokens(FourberiePatchKind.StealthMilitiaChoice, 0x0600050D);
        AddTokens(FourberiePatchKind.StealthAbortContract, 0x0600050F);
        AddTokens(FourberiePatchKind.StealthAlertConsequence, 0x06000510);
        AddTokens(FourberiePatchKind.StealthAnswer, 0x06000511);
        AddTokens(FourberiePatchKind.StealthAgentRemoved, 0x0600052A);
        AddTokens(FourberiePatchKind.StealthAlarm, 0x0600052E);
        AddTokens(FourberiePatchKind.StealthScandalSuccess, 0x0600092A);
        AddTokens(FourberiePatchKind.StealthPrisonSuccess, 0x0600092D);
        AddReturning("Fourberie.FourbFightClubController", "OnEndMissionRequest",
            "TaleWorlds.Library.InquiryData", FourberiePatchKind.FightClubOutcome, "System.Boolean&");
        Add("Fourberie.FourbFightClubBehavior", "PitFightFameLoss",
            FourberiePatchKind.FightClubFame,
            "System.Int32", "System.Int32", "System.Int32", "System.String");
        Add("Fourberie.FourbFightClubBehavior", "PitFightFameGain",
            FourberiePatchKind.FightClubFame,
            "System.Int32", "System.Int32", "System.Int32", "System.String", "System.Boolean");
        AddReturning("Fourberie.FourbFightClubBehavior", "PatronPaycheck", "System.Int32",
            FourberiePatchKind.FightClubPatronPayment,
            "TaleWorlds.CampaignSystem.Hero", "System.Int32", "System.Boolean", "System.Boolean&");
        Add("Fourberie.FourbFightClubBehavior+<>c__DisplayClass18_0", "<PitTrainingStart>b__0",
            FourberiePatchKind.FightClubAdmission,
            "System.Collections.Generic.List`1[TaleWorlds.Core.InquiryElement]");
        Add("Fourberie.FourbFightClubBehavior+<>c__DisplayClass19_0", "<PitFightStart>b__0",
            FourberiePatchKind.FightClubAdmission,
            "System.Collections.Generic.List`1[TaleWorlds.Core.InquiryElement]");
        Add("Fourberie.FourbFightClubBehavior+<>c", "<DialogsPitFight>b__8_1",
            FourberiePatchKind.FightClubEnrollment);
        Add("Fourberie.FourbFightClubBehavior+<>c__DisplayClass22_1", "<PatronStatus>b__0",
            FourberiePatchKind.FightClubPatronRefusal,
            "System.Collections.Generic.List`1[TaleWorlds.Core.InquiryElement]");
        Add("Fourberie.FourbFightClubBehavior+<>c__DisplayClass12_0", "<YourStableStatus>b__4",
            FourberiePatchKind.FightClubStableOwnership);
        Add("Fourberie.FourbFightClubBehavior+<>c__DisplayClass12_2", "<YourStableStatus>b__2",
            FourberiePatchKind.FightClubStableOwnership);
        AddReturning("Fourberie.FourbFightClubBehavior", "RecruitLadsOnDoneClicked", "System.Boolean",
            FourberiePatchKind.FightClubStableRecruitment,
            TroopRoster, TroopRoster, TroopRoster, TroopRoster,
            FlattenedTroopRoster, FlattenedTroopRoster, "System.Boolean", PartyBase, PartyBase);
        Add("Fourberie.FourbFightClubBehavior", "fb_menu_main_on_init",
            FourberiePatchKind.FightClubMenuRefresh, MenuCallbackArgs);

        // Screen registration is presentation-only. The adapter owns exact model compatibility, so
        // OnGameInitializationFinished keeps only the server-owned hero-dictionary rebuild.
        Add("Fourberie.Main", "OnScreenManagerPushScreen", FourberiePatchKind.ClientPresentation,
            "TaleWorlds.ScreenSystem.ScreenBase");
        Add("Fourberie.Main", "OnGameInitializationFinished", FourberiePatchKind.RefreshHeroDicoOnly,
            "TaleWorlds.Core.Game");

        // Fourberie's loyalty decorator remains the active model so its town modifiers are not
        // discarded. These two delegated thresholds are composed with Friend Edition's integrated
        // Separatism settings instead of replacing the whole Fourberie model afterward.
        AddReturning("Fourberie.FModelLoyalty", "get_RebellionStartLoyaltyThreshold", "System.Int32",
            FourberiePatchKind.SeparatismLoyaltyComposition);
        AddReturning("Fourberie.FModelLoyalty", "get_RebelliousStateStartLoyaltyThreshold", "System.Int32",
            FourberiePatchKind.SeparatismLoyaltyComposition);

        // This calculation awards random skill XP when applyWithdrawals=true. Clients may calculate
        // display values, but may never apply those side effects.
        Add("Fourberie.FModelHelperFinance", "CalculateClanIncomeFourberie", FourberiePatchKind.FinanceRead,
            "TaleWorlds.CampaignSystem.ExplainedNumber&", "System.Boolean", "System.Boolean");
        Add("Fourberie.FModelHelperFinance", "CalculateClanExpenseFourberie", FourberiePatchKind.FinanceRead,
            "TaleWorlds.CampaignSystem.ExplainedNumber&", "System.Boolean");

        // Exact-token mission surface. These callbacks manipulate only the local Mission, Agent,
        // controller, navigation, audio, marker, and view-model graph. Persistent commit callbacks
        // are deliberately absent and have dedicated typed transaction kinds above.
        AddTokens(FourberiePatchKind.ClientPresentation,
            0x06000011, 0x06000012, 0x06000034, 0x060001A5, 0x060003DF, 0x060003E7,
            0x060003E9, 0x06000430, 0x06000433, 0x06000461, 0x06000463, 0x06000465,
            0x06000466, 0x06000467, 0x06000468, 0x06000469, 0x0600046B, 0x06000470,
            0x06000471, 0x06000472, 0x060004F9, 0x060004FA, 0x060004FB, 0x060004FC,
            0x060004FE, 0x060004FF, 0x06000500, 0x06000501, 0x06000502, 0x06000505,
            0x06000506, 0x06000507, 0x06000508, 0x06000509, 0x0600050A, 0x0600050B,
            0x0600050E, 0x06000513, 0x06000514, 0x06000515, 0x06000516,
            0x06000517, 0x06000518, 0x06000519, 0x0600051A, 0x0600051B, 0x0600051C,
            0x0600051D, 0x0600051E, 0x0600051F, 0x06000520, 0x06000521, 0x06000522,
            0x06000524, 0x06000525, 0x06000527, 0x0600052B, 0x0600052C, 0x0600052F,
            0x06000530, 0x06000531, 0x06000532, 0x06000533, 0x06000534, 0x06000535,
            0x06000536, 0x06000537, 0x06000538, 0x06000539, 0x0600053A, 0x0600053B,
            0x0600053C, 0x0600053D, 0x0600053E, 0x0600053F, 0x06000540, 0x06000541,
            0x06000542, 0x06000543, 0x06000544, 0x06000545, 0x06000546, 0x06000547,
            0x06000548, 0x06000549, 0x0600054A, 0x0600054B, 0x0600054C, 0x0600054D,
            0x0600054E, 0x0600054F, 0x06000550, 0x06000551, 0x06000552, 0x06000553,
            0x06000554, 0x06000555, 0x06000556, 0x06000557, 0x060008A9, 0x060008AF,
            0x060008B0, 0x060008C1, 0x060008C2, 0x060008C3, 0x060008C4, 0x06000914,
            0x06000915, 0x06000916, 0x06000917, 0x06000918, 0x0600091A, 0x0600091B,
            0x0600091E, 0x0600091F, 0x06000920, 0x06000921, 0x06000922, 0x06000923,
            0x06000924, 0x06000925, 0x06000926, 0x06000927, 0x06000928, 0x06000929,
            0x0600092B, 0x0600092C, 0x0600092E, 0x0600092F, 0x06000931, 0x06000933,
            0x06000935, 0x06000936, 0x06000937, 0x0600093A, 0x0600093C, 0x0600093D,
            // InsideMissionsHelper's setup and teardown operate on the controlling client's
            // Mission/Agent graph. The eight AfterMaths callbacks are intentionally not here:
            // they are replaced by the authenticated outcome operations above. Alley ownership
            // is likewise excluded until its existing Coop alley-acquisition route has run.
            0x060005EB, 0x060005EC, 0x060005EE, 0x060005F0, 0x060005F2, 0x060005F4,
            0x060005F6, 0x060005F8, 0x060005FA, 0x06000601, 0x06000A6E);
        // Bandit mission bookkeeping and menu/condition builders are local presentation. Their
        // nested selection callbacks (truce, war-dog, follower, roster, and ship transfer) are
        // intentionally excluded until their authenticated operation routes commit on the host.
        AddTokens(FourberiePatchKind.ClientPresentation,
            0x06000013, 0x06000014, 0x06000278, 0x06000291, 0x06000292,
            0x0600029A, 0x060002B8, 0x060002B9, 0x060002BA, 0x060002BD,
            0x06000738, 0x0600077C, 0x0600077E, 0x06000780);
        AddTokens(FourberiePatchKind.ServerOnly,
            0x06000260, 0x06000286, 0x060002AF, 0x060002B2,
            // Fourberie's developer/cheat console commands mutate canonical campaign state and
            // therefore exist only on the authoritative campaign host.
            0x060002C8, 0x060002C9, 0x060002CA, 0x060002CC);
        AddTokens(FourberiePatchKind.BanditConsequence,
            0x06000272, 0x060002C0, 0x06000745, 0x0600074C, 0x0600074E,
            0x0600075E, 0x06000766, 0x06000776, 0x06000782, 0x06000791,
            0x06000795);
        AddTokens(FourberiePatchKind.BanditDonationConsequence, 0x06000280);
        AddTokens(FourberiePatchKind.BanditRosterOpen, 0x06000282);
        AddTokens(FourberiePatchKind.BanditRosterConsequence, 0x06000285);
        AddTokens(FourberiePatchKind.BanditPreparation,
            0x06000741, 0x06000744, 0x06000746, 0x06000748, 0x0600074A);
        AddTokens(FourberiePatchKind.LegacyCallback,
            0x060007C1, 0x060007E8, 0x060007EA,
            0x0600094E, 0x0600096D, 0x0600098E, 0x06000998,
            0x060009A3, 0x060009B1, 0x060009B6, 0x060009B7,
            0x060009C0, 0x060009D4, 0x060009E6,
            0x060009F9, 0x060009FA, 0x060009FE, 0x06000A03,
            0x06000A11, 0x06000A12, 0x06000A13, 0x06000A15);
        // Exact presentation/mission helpers whose apparent writes are confined to Gauntlet VM,
        // Mission Agent/navigation, encounter-position, score, and transient controller fields.
        AddTokens(FourberiePatchKind.ClientPresentation,
            0x060001A2, 0x060002C2, 0x060002C3, 0x060002CB, 0x0600033C, 0x0600033E,
            0x060003EB, 0x0600046D, 0x06000474, 0x06000475, 0x0600047A, 0x0600047C,
            0x0600047E, 0x06000482, 0x06000485, 0x06000499, 0x060004C6, 0x06000512,
            0x06000523, 0x06000526, 0x06000529, 0x0600052D,
            // Criminal menu conditions, score/income composition, and hint builders are reads or
            // presentation caches. They must resolve against the replicated player view and never
            // execute on the host's global MainHero/PlayerClan context.
            0x0600031B, 0x0600031D, 0x0600031E, 0x06000322, 0x06000323, 0x06000329,
            0x06000341, 0x0600034B, 0x0600034E, 0x0600037B, 0x0600037C, 0x0600037E,
            0x0600037F, 0x06000384, 0x06000385, 0x06000386, 0x06000387, 0x060003B7,
            0x060003B9, 0x060003BB, 0x060003C1, 0x060003C3, 0x060007CC, 0x0600080D);
        AddTokens(FourberiePatchKind.FightClubMissionLocal,
            0x0600042C, 0x0600042D, 0x0600042F, 0x06000431, 0x06000434, 0x0600043A,
            0x06000437, 0x060008AC, 0x060008B9);
        AddTokens(FourberiePatchKind.ClientPresentation,
            0x060003F7, 0x060003F9, 0x06000400, 0x06000401, 0x06000403,
            0x06000405, 0x06000408, 0x0600040A, 0x0600040D, 0x06000410, 0x06000413,
            0x06000414, 0x06000415, 0x06000416, 0x06000419, 0x0600041A, 0x0600041C,
            0x0600041D, 0x0600041F, 0x06000420, 0x06000426, 0x06000428, 0x0600042A,
            0x0600042B, 0x060008A4);
        AddTokens(FourberiePatchKind.SchemeOwnedReplacement, 0x060008B3, 0x060008B5);

        return methods;
    }
}
