using System.Diagnostics;
using CoopLauncher.Services;
using Xunit;

namespace CoopLauncher.Tests;

public sealed class GameLauncherTests
{
    [Fact]
    public void CreateStartInfo_UsesPasswordEnteredInLauncher()
    {
        var config = new LauncherConfig
        {
            ServerHost = "203.0.113.7",
            ServerPort = 4200,
            ServerPassword = "",
            ModuleToken = "_MODULES_*Coop*_MODULES_",
        };
        ProcessStartInfo startInfo = GameLauncher.CreateStartInfo(
            @"C:\Games\Bannerlord.exe",
            config,
            "8888");

        Assert.Equal(
            new[]
            {
                "/singleplayer",
                "_MODULES_*Coop*_MODULES_",
                "/coopjoin",
                "203.0.113.7",
                "4200",
                "8888",
            },
            startInfo.ArgumentList);
    }
}
