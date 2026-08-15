using CoopLauncher.Services;
using System;
using System.IO;
using Xunit;

namespace CoopLauncher.Tests;

public sealed class GameLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CoopGameLocatorTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FindInstallations_ReportsDeclaredVersionAndConfiguredRoot()
    {
        Directory.CreateDirectory(Path.Combine(_root, "bin", "Win64_Shipping_Client"));
        Directory.CreateDirectory(Path.Combine(_root, "Modules", "Native"));
        File.WriteAllBytes(Path.Combine(_root, "bin", "Win64_Shipping_Client", "Bannerlord.exe"), [1]);
        File.WriteAllText(Path.Combine(_root, "Modules", "Native", "SubModule.xml"),
            "<Module><Version value=\"v1.4.8\" /></Module>");

        GameLocator.GameInstallation install = Assert.Single(GameLocator.FindInstallations(_root));

        Assert.Equal("1.4.8", install.Version);
        Assert.Equal(Path.GetFullPath(_root), install.RootPath);
        Assert.Equal(install.ExePath, GameLocator.FindBannerlordExe(_root));
    }

    [Theory]
    [InlineData("v1.4.8", "1.4.8", true)]
    [InlineData("1.4.8", "V1.4.8", true)]
    [InlineData("1.4.7", "1.4.8", false)]
    [InlineData("unknown", "1.4.8", false)]
    public void VersionsMatch_NormalizesOnlyTheVersionPrefix(string installed, string required, bool expected)
    {
        Assert.Equal(expected, GameLocator.VersionsMatch(installed, required));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
