using System.Security.Cryptography;
using System.Text.Json;
using CoopServerModKit.ButterLib;
using CoopServerModKit.DedicatedServer;
using DedicatedServerCompatibilityPatcher;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace CoopServerModKit.Tests;

public sealed class ServerKitPatcherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "coop-server-kit-tests-" + Guid.NewGuid().ToString("N"));

    public ServerKitPatcherTests() => Directory.CreateDirectory(root);

    [Fact]
    public void ButterLibPatch_RequiresTheExactFivePinnedMethodsBeforeWriting()
    {
        string input = Path.Combine(root, "ButterLib.dll");
        string output = Path.Combine(root, "ButterLib.patched.dll");
        CreateButterLibFixture(input, omitLastTarget: true);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            ButterLibHeadlessPatcher.Patch(input, output, Sha256(input)));

        Assert.Contains("exactly one", error.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ButterLibPatch_NeutralizesOnlyTheFiveExactPinnedMethods()
    {
        string input = Path.Combine(root, "ButterLib.dll");
        string output = Path.Combine(root, "ButterLib.patched.dll");
        CreateButterLibFixture(input);

        Assert.Equal(5, ButterLibHeadlessPatcher.Patch(input, output, Sha256(input)));

        using AssemblyDefinition patched = AssemblyDefinition.ReadAssembly(output);
        MethodDefinition[] neutralized = patched.MainModule.Types
            .SelectMany(type => type.Methods)
            .Where(method => method.Name is "Enable" or "ValidateLoadOrder")
            .Where(method => method.Body.Instructions.Count == 1 && method.Body.Instructions[0].OpCode == OpCodes.Ret)
            .ToArray();
        Assert.Equal(5, neutralized.Length);

        MethodDefinition unrelated = patched.MainModule.GetType("Unrelated.Type").Methods.Single();
        Assert.Equal(OpCodes.Ldstr, unrelated.Body.Instructions[0].OpCode);
    }

    [Fact]
    public void DedicatedServerLoaderPatch_RequiresOneExactEnsureLoadedSignature()
    {
        string input = Path.Combine(root, "DedicatedServer.Core.dll");
        string output = Path.Combine(root, "DedicatedServer.Core.patched.dll");
        CreateDedicatedServerLoaderFixture(input, duplicateTarget: true);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            DedicatedServerLoaderPatcher.Patch(input, output, Sha256(input)));

        Assert.Contains("exactly one", error.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void DedicatedServerLoaderPatch_ReusesLoadedAssembliesWithoutPermanentProbeLogging()
    {
        string input = Path.Combine(root, "DedicatedServer.Core.dll");
        string output = Path.Combine(root, "DedicatedServer.Core.patched.dll");
        CreateDedicatedServerLoaderFixture(input);

        DedicatedServerLoaderPatcher.Patch(input, output, Sha256(input));

        using AssemblyDefinition patched = AssemblyDefinition.ReadAssembly(output);
        MethodDefinition method = patched.MainModule.GetType("DedicatedServer.CoopDriver").Methods.Single();
        Assert.Contains(method.Body.Instructions, instruction =>
            instruction.Operand is MethodReference reference && reference.Name == "GetAssemblies");
        Assert.DoesNotContain(method.Body.Instructions, instruction =>
            instruction.Operand is string value && value.Contains("ensure.log", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleasePairingPatch_PinsFourModuleHashesAndRestoresFailClosedExit()
    {
        string input = Path.Combine(root, "DedicatedServer.Core.loader-patched.dll");
        string output = Path.Combine(root, "DedicatedServer.Core.release-paired.dll");
        string receipt = Path.Combine(root, "SERVER-COOP-PAIRING.json");
        string moduleBin = Path.Combine(root, "module-bin");
        Directory.CreateDirectory(moduleBin);
        CreateReleasePairingFixture(input);
        foreach (string name in DedicatedServerReleasePairingPatcher.RequiredModuleFiles)
            File.WriteAllText(Path.Combine(moduleBin, name), "fixture-" + name);

        DedicatedServerReleasePairingPatcher.Patch(input, output, moduleBin, receipt, Sha256(input));

        using AssemblyDefinition patched = AssemblyDefinition.ReadAssembly(output);
        MethodDefinition dictionaryFactory = patched.MainModule.GetType("A.K").Methods.Single();
        string[] strings = dictionaryFactory.Body.Instructions
            .Where(instruction => instruction.OpCode == OpCodes.Ldstr)
            .Select(instruction => (string)instruction.Operand)
            .ToArray();
        foreach (string name in DedicatedServerReleasePairingPatcher.RequiredModuleFiles)
        {
            Assert.Contains(Path.GetFileNameWithoutExtension(name), strings);
            Assert.Contains(Sha256(Path.Combine(moduleBin, name)), strings);
        }

        MethodDefinition abort = patched.MainModule.GetType("A.j").Methods.Single();
        Assert.Contains(abort.Body.Instructions, instruction => instruction.OpCode == OpCodes.Ldc_I4_4);
        Assert.Contains(abort.Body.Instructions, instruction =>
            instruction.OpCode == OpCodes.Call &&
            instruction.Operand is MethodReference reference && reference.FullName == "System.Void A.J::A(System.Int32)");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(receipt));
        Assert.Equal(4, document.RootElement.GetProperty("modules").GetArrayLength());
        Assert.Equal(Sha256(output), document.RootElement.GetProperty("pairedDedicatedServerCoreSha256").GetString());
    }

    [Fact]
    public void ReleasePairingPatch_MissingModuleLeavesNoOutputs()
    {
        string input = Path.Combine(root, "DedicatedServer.Core.loader-patched.dll");
        string output = Path.Combine(root, "DedicatedServer.Core.release-paired.dll");
        string receipt = Path.Combine(root, "SERVER-COOP-PAIRING.json");
        string moduleBin = Path.Combine(root, "module-bin");
        Directory.CreateDirectory(moduleBin);
        CreateReleasePairingFixture(input);

        Assert.Throws<FileNotFoundException>(() =>
            DedicatedServerReleasePairingPatcher.Patch(input, output, moduleBin, receipt, Sha256(input)));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(receipt));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static void CreateButterLibFixture(string path, bool omitLastTarget = false)
    {
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Bannerlord.ButterLib", new Version(2, 11, 1, 0)),
            "Bannerlord.ButterLib", ModuleKind.Dll);
        (string Type, string Method, bool Static)[] targets =
        {
            ("Bannerlord.ButterLib.ExceptionHandler.ExceptionHandlerSubSystem", "Enable", false),
            ("Bannerlord.ButterLib.CrashUploader.CrashUploaderSubSystem", "Enable", false),
            ("Bannerlord.ButterLib.DelayedSubModule.DelayedSubModuleSubSystem", "Enable", false),
            ("Bannerlord.ButterLib.SubModuleWrappers2.SubModuleWrappers2SubSystem", "Enable", false),
            ("Bannerlord.ButterLib.ButterLibSubModule", "ValidateLoadOrder", true),
        };
        foreach ((string typeName, string methodName, bool isStatic) in omitLastTarget ? targets[..^1] : targets)
        {
            TypeDefinition type = AddType(assembly.MainModule, typeName);
            MethodAttributes attributes = MethodAttributes.Private | (isStatic ? MethodAttributes.Static : 0);
            MethodDefinition method = new(methodName, attributes, assembly.MainModule.TypeSystem.Void);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "original"));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(method);
        }
        TypeDefinition unrelated = AddType(assembly.MainModule, "Unrelated.Type");
        MethodDefinition unrelatedValidate = new("ValidateLoadOrder", MethodAttributes.Static, assembly.MainModule.TypeSystem.Void);
        unrelatedValidate.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "must-survive"));
        unrelatedValidate.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        unrelatedValidate.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        unrelated.Methods.Add(unrelatedValidate);
        assembly.Write(path);
    }

    private static void CreateDedicatedServerLoaderFixture(string path, bool duplicateTarget = false)
    {
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("DedicatedServer.Core", new Version(0, 0, 0, 0)),
            "DedicatedServer.Core", ModuleKind.Dll);
        TypeDefinition type = AddType(assembly.MainModule, "DedicatedServer.CoopDriver");
        AddEnsureLoaded(type, assembly.MainModule);
        if (duplicateTarget) AddEnsureLoaded(type, assembly.MainModule);
        assembly.Write(path);
    }

    private static void AddEnsureLoaded(TypeDefinition type, ModuleDefinition module)
    {
        TypeReference assemblyType = module.ImportReference(typeof(System.Reflection.Assembly));
        MethodDefinition method = new("EnsureLoaded", MethodAttributes.Public | MethodAttributes.Static, assemblyType);
        method.Parameters.Add(new ParameterDefinition("simpleName", ParameterAttributes.None, module.TypeSystem.String));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
    }

    private static void CreateReleasePairingFixture(string path)
    {
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("DedicatedServer.Core", new Version(0, 0, 0, 0)),
            "DedicatedServer.Core", ModuleKind.Dll);
        TypeDefinition exitType = AddType(assembly.MainModule, "A.J");
        MethodDefinition exit = new("A", MethodAttributes.Static, assembly.MainModule.TypeSystem.Void);
        exit.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.Int32));
        exit.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        exitType.Methods.Add(exit);

        TypeDefinition guardType = AddType(assembly.MainModule, "A.j");
        MethodDefinition abort = new("B", MethodAttributes.Static, assembly.MainModule.TypeSystem.Void);
        abort.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.String));
        abort.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
        abort.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
        abort.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        guardType.Methods.Add(abort);

        TypeDefinition hashType = AddType(assembly.MainModule, "A.K");
        TypeReference dictionaryType = assembly.MainModule.ImportReference(typeof(Dictionary<string, string>));
        MethodDefinition factory = new("A", MethodAttributes.Static, dictionaryType);
        factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        hashType.Methods.Add(factory);
        assembly.Write(path);
    }

    private static TypeDefinition AddType(ModuleDefinition module, string fullName)
    {
        int separator = fullName.LastIndexOf('.');
        string ns = separator < 0 ? string.Empty : fullName[..separator];
        string name = separator < 0 ? fullName : fullName[(separator + 1)..];
        TypeDefinition type = new(ns, name, TypeAttributes.Class | TypeAttributes.NotPublic, module.TypeSystem.Object);
        module.Types.Add(type);
        return type;
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
