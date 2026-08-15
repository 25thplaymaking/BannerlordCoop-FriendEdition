using System.Security.Cryptography;
using System.Xml.Linq;

namespace DedicatedServerCompatibilityPatcher;

public static class SandBoxServerDescriptorPatcher
{
    public const string PinnedV148InputSha256 = "179168441d5696c64e9a7bb53ea93c0b61302e40fd20b504c0fc57141a9c02ec";
    public const string PinnedV148OutputSha256 = "960e047adcc054ab9b6805862bfb1531d03212b814843782fc97034a60bc8b5b";
    public const string GameplayClass = "SandBox.SandBoxSubModule";

    public static void Patch(string inputPath, string outputPath) =>
        Patch(inputPath, outputPath, PinnedV148InputSha256, PinnedV148OutputSha256);

    public static void Patch(string inputPath, string outputPath, string expectedInputSha256, string? expectedOutputSha256)
    {
        (string input, string output) = ValidatePaths(inputPath, outputPath);
        string inputHash = Sha256(input);
        if (!string.Equals(inputHash, expectedInputSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Unsupported SandBox v1.4.8 descriptor SHA-256 '{inputHash}'; expected '{expectedInputSha256}'.");

        string xml = File.ReadAllText(input);
        XDocument document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        XElement[] gameplaySubModules = document.Descendants("SubModule")
            .Where(element => string.Equals(
                element.Element("SubModuleClassType")?.Attribute("value")?.Value,
                GameplayClass,
                StringComparison.Ordinal))
            .ToArray();
        if (gameplaySubModules.Length != 1)
            throw new InvalidOperationException(
                $"Unsupported SandBox descriptor: expected one {GameplayClass}; found {gameplaySubModules.Length}.");

        XElement gameplay = gameplaySubModules[0];
        XElement[] tagContainers = gameplay.Elements("Tags").ToArray();
        XElement[] tags = tagContainers.SelectMany(element => element.Elements("Tag")).ToArray();
        if (tagContainers.Length != 1 || tags.Length != 2 ||
            !tags.Any(IsDedicatedServerNone) || !tags.Any(IsNoRenderFalse))
            throw new InvalidOperationException(
                "Unsupported SandBox gameplay tag shape; refusing to broaden the server overlay.");

        XElement[] presentationSubModules = document.Descendants("SubModule")
            .Where(element => !ReferenceEquals(element, gameplay))
            .ToArray();
        if (presentationSubModules.Length != 2 || presentationSubModules.Any(element => element.Element("Tags") == null))
            throw new InvalidOperationException(
                "Unsupported SandBox presentation shape; expected tagged View and GauntletUI submodules.");

        const string tagsCrLf =
            "\t\t\t<Tags>\r\n" +
            "\t\t\t\t<Tag key=\"DedicatedServerType\" value=\"none\" />\r\n" +
            "\t\t\t\t<Tag key=\"IsNoRenderModeElement\" value=\"false\" />\r\n" +
            "\t\t\t</Tags>\r\n";
        const string tagsLf =
            "\t\t\t<Tags>\n" +
            "\t\t\t\t<Tag key=\"DedicatedServerType\" value=\"none\" />\n" +
            "\t\t\t\t<Tag key=\"IsNoRenderModeElement\" value=\"false\" />\n" +
            "\t\t\t</Tags>\n";
        string marker = xml.Contains(tagsCrLf, StringComparison.Ordinal) ? tagsCrLf : tagsLf;
        int first = xml.IndexOf(marker, StringComparison.Ordinal);
        if (first < 0)
            throw new InvalidOperationException("Unsupported SandBox descriptor formatting; expected the exact gameplay tag block.");

        string patched = xml.Remove(first, marker.Length);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string outputTemp = output + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(outputTemp, patched);
            string outputHash = Sha256(outputTemp);
            if (expectedOutputSha256 != null &&
                !string.Equals(outputHash, expectedOutputSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Unexpected patched SandBox descriptor SHA-256 '{outputHash}'; expected '{expectedOutputSha256}'.");
            File.Move(outputTemp, output);
        }
        finally
        {
            if (File.Exists(outputTemp)) File.Delete(outputTemp);
        }
    }

    private static bool IsDedicatedServerNone(XElement tag) =>
        tag.Attribute("key")?.Value == "DedicatedServerType" && tag.Attribute("value")?.Value == "none";

    private static bool IsNoRenderFalse(XElement tag) =>
        tag.Attribute("key")?.Value == "IsNoRenderModeElement" && tag.Attribute("value")?.Value == "false";

    private static (string Input, string Output) ValidatePaths(string inputPath, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Input and output paths are required.");
        string input = Path.GetFullPath(inputPath);
        string output = Path.GetFullPath(outputPath);
        if (!File.Exists(input)) throw new FileNotFoundException("SandBox descriptor input does not exist.", input);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Input and output must be different files.");
        if (File.Exists(output)) throw new InvalidOperationException("Output path must not already exist.");
        return (input, output);
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
