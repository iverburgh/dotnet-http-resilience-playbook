using HttpResilience.Strategies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace HttpResilience;

/// <summary>Adds the resilience pipeline to a named or typed <see cref="HttpClient"/>.</summary>
public static class HttpClientBuilderExtensions
{
    /// <summary>Name of the full pipeline.</summary>
    public const string PipelineName = "http-resilience";

    /// <summary>Name of the throttling-only pipeline.</summary>
    public const string ThrottlingPipelineName = "outbound-throttling";

    /// <summary>
    /// Adds timeouts, transient retries, throttle retries, the shared 429 gate and the outbound rate limiter.
    /// Sets <see cref="HttpClient.Timeout"/> to infinite: the pipeline owns time.
    /// </summary>
    /// <remarks>
    /// The handler sits where you call this among other <c>AddHttpMessageHandler</c> calls; call it first to
    /// make it the outermost handler, so a retry runs your other handlers (for example a token handler) again.
    /// Options are read from <c>IOptions&lt;HttpResilienceOptions&gt;</c> named after the client, then passed to
    /// <paramref name="configure"/>. The pipeline is rebuilt when the named options change.
    /// </remarks>
    public static IHttpClientBuilder AddHttpResilience(
        this IHttpClientBuilder builder,
        Action<HttpResilienceOptions, ResilienceHandlerContext>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHttpResilienceCore();

        // HttpClient's own 100-second timeout would cut through retries and throttle waits.
        builder.ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

        var clientName = builder.Name;
        builder.AddResilienceHandler(PipelineName, (pipeline, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptionsFactory<HttpResilienceOptions>>().Create(clientName);
            configure?.Invoke(options, context);
            OptionsValidation.ThrowIfInvalid<HttpResilienceOptions>(clientName, OptionsValidation.Validate(options));
            context.EnableReloads<HttpResilienceOptions>(clientName);

            PipelineComposer.AddFull(pipeline, options, PipelineServices.From(context.ServiceProvider));
        });

        return builder;
    }

    /// <summary>
    /// Adds only the throttling part: throttle retries, the shared 429 gate and the outbound rate limiter.
    /// For a client that keeps its own retry and timeout handling. Leaves <see cref="HttpClient.Timeout"/> alone,
    /// so raise it above <see cref="ThrottleRetryOptions.MaxRetryAfter"/> times the number of retries, or the
    /// client gives up while the pipeline is still waiting.
    /// </summary>
    public static IHttpClientBuilder AddOutboundThrottling(
        this IHttpClientBuilder builder,
        Action<ThrottleRetryOptions, ResilienceHandlerContext>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHttpResilienceCore();

        var clientName = builder.Name;
        builder.AddResilienceHandler(ThrottlingPipelineName, (pipeline, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptionsFactory<ThrottleRetryOptions>>().Create(clientName);
            configure?.Invoke(options, context);
            OptionsValidation.ThrowIfInvalid<ThrottleRetryOptions>(clientName, OptionsValidation.Validate(options));
            context.EnableReloads<ThrottleRetryOptions>(clientName);

            PipelineComposer.AddThrottling(pipeline, options, PipelineServices.From(context.ServiceProvider));
        });

        return builder;
    }
}
