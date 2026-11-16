using System.Threading.RateLimiting;
using HttpResilience.Throttling;
using Polly;

namespace HttpResilience.Strategies;

internal sealed class RateLimitStrategyOptions : ResilienceStrategyOptions
{
    public RateLimitStrategyOptions() => Name = "outbound-rate-limit";
}

/// <summary>
/// Takes a permit from the scope's limiter before each attempt. No permit: the attempt is refused locally, and
/// the throttle retry waits for the limiter's retry hint. A broken limiter lets requests through.
/// </summary>
internal sealed class RateLimitStrategy(PipelineServices services) : ResilienceStrategy<HttpResponseMessage>
{
    protected override async ValueTask<Outcome<HttpResponseMessage>> ExecuteCore<TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<HttpResponseMessage>>> callback,
        ResilienceContext context,
        TState state)
    {
        var scope = RequestState.GetScope(context, services.Matcher);
        if (scope?.Limit is null)
        {
            return await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        RateLimitLease lease;
        try
        {
            var limiter = services.LimiterFactory.GetLimiter(scope);
            lease = await limiter.AcquireAsync(1, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LimiterUnavailable(services.Logger, ex, scope.Key);
            services.Metrics.RecordStoreFailure("acquire");
            return await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        using (lease)
        {
            if (!lease.IsAcquired)
            {
                TimeSpan? retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var hint) ? hint : null;
                RequestState.RecordThrottled(context, services, scope, ThrottleSources.Limiter);
                return Outcome.FromException<HttpResponseMessage>(
                    new OutboundThrottleRejectedException(ThrottleRejectionReason.RateLimit, scope.Key, retryAfter));
            }

            return await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        }
    }
}
