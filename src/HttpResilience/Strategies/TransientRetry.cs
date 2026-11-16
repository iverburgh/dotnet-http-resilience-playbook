using System.Net;
using HttpResilience.Throttling;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace HttpResilience.Strategies;

/// <summary>Retries failures that are likely to pass on a second try, but never a request that is unsafe to send twice.</summary>
internal static class TransientRetry
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    private static readonly HashSet<HttpMethod> IdempotentMethods =
    [
        HttpMethod.Get, HttpMethod.Head, HttpMethod.Options, HttpMethod.Trace, HttpMethod.Put, HttpMethod.Delete,
    ];

    public static RetryStrategyOptions<HttpResponseMessage> Create(TransientRetryOptions options, PipelineServices services) => new()
    {
        Name = "transient-retry",
        MaxRetryAttempts = options.MaxRetryAttempts,
        Delay = options.BaseDelay,
        MaxDelay = options.MaxDelay,
        BackoffType = options.BackoffType,
        UseJitter = options.UseJitter,
        ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome, args.Context, options, services)),
        OnRetry = async args =>
        {
            var request = args.Context.GetRequestMessage();
            Log.TransientRetry(services.Logger, request?.Method, request?.RequestUri?.Authority, request?.RequestUri?.AbsolutePath,
                Describe(args.Outcome), args.AttemptNumber + 1, args.RetryDelay.TotalMilliseconds);
            services.Metrics.RecordRetryDelay(RequestState.GetScope(args.Context, services.Matcher), "transient", args.RetryDelay);
            if (options.OnRetry is { } onRetry)
            {
                await onRetry(args).ConfigureAwait(false);
            }
        },
    };

    internal static bool ShouldRetry(Outcome<HttpResponseMessage> outcome, ResilienceContext context, TransientRetryOptions options, PipelineServices services)
    {
        // Throttling has its own retry, which waits for Retry-After instead of backing off.
        if (outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests || outcome.Exception is OutboundThrottleRejectedException)
        {
            return false;
        }

        if (!IsTransient(outcome) && !(options.AdditionalPredicate?.Invoke(outcome) ?? false))
        {
            return false;
        }

        var request = context.GetRequestMessage();
        if (!IsRetrySafe(request, options))
        {
            if (request is not null)
            {
                Log.NotRetriedNonIdempotent(services.Logger, request.Method, request.RequestUri?.Authority, request.RequestUri?.AbsolutePath, outcome.Result?.StatusCode);
            }

            return false;
        }

        return true;
    }

    internal static bool IsTransient(Outcome<HttpResponseMessage> outcome)
        => outcome.Exception is HttpRequestException or TimeoutRejectedException
        || outcome.Result is { StatusCode: >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout };

    internal static bool IsRetrySafe(HttpRequestMessage? request, TransientRetryOptions options)
        => options.RetryNonIdempotentMethods
        || (request is not null
            && (IdempotentMethods.Contains(request.Method)
                || request.Headers.Contains(IdempotencyKeyHeader)
                || (request.Options.TryGetValue(HttpResilienceRequestOptions.RetrySafe, out var safe) && safe)));

    private static string Describe(Outcome<HttpResponseMessage> outcome)
        => outcome.Result is { } response ? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture) : outcome.Exception?.GetType().Name ?? "unknown";
}
