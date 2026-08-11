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
    UnsupportedPlayerAction,
    ClientPresentation,
    FinanceRead,

    /// <summary>
    /// Runs Fourberie's gameplay behaviors but suppresses its 14 game-model replacements, which
    /// overlap Coop's authority (healing, finance, crime, diplomacy, loyalty, food, party, …).
    /// Used for the monolithic <c>InitializeCampaignBehaviors</c> so its content is available while
    /// the conflicting models stay vanilla and Coop-synced. Behavior ticks/mutations remain gated
    /// by their own ServerTick/ServerOnly guards.
    /// </summary>
    BehaviorsWithoutModels,

    /// <summary>
    /// Player create-action routed to the server (Diplomacy-pattern): on a client the guard
    /// publishes an intent and skips local execution; on the server it runs the mod's routine so
    /// the created parties/rosters flow through Coop's authoritative create funnels. Used for
    /// Fourberie actions that mint <c>MobileParty</c>/<c>TroopRoster</c> objects.
    /// </summary>
    RoutedCreateAction,

    /// <summary>
    /// Replaces <c>Main.OnGameInitializationFinished</c>: runs only its
    /// <c>StringDicoHelper.RefreshHeroDico()</c> call (a client-local hero-name cache the menus
    /// need) and skips its game-model load-order validation, which would otherwise spam red
    /// warnings because Coop intentionally suppresses Fourberie's 14 model replacements.
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
        "Fourberie.HomesSteadsAddOn",
        "Fourberie.BellumCivileAddOn",
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

        // Fourberie's monolithic initializer installs every behavior plus fourteen model
        // replacements that overlap Coop's healing, birth/death, combat, finance, diplomacy,
        // food, settlement, and party-transition authority. Server-only execution does not make
        // those overlapping results safe, so the initializer is feature-blocked on every role.
        // Concrete callbacks below remain a second boundary for an already-registered listener;
        // the runtime surface gate separately aborts if the models were installed before Coop.
        Add("Fourberie.Main", "InitializeCampaignBehaviors", FourberiePatchKind.BehaviorsWithoutModels,
            "TaleWorlds.Core.IGameStarter");

        // These static void M(int) routines create parties/rosters through MainHero, MainParty and
        // Fourberie's singleton _agentsParty. A server request can authenticate its peer, but the
        // original API cannot receive that peer's hero/party context. Running it would therefore
        // apply to the host singleton. Keep all three fail-closed until an explicit-context API is
        // deliberately implemented and tested.
        Add("Fourberie.CriminalVM", "AgentsEnlistRoutine", FourberiePatchKind.UnsupportedPlayerAction, "System.Int32");
        Add("Fourberie.FourbBanditBehavior", "FourbRecruitBandit", FourberiePatchKind.UnsupportedPlayerAction, "System.Int32");
        Add("Fourberie.HelperSubInsuScam", "SpawnBandits", FourberiePatchKind.UnsupportedPlayerAction, "System.Int32");

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
        Add("Fourberie.HomesSteadsAddOn", "MenuHomeSteads", FourberiePatchKind.ClientPresentation,
            CampaignGameStarter);

        // OnApplicationTick is Fourberie's hotkey handler: it reads local input
        // (Settings.BaseMenuButton / TacticsMenuButton) and opens the mod's menus for the local
        // player. On a client Hero.MainHero IS that player, so this is correct client-local UI; the
        // actual state-changing menu options it opens are separately routed (RoutedCreateAction) or
        // client-safe. Runs client-only (headless has no input). OnMissionBehaviorInitialize stays
        // blocked (mission-context singleton mutation, not yet routed).
        Add("Fourberie.Main", "OnApplicationTick", FourberiePatchKind.ClientPresentation, "System.Single");
        Add("Fourberie.Main", "OnMissionBehaviorInitialize", FourberiePatchKind.UnsupportedPlayerAction,
            "TaleWorlds.MountAndBlade.Mission");

        // Screen registration is presentation-only. OnGameInitializationFinished does two things: it
        // validates the 14 game-model replacements (which now emit red "move Fourberie in load
        // order" spam because we intentionally suppress those models) AND calls
        // StringDicoHelper.RefreshHeroDico() (a client-local hero-name cache the menus need). We run
        // only the RefreshHeroDico half and skip the model validation — see RefreshHeroDicoOnlyPrefix.
        Add("Fourberie.Main", "OnScreenManagerPushScreen", FourberiePatchKind.ClientPresentation,
            "TaleWorlds.ScreenSystem.ScreenBase");
        Add("Fourberie.Main", "OnGameInitializationFinished", FourberiePatchKind.RefreshHeroDicoOnly,
            "TaleWorlds.Core.Game");

        // This calculation awards random skill XP when applyWithdrawals=true. Clients may calculate
        // display values, but may never apply those side effects.
        Add("Fourberie.FModelHelperFinance", "CalculateClanIncomeFourberie", FourberiePatchKind.FinanceRead,
            "TaleWorlds.CampaignSystem.ExplainedNumber&", "System.Boolean", "System.Boolean");
        Add("Fourberie.FModelHelperFinance", "CalculateClanExpenseFourberie", FourberiePatchKind.FinanceRead,
            "TaleWorlds.CampaignSystem.ExplainedNumber&", "System.Boolean");

        return methods;
    }
}
