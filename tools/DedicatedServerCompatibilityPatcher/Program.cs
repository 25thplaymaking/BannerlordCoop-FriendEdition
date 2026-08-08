using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "Usage: DedicatedServerCompatibilityPatcher <DedicatedServer.Core.dll> <patched-output.dll>");
    return 1;
}

var inputPath = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[1]);
if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Input assembly does not exist: {inputPath}");
    return 2;
}

if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Input and output must be different files so the original remains recoverable.");
    return 3;
}

using var assembly = AssemblyDefinition.ReadAssembly(inputPath, new ReaderParameters
{
    InMemory = true,
    ReadWrite = false
});

// In the supported dedicated-server build, A.j::B(string) prints the module verification
// diagnostics and then invokes A.J::A(4), whose only effect is Environment.Exit(4).
var guardType = assembly.MainModule.GetType("A.j");
if (guardType == null)
{
    Console.Error.WriteLine("Unsupported assembly: type A.j was not found.");
    return 4;
}

var abortMethod = guardType.Methods.SingleOrDefault(method =>
    method.Name == "B" &&
    method.Parameters.Count == 1 &&
    method.Parameters[0].ParameterType.FullName == "System.String");
if (abortMethod?.HasBody != true)
{
    Console.Error.WriteLine("Unsupported assembly: method A.j::B(string) was not found.");
    return 5;
}

var exitCalls = abortMethod.Body.Instructions
    .Where(instruction =>
        instruction.OpCode == OpCodes.Call &&
        instruction.Operand is MethodReference method &&
        method.DeclaringType.FullName == "A.J" &&
        method.Name == "A" &&
        method.Parameters.Count == 1 &&
        method.Parameters[0].ParameterType.MetadataType == MetadataType.Int32)
    .ToArray();

if (exitCalls.Length != 1 || exitCalls[0].Previous?.OpCode != OpCodes.Ldc_I4_4)
{
    Console.Error.WriteLine(
        $"Unsupported assembly: expected exactly one A.J::A(int) call preceded by ldc.i4.4; found {exitCalls.Length}.");
    return 6;
}

// Mutate in place so any branch targets that refer to these instruction objects remain valid.
var exitCall = exitCalls[0];
var exitCode = exitCall.Previous!;
exitCode.OpCode = OpCodes.Nop;
exitCode.Operand = null;
exitCall.OpCode = OpCodes.Nop;
exitCall.Operand = null;

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
assembly.Write(outputPath);

Console.WriteLine("Patched one verification-abort call. Diagnostics and hash checks remain enabled.");
Console.WriteLine($"Input:  {inputPath}");
Console.WriteLine($"Output: {outputPath}");
return 0;
