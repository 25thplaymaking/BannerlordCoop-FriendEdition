using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Patch DedicatedServer.Core.dll : CoopDriver.EnsureLoaded(string) so it PREFERS an
// already-loaded assembly (by simple name, ordinal-ignore-case) before ever calling
// Assembly.LoadFrom. On .NET Core, a second LoadFrom of an already-present simple name
// throws FileLoadException "Assembly with same name is already loaded" — which is exactly
// the FATAL that kills the coop host at tick ~564 once ButterLib + the startup hook have
// loaded the shared assemblies (Common/Serilog/GameInterface/Coop.Core) first.
//
// We prepend a scan loop to the existing method body and branch into the original body
// only when nothing already-loaded matches. The original body is left untouched.
class Program
{
    static int Main(string[] args)
    {
        string input = args[0];
        string output = args[1];

        var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { ReadWrite = false });
        var module = asm.MainModule;

        var coopDriver = module.Types.FirstOrDefault(t => t.Name == "CoopDriver" && t.Namespace == "DedicatedServer");
        if (coopDriver == null) { Console.Error.WriteLine("CoopDriver not found"); return 2; }
        var ensure = coopDriver.Methods.FirstOrDefault(m => m.Name == "EnsureLoaded" && m.Parameters.Count == 1);
        if (ensure == null) { Console.Error.WriteLine("EnsureLoaded not found"); return 3; }

        var ts = module.TypeSystem;

        // Import the reflection surface we need.
        var appDomainType = new TypeReference("System", "AppDomain", module, module.TypeSystem.CoreLibrary);
        var assemblyType  = new TypeReference("System.Reflection", "Assembly", module, module.TypeSystem.CoreLibrary);
        var assemblyNameType = new TypeReference("System.Reflection", "AssemblyName", module, module.TypeSystem.CoreLibrary);
        var stringComparisonType = new TypeReference("System", "StringComparison", module, module.TypeSystem.CoreLibrary) { IsValueType = true };

        var getCurrentDomain = new MethodReference("get_CurrentDomain", appDomainType, appDomainType) { HasThis = false };
        var getAssemblies    = new MethodReference("GetAssemblies", new ArrayType(assemblyType), appDomainType) { HasThis = true };
        var getName          = new MethodReference("GetName", assemblyNameType, assemblyType) { HasThis = true };
        var getNameName      = new MethodReference("get_Name", ts.String, assemblyNameType) { HasThis = true };
        var strEquals        = new MethodReference("Equals", ts.Boolean, ts.String) { HasThis = false };
        strEquals.Parameters.Add(new ParameterDefinition(ts.String));
        strEquals.Parameters.Add(new ParameterDefinition(ts.String));
        strEquals.Parameters.Add(new ParameterDefinition(stringComparisonType));

        var mGetCurrentDomain = module.ImportReference(getCurrentDomain);
        var mGetAssemblies    = module.ImportReference(getAssemblies);
        var mGetName          = module.ImportReference(getName);
        var mGetNameName      = module.ImportReference(getNameName);
        var mStrEquals        = module.ImportReference(strEquals);
        var assemblyArrayTr   = module.ImportReference(new ArrayType(assemblyType));
        var assemblyTr        = module.ImportReference(assemblyType);

        // File.AppendAllText("/tmp/ensure.log", simpleName + "\n") right before the original body,
        // so the last logged name before the FATAL is the culprit.
        var fileType = new TypeReference("System.IO", "File", module, module.TypeSystem.CoreLibrary);
        var appendAll = new MethodReference("AppendAllText", ts.Void, fileType) { HasThis = false };
        appendAll.Parameters.Add(new ParameterDefinition(ts.String));
        appendAll.Parameters.Add(new ParameterDefinition(ts.String));
        var strConcat = new MethodReference("Concat", ts.String, ts.String) { HasThis = false };
        strConcat.Parameters.Add(new ParameterDefinition(ts.String));
        strConcat.Parameters.Add(new ParameterDefinition(ts.String));
        var mAppendAll = module.ImportReference(appendAll);
        var mConcat = module.ImportReference(strConcat);

        var body = ensure.Body;
        var il = body.GetILProcessor();

        // Two new locals: Assembly[] arr, int i
        var vArr = new VariableDefinition(assemblyArrayTr);
        var vI = new VariableDefinition(ts.Int32);
        body.Variables.Add(vArr);
        body.Variables.Add(vI);

        var first = body.Instructions[0]; // original first instruction (fall-through target)

        // Build instructions (we insert them before `first`)
        var iLoopBodyStart = il.Create(OpCodes.Ldloc, vArr);          // BODY: ldloc arr
        var iInc = il.Create(OpCodes.Ldloc, vI);                      // INC: ldloc i
        var iCheck = il.Create(OpCodes.Ldloc, vI);                    // CHECK: ldloc i

        // Prologue: arr = AppDomain.CurrentDomain.GetAssemblies(); i = 0; goto CHECK;
        var prologue = new[]
        {
            il.Create(OpCodes.Call, mGetCurrentDomain),
            il.Create(OpCodes.Callvirt, mGetAssemblies),
            il.Create(OpCodes.Stloc, vArr),
            il.Create(OpCodes.Ldc_I4_0),
            il.Create(OpCodes.Stloc, vI),
            il.Create(OpCodes.Br, iCheck),
        };

        // BODY: if string.Equals(arr[i].GetName().Name, simpleName, OrdinalIgnoreCase) return arr[i];
        var loopBody = new[]
        {
            iLoopBodyStart,                                  // ldloc arr
            il.Create(OpCodes.Ldloc, vI),                    // ldloc i
            il.Create(OpCodes.Ldelem_Ref),                   // arr[i]
            il.Create(OpCodes.Callvirt, mGetName),           // .GetName()
            il.Create(OpCodes.Callvirt, mGetNameName),       // .Name
            il.Create(OpCodes.Ldarg_0),                      // simpleName
            il.Create(OpCodes.Ldc_I4_5),                     // StringComparison.OrdinalIgnoreCase
            il.Create(OpCodes.Call, mStrEquals),             // string.Equals(a,b,cmp)
            il.Create(OpCodes.Brfalse, iInc),                // if !equal -> INC
            il.Create(OpCodes.Ldloc, vArr),                  // ldloc arr
            il.Create(OpCodes.Ldloc, vI),                    // ldloc i
            il.Create(OpCodes.Ldelem_Ref),                   // arr[i]
            il.Create(OpCodes.Ret),                          // return it
        };

        // INC: i = i + 1;
        var inc = new[]
        {
            iInc,                                            // ldloc i
            il.Create(OpCodes.Ldc_I4_1),
            il.Create(OpCodes.Add),
            il.Create(OpCodes.Stloc, vI),
        };

        // CHECK: if (i < arr.Length) goto BODY;  (fall through to original otherwise)
        var check = new[]
        {
            iCheck,                                          // ldloc i
            il.Create(OpCodes.Ldloc, vArr),                  // ldloc arr
            il.Create(OpCodes.Ldlen),
            il.Create(OpCodes.Conv_I4),
            il.Create(OpCodes.Blt, iLoopBodyStart),          // i < len -> BODY
        };

        // Logging: File.AppendAllText("/tmp/ensure.log", simpleName + "\n");
        var logIns = new[]
        {
            il.Create(OpCodes.Ldstr, "/tmp/ensure.log"),
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Ldstr, "\n"),
            il.Create(OpCodes.Call, mConcat),
            il.Create(OpCodes.Call, mAppendAll),
        };

        // Insert everything before `first`, in order: prologue, loopBody, inc, check, log
        foreach (var ins in prologue) il.InsertBefore(first, ins);
        foreach (var ins in loopBody) il.InsertBefore(first, ins);
        foreach (var ins in inc)      il.InsertBefore(first, ins);
        foreach (var ins in check)    il.InsertBefore(first, ins);
        foreach (var ins in logIns)   il.InsertBefore(first, ins);

        body.OptimizeMacros();
        asm.Write(output);
        Console.WriteLine("Patched EnsureLoaded -> prefer already-loaded. Written: " + output);
        return 0;
    }
}
