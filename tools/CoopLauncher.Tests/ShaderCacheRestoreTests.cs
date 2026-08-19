using System;
using System.IO;
using Xunit;
using CoopLauncher.Services;

namespace CoopLauncher.Tests;

/// <summary>
/// Covers putting a conversion's shader cache back after it was renamed aside.
/// </summary>
/// <remarks>
/// Turning the neutralise option off cannot just mean "stop renaming": anyone who ran an earlier
/// launcher already has the cache disabled on disk, and without a restore they would keep 979 MB of
/// precompiled shader variants dead forever while compiling them at runtime instead.
/// </remarks>
public class ShaderCacheRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "shadercache-" + Guid.NewGuid().ToString("N"));

    private string ShadersDir => Path.Combine(_root, "Shaders", "D3D11");

    public ShaderCacheRestoreTests() => Directory.CreateDirectory(ShadersDir);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Sack => Path.Combine(ShadersDir, "compressed_shader_cache.sack");
    private string Disabled => Sack + ".disabled";

    [Fact]
    public void ADisabledCacheIsPutBack()
    {
        File.WriteAllText(Disabled, "cache");

        Assert.True(ConversionBootstrap.RestoreShaderCache(_root));

        Assert.True(File.Exists(Sack));
        Assert.False(File.Exists(Disabled));
        Assert.Equal("cache", File.ReadAllText(Sack));
    }

    [Fact]
    public void NothingToRestoreReportsNoChange()
    {
        File.WriteAllText(Sack, "cache");

        Assert.False(ConversionBootstrap.RestoreShaderCache(_root));
        Assert.True(File.Exists(Sack));
    }

    [Fact]
    public void AStaleDisabledCopyIsRemovedRatherThanRestoredOverALiveOne()
    {
        // A partially applied run can leave both. The live file is the real one; restoring the
        // disabled copy over it would replace a good cache with an older one.
        File.WriteAllText(Sack, "live");
        File.WriteAllText(Disabled, "stale");

        Assert.True(ConversionBootstrap.RestoreShaderCache(_root));

        Assert.Equal("live", File.ReadAllText(Sack));
        Assert.False(File.Exists(Disabled));
    }

    [Fact]
    public void RestoreIsTheInverseOfNeutralise()
    {
        File.WriteAllText(Sack, "cache");

        Assert.True(ConversionBootstrap.NeutralizeShaderCache(_root));
        Assert.False(File.Exists(Sack));

        Assert.True(ConversionBootstrap.RestoreShaderCache(_root));
        Assert.Equal("cache", File.ReadAllText(Sack));
    }

    [Fact]
    public void AModuleWithNoShadersDirectoryIsLeftAlone()
    {
        string bare = Path.Combine(_root, "bare-module");
        Directory.CreateDirectory(bare);

        Assert.False(ConversionBootstrap.RestoreShaderCache(bare));
    }
}
