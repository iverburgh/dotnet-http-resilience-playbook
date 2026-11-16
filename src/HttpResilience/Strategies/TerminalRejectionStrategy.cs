using System.Net;
using HttpResilience.Throttling;
using Polly;

namespace HttpResilience.Strategies;

internal sealed class TerminalRejectionStrategyOptions : ResilienceStrategyOptions
{
    public TerminalRejectionStrategyOptions() => Name = "throttle-exhausted";
}

/// <summary>
/// Sits just outside the throttle retry. Whatever 429 or rejection still comes out is final: it marks the
/// request, counts it once, and lets it through to the caller.
/// </summary>
internal sealed class TerminalRejectionStrategy(PipelineServices services) : ResilienceStrategy<HttpResponseMessage>
{
    protected override async ValueTask<Outcome<HttpResponseMessage>> ExecuteCore<TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<HttpResponseMessage>>> callback,
        ResilienceContext context,
        TState state)
    {
        var outcome = await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);

        var reason = outcome switch
        {
            { Result.StatusCode: HttpStatusCode.TooManyRequests } => ThrottleSources.Server429Reason,
            { Exception: OutboundThrottleRejectedException { Reason: ThrottleRejectionReason.RateLimit } } => ThrottleSources.RateLimitReason,
            { Exception: OutboundThrottleRejectedException } => ThrottleSources.Server429Reason,
            _ => null,
        };

        if (reason is not null)
        {
            context.GetRequestMessage()?.Options.Set(HttpResilienceRequestOptions.ThrottleExhausted, true);
            var scope = RequestState.GetScope(context, services.Matcher);
            services.Metrics.RecordRejectedRequest(scope, reason);
            Log.ThrottleExhausted(services.Logger, scope?.Key ?? string.Empty, reason);
        }

        return outcome;
    }
}
