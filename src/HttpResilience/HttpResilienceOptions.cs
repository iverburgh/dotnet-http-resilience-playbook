using Polly;
using Polly.Retry;

namespace HttpResilience;

/// <summary>
/// Settings for one <see cref="HttpClient"/> registered with
/// <see cref="HttpClientBuilderExtensions.AddHttpResilience"/>.
/// </summary>
/// <remarks>
/// Bind it per client name, for example
/// <c>services.Configure&lt;HttpResilienceOptions&gt;("partner", configuration.GetSection("Partner:Resilience"))</c>,
/// then adjust it in code in the callback of <c>AddHttpResilience</c>.
/// </remarks>
public sealed class HttpResilienceOptions
{
    /// <summary>How long one attempt may take. Innermost strategy, so every attempt gets the full time.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the caller waits at most, retries and throttle waits included. Outermost strategy.
    /// When left empty it is computed from the other settings; see <see cref="GetEffectiveTotalTimeout"/>.
    /// </summary>
    public TimeSpan? TotalTimeout { get; set; }

    /// <summary>Retries for transient failures: network errors, timeouts, 5xx and 408.</summary>
    public TransientRetryOptions TransientRetry { get; set; } = new();

    /// <summary>Retries for throttling: 429 responses and local rate-limit rejections.</summary>
    public ThrottleRetryOptions ThrottleRetry { get; set; } = new();

    /// <summary>
    /// The explicit <see cref="TotalTimeout"/>, or a budget in which every transient attempt may time out
    /// and back off, and the throttle may wait its maximum for each of its retries:
    /// <c>(T + 1) × AttemptTimeout + T × MaxDelay + R × MaxRetryAfter</c>, where T is the number of transient
    /// retries and R the number of throttle retries.
    /// </summary>
    public TimeSpan GetEffectiveTotalTimeout()
    {
        if (TotalTimeout is { } explicitTimeout)
        {
            return explicitTimeout;
        }

        var transientRetries = Math.Max(0, TransientRetry.MaxRetryAttempts);
        var throttleRetries = Math.Max(0, ThrottleRetry.MaxRetryAttempts);
        return ((transientRetries + 1) * AttemptTimeout)
            + (transientRetries * TransientRetry.MaxDelay)
            + (throttleRetries * ThrottleRetry.MaxRetryAfter);
    }
}

/// <summary>Retries for failures that are likely to succeed when tried again.</summary>
public sealed class TransientRetryOptions
{
    /// <summary>Retries after the first attempt. 0 disables transient retries.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Delay before the first retry; grows with <see cref="BackoffType"/>.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound for a single delay.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How the delay grows between retries.</summary>
    public DelayBackoffType BackoffType { get; set; } = DelayBackoffType.Exponential;

    /// <summary>Spreads retries of callers that failed at the same moment.</summary>
    public bool UseJitter { get; set; } = true;

    /// <summary>
    /// Also retry requests whose method is not idempotent (POST, PATCH) when they carry no
    /// <c>Idempotency-Key</c> header and are not marked with <see cref="HttpResilienceRequestOptions.RetrySafe"/>.
    /// Off by default: a timed-out POST may have been processed, and sending it again may do the work twice.
    /// </summary>
    public bool RetryNonIdempotentMethods { get; set; }

    /// <summary>
    /// Extra outcomes to retry, on top of the built-in transient set. Use it for a dependency that is known
    /// to return, say, a transient 404 or 409. Never sees 429s or throttle rejections: those belong to the
    /// throttle retry.
    /// </summary>
    public Func<Outcome<HttpResponseMessage>, bool>? AdditionalPredicate { get; set; }

    /// <summary>Called before each transient retry, for example to refresh a cached token after a 401.</summary>
    public Func<OnRetryArguments<HttpResponseMessage>, ValueTask>? OnRetry { get; set; }
}

/// <summary>Retries for throttling, driven by <c>Retry-After</c> rather than by backoff.</summary>
public sealed class ThrottleRetryOptions
{
    /// <summary>Retries after a 429 or a rate-limit rejection. 0 returns the 429 to the caller at once.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Wait used when a 429 or a rejection carries no usable <c>Retry-After</c>.</summary>
    public TimeSpan DefaultRetryAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Cap on any single wait, so a server cannot park the caller for an hour.</summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(120);
}
