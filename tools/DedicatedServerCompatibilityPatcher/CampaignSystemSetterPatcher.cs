using System.Security.Cryptography;
using Mono.Cecil;

namespace DedicatedServerCompatibilityPatcher;

public static class CampaignSystemSetterPatcher
{
    public const string PinnedV148InputSha256 = "1f8e33e2ed73e6ec653d7629180afb70649ddc6e5bd1657a802a264efda1c3ae";
    public const int PinnedV148SetterCount = 1274;
    public const int PinnedV148ConcreteSetterCount = 1264;

    public static int Patch(string inputPath, string outputPath) =>
        Patch(inputPath, outputPath, PinnedV148InputSha256, PinnedV148SetterCount, PinnedV148ConcreteSetterCount);

    public static int Patch(
        string inputPath,
        string outputPath,
        string expectedInputSha256,
        int expectedSetterCount,
        int expectedConcreteSetterCount)
    {
        (string input, string output) = ValidatePaths(inputPath, outputPath);
        string inputHash = Sha256(input);
        if (!string.Equals(inputHash, expectedInputSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Unsupported TaleWorlds.CampaignSystem input SHA-256 '{inputHash}'; expected '{expectedInputSha256}'.");

        using DefaultAssemblyResolver resolver = new();
        resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters
        {
            InMemory = true,
            ReadWrite = false,
            AssemblyResolver = resolver,
        });
        if (!string.Equals(assembly.Name.Name, "TaleWorlds.CampaignSystem", StringComparison.Ordinal) ||
            assembly.Name.Version != new Version(1, 0, 0, 0))
            throw new InvalidOperationException($"Unsupported campaign-system identity '{assembly.Name.FullName}'.");

        MethodDefinition[] setters = assembly.MainModule.GetTypes()
            .SelectMany(type => type.Methods)
            .Where(method => method.IsSetter)
            .ToArray();
        MethodDefinition[] concreteSetters = setters.Where(method => method.HasBody).ToArray();
        if (setters.Length != expectedSetterCount || concreteSetters.Length != expectedConcreteSetterCount)
            throw new InvalidOperationException(
                "Unsupported TaleWorlds.CampaignSystem setter shape: " +
                $"found {setters.Length} setters/{concreteSetters.Length} concrete; " +
                $"expected {expectedSetterCount}/{expectedConcreteSetterCount}.");
        if (concreteSetters.Any(method =>
                (method.ImplAttributes & MethodImplAttributes.NoInlining) != 0))
            throw new InvalidOperationException("TaleWorlds.CampaignSystem input is already setter-patched.");

        foreach (MethodDefinition setter in concreteSetters)
            setter.ImplAttributes |= MethodImplAttributes.NoInlining;

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string outputTemp = output + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            assembly.Write(outputTemp);
            File.Move(outputTemp, output);
        }
        finally
        {
            if (File.Exists(outputTemp)) File.Delete(outputTemp);
        }
        return concreteSetters.Length;
    }

    private static (string Input, string Output) ValidatePaths(string inputPath, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Input and output paths are required.");
        string input = Path.GetFullPath(inputPath);
        string output = Path.GetFullPath(outputPath);
        if (!File.Exists(input)) throw new FileNotFoundException("Campaign-system input does not exist.", input);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Input and output must be different files.");
        if (File.Exists(output))
            throw new InvalidOperationException("Output path must not already exist.");
        return (input, output);
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
