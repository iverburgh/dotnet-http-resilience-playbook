using System.Net;
using HttpResilience.Throttling;
using Polly;

namespace HttpResilience.Strategies;

internal sealed class ThrottleGateStrategyOptions : ResilienceStrategyOptions
{
    public ThrottleGateStrategyOptions() => Name = "throttle-gate";
}

/// <summary>
/// Shares a 429 across every request to the same scope. After a dependency says "slow down", other requests
/// wait out the same Retry-After instead of each collecting their own 429.
/// </summary>
internal sealed class ThrottleGateStrategy(PipelineServices services, ThrottleRetryOptions options) : ResilienceStrategy<HttpResponseMessage>
{
    protected override async ValueTask<Outcome<HttpResponseMessage>> ExecuteCore<TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<HttpResponseMessage>>> callback,
        ResilienceContext context,
        TState state)
    {
        var scope = RequestState.GetScope(context, services.Matcher);
        if (scope is null)
        {
            return await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        DateTimeOffset? blockedUntil = null;
        try
        {
            blockedUntil = await services.Store.GetBlockedUntilAsync(scope.Key, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.StoreFailed(services.Logger, ex, "get", scope.Key);
            services.Metrics.RecordStoreFailure("get");
        }

        var now = services.TimeProvider.GetUtcNow();
        if (blockedUntil is { } until && until > now)
        {
            var remaining = until - now;
            RequestState.RecordThrottled(context, services, scope, ThrottleSources.Cached);
            Log.BlockedByCachedThrottle(services.Logger, context.GetRequestMessage()?.Method, scope.Key, remaining.TotalMilliseconds);
            return Outcome.FromException<HttpResponseMessage>(
                new OutboundThrottleRejectedException(ThrottleRejectionReason.ServerThrottle, scope.Key, remaining));
        }

        var outcome = await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);

        if (outcome.Result is { StatusCode: HttpStatusCode.TooManyRequests } response)
        {
            RequestState.RecordThrottled(context, services, scope, ThrottleSources.Server);
            var answeredAt = services.TimeProvider.GetUtcNow();
            var wait = RetryAfter.Parse(response.Headers.RetryAfter, answeredAt, options.DefaultRetryAfter, options.MaxRetryAfter);
            try
            {
                await services.Store.BlockAsync(scope.Key, answeredAt + wait, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.StoreFailed(services.Logger, ex, "block", scope.Key);
                services.Metrics.RecordStoreFailure("block");
            }
        }

        return outcome;
    }
}
