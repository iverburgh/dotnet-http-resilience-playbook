using Polly;
using Polly.Timeout;

namespace HttpResilience.Strategies;

/// <summary>
/// Builds the pipeline, outermost first:
/// total timeout → transient retry → throttle-exhausted marker → throttle retry → 429 gate → rate limiter → attempt timeout.
/// </summary>
internal static class PipelineComposer
{
    public static void AddFull(ResiliencePipelineBuilder<HttpResponseMessage> pipeline, HttpResilienceOptions options, PipelineServices services)
    {
        pipeline.AddTimeout(new TimeoutStrategyOptions { Name = "total-timeout", Timeout = options.GetEffectiveTotalTimeout() });

        // Polly needs at least one retry; zero means "leave the strategy out".
        if (options.TransientRetry.MaxRetryAttempts > 0)
        {
            pipeline.AddRetry(TransientRetry.Create(options.TransientRetry, services));
        }

        AddThrottling(pipeline, options.ThrottleRetry, services);

        pipeline.AddTimeout(new TimeoutStrategyOptions { Name = "attempt-timeout", Timeout = options.AttemptTimeout });
    }

    public static void AddThrottling(ResiliencePipelineBuilder<HttpResponseMessage> pipeline, ThrottleRetryOptions options, PipelineServices services)
    {
        pipeline.AddStrategy(_ => new TerminalRejectionStrategy(services), new TerminalRejectionStrategyOptions());

        if (options.MaxRetryAttempts > 0)
        {
            pipeline.AddRetry(ThrottleRetry.Create(options, services));
        }

        pipeline.AddStrategy(_ => new ThrottleGateStrategy(services, options), new ThrottleGateStrategyOptions());
        pipeline.AddStrategy(_ => new RateLimitStrategy(services), new RateLimitStrategyOptions());
    }
}
