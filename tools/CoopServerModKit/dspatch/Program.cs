using CoopServerModKit.DedicatedServer;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dspatch <DedicatedServer.Core.dll> <patched-output.dll>");
    return 1;
}

try
{
    DedicatedServerLoaderPatcher.Patch(args[0], args[1]);
    Console.WriteLine("Patched the exact DedicatedServer.CoopDriver.EnsureLoaded(string) method.");
    Console.WriteLine($"Output: {Path.GetFullPath(args[1])}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
