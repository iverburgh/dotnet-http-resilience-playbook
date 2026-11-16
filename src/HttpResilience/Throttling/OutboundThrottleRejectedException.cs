using Polly;

namespace HttpResilience.Throttling;

/// <summary>Why a request was refused before it reached the dependency.</summary>
public enum ThrottleRejectionReason
{
    /// <summary>The outbound rate limiter had no permit.</summary>
    RateLimit,

    /// <summary>The dependency answered 429 recently, and the scope is still blocked.</summary>
    ServerThrottle,
}

/// <summary>
/// Thrown when throttling refused a request and the throttle retries ran out. The dependency was not called
/// for the final attempt. Catch it where you would handle a 429.
/// </summary>
public sealed class OutboundThrottleRejectedException : ExecutionRejectedException
{
    /// <summary>Creates the exception.</summary>
    public OutboundThrottleRejectedException(ThrottleRejectionReason reason, string scopeKey, TimeSpan? retryAfter)
        : base(reason == ThrottleRejectionReason.RateLimit
            ? $"Outbound rate limit reached for {scopeKey}."
            : $"{scopeKey} answered 429 recently; the request was not sent.")
    {
        Reason = reason;
        ScopeKey = scopeKey;
        RetryAfter = retryAfter;
    }

    /// <summary>Why the request was refused.</summary>
    public ThrottleRejectionReason Reason { get; }

    /// <summary>The throttle scope, for example <c>api.partner.example/orders</c>.</summary>
    public string ScopeKey { get; }

    /// <summary>When trying again makes sense, if known.</summary>
    public TimeSpan? RetryAfter { get; }
}
