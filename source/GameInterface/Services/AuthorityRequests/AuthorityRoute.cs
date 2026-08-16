using Common.Messaging;
using GameInterface.Services.Players.Data;
using LiteNetLib;
using System;

namespace GameInterface.Services.AuthorityRequests;

public enum AuthorityCommitProbeResult
{
    Pending,
    Applied,
    Invalid,
}

public enum AuthorityClientCompletion
{
    Applied,
    Rejected,
    TimedOut,
    Cancelled,
    ReplicaApplyFailed,
}

public sealed class AuthorityTimeoutPolicy
{
    public static readonly AuthorityTimeoutPolicy CampaignMutation = new AuthorityTimeoutPolicy(
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), 1);
    public static readonly AuthorityTimeoutPolicy BootstrapQuery = new AuthorityTimeoutPolicy(
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), 3);

    public AuthorityTimeoutPolicy(TimeSpan responseTimeout, TimeSpan applyTimeout, int retryCount)
    {
        if (responseTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        if (applyTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(applyTimeout));
        if (retryCount < 0 || retryCount > 3) throw new ArgumentOutOfRangeException(nameof(retryCount));

        ResponseTimeout = responseTimeout;
        ApplyTimeout = applyTimeout;
        RetryCount = retryCount;
    }

    public TimeSpan ResponseTimeout { get; }
    public TimeSpan ApplyTimeout { get; }
    public int RetryCount { get; }
}

public readonly struct AuthorityServerContext
{
    public AuthorityServerContext(NetPeer peer, Player player, AuthorityRequestHeader header, string routeId)
    {
        Peer = peer;
        Player = player;
        Header = header;
        RouteId = routeId;
    }

    public NetPeer Peer { get; }
    public Player Player { get; }
    public AuthorityRequestHeader Header { get; }
    public string RouteId { get; }
}

public readonly struct AuthorityServerReply<TResult> where TResult : IMessage
{
    public AuthorityServerReply(TResult result, bool statePublished, bool suppressReply = false)
    {
        Result = result;
        StatePublished = statePublished;
        SuppressReply = suppressReply;
    }

    public TResult Result { get; }
    public bool StatePublished { get; }
    /// <summary>Used only after the request peer has been deliberately isolated following a partial publication.</summary>
    public bool SuppressReply { get; }
}

public readonly struct AuthorityClientOutcome<TResult> where TResult : IMessage
{
    public AuthorityClientOutcome(AuthorityClientCompletion completion, TResult result, string reasonCode)
    {
        Completion = completion;
        Result = result;
        ReasonCode = reasonCode;
    }

    public AuthorityClientCompletion Completion { get; }
    public TResult Result { get; }
    public string ReasonCode { get; }
    public bool Applied => Completion == AuthorityClientCompletion.Applied;
}

public interface IAuthorityRouteHandle<TIntent, TResult> : IDisposable where TResult : IMessage
{
    AuthorityRequestLifecycle Lifecycle { get; }
    AuthorityRequestTicket<TResult> Submit(TIntent intent, Action<AuthorityClientOutcome<TResult>> completion = null);
    AuthorityClientOutcome<TResult> SubmitBlocking(TIntent intent);
    void Poll();
    void CancelAll(string reasonCode);
}

public sealed class AuthorityRequestTicket<TResult> where TResult : IMessage
{
    internal AuthorityRequestTicket(long requestId)
    {
        RequestId = requestId;
    }

    public long RequestId { get; }
    public bool IsCompleted { get; internal set; }
    public AuthorityClientOutcome<TResult> Outcome { get; internal set; }
}

/// <summary>Typed feature adapter. It contains no remote method metadata or untyped payload.</summary>
public sealed class AuthorityRoute<TIntent, TRequest, TResult>
    where TRequest : IMessage
    where TResult : IMessage
{
    private AuthorityRoute(
        string routeId,
        AuthorityRouteKind kind,
        Func<long, AuthorityRequestHeader> createHeader,
        Func<TIntent, AuthorityRequestHeader, TRequest> buildRequest,
        Func<TRequest, AuthorityRequestHeader> readRequestHeader,
        Func<TResult, AuthorityResultHeader> readResultHeader,
        Func<TRequest, string> validateWireShape,
        Func<TRequest, string> buildCommandKey,
        Func<AuthorityRequestHeader, AuthorityHeaderValidation> validateHeader,
        Func<AuthorityServerContext, TRequest, AuthorityServerReply<TResult>> execute,
        Func<AuthorityRequestHeader, AuthorityResultStatus, string, TResult> createTerminalResult,
        Func<TResult, AuthorityCommitProbeResult> probeClientCommit,
        Action<TResult> requestResync,
        Action<AuthorityClientOutcome<TResult>> presentTerminalOutcome,
        Func<object, bool> isTrustedResultSource,
        AuthorityTimeoutPolicy timeoutPolicy,
        bool requireAuthenticatedPlayer,
        bool failClosedOnApplyFailure,
        Func<TRequest, TResult, bool> isExpectedClientResult)
    {
        RouteId = routeId ?? throw new ArgumentNullException(nameof(routeId));
        Kind = kind;
        CreateHeader = createHeader ?? throw new ArgumentNullException(nameof(createHeader));
        BuildRequest = buildRequest ?? throw new ArgumentNullException(nameof(buildRequest));
        ReadRequestHeader = readRequestHeader ?? throw new ArgumentNullException(nameof(readRequestHeader));
        ReadResultHeader = readResultHeader ?? throw new ArgumentNullException(nameof(readResultHeader));
        ValidateWireShape = validateWireShape ?? throw new ArgumentNullException(nameof(validateWireShape));
        BuildCommandKey = buildCommandKey ?? throw new ArgumentNullException(nameof(buildCommandKey));
        ValidateHeader = validateHeader ?? throw new ArgumentNullException(nameof(validateHeader));
        Execute = execute ?? throw new ArgumentNullException(nameof(execute));
        CreateTerminalResult = createTerminalResult ?? throw new ArgumentNullException(nameof(createTerminalResult));
        ProbeClientCommit = probeClientCommit ?? throw new ArgumentNullException(nameof(probeClientCommit));
        IsExpectedClientResult = isExpectedClientResult ?? ((_, _) => true);
        RequestResync = requestResync ?? (_ => { });
        PresentTerminalOutcome = presentTerminalOutcome ?? (_ => { });
        IsTrustedResultSource = isTrustedResultSource ?? throw new ArgumentNullException(nameof(isTrustedResultSource));
        TimeoutPolicy = timeoutPolicy ?? throw new ArgumentNullException(nameof(timeoutPolicy));
        RequireAuthenticatedPlayer = requireAuthenticatedPlayer;
        FailClosedOnApplyFailure = failClosedOnApplyFailure;
    }

    public string RouteId { get; }
    public AuthorityRouteKind Kind { get; }
    public Func<long, AuthorityRequestHeader> CreateHeader { get; }
    public Func<TIntent, AuthorityRequestHeader, TRequest> BuildRequest { get; }
    public Func<TRequest, AuthorityRequestHeader> ReadRequestHeader { get; }
    public Func<TResult, AuthorityResultHeader> ReadResultHeader { get; }
    public Func<TRequest, string> ValidateWireShape { get; }
    public Func<TRequest, string> BuildCommandKey { get; }
    public Func<AuthorityRequestHeader, AuthorityHeaderValidation> ValidateHeader { get; }
    public Func<AuthorityServerContext, TRequest, AuthorityServerReply<TResult>> Execute { get; }
    public Func<AuthorityRequestHeader, AuthorityResultStatus, string, TResult> CreateTerminalResult { get; }
    public Func<TResult, AuthorityCommitProbeResult> ProbeClientCommit { get; }
    public Func<TRequest, TResult, bool> IsExpectedClientResult { get; }
    public Action<TResult> RequestResync { get; }
    public Action<AuthorityClientOutcome<TResult>> PresentTerminalOutcome { get; }
    public Func<object, bool> IsTrustedResultSource { get; }
    public AuthorityTimeoutPolicy TimeoutPolicy { get; }
    public bool RequireAuthenticatedPlayer { get; }
    /// <summary>Use when no route-specific canonical resync exists and reconnect is the only safe recovery.</summary>
    public bool FailClosedOnApplyFailure { get; }

    public static AuthorityRoute<TIntent, TRequest, TResult> Define(
        string routeId,
        AuthorityRouteKind kind,
        Func<long, AuthorityRequestHeader> createHeader,
        Func<TIntent, AuthorityRequestHeader, TRequest> buildRequest,
        Func<TRequest, AuthorityRequestHeader> readRequestHeader,
        Func<TResult, AuthorityResultHeader> readResultHeader,
        Func<TRequest, string> validateWireShape,
        Func<TRequest, string> buildCommandKey,
        Func<AuthorityRequestHeader, AuthorityHeaderValidation> validateHeader,
        Func<AuthorityServerContext, TRequest, AuthorityServerReply<TResult>> execute,
        Func<AuthorityRequestHeader, AuthorityResultStatus, string, TResult> createTerminalResult,
        Func<TResult, AuthorityCommitProbeResult> probeClientCommit,
        Action<TResult> requestResync,
        Action<AuthorityClientOutcome<TResult>> presentTerminalOutcome,
        Func<object, bool> isTrustedResultSource,
        AuthorityTimeoutPolicy timeoutPolicy,
        bool requireAuthenticatedPlayer = true,
        bool failClosedOnApplyFailure = false,
        Func<TRequest, TResult, bool> isExpectedClientResult = null) =>
        new AuthorityRoute<TIntent, TRequest, TResult>(
            routeId, kind, createHeader, buildRequest, readRequestHeader, readResultHeader,
            validateWireShape, buildCommandKey, validateHeader, execute, createTerminalResult,
            probeClientCommit, requestResync, presentTerminalOutcome, isTrustedResultSource, timeoutPolicy,
            requireAuthenticatedPlayer, failClosedOnApplyFailure, isExpectedClientResult);
}
