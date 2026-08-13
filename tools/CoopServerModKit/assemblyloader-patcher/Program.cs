using CoopServerModKit.TaleWorlds;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: assemblyloader-patcher <TaleWorlds.Library.dll> <patched-output.dll>");
    return 1;
}

try
{
    TaleWorldsAssemblyLoaderPatcher.Patch(args[0], args[1]);
    Console.WriteLine("Patched the exact TaleWorlds.Library.AssemblyLoader.LoadFrom dependency boundary.");
    Console.WriteLine($"Output: {Path.GetFullPath(args[1])}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
