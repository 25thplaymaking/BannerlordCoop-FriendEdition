using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DedicatedServerCompatibilityPatcher;

public static class DedicatedServerReleasePairingPatcher
{
    public const string PinnedLoaderPatchedInputSha256 = "8b67ff3491bf1f91d0a27bfd2a0425975603f6dfa5102cd7dcfc057e5f778449";

    public static IReadOnlyList<string> RequiredModuleFiles { get; } = new[]
    {
        "Coop.Core.dll",
        "GameInterface.dll",
        "Common.dll",
        "Coop.Steam.dll",
    };

    public static void Patch(
        string inputPath,
        string outputPath,
        string moduleBinPath,
        string receiptPath) =>
        Patch(inputPath, outputPath, moduleBinPath, receiptPath, PinnedLoaderPatchedInputSha256);

    public static void Patch(
        string inputPath,
        string outputPath,
        string moduleBinPath,
        string receiptPath,
        string expectedInputSha256)
    {
        Paths paths = ValidatePaths(inputPath, outputPath, moduleBinPath, receiptPath);
        string inputHash = Sha256(paths.Input);
        if (!string.Equals(inputHash, expectedInputSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported loader-patched DedicatedServer.Core SHA-256 '{inputHash}'; expected '{expectedInputSha256}'.");
        }

        ModulePin[] pins = RequiredModuleFiles.Select(fileName =>
        {
            string path = Path.Combine(paths.ModuleBin, fileName);
            if (!File.Exists(path)) throw new FileNotFoundException($"Required Coop pairing input is missing: {path}", path);
            return new ModulePin(fileName, Sha256(path));
        }).ToArray();

        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(paths.Input, new ReaderParameters
        {
            InMemory = true,
            ReadWrite = false,
        });
        if (!string.Equals(assembly.Name.Name, "DedicatedServer.Core", StringComparison.Ordinal) ||
            assembly.Name.Version != new Version(0, 0, 0, 0))
        {
            throw new InvalidOperationException($"Unsupported dedicated-server identity '{assembly.Name.FullName}'.");
        }

        ModuleDefinition module = assembly.MainModule;
        MethodDefinition dictionaryFactory = RequireMethod(
            module,
            "A.K",
            "A",
            isStatic: true,
            returnType: "System.Collections.Generic.Dictionary`2<System.String,System.String>");
        MethodDefinition abort = RequireMethod(
            module,
            "A.j",
            "B",
            isStatic: true,
            returnType: "System.Void",
            "System.String");
        MethodDefinition exit = RequireMethod(
            module,
            "A.J",
            "A",
            isStatic: true,
            returnType: "System.Void",
            "System.Int32");

        RewriteHashDictionary(module, dictionaryFactory, pins);
        RestoreFailClosedAbort(abort, exit);

        Directory.CreateDirectory(Path.GetDirectoryName(paths.Output)!);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Receipt)!);
        string outputTemp = paths.Output + ".tmp-" + Guid.NewGuid().ToString("N");
        string receiptTemp = paths.Receipt + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            assembly.Write(outputTemp);
            string outputHash = Sha256(outputTemp);
            var receipt = new PairingReceipt(
                1,
                "friend-edition-dedicated-server-coop-pairing",
                DateTimeOffset.UtcNow,
                inputHash,
                outputHash,
                pins);
            File.WriteAllText(receiptTemp, JsonSerializer.Serialize(receipt, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }) + Environment.NewLine);
            File.Move(outputTemp, paths.Output);
            try
            {
                File.Move(receiptTemp, paths.Receipt);
            }
            catch
            {
                File.Delete(paths.Output);
                throw;
            }
        }
        finally
        {
            if (File.Exists(outputTemp)) File.Delete(outputTemp);
            if (File.Exists(receiptTemp)) File.Delete(receiptTemp);
        }
    }

    private static MethodDefinition RequireMethod(
        ModuleDefinition module,
        string typeName,
        string methodName,
        bool isStatic,
        string returnType,
        params string[] parameterTypes)
    {
        MethodDefinition[] matches = module.GetTypes()
            .Where(type => string.Equals(type.FullName, typeName, StringComparison.Ordinal))
            .SelectMany(type => type.Methods)
            .Where(method =>
                string.Equals(method.Name, methodName, StringComparison.Ordinal) &&
                method.IsStatic == isStatic &&
                string.Equals(method.ReturnType.FullName, returnType, StringComparison.Ordinal) &&
                method.Parameters.Select(parameter => parameter.ParameterType.FullName)
                    .SequenceEqual(parameterTypes, StringComparer.Ordinal))
            .ToArray();
        if (matches.Length != 1 || !matches[0].HasBody)
        {
            throw new InvalidOperationException(
                $"Unsupported DedicatedServer.Core: expected exactly one concrete " +
                $"{typeName}::{methodName}({string.Join(",", parameterTypes)}):{returnType}; found {matches.Length}.");
        }
        return matches[0];
    }

    private static void RewriteHashDictionary(
        ModuleDefinition module,
        MethodDefinition dictionaryFactory,
        IEnumerable<ModulePin> pins)
    {
        MethodReference comparerGetter = module.ImportReference(
            typeof(StringComparer).GetProperty(nameof(StringComparer.OrdinalIgnoreCase))!.GetMethod!);
        MethodReference dictionaryConstructor = module.ImportReference(
            typeof(Dictionary<string, string>).GetConstructor(new[] { typeof(IEqualityComparer<string>) })!);
        MethodReference setItem = module.ImportReference(
            typeof(Dictionary<string, string>).GetProperty("Item")!.SetMethod!);

        MethodBody body = dictionaryFactory.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        body.InitLocals = false;
        ILProcessor il = body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Call, comparerGetter));
        il.Append(Instruction.Create(OpCodes.Newobj, dictionaryConstructor));
        foreach (ModulePin pin in pins)
        {
            il.Append(Instruction.Create(OpCodes.Dup));
            il.Append(Instruction.Create(OpCodes.Ldstr, Path.GetFileNameWithoutExtension(pin.FileName)));
            il.Append(Instruction.Create(OpCodes.Ldstr, pin.Sha256));
            il.Append(Instruction.Create(OpCodes.Callvirt, setItem));
        }
        il.Append(Instruction.Create(OpCodes.Ret));
    }

    private static void RestoreFailClosedAbort(MethodDefinition abort, MethodDefinition exit)
    {
        Instruction[][] candidates = abort.Body.Instructions
            .Select((instruction, index) => new { instruction, index })
            .Where(item =>
                item.index + 2 < abort.Body.Instructions.Count &&
                item.instruction.OpCode == OpCodes.Nop &&
                abort.Body.Instructions[item.index + 1].OpCode == OpCodes.Nop &&
                abort.Body.Instructions[item.index + 2].OpCode == OpCodes.Ret)
            .Select(item => new[] { item.instruction, abort.Body.Instructions[item.index + 1] })
            .ToArray();
        if (candidates.Length != 1 || abort.Body.Instructions.Any(instruction =>
                instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference reference &&
                reference.FullName == exit.FullName))
        {
            throw new InvalidOperationException(
                $"Unsupported DedicatedServer.Core: expected exactly one neutralized verification-abort pair; found {candidates.Length}.");
        }

        candidates[0][0].OpCode = OpCodes.Ldc_I4_4;
        candidates[0][0].Operand = null;
        candidates[0][1].OpCode = OpCodes.Call;
        candidates[0][1].Operand = exit;
    }

    private static Paths ValidatePaths(string inputPath, string outputPath, string moduleBinPath, string receiptPath)
    {
        if (new[] { inputPath, outputPath, moduleBinPath, receiptPath }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Input, output, module-bin, and receipt paths are required.");
        string input = Path.GetFullPath(inputPath);
        string output = Path.GetFullPath(outputPath);
        string moduleBin = Path.GetFullPath(moduleBinPath);
        string receipt = Path.GetFullPath(receiptPath);
        if (!File.Exists(input)) throw new FileNotFoundException("DedicatedServer.Core input does not exist.", input);
        if (!Directory.Exists(moduleBin)) throw new DirectoryNotFoundException($"Coop server-bin does not exist: {moduleBin}");
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Input and output must be different files.");
        if (File.Exists(output) || File.Exists(receipt))
            throw new InvalidOperationException("Output and receipt paths must not already exist.");
        return new Paths(input, output, moduleBin, receipt);
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed record Paths(string Input, string Output, string ModuleBin, string Receipt);
    public sealed record ModulePin(string FileName, string Sha256);
    public sealed record PairingReceipt(
        int SchemaVersion,
        string Purpose,
        DateTimeOffset GeneratedUtc,
        string LoaderPatchedDedicatedServerCoreSha256,
        string PairedDedicatedServerCoreSha256,
        IReadOnlyList<ModulePin> Modules);
}
