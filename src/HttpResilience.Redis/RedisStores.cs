using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using HttpResilience.Throttling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace HttpResilience.Redis;

/// <summary>One <see cref="RedisRateLimiter"/> per throttle scope.</summary>
internal sealed class RedisRateLimiterFactory(
    RedisConnection connection,
    IOptions<RedisThrottlingOptions> options,
    HttpResilienceMetrics metrics,
    ILoggerFactory? loggerFactory = null) : IRateLimiterFactory
{
    private readonly ConcurrentDictionary<string, (ThrottleScope Scope, RedisRateLimiter Limiter)> _limiters = new(StringComparer.Ordinal);
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(HttpResilienceTelemetry.LogCategory);

    public IReadOnlyCollection<KeyValuePair<ThrottleScope, RateLimiter>> ActiveLimiters
        => _limiters.Values.Select(v => KeyValuePair.Create(v.Scope, (RateLimiter)v.Limiter)).ToArray();

    public RateLimiter GetLimiter(ThrottleScope scope)
    {
        var limit = scope.Limit ?? throw new ArgumentException("The scope has no limit.", nameof(scope));
        return _limiters.GetOrAdd(scope.Key, key => (scope, new RedisRateLimiter(
            connection, RedisKeys.Limiter(connection.KeyPrefix, key), limit, options.Value, metrics, _logger))).Limiter;
    }
}

/// <summary>Shares 429 blocks across instances.</summary>
/// <remarks>The blocked-until moment is written from this instance's clock and read against the reader's.</remarks>
internal sealed class RedisThrottleStateStore(RedisConnection connection, TimeProvider timeProvider) : IThrottleStateStore
{
    public async ValueTask<DateTimeOffset?> GetBlockedUntilAsync(string scopeKey, CancellationToken cancellationToken)
    {
        var database = await connection.GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var value = await database.StringGetAsync(RedisKeys.Blocked(connection.KeyPrefix, scopeKey)).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!value.HasValue || !value.TryParse(out long untilMs))
        {
            return null;
        }

        var until = DateTimeOffset.FromUnixTimeMilliseconds(untilMs);
        return until > timeProvider.GetUtcNow() ? until : null;
    }

    public async ValueTask BlockAsync(string scopeKey, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var ttl = until - timeProvider.GetUtcNow();
        if (ttl <= TimeSpan.Zero)
        {
            return;
        }

        var database = await connection.GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await database.ScriptEvaluateAsync(
                LuaScripts.Block,
                [RedisKeys.Blocked(connection.KeyPrefix, scopeKey)],
                [until.ToUnixTimeMilliseconds(), Math.Max(1L, (long)Math.Ceiling(ttl.TotalMilliseconds))])
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal static class RedisKeys
{
    public static RedisKey Limiter(string prefix, string scopeKey) => $"{prefix}:limiter:{scopeKey}";

    public static RedisKey Blocked(string prefix, string scopeKey) => $"{prefix}:blocked:{scopeKey}";
}

internal static partial class RedisLog
{
    [LoggerMessage(101, LogLevel.Warning, "Redis rate limiter for {Key} failed; failure mode {Mode}. With AllowRequests, outbound throttling is off until Redis is back.")]
    public static partial void LimiterFailed(ILogger logger, Exception exception, string key, LimiterFailureMode mode);
}
