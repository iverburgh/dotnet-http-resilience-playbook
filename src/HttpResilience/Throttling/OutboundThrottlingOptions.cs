namespace HttpResilience.Throttling;

/// <summary>
/// App-wide outbound rate limits. One limiter per rule is shared by every <see cref="HttpClient"/> in the
/// process (or, with the Redis store, by every instance), because the limit belongs to the dependency, not
/// to a client registration.
/// </summary>
public sealed class OutboundThrottlingOptions
{
    /// <summary>Default configuration section.</summary>
    public const string SectionName = "OutboundThrottling";

    /// <summary>Rules matched on host and path prefix; the longest matching prefix wins.</summary>
    public List<ThrottlingRule> Rules { get; set; } = [];

    /// <summary>
    /// Optional limit for requests no rule matches, applied per host: every host gets its own limiter.
    /// Without it, unmatched requests are not rate limited (the shared 429 gate still applies).
    /// </summary>
    public CatchAllThrottlingRule? CatchAll { get; set; }
}

/// <summary>The algorithm a limiter uses.</summary>
public enum RateLimitAlgorithm
{
    /// <summary>At most <c>PermitLimit</c> requests in any window of <c>Window</c>. Smooth; the default.</summary>
    SlidingWindow,

    /// <summary>At most <c>PermitLimit</c> requests per fixed window. Allows a burst at each window edge.</summary>
    FixedWindow,

    /// <summary>A bucket of <c>PermitLimit</c> tokens, refilled with <c>TokensPerPeriod</c> each <c>ReplenishmentPeriod</c>.</summary>
    TokenBucket,

    /// <summary>At most <c>PermitLimit</c> requests in flight at the same time: a bulkhead.</summary>
    Concurrency,
}

/// <summary>Limit settings shared by a rule and the catch-all.</summary>
public class ThrottlingRuleBase
{
    /// <summary>Which algorithm to use.</summary>
    public RateLimitAlgorithm Algorithm { get; set; } = RateLimitAlgorithm.SlidingWindow;

    /// <summary>Requests per window, bucket size, or requests in flight, depending on <see cref="Algorithm"/>.</summary>
    public int PermitLimit { get; set; }

    /// <summary>Window length for the window algorithms.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Segments of the in-process sliding window; more segments approximate a true rolling window better.</summary>
    public int SegmentsPerWindow { get; set; } = 4;

    /// <summary>Tokens added each period, for <see cref="RateLimitAlgorithm.TokenBucket"/>.</summary>
    public int TokensPerPeriod { get; set; } = 1;

    /// <summary>Refill period, for <see cref="RateLimitAlgorithm.TokenBucket"/>.</summary>
    public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Requests that may wait in-process for a permit instead of being rejected. Use it with
    /// <see cref="RateLimitAlgorithm.Concurrency"/>, which gives no retry hint. Ignored by the Redis limiters.
    /// </summary>
    public int QueueLimit { get; set; }
}

/// <summary>A limit for one dependency, or one part of it.</summary>
public sealed class ThrottlingRule : ThrottlingRuleBase
{
    /// <summary>Name used in logs and metrics.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Host and optional path prefix the limit applies to, for example <c>https://api.partner.example/orders</c>.
    /// The scheme is ignored.
    /// </summary>
    public Uri? BaseAddress { get; set; }
}

/// <summary>The per-host limit for requests that no rule matches.</summary>
public sealed class CatchAllThrottlingRule : ThrottlingRuleBase;
