using System.IO;
using System.Reflection;

namespace CoopLauncher.Services;

public enum ConversionOutcome
{
    /// <summary>Every conversion module is already present at the pinned version.</summary>
    UpToDate,

    /// <summary>At least one module was installed or repaired.</summary>
    Installed,

    /// <summary>A Workshop-sourced module is not subscribed, so there is nothing to copy from.</summary>
    MissingSubscription,

    /// <summary>Something went wrong; the message names it. Launch is not blocked.</summary>
    Failed,
}

public readonly record struct ConversionResult(ConversionOutcome Outcome, string Message, IReadOnlyList<string> MissingWorkshopIds)
{
    public ConversionResult(ConversionOutcome outcome, string message) : this(outcome, message, []) { }
}

/// <summary>
/// Installs the Empires of Europe 1100 conversion set into <c>Modules\</c> without shipping its
/// bytes.
/// </summary>
/// <remarks>
/// The three EoE modules are public Steam Workshop items totalling ~4.6 GB, and their
/// <c>Modules\</c> copies are byte-identical to the subscribed Workshop copies **except** for
/// <c>SubModule.xml</c>, which Friend Edition re-pins to v1.4.8 dependencies. So the launcher lets
/// Steam carry the payload — it copies the subscribed content across and overlays the re-pinned
/// manifest from its own resources. That keeps the launcher download at a few megabytes and avoids
/// putting a 4.9 GB asset through the release feed.
/// <para>
/// None of these modules are in the Friend Edition catalog, so none of them are hashed by the join
/// handshake. This bootstrap is therefore best-effort and must never block a launch: a friend who
/// has not subscribed gets a clear message naming the Workshop items, not a refused session.
/// </para>
/// </remarks>
public sealed class ConversionBootstrap
{
    private const string BannerlordAppId = "261550";
    private const string ResourcePrefix = "CoopLauncher.Assets.Conversion.";

    /// <param name="ModuleId">Folder name under <c>Modules\</c>, and the module's declared Id.</param>
    /// <param name="WorkshopId">Steam Workshop item to copy from.</param>
    /// <param name="Version">The version the re-pinned manifest declares.</param>
    /// <param name="ManifestResource">Embedded re-pinned <c>SubModule.xml</c>.</param>
    public sealed record ConversionModule(
        string ModuleId,
        string WorkshopId,
        string Version,
        string ManifestResource);

    /// <summary>
    /// The conversion set, in the EoE author's published load order. Keep this in step with the
    /// module token in <see cref="LauncherConfig.CurrentModuleToken"/>.
    /// </summary>
    /// <remarks>
    /// gfrontsEOENamesMod is deliberately absent: it declares SPCultures XmlNames under
    /// ModuleData/gfront55_names but ships only .xslt transforms and no .xml, so it contributes
    /// nothing.
    /// <para>
    /// RBM is absent too, and could not be salvaged as a data-only component: the engine applies
    /// its combat-parameter XML whether or not RBM.SubModule loads, which is the native path the
    /// dedicated host died on (exit 84, right after "Combat parameter overriden"). It was already
    /// retired project-wide on 2026-08-11 for that same fault.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<ConversionModule> Conversion =
    [
        new("Europe1100", "2968204274", "v1.4.7.3", "Europe1100.SubModule.xml"),
        new("Europe1100Expanded", "3671120238", "v1.0.1", "Europe1100Expanded.SubModule.xml"),
        new("SnowballingKingdoms - EOE 1100", "3413432422", "v1.0.21", "SnowballingKingdoms.SubModule.xml"),
    ];

    private readonly Assembly _resources;

    public ConversionBootstrap() : this(typeof(ConversionBootstrap).Assembly) { }

    internal ConversionBootstrap(Assembly resources) => _resources = resources;

    /// <summary>
    /// Brings every conversion module up to its pinned version. Never throws.
    /// </summary>
    /// <param name="modulesDir">The resolved install's <c>Modules</c> folder.</param>
    public ConversionResult Ensure(string modulesDir, bool neutralizeShaderCache = false)
    {
        if (string.IsNullOrWhiteSpace(modulesDir) || !Directory.Exists(modulesDir))
            return new ConversionResult(ConversionOutcome.Failed, "The Bannerlord Modules folder could not be found.");

        var installed = new List<string>();
        var missing = new List<string>();
        var failures = new List<string>();

        foreach (ConversionModule module in Conversion)
        {
            try
            {
                string target = Path.Combine(modulesDir, module.ModuleId);
                byte[] manifest = ReadResource(module.ManifestResource);

                string? source = FindWorkshopContent(modulesDir, module.WorkshopId);
                if (source == null)
                {
                    missing.Add(module.WorkshopId);
                    continue;
                }

                // Always mirror. Steam updates the Workshop item under us, and because these
                // modules are uncatalogued the join handshake will not catch the resulting peer
                // drift — so the check has to happen here, every launch. Files that already match
                // are skipped, which makes a no-op pass cost one stat per file.
                bool changed = CopyTree(source, target);

                if (!ManifestMatches(target, manifest))
                {
                    // Written last. Until the re-pinned manifest is in place the module still
                    // declares v1.4.7.x dependencies and Bannerlord refuses to load it, so a crash
                    // between the copy and this write leaves a module that is visibly wrong rather
                    // than one that silently loads stale content.
                    WriteBytes(Path.Combine(target, "SubModule.xml"), manifest);
                    changed = true;
                }

                if (changed) installed.Add(module.ModuleId);
            }
            catch (Exception error)
            {
                failures.Add($"{module.ModuleId}: {error.Message}");
            }
        }

        if (neutralizeShaderCache)
        {
            foreach (ConversionModule module in Conversion)
            {
                try
                {
                    if (NeutralizeShaderCache(Path.Combine(modulesDir, module.ModuleId)))
                        installed.Add(module.ModuleId + " (shader cache)");
                }
                catch (Exception error)
                {
                    // Never fail a launch over this: the incomplete sack costs frame hitches, a
                    // missing module costs the session.
                    failures.Add($"{module.ModuleId} shader cache: {error.Message}");
                }
            }
        }

        if (failures.Count > 0)
            return new ConversionResult(ConversionOutcome.Failed,
                "Some Europe 1100 modules could not be installed — " + string.Join("; ", failures), missing);

        if (missing.Count > 0)
            return new ConversionResult(ConversionOutcome.MissingSubscription,
                "Subscribe to these Steam Workshop items, then press Play again: " +
                string.Join(", ", missing.Select(id => $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}")),
                missing);

        return installed.Count == 0
            ? new ConversionResult(ConversionOutcome.UpToDate, "Europe 1100 is installed and current.")
            : new ConversionResult(ConversionOutcome.Installed,
                "Installed Europe 1100 modules: " + string.Join(", ", installed));
    }

    /// <summary>
    /// Compares the installed manifest against the re-pinned one, byte for byte.
    /// </summary>
    /// <remarks>
    /// Id and Version are NOT enough. Friend Edition re-pins dependency versions only, so a raw
    /// Workshop copy declares the very same Id and Version as the patched one and would read as
    /// current while still demanding v1.4.7.x natives.
    /// </remarks>
    /// <summary>
    /// Renames a conversion module's precompiled shader caches aside so the engine falls back to the
    /// base game's complete shader sources. Returns true when it moved something.
    /// </summary>
    /// <remarks>
    /// See <c>LauncherConfig.NeutralizeConversionShaderCache</c> for why. Renamed rather than
    /// deleted so it can be put straight back, and idempotent so repeated launches do nothing once
    /// it has moved.
    /// </remarks>
    internal static bool NeutralizeShaderCache(string moduleDir)
    {
        string shaders = Path.Combine(moduleDir, "Shaders");
        if (!Directory.Exists(shaders)) return false;

        bool moved = false;
        foreach (string sack in Directory.GetFiles(shaders, "*.sack", SearchOption.AllDirectories))
        {
            string disabled = sack + ".disabled";
            if (File.Exists(disabled)) continue;

            File.Move(sack, disabled);
            moved = true;
        }

        return moved;
    }

    internal static bool ManifestMatches(string moduleDir, byte[] expected)
    {
        string manifest = Path.Combine(moduleDir, "SubModule.xml");
        try
        {
            return File.Exists(manifest) && File.ReadAllBytes(manifest).AsSpan().SequenceEqual(expected);
        }
        catch
        {
            // An unreadable manifest is stale by definition; rewriting it repairs it.
            return false;
        }
    }

    /// <summary>
    /// Resolves <c>steamapps\workshop\content\261550\&lt;id&gt;</c> from the Modules folder, which
    /// sits at <c>steamapps\common\Mount &amp; Blade II Bannerlord\Modules</c> in the same library.
    /// </summary>
    internal static string? FindWorkshopContent(string modulesDir, string workshopId)
    {
        DirectoryInfo? steamApps = Directory.GetParent(Path.GetFullPath(modulesDir))?.Parent?.Parent;
        if (!string.Equals(steamApps?.Name, "steamapps", StringComparison.OrdinalIgnoreCase)) return null;

        string content = Path.Combine(steamApps!.FullName, "workshop", "content", BannerlordAppId, workshopId);
        if (!Directory.Exists(content)) return null;

        // An unsubscribed-but-not-yet-cleaned item can leave an empty folder behind.
        return File.Exists(Path.Combine(content, "SubModule.xml")) ? content : null;
    }

    /// <summary>
    /// Mirrors the Workshop copy. Files present only in the target are removed so a downgraded
    /// Workshop item cannot leave orphans behind that the game would still load.
    /// </summary>
    private static bool CopyTree(string source, string target)
    {
        bool changed = !Directory.Exists(target);
        Directory.CreateDirectory(target);

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            wanted.Add(relative);

            // Never mirror the manifest. The re-pinned copy is written straight after this, so
            // copying the Workshop one across would undo the re-pin on every single launch and
            // leave the module demanding v1.4.7.x natives.
            if (string.Equals(relative, "SubModule.xml", StringComparison.OrdinalIgnoreCase)) continue;

            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // Skip bytes that are already right: on a 4.6 GB conversion a re-run should cost
            // seconds, not another full copy.
            var existing = new FileInfo(destination);
            var incoming = new FileInfo(file);
            if (existing.Exists && existing.Length == incoming.Length &&
                existing.LastWriteTimeUtc == incoming.LastWriteTimeUtc)
                continue;

            File.Copy(file, destination, overwrite: true);
            File.SetLastWriteTimeUtc(destination, incoming.LastWriteTimeUtc);
            changed = true;
        }

        foreach (string file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(target, file);
            // SubModule.xml is overlaid from launcher resources right after this, and is expected
            // to differ from the Workshop copy.
            if (wanted.Contains(relative) ||
                string.Equals(relative, "SubModule.xml", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Delete(file);
            changed = true;
        }

        return changed;
    }

    private static void WriteBytes(string destination, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, content);
    }

    private byte[] ReadResource(string name)
    {
        using Stream source = OpenResource(name);
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return buffer.ToArray();
    }

    private Stream OpenResource(string name) =>
        _resources.GetManifestResourceStream(ResourcePrefix + name)
        ?? throw new InvalidOperationException($"the launcher is missing its '{name}' payload");

    private static void DeleteTree(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
