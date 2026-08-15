using DedicatedServerCompatibilityPatcher;

if (args.Length == 3 && string.Equals(args[0], "campaign-setters", StringComparison.Ordinal))
{
    try
    {
        int patched = CampaignSystemSetterPatcher.Patch(args[1], args[2]);
        Console.WriteLine($"Pinned NoInlining on {patched} concrete TaleWorlds.CampaignSystem property setters.");
        Console.WriteLine($"Output: {Path.GetFullPath(args[2])}");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
}

if (args.Length == 3 && string.Equals(args[0], "sandbox-server-descriptor", StringComparison.Ordinal))
{
    try
    {
        SandBoxServerDescriptorPatcher.Patch(args[1], args[2]);
        Console.WriteLine("Enabled only SandBox.SandBoxSubModule for the v1.4.8 Coop dedicated server.");
        Console.WriteLine($"Output: {Path.GetFullPath(args[2])}");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
}

if (args.Length != 4)
{
    Console.Error.WriteLine(
        "Usage: DedicatedServerCompatibilityPatcher <loader-patched DedicatedServer.Core.dll> " +
        "<release-paired-output.dll> <Coop server-bin> <SERVER-COOP-PAIRING.json>\n" +
        "   or: DedicatedServerCompatibilityPatcher campaign-setters " +
        "<TaleWorlds.CampaignSystem.dll> <patched-output.dll>\n" +
        "   or: DedicatedServerCompatibilityPatcher sandbox-server-descriptor " +
        "<SandBox/SubModule.xml> <server-output.xml>");
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
