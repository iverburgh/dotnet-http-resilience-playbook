using HttpResilience.Throttling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HttpResilience;

/// <summary>Registers the app-wide outbound rate-limit rules.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="OutboundThrottlingOptions"/> from <paramref name="section"/> (usually
    /// <c>configuration.GetSection("OutboundThrottling")</c>) and validates it at startup. Rules are read once:
    /// changing them needs a restart.
    /// </summary>
    public static IServiceCollection AddOutboundThrottlingRules(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);
        services.AddHttpResilienceCore();
        services.AddOptions<OutboundThrottlingOptions>().Bind(section);
        return services;
    }

    /// <summary>Configures <see cref="OutboundThrottlingOptions"/> in code and validates it at startup.</summary>
    public static IServiceCollection AddOutboundThrottlingRules(this IServiceCollection services, Action<OutboundThrottlingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddHttpResilienceCore();
        services.AddOptions<OutboundThrottlingOptions>().Configure(configure);
        return services;
    }

    /// <summary>The shared singletons. Safe to call more than once; a store registered earlier wins.</summary>
    internal static IServiceCollection AddHttpResilienceCore(this IServiceCollection services)
    {
        services.AddMetrics();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IThrottleStateStore, InMemoryThrottleStateStore>();
        services.TryAddSingleton<IRateLimiterFactory, LocalRateLimiterFactory>();
        services.TryAddSingleton<ThrottleRuleMatcher>();
        services.TryAddSingleton<HttpResilienceMetrics>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OutboundThrottlingOptions>, OutboundThrottlingOptionsValidator>());
        services.AddOptions<OutboundThrottlingOptions>().ValidateOnStart();
        return services;
    }
}
