using System.IO;
using System.Text.Json;
using Xunit;

namespace CoopLauncher.Tests;

public sealed class LauncherConfigTests
{
    [Fact]
    public void MissingConfig_FallsBackWithoutPublishingAJoinPassword()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"missing-launcher-config-{Guid.NewGuid():N}.json");

        LauncherConfig config = LauncherConfig.Load(missingPath);

        Assert.Equal(string.Empty, config.ServerPassword);
    }

    [Fact]
    public void ShippedConfig_DoesNotContainAJoinPassword()
    {
        string shippedConfigPath = Path.Combine(AppContext.BaseDirectory, "launcher-config.json");

        LauncherConfig config = LauncherConfig.Load(shippedConfigPath);

        Assert.Equal(string.Empty, config.ServerPassword);
        Assert.Equal(
            "https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/releases/download/launcher-app/launcher.json",
            config.LauncherManifestUrl);
        Assert.Equal("1.4.8", config.RequiredGameVersion);
        Assert.Empty(config.BlockedModuleIds);
        Assert.Contains("RebellionsAndDemographics", config.ModuleToken);
    }

    [Fact]
    public void ExistingConfigWithoutLauncherFeed_InheritsStableFeed()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"launcher-config-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, "{ \"groupName\": \"Existing Group\" }");

            LauncherConfig config = LauncherConfig.Load(tempPath);

            Assert.Equal("Existing Group", config.GroupName);
            Assert.Equal(
                "https://github.com/25thplaymaking/BannerlordCoop-FriendEdition/releases/download/launcher-app/launcher.json",
                config.LauncherManifestUrl);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void PrivateLocalConfig_CanStillSupplyAJoinPassword()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"launcher-config-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new { serverPassword = "private-local-token" }));

            LauncherConfig config = LauncherConfig.Load(tempPath);

            Assert.Equal("private-local-token", config.ServerPassword);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void EmptyProductionBlocklist_DoesNotRejectTheApprovedRAndDToken()
    {
        var config = new LauncherConfig
        {
            ModuleToken = "_MODULES_*Coop*RebellionsAndDemographics*_MODULES_",
        };

        Assert.Null(config.GetBlockedModuleInToken());
    }
}
