using HttpResilience.Throttling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HttpResilience.Redis;

/// <summary>Moves the outbound limiters and the 429 gate to Redis, so all instances share them.</summary>
public static class RedisServiceCollectionExtensions
{
    /// <summary>Uses Redis for the limiters and the shared 429 state, configured in code.</summary>
    public static IServiceCollection AddHttpResilienceRedis(this IServiceCollection services, Action<RedisThrottlingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        AddCore(services).Configure(configure);
        return services;
    }

    /// <summary>Uses Redis for the limiters and the shared 429 state, bound from <paramref name="section"/>.</summary>
    public static IServiceCollection AddHttpResilienceRedis(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);
        AddCore(services).Bind(section);
        return services;
    }

    private static OptionsBuilder<RedisThrottlingOptions> AddCore(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpResilienceCore();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RedisThrottlingOptions>, RedisThrottlingOptionsValidator>());
        services.TryAddSingleton<RedisConnection>();
        services.Replace(ServiceDescriptor.Singleton<IThrottleStateStore, RedisThrottleStateStore>());
        services.Replace(ServiceDescriptor.Singleton<IRateLimiterFactory, RedisRateLimiterFactory>());
        return services.AddOptions<RedisThrottlingOptions>().ValidateOnStart();
    }
}
