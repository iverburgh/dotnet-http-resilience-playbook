using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace HttpResilience.Throttling;

/// <summary>Creates, and keeps, one limiter per throttle scope.</summary>
public interface IRateLimiterFactory
{
    /// <summary>The limiter for <paramref name="scope"/>; the same instance on every call.</summary>
    RateLimiter GetLimiter(ThrottleScope scope);

    /// <summary>Limiters created so far, for the available-permits gauge.</summary>
    IReadOnlyCollection<KeyValuePair<ThrottleScope, RateLimiter>> ActiveLimiters { get; }
}

/// <summary>In-process limiters from <c>System.Threading.RateLimiting</c>; the default.</summary>
internal sealed class LocalRateLimiterFactory : IRateLimiterFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, (ThrottleScope Scope, Lazy<RateLimiter> Limiter)> _limiters = new(StringComparer.Ordinal);

    public IReadOnlyCollection<KeyValuePair<ThrottleScope, RateLimiter>> ActiveLimiters
        => _limiters.Values.Where(v => v.Limiter.IsValueCreated)
            .Select(v => KeyValuePair.Create(v.Scope, v.Limiter.Value))
            .ToArray();

    public RateLimiter GetLimiter(ThrottleScope scope)
    {
        var limit = scope.Limit ?? throw new ArgumentException("The scope has no limit.", nameof(scope));
        return _limiters.GetOrAdd(scope.Key, _ => (scope, new Lazy<RateLimiter>(() => Create(limit)))).Limiter.Value;
    }

    internal static RateLimiter Create(ThrottlingRuleBase limit) => limit.Algorithm switch
    {
        RateLimitAlgorithm.SlidingWindow => new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = limit.PermitLimit,
            Window = limit.Window,
            SegmentsPerWindow = limit.SegmentsPerWindow,
            QueueLimit = limit.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        }),
        RateLimitAlgorithm.FixedWindow => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit.PermitLimit,
            Window = limit.Window,
            QueueLimit = limit.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        }),
        RateLimitAlgorithm.TokenBucket => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = limit.PermitLimit,
            TokensPerPeriod = limit.TokensPerPeriod,
            ReplenishmentPeriod = limit.ReplenishmentPeriod,
            QueueLimit = limit.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        }),
        RateLimitAlgorithm.Concurrency => new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = limit.PermitLimit,
            QueueLimit = limit.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(limit), limit.Algorithm, "Unknown rate-limit algorithm."),
    };

    public void Dispose()
    {
        foreach (var (_, limiter) in _limiters.Values)
        {
            if (limiter.IsValueCreated)
            {
                limiter.Value.Dispose();
            }
        }
    }
}
