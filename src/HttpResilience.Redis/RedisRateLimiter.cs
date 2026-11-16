using System.Threading.RateLimiting;
using HttpResilience.Throttling;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace HttpResilience.Redis;

/// <summary>
/// A limiter whose counter lives in Redis, so every instance shares one budget. Asynchronous only:
/// <see cref="RateLimiter.AttemptAcquire(int)"/> always refuses, because it cannot wait for Redis.
/// </summary>
internal sealed class RedisRateLimiter : RateLimiter
{
    private readonly RedisConnection _connection;
    private readonly RedisKey _key;
    private readonly ThrottlingRuleBase _limit;
    private readonly RedisThrottlingOptions _options;
    private readonly HttpResilienceMetrics _metrics;
    private readonly ILogger _logger;
    private long _lastRemaining;

    public RedisRateLimiter(RedisConnection connection, RedisKey key, ThrottlingRuleBase limit, RedisThrottlingOptions options, HttpResilienceMetrics metrics, ILogger logger)
    {
        _connection = connection;
        _key = key;
        _limit = limit;
        _options = options;
        _metrics = metrics;
        _logger = logger;
        _lastRemaining = limit.PermitLimit;
    }

    public override TimeSpan? IdleDuration => null;

    /// <summary>The remaining count from the last call; no Redis round trip.</summary>
    public override RateLimiterStatistics? GetStatistics() => new() { CurrentAvailablePermits = Interlocked.Read(ref _lastRemaining) };

    protected override RateLimitLease AttemptAcquireCore(int permitCount) => RedisLease.Rejected(null);

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        if (permitCount > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(permitCount), permitCount, "The Redis limiter hands out one permit at a time.");
        }

        if (permitCount == 0)
        {
            return RedisLease.Acquired(null);
        }

        try
        {
            var database = await _connection.GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
            var member = Guid.NewGuid().ToString("N");
            var (script, args) = _limit.Algorithm switch
            {
                RateLimitAlgorithm.FixedWindow => (LuaScripts.FixedWindow, new RedisValue[] { _limit.PermitLimit, Ms(_limit.Window) }),
                RateLimitAlgorithm.SlidingWindow => (LuaScripts.SlidingWindow, new RedisValue[] { _limit.PermitLimit, Ms(_limit.Window), member }),
                RateLimitAlgorithm.TokenBucket => (LuaScripts.TokenBucket, new RedisValue[] { _limit.PermitLimit, _limit.TokensPerPeriod, Ms(_limit.ReplenishmentPeriod) }),
                RateLimitAlgorithm.Concurrency => (LuaScripts.Concurrency, new RedisValue[] { _limit.PermitLimit, Ms(_options.ConcurrencyLeaseTimeout), member }),
                _ => throw new InvalidOperationException($"Unknown algorithm {_limit.Algorithm}."),
            };

            var result = (RedisResult[])(await database.ScriptEvaluateAsync(script, [_key], args).WaitAsync(cancellationToken).ConfigureAwait(false))!;
            var allowed = (long)result[0] == 1;
            Interlocked.Exchange(ref _lastRemaining, (long)result[1]);

            if (!allowed)
            {
                var retryMs = (long)result[2];
                return RedisLease.Rejected(retryMs >= 0 ? TimeSpan.FromMilliseconds(retryMs) : null);
            }

            return _limit.Algorithm == RateLimitAlgorithm.Concurrency
                ? RedisLease.Acquired(() => database.SortedSetRemove(_key, member, CommandFlags.FireAndForget))
                : RedisLease.Acquired(null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RedisLog.LimiterFailed(_logger, ex, _key.ToString(), _options.LimiterFailureMode);
            _metrics.RecordStoreFailure("limiter");
            return _options.LimiterFailureMode == LimiterFailureMode.AllowRequests ? RedisLease.Acquired(null) : RedisLease.Rejected(null);
        }
    }

    private static long Ms(TimeSpan value) => (long)value.TotalMilliseconds;
}

internal sealed class RedisLease : RateLimitLease
{
    private readonly TimeSpan? _retryAfter;
    private Action? _release;

    private RedisLease(bool acquired, TimeSpan? retryAfter, Action? release)
    {
        IsAcquired = acquired;
        _retryAfter = retryAfter;
        _release = release;
    }

    public static RedisLease Acquired(Action? release) => new(true, null, release);

    public static RedisLease Rejected(TimeSpan? retryAfter) => new(false, retryAfter, null);

    public override bool IsAcquired { get; }

    public override IEnumerable<string> MetadataNames => _retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

    public override bool TryGetMetadata(string metadataName, out object? metadata)
    {
        if (_retryAfter is { } retryAfter && metadataName == MetadataName.RetryAfter.Name)
        {
            metadata = retryAfter;
            return true;
        }

        metadata = null;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}
