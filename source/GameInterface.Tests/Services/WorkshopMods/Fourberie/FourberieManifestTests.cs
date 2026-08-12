using GameInterface.Services.WorkshopMods.Fourberie;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Fourberie;

public sealed class FourberieManifestTests
{
    private sealed class ShapeFixture
    {
        private static void Target(ref int value, bool enabled, List<string> names)
        {
        }
    }

    [Fact]
    public void CompatibilityManifest_MethodShapesMatchPinnedCreatorInventory()
    {
        string inventoryPath = FindRepositoryFile("doc", "generated", "workshop-function-inventory.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(inventoryPath));
        JsonElement assembly = Assert.Single(
            document.RootElement.GetProperty("assemblies").EnumerateArray(),
            candidate => candidate.GetProperty("moduleId").GetString() == "Fourberie" &&
                         candidate.GetProperty("sha256").GetString() ==
                         FourberieCompatibilityManifest.SupportedSha256.ToLowerInvariant());
        var pinnedShapes = assembly.GetProperty("methods").EnumerateArray()
            .Select(method =>
                $"{method.GetProperty("declaringType").GetString()}::" +
                $"{method.GetProperty("name").GetString()}(" +
                string.Join(",", method.GetProperty("parameterTypes").EnumerateArray()
                    .Select(parameter => NormalizeInventoryTypeName(parameter.GetString()))) +
                $"):{NormalizeInventoryTypeName(method.GetProperty("returnType").GetString())}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (FourberieMethodSpec spec in FourberieCompatibilityManifest.Methods)
        {
            string requestedShape =
                $"{spec.TypeName}::{spec.MethodName}({string.Join(",", spec.ParameterTypeNames)}):{spec.ReturnTypeName}";
            Assert.True(pinnedShapes.Contains(requestedShape),
                $"Compatibility manifest requests a method shape absent from the pinned Fourberie binary: {requestedShape}");
        }
    }

    private static string NormalizeInventoryTypeName(string? typeName) =>
        (typeName ?? string.Empty).Replace('<', '[').Replace('>', ']');

    [Theory]
    [InlineData("v1.4.7.5", "FD1C02158817FAE5B90E3C121DA474096CAA368CB35495D83CE81EA49D860C71", true)]
    [InlineData("v1.4.7.4", "FD1C02158817FAE5B90E3C121DA474096CAA368CB35495D83CE81EA49D860C71", false)]
    [InlineData("v1.4.7.5", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    public void IdentityGate_RequiresExactCreatorBinary(string version, string hash, bool expected)
    {
        Assert.Equal(expected, FourberieCompatibilityManifest.IsSupportedIdentity(version, hash));
    }

    [Fact]
    public void MethodShapeGate_MatchesReturnByRefAndGenericArgumentsExactly()
    {
        var spec = new FourberieMethodSpec(
            typeof(ShapeFixture).FullName,
            "Target",
            "System.Void",
            FourberiePatchKind.ServerOnly,
            "System.Int32&",
            "System.Boolean",
            "System.Collections.Generic.List`1[System.String]");

        Assert.NotNull(spec.Resolve(typeof(ShapeFixture).Assembly));
    }

    [Theory]
    [InlineData("Fourberie.CriminalVM", "AgentsEnlistRoutine", "ClientOperationPresentation")]
    [InlineData("Fourberie.CriminalVM", "EnlistFromPartyDone", "EnlistPartyConsequence")]
    [InlineData("Fourberie.CriminalVM", "EnlistFromLadsDone", "EnlistLadsConsequence")]
    [InlineData("Fourberie.FourberieBehavior", "OnDoneEnslaved", "EnslavePrisonersConsequence")]
    [InlineData("Fourberie.FourbBanditBehavior", "FourbRecruitBandit", "ClientOperationPresentation")]
    [InlineData("Fourberie.FourbBanditBehavior", "RecruitLadsOnDoneClicked", "RecruitBanditsConsequence")]
    [InlineData("Fourberie.HelperSubInsuScam+<>c__DisplayClass0_0", "<Menu>b__4", "InsuranceScamConsequence")]
    [InlineData("Fourberie.CriminalVM+<>c", "<UpSmugglers>b__12_0", "BusinessStartConsequence")]
    [InlineData("Fourberie.CriminalVM+<>c__DisplayClass16_0", "<UpWorkers>b__0", "BusinessStartConsequence")]
    [InlineData("Fourberie.CriminalVM+<>c__DisplayClass28_0", "<UpServants>b__0", "BusinessStartConsequence")]
    [InlineData("Fourberie.FourberieBehavior", "BizUpgrades", "BusinessUpgradeConsequence")]
    [InlineData("Fourberie.FourberieBehavior", "BizDowngrades", "BusinessDowngradeConsequence")]
    [InlineData("Fourberie.FourberieBehavior", "BonUpg", "SchemeBonusUpgradeConsequence")]
    [InlineData("Fourberie.FourberieBehavior", "BonDowng", "SchemeBonusDowngradeConsequence")]
    [InlineData("Fourberie.CriminalVM", "SchBonus1Re", "SchemeBonusResetConsequence")]
    [InlineData("Fourberie.CriminalVM", "SchBonus2Re", "SchemeBonusResetConsequence")]
    [InlineData("Fourberie.CriminalVM", "PackAgents", "AgentPartyCreateConsequence")]
    [InlineData("Fourberie.CriminalVM", "UnpackAgents", "AgentPartyDisbandConsequence")]
    [InlineData("Fourberie.CriminalVM+<>c__DisplayClass35_0", "<AgentsList>b__0", "AgentPartySelectionConsequence")]
    [InlineData("Fourberie.CriminalVM+<>c", "<FRefrparty>b__22_0", "CrimeBaseResetConsequence")]
    [InlineData("Fourberie.CriminalVM", "ButAssign1", "RoleSelectionPresentation")]
    [InlineData("Fourberie.CriminalVM", "ButRemove1", "RoleSelectionPresentation")]
    [InlineData("Fourberie.CriminalVM", "ButAssign4", "RoleSelectionPresentation")]
    [InlineData("Fourberie.CriminalVM", "ButRemove4", "RoleSelectionPresentation")]
    [InlineData("Fourberie.FourberieBehavior", "Comparole", "RoleSelectionPresentation")]
    [InlineData("Fourberie.FourberieBehavior", "RemoveCompa", "RoleRemovalConsequence")]
    [InlineData("Fourberie.FourberieBehavior+<>c__DisplayClass84_0", "<Comparole>b__0", "RoleAssignmentConsequence")]
    [InlineData("Fourberie.CriminalVM+<>c__DisplayClass137_0", "<Schemhero1>b__0", "SchemeVictimConsequence")]
    [InlineData("Fourberie.CriminalVM+<>c__DisplayClass138_0", "<Schemhero2>b__0", "SchemeVictimConsequence")]
    [InlineData("Fourberie.FourberieBehavior+<>c__DisplayClass102_0", "<SchemeSel>b__0", "SchemeTypeConsequence")]
    [InlineData("Fourberie.CriminalVM", "SchemeAllButtonRoutine", "SchemeLifecycleConsequence")]
    [InlineData("Fourberie.CriminalVM+<>c__DisplayClass562_0", "<SchemeRoomStanceList>b__0", "SchemeStanceConsequence")]
    [InlineData("Fourberie.CriminalVM", "RefreshValues", "ClientRoleRefresh")]
    [InlineData("Fourberie.CriminalVM", "Close2", "ClientRoleRefresh")]
    [InlineData("Fourberie.CriminalVM", "CorruptSelect", "ClientPresentation")]
    [InlineData("Fourberie.CriminalVM+<>c", "<CorruptSelect>b__33_0", "CorruptionLevelConsequence")]
    [InlineData("Fourberie.CriminalVM", "set_UpgradeSlideBar", "CrimeRoomSliderConsequence")]
    [InlineData("Fourberie.CriminalVM", "set_LadsDutySlideBar", "CrimeRoomSliderConsequence")]
    [InlineData("Fourberie.CriminalVM", "set_SlavesDutySlideBar", "CrimeRoomSliderConsequence")]
    [InlineData("Fourberie.CriminalVM", "get_UpgradeSlideBar", "ClientCrimeRoomRead")]
    [InlineData("Fourberie.CriminalVM", "UpInvestHint", "ClientCrimeRoomRead")]
    [InlineData("Fourberie.CriminalVM", "FContractCom", "ClientPresentation")]
    [InlineData("Fourberie.CriminalVM+<>c", "<FContractCom>b__151_0", "ContractConsequence")]
    [InlineData("Fourberie.CriminalVM+<>c", "<FContractCom>b__151_3", "ContractConsequence")]
    [InlineData("Fourberie.CriminalVM+<>c", "<FContractCom>b__151_5", "ContractConsequence")]
    [InlineData("Fourberie.FourbContractBehavior", "ContractAborted", "ServerOnly")]
    [InlineData("Fourberie.CriminalVM+<>c", "<KingdomFilter>b__148_1", "ClientSchemeFilter")]
    [InlineData("Fourberie.CriminalVM+<>c", "<ClanFilter>b__152_0", "ClientSchemeFilter")]
    [InlineData("Fourberie.CriminalVM+<>c", "<ClanFilter2>b__153_0", "ClientSchemeFilter")]
    [InlineData("Fourberie.CriminalVM+<>c", "<ListArmiesF>b__161_0", "ClientSchemeFilter")]
    [InlineData("Fourberie.CriminalVM+<>c", "<ListKPartiesF>b__162_0", "ClientSchemeFilter")]
    [InlineData("Fourberie.CriminalVM+<>c", "<KingFiefsF>b__166_0", "ClientSchemeFilter")]
    [InlineData("Fourberie.CriminalVM+<>c", "<ClanFiefsF>b__167_0", "ClientSchemeFilter")]
    [InlineData("Fourberie.CriminalVM+<>c", "<ClanFiefsF2>b__168_0", "ClientSchemeFilter")]
    [InlineData("Fourberie.CriminalVM+<>c", "<ClanPartiesF>b__170_0", "ClientSchemeFilter")]
    [InlineData("Fourberie.CriminalVM+<>c", "<ClanPartiesF2>b__171_0", "ClientSchemeFilter")]
    [InlineData("Fourberie.CriminalVM+<>c", "<ListKingPoliticsF>b__551_0", "ClientSchemeFilter")]
    [InlineData("Fourberie.CriminalVM", "<ListKingDiploF>b__552_2", "ClientSchemeFilter")]
    [InlineData("Fourberie.CriminalVM", "TerritoryMakeMainBase", "ClientPresentation")]
    [InlineData("Fourberie.CriminalVM+<>c", "<TerritoryMakeMainBase>b__160_0", "MainBaseConsequence")]
    [InlineData("Fourberie.CriminalVM", "ListTributeF", "ClientPresentation")]
    [InlineData("Fourberie.CriminalVM+<>c", "<ListTributeF>b__157_0", "TerritorySelectionConsequence")]
    [InlineData("Fourberie.CriminalVM+<>c__DisplayClass157_0", "<ListTributeF>b__2", "TerritoryAbandonConsequence")]
    [InlineData("Fourberie.CriminalVM", "AbandonSafeClick", "ClientPresentation")]
    [InlineData("Fourberie.CriminalVM+<>c__DisplayClass544_0", "<AbandonSafeClick>b__0", "SafehouseAbandonConsequence")]
    [InlineData("Fourberie.FourbSafeHouseBehavior", "<AddDialogsSafeHouse>b__9_1", "SafehouseEstablishmentConsequence")]
    [InlineData("Fourberie.FourbSafeHouseBehavior+<>c", "<AddDialogsSafeHouse>b__9_18", "SafehouseTraderConsequence")]
    [InlineData("Fourberie.FourbSafeHouseBehavior+<>c", "<AddDialogsSafeHouse>b__9_20", "SafehouseTraderConsequence")]
    [InlineData("Fourberie.FourbSafeHouseBehavior+<>c", "<AddDialogsSafeHouse>b__9_22", "SafehouseTraderConsequence")]
    [InlineData("Fourberie.FourbSafeHouseBehavior+<>c", "<AddDialogsSafeHouse>b__9_24", "SafehouseTraderConsequence")]
    [InlineData("Fourberie.FourbSafeHouseBehavior+<>c", "<MenuSafeHouse>b__13_5", "SafehouseWaitConsequence")]
    [InlineData("Fourberie.FourbSafeHouseBehavior+<>c", "<MenuSafeHouse>b__13_7", "SafehouseWaitConsequence")]
    [InlineData("Fourberie.FourbSafeHouseBehavior", "SHOnGaMenOpened", "SafehouseReturnLifecycle")]
    [InlineData("Fourberie.FourberieBehavior", "DataDeleteEx", "ServerMutation")]
    [InlineData("Fourberie.CriminalVM", "ClanGruFdilter", "ClientPresentation")]
    [InlineData("Fourberie.CriminalVM", "CanPlayerPay", "ClientPresentation")]
    [InlineData("Fourberie.CriminalVM", "<ClanGruFdilter>b__149_0", "GrudgeSelectionConsequence")]
    [InlineData("Fourberie.CriminalVM+<>c__DisplayClass149_1", "<ClanGruFdilter>b__4", "GrudgeSettlementConsequence")]
    [InlineData("Fourberie.Main", "OnMissionBehaviorInitialize", "MissionInitialization")]
    public void PreviouslyBlockedFeatureEntryPoints_HaveLiveAuthorityOwners(
        string type,
        string method,
        string expectedKind)
    {
        Assert.Contains(
            FourberieCompatibilityManifest.Methods,
            spec => spec.TypeName == type &&
                    spec.MethodName == method &&
                    spec.Kind.ToString() == expectedKind);

        Assert.DoesNotContain(
            FourberieCompatibilityManifest.Methods,
            spec => spec.TypeName == type &&
                    spec.MethodName == method &&
                    spec.Kind.ToString().Contains("Unsupported"));
    }

    [Theory]
    [InlineData("Fourberie.FourberieBehavior", "AddGameMenus")]
    [InlineData("Fourberie.FourbSafeHouseBehavior", "SHAddGameMenus")]
    [InlineData("Fourberie.FourbSafeHouseBehavior", "SHOnMissionStarted")]
    [InlineData("Fourberie.FourbSafeHouseBehavior", "SHLocationCharactersAreReadyToSpawn")]
    [InlineData("Fourberie.FourbEscapeBehavior", "FourbEscapMenu")]
    [InlineData("Fourberie.FourbFightClubBehavior", "PitAddGameMenus")]
    [InlineData("Fourberie.FourbBanditBehavior", "BanditAddMenu")]
    [InlineData("Fourberie.FourbRecruitableBehavior", "AddGameMenus")]
    [InlineData("Fourberie.FourbContactMenu", "AddContactMenusF")]
    [InlineData("Fourberie.FourbContractBehavior", "AddGameMenus")]
    [InlineData("Fourberie.Main", "OnApplicationTick")]
    public void PresentationEntryPoints_AreClientOnly(string type, string method)
    {
        Assert.Contains(
            FourberieCompatibilityManifest.Methods,
            spec => spec.TypeName == type &&
                    spec.MethodName == method &&
                    spec.Kind == FourberiePatchKind.ClientPresentation);
    }

    [Theory]
    [InlineData("Fourberie.Main", "InitializeCampaignBehaviors", "BehaviorsAndModels")]
    [InlineData("Fourberie.Main", "OnGameInitializationFinished", "RefreshHeroDicoOnly")]
    public void ReplacedInitializationEntryPoints_UseTheirAuditedAdapters(
        string type,
        string method,
        string expectedKind)
    {
        Assert.Contains(
            FourberieCompatibilityManifest.Methods,
            spec => spec.TypeName == type &&
                    spec.MethodName == method &&
                    spec.Kind.ToString() == expectedKind);
    }

    [Theory]
    [InlineData("OnSubModuleLoad")]
    [InlineData("OnGameStart")]
    public void OuterInitializationEntryPoints_RemainFrameworkOwned(string method) =>
        Assert.DoesNotContain(
            FourberieCompatibilityManifest.Methods,
            spec => spec.TypeName == "Fourberie.Main" && spec.MethodName == method);

    private static string FindRepositoryFile(params string[] pathParts)
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                string path = Path.Combine(directory.FullName, Path.Combine(pathParts));
                if (File.Exists(path)) return path;
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException(
            $"Unable to find {Path.Combine(pathParts)} from {Directory.GetCurrentDirectory()} or {AppContext.BaseDirectory}");
    }
}
