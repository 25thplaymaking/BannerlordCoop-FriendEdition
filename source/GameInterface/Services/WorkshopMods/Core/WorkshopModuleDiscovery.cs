using Common.Logging;
using GameInterface.Services.Modules;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaleWorlds.Library;
using TaleWorldsModuleHelper = TaleWorlds.ModuleManager.ModuleHelper;

namespace GameInterface.Services.WorkshopMods.Core;

public sealed class WorkshopModuleRuntimeInfo
{
    public WorkshopModuleRuntimeInfo(
        WorkshopModuleExpectation expectation,
        string version,
        string rootPath,
        bool active,
        bool managedDistributionComponent,
        bool activationOrderValid,
        string pinnedContentSha256,
        string pinnedConfigurationSha256)
    {
        Expectation = expectation ?? throw new ArgumentNullException(nameof(expectation));
        Version = version ?? throw new ArgumentNullException(nameof(version));
        RootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
        Active = active;
        ManagedDistributionComponent = managedDistributionComponent;
        ActivationOrderValid = activationOrderValid;
        PinnedContentSha256 = pinnedContentSha256;
        PinnedConfigurationSha256 = pinnedConfigurationSha256;
    }

    public WorkshopModuleExpectation Expectation { get; }
    public string Version { get; }
    public string RootPath { get; }
    public bool Active { get; }
    public bool ManagedDistributionComponent { get; }
    public bool ActivationOrderValid { get; }
    public string PinnedContentSha256 { get; }
    public string PinnedConfigurationSha256 { get; }
}

/// <summary>
/// Injectable boundary around TaleWorlds module discovery. E2E tests and packaging checks can
/// supply exact module roots without mutating the process-wide ModuleHelper state.
/// </summary>
public interface IWorkshopModuleDiscovery : IGameAbstraction
{
    IReadOnlyList<WorkshopModuleRuntimeInfo> Discover();
}

public sealed class RuntimeWorkshopModuleDiscovery : IWorkshopModuleDiscovery
{
    private const string CoopModuleId = "Coop";
    private static readonly ILogger Logger = LogManager.GetLogger<RuntimeWorkshopModuleDiscovery>();

    private readonly IModuleInfoProvider moduleInfoProvider;
    private readonly IWorkshopModuleCatalog catalog;
    private readonly Func<string, string> modulePathResolver;
    private readonly IWorkshopSuiteReceiptProvider receiptProvider;

    public RuntimeWorkshopModuleDiscovery(IModuleInfoProvider moduleInfoProvider)
        : this(moduleInfoProvider, new FriendEditionWorkshopModuleCatalog(), ResolveModulePath, null)
    {
    }

    public RuntimeWorkshopModuleDiscovery(
        IModuleInfoProvider moduleInfoProvider,
        IWorkshopModuleCatalog catalog,
        Func<string, string> modulePathResolver,
        IWorkshopSuiteReceiptProvider receiptProvider = null)
    {
        this.moduleInfoProvider = moduleInfoProvider ?? throw new ArgumentNullException(nameof(moduleInfoProvider));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.modulePathResolver = modulePathResolver ?? throw new ArgumentNullException(nameof(modulePathResolver));
        this.receiptProvider = receiptProvider ?? new WorkshopSuiteReceiptProvider(catalog);
    }

    public IReadOnlyList<WorkshopModuleRuntimeInfo> Discover()
    {
        ModuleInfo[] activeModuleList = (moduleInfoProvider.GetModuleInfos() ?? Enumerable.Empty<ModuleInfo>())
            .ToArray();
        var activeModules = activeModuleList
            .Where(module => !string.IsNullOrWhiteSpace(module.Id))
            .GroupBy(module => module.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        string coopRoot = ResolveCoopRoot();
        WorkshopSuiteReceipt receipt = receiptProvider.Load(coopRoot);
        bool activationOrderValid = IsActivationOrderValid(activeModuleList);
        var result = new List<WorkshopModuleRuntimeInfo>(catalog.Modules.Count);

        foreach (var expectation in catalog.Modules)
        {
            if (activeModules.TryGetValue(expectation.ModuleId, out ModuleInfo active))
            {
                string externalRoot = modulePathResolver(expectation.ModuleId);
                WorkshopSuiteReceiptEntry receiptEntry = null;
                bool hasReceiptEntry = receipt != null &&
                    receipt.TryGet(expectation.ModuleId, out receiptEntry);
                bool versionMatches = hasReceiptEntry &&
                    IsSameVersion(receiptEntry.Version, active.Version);
                bool pathIsManaged =
                    IsManagedComponentPath(coopRoot, externalRoot, expectation.ModuleId);
                bool managed = hasReceiptEntry && versionMatches && pathIsManaged;

                // "Unmanaged copy" refuses the join, so record which specific condition failed;
                // otherwise the message cannot be told apart from a genuinely modified install.
                if (!managed)
                {
                    Logger.Warning(
                        "'{ModuleId}' is not a managed component: receiptEntry={HasEntry} " +
                        "versionMatch={VersionMatch} (receipt='{ReceiptVersion}' active='{ActiveVersion}') " +
                        "managedPath={PathManaged} (component='{ComponentRoot}' coop='{CoopRoot}')",
                        expectation.ModuleId,
                        hasReceiptEntry,
                        versionMatches,
                        receiptEntry?.Version ?? "<none>",
                        active.Version.ToString(),
                        pathIsManaged,
                        externalRoot ?? "<null>",
                        coopRoot ?? "<null>");
                }
                result.Add(new WorkshopModuleRuntimeInfo(
                    expectation,
                    active.Version.ToString(),
                    RequireDirectory(externalRoot, expectation.ModuleId),
                    active: true,
                    managedDistributionComponent: managed,
                    activationOrderValid: activationOrderValid,
                    pinnedContentSha256: managed ? receiptEntry.ContentSha256 : null,
                    pinnedConfigurationSha256: managed ? receiptEntry.ConfigurationSha256 : null));
                continue;
            }

            // Any receipt-backed component may be staged but inactive on a headless server. Keep
            // package presence distinct from feature activation: the provider advertises its trusted
            // digest and Active=false, while compatibility adapters remain feature-guarded.
            if (receipt != null &&
                receipt.TryGet(expectation.ModuleId, out WorkshopSuiteReceiptEntry stagedEntry))
            {
                string stagedRoot = modulePathResolver(expectation.ModuleId);
                if (IsManagedComponentPath(coopRoot, stagedRoot, expectation.ModuleId))
                {
                    result.Add(new WorkshopModuleRuntimeInfo(
                        expectation,
                        stagedEntry.Version,
                        Path.GetFullPath(stagedRoot),
                        active: false,
                        managedDistributionComponent: true,
                        activationOrderValid: activationOrderValid,
                        pinnedContentSha256: stagedEntry.ContentSha256,
                        pinnedConfigurationSha256: stagedEntry.ConfigurationSha256));
                }
            }
        }

        return result;
    }

    private bool IsActivationOrderValid(IReadOnlyList<ModuleInfo> activeModules)
    {
        string[] activeIds = activeModules
            .Where(module => !string.IsNullOrWhiteSpace(module.Id))
            .Select(module => module.Id)
            .ToArray();
        var activeSet = new HashSet<string>(activeIds, StringComparer.OrdinalIgnoreCase);
        string[] actualManagedOrder = activeIds
            .Where(id => catalog.TryGet(id, out _))
            .ToArray();

        // Managed components occupy two slots around Coop: everything that must be loaded before
        // Coop (frameworks, plus adapters that re-guard a module's load-time patches) and
        // everything that loads after it. Each group keeps its pinned relative LoadOrder.
        bool BeforeCoop(WorkshopModuleExpectation module) =>
            module.Role == WorkshopModuleRole.Framework || module.LoadsBeforeCoop;

        var activeCatalogModules = catalog.Modules
            .Where(module => activeSet.Contains(module.ModuleId))
            .ToArray();
        string[] expectedManagedOrder = activeCatalogModules
            .Where(BeforeCoop)
            .OrderBy(module => module.LoadOrder)
            .Concat(activeCatalogModules.Where(module => !BeforeCoop(module)).OrderBy(module => module.LoadOrder))
            .Select(module => module.ModuleId)
            .ToArray();

        if (!actualManagedOrder.SequenceEqual(expectedManagedOrder, StringComparer.OrdinalIgnoreCase))
            return false;

        int nativeIndex = Array.FindIndex(activeIds,
            id => string.Equals(id, "Native", StringComparison.OrdinalIgnoreCase));
        int coopIndex = Array.FindIndex(activeIds,
            id => string.Equals(id, CoopModuleId, StringComparison.OrdinalIgnoreCase));
        if (nativeIndex < 0 || coopIndex < 0) return false;

        foreach (var module in activeCatalogModules)
        {
            int moduleIndex = Array.FindIndex(activeIds,
                id => string.Equals(id, module.ModuleId, StringComparison.OrdinalIgnoreCase));
            if (module.Role == WorkshopModuleRole.Framework)
            {
                if (moduleIndex >= nativeIndex) return false;
            }
            else if (module.LoadsBeforeCoop)
            {
                // After the natives (it reads native campaign types) but before Coop.
                if (moduleIndex <= nativeIndex || moduleIndex >= coopIndex) return false;
            }
            else if (moduleIndex <= coopIndex)
            {
                return false;
            }
        }

        return true;
    }

    private string ResolveCoopRoot()
    {
        string coopRoot = modulePathResolver(CoopModuleId);
        if (!string.IsNullOrWhiteSpace(coopRoot) && Directory.Exists(coopRoot))
            return Path.GetFullPath(coopRoot);

        string candidate = Path.GetDirectoryName(typeof(RuntimeWorkshopModuleDiscovery).Assembly.Location);
        for (int depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(candidate); depth++)
        {
            if (File.Exists(Path.Combine(candidate, "WorkshopSuite", "MANIFEST.json")))
                return Path.GetFullPath(candidate);
            candidate = Path.GetDirectoryName(candidate);
        }

        return null;
    }

    /// <summary>
    /// Compares versions by their parsed value, not their formatted string. ApplicationVersion.FromString
    /// defaults an absent change-set segment to 0, so a pinned three-part version like "v2.11.1" round-trips
    /// through ToString() as "v2.11.1.0" — a raw string comparison against the receipt's original three-part
    /// text would spuriously reject every module whose upstream author never publishes a change-set segment.
    /// </summary>
    private static bool IsSameVersion(string pinnedVersion, ApplicationVersion active)
    {
        if (string.IsNullOrWhiteSpace(pinnedVersion)) return false;
        try
        {
            return ApplicationVersion.FromString(pinnedVersion).IsSame(active, checkChangeSet: true);
        }
        catch (Exception)
        {
            // ApplicationVersion.FromString throws a bare Exception (not a specific type) on malformed
            // input. The receipt is untrusted disk content by the time it reaches here in production
            // (though this test harness's FakeReceiptProvider bypasses TryValidate's format check), so
            // treat anything unparsable as "not managed" rather than letting discovery crash.
            return false;
        }
    }

    private static bool IsManagedComponentPath(string coopRoot, string componentRoot, string moduleId)
    {
        if (string.IsNullOrWhiteSpace(coopRoot) || string.IsNullOrWhiteSpace(componentRoot) ||
            string.IsNullOrWhiteSpace(moduleId) || !Directory.Exists(componentRoot))
            return false;

        string normalizedCoop = Path.GetFullPath(coopRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedComponent = Path.GetFullPath(componentRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string suiteModulesRoot = Path.GetDirectoryName(normalizedCoop);
        string componentParent = Path.GetDirectoryName(normalizedComponent);
        return string.Equals(componentParent, suiteModulesRoot, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetFileName(normalizedComponent), moduleId, StringComparison.OrdinalIgnoreCase);
    }

    private static string RequireDirectory(string path, string moduleId)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            throw new DirectoryNotFoundException($"Unable to locate files for Workshop component '{moduleId}'.");
        return Path.GetFullPath(path);
    }

    private static string ResolveModulePath(string moduleId)
    {
        try
        {
            return TaleWorldsModuleHelper.GetModuleFullPath(moduleId);
        }
        catch
        {
            return null;
        }
    }
}
