using System.Net;
using HttpResilience.Throttling;
using Polly;
using Polly.Retry;

namespace HttpResilience.Strategies;

/// <summary>Retries a 429 or a local rejection after the wait the server or the limiter named.</summary>
internal static class ThrottleRetry
{
    public static RetryStrategyOptions<HttpResponseMessage> Create(ThrottleRetryOptions options, PipelineServices services) => new()
    {
        Name = "throttle-retry",
        MaxRetryAttempts = options.MaxRetryAttempts,
        BackoffType = DelayBackoffType.Constant,
        Delay = TimeSpan.Zero,
        UseJitter = false,
        ShouldHandle = args => ValueTask.FromResult(IsThrottled(args.Outcome)),
        DelayGenerator = args => ValueTask.FromResult<TimeSpan?>(
            ComputeDelay(args.Outcome, options, services.TimeProvider.GetUtcNow(), Random.Shared.NextDouble())),
        OnRetry = args =>
        {
            var scope = RequestState.GetScope(args.Context, services.Matcher);
            Log.ThrottleRetry(services.Logger, scope?.Host ?? string.Empty, scope?.PathPrefix ?? string.Empty,
                SourceOf(args.Outcome), args.AttemptNumber + 1, args.RetryDelay.TotalMilliseconds);
            services.Metrics.RecordRetryDelay(scope, "throttle", args.RetryDelay);
            return ValueTask.CompletedTask;
        },
    };

    internal static bool IsThrottled(Outcome<HttpResponseMessage> outcome)
        => outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests || outcome.Exception is OutboundThrottleRejectedException;

    internal static TimeSpan ComputeDelay(Outcome<HttpResponseMessage> outcome, ThrottleRetryOptions options, DateTimeOffset now, double random)
    {
        var wait = outcome.Exception is OutboundThrottleRejectedException rejected
            ? RetryAfter.Clamp(rejected.RetryAfter ?? options.DefaultRetryAfter, options.MaxRetryAfter)
            : RetryAfter.Parse(outcome.Result?.Headers.RetryAfter, now, options.DefaultRetryAfter, options.MaxRetryAfter);
        return RetryAfter.AddJitter(wait, random);
    }

    internal static string SourceOf(Outcome<HttpResponseMessage> outcome) => outcome.Exception switch
    {
        OutboundThrottleRejectedException { Reason: ThrottleRejectionReason.RateLimit } => ThrottleSources.Limiter,
        OutboundThrottleRejectedException => ThrottleSources.Cached,
        _ => ThrottleSources.Server,
    };
}
