using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CoopServerModKit.ButterLib;

public static class ButterLibHeadlessPatcher
{
    public const string PinnedInputSha256 = "d820692e0c02377f53804e7ec14bb35bd524644613dcdd013c5873729b262172";

    private static readonly Target[] Targets =
    {
        new("Bannerlord.ButterLib.ExceptionHandler.ExceptionHandlerSubSystem", "Enable", IsStatic: false),
        new("Bannerlord.ButterLib.CrashUploader.CrashUploaderSubSystem", "Enable", IsStatic: false),
        new("Bannerlord.ButterLib.DelayedSubModule.DelayedSubModuleSubSystem", "Enable", IsStatic: false),
        new("Bannerlord.ButterLib.SubModuleWrappers2.SubModuleWrappers2SubSystem", "Enable", IsStatic: false),
        new("Bannerlord.ButterLib.ButterLibSubModule", "ValidateLoadOrder", IsStatic: true),
    };

    public static int Patch(string inputPath, string outputPath) =>
        Patch(inputPath, outputPath, PinnedInputSha256);

    public static int Patch(string inputPath, string outputPath, string expectedInputSha256)
    {
        (string input, string output) = ValidatePaths(inputPath, outputPath);
        string actualHash = Sha256(input);
        if (!string.Equals(actualHash, expectedInputSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported ButterLib input SHA-256 '{actualHash}'; expected pinned v2.11.1 '{expectedInputSha256}'.");
        }

        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters
        {
            InMemory = true,
            ReadWrite = false,
        });
        if (!string.Equals(assembly.Name.Name, "Bannerlord.ButterLib", StringComparison.Ordinal) ||
            assembly.Name.Version != new Version(2, 11, 1, 0))
        {
            throw new InvalidOperationException(
                $"Unsupported ButterLib identity '{assembly.Name.FullName}'; expected Bannerlord.ButterLib 2.11.1.0.");
        }

        MethodDefinition[] methods = Targets.Select(target => ResolveExactTarget(assembly, target)).ToArray();
        foreach (MethodDefinition method in methods)
        {
            method.Body.Instructions.Clear();
            method.Body.Variables.Clear();
            method.Body.ExceptionHandlers.Clear();
            method.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        assembly.Write(output);
        return methods.Length;
    }

    private static MethodDefinition ResolveExactTarget(AssemblyDefinition assembly, Target target)
    {
        MethodDefinition[] matches = assembly.Modules
            .SelectMany(module => module.GetTypes())
            .Where(type => string.Equals(type.FullName, target.TypeName, StringComparison.Ordinal))
            .SelectMany(type => type.Methods)
            .Where(method =>
                string.Equals(method.Name, target.MethodName, StringComparison.Ordinal) &&
                method.IsStatic == target.IsStatic &&
                method.Parameters.Count == 0 &&
                method.ReturnType.MetadataType == MetadataType.Void)
            .ToArray();
        if (matches.Length != 1 || !matches[0].HasBody || matches[0].IsAbstract || matches[0].IsPInvokeImpl)
        {
            throw new InvalidOperationException(
                $"Unsupported ButterLib input: expected exactly one concrete {target.TypeName}::{target.MethodName}() " +
                $"returning System.Void with static={target.IsStatic}; found {matches.Length}.");
        }
        return matches[0];
    }

    private static (string Input, string Output) ValidatePaths(string inputPath, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Input and output paths are required.");
        string input = Path.GetFullPath(inputPath);
        string output = Path.GetFullPath(outputPath);
        if (!File.Exists(input)) throw new FileNotFoundException("ButterLib input does not exist.", input);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Input and output must be different files.");
        return (input, output);
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed record Target(string TypeName, string MethodName, bool IsStatic);
}
