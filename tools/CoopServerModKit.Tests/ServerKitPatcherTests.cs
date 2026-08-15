using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using CoopServerModKit.ButterLib;
using CoopServerModKit.DedicatedServer;
using CoopServerModKit.TaleWorlds;
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
    public void TaleWorldsAssemblyLoaderPatch_RequiresOneExactLoadFromSignature()
    {
        string input = Path.Combine(root, "TaleWorlds.Library.dll");
        string output = Path.Combine(root, "TaleWorlds.Library.patched.dll");
        CreateTaleWorldsAssemblyLoaderFixture(input, duplicateTarget: true);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            TaleWorldsAssemblyLoaderPatcher.Patch(input, output, Sha256(input)));

        Assert.Contains("exactly one", error.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void TaleWorldsAssemblyLoaderPatch_ResolvesMissingBareDependenciesBeforeShowingAnEngineMessageBox()
    {
        string input = Path.Combine(root, "TaleWorlds.Library.dll");
        string output = Path.Combine(root, "TaleWorlds.Library.patched.dll");
        CreateTaleWorldsAssemblyLoaderFixture(input);

        TaleWorldsAssemblyLoaderPatcher.Patch(input, output, Sha256(input));

        using AssemblyDefinition patched = AssemblyDefinition.ReadAssembly(output);
        TypeDefinition type = patched.MainModule.GetType("TaleWorlds.Library.AssemblyLoader");
        MethodDefinition target = type.Methods.Single(method => method.Name == "LoadFrom");
        MethodDefinition resolver = type.Methods.Single(method => method.Name == "TryResolveMissingDependency");
        Assert.Contains(target.Body.Instructions, instruction =>
            instruction.Operand is MethodReference reference && reference.Name == resolver.Name);
        Assert.Contains(target.Body.Instructions, instruction =>
            instruction.Operand is MethodReference reference && reference.FullName == "System.Boolean System.IO.File::Exists(System.String)");
        Assert.Contains(resolver.Body.Instructions, instruction =>
            instruction.Operand is MethodReference reference && reference.FullName == "System.Boolean System.IO.File::Exists(System.String)");
        Assert.Contains(resolver.Body.Instructions, instruction =>
            instruction.Operand is MethodReference reference && reference.FullName == "System.Reflection.Assembly System.Reflection.Assembly::Load(System.Reflection.AssemblyName)");
        Assert.Contains(resolver.Body.ExceptionHandlers, handler =>
            handler.CatchType?.FullName == "System.Exception");
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

    [Fact]
    public void CampaignSystemSetterPatch_MarksOnlyConcretePropertySettersNoInlining()
    {
        string input = Path.Combine(root, "TaleWorlds.CampaignSystem.dll");
        string output = Path.Combine(root, "TaleWorlds.CampaignSystem.patched.dll");
        CreateCampaignSystemFixture(input);

        Assert.Equal(2, CampaignSystemSetterPatcher.Patch(input, output, Sha256(input), 3, 2));

        using AssemblyDefinition patched = AssemblyDefinition.ReadAssembly(output);
        TypeDefinition type = patched.MainModule.GetType("TaleWorlds.CampaignSystem.Fixture");
        MethodDefinition[] setters = type.Methods.Where(method => method.IsSetter).ToArray();
        Assert.Equal(3, setters.Length);
        Assert.All(setters.Where(method => method.HasBody), setter =>
            Assert.NotEqual(0, (int)(setter.ImplAttributes & MethodImplAttributes.NoInlining)));
        Assert.DoesNotContain(setters.Where(method => !method.HasBody), setter =>
            (setter.ImplAttributes & MethodImplAttributes.NoInlining) != 0);
        Assert.Equal(MethodImplAttributes.IL, type.Methods.Single(method => method.Name == "Unrelated").ImplAttributes);
    }

    [Fact]
    public void CampaignSystemSetterPatch_RejectsUnexpectedSetterShapeWithoutWriting()
    {
        string input = Path.Combine(root, "TaleWorlds.CampaignSystem.dll");
        string output = Path.Combine(root, "TaleWorlds.CampaignSystem.patched.dll");
        CreateCampaignSystemFixture(input);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            CampaignSystemSetterPatcher.Patch(input, output, Sha256(input), 4, 2));

        Assert.Contains("setter shape", error.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void SandBoxServerDescriptorPatch_EnablesOnlyGameplaySubModule()
    {
        string input = Path.Combine(root, "SandBox.SubModule.xml");
        string output = Path.Combine(root, "SandBox.SubModule.server.xml");
        File.WriteAllText(input, CreateSandBoxDescriptorFixture());

        SandBoxServerDescriptorPatcher.Patch(input, output, Sha256(input), expectedOutputSha256: null);

        XDocument patched = XDocument.Load(output);
        XElement[] subModules = patched.Descendants("SubModule").ToArray();
        XElement gameplay = subModules.Single(element =>
            element.Element("SubModuleClassType")?.Attribute("value")?.Value ==
            SandBoxServerDescriptorPatcher.GameplayClass);
        Assert.Null(gameplay.Element("Tags"));
        Assert.Equal(2, subModules.Count(element => element.Element("Tags") != null));
    }

    [Fact]
    public void SandBoxServerDescriptorPatch_RejectsUnexpectedGameplayTagsWithoutWriting()
    {
        string input = Path.Combine(root, "SandBox.SubModule.xml");
        string output = Path.Combine(root, "SandBox.SubModule.server.xml");
        File.WriteAllText(input, CreateSandBoxDescriptorFixture().Replace(
            "<Tag key=\"DedicatedServerType\" value=\"none\" />",
            "<Tag key=\"DedicatedServerType\" value=\"custom\" />",
            StringComparison.Ordinal));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            SandBoxServerDescriptorPatcher.Patch(input, output, Sha256(input), expectedOutputSha256: null));

        Assert.Contains("gameplay tag shape", error.Message);
        Assert.False(File.Exists(output));
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

    private static void CreateCampaignSystemFixture(string path)
    {
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("TaleWorlds.CampaignSystem", new Version(1, 0, 0, 0)),
            "TaleWorlds.CampaignSystem", ModuleKind.Dll);
        TypeDefinition type = AddType(assembly.MainModule, "TaleWorlds.CampaignSystem.Fixture");
        AddProperty(type, "First", hasBody: true);
        AddProperty(type, "Second", hasBody: true);
        AddProperty(type, "Abstract", hasBody: false);
        MethodDefinition unrelated = new("Unrelated", MethodAttributes.Public, assembly.MainModule.TypeSystem.Void);
        unrelated.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(unrelated);
        assembly.Write(path);
    }

    private static string CreateSandBoxDescriptorFixture() =>
        string.Join("\r\n", new[]
        {
            "<?xml version='1.0' encoding='utf-8'?>",
            "<Module>",
            "\t<SubModules>",
            "\t\t<SubModule>",
            "\t\t\t<Name value=\"SandBox\" />",
            "\t\t\t<DLLName value=\"SandBox.dll\" />",
            "\t\t\t<SubModuleClassType value=\"SandBox.SandBoxSubModule\" />",
            "\t\t\t<Tags>",
            "\t\t\t\t<Tag key=\"DedicatedServerType\" value=\"none\" />",
            "\t\t\t\t<Tag key=\"IsNoRenderModeElement\" value=\"false\" />",
            "\t\t\t</Tags>",
            "\t\t</SubModule>",
            "\t\t<SubModule>",
            "\t\t\t<SubModuleClassType value=\"SandBox.View.SandBoxViewSubModule\" />",
            "\t\t\t<Tags><Tag key=\"DedicatedServerType\" value=\"none\" /></Tags>",
            "\t\t</SubModule>",
            "\t\t<SubModule>",
            "\t\t\t<SubModuleClassType value=\"SandBox.GauntletUI.SandBoxGauntletUISubModule\" />",
            "\t\t\t<Tags><Tag key=\"DedicatedServerType\" value=\"none\" /></Tags>",
            "\t\t</SubModule>",
            "\t</SubModules>",
            "</Module>",
        });

    private static void AddProperty(TypeDefinition type, string name, bool hasBody)
    {
        ModuleDefinition module = type.Module;
        PropertyDefinition property = new(name, PropertyAttributes.None, module.TypeSystem.Int32);
        MethodAttributes attributes = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
        MethodDefinition setter = new("set_" + name, attributes, module.TypeSystem.Void);
        setter.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Int32));
        if (hasBody)
            setter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        else
            setter.Attributes |= MethodAttributes.Abstract | MethodAttributes.Virtual;
        property.SetMethod = setter;
        type.Methods.Add(setter);
        type.Properties.Add(property);
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

    private static void CreateTaleWorldsAssemblyLoaderFixture(string path, bool duplicateTarget = false)
    {
        using AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("TaleWorlds.Library", new Version(1, 0, 0, 0)),
            "TaleWorlds.Library", ModuleKind.Dll);
        TypeDefinition type = AddType(assembly.MainModule, "TaleWorlds.Library.AssemblyLoader");
        TypeDefinition result = new(
            string.Empty,
            "AssemblyLoadResult",
            TypeAttributes.NestedPublic | TypeAttributes.Sealed,
            assembly.MainModule.ImportReference(typeof(Enum)));
        result.Fields.Add(new FieldDefinition(
            "value__",
            FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName,
            assembly.MainModule.TypeSystem.Int32));
        type.NestedTypes.Add(result);
        AddTaleWorldsLoadFrom(type, result, assembly.MainModule);
        if (duplicateTarget) AddTaleWorldsLoadFrom(type, result, assembly.MainModule);
        assembly.Write(path);
    }

    private static void AddTaleWorldsLoadFrom(TypeDefinition type, TypeDefinition result, ModuleDefinition module)
    {
        MethodDefinition method = new(
            "LoadFrom",
            MethodAttributes.Public | MethodAttributes.Static,
            module.ImportReference(typeof(System.Reflection.Assembly)));
        method.Parameters.Add(new ParameterDefinition("assemblyFile", ParameterAttributes.None, module.TypeSystem.String));
        method.Parameters.Add(new ParameterDefinition("result", ParameterAttributes.Out, new ByReferenceType(result)));
        method.Parameters.Add(new ParameterDefinition("showError", ParameterAttributes.Optional, module.TypeSystem.Boolean));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
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
