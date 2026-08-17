using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GameInterface.Services.WorkshopMods.Core;

public sealed class WorkshopModuleHashResult
{
    public WorkshopModuleHashResult(string contentSha256, string configurationSha256, bool subModuleOnly = false)
    {
        ContentSha256 = contentSha256;
        ConfigurationSha256 = configurationSha256;
        SubModuleOnly = subModuleOnly;
    }

    public string ContentSha256 { get; }
    public string ConfigurationSha256 { get; }

    /// <summary>
    /// True when the module root contains nothing hashable beyond its root SubModule.xml — the
    /// shape of a headless-server stub, whose engine cannot load the module's real content.
    /// </summary>
    public bool SubModuleOnly { get; }
}

/// <summary>
/// Hashes every shipped module file deterministically while keeping host-owned runtime settings
/// out of the package contract. Paths are normalized and sorted, so install location and file-system
/// enumeration order cannot affect the result.
/// </summary>
public sealed class WorkshopModuleFileHasher
{
    internal const int MaximumFiles = 16_384;
    internal const long MaximumTotalBytes = 16L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> MutableFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mod-config.json",
        "coop-options.json",
        "server-config.json",
        // Improved Garrisons appends to this file inside its OWN ModuleData folder whenever one of
        // its methods throws — and it throws on every campaign tick of the headless host, where
        // IGSaveFilePath.get_SaveFilesPath() has no player profile to resolve. The package bytes
        // therefore drift the moment the server runs, even though nothing was ever installed or
        // edited. It is a log that happens to carry an .xml extension, so the ".log" rule below
        // never caught it and the .xml rule filed it under CONFIGURATION: the host advertised
        // itself as an unmanaged copy and refused every join with a configuration mismatch that
        // no player could act on. No shipped package contains a file with this name.
        "errorlog.xml",
    };

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".pdb", ".md", ".bak", ".tmp",
    };

    private static readonly HashSet<string> ConfigurationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".json", ".config", ".ini", ".yaml", ".yml", ".csv", ".txt",
    };

    public WorkshopModuleHashResult Hash(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("A module root is required.", nameof(rootPath));

        string root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Module root '{root}' does not exist.");

        var files = EnumerateFiles(root)
            .Select(path => new HashFile(path, NormalizeRelativePath(root, path)))
            .Where(file => ShouldHash(file.RelativePath))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();

        if (files.Length > MaximumFiles)
            throw new InvalidDataException($"Module root contains more than {MaximumFiles} package files.");

        long totalBytes = 0;
        foreach (var file in files)
        {
            totalBytes = checked(totalBytes + new FileInfo(file.FullPath).Length);
            if (totalBytes > MaximumTotalBytes)
                throw new InvalidDataException("Module package exceeds the hashing safety limit.");
        }

        var configuration = files.Where(file => IsConfiguration(file.RelativePath)).ToArray();
        var content = files.Where(file => !IsConfiguration(file.RelativePath)).ToArray();
        bool subModuleOnly = files.Length == 1 &&
            string.Equals(files[0].RelativePath, "submodule.xml", StringComparison.Ordinal);
        return new WorkshopModuleHashResult(HashFiles(content), HashFiles(configuration), subModuleOnly);
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(current))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Module package contains a reparse-point file.");
                yield return file;
            }

            foreach (string directory in Directory.EnumerateDirectories(current))
            {
                var attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                pending.Push(directory);
            }
        }
    }

    // The dedicated-server host overlays each module's client binaries into a server bin folder
    // (TaleWorlds' server engine only resolves bin\Win64_Shipping_Server). The shipped package
    // never contains that folder — it is a host-side duplicate of already-hashed client bytes —
    // so it must not perturb the package contract on the peer that adds it.
    private const string ServerBinOverlayPrefix = "bin/win64_shipping_server/";

    private static bool ShouldHash(string relativePath)
    {
        if (relativePath.StartsWith(ServerBinOverlayPrefix, StringComparison.Ordinal)) return false;
        string fileName = Path.GetFileName(relativePath);
        if (MutableFileNames.Contains(fileName)) return false;
        return !IgnoredExtensions.Contains(Path.GetExtension(fileName));
    }

    private static bool IsConfiguration(string relativePath)
    {
        string extension = Path.GetExtension(relativePath);
        return ConfigurationExtensions.Contains(extension);
    }

    private static string NormalizeRelativePath(string root, string path)
    {
        string relative = path.Substring(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .ToLowerInvariant();
        return relative;
    }

    private static string HashFiles(IEnumerable<HashFile> files)
    {
        var lines = new List<string>();
        foreach (var file in files.OrderBy(value => value.RelativePath, StringComparer.Ordinal))
        {
            if (file.RelativePath.IndexOfAny(new[] { '|', '\r', '\n' }) >= 0)
                throw new InvalidDataException("Module package contains a path that cannot be represented in its receipt.");

            using var stream = new FileStream(
                file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.SequentialScan);
            using var fileHash = SHA256.Create();
            string sha256 = WorkshopCompatibilityManifest.ToHex(fileHash.ComputeHash(stream));
            lines.Add(string.Join("|",
                file.RelativePath,
                stream.Length.ToString(CultureInfo.InvariantCulture),
                sha256));
        }

        using var aggregate = SHA256.Create();
        byte[] canonical = Encoding.UTF8.GetBytes(string.Join("\n", lines));
        return WorkshopCompatibilityManifest.ToHex(aggregate.ComputeHash(canonical));
    }

    private sealed class HashFile
    {
        public HashFile(string fullPath, string relativePath)
        {
            FullPath = fullPath;
            RelativePath = relativePath;
        }

        public string FullPath { get; }
        public string RelativePath { get; }
    }
}
