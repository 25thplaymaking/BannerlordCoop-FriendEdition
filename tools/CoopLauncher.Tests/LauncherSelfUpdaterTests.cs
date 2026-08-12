using CoopLauncher.Services;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CoopLauncher.Tests;

public sealed class LauncherSelfUpdaterTests
{
    private const string ValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task CheckOnly_NewerManifestDoesNotDownloadExecutable()
    {
        int manifestRequests = 0;
        int executableRequests = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.True(request.Headers.CacheControl?.NoCache);
            Assert.True(request.Headers.CacheControl?.NoStore);
            if (request.RequestUri!.AbsolutePath.EndsWith("launcher.json"))
            {
                manifestRequests++;
                return JsonResponse(Manifest("2.0.0", ValidSha));
            }
            executableRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("must not download")),
            };
        }));

        LauncherUpdateCheck check = await Updater(http).CheckAsync(new Version(1, 0));

        Assert.Equal(ComponentUpdateState.UpdateAvailable, check.Status.State);
        Assert.Equal("1.0", check.Status.InstalledVersion);
        Assert.Equal("2.0.0", check.Status.AvailableVersion);
        Assert.Equal("test build", check.Status.Notes);
        Assert.NotNull(check.Manifest);
        Assert.Equal(1, manifestRequests);
        Assert.Equal(0, executableRequests);
    }

    [Fact]
    public async Task CheckOnly_UnreachableManifestIsUnverified()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            throw new HttpRequestException("offline")));

        LauncherUpdateCheck check = await Updater(http).CheckAsync(new Version(1, 0));

        Assert.Equal(ComponentUpdateState.Unverified, check.Status.State);
        Assert.Null(check.Manifest);
    }

    [Fact]
    public async Task CheckOnly_TimedOutManifestIsUnverified()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            throw new TaskCanceledException("timeout")));

        LauncherUpdateCheck check = await Updater(http).CheckAsync(new Version(1, 0));

        Assert.Equal(ComponentUpdateState.Unverified, check.Status.State);
        Assert.Contains("timed out", check.Status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckOnly_MissingRequiredFeedIsUnverified()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException("HTTP must not be called")));
        var updater = new LauncherSelfUpdater(new LauncherConfig { LauncherManifestUrl = "" }, http);

        LauncherUpdateCheck check = await updater.CheckAsync(new Version(1, 0));

        Assert.Equal(ComponentUpdateState.Unverified, check.Status.State);
        Assert.Contains("not configured", check.Status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "HTTP 404")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task CheckOnly_HttpFailureIsUnverified(HttpStatusCode status, string detail)
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(status)));

        LauncherUpdateCheck check = await Updater(http).CheckAsync(new Version(1, 0));

        Assert.Equal(ComponentUpdateState.Unverified, check.Status.State);
        Assert.Contains(detail, check.Status.Detail);
    }

    [Fact]
    public async Task CheckOnly_MalformedManifestIsUnverified()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{broken", Encoding.UTF8, "application/json"),
        }));

        LauncherUpdateCheck check = await Updater(http).CheckAsync(new Version(1, 0));

        Assert.Equal(ComponentUpdateState.Unverified, check.Status.State);
        Assert.Null(check.Manifest);
    }

    [Fact]
    public async Task CheckOnly_EqualOrOlderRemoteVersionIsCurrent()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            JsonResponse(Manifest("1.0.0", ValidSha))));

        LauncherUpdateCheck check = await Updater(http).CheckAsync(new Version(1, 1));

        Assert.Equal(ComponentUpdateState.Current, check.Status.State);
        Assert.Equal("1.1", check.Status.InstalledVersion);
        Assert.Equal("1.0.0", check.Status.AvailableVersion);
    }

    [Fact]
    public void ManifestRequiresNumericVersionHttpsPayloadAndExactSha256()
    {
        var valid = new LauncherUpdateManifest
        {
            Version = "2026.8.11.2",
            LauncherUrl = "https://github.com/example/releases/download/launcher/CalradiaCoop.exe",
            Sha256 = ValidSha,
        };

        Assert.True(LauncherSelfUpdater.IsManifestValid(valid));
        Assert.False(LauncherSelfUpdater.IsManifestValid(valid with { Version = "nightly" }));
        Assert.False(LauncherSelfUpdater.IsManifestValid(valid with { LauncherUrl = "http://example.test/CalradiaCoop.exe" }));
        Assert.False(LauncherSelfUpdater.IsManifestValid(valid with { Sha256 = "short" }));
    }

    [Theory]
    [InlineData("2026.8.11.2", 2026, 8, 11, 1, true)]
    [InlineData("2026.8.11.1", 2026, 8, 11, 1, false)]
    [InlineData("2026.8.11.0", 2026, 8, 11, 1, false)]
    [InlineData("invalid", 2026, 8, 11, 1, false)]
    public void RemoteVersionMustBeStrictlyNewer(
        string remote, int major, int minor, int build, int revision, bool expected)
    {
        Assert.Equal(expected, LauncherSelfUpdater.IsNewer(
            remote, new Version(major, minor, build, revision)));
    }

    [Fact]
    public async Task CurrentVersion_DoesNotDownloadOrStart()
    {
        int requests = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests++;
            Assert.EndsWith("launcher.json", request.RequestUri!.AbsolutePath);
            return JsonResponse(Manifest("2026.8.11.1", ValidSha));
        }));
        using var fixture = new UpdateFixture();
        bool started = false;

        LauncherUpdateResult result = await Updater(http).CheckAndStageAsync(
            fixture.TargetExe, new Version(2026, 8, 11, 1), (_, _) => { },
            _ => { started = true; return new Process(); });

        Assert.Equal(LauncherUpdateOutcome.UpToDate, result.Outcome);
        Assert.Equal(1, requests);
        Assert.False(started);
        Assert.Empty(fixture.UpdateDirectories());
    }

    [Fact]
    public async Task UnreachableManifest_ContinuesOffline()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            throw new HttpRequestException("offline")));
        using var fixture = new UpdateFixture();

        LauncherUpdateResult result = await Updater(http).CheckAndStageAsync(
            fixture.TargetExe, new Version(1, 0), (_, _) => { });

        Assert.Equal(LauncherUpdateOutcome.Offline, result.Outcome);
        Assert.Empty(fixture.UpdateDirectories());
    }

    [Fact]
    public async Task ReachedManifestHttpError_FailsClosed()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var fixture = new UpdateFixture();

        LauncherUpdateResult result = await Updater(http).CheckAndStageAsync(
            fixture.TargetExe, new Version(1, 0), (_, _) => { });

        Assert.Equal(LauncherUpdateOutcome.Failed, result.Outcome);
        Assert.Empty(fixture.UpdateDirectories());
    }

    [Fact]
    public async Task WrongPayloadHash_FailsWithoutStartingAndRemovesStage()
    {
        byte[] payload = Encoding.UTF8.GetBytes("new launcher");
        using var http = Feed(Manifest("2.0.0", ValidSha), payload);
        using var fixture = new UpdateFixture();
        bool started = false;

        LauncherUpdateResult result = await Updater(http).CheckAndStageAsync(
            fixture.TargetExe, new Version(1, 0), (_, _) => { },
            _ => { started = true; return new Process(); });

        Assert.Equal(LauncherUpdateOutcome.Failed, result.Outcome);
        Assert.False(started);
        Assert.Empty(fixture.UpdateDirectories());
    }

    [Fact]
    public async Task VerifiedNewerPayload_IsStagedBesideTargetAndStartedInApplyMode()
    {
        byte[] payload = Encoding.UTF8.GetBytes("new launcher");
        string sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        using var http = Feed(Manifest("2.0.0", sha), payload);
        using var fixture = new UpdateFixture();
        ProcessStartInfo? started = null;

        LauncherUpdateResult result = await Updater(http).CheckAndStageAsync(
            fixture.TargetExe, new Version(1, 0), (_, _) => { },
            info => { started = info; return new Process(); });

        Assert.Equal(LauncherUpdateOutcome.Restarting, result.Outcome);
        Assert.NotNull(started);
        Assert.StartsWith(fixture.Root, started!.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"{Path.DirectorySeparatorChar}.calradia-launcher-update-", started.FileName);
        Assert.Equal(payload, File.ReadAllBytes(started.FileName));
        Assert.Equal(LauncherUpdateCommand.ApplySwitch, started.ArgumentList[0]);
        Assert.Equal(Path.GetFullPath(fixture.TargetExe), started.ArgumentList[1]);
        Assert.Equal(sha, started.ArgumentList[3]);
    }

    [Theory]
    [InlineData(LauncherUpdateOutcome.Failed, false)]
    [InlineData(LauncherUpdateOutcome.Offline, true)]
    [InlineData(LauncherUpdateOutcome.UpToDate, true)]
    [InlineData(LauncherUpdateOutcome.Disabled, true)]
    public void StartupGate_BlocksOnlyFailedLauncherUpdate(
        LauncherUpdateOutcome outcome, bool expected)
    {
        Assert.Equal(expected, MainWindow.CanContinueAfterLauncherUpdate(
            new LauncherUpdateResult(outcome, "status")));
    }

    [Fact]
    public void RollbackSkipMarker_SuppressesOnlyExplicitOneShotLaunch()
    {
        Assert.True(LauncherUpdateApplier.ShouldSkipSelfUpdate(
            [LauncherUpdateApplier.SkipOnceSwitch]));
        Assert.False(LauncherUpdateApplier.ShouldSkipSelfUpdate([]));
        Assert.False(LauncherUpdateApplier.ShouldSkipSelfUpdate(
            [LauncherUpdateApplier.SkipOnceSwitch, "unexpected"]));
    }

    private static LauncherSelfUpdater Updater(HttpClient http) => new(
        new LauncherConfig { LauncherManifestUrl = "https://updates.example/launcher.json" }, http);

    private static LauncherUpdateManifest Manifest(string version, string sha) => new()
    {
        Version = version,
        LauncherUrl = "https://updates.example/CalradiaCoop.exe",
        Sha256 = sha,
        Notes = "test build",
    };

    private static HttpClient Feed(LauncherUpdateManifest manifest, byte[] payload) => new(
        new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("launcher.json")
            ? JsonResponse(manifest)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));

    private static HttpResponseMessage JsonResponse(LauncherUpdateManifest manifest) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class UpdateFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"launcher-update-test-{Guid.NewGuid():N}");
        public string TargetExe => Path.Combine(Root, "CalradiaCoop.exe");

        public UpdateFixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(TargetExe, "old launcher");
        }

        public string[] UpdateDirectories() =>
            Directory.GetDirectories(Root, ".calradia-launcher-update-*");

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
