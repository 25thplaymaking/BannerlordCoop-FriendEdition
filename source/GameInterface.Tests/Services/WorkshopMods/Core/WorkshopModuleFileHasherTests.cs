using GameInterface.Services.WorkshopMods.Core;
using System;
using System.IO;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Core;

public sealed class WorkshopModuleFileHasherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "coop-workshop-hash-" + Guid.NewGuid());

    public WorkshopModuleFileHasherTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Hash_IsIndependentOfRootAndCreationOrder()
    {
        string first = Path.Combine(root, "first");
        string second = Path.Combine(root, "second");
        Directory.CreateDirectory(Path.Combine(first, "ModuleData"));
        Directory.CreateDirectory(Path.Combine(second, "ModuleData"));

        File.WriteAllBytes(Path.Combine(first, "component.dll"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(first, "ModuleData", "settings.xml"), "<settings value=\"1\" />");
        File.WriteAllText(Path.Combine(second, "ModuleData", "settings.xml"), "<settings value=\"1\" />");
        File.WriteAllBytes(Path.Combine(second, "component.dll"), new byte[] { 1, 2, 3 });

        var hasher = new WorkshopModuleFileHasher();
        WorkshopModuleHashResult firstHash = hasher.Hash(first);
        WorkshopModuleHashResult secondHash = hasher.Hash(second);

        Assert.Equal(firstHash.ContentSha256, secondHash.ContentSha256);
        Assert.Equal(firstHash.ConfigurationSha256, secondHash.ConfigurationSha256);
    }

    [Fact]
    public void Hash_SeparatesCodeAndConfiguration_AndIgnoresHostSettings()
    {
        string module = Path.Combine(root, "module");
        Directory.CreateDirectory(Path.Combine(module, "ModuleData"));
        string binary = Path.Combine(module, "component.dll");
        string config = Path.Combine(module, "ModuleData", "rules.json");
        string hostConfig = Path.Combine(module, "mod-config.json");
        File.WriteAllBytes(binary, new byte[] { 1, 2, 3 });
        File.WriteAllText(config, "{\"rule\":1}");
        File.WriteAllText(hostConfig, "{\"server\":1}");

        var hasher = new WorkshopModuleFileHasher();
        WorkshopModuleHashResult original = hasher.Hash(module);

        File.WriteAllText(hostConfig, "{\"server\":2}");
        WorkshopModuleHashResult hostSettingsChanged = hasher.Hash(module);
        Assert.Equal(original.ContentSha256, hostSettingsChanged.ContentSha256);
        Assert.Equal(original.ConfigurationSha256, hostSettingsChanged.ConfigurationSha256);

        File.WriteAllText(config, "{\"rule\":2}");
        WorkshopModuleHashResult configurationChanged = hasher.Hash(module);
        Assert.Equal(original.ContentSha256, configurationChanged.ContentSha256);
        Assert.NotEqual(original.ConfigurationSha256, configurationChanged.ConfigurationSha256);

        File.WriteAllBytes(binary, new byte[] { 1, 2, 4 });
        WorkshopModuleHashResult codeChanged = hasher.Hash(module);
        Assert.NotEqual(configurationChanged.ContentSha256, codeChanged.ContentSha256);
    }

    /// <summary>
    /// A dedicated-server host copies each module's client binaries into bin\Win64_Shipping_Server
    /// because TaleWorlds' server engine only resolves that folder. The shipped package never
    /// contains it, so the overlay must not change either hash — otherwise every server-side
    /// module would report as an unmanaged copy and no client could join.
    /// </summary>
    [Fact]
    public void Hash_IgnoresServerBinOverlay()
    {
        string module = Path.Combine(root, "module");
        Directory.CreateDirectory(Path.Combine(module, "bin", "Win64_Shipping_Client"));
        File.WriteAllBytes(Path.Combine(module, "bin", "Win64_Shipping_Client", "component.dll"),
            new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(module, "SubModule.xml"), "<Module />");

        var hasher = new WorkshopModuleFileHasher();
        WorkshopModuleHashResult original = hasher.Hash(module);

        Directory.CreateDirectory(Path.Combine(module, "bin", "Win64_Shipping_Server"));
        File.WriteAllBytes(Path.Combine(module, "bin", "Win64_Shipping_Server", "component.dll"),
            new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(module, "bin", "Win64_Shipping_Server", "extra.xml"), "<x />");
        WorkshopModuleHashResult overlaid = hasher.Hash(module);

        Assert.Equal(original.ContentSha256, overlaid.ContentSha256);
        Assert.Equal(original.ConfigurationSha256, overlaid.ConfigurationSha256);
    }

    /// <summary>
    /// Improved Garrisons appends to ModuleData\ErrorLog.xml inside its own module root whenever
    /// one of its methods throws, which the headless host does on every campaign tick. Hashing
    /// that runtime log made the host's package bytes drift away from the receipt within minutes
    /// of starting, so it advertised an unmanaged copy and refused every join with a
    /// configuration mismatch no player could act on. The file is a log, not package content.
    /// </summary>
    [Fact]
    public void Hash_IgnoresModWrittenRuntimeErrorLog()
    {
        string module = Path.Combine(root, "module");
        Directory.CreateDirectory(Path.Combine(module, "ModuleData"));
        File.WriteAllBytes(Path.Combine(module, "component.dll"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(module, "ModuleData", "language_data.xml"), "<base />");
        File.WriteAllText(Path.Combine(module, "SubModule.xml"), "<Module />");

        var hasher = new WorkshopModuleFileHasher();
        WorkshopModuleHashResult packaged = hasher.Hash(module);

        string errorLog = Path.Combine(module, "ModuleData", "ErrorLog.xml");
        File.WriteAllText(errorLog, "New Log Entry: get_SaveFilesPath threw");
        WorkshopModuleHashResult afterFirstThrow = hasher.Hash(module);

        Assert.Equal(packaged.ContentSha256, afterFirstThrow.ContentSha256);
        Assert.Equal(packaged.ConfigurationSha256, afterFirstThrow.ConfigurationSha256);

        // The mod appends, so the log keeps growing for as long as the host runs.
        File.AppendAllText(errorLog, "\nNew Log Entry: get_SaveFilesPath threw again");
        WorkshopModuleHashResult afterMoreThrows = hasher.Hash(module);

        Assert.Equal(packaged.ContentSha256, afterMoreThrows.ContentSha256);
        Assert.Equal(packaged.ConfigurationSha256, afterMoreThrows.ConfigurationSha256);
    }

    /// <summary>
    /// The exclusion is by exact file name: a genuinely shipped configuration file must still be
    /// covered even when its name merely contains "errorlog".
    /// </summary>
    [Fact]
    public void Hash_StillCoversShippedFilesWithSimilarNames()
    {
        string module = Path.Combine(root, "module");
        Directory.CreateDirectory(Path.Combine(module, "ModuleData"));
        File.WriteAllText(Path.Combine(module, "SubModule.xml"), "<Module />");

        var hasher = new WorkshopModuleFileHasher();
        WorkshopModuleHashResult original = hasher.Hash(module);

        File.WriteAllText(Path.Combine(module, "ModuleData", "errorlog_settings.xml"), "<settings />");
        WorkshopModuleHashResult withSimilarName = hasher.Hash(module);

        Assert.NotEqual(original.ConfigurationSha256, withSimilarName.ConfigurationSha256);
    }
}
