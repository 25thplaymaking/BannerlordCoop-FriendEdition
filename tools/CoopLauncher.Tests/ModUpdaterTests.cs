using CoopLauncher.Services;
using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CoopLauncher.Tests;

public sealed class ModUpdaterTests
{
    [Fact]
    public async Task CheckOnly_QueriesBothManifestsWithoutDownloadingPayloads()
    {
        using var fixture = new UpdateFixture();
        fixture.WriteInstalled("Coop", "installed-version.txt", "1.0");
        File.WriteAllText(Path.Combine(fixture.Modules, "coop-suite-version.txt"), "1.0");
        int manifestRequests = 0;
        int payloadRequests = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.True(request.Headers.CacheControl?.NoCache);
            Assert.True(request.Headers.CacheControl?.NoStore);
            if (request.RequestUri!.AbsolutePath.EndsWith(".json"))
            {
                manifestRequests++;
                string version = request.RequestUri.AbsolutePath.Contains("client") ? "2.0" : "1.0";
                return JsonResponse(Manifest(version, request.RequestUri.AbsolutePath.Contains("client")
                    ? "client.zip" : "suite.zip", new string('a', 64)));
            }
            payloadRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        ModUpdater updater = Updater(http);

        ModUpdateCheck check = await updater.CheckAsync(fixture.Modules);

        Assert.Equal(ComponentUpdateState.Current, check.SuiteStatus.State);
        Assert.Equal("1.0", check.SuiteStatus.InstalledVersion);
        Assert.Equal(ComponentUpdateState.UpdateAvailable, check.ClientStatus.State);
        Assert.Equal("1.0", check.ClientStatus.InstalledVersion);
        Assert.Equal("2.0", check.ClientStatus.AvailableVersion);
        Assert.Equal(2, manifestRequests);
        Assert.Equal(0, payloadRequests);
    }

    [Fact]
    public async Task CheckOnly_MissingReceiptIsReportedAsNotInstalledUpdate()
    {
        using var fixture = new UpdateFixture();
        using var http = new HttpClient(new StubHandler(request =>
            JsonResponse(Manifest("2.0", request.RequestUri!.AbsolutePath.Contains("client")
                ? "client.zip" : "suite.zip", new string('a', 64)))));

        ModUpdateCheck check = await Updater(http).CheckAsync(fixture.Modules);

        Assert.Equal(ComponentUpdateState.UpdateAvailable, check.SuiteStatus.State);
        Assert.Null(check.SuiteStatus.InstalledVersion);
        Assert.Equal(ComponentUpdateState.UpdateAvailable, check.ClientStatus.State);
        Assert.Null(check.ClientStatus.InstalledVersion);
    }

    [Fact]
    public async Task CheckOnly_UnreachableRequiredFeedIsUnverified()
    {
        using var fixture = new UpdateFixture();
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("client"))
                throw new HttpRequestException("offline");
            return JsonResponse(Manifest("1.0", "suite.zip", new string('a', 64)));
        }));

        ModUpdateCheck check = await Updater(http).CheckAsync(fixture.Modules);

        Assert.Equal(ComponentUpdateState.Unverified, check.ClientStatus.State);
        Assert.Null(check.ClientManifest);
    }

    [Fact]
    public async Task CheckOnly_MissingRequiredFeedIsUnverified()
    {
        using var fixture = new UpdateFixture();
        using var http = new HttpClient(new StubHandler(request =>
            JsonResponse(Manifest("1.0", "client.zip", new string('a', 64)))));
        var updater = new ModUpdater(new LauncherConfig
        {
            SuiteManifestUrl = "",
            UpdateManifestUrl = "https://updates.example/client.json",
        }, http);

        ModUpdateCheck check = await updater.CheckAsync(fixture.Modules);

        Assert.Equal(ComponentUpdateState.Unverified, check.SuiteStatus.State);
        Assert.Contains("not configured", check.SuiteStatus.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckOnly_UnsafePayloadUrlIsUnverified()
    {
        using var fixture = new UpdateFixture();
        using var http = new HttpClient(new StubHandler(request =>
            JsonResponse(new UpdateManifest
            {
                Version = "2.0",
                ClientZipUrl = "http://updates.example/unsafe.zip",
                Sha256 = new string('a', 64),
                Notes = "unsafe",
            })));

        ModUpdateCheck check = await Updater(http).CheckAsync(fixture.Modules);

        Assert.Equal(ComponentUpdateState.Unverified, check.SuiteStatus.State);
        Assert.Equal(ComponentUpdateState.Unverified, check.ClientStatus.State);
    }

    [Fact]
    public async Task InstallCheckedPlan_DownloadsSuiteBeforeClientAndWritesReceipts()
    {
        using var fixture = new UpdateFixture();
        byte[] suiteZip = CreateZipBytes(("Harmony/current.dll", "suite-new"));
        byte[] clientZip = CreateZipBytes(("Coop/current.dll", "client-new"));
        string suiteSha = Convert.ToHexString(SHA256.HashData(suiteZip)).ToLowerInvariant();
        string clientSha = Convert.ToHexString(SHA256.HashData(clientZip)).ToLowerInvariant();
        var payloadOrder = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("suite.json"))
                return JsonResponse(Manifest("2.0", "suite.zip", suiteSha));
            if (path.EndsWith("client.json"))
                return JsonResponse(Manifest("2.0", "client.zip", clientSha));
            if (path.EndsWith("suite.zip"))
            {
                payloadOrder.Add("suite");
                return BytesResponse(suiteZip);
            }
            Assert.True(File.Exists(Path.Combine(fixture.Modules, "Harmony", "current.dll")));
            payloadOrder.Add("client");
            return BytesResponse(clientZip);
        }));
        ModUpdater updater = Updater(http);
        ModUpdateCheck check = await updater.CheckAsync(fixture.Modules);

        UpdateResult result = await updater.InstallAsync(fixture.Modules, check, (_, _, _) => { });

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(new[] { "suite", "client" }, payloadOrder);
        Assert.Equal("2.0", File.ReadAllText(Path.Combine(fixture.Modules, "coop-suite-version.txt")));
        Assert.Equal("2.0", File.ReadAllText(Path.Combine(fixture.Modules, "Coop", "installed-version.txt")));
        Assert.Equal("suite-new", File.ReadAllText(Path.Combine(fixture.Modules, "Harmony", "current.dll")));
        Assert.Equal("client-new", File.ReadAllText(Path.Combine(fixture.Modules, "Coop", "current.dll")));
    }

    [Fact]
    public async Task ReachedManifestHttpError_FailsClosed()
    {
        using var fixture = new UpdateFixture();
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("suite"))
                return JsonResponse(Manifest("1.0", "suite.zip", new string('a', 64)));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        ModUpdater updater = Updater(http);

        UpdateResult result = await updater.RunAsync(fixture.Modules, (_, _) => { });

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
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
        var unsafeUrl = new UpdateManifest
        {
            Version = "1",
            ClientZipUrl = "http://example.invalid/client.zip",
            Sha256 = new string('a', 64)
        };

        Assert.False(ModUpdater.IsManifestValid(missing));
        Assert.False(ModUpdater.IsManifestValid(malformed));
        Assert.True(ModUpdater.IsManifestValid(valid));
        Assert.False(ModUpdater.IsManifestValid(unsafeUrl));
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

    private static ModUpdater Updater(HttpClient http) => new(
        new LauncherConfig
        {
            SuiteManifestUrl = "https://updates.example/suite.json",
            UpdateManifestUrl = "https://updates.example/client.json",
        }, http);

    private static UpdateManifest Manifest(string version, string asset, string sha) => new()
    {
        Version = version,
        ClientZipUrl = $"https://updates.example/{asset}",
        Sha256 = sha,
        Notes = $"{asset} notes",
    };

    private static HttpResponseMessage JsonResponse(UpdateManifest manifest) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage BytesResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    };

    private static byte[] CreateZipBytes(params (string Path, string Contents)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, string contents) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(contents);
            }
        }
        return stream.ToArray();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
