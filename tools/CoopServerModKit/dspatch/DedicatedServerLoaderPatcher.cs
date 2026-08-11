using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace CoopServerModKit.DedicatedServer;

public static class DedicatedServerLoaderPatcher
{
    public const string PinnedInputSha256 = "d766be598213e69f0cda4e146470025c49d6a2f16de8df63bc6bc9b7dfacfb20";

    public static void Patch(string inputPath, string outputPath) =>
        Patch(inputPath, outputPath, PinnedInputSha256);

    public static void Patch(string inputPath, string outputPath, string expectedInputSha256)
    {
        (string input, string output) = ValidatePaths(inputPath, outputPath);
        string actualHash = Sha256(input);
        if (!string.Equals(actualHash, expectedInputSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported DedicatedServer.Core input SHA-256 '{actualHash}'; expected '{expectedInputSha256}'.");
        }

        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters
        {
            InMemory = true,
            ReadWrite = false,
        });
        if (!string.Equals(assembly.Name.Name, "DedicatedServer.Core", StringComparison.Ordinal) ||
            assembly.Name.Version != new Version(0, 0, 0, 0))
        {
            throw new InvalidOperationException(
                $"Unsupported dedicated-server identity '{assembly.Name.FullName}'.");
        }

        ModuleDefinition module = assembly.MainModule;
        MethodDefinition[] matches = module.GetTypes()
            .Where(type => string.Equals(type.FullName, "DedicatedServer.CoopDriver", StringComparison.Ordinal))
            .SelectMany(type => type.Methods)
            .Where(method =>
                string.Equals(method.Name, "EnsureLoaded", StringComparison.Ordinal) &&
                method.IsStatic &&
                method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
                method.ReturnType.FullName == "System.Reflection.Assembly")
            .ToArray();
        if (matches.Length != 1 || !matches[0].HasBody || matches[0].Body.Instructions.Count == 0)
        {
            throw new InvalidOperationException(
                "Unsupported DedicatedServer.Core: expected exactly one concrete " +
                $"DedicatedServer.CoopDriver::EnsureLoaded(System.String):System.Reflection.Assembly; found {matches.Length}.");
        }

        InjectLoadedAssemblyPreference(module, matches[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        assembly.Write(output);
    }

    private static void InjectLoadedAssemblyPreference(ModuleDefinition module, MethodDefinition method)
    {
        TypeSystem types = module.TypeSystem;
        TypeReference appDomainType = new("System", "AppDomain", module, types.CoreLibrary);
        TypeReference assemblyType = new("System.Reflection", "Assembly", module, types.CoreLibrary);
        TypeReference assemblyNameType = new("System.Reflection", "AssemblyName", module, types.CoreLibrary);
        TypeReference comparisonType = new("System", "StringComparison", module, types.CoreLibrary) { IsValueType = true };

        MethodReference getCurrentDomain = new("get_CurrentDomain", appDomainType, appDomainType) { HasThis = false };
        MethodReference getAssemblies = new("GetAssemblies", new ArrayType(assemblyType), appDomainType) { HasThis = true };
        MethodReference getName = new("GetName", assemblyNameType, assemblyType) { HasThis = true };
        MethodReference getNameValue = new("get_Name", types.String, assemblyNameType) { HasThis = true };
        MethodReference equals = new("Equals", types.Boolean, types.String) { HasThis = false };
        equals.Parameters.Add(new ParameterDefinition(types.String));
        equals.Parameters.Add(new ParameterDefinition(types.String));
        equals.Parameters.Add(new ParameterDefinition(comparisonType));

        MethodBody body = method.Body;
        body.InitLocals = true;
        ILProcessor il = body.GetILProcessor();
        VariableDefinition assemblies = new(module.ImportReference(new ArrayType(assemblyType)));
        VariableDefinition index = new(types.Int32);
        body.Variables.Add(assemblies);
        body.Variables.Add(index);

        Instruction originalFirst = body.Instructions[0];
        Instruction loopStart = Instruction.Create(OpCodes.Ldloc, assemblies);
        Instruction increment = Instruction.Create(OpCodes.Ldloc, index);
        Instruction check = Instruction.Create(OpCodes.Ldloc, index);
        Instruction[] injected =
        {
            Instruction.Create(OpCodes.Call, module.ImportReference(getCurrentDomain)),
            Instruction.Create(OpCodes.Callvirt, module.ImportReference(getAssemblies)),
            Instruction.Create(OpCodes.Stloc, assemblies),
            Instruction.Create(OpCodes.Ldc_I4_0),
            Instruction.Create(OpCodes.Stloc, index),
            Instruction.Create(OpCodes.Br, check),
            loopStart,
            Instruction.Create(OpCodes.Ldloc, index),
            Instruction.Create(OpCodes.Ldelem_Ref),
            Instruction.Create(OpCodes.Callvirt, module.ImportReference(getName)),
            Instruction.Create(OpCodes.Callvirt, module.ImportReference(getNameValue)),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldc_I4_5),
            Instruction.Create(OpCodes.Call, module.ImportReference(equals)),
            Instruction.Create(OpCodes.Brfalse, increment),
            Instruction.Create(OpCodes.Ldloc, assemblies),
            Instruction.Create(OpCodes.Ldloc, index),
            Instruction.Create(OpCodes.Ldelem_Ref),
            Instruction.Create(OpCodes.Ret),
            increment,
            Instruction.Create(OpCodes.Ldc_I4_1),
            Instruction.Create(OpCodes.Add),
            Instruction.Create(OpCodes.Stloc, index),
            check,
            Instruction.Create(OpCodes.Ldloc, assemblies),
            Instruction.Create(OpCodes.Ldlen),
            Instruction.Create(OpCodes.Conv_I4),
            Instruction.Create(OpCodes.Blt, loopStart),
        };
        foreach (Instruction instruction in injected) il.InsertBefore(originalFirst, instruction);
        body.OptimizeMacros();
    }

    private static (string Input, string Output) ValidatePaths(string inputPath, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Input and output paths are required.");
        string input = Path.GetFullPath(inputPath);
        string output = Path.GetFullPath(outputPath);
        if (!File.Exists(input)) throw new FileNotFoundException("DedicatedServer.Core input does not exist.", input);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Input and output must be different files.");
        return (input, output);
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
