using CoopServerModKit.ButterLib;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: butterlib-patcher <Bannerlord.ButterLib.dll> <patched-output.dll>");
    return 1;
}

try
{
    int patched = ButterLibHeadlessPatcher.Patch(args[0], args[1]);
    Console.WriteLine($"Neutralized {patched} exact ButterLib headless methods.");
    Console.WriteLine($"Output: {Path.GetFullPath(args[1])}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
