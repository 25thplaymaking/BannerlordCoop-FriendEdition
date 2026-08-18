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
        Assert.DoesNotContain("RebellionsAndDemographics", config.ModuleToken);
        Assert.DoesNotContain("RBM", config.ModuleToken);
        Assert.DoesNotContain("PlayerSettlement", config.ModuleToken);
        Assert.EndsWith("*Europe1100*Europe1100Expanded*SnowballingKingdoms - EOE 1100*_MODULES_", config.ModuleToken);
        Assert.DoesNotContain("gfrontsEOENamesMod", config.ModuleToken);
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
    public void KnownLegacyProductionToken_MigratesWithoutLosingPrivateSettings()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"launcher-config-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new
            {
                serverPassword = "private-local-token",
                moduleToken = LauncherConfig.LegacyModuleTokenBeforeGearAndDemographics,
            }));

            LauncherConfig config = LauncherConfig.Load(tempPath);

            Assert.Equal("private-local-token", config.ServerPassword);
            Assert.Equal(LauncherConfig.CurrentModuleToken, config.ModuleToken);
            // The migration target is now the Europe 1100 loadout: the frameworks and the base
            // game stay active, everything else is staged-inactive, and the conversion is last.
            Assert.EndsWith("*Europe1100*Europe1100Expanded*SnowballingKingdoms - EOE 1100*_MODULES_",
                config.ModuleToken);
            Assert.DoesNotContain("OpenSource", config.ModuleToken);
            Assert.DoesNotContain("RebellionsAndDemographics", config.ModuleToken);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void PreEuropeConversionToken_MigratesToTheEmpiresOfEuropeOrder()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"launcher-config-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new
            {
                serverPassword = "private-local-token",
                moduleToken = LauncherConfig.LegacyModuleTokenBeforeEuropeConversion,
            }));

            LauncherConfig config = LauncherConfig.Load(tempPath);

            Assert.Equal("private-local-token", config.ServerPassword);
            Assert.Equal(LauncherConfig.CurrentModuleToken, config.ModuleToken);
            Assert.EndsWith("*Europe1100*Europe1100Expanded*SnowballingKingdoms - EOE 1100*_MODULES_", config.ModuleToken);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void CustomModuleToken_IsNotRewritten()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"launcher-config-{Guid.NewGuid():N}.json");
        const string customToken = "_MODULES_*Native*Sandbox*Coop*MyPrivateModule*_MODULES_";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new { moduleToken = customToken }));

            LauncherConfig config = LauncherConfig.Load(tempPath);

            Assert.Equal(customToken, config.ModuleToken);
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
