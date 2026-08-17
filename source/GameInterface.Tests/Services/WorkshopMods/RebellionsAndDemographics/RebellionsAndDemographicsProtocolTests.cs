using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.WorkshopMods.RebellionsAndDemographics;
using HarmonyLib;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.RebellionsAndDemographics;

public sealed class RebellionsAndDemographicsProtocolTests
{
    private static readonly AuthorityRequestHeader Header = new(7, "session-a", 41, 9);

    [Fact]
    public void PlagueTimeFingerprint_UsesRawTicksBeforeCalendarInitialization()
    {
        Assert.Equal("12345", RebellionsAndDemographicsCompatibilityHandler.CanonicalCampaignTime(new CampaignTime(12345)));
        Assert.Equal(string.Empty, RebellionsAndDemographicsCompatibilityHandler.CanonicalCampaignTime(null!));
    }

    [Fact]
    public void SaveDefinitionCompatibility_PreservesFirstDefinition_AndSuppressesOnlyDuplicates()
    {
        Assert.True(RebellionsAndDemographicsHarmonyIsolation.ShouldRunSaveDefinitionOriginal(false));
        Assert.False(RebellionsAndDemographicsHarmonyIsolation.ShouldRunSaveDefinitionOriginal(true));
    }

    [Fact]
    public void ChoiceCommand_IsAnAuthenticatedTypedRoute_AndRejectsMalformedLease()
    {
        var valid = new NetworkRequestRebellionsAndDemographicsChoice(Header, "lease-a", RdPromptKind.Ultimatum, true, 12);
        var malformed = new NetworkRequestRebellionsAndDemographicsChoice(Header, string.Empty, (RdPromptKind)99, true, 12);

        Assert.True(valid.IsValid);
        Assert.Equal(Header.RequestId, valid.Header.RequestId);
        Assert.Equal(Header.SessionId, valid.Header.SessionId);
        Assert.False(malformed.IsValid);
        Assert.Contains(typeof(NetworkRequestRebellionsAndDemographicsChoice).GetCustomAttributes(false),
            attribute => attribute is AuthorityRouteAttribute route && route.RouteId == RebellionsAndDemographicsCompatibilityHandler.ChoiceRouteId);
    }

    [Fact]
    public void PinnedWorkshopBinary_UsesItsActualManagedIdentity_WhenLocalAuditPayloadIsPresent()
    {
        const string path = @"P:\SteamLibrary\steamapps\workshop\content\261550\3644127631\bin\Win64_Shipping_Client\RebellionsAndDemographics.dll";
        if (!File.Exists(path)) return; // CI uses a staged private suite rather than this developer audit source.

        var identity = AssemblyName.GetAssemblyName(path);
        Assert.Equal("ClassLibrary22", identity.Name);
        Assert.Equal(RebellionsAndDemographicsModule.AssemblyName, identity.Name);
    }

    [Fact]
    public void PinnedWorkshopBinary_PatchAllIsPurgedAndLifecycleGuardsSurvive_WhenLocalAuditPayloadIsPresent()
    {
        const string path = @"P:\SteamLibrary\steamapps\workshop\content\261550\3644127631\bin\Win64_Shipping_Client\RebellionsAndDemographics.dll";
        if (!File.Exists(path)) return; // CI validates the staged package, not this local Workshop source.

        Assembly upstream = Assembly.LoadFrom(path);
        Type subModule = upstream.GetType("RebellionsAndDemographics.SubModule", throwOnError: true);
        MethodInfo onLoad = subModule.GetMethod("OnSubModuleLoad", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo onGameStart = subModule.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name == "OnGameStart" && method.GetParameters().Length == 2);
        MethodInfo onMission = subModule.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name == "OnMissionBehaviorInitialize" && method.GetParameters().Length == 1);
        Type rebellionCore = upstream.GetType("RebellionsAndDemographics.RebellionCoreBehavior", throwOnError: true);
        MethodInfo tryStart = rebellionCore.GetMethod("TryStartRebellion", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo processDefeat = rebellionCore.GetMethod("ProcessRebelDefeat", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo triggerUltimatum = rebellionCore.GetMethod("TriggerPlayerUltimatum", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onLoad);
        Assert.NotNull(tryStart);
        Assert.NotNull(processDefeat);
        Assert.NotNull(triggerUltimatum);

        var adapter = new Harmony(RebellionsAndDemographicsHarmonyIsolation.AdapterOwner);
        var handler = (RebellionsAndDemographicsCompatibilityHandler)RuntimeHelpers.GetUninitializedObject(
            typeof(RebellionsAndDemographicsCompatibilityHandler));
        typeof(RebellionsAndDemographicsCompatibilityHandler).GetField("harmony", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(handler, adapter);
        RebellionsAndDemographicsRuntime.Current = handler;
        try
        {
            bool installed = (bool)typeof(RebellionsAndDemographicsCompatibilityHandler)
                .GetMethod("TryInstall", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(handler, null);
            Assert.True(installed);
            AssertPrefix(onGameStart, adapter.Id);
            AssertPrefix(onMission, adapter.Id);
            AssertPrefix(tryStart, adapter.Id);
            AssertPrefix(processDefeat, adapter.Id);
            AssertPrefix(triggerUltimatum, adapter.Id);
            AssertPrefix(typeof(TaleWorlds.SaveSystem.SaveableTypeDefiner).GetMethod(
                "ConstructContainerDefinition", BindingFlags.Instance | BindingFlags.NonPublic), adapter.Id);
            AssertPrefix(typeof(TaleWorlds.SaveSystem.SaveableTypeDefiner).GetMethods(
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(method => method.Name == "AddClassDefinition" && method.GetParameters().Length == 3), adapter.Id);

            // This executes the upstream PatchAll. The adapter's previously installed postfix must
            // remove every upstream-owned patch before control returns.
            onLoad.Invoke(Activator.CreateInstance(subModule), null);
            Assert.All(Harmony.GetAllPatchedMethods(), original => Assert.DoesNotContain(
                Enumerate(Harmony.GetPatchInfo(original)), patch => patch.owner == RebellionsAndDemographicsHarmonyIsolation.UpstreamOwner ||
                    patch.PatchMethod?.DeclaringType?.Assembly == upstream));
            AssertPrefix(onGameStart, adapter.Id);
            AssertPrefix(onMission, adapter.Id);
        }
        finally
        {
            RebellionsAndDemographicsRuntime.Current = null;
            adapter.UnpatchAll(RebellionsAndDemographicsHarmonyIsolation.AdapterOwner);
        }
    }

    [Fact]
    public void PinnedAllowlist_HasNoDependencyOnOmittedBehaviorSingletons_WhenLocalAuditPayloadIsPresent()
    {
        const string binaryPath = @"P:\SteamLibrary\steamapps\workshop\content\261550\3644127631\bin\Win64_Shipping_Client\RebellionsAndDemographics.dll";
        if (!File.Exists(binaryPath)) return;

        string inventoryPath = FindRepositoryFile("doc", "generated", "workshop-function-inventory.json");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(inventoryPath));
        JsonElement assembly = document.RootElement.GetProperty("assemblies").EnumerateArray()
            .Single(value => value.GetProperty("moduleId").GetString() == "RebellionsAndDemographics");
        string[] allowlisted = { "PopulationBehavior", "PlagueBehavior", "RebellionCoreBehavior", "RecruitmentLimiterBehavior", "DemographicsBehavior" };
        string[] omitted = { "Corruption", "Government", "Stability", "ShadowGarrison", "Schism", "Strike", "DiplomacyDialog" };

        var forbidden = assembly.GetProperty("methods").EnumerateArray()
            .Where(method => allowlisted.Any(type => IsBehaviorMember(method.GetProperty("declaringType").GetString(), type)))
            .SelectMany(method => method.GetProperty("authorityEvidence").GetProperty("calledMembers").EnumerateArray())
            .Select(value => value.GetString())
            .Where(member => member != null && omitted.Any(type => IsBehaviorMember(member, type)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(forbidden);
    }

    private static bool IsBehaviorMember(string member, string type) =>
        member.StartsWith("RebellionsAndDemographics." + type, StringComparison.Ordinal) &&
        (member.Length == "RebellionsAndDemographics.".Length + type.Length ||
         member["RebellionsAndDemographics.".Length + type.Length] is '.' or '+');

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

    private static System.Collections.Generic.IEnumerable<Patch> Enumerate(Patches patches)
    {
        if (patches == null) yield break;
        foreach (var patch in patches.Prefixes) yield return patch;
        foreach (var patch in patches.Postfixes) yield return patch;
        foreach (var patch in patches.Transpilers) yield return patch;
        foreach (var patch in patches.Finalizers) yield return patch;
    }

    private static void AssertPrefix(MethodBase method, string owner) =>
        Assert.Contains(Harmony.GetPatchInfo(method).Prefixes, patch => patch.owner == owner);

    [Fact]
    public void CanonicalState_UsesStructuredBoundedCultures_AndCorrelatedPromptTombstone()
    {
        var lease = new RdPromptLease("lease-a", RdPromptKind.Defeat, "session-a", "hero-owner", "rebels",
            new[] { "clan-a", "clan-b" }, 0, 12, 15);
        var tombstone = new RdPromptTombstone(lease, Header.RequestId, accepted: false, completed: true);
        var settlement = new RdSettlementPopulationState("town_A", 4200, 1200, 0, 24,
            new[] { new RdCulturePopulationState("empire", 4000), new RdCulturePopulationState("vlandia", 200) });
        var state = new RebellionsAndDemographicsState("session-a", 13, new[] { "PopulationBehavior" },
            new[] { settlement }, new RdPlagueState("town_A", 3, "day-14"), Array.Empty<RdPromptLease>(), new[] { tombstone }, Array.Empty<RdInterventionWatermark>());

        Assert.Equal(64, state.Fingerprint.Length);
        Assert.Equal(new[] { "empire", "vlandia" }, state.Settlements.Single().Cultures.Select(value => value.CultureId));
        Assert.All(state.Settlements.Single().Cultures, culture => Assert.DoesNotContain("CultureObject", culture.CultureId));
        Assert.Single(state.PromptTombstones);
        Assert.Equal(Header.RequestId, state.PromptTombstones[0].AuthorityRequestId);
        Assert.True(state.PromptTombstones[0].Completed);
    }

    [Fact]
    public void InterventionWatermark_CorrelatesActorTargetAndExactPostconditions()
    {
        var watermark = new RdInterventionWatermark(Header.RequestId, "hero-owner", "clan-owner", "hero-target",
            "clan-target", "kingdom-old", "kingdom-new", 123, 456.5f, 3);

        Assert.Equal(Header.RequestId, watermark.AuthorityRequestId);
        Assert.Equal("kingdom-new", watermark.NewKingdomId);
        Assert.Equal(123, watermark.PostActorGold);
        Assert.Equal(3, watermark.AllyCount);
    }

    [Fact]
    public void CanonicalState_RetainsConcurrentInterventionWatermarks()
    {
        var first = new RdInterventionWatermark(41, "hero-a", "clan-a", "hero-target-a", "clan-target-a", "old-a", "new-a", 1, 2, 3);
        var second = new RdInterventionWatermark(42, "hero-b", "clan-b", "hero-target-b", "clan-target-b", "old-b", "new-b", 4, 5, 4);
        var state = new RebellionsAndDemographicsState("session-a", 13, Array.Empty<string>(), Array.Empty<RdSettlementPopulationState>(),
            new RdPlagueState(string.Empty, 0, string.Empty), Array.Empty<RdPromptLease>(), Array.Empty<RdPromptTombstone>(), new[] { second, first });

        Assert.Equal(new long[] { 41, 42 }, state.InterventionWatermarks.Select(value => value.AuthorityRequestId));
    }
}
