using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Neutralizes ButterLib's headless-incompatible startup subsystems for the DEDICATED SERVER copy.
// Replaces the bodies of ExceptionHandlerSubSystem.Enable() and CrashUploaderSubSystem.Enable()
// with a bare `ret`. These subsystems are crash-reporting only; the campaign mods that depend on
// ButterLib do not need them, and their Enable() paths hard-patch screen/mission tick methods that
// do not exist on the headless server (the crash). The client's assembly is untouched.

class Program
{
    static int Main(string[] args)
    {
        var input = args[0];
        var output = args[1];
        var asm = AssemblyDefinition.ReadAssembly(input);
        string[] targets =
        {
            "Bannerlord.ButterLib.ExceptionHandler.ExceptionHandlerSubSystem::Enable",
            "Bannerlord.ButterLib.CrashUploader.CrashUploaderSubSystem::Enable",
            "Bannerlord.ButterLib.DelayedSubModule.DelayedSubModuleSubSystem::Enable",
            "Bannerlord.ButterLib.SubModuleWrappers2.SubModuleWrappers2SubSystem::Enable",
            "Bannerlord.ButterLib.ButterLibSubModule::ValidateLoadOrder",
        };
        int patched = 0;
        foreach (var module in asm.Modules)
        {
            foreach (var type in module.GetTypes())
            {
                foreach (var m in type.Methods)
                {
                    var key = type.FullName + "::" + m.Name;
                    // Also neutralize any WinForms-MessageBox validation method by name, in any mod:
                    // ValidateHarmony / ValidateLoadOrder pop a MessageBox on a perceived problem,
                    // which crashes the headless server.
                    bool byName = (m.Name == "ValidateHarmony" || m.Name == "ValidateLoadOrder");
                    if ((targets.Contains(key) || byName) && m.HasBody && m.Parameters.Count == 0)
                    {
                        m.Body.Instructions.Clear();
                        m.Body.Variables.Clear();
                        m.Body.ExceptionHandlers.Clear();
                        var il = m.Body.GetILProcessor();
                        il.Append(il.Create(OpCodes.Ret));
                        Console.WriteLine("NEUTRALIZED " + key);
                        patched++;
                    }
                }
            }
        }
        asm.Write(output);
        Console.WriteLine("patched methods: " + patched);
        return patched >= 1 ? 0 : 2;
    }
}
