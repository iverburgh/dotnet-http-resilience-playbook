using Azure.Core;

namespace HttpResilience.Redis;

/// <summary>What a Redis limiter does when Redis cannot be reached.</summary>
public enum LimiterFailureMode
{
    /// <summary>Let the request through: throttling is off until Redis is back. The default.</summary>
    AllowRequests,

    /// <summary>Refuse the request: the dependency is protected, your callers are not.</summary>
    RejectRequests,
}

/// <summary>Settings for <see cref="RedisServiceCollectionExtensions.AddHttpResilienceRedis(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{RedisThrottlingOptions})"/>.</summary>
public sealed class RedisThrottlingOptions
{
    /// <summary>Default configuration section.</summary>
    public const string SectionName = "HttpResilience:Redis";

    /// <summary>
    /// A StackExchange.Redis connection string, for a local Redis or one with an access key. Use
    /// <see cref="Endpoint"/> instead to sign in with Microsoft Entra ID.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Host and port of an Azure cache, for example <c>my-cache.redis.cache.windows.net:6380</c>. Signs in with
    /// <see cref="Credential"/>, or <c>DefaultAzureCredential</c> when that is empty.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>Credential for <see cref="Endpoint"/>; for example a <c>ManagedIdentityCredential</c>.</summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>Prefix of every key, so several apps can share one cache.</summary>
    public string KeyPrefix { get; set; } = "http-resilience";

    /// <summary>What the limiters do when Redis cannot be reached.</summary>
    public LimiterFailureMode LimiterFailureMode { get; set; } = LimiterFailureMode.AllowRequests;

    /// <summary>
    /// How long a concurrency permit is held at most when the instance holding it dies before releasing it.
    /// Longer than your longest request.
    /// </summary>
    public TimeSpan ConcurrencyLeaseTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
