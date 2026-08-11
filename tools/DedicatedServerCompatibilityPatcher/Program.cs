using DedicatedServerCompatibilityPatcher;

if (args.Length != 4)
{
    Console.Error.WriteLine(
        "Usage: DedicatedServerCompatibilityPatcher <loader-patched DedicatedServer.Core.dll> " +
        "<release-paired-output.dll> <Coop server-bin> <SERVER-COOP-PAIRING.json>");
    return 1;
}

try
{
    DedicatedServerReleasePairingPatcher.Patch(args[0], args[1], args[2], args[3]);
    Console.WriteLine("Pinned the four Coop module hashes and restored fail-closed verification.");
    Console.WriteLine($"Output:  {Path.GetFullPath(args[1])}");
    Console.WriteLine($"Receipt: {Path.GetFullPath(args[3])}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
