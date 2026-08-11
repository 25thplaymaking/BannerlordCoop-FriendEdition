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
}
