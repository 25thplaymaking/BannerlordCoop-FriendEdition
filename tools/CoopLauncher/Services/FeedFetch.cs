using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace CoopLauncher.Services;

internal enum FeedFetchStatus { Success, TransientFailure, PermanentFailure }

/// <param name="Body">The manifest text on <see cref="FeedFetchStatus.Success"/>; otherwise null.</param>
/// <param name="Detail">A member-facing reason string, reused verbatim by the callers' Unverified records.</param>
internal readonly record struct FeedFetchResult(FeedFetchStatus Status, string? Body, string Detail);

/// <summary>
/// One place to GET an update "scroll" (launcher / mod-suite / co-op-client manifest) resiliently.
/// A single transient GitHub blip — release-assets returning HTTP 5xx/429, a dropped connection, or a
/// slow round-trip — used to fail the armory check closed and leave the launcher stuck on
/// THE COURT JESTER IS ASLEEP until the member hammered the retry button (observed 2026-08-12: all three
/// feeds 503'd for ~4 minutes, then recovered on their own). This retries a few times with a short
/// per-attempt timeout and backoff before giving up, so a brief blip self-heals without weakening the
/// fail-closed guarantee. A genuine 404/403 or a malformed body is returned immediately without retrying —
/// those never recover on a retry, and the member should hear the real reason fast.
/// </summary>
internal static class FeedFetch
{
    /// <summary>Cap a single manifest GET well under the shared client timeout so one stall can't hang the check.</summary>
    internal static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Delays between attempts; its length is the retry count (so attempts = Backoff.Count + 1).</summary>
    internal static readonly IReadOnlyList<TimeSpan> Backoff =
    [
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(900),
        TimeSpan.FromMilliseconds(1800),
    ];

    internal static async Task<FeedFetchResult> GetStringAsync(
        HttpClient http, Uri uri, string label, Func<TimeSpan, Task> delay)
    {
        FeedFetchResult result = default;
        for (int attempt = 0; ; attempt++)
        {
            result = await AttemptAsync(http, uri, label);
            if (result.Status != FeedFetchStatus.TransientFailure || attempt >= Backoff.Count)
                return result;
            await delay(Backoff[attempt]);
        }
    }

    private static async Task<FeedFetchResult> AttemptAsync(HttpClient http, Uri uri, string label)
    {
        using var timeout = new CancellationTokenSource(PerAttemptTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using HttpResponseMessage response = await http.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
                return new(FeedFetchStatus.Success, await response.Content.ReadAsStringAsync(), "ok");
            return Failure(response.StatusCode, label);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            return Failure(ex.StatusCode.Value, label);
        }
        catch (HttpRequestException)
        {
            return new(FeedFetchStatus.TransientFailure, null, $"{label} feed could not be reached.");
        }
        catch (OperationCanceledException)
        {
            return new(FeedFetchStatus.TransientFailure, null, $"{label} feed check timed out.");
        }
    }

    private static FeedFetchResult Failure(HttpStatusCode code, string label) =>
        new(IsTransient(code) ? FeedFetchStatus.TransientFailure : FeedFetchStatus.PermanentFailure,
            null, $"{label} feed returned HTTP {(int)code}.");

    /// <summary>Codes that a retry can plausibly clear: request timeout, rate-limit, and any 5xx.</summary>
    private static bool IsTransient(HttpStatusCode code) => (int)code switch
    {
        408 or 429 => true,
        >= 500 and <= 599 => true,
        _ => false,
    };
}
