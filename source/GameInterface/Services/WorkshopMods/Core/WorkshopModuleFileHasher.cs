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
    public WorkshopModuleHashResult(string contentSha256, string configurationSha256)
    {
        ContentSha256 = contentSha256;
        ConfigurationSha256 = configurationSha256;
    }

    public string ContentSha256 { get; }
    public string ConfigurationSha256 { get; }
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
        return new WorkshopModuleHashResult(HashFiles(content), HashFiles(configuration));
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

    private static bool ShouldHash(string relativePath)
    {
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
