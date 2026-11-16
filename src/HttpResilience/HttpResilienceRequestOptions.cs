namespace HttpResilience;

/// <summary>Per-request switches and signals, carried in <see cref="HttpRequestMessage.Options"/>.</summary>
public static class HttpResilienceRequestOptions
{
    /// <summary>
    /// Set to <see langword="true"/> on a request that is safe to send twice although its method is not
    /// idempotent, for example a POST that only searches.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> RetrySafe = new("HttpResilience.RetrySafe");

    /// <summary>
    /// Set by the pipeline when it gave up on throttling: the 429 or the rejection that reaches the caller is
    /// final. An outer retry of your own should not try again soon.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> ThrottleExhausted = new("HttpResilience.ThrottleExhausted");

    /// <summary>Marks the request as safe to retry; see <see cref="RetrySafe"/>.</summary>
    public static HttpRequestMessage MarkRetrySafe(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(RetrySafe, true);
        return request;
    }

    /// <summary>Whether the pipeline gave up on throttling for this request; see <see cref="ThrottleExhausted"/>.</summary>
    public static bool IsThrottleExhausted(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Options.TryGetValue(ThrottleExhausted, out var exhausted) && exhausted;
    }
}
