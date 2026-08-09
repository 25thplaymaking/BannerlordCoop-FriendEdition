using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TaleWorlds.Library;

namespace GameInterface.Services.WorkshopMods.Fourberie;

/// <summary>
/// Canonicalizes the two external files Fourberie reads during OnSubModuleLoad. XML comments,
/// formatting, attribute order, local paths and line endings do not affect the digest; element
/// order remains significant because ListRecruitable.xml is an ordered gameplay input.
/// </summary>
internal static class FourberieConfigurationFingerprint
{
    internal const long MaximumConfigurationBytes = 2 * 1024 * 1024;

    private static readonly string[] FileNames =
    {
        "FourberieConfig.xml",
        "ListRecruitable.xml",
    };

    private static readonly string[] PersonalFileNames =
    {
        "FourberieConfig_perso.xml",
        "ListRecruitable_perso.xml",
    };

    public static bool TryComputeForAssembly(
        Assembly assembly,
        out string fingerprint,
        out IReadOnlyList<string> selectedFiles,
        out string failure)
    {
        fingerprint = null;
        selectedFiles = Array.Empty<string>();
        failure = null;

        try
        {
            var binaryPath = assembly?.Location;
            var moduleRoot = string.IsNullOrWhiteSpace(binaryPath)
                ? null
                : Directory.GetParent(binaryPath)?.Parent?.Parent?.FullName;
            if (moduleRoot == null || !Directory.Exists(moduleRoot))
            {
                failure = "cannot locate Fourberie module root for configuration verification";
                return false;
            }

            var gameModulesRoot = Path.Combine(BasePath.Name, "Modules");
            var files = new List<string>(FileNames.Length);
            for (var index = 0; index < FileNames.Length; index++)
            {
                var personal = Path.Combine(gameModulesRoot, PersonalFileNames[index]);
                var bundled = Path.Combine(moduleRoot, FileNames[index]);
                files.Add(File.Exists(personal) ? personal : bundled);
            }

            if (!TryCompute(files, out fingerprint, out failure)) return false;
            selectedFiles = files.Select(Path.GetFullPath).ToArray();
            return true;
        }
        catch (Exception exception)
        {
            failure = $"could not resolve Fourberie configuration: {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    internal static bool TryCompute(
        IReadOnlyList<string> files,
        out string fingerprint,
        out string failure)
    {
        fingerprint = null;
        failure = null;

        if (files == null || files.Count != FileNames.Length)
        {
            failure = $"Fourberie requires exactly {FileNames.Length} configuration files";
            return false;
        }

        try
        {
            var combined = new StringBuilder();
            for (var index = 0; index < files.Count; index++)
            {
                var path = files[index];
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    failure = $"missing Fourberie configuration {FileNames[index]}";
                    return false;
                }

                var length = new FileInfo(path).Length;
                if (length < 0 || length > MaximumConfigurationBytes)
                {
                    failure = $"Fourberie configuration {FileNames[index]} exceeds {MaximumConfigurationBytes} bytes";
                    return false;
                }

                if (!TryCanonicalizeXml(path, out var canonical, out failure)) return false;
                AppendFramed(combined, FileNames[index]);
                AppendFramed(combined, canonical);
            }

            using (var sha = SHA256.Create())
                fingerprint = ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(combined.ToString())));
            return true;
        }
        catch (Exception exception)
        {
            failure = $"could not fingerprint Fourberie configuration: {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    internal static bool TryCanonicalizeXml(string path, out string canonical, out string failure)
    {
        canonical = null;
        failure = null;

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersInDocument = MaximumConfigurationBytes,
                MaxCharactersFromEntities = 0,
            };

            XDocument document;
            using (var reader = XmlReader.Create(path, settings))
                document = XDocument.Load(reader, LoadOptions.None);

            if (document.Root == null)
            {
                failure = $"Fourberie configuration {Path.GetFileName(path)} has no root element";
                return false;
            }

            var builder = new StringBuilder();
            AppendElement(builder, document.Root);
            canonical = builder.ToString();
            return true;
        }
        catch (Exception exception) when (
            exception is XmlException ||
            exception is IOException ||
            exception is UnauthorizedAccessException)
        {
            failure = $"invalid Fourberie configuration {Path.GetFileName(path)}: {exception.Message}";
            return false;
        }
    }

    private static void AppendElement(StringBuilder builder, XElement element)
    {
        builder.Append('E');
        AppendFramed(builder, element.Name.NamespaceName);
        AppendFramed(builder, element.Name.LocalName);

        var attributes = element.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .OrderBy(attribute => attribute.Name.NamespaceName, StringComparer.Ordinal)
            .ThenBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
            .ToArray();
        builder.Append(attributes.Length.ToString(CultureInfo.InvariantCulture)).Append(';');
        foreach (var attribute in attributes)
        {
            AppendFramed(builder, attribute.Name.NamespaceName);
            AppendFramed(builder, attribute.Name.LocalName);
            AppendFramed(builder, NormalizeText(attribute.Value));
        }

        foreach (var node in element.Nodes())
        {
            if (node is XElement child)
            {
                AppendElement(builder, child);
                continue;
            }

            if (node is XText text)
            {
                var normalized = NormalizeText(text.Value);
                if (normalized.Length == 0) continue;
                builder.Append('T');
                AppendFramed(builder, normalized);
            }
        }

        builder.Append('Z');
    }

    private static string NormalizeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }

    private static void AppendFramed(StringBuilder builder, string value)
    {
        value ??= string.Empty;
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':').Append(value).Append(';');
    }

    private static string ToHex(byte[] bytes)
    {
        const string digits = "0123456789abcdef";
        var characters = new char[bytes.Length * 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            characters[index * 2] = digits[bytes[index] >> 4];
            characters[(index * 2) + 1] = digits[bytes[index] & 15];
        }
        return new string(characters);
    }
}
