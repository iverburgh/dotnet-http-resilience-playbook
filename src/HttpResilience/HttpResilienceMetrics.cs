using System.Diagnostics;
using System.Diagnostics.Metrics;
using HttpResilience.Throttling;
using Microsoft.Extensions.DependencyInjection;

namespace HttpResilience;

/// <summary>Names to subscribe to in OpenTelemetry or a log filter.</summary>
public static class HttpResilienceTelemetry
{
    /// <summary>The meter: <c>metrics.AddMeter(HttpResilienceTelemetry.MeterName)</c>.</summary>
    public const string MeterName = "HttpResilience";

    /// <summary>The logger category of every pipeline log message.</summary>
    public const string LogCategory = "HttpResilience.Pipeline";
}

/// <summary>
/// The pipeline's own metrics. Polly's <c>resilience.polly.strategy.events</c> says that a retry happened;
/// these say why, for which dependency, and whether the caller was refused in the end.
/// </summary>
internal sealed class HttpResilienceMetrics : IDisposable
{
    public const string ThrottledAttempts = "resilience.http.throttle.attempts";
    public const string ThrottledRequests = "resilience.http.throttle.requests";
    public const string RejectedRequests = "resilience.http.rejected_requests";
    public const string RetryDelay = "resilience.http.retry.delay";
    public const string AvailablePermits = "resilience.http.limiter.available_permits";
    public const string StoreFailures = "resilience.http.store.failures";

    private readonly Meter _meter;
    private readonly Counter<long> _throttledAttempts;
    private readonly Counter<long> _throttledRequests;
    private readonly Counter<long> _rejectedRequests;
    private readonly Histogram<double> _retryDelay;
    private readonly Counter<long> _storeFailures;

    public HttpResilienceMetrics(IMeterFactory meterFactory, IServiceProvider services)
    {
        _meter = meterFactory.Create(HttpResilienceTelemetry.MeterName);
        _throttledAttempts = _meter.CreateCounter<long>(ThrottledAttempts, "{attempt}",
            "Attempts refused by the limiter or the 429 gate, or answered with 429 by the dependency.");
        _throttledRequests = _meter.CreateCounter<long>(ThrottledRequests, "{request}",
            "Logical requests throttled at least once, counted once per source.");
        _rejectedRequests = _meter.CreateCounter<long>(RejectedRequests, "{request}",
            "Logical requests that reached the caller as a 429 or a rejection after the throttle retries ran out.");
        _retryDelay = _meter.CreateHistogram<double>(RetryDelay, "s",
            "Wait before a retry.");
        _storeFailures = _meter.CreateCounter<long>(StoreFailures, "{failure}",
            "Calls to the throttle state store or limiter backend that failed; the pipeline let the request through.");

        // Resolved on observation, not in the constructor: a store may depend on these metrics.
        _meter.CreateObservableGauge(AvailablePermits, () => ObservePermits(services), "{permit}",
            "Permits the outbound limiter has left.");
    }

    public void RecordThrottledAttempt(ThrottleScope scope, string source)
        => _throttledAttempts.Add(1, Tags(scope, "throttle.source", source));

    public void RecordThrottledRequest(ThrottleScope scope, string source)
        => _throttledRequests.Add(1, Tags(scope, "throttle.source", source));

    public void RecordRejectedRequest(ThrottleScope? scope, string reason)
        => _rejectedRequests.Add(1, Tags(scope, "rejection.reason", reason));

    public void RecordRetryDelay(ThrottleScope? scope, string kind, TimeSpan delay)
        => _retryDelay.Record(delay.TotalSeconds, Tags(scope, "retry.kind", kind));

    public void RecordStoreFailure(string operation)
        => _storeFailures.Add(1, new KeyValuePair<string, object?>("store.operation", operation));

    public void Dispose() => _meter.Dispose();

    private static IEnumerable<Measurement<long>> ObservePermits(IServiceProvider services)
    {
        var factory = services.GetService<IRateLimiterFactory>();
        if (factory is null)
        {
            yield break;
        }

        foreach (var (scope, limiter) in factory.ActiveLimiters)
        {
            if (limiter.GetStatistics() is { } statistics)
            {
                yield return new Measurement<long>(statistics.CurrentAvailablePermits, Tags(scope, null, null));
            }
        }
    }

    private static TagList Tags(ThrottleScope? scope, string? key, string? value)
    {
        var tags = new TagList();
        if (scope is not null)
        {
            tags.Add("server.address", scope.Host);
            tags.Add("url.path_prefix", scope.PathPrefix);
            if (scope.RuleName.Length > 0)
            {
                tags.Add("throttle.rule", scope.RuleName);
            }
        }

        if (key is not null)
        {
            tags.Add(key, value);
        }

        return tags;
    }
}

/// <summary>Values of the <c>throttle.source</c> and <c>rejection.reason</c> tags.</summary>
internal static class ThrottleSources
{
    public const string Limiter = "limiter";
    public const string Server = "server";
    public const string Cached = "cached";

    public const string RateLimitReason = "rate_limit";
    public const string Server429Reason = "server_429";
}
