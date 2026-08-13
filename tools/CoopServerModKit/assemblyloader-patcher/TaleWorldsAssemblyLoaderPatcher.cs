using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace CoopServerModKit.TaleWorlds;

public static class TaleWorldsAssemblyLoaderPatcher
{
    public const string PinnedInputSha256 = "4a23b67965f0c928dde0121f08dca1eedd988167b447a11c1ebb3d64db11562c";

    public static void Patch(string inputPath, string outputPath) =>
        Patch(inputPath, outputPath, PinnedInputSha256);

    public static void Patch(string inputPath, string outputPath, string expectedInputSha256)
    {
        (string input, string output) = ValidatePaths(inputPath, outputPath);
        string actualHash = Sha256(input);
        if (!string.Equals(actualHash, expectedInputSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported TaleWorlds.Library input SHA-256 '{actualHash}'; expected '{expectedInputSha256}'.");
        }

        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters
        {
            InMemory = true,
            ReadWrite = false,
        });
        if (!string.Equals(assembly.Name.Name, "TaleWorlds.Library", StringComparison.Ordinal) ||
            assembly.Name.Version != new Version(1, 0, 0, 0))
        {
            throw new InvalidOperationException($"Unsupported TaleWorlds.Library identity '{assembly.Name.FullName}'.");
        }

        ModuleDefinition module = assembly.MainModule;
        TypeDefinition? loader = module.GetType("TaleWorlds.Library.AssemblyLoader");
        MethodDefinition[] matches = loader?.Methods
            .Where(method =>
                string.Equals(method.Name, "LoadFrom", StringComparison.Ordinal) &&
                method.IsStatic &&
                method.Parameters.Count == 3 &&
                method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
                method.Parameters[1].ParameterType is ByReferenceType resultType &&
                resultType.ElementType.FullName == "TaleWorlds.Library.AssemblyLoader/AssemblyLoadResult" &&
                method.Parameters[2].ParameterType.MetadataType == MetadataType.Boolean &&
                method.ReturnType.FullName == "System.Reflection.Assembly")
            .ToArray() ?? [];
        if (loader is null || matches.Length != 1 || !matches[0].HasBody || matches[0].Body.Instructions.Count == 0)
        {
            throw new InvalidOperationException(
                "Unsupported TaleWorlds.Library: expected exactly one concrete " +
                "TaleWorlds.Library.AssemblyLoader::LoadFrom(string,out AssemblyLoadResult,bool); " +
                $"found {matches.Length}.");
        }
        if (loader.Methods.Any(method => method.Name == "TryResolveMissingDependency"))
            throw new InvalidOperationException("Unsupported TaleWorlds.Library: dependency resolver is already present.");

        MethodDefinition resolver = AddResolver(module, loader);
        InjectResolverPreference(module, matches[0], resolver);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        assembly.Write(output);
    }

    private static MethodDefinition AddResolver(ModuleDefinition module, TypeDefinition loader)
    {
        TypeSystem types = module.TypeSystem;
        TypeReference fileType = new("System.IO", "File", module, types.CoreLibrary);
        TypeReference pathType = new("System.IO", "Path", module, types.CoreLibrary);
        TypeReference assemblyType = new("System.Reflection", "Assembly", module, types.CoreLibrary);
        TypeReference assemblyNameType = new("System.Reflection", "AssemblyName", module, types.CoreLibrary);
        TypeReference exceptionType = new("System", "Exception", module, types.CoreLibrary);

        MethodReference fileExists = new("Exists", types.Boolean, fileType) { HasThis = false };
        fileExists.Parameters.Add(new ParameterDefinition(types.String));
        MethodReference getFileNameWithoutExtension = new("GetFileNameWithoutExtension", types.String, pathType) { HasThis = false };
        getFileNameWithoutExtension.Parameters.Add(new ParameterDefinition(types.String));
        MethodReference assemblyNameConstructor = new(".ctor", types.Void, assemblyNameType) { HasThis = true };
        assemblyNameConstructor.Parameters.Add(new ParameterDefinition(types.String));
        MethodReference assemblyLoad = new("Load", assemblyType, assemblyType) { HasThis = false };
        assemblyLoad.Parameters.Add(new ParameterDefinition(assemblyNameType));

        MethodDefinition resolver = new(
            "TryResolveMissingDependency",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            assemblyType);
        resolver.Parameters.Add(new ParameterDefinition("assemblyFile", ParameterAttributes.None, types.String));
        resolver.Body.InitLocals = true;
        VariableDefinition resolved = new(assemblyType);
        resolver.Body.Variables.Add(resolved);
        ILProcessor il = resolver.Body.GetILProcessor();

        Instruction tryStart = Instruction.Create(OpCodes.Ldarg_0);
        Instruction catchStart = Instruction.Create(OpCodes.Pop);
        Instruction returnResolved = Instruction.Create(OpCodes.Ldloc, resolved);
        Instruction returnNull = Instruction.Create(OpCodes.Ldnull);
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(fileExists)));
        il.Append(Instruction.Create(OpCodes.Brfalse, tryStart));
        il.Append(Instruction.Create(OpCodes.Ldnull));
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(tryStart);
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(getFileNameWithoutExtension)));
        il.Append(Instruction.Create(OpCodes.Newobj, module.ImportReference(assemblyNameConstructor)));
        il.Append(Instruction.Create(OpCodes.Call, module.ImportReference(assemblyLoad)));
        il.Append(Instruction.Create(OpCodes.Stloc, resolved));
        il.Append(Instruction.Create(OpCodes.Leave, returnResolved));
        il.Append(catchStart);
        il.Append(Instruction.Create(OpCodes.Leave, returnNull));
        il.Append(returnResolved);
        il.Append(Instruction.Create(OpCodes.Ret));
        il.Append(returnNull);
        il.Append(Instruction.Create(OpCodes.Ret));
        resolver.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = exceptionType,
            TryStart = tryStart,
            TryEnd = catchStart,
            HandlerStart = catchStart,
            HandlerEnd = returnResolved,
        });
        loader.Methods.Add(resolver);
        return resolver;
    }

    private static void InjectResolverPreference(ModuleDefinition module, MethodDefinition target, MethodDefinition resolver)
    {
        TypeReference fileType = new("System.IO", "File", module, module.TypeSystem.CoreLibrary);
        MethodReference fileExists = new("Exists", module.TypeSystem.Boolean, fileType) { HasThis = false };
        fileExists.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        MethodBody body = target.Body;
        body.InitLocals = true;
        VariableDefinition resolved = new(module.ImportReference(resolver.ReturnType));
        body.Variables.Add(resolved);
        ILProcessor il = body.GetILProcessor();
        Instruction originalFirst = body.Instructions[0];
        Instruction deferMissingDependency = Instruction.Create(OpCodes.Ldarg_0);
        Instruction[] injected =
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, resolver),
            Instruction.Create(OpCodes.Stloc, resolved),
            Instruction.Create(OpCodes.Ldloc, resolved),
            Instruction.Create(OpCodes.Brfalse, deferMissingDependency),
            Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Ldc_I4_0),
            Instruction.Create(OpCodes.Stind_I4),
            Instruction.Create(OpCodes.Ldloc, resolved),
            Instruction.Create(OpCodes.Ret),
            deferMissingDependency,
            Instruction.Create(OpCodes.Call, module.ImportReference(fileExists)),
            Instruction.Create(OpCodes.Brtrue, originalFirst),
            Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Ldc_I4_0),
            Instruction.Create(OpCodes.Stind_I4),
            Instruction.Create(OpCodes.Ldnull),
            Instruction.Create(OpCodes.Ret),
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
        if (!File.Exists(input)) throw new FileNotFoundException("TaleWorlds.Library input does not exist.", input);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Input and output must be different files.");
        return (input, output);
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
