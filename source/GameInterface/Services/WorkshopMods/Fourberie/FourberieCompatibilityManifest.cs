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
    GrudgeSelectionConsequence,
    GrudgeSettlementConsequence,
    MissionInitialization,
    SeparatismLoyaltyComposition,

    /// <summary>
    /// Replaces <c>Main.OnGameInitializationFinished</c>: runs only its
    /// <c>StringDicoHelper.RefreshHeroDico()</c> call (a client-local hero-name cache the menus
    /// need). Compatibility validation remains owned by this exact-binary adapter.
    /// </summary>
    RefreshHeroDicoOnly,
}

internal sealed class FourberieMethodSpec
{
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

    public string Key => $"{TypeName}::{MethodName}({string.Join(",", ParameterTypeNames)}):{ReturnTypeName}";

    public MethodInfo Resolve(Assembly assembly)
    {
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
/// Compatibility contract for the creator-authorized Fourberie 1.4.7.5 binary. File identity and
/// every method we detour must match before any compatibility patch is installed. An upstream
/// update therefore cannot silently inherit guards written for a different implementation.
/// </summary>
internal static class FourberieCompatibilityManifest
{
    public const string AssemblyName = "Fourberie";
    public const string ModuleId = "Fourberie";
    public const string WorkshopId = "2875710877";
    public const string SupportedModuleVersion = "v1.4.7.5";
    public const string SupportedAssemblyVersion = "1.4.7.5";
    public const string SupportedSha256 = "FD1C02158817FAE5B90E3C121DA474096CAA368CB35495D83CE81EA49D860C71";
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
        Add("Fourberie.CriminalVM", "AgentsEnlistRoutine", FourberiePatchKind.ClientOperationPresentation, "System.Int32");
        AddReturning("Fourberie.CriminalVM", "EnlistFromPartyDone", "System.Boolean",
            FourberiePatchKind.EnlistPartyConsequence,
            TroopRoster, TroopRoster, TroopRoster, TroopRoster,
            FlattenedTroopRoster, FlattenedTroopRoster, "System.Boolean", PartyBase, PartyBase);
        Add("Fourberie.CriminalVM", "EnlistFromLadsDone", FourberiePatchKind.EnlistLadsConsequence,
            PartyBase, TroopRoster, TroopRoster, PartyBase, TroopRoster, TroopRoster, "System.Boolean");
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
        const string InquiryElements = "System.Collections.Generic.List`1[TaleWorlds.Core.InquiryElement]";
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
        Add("Fourberie.CriminalVM", "ClanGruFdilter", FourberiePatchKind.ClientPresentation);
        Add("Fourberie.CriminalVM", "CanPlayerPay", FourberiePatchKind.ClientPresentation);
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
        // in the 1.4.7.5 audit. They are separately guarded so a duplicate listener cannot execute
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

        Add("Fourberie.FourbContractBehavior", "HourlyTick", FourberiePatchKind.ServerTick);
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
        Add("Fourberie.FourbSafeHouseBehavior", "SHOnMissionStarted", FourberiePatchKind.ServerMutation,
            "TaleWorlds.Core.IMission");
        Add("Fourberie.FourbSafeHouseBehavior", "SHOnSiegeBombardmentWallHit", FourberiePatchKind.ServerMutation,
            MobileParty, Settlement, BattleSide, "TaleWorlds.Core.SiegeEngineType", "System.Boolean");
        Add("Fourberie.FourbSafeHouseBehavior", "OnGameLoadFinished", FourberiePatchKind.ServerMutation);

        // Fourberie registers all player-facing menus, conversations, and mission-spawn hooks
        // through these session callbacks. They are deliberately not exposed on either peer:
        // there is no controller-scoped request, stable target identity, expected revision, or
        // request-id replay ledger for any of their consequences.
        const string CampaignGameStarter = "TaleWorlds.CampaignSystem.CampaignGameStarter";
        const string MenuCallbackArgs = "TaleWorlds.CampaignSystem.GameMenus.MenuCallbackArgs";
        const string SpawnTags = "System.Collections.Generic.Dictionary`2[System.String,System.Int32]";
        Add("Fourberie.FourberieBehavior", "AddGameMenus", FourberiePatchKind.ClientPresentation,
            CampaignGameStarter);
        Add("Fourberie.FourberieBehavior", "FourbOnGaMenOpened", FourberiePatchKind.ClientPresentation,
            MenuCallbackArgs);
        Add("Fourberie.FourbSafeHouseBehavior", "SHAddGameMenus", FourberiePatchKind.ClientPresentation,
            CampaignGameStarter);
        Add("Fourberie.FourbSafeHouseBehavior", "SHOnGaMenOpened", FourberiePatchKind.ClientPresentation,
            MenuCallbackArgs);
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

        // Screen registration is presentation-only. The adapter owns exact model compatibility, so
        // OnGameInitializationFinished keeps only StringDicoHelper.RefreshHeroDico(), the local
        // hero-name cache the menus need.
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

        return methods;
    }
}
