using CoopLauncher.Services;
using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace CoopLauncher.Tests;

public sealed class ModUpdaterTests
{
    [Fact]
    public async Task ReachedManifestHttpError_FailsClosed()
    {
        using var fixture = new UpdateFixture();
        var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(
                stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            while (await reader.ReadLineAsync() is { Length: > 0 }) { }
            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response);
        });

        try
        {
            var updater = new ModUpdater(new LauncherConfig
            {
                SuiteManifestUrl = "",
                UpdateManifestUrl = $"http://[::1]:{port}/missing.json",
            });

            UpdateResult result = await updater.RunAsync(fixture.Modules, (_, _) => { });

            Assert.Equal(UpdateOutcome.Failed, result.Outcome);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void ManifestRequiresExactSha256()
    {
        var missing = new UpdateManifest { Version = "1", ClientZipUrl = "https://example.invalid/client.zip" };
        var malformed = new UpdateManifest
        {
            Version = "1",
            ClientZipUrl = "https://example.invalid/client.zip",
            Sha256 = "not-a-hash"
        };
        var valid = new UpdateManifest
        {
            Version = "1",
            ClientZipUrl = "https://example.invalid/client.zip",
            Sha256 = new string('a', 64)
        };

        Assert.False(ModUpdater.IsManifestValid(missing));
        Assert.False(ModUpdater.IsManifestValid(malformed));
        Assert.True(ModUpdater.IsManifestValid(valid));
    }

    [Fact]
    public void ExactInstall_ReplacesModuleAndRemovesStaleFiles()
    {
        using var fixture = new UpdateFixture();
        fixture.WriteInstalled("Coop", "stale.dll", "old");
        string zip = fixture.CreateZip(("Coop/current.dll", "new"));
        string versionFile = Path.Combine(fixture.Modules, "Coop", "installed-version.txt");

        ModUpdater.InstallExact(zip, fixture.Modules, versionFile, "2026.8.11.1");

        Assert.False(File.Exists(Path.Combine(fixture.Modules, "Coop", "stale.dll")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(fixture.Modules, "Coop", "current.dll")));
        Assert.Equal("2026.8.11.1", File.ReadAllText(versionFile));
    }

    [Fact]
    public void InstallFailure_RollsBackPreviouslyInstalledModule()
    {
        using var fixture = new UpdateFixture();
        fixture.WriteInstalled("Coop", "current.dll", "old");
        string zip = fixture.CreateZip(("Coop/current.dll", "new"));
        string blockedParent = Path.Combine(fixture.Modules, "blocked-version-parent");
        File.WriteAllText(blockedParent, "not a directory");

        Assert.ThrowsAny<Exception>(() => ModUpdater.InstallExact(
            zip,
            fixture.Modules,
            Path.Combine(blockedParent, "version.txt"),
            "2026.8.11.2"));

        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.Modules, "Coop", "current.dll")));
    }

    [Fact]
    public void EscapingZipEntry_IsRejectedBeforeInstalledFilesChange()
    {
        using var fixture = new UpdateFixture();
        fixture.WriteInstalled("Coop", "current.dll", "old");
        string zip = fixture.CreateZip(("../outside.txt", "escape"));

        Assert.Throws<InvalidDataException>(() => ModUpdater.InstallExact(
            zip,
            fixture.Modules,
            Path.Combine(fixture.Modules, "Coop", "installed-version.txt"),
            "2026.8.11.3"));

        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.Modules, "Coop", "current.dll")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "outside.txt")));
    }

    [Fact]
    public void RequiredTierFailure_OutranksSuccessfulTierUpdate()
    {
        var result = ModUpdater.Combine(
            new UpdateResult(UpdateOutcome.Failed, "suite failed"),
            new UpdateResult(UpdateOutcome.Updated, "client updated"));

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal("suite failed", result.Message);
    }

    [Theory]
    [InlineData(UpdateOutcome.Failed, false)]
    [InlineData(UpdateOutcome.Offline, true)]
    [InlineData(UpdateOutcome.Updated, true)]
    [InlineData(UpdateOutcome.UpToDate, true)]
    [InlineData(UpdateOutcome.Disabled, true)]
    public void JoinGate_BlocksOnlyFailedRequiredUpdate(UpdateOutcome outcome, bool expected)
    {
        Assert.Equal(expected, MainWindow.CanJoinAfterUpdate(new UpdateResult(outcome, "status")));
    }

    private sealed class UpdateFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"coop-updater-test-{Guid.NewGuid():N}");
        public string Modules => Path.Combine(Root, "Modules");

        public UpdateFixture() => Directory.CreateDirectory(Modules);

        public void WriteInstalled(string module, string relativePath, string contents)
        {
            string path = Path.Combine(Modules, module, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public string CreateZip(params (string Path, string Contents)[] entries)
        {
            string zipPath = Path.Combine(Root, $"update-{Guid.NewGuid():N}.zip");
            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var item in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(item.Path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(item.Contents);
            }
            return zipPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
