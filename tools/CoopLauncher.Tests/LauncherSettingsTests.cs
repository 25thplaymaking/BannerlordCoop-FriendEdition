using System.IO;
using CoopLauncher;
using Xunit;

namespace CoopLauncher.Tests;

public sealed class LauncherSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("launcher-settings-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string SettingsPath => Path.Combine(_dir, "launcher-settings.json");

    [Fact]
    public void RoundTrip_PersistsEveryPreference()
    {
        var settings = new LauncherSettings
        {
            GamePathOverride = @"D:\Games\Bannerlord",
            LauncherChannel = LauncherSettings.NightlyChannel,
            SuiteChannel = LauncherSettings.StableChannel,
            ClientChannel = LauncherSettings.NightlyChannel,
            RememberPassword = true,
            CloseAfterLaunch = false,
            VerboseLogging = true,
        };
        settings.Save(SettingsPath);

        LauncherSettings loaded = LauncherSettings.Load(SettingsPath);
        Assert.Equal(settings.GamePathOverride, loaded.GamePathOverride);
        Assert.Equal(settings.LauncherChannel, loaded.LauncherChannel);
        Assert.Equal(settings.SuiteChannel, loaded.SuiteChannel);
        Assert.Equal(settings.ClientChannel, loaded.ClientChannel);
        Assert.True(loaded.RememberPassword);
        Assert.False(loaded.CloseAfterLaunch);
        Assert.True(loaded.VerboseLogging);
    }

    [Fact]
    public void Load_MissingOrCorruptFile_FallsBackToDefaults()
    {
        Assert.True(LauncherSettings.Load(Path.Combine(_dir, "absent.json")).CloseAfterLaunch);

        File.WriteAllText(SettingsPath, "{ not json !!");
        LauncherSettings loaded = LauncherSettings.Load(SettingsPath);
        Assert.Equal(LauncherSettings.StableChannel, loaded.ClientChannel);
        Assert.False(loaded.RememberPassword);
    }

    [Theory]
    // Stable default URLs flip to nightly and back.
    [InlineData(
        "https://github.com/o/r/releases/download/client-stable/launcher.json", "client-stable",
        "client-nightly", "nightly",
        "https://github.com/o/r/releases/download/client-nightly/launcher.json")]
    [InlineData(
        "https://github.com/o/r/releases/download/client-nightly/launcher.json", "client-stable",
        "client-nightly", "stable",
        "https://github.com/o/r/releases/download/client-stable/launcher.json")]
    [InlineData(
        "https://github.com/o/r/releases/download/launcher-app/launcher.json", "launcher-app",
        "launcher-nightly", "nightly",
        "https://github.com/o/r/releases/download/launcher-nightly/launcher.json")]
    // Already on the requested channel: unchanged.
    [InlineData(
        "https://github.com/o/r/releases/download/client-stable/launcher.json", "client-stable",
        "client-nightly", "stable",
        "https://github.com/o/r/releases/download/client-stable/launcher.json")]
    // A bespoke feed a group admin pointed elsewhere is never mangled by a channel choice.
    [InlineData(
        "https://example.com/private/feed.json", "client-stable",
        "client-nightly", "nightly",
        "https://example.com/private/feed.json")]
    [InlineData("", "client-stable", "client-nightly", "nightly", "")]
    public void RewriteChannel_SwapsOnlyKnownTokens(
        string url, string stableToken, string nightlyToken, string channel, string expected)
    {
        Assert.Equal(expected, LauncherSettings.RewriteChannel(url, stableToken, nightlyToken, channel));
    }

    [Fact]
    public void ApplyTo_OverridesGamePathAndChannels_WithoutMutatingTheShippedConfig()
    {
        var config = new LauncherConfig();
        string shippedClientUrl = config.UpdateManifestUrl;
        var settings = new LauncherSettings
        {
            GamePathOverride = @"D:\Games\Bannerlord",
            ClientChannel = LauncherSettings.NightlyChannel,
            LauncherChannel = LauncherSettings.NightlyChannel,
        };

        LauncherConfig effective = settings.ApplyTo(config);

        Assert.Equal(@"D:\Games\Bannerlord", effective.GamePath);
        Assert.Contains("client-nightly", effective.UpdateManifestUrl);
        Assert.Contains("launcher-nightly", effective.LauncherManifestUrl);
        Assert.Contains("suite-stable", effective.SuiteManifestUrl);
        // The shipped config is untouched — one source of truth for what the admin deployed.
        Assert.Equal(shippedClientUrl, config.UpdateManifestUrl);
        Assert.Equal("", config.GamePath);
    }

    [Fact]
    public void ApplyTo_EmptyOverride_KeepsConfigGamePath()
    {
        var config = new LauncherConfig { GamePath = @"C:\Configured" };
        LauncherConfig effective = new LauncherSettings().ApplyTo(config);
        Assert.Equal(@"C:\Configured", effective.GamePath);
    }

    [Fact]
    public void PasswordProtection_RoundTrips_ForTheCurrentUser()
    {
        var settings = new LauncherSettings();
        settings.ProtectPassword("hunter2");
        Assert.NotEqual("", settings.ProtectedPassword);
        Assert.DoesNotContain("hunter2", settings.ProtectedPassword);
        Assert.Equal("hunter2", settings.UnprotectPassword());
    }

    [Fact]
    public void PasswordProtection_EmptyOrCorrupt_FailsClosedToEmpty()
    {
        var settings = new LauncherSettings();
        settings.ProtectPassword("");
        Assert.Equal("", settings.ProtectedPassword);
        Assert.Equal("", settings.UnprotectPassword());

        settings.ProtectedPassword = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        Assert.Equal("", settings.UnprotectPassword());

        settings.ProtectedPassword = "not-base64!";
        Assert.Equal("", settings.UnprotectPassword());
    }
}
