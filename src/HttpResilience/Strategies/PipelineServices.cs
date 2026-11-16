using HttpResilience.Throttling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace HttpResilience.Strategies;

/// <summary>The singletons every strategy needs, resolved once per pipeline build.</summary>
internal sealed record PipelineServices(
    ThrottleRuleMatcher Matcher,
    IThrottleStateStore Store,
    IRateLimiterFactory LimiterFactory,
    HttpResilienceMetrics Metrics,
    ILogger Logger,
    TimeProvider TimeProvider)
{
    public static PipelineServices From(IServiceProvider services) => new(
        services.GetRequiredService<ThrottleRuleMatcher>(),
        services.GetRequiredService<IThrottleStateStore>(),
        services.GetRequiredService<IRateLimiterFactory>(),
        services.GetRequiredService<HttpResilienceMetrics>(),
        (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger(HttpResilienceTelemetry.LogCategory),
        services.GetRequiredService<TimeProvider>());
}

/// <summary>State that lives for one logical request, across all its attempts.</summary>
internal static class RequestState
{
    private static readonly ResiliencePropertyKey<ThrottleScope> ScopeKey = new("HttpResilience.ThrottleScope");
    private static readonly ResiliencePropertyKey<HashSet<string>> ThrottledSourcesKey = new("HttpResilience.ThrottledSources");

    /// <summary>The throttle scope of the request, resolved on first use.</summary>
    public static ThrottleScope? GetScope(ResilienceContext context, ThrottleRuleMatcher matcher)
    {
        if (context.Properties.TryGetValue(ScopeKey, out var cached))
        {
            return cached;
        }

        var scope = matcher.Resolve(context.GetRequestMessage()?.RequestUri);
        if (scope is not null)
        {
            context.Properties.Set(ScopeKey, scope);
        }

        return scope;
    }

    /// <summary>Records a throttle event; counts the logical request once per source.</summary>
    public static void RecordThrottled(ResilienceContext context, PipelineServices services, ThrottleScope scope, string source)
    {
        services.Metrics.RecordThrottledAttempt(scope, source);

        if (!context.Properties.TryGetValue(ThrottledSourcesKey, out var sources))
        {
            sources = new HashSet<string>(StringComparer.Ordinal);
            context.Properties.Set(ThrottledSourcesKey, sources);
        }

        if (sources.Add(source))
        {
            services.Metrics.RecordThrottledRequest(scope, source);
        }
    }
}
