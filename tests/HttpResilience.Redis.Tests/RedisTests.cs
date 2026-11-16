using System.Diagnostics.Metrics;
using System.Net;
using System.Threading.RateLimiting;
using HttpResilience.Throttling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace HttpResilience.Redis.Tests;

internal static class Redis
{
    public static ServiceProvider Provider(string prefix, Action<RedisThrottlingOptions>? configure = null, string? connectionString = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpResilienceRedis(o =>
        {
            o.ConnectionString = connectionString ?? RedisServer.ConnectionString;
            o.KeyPrefix = prefix;
            configure?.Invoke(o);
        });
        return services.BuildServiceProvider();
    }

    public static ThrottleScope Scope(ThrottlingRuleBase limit, string key = "api.partner.example/")
        => new(key, "api.partner.example", "/", "partner", limit);

    public static RateLimiter Limiter(this IServiceProvider provider, ThrottleScope scope)
        => provider.GetRequiredService<IRateLimiterFactory>().GetLimiter(scope);

    public static TimeSpan? RetryAfter(this RateLimitLease lease)
        => lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : null;

    /// <summary>Acquires <paramref name="count"/> leases in a row and reports which ones got a permit.</summary>
    public static async Task<bool[]> AcquireInTurnAsync(this RateLimiter limiter, int count)
    {
        var results = new bool[count];
        for (var i = 0; i < count; i++)
        {
            using var lease = await limiter.AcquireAsync();
            results[i] = lease.IsAcquired;
        }

        return results;
    }
}

public class RedisRateLimiterTests
{
    [TestCase(RateLimitAlgorithm.FixedWindow)]
    [TestCase(RateLimitAlgorithm.SlidingWindow)]
    [TestCase(RateLimitAlgorithm.TokenBucket)]
    public async Task Window_algorithms_allow_the_limit_and_then_tell_when_to_come_back(RateLimitAlgorithm algorithm)
    {
        using var provider = Redis.Provider(RedisServer.UniquePrefix());
        var limiter = provider.Limiter(Redis.Scope(new ThrottlingRule
        {
            Algorithm = algorithm, PermitLimit = 2, Window = TimeSpan.FromSeconds(10),
            TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromSeconds(10),
        }));

        Assert.That(await limiter.AcquireInTurnAsync(2), Is.EqualTo(new[] { true, true }));
        using var rejected = await limiter.AcquireAsync();

        Assert.That(rejected.IsAcquired, Is.False);
        Assert.That(rejected.RetryAfter(), Is.GreaterThan(TimeSpan.Zero).And.LessThanOrEqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(limiter.GetStatistics()!.CurrentAvailablePermits, Is.Zero);
    }

    [TestCase(RateLimitAlgorithm.FixedWindow)]
    [TestCase(RateLimitAlgorithm.SlidingWindow)]
    [TestCase(RateLimitAlgorithm.TokenBucket)]
    public async Task Permits_come_back_after_the_window(RateLimitAlgorithm algorithm)
    {
        using var provider = Redis.Provider(RedisServer.UniquePrefix());
        var limiter = provider.Limiter(Redis.Scope(new ThrottlingRule
        {
            Algorithm = algorithm, PermitLimit = 1, Window = TimeSpan.FromMilliseconds(300),
            TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromMilliseconds(300),
        }));

        Assert.That(await limiter.AcquireInTurnAsync(2), Is.EqualTo(new[] { true, false }));
        await Task.Delay(450);

        Assert.That(await limiter.AcquireInTurnAsync(1), Is.EqualTo(new[] { true }));
    }

    [Test]
    public async Task Concurrency_permit_returns_when_the_lease_is_disposed()
    {
        using var provider = Redis.Provider(RedisServer.UniquePrefix());
        var limiter = provider.Limiter(Redis.Scope(new ThrottlingRule { Algorithm = RateLimitAlgorithm.Concurrency, PermitLimit = 1 }));

        var held = await limiter.AcquireAsync();
        using (var second = await limiter.AcquireAsync())
        {
            Assert.That(held.IsAcquired, Is.True);
            Assert.That(second.IsAcquired, Is.False);
            Assert.That(second.RetryAfter(), Is.Null, "a concurrency limit cannot predict when a permit frees up");
        }

        held.Dispose();

        // The release is fire-and-forget, so give it a moment.
        var acquired = false;
        for (var i = 0; i < 40 && !acquired; i++)
        {
            await Task.Delay(50);
            using var lease = await limiter.AcquireAsync();
            acquired = lease.IsAcquired;
        }

        Assert.That(acquired, Is.True);
    }

    [Test]
    public async Task Concurrency_permit_of_a_dead_holder_expires()
    {
        using var provider = Redis.Provider(RedisServer.UniquePrefix(), o => o.ConcurrencyLeaseTimeout = TimeSpan.FromMilliseconds(300));
        var limiter = provider.Limiter(Redis.Scope(new ThrottlingRule { Algorithm = RateLimitAlgorithm.Concurrency, PermitLimit = 1 }));

        // Never disposed: the holder "crashed".
        _ = await limiter.AcquireAsync();
        Assert.That(await limiter.AcquireInTurnAsync(1), Is.EqualTo(new[] { false }));
        await Task.Delay(450);

        Assert.That(await limiter.AcquireInTurnAsync(1), Is.EqualTo(new[] { true }));
    }

    [Test]
    public async Task Two_instances_share_one_budget()
    {
        var prefix = RedisServer.UniquePrefix();
        var rule = new ThrottlingRule { Algorithm = RateLimitAlgorithm.FixedWindow, PermitLimit = 3, Window = TimeSpan.FromSeconds(30) };
        using var first = Redis.Provider(prefix);
        using var second = Redis.Provider(prefix);

        Assert.That(await first.Limiter(Redis.Scope(rule)).AcquireInTurnAsync(2), Is.EqualTo(new[] { true, true }));
        Assert.That(await second.Limiter(Redis.Scope(rule)).AcquireInTurnAsync(2), Is.EqualTo(new[] { true, false }));
    }

    [Test]
    public async Task Different_scopes_have_different_budgets()
    {
        using var provider = Redis.Provider(RedisServer.UniquePrefix());
        var rule = new ThrottlingRule { Algorithm = RateLimitAlgorithm.FixedWindow, PermitLimit = 1, Window = TimeSpan.FromSeconds(30) };

        Assert.That(await provider.Limiter(Redis.Scope(rule, "a.example/")).AcquireInTurnAsync(1), Is.EqualTo(new[] { true }));
        Assert.That(await provider.Limiter(Redis.Scope(rule, "b.example/")).AcquireInTurnAsync(1), Is.EqualTo(new[] { true }));
    }

    [Test]
    public void Synchronous_acquire_always_refuses()
    {
        using var provider = Redis.Provider(RedisServer.UniquePrefix());
        var limiter = provider.Limiter(Redis.Scope(new ThrottlingRule { PermitLimit = 100 }));

        using var lease = limiter.AttemptAcquire();

        Assert.That(lease.IsAcquired, Is.False);
    }
}

public class RedisThrottleStateStoreTests
{
    [Test]
    public async Task A_block_written_by_one_instance_is_seen_by_another()
    {
        var prefix = RedisServer.UniquePrefix();
        using var writer = Redis.Provider(prefix);
        using var reader = Redis.Provider(prefix);
        var until = DateTimeOffset.UtcNow.AddSeconds(30);

        await writer.GetRequiredService<IThrottleStateStore>().BlockAsync("a/", until, CancellationToken.None);

        var seen = await reader.GetRequiredService<IThrottleStateStore>().GetBlockedUntilAsync("a/", CancellationToken.None);
        Assert.That(seen, Is.EqualTo(until).Within(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public async Task A_block_is_only_ever_extended()
    {
        using var provider = Redis.Provider(RedisServer.UniquePrefix());
        var store = provider.GetRequiredService<IThrottleStateStore>();
        var later = DateTimeOffset.UtcNow.AddSeconds(60);

        await store.BlockAsync("a/", later, CancellationToken.None);
        await store.BlockAsync("a/", DateTimeOffset.UtcNow.AddSeconds(5), CancellationToken.None);

        Assert.That(await store.GetBlockedUntilAsync("a/", CancellationToken.None), Is.EqualTo(later).Within(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public async Task A_block_expires()
    {
        using var provider = Redis.Provider(RedisServer.UniquePrefix());
        var store = provider.GetRequiredService<IThrottleStateStore>();

        await store.BlockAsync("a/", DateTimeOffset.UtcNow.AddMilliseconds(300), CancellationToken.None);
        Assert.That(await store.GetBlockedUntilAsync("a/", CancellationToken.None), Is.Not.Null);
        await Task.Delay(450);

        Assert.That(await store.GetBlockedUntilAsync("a/", CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task A_block_in_the_past_is_not_written()
    {
        using var provider = Redis.Provider(RedisServer.UniquePrefix());
        var store = provider.GetRequiredService<IThrottleStateStore>();

        await store.BlockAsync("a/", DateTimeOffset.UtcNow.AddSeconds(-1), CancellationToken.None);

        Assert.That(await store.GetBlockedUntilAsync("a/", CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Keys_carry_the_prefix()
    {
        var prefix = RedisServer.UniquePrefix();
        using var provider = Redis.Provider(prefix);
        await provider.GetRequiredService<IThrottleStateStore>().BlockAsync("a/", DateTimeOffset.UtcNow.AddSeconds(30), CancellationToken.None);

        using var connection = await ConnectionMultiplexer.ConnectAsync(RedisServer.ConnectionString);

        Assert.That(await connection.GetDatabase().KeyExistsAsync($"{prefix}:blocked:a/"), Is.True);
    }
}

public class RedisFailureModeTests
{
    // Nothing listens on port 1; the short timeouts keep the tests fast.
    private const string Unreachable = "localhost:1,connectTimeout=200,asyncTimeout=300,syncTimeout=300";

    private static readonly ThrottleScope Scope = Redis.Scope(new ThrottlingRule { Algorithm = RateLimitAlgorithm.FixedWindow, PermitLimit = 1 });

    [Test]
    public async Task By_default_requests_pass_when_Redis_is_down()
    {
        using var provider = Redis.Provider("down", connectionString: Unreachable);
        using var failures = new MetricCollector<long>(provider.GetRequiredService<IMeterFactory>(), HttpResilienceTelemetry.MeterName, HttpResilienceMetrics.StoreFailures);

        Assert.That(await provider.Limiter(Scope).AcquireInTurnAsync(2), Is.EqualTo(new[] { true, true }));
        Assert.That(failures.GetMeasurementSnapshot().Select(m => m.Tags["store.operation"]), Is.All.EqualTo("limiter"));
    }

    [Test]
    public async Task RejectRequests_refuses_when_Redis_is_down()
    {
        using var provider = Redis.Provider("down", o => o.LimiterFailureMode = LimiterFailureMode.RejectRequests, Unreachable);

        using var lease = await provider.Limiter(Scope).AcquireAsync();

        Assert.That(lease.IsAcquired, Is.False);
        Assert.That(lease.RetryAfter(), Is.Null);
    }
}

public class RedisRegistrationTests
{
    private static OptionsValidationException? Validate(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        using var provider = services.BuildServiceProvider();
        try
        {
            _ = provider.GetRequiredService<IOptions<RedisThrottlingOptions>>().Value;
            return null;
        }
        catch (OptionsValidationException ex)
        {
            return ex;
        }
    }

    [Test]
    public void A_connection_is_required()
        => Assert.That(Validate(s => s.AddHttpResilienceRedis(_ => { }))?.Message, Does.Contain("register an IConnectionMultiplexer"));

    [Test]
    public void Connection_string_and_endpoint_exclude_each_other()
        => Assert.That(Validate(s => s.AddHttpResilienceRedis(o =>
        {
            o.ConnectionString = "localhost";
            o.Endpoint = "cache.example:6380";
        }))?.Message, Does.Contain("not both"));

    [Test]
    public void A_registered_multiplexer_is_enough()
        => Assert.That(Validate(s =>
        {
            s.AddSingleton<IConnectionMultiplexer>(_ => throw new NotSupportedException("not resolved by validation"));
            s.AddHttpResilienceRedis(_ => { });
        }), Is.Null);

    [Test]
    public void Prefix_and_lease_timeout_are_checked()
    {
        var ex = Validate(s => s.AddHttpResilienceRedis(o =>
        {
            o.ConnectionString = "localhost";
            o.KeyPrefix = " ";
            o.ConcurrencyLeaseTimeout = TimeSpan.Zero;
        }));

        Assert.That(ex!.Failures, Has.Count.EqualTo(2));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Redis_replaces_the_in_memory_parts_in_either_order(bool redisFirst)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (redisFirst)
        {
            services.AddHttpResilienceRedis(o => o.ConnectionString = "localhost");
        }

        services.AddHttpClient("partner").AddHttpResilience();
        if (!redisFirst)
        {
            services.AddHttpResilienceRedis(o => o.ConnectionString = "localhost");
        }

        using var provider = services.BuildServiceProvider();

        Assert.That(provider.GetRequiredService<IThrottleStateStore>(), Is.TypeOf<RedisThrottleStateStore>());
        Assert.That(provider.GetRequiredService<IRateLimiterFactory>(), Is.TypeOf<RedisRateLimiterFactory>());
        Assert.That(services.Count(d => d.ServiceType == typeof(IThrottleStateStore)), Is.EqualTo(1));
    }

    [Test]
    public void Options_bind_from_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HttpResilience:Redis:ConnectionString"] = "localhost:6379",
            ["HttpResilience:Redis:KeyPrefix"] = "orders-api",
            ["HttpResilience:Redis:LimiterFailureMode"] = "RejectRequests",
            ["HttpResilience:Redis:ConcurrencyLeaseTimeout"] = "00:02:00",
        }).Build();
        var services = new ServiceCollection();
        services.AddHttpResilienceRedis(configuration.GetSection(RedisThrottlingOptions.SectionName));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RedisThrottlingOptions>>().Value;

        Assert.That(options.KeyPrefix, Is.EqualTo("orders-api"));
        Assert.That(options.LimiterFailureMode, Is.EqualTo(LimiterFailureMode.RejectRequests));
        Assert.That(options.ConcurrencyLeaseTimeout, Is.EqualTo(TimeSpan.FromMinutes(2)));
    }
}

/// <summary>Two app instances, one Redis: the whole pipeline, not just the parts.</summary>
public class RedisEndToEndTests
{
    private static readonly Uri BaseAddress = new("https://api.partner.example");

    private static ServiceProvider Instance(string prefix, StubPartner partner)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("partner", c => c.BaseAddress = BaseAddress)
            .AddHttpResilience((o, _) => o.ThrottleRetry.MaxRetryAttempts = 0)
            .ConfigurePrimaryHttpMessageHandler(() => partner);
        services.AddOutboundThrottlingRules(o => o.Rules.Add(new ThrottlingRule
        {
            Name = "partner", BaseAddress = BaseAddress, Algorithm = RateLimitAlgorithm.FixedWindow, PermitLimit = 2, Window = TimeSpan.FromMinutes(1),
        }));
        services.AddHttpResilienceRedis(o =>
        {
            o.ConnectionString = RedisServer.ConnectionString;
            o.KeyPrefix = prefix;
        });
        return services.BuildServiceProvider();
    }

    private static HttpClient Client(IServiceProvider provider) => provider.GetRequiredService<IHttpClientFactory>().CreateClient("partner");

    [Test]
    public async Task The_rate_limit_holds_across_instances()
    {
        var prefix = RedisServer.UniquePrefix();
        var partner = new StubPartner(HttpStatusCode.OK);
        using var first = Instance(prefix, partner);
        using var second = Instance(prefix, partner);

        using var a = await Client(first).GetAsync("/orders/1");
        using var b = await Client(second).GetAsync("/orders/2");
        var ex = Assert.ThrowsAsync<OutboundThrottleRejectedException>(() => Client(first).GetAsync("/orders/3"));

        Assert.That(partner.Calls, Is.EqualTo(2));
        Assert.That(ex!.Reason, Is.EqualTo(ThrottleRejectionReason.RateLimit));
    }

    [Test]
    public async Task A_429_seen_by_one_instance_holds_back_the_other()
    {
        var prefix = RedisServer.UniquePrefix();
        var partner = new StubPartner(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(30));
        using var first = Instance(prefix, partner);
        using var second = Instance(prefix, partner);

        using var throttled = await Client(first).GetAsync("/orders/1");
        var ex = Assert.ThrowsAsync<OutboundThrottleRejectedException>(() => Client(second).GetAsync("/orders/2"));

        Assert.That(throttled.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(partner.Calls, Is.EqualTo(1));
        Assert.That(ex!.Reason, Is.EqualTo(ThrottleRejectionReason.ServerThrottle));
        Assert.That(ex.RetryAfter, Is.GreaterThan(TimeSpan.FromSeconds(25)).And.LessThanOrEqualTo(TimeSpan.FromSeconds(30)));
    }

    private sealed class StubPartner(HttpStatusCode status, TimeSpan? retryAfter = null) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var response = new HttpResponseMessage(status) { RequestMessage = request };
            if (retryAfter is { } wait)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
            }

            return Task.FromResult(response);
        }
    }
}
