using System.Net;
using Microsoft.Extensions.Logging;

namespace HttpResilience;

internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Warning, "Transient failure calling {Method} {Host}{Path} ({Outcome}); retry {Attempt} in {DelayMs} ms.")]
    public static partial void TransientRetry(ILogger logger, HttpMethod? method, string? host, string? path, string outcome, int attempt, double delayMs);

    [LoggerMessage(2, LogLevel.Warning, "{Host}{Path} is throttled ({Source}); retry {Attempt} in {DelayMs} ms.")]
    public static partial void ThrottleRetry(ILogger logger, string host, string path, string source, int attempt, double delayMs);

    [LoggerMessage(3, LogLevel.Information, "Not sending {Method} to {ScopeKey}: it answered 429 and is blocked for another {RemainingMs} ms.")]
    public static partial void BlockedByCachedThrottle(ILogger logger, HttpMethod? method, string scopeKey, double remainingMs);

    [LoggerMessage(4, LogLevel.Warning, "Gave up on throttling for {ScopeKey} ({Reason}); the caller gets the refusal.")]
    public static partial void ThrottleExhausted(ILogger logger, string scopeKey, string reason);

    [LoggerMessage(5, LogLevel.Warning, "No rate limiter for {ScopeKey}; the request is sent without a limit.")]
    public static partial void LimiterUnavailable(ILogger logger, Exception exception, string scopeKey);

    [LoggerMessage(6, LogLevel.Warning, "Throttle state store failed on {Operation} for {ScopeKey}; continuing without shared 429 state.")]
    public static partial void StoreFailed(ILogger logger, Exception exception, string operation, string scopeKey);

    [LoggerMessage(7, LogLevel.Debug, "Not retrying {Method} {Host}{Path} after {StatusCode}: the method is not idempotent and the request is not marked safe to retry.")]
    public static partial void NotRetriedNonIdempotent(ILogger logger, HttpMethod method, string? host, string? path, HttpStatusCode? statusCode);
}
