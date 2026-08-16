using System;

namespace Common.Messaging;

/// <summary>Stable terminal decisions returned by an authoritative route.</summary>
public enum AuthorityResultStatus
{
    Accepted,
    Rejected,
    Unauthorized,
    Unavailable,
    StaleSession,
    StaleState,
    InvalidRequest,
    ExecutionFailed,
}

/// <summary>Distinguishes a state-mutating command from a readiness/snapshot query.</summary>
public enum AuthorityRouteKind
{
    Command,
    BootstrapQuery,
}

/// <summary>
/// An explicit server-side header gate result. A non-valid result can only be a terminal
/// rejection status; it can never accidentally manufacture an Accepted response.
/// </summary>
public readonly struct AuthorityHeaderValidation
{
    private AuthorityHeaderValidation(AuthorityResultStatus status, string reasonCode)
    {
        if (status == AuthorityResultStatus.Accepted)
            throw new ArgumentOutOfRangeException(nameof(status), "Accepted is not a header-validation failure.");

        Status = status;
        ReasonCode = reasonCode;
    }

    public bool IsValid => ReasonCode == null;
    public AuthorityResultStatus Status { get; }
    public string ReasonCode { get; }

    public static AuthorityHeaderValidation Valid => default;

    public static AuthorityHeaderValidation Reject(AuthorityResultStatus status, string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("A stable failure reason is required.", nameof(reasonCode));
        return new AuthorityHeaderValidation(status, reasonCode);
    }
}

/// <summary>
/// Correlation and optimistic-concurrency data carried by a client-originated authority request.
/// The route is identified by the typed request message, never by a client-selected method name.
/// </summary>
public readonly struct AuthorityRequestHeader
{
    public const int MaximumSessionIdLength = 96;

    public AuthorityRequestHeader(int protocolVersion, string sessionId, long requestId, long expectedRevision)
    {
        ProtocolVersion = protocolVersion;
        SessionId = sessionId;
        RequestId = requestId;
        ExpectedRevision = expectedRevision;
    }

    public int ProtocolVersion { get; }
    public string SessionId { get; }
    public long RequestId { get; }
    public long ExpectedRevision { get; }

    public bool TryValidate(out string failure)
    {
        if (ProtocolVersion <= 0)
        {
            failure = "invalid-protocol-version";
            return false;
        }

        if (!IsBoundedToken(SessionId, MaximumSessionIdLength))
        {
            failure = "invalid-session-id";
            return false;
        }

        if (RequestId <= 0)
        {
            failure = "invalid-request-id";
            return false;
        }

        if (ExpectedRevision < 0)
        {
            failure = "invalid-expected-revision";
            return false;
        }

        failure = null;
        return true;
    }

    internal static bool IsBoundedToken(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength) return false;

        foreach (char character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character)) return false;
        }

        return true;
    }
}

/// <summary>Correlation and terminal decision metadata returned by an authoritative route.</summary>
public readonly struct AuthorityResultHeader
{
    public const int MaximumReasonCodeLength = 64;

    public AuthorityResultHeader(
        string sessionId,
        long requestId,
        AuthorityResultStatus status,
        long committedRevision,
        string reasonCode)
    {
        SessionId = sessionId;
        RequestId = requestId;
        Status = status;
        CommittedRevision = committedRevision;
        ReasonCode = reasonCode;
    }

    public string SessionId { get; }
    public long RequestId { get; }
    public AuthorityResultStatus Status { get; }
    public long CommittedRevision { get; }
    public string ReasonCode { get; }

    public bool TryValidate(out string failure)
    {
        if (!AuthorityRequestHeader.IsBoundedToken(SessionId, AuthorityRequestHeader.MaximumSessionIdLength))
        {
            failure = "invalid-session-id";
            return false;
        }

        if (RequestId <= 0)
        {
            failure = "invalid-request-id";
            return false;
        }

        if (CommittedRevision < 0)
        {
            failure = "invalid-committed-revision";
            return false;
        }

        if (!string.IsNullOrEmpty(ReasonCode) && !IsReasonCode(ReasonCode))
        {
            failure = "invalid-reason-code";
            return false;
        }

        failure = null;
        return true;
    }

    private static bool IsReasonCode(string value)
    {
        if (value.Length > MaximumReasonCodeLength) return false;

        foreach (char character in value)
        {
            bool isLowerAlpha = character >= 'a' && character <= 'z';
            bool isDigit = character >= '0' && character <= '9';
            if (!isLowerAlpha && !isDigit && character != '.' && character != '-' && character != '_') return false;
        }

        return true;
    }
}

/// <summary>Declares the stable identity and category of a typed authority request message.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class AuthorityRouteAttribute : Attribute
{
    public AuthorityRouteAttribute(string routeId, AuthorityRouteKind kind)
    {
        if (!IsRouteId(routeId)) throw new ArgumentException("A stable route id is required.", nameof(routeId));

        RouteId = routeId;
        Kind = kind;
    }

    public string RouteId { get; }
    public AuthorityRouteKind Kind { get; }

    private static bool IsRouteId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 96) return false;

        foreach (char character in value)
        {
            bool isLowerAlpha = character >= 'a' && character <= 'z';
            bool isDigit = character >= '0' && character <= '9';
            if (!isLowerAlpha && !isDigit && character != '.' && character != '-') return false;
        }

        return true;
    }
}
